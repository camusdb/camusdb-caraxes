using System.Globalization;
using System.Text.RegularExpressions;

namespace P3c;

/// <summary>
/// Per-window regime analysis of one soak: did the thing the throughput depends on hold still?
///
/// <para>Why a per-window view. The four <c>bank-rebase</c> soaks (feature 80af367a) produced matched
/// ratios of 1.78x and 7.95x for the same arm pair. The end-of-run <c>raft write mean</c> passed them
/// as one durability regime (1.30x apart) because a whole-run mean averages over the break: in every one
/// of the four the write leader's per-batch Raft latency stepped up 3-4x inside a single 5-second
/// interval — at minute 4, 5, 17 and 35 respectively — and stayed there. The per-partition write
/// pipeline runs one Raft batch at a time, so that step is the throughput. A run whose regime broke
/// mid-window is not one measurement; it is two, and no ratio built on it estimates anything.</para>
///
/// <para>What is read. The 5-second <c>node-metrics.csv</c> for the write leader's
/// <c>kahuna_kv_write_raft_duration</c> mean and its <c>camus_request_duration{operation=query}</c> mean
/// (the leader read stall of the slow candidate run, 1 ms to 40-50 ms); the node logs for the follower
/// repair events that accompanied every step (<c>batch landed over a gap</c>, <c>min-log-index
/// mismatch</c>, backfill pacing); and <c>host-io.csv</c> when the harness recorded it, for the
/// device's utilisation and the cost of one fsync on it.</para>
/// </summary>
public static class Regime
{
    private const int WindowSeconds = 300;

    /// <summary>The ratio between the slowest and fastest window's Raft mean beyond which the run is two regimes.</summary>
    public const double MaxRaftWindowSpread = 1.5;

    /// <summary>Leader read latency growth (last window over first) beyond which the run carried a read stall.</summary>
    public const double MaxReadWindowGrowth = 3.0;

    public sealed record Window(
        int Index,
        double RaftMeanMs,
        double RaftBatchesPerSecond,
        double ReadMeanMs,
        int RepairEvents,
        double? HostFsyncP50Ms,
        double? HostUtilPercent,
        double? HostReadsPerSecond);

    public sealed record Report(string Run, string? Leader, IReadOnlyList<Window> Windows)
    {
        public double RaftSpread
        {
            get
            {
                double[] raft = [.. Windows.Select(w => w.RaftMeanMs).Where(v => v > 0)];
                return raft.Length < 2 ? 1 : raft.Max() / raft.Min();
            }
        }

        public double ReadGrowth
        {
            get
            {
                double[] reads = [.. Windows.Select(w => w.ReadMeanMs).Where(v => v > 0)];
                return reads.Length < 2 ? 1 : reads[^1] / reads[0];
            }
        }

        public int RepairEvents => Windows.Sum(w => w.RepairEvents);

        public bool RaftStable => RaftSpread <= MaxRaftWindowSpread;

        public bool ReadsStable => ReadGrowth <= MaxReadWindowGrowth;

        /// <summary>One measurement, not two: both the durable-write cost and the leader read cost held.</summary>
        public bool Stable => RaftStable && ReadsStable;

        /// <summary>The first window whose Raft mean exceeds the fastest window by the spread bar, if any.</summary>
        public int? RaftBreakWindow
        {
            get
            {
                double min = Windows.Where(w => w.RaftMeanMs > 0).Select(w => w.RaftMeanMs).DefaultIfEmpty(0).Min();
                if (min <= 0)
                    return null;
                foreach (Window w in Windows)
                    if (w.RaftMeanMs > min * MaxRaftWindowSpread)
                        return w.Index;
                return null;
            }
        }
    }

