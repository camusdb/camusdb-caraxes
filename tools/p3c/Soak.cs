using System.Globalization;

namespace P3c;

/// <summary>
/// Windowed analysis of a 45-minute soak, and the matched ABBA ratio for the sustained `bank`
/// re-baseline.
///
/// <para>Why windows rather than the summary average: the previous soak campaign's entire finding was
/// that the candidates decayed while the baseline held flat. A run that serves 3,347 ops/s for ten
/// minutes and 1,776 for the last five produces a summary average that hides that completely, so this
/// reads <c>intervals.csv</c> and reports the window average, the first five minutes and the last five
/// minutes side by side.</para>
///
/// <para>Why ABBA: across six 45-minute p1/w128 arms on this host, wall-clock start time predicted
/// throughput at Pearson -0.963 — a stronger predictor than any code variable in that experiment. A
/// single base-then-candidate pair measures the hour as much as the stack. Running base, cand, cand,
/// base cancels a monotonic trend exactly. If the two orderings disagree in direction the drift is not
/// monotonic, and <see cref="Rebase"/> says so rather than averaging them into a number.</para>
/// </summary>
public static class Soak
{
    private const int WindowSeconds = 300;

    /// <summary>
    /// How far the two orderings may differ before their mean stops being an estimate of anything. ABBA
    /// removes a monotonic trend; it cannot remove a term larger than the effect. Set at 25% because the
    /// campaign already treats a 1.5x durability spread as disqualifying, and a ratio whose two halves
    /// differ by more than a quarter is in the same territory.
    /// </summary>
    private const double MaxOrderingSpread = 0.25;

    /// <summary>One soak's throughput windows, failure counts and memory ceiling.</summary>
    public sealed record Window(
        string Name,
        int Seconds,
        double Average,
        double First5,
        double Last5,
        double DecayPercent,
        double Failed,
        double Indeterminate,
        double WriteP99Max,
        double PeakMib,
        double LimitMib,
        double RaftWriteMeanMs)
    {
        public double PeakPercent => LimitMib > 0 ? 100.0 * PeakMib / LimitMib : 0;
    }

    public static void Run(IReadOnlyList<string> runDirs)
    {
        List<Window> windows = [.. runDirs.Select(Read)];
        PrintTable(windows);

        Console.WriteLine();
        foreach (string dir in runDirs)
            if (Regime.Analyze(dir) is Regime.Report report)
            {
                Regime.Print(report);
                Console.WriteLine();
            }
    }

