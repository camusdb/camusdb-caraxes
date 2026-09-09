namespace P3c;

/// <summary>
/// Session-registration hop attribution, for the client-routing A/B (CamusDB <c>e89bc8c4</c>).
///
/// <para>Routing's claim is that it removes hops, so hops are what this reads — not throughput. The
/// counters come from kahuna <c>1bc70727</c>: <c>session_registration_forwards{op,route,result}</c>
/// counts every registration call by whether it stayed local or crossed to the session owner, and
/// <c>session_registration_forward_ms</c> (1.6.6) times the forwarded ones. Before routing existed the
/// campaign measured 9.62 forwarded hops per committed write transaction on <c>accounts</c> and 19.62 on
/// <c>fanout</c>, at a 40% remote share.</para>
///
/// <para><b>Two denominators, both printed.</b> "Hops per committed write transaction" depends on what
/// counts as one, and the earlier figure's denominator is not recorded precisely enough to reproduce. So
/// this prints hops against the client's completed write operations AND against the server's committed
/// transactions, and never silently picks one. A conclusion that survives both is a conclusion; one that
/// needs a particular denominator is an artifact of choosing it.</para>
/// </summary>
public static class Hops
{
    private const string Forwards = "kahuna_durable_tx_session_registration_forwards_total";
    private const string ForwardMs = "kahuna_durable_tx_session_registration_forward_ms_milliseconds";

    public static void Run(IReadOnlyList<string> runDirs)
    {
        Console.WriteLine(
            $"{"run",-28}{"forwarded",12}{"local",10}{"fwd share",11}{"hops/write",12}"
            + $"{"hops/commit",13}{"fwd p50 ms",12}{"fwd mean",10}{"explained",11}");

        foreach (string dir in runDirs)
            Report(dir);
    }

    private static void Report(string runDir)
    {
        RunArtifacts? run = RunArtifacts.TryLoad(runDir);
        if (run is null)
        {
            Console.WriteLine($"{Path.GetFileName(runDir.TrimEnd(Path.DirectorySeparatorChar)),-28}  (not a run directory)");
            return;
        }

        double forwarded = 0, local = 0;
        foreach ((SeriesKey key, double value) in run.Metrics.Totals)
        {
            if (key.Name != Forwards)
                continue;

            // Only successful calls are hops. `threw` is a failure and `unrouted` sent no call at all —
            // counting either would inflate the number the decision rule divides by, which is the exact
            // mistake kahuna 1bc70727 split those results out to prevent.
            if (!key.Labels.Contains("result=ok", StringComparison.Ordinal))
                continue;

            if (key.Labels.Contains("route=forwarded", StringComparison.Ordinal))
                forwarded += value;
            else if (key.Labels.Contains("route=local", StringComparison.Ordinal))
                local += value;
        }

        double registrations = forwarded + local;
        double writes = Number(run, "CompletedWrite");
        double commits = run.Metrics.Value("camus_transaction_count_total", "operation=commit,outcome=ok")
                         ?? CommitsByScan(run);

        // Aggregated across the `op` tag, not looked up with an empty label set: the histogram is tagged
        // by op (begin/complete), so there is no untagged series to read and a plain lookup silently
        // returns nothing — which reads as "hops are free" rather than as "not measured".
        (double fwdP50, double fwdMean) = ForwardCost(run);
        double writeP50 = Metrics.Rows.FirstOrDefault(r => r.Label == "write p50 ms").Read?.Invoke(run) ?? 0;

        double hopsPerWrite = writes > 0 ? forwarded / writes : 0;
        double hopsPerCommit = commits > 0 ? forwarded / commits : 0;

        // The share of a write's latency the forwarded hops account for, using the measured per-call cost
        // rather than a network round trip. 1bc70727 could only estimate this at 1.4-2.0% from a 0.055 ms
        // bare RTT; the histogram makes it arithmetic.
        double explained = writeP50 > 0 ? 100 * hopsPerWrite * fwdP50 / writeP50 : 0;

        Console.WriteLine(
            $"{run.Name,-28}{forwarded,12:N0}{local,10:N0}"
            + $"{(registrations > 0 ? 100 * forwarded / registrations : 0),10:F1}%"
            + $"{hopsPerWrite,12:F2}{hopsPerCommit,13:F2}{fwdP50,12:F3}{fwdMean,10:F3}{explained,10:F1}%");
    }

    /// <summary>
    /// The measured cost of one forwarded registration, summed over every <c>op</c> the histogram is
    /// tagged by: the mean as <c>sum/count</c>, and the median from the cumulative buckets added together
    /// across ops and interpolated where the count crosses the halfway mark.
    /// </summary>
    private static (double P50, double Mean) ForwardCost(RunArtifacts run)
    {
        double sum = 0, count = 0;
        SortedDictionary<double, double> buckets = [];

        foreach ((SeriesKey key, double value) in run.Metrics.Totals)
        {
            if (key.Name == ForwardMs + "_sum") sum += value;
            else if (key.Name == ForwardMs + "_count") count += value;
            else if (key.Name == ForwardMs + "_bucket")
            {
                foreach (string pair in key.Labels.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!pair.StartsWith("le=", StringComparison.Ordinal))
                        continue;

                    string le = pair[3..].Trim('"');
                    double edge = le is "+Inf" or "inf"
                        ? double.PositiveInfinity
                        : double.Parse(le, System.Globalization.CultureInfo.InvariantCulture);
                    buckets[edge] = buckets.GetValueOrDefault(edge) + value;
                }
            }
        }

        if (count <= 0)
            return (0, 0);

        double mean = sum / count;
        double target = count / 2;
        double previousEdge = 0, previousCount = 0;

        foreach ((double edge, double cumulative) in buckets)
        {
            if (cumulative < target)
            {
                previousEdge = edge;
                previousCount = cumulative;
                continue;
            }

            if (double.IsPositiveInfinity(edge) || cumulative <= previousCount)
                return (previousEdge, mean);

            // Linear interpolation inside the bucket the median lands in, as histogram_quantile does.
            return (previousEdge + (edge - previousEdge) * (target - previousCount) / (cumulative - previousCount), mean);
        }

        return (previousEdge, mean);
    }

    /// <summary>Client-side completed operations of a kind, from the workload's own summary.</summary>
    private static double Number(RunArtifacts run, string property)
        => run.Summary.TryGetProperty(property, out System.Text.Json.JsonElement value)
           && value.TryGetDouble(out double d)
            ? d
            : 0;

    /// <summary>
    /// Server-side committed transactions, when the exact label string does not match. Scans rather than
    /// looking up so a label-order change in the exposition does not silently return zero and make the
    /// per-commit column read as "no hops".
    /// </summary>
    private static double CommitsByScan(RunArtifacts run)
    {
        double total = 0;
        foreach ((SeriesKey key, double value) in run.Metrics.Totals)
            if (key.Name == "camus_transaction_count_total"
                && key.Labels.Contains("operation=commit", StringComparison.Ordinal)
                && key.Labels.Contains("outcome=ok", StringComparison.Ordinal))
                total += value;

        return total;
    }
}