    public static Report? Analyze(string runDir)
    {
        string artifacts = Path.Combine(runDir, "artifacts", "run");
        string metricsPath = Path.Combine(artifacts, "node-metrics.csv");
        string metaPath = Path.Combine(artifacts, "run-meta.json");
        if (!File.Exists(metricsPath) || !File.Exists(metaPath))
            return null;

        using System.Text.Json.JsonDocument meta = System.Text.Json.JsonDocument.Parse(File.ReadAllText(metaPath));
        DateTime start = meta.RootElement.GetProperty("measureStartUtc").GetDateTime().ToUniversalTime();
        int measureSeconds = meta.RootElement.TryGetProperty("measureSeconds", out System.Text.Json.JsonElement ms) ? ms.GetInt32() : 2700;
        int windows = Math.Max(1, measureSeconds / WindowSeconds);
        long startMs = new DateTimeOffset(start).ToUnixTimeMilliseconds();

        // node -> metric -> cumulative samples (ts, value), labels summed away except where a label selects.
        Dictionary<(string Node, string Metric), List<(long Ts, double Value)>> series = LoadSeries(metricsPath);

        string? leader = series
            .Where(kv => kv.Key.Metric == "kahuna_kv_write_raft_duration_milliseconds_count")
            .OrderByDescending(kv => kv.Value.Count == 0 ? 0 : kv.Value[^1].Value - kv.Value[0].Value)
            .Select(kv => kv.Key.Node)
            .FirstOrDefault();

        List<Window> result = [];
        Dictionary<int, int> repairs = CountRepairEvents(artifacts, start, windows);
        List<(DateTime Utc, double Fsync, double Util, double Reads)>? hostIo = LoadHostIo(runDir);

        for (int i = 0; i < windows; i++)
        {
            long lo = startMs + (long)i * WindowSeconds * 1000;
            long hi = lo + WindowSeconds * 1000;

            (double raftMean, double raftRate) = leader is null
                ? (0, 0)
                : MeanAndRate(series, leader, "kahuna_kv_write_raft_duration_milliseconds", lo, hi);
            (double readMean, _) = leader is null
                ? (0, 0)
                : MeanAndRate(series, leader, "camus_request_duration_milliseconds|operation=query", lo, hi);

            double? fsync = null, util = null, reads = null;
            if (hostIo is not null)
            {
                DateTime wlo = DateTimeOffset.FromUnixTimeMilliseconds(lo).UtcDateTime;
                DateTime whi = DateTimeOffset.FromUnixTimeMilliseconds(hi).UtcDateTime;
                List<(DateTime Utc, double Fsync, double Util, double Reads)> inWindow = hostIo.Where(s => s.Utc >= wlo && s.Utc < whi).ToList();
                if (inWindow.Count > 0)
                {
                    double[] f = [.. inWindow.Select(s => s.Fsync).Where(v => v >= 0).Order()];
                    fsync = f.Length > 0 ? f[f.Length / 2] : null;
                    util = inWindow.Average(s => s.Util);
                    reads = inWindow.Average(s => s.Reads);
                }
            }

            result.Add(new Window(i, raftMean, raftRate, readMean, repairs.GetValueOrDefault(i), fsync, util, reads));
        }

        return new Report(Path.GetFileName(runDir.TrimEnd(Path.DirectorySeparatorChar)), leader, result);
    }

    public static void Print(Report report)
    {
        Console.WriteLine($"regime — {report.Run} (write leader {report.Leader ?? "unknown"})");
        Console.WriteLine($"  {"win",4}{"raft ms",9}{"batch/s",9}{"read ms",9}{"repairs",9}{"fsync ms",10}{"util%",7}{"dev r/s",9}");
        foreach (Window w in report.Windows)
            Console.WriteLine(
                $"  {w.Index,4}{w.RaftMeanMs,9:F2}{w.RaftBatchesPerSecond,9:F0}{w.ReadMeanMs,9:F2}{w.RepairEvents,9}"
                + $"{(w.HostFsyncP50Ms is double f ? f.ToString("F2", CultureInfo.InvariantCulture) : "-"),10}"
                + $"{(w.HostUtilPercent is double u ? u.ToString("F0", CultureInfo.InvariantCulture) : "-"),7}"
                + $"{(w.HostReadsPerSecond is double r ? r.ToString("F0", CultureInfo.InvariantCulture) : "-"),9}");

        string raftLine = report.RaftStable
            ? $"raft regime held: window means within {report.RaftSpread:F2}x"
            : $"*** RAFT REGIME BREAK at window {report.RaftBreakWindow}: window means span {report.RaftSpread:F2}x (bar {MaxRaftWindowSpread:F1}x) ***";
        string readLine = report.ReadsStable
            ? $"leader reads held: last/first {report.ReadGrowth:F2}x"
            : $"*** LEADER READ STALL: last/first window read mean {report.ReadGrowth:F2}x (bar {MaxReadWindowGrowth:F1}x) ***";
        Console.WriteLine($"  {raftLine}");
        Console.WriteLine($"  {readLine}");
        Console.WriteLine($"  follower repair events (gap / min-log-index mismatch / backfill pacing): {report.RepairEvents}");
        Console.WriteLine(report.Stable
            ? "  -> one regime for the whole window; admissible for a ratio"
            : "  -> two regimes in one run; NOT admissible for a ratio (the window average is a mixture)");
    }

    // ── Readers ────────────────────────────────────────────────────────────────