    /// <summary>
    /// The matched re-baseline: two orderings of the same pair, reported separately and only then
    /// averaged. Argument order is base(pair1), cand(pair1), cand(pair2), base(pair2).
    /// </summary>
    public static void Rebase(IReadOnlyList<string> runDirs)
    {
        if (runDirs.Count != 4)
        {
            Console.Error.WriteLine("rebase needs four run directories: base-p1 cand-p1 cand-p2 base-p2");
            return;
        }

        Window baseP1 = Read(runDirs[0]);
        Window candP1 = Read(runDirs[1]);
        Window candP2 = Read(runDirs[2]);
        Window baseP2 = Read(runDirs[3]);

        PrintTable([baseP1, candP1, candP2, baseP2]);

        double avgP1 = candP1.Average / baseP1.Average;
        double avgP2 = candP2.Average / baseP2.Average;
        double endP1 = candP1.Last5 / baseP1.Last5;
        double endP2 = candP2.Last5 / baseP2.Last5;

        Console.WriteLine();
        Console.WriteLine("matched ratios, per ordering (never averaged before both are shown)");
        Console.WriteLine($"  pair 1  base -> cand   window {avgP1:F2}x   end-of-window {endP1:F2}x");
        Console.WriteLine($"  pair 2  cand -> base   window {avgP2:F2}x   end-of-window {endP2:F2}x");
        Console.WriteLine();

        // A monotonic host trend moves the two orderings in opposite directions by roughly equal
        // amounts, so their mean is the drift-free estimate. Two things break that. A disagreement in
        // direction means the drift is not monotonic. And two orderings that agree in direction but
        // differ hugely in magnitude mean the trend was not the dominant term either — the mean of
        // 1.78x and 7.95x is arithmetic, not an estimate. Both are refusals to report.
        bool sameDirection = (avgP1 - 1) * (avgP2 - 1) > 0;
        double spread = Math.Abs(avgP1 - avgP2) / Math.Max(avgP1, avgP2);
        bool tightEnough = spread <= MaxOrderingSpread;

        Console.WriteLine($"  ABBA window ratio        {(avgP1 + avgP2) / 2:F2}x");
        Console.WriteLine($"  ABBA end-of-window ratio {(endP1 + endP2) / 2:F2}x");
        Console.WriteLine(
            $"  ordering spread          {100 * spread:F1}%"
            + (sameDirection ? "" : "   *** ORDERINGS DISAGREE IN DIRECTION ***")
            + (sameDirection && !tightEnough ? $"   *** EXCEEDS THE {100 * MaxOrderingSpread:F0}% BAR ***" : ""));

        if (!sameDirection)
            Console.WriteLine("  -> NOT REPORTABLE as a ratio: the drift this design controls for is not monotonic.");
        else if (!tightEnough)
            Console.WriteLine(
                "  -> NOT REPORTABLE as a ratio: the orderings agree in direction but not in magnitude, so"
                + " ordering is not the dominant term and averaging them estimates nothing.");

        Console.WriteLine();
        CheckRegimes([baseP1, candP1, candP2, baseP2]);
        CheckDisqualifiers([baseP1, candP1, candP2, baseP2]);

        // The whole-run raft mean above cannot see a regime that broke mid-run — it averages over the
        // break. Every run of the pair must have held ONE regime for its window, or the ratio is built
        // on a mixture (the 1.78x / 7.95x campaign: all four runs stepped 3-4x mid-run and every
        // whole-run check passed them).
        Console.WriteLine();
        bool allStable = true;
        foreach (string dir in runDirs)
        {
            Regime.Report? report = Regime.Analyze(dir);
            if (report is null)
            {
                Console.WriteLine($"regime — {Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar))}: no node-metrics.csv, cannot check");
                allStable = false;
                continue;
            }
            Regime.Print(report);
            Console.WriteLine();
            allStable &= report.Stable;
        }
        Console.WriteLine(allStable
            ? "per-window regimes: every run held one regime; the ratio above is built on four single measurements"
            : "per-window regimes: at least one run broke regime mid-window — NOT REPORTABLE as a ratio whatever the spread says");
    }

    /// <summary>
    /// The campaign's standing admissibility rule: this host has moved the cost of one durable Raft
    /// write by 4.5x between runs, which is larger than any effect being measured, so arms whose
    /// durability cost straddles a 1.5x regime are not comparable however carefully they were paired.
    /// </summary>
    private static void CheckRegimes(IReadOnlyList<Window> windows)
    {
        double[] raft = [.. windows.Select(w => w.RaftWriteMeanMs).Where(v => v > 0)];
        if (raft.Length < 2)
        {
            Console.WriteLine("durability regime: raft write mean unavailable on at least one arm — cannot check");
            return;
        }

        double ratio = raft.Max() / raft.Min();
        Console.WriteLine(
            $"durability regime: raft write mean {raft.Min():F2}-{raft.Max():F2} ms, spread {ratio:F2}x"
            + (ratio > 1.5 ? "   *** MIXED REGIMES — NOT COMPARABLE ***" : "   (within the 1.5x rule)"));
    }

    /// <summary>
    /// Disqualifiers bind whatever the ratio says: the previous campaign retired p1-w256 on failures
    /// and a 12-second tail, not on its throughput.
    /// </summary>
    private static void CheckDisqualifiers(IReadOnlyList<Window> windows)
    {
        foreach (Window w in windows)
        {
            List<string> problems = [];
            if (w.Failed > 0) problems.Add($"{w.Failed:N0} failed");
            if (w.Indeterminate > 0) problems.Add($"{w.Indeterminate:N0} indeterminate");
            if (w.PeakPercent >= 95) problems.Add($"peak RSS {w.PeakPercent:F0}% of cap");

            if (problems.Count > 0)
                Console.WriteLine($"disqualifier check: {w.Name} — {string.Join(", ", problems)}");
        }
    }