    private static Dictionary<(string, string), List<(long, double)>> LoadSeries(string path)
    {
        Dictionary<(string, string), Dictionary<long, double>> acc = [];
        bool first = true;
        foreach (string line in File.ReadLines(path))
        {
            if (first) { first = false; continue; }
            string[] parts = line.Split(',');
            if (parts.Length < 5) continue;

            string metric = parts[2];
            bool wanted = metric.StartsWith("kahuna_kv_write_raft_duration_milliseconds_", StringComparison.Ordinal)
                || metric.StartsWith("camus_request_duration_milliseconds_", StringComparison.Ordinal);
            if (!wanted || metric.EndsWith("_bucket", StringComparison.Ordinal))
                continue;

            string key = metric;
            if (metric.StartsWith("camus_request_duration", StringComparison.Ordinal))
            {
                if (!parts[3].Contains("operation=query", StringComparison.Ordinal))
                    continue;
                key = metric.Replace("_sum", "|operation=query_sum").Replace("_count", "|operation=query_count");
            }

            if (!long.TryParse(parts[0], out long ts) ||
                !double.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                continue;

            (string, string) k = (parts[1], key);
            if (!acc.TryGetValue(k, out Dictionary<long, double>? byTs))
                acc[k] = byTs = [];
            byTs[ts] = byTs.GetValueOrDefault(ts) + value;
        }

        Dictionary<(string, string), List<(long, double)>> series = [];
        foreach (((string, string) k, Dictionary<long, double> byTs) in acc)
            series[k] = byTs.OrderBy(p => p.Key).Select(p => (p.Key, p.Value)).ToList();
        return series;
    }

    /// <summary>Mean of a (sum, count) histogram pair over [lo, hi), plus the count's rate per second.</summary>
    private static (double Mean, double Rate) MeanAndRate(
        Dictionary<(string, string), List<(long Ts, double Value)>> series, string node, string metric, long lo, long hi)
    {
        // Two key shapes: a bare histogram name, or one with a selecting label baked in by LoadSeries
        // ("camus_request_duration_milliseconds|operation=query").
        string sumKey = metric + "_sum";
        string countKey = metric + "_count";

        if (!series.TryGetValue((node, sumKey), out List<(long Ts, double Value)>? sums)
            || !series.TryGetValue((node, countKey), out List<(long Ts, double Value)>? counts))
            return (0, 0);

        (long Ts, double Value)[] s = [.. sums.Where(p => p.Ts >= lo && p.Ts < hi)];
        (long Ts, double Value)[] c = [.. counts.Where(p => p.Ts >= lo && p.Ts < hi)];
        if (s.Length < 2 || c.Length < 2)
            return (0, 0);

        double dSum = s[^1].Value - s[0].Value;
        double dCount = c[^1].Value - c[0].Value;
        double seconds = Math.Max(1, (c[^1].Ts - c[0].Ts) / 1000.0);
        return (dCount > 0 ? dSum / dCount : 0, dCount / seconds);
    }

    private static readonly Regex LogStamp = new(@"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})", RegexOptions.Compiled);

    /// <summary>
    /// Follower repair events per window from the node logs. Node log stamps are UTC to the second.
    /// </summary>
    private static Dictionary<int, int> CountRepairEvents(string artifacts, DateTime start, int windows)
    {
        Dictionary<int, int> counts = [];
        foreach (string path in Directory.EnumerateFiles(artifacts, "node-log-camus*.txt"))
        {
            foreach (string line in File.ReadLines(path))
            {
                if (!line.Contains("landed over a gap", StringComparison.Ordinal)
                    && !line.Contains("min-log-index mismatch", StringComparison.Ordinal)
                    && !line.Contains("consecutive batches without", StringComparison.Ordinal))
                    continue;

                Match m = LogStamp.Match(line);
                if (!m.Success || !DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime at))
                    continue;

                int window = (int)Math.Floor((at - start).TotalSeconds / WindowSeconds);
                if (window < 0 || window >= windows)
                    continue;
                counts[window] = counts.GetValueOrDefault(window) + 1;
            }
        }
        return counts;
    }

    private static List<(DateTime, double, double, double)>? LoadHostIo(string runDir)
    {
        string path = Path.Combine(runDir, "host-io.csv");
        if (!File.Exists(path))
            return null;

        List<(DateTime, double, double, double)> rows = [];
        string[] lines = File.ReadAllLines(path);
        if (lines.Length < 2)
            return null;

        string[] header = lines[0].Split(',');
        int iTs = Array.IndexOf(header, "ts");
        int iFsync = Array.IndexOf(header, "fsync_ms");
        int iUtil = Array.IndexOf(header, "util_pct");
        int iReads = Array.IndexOf(header, "r_per_s");

        foreach (string line in lines[1..])
        {
            string[] c = line.Split(',');
            if (c.Length <= Math.Max(Math.Max(iTs, iFsync), Math.Max(iUtil, iReads)))
                continue;
            if (!DateTime.TryParse(c[iTs], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime ts))
                continue;
            rows.Add((ts, Num(c, iFsync), Num(c, iUtil), Num(c, iReads)));
        }
        return rows;
    }

    private static double Num(string[] cells, int index)
        => index >= 0 && index < cells.Length
           && double.TryParse(cells[index], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : 0;
}