    private static void PrintTable(IReadOnlyList<Window> windows)
    {
        Console.WriteLine($"{"run",-34}{"avg",8}{"first5",9}{"last5",8}{"decay",9}{"failed",8}{"indet",7}{"wp99max",9}{"peakMiB",9}{"cap%",7}{"raft",7}");
        foreach (Window w in windows)
            Console.WriteLine(
                $"{w.Name,-34}{w.Average,8:F1}{w.First5,9:F1}{w.Last5,8:F1}{w.DecayPercent,8:F1}%{w.Failed,8:N0}{w.Indeterminate,7:N0}"
                + $"{w.WriteP99Max,9:F0}{w.PeakMib,9:F0}{w.PeakPercent,6:F0}%{w.RaftWriteMeanMs,7:F2}");
    }

    private static Window Read(string runDir)
    {
        string name = Path.GetFileName(runDir.TrimEnd(Path.DirectorySeparatorChar));
        string intervals = Path.Combine(runDir, "artifacts", "run", "intervals.csv");
        if (!File.Exists(intervals))
            throw new FileNotFoundException($"no intervals.csv under {runDir}");

        List<double> completed = [];
        List<double> writeP99 = [];
        double failed = 0;

        string[] lines = File.ReadAllLines(intervals);
        string[] header = lines[0].Split(',');
        int iCompleted = Array.IndexOf(header, "completed");
        int iFailed = Array.IndexOf(header, "failed");
        int iWriteP99 = Array.IndexOf(header, "write_p99_ms");

        foreach (string line in lines[1..])
        {
            if (line.Length == 0) continue;
            string[] cells = line.Split(',');
            completed.Add(Num(cells, iCompleted));
            failed += Num(cells, iFailed);
            double p99 = Num(cells, iWriteP99);
            if (p99 > 0) writeP99.Add(p99);
        }

        int n = completed.Count;
        int w = Math.Min(WindowSeconds, n);
        double first5 = completed.Take(w).Average();
        double last5 = completed.TakeLast(w).Average();

        (double peak, double limit) = ReadMemory(runDir);
        RunArtifacts? run = RunArtifacts.TryLoad(runDir);

        return new Window(
            name,
            n,
            completed.Count > 0 ? completed.Average() : 0,
            first5,
            last5,
            first5 > 0 ? 100 * (last5 / first5 - 1) : 0,
            failed,
            run is null ? 0 : Indeterminate(run),
            writeP99.Count > 0 ? writeP99.Max() : 0,
            peak,
            limit,
            run is null ? 0 : RaftWriteMean(run));
    }

    /// <summary>The durability cost this host drifts on, read through the same row `extract` reports.</summary>
    private static double RaftWriteMean(RunArtifacts run)
        => Metrics.Rows.FirstOrDefault(r => r.Label == "raft write mean ms").Read?.Invoke(run) ?? 0;

    private static double Indeterminate(RunArtifacts run)
        => run.Summary.TryGetProperty("Indeterminate", out System.Text.Json.JsonElement value) ? value.GetDouble() : 0;

    /// <summary>Peak resident set across every node, with the cap it was measured against.</summary>
    private static (double Peak, double Limit) ReadMemory(string runDir)
    {
        string path = Path.Combine(runDir, "memory-samples.csv");
        if (!File.Exists(path))
            return (0, 0);

        double peak = 0, limit = 0;
        string[] lines = File.ReadAllLines(path);
        string[] header = lines[0].Split(',');
        int iMem = Array.IndexOf(header, "mem_mib");
        int iLimit = Array.IndexOf(header, "limit_mib");

        foreach (string line in lines[1..])
        {
            if (line.Length == 0) continue;
            string[] cells = line.Split(',');
            peak = Math.Max(peak, Num(cells, iMem));
            limit = Math.Max(limit, Num(cells, iLimit));
        }

        return (peak, limit);
    }

    private static double Num(string[] cells, int index)
        => index >= 0 && index < cells.Length
           && double.TryParse(cells[index], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : 0;
}
