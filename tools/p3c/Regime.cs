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

    /// <summary>Best 60-second minute of completed ops over the worst one. The Raft-mean rule cannot see a
    /// collapse that starves batches of items while the round stays constant (the 2026-09-09 preconditioned
    /// runs: 2,500 → 600 ops/s for a minute at an unchanged 16 ms round, all three nodes idle, 1,282 failed
    /// writes), so throughput dispersion is judged directly, at minute resolution because those collapses
    /// last one to two minutes and a five-minute window averages them away. Healthy runs sit within 1.1x.</summary>
    public const double MaxOpsMinuteSpread = 1.5;

    /// <summary>Raft-log live SST bytes (Kommander gauge <c>raft_wal_shard_live_sst_bytes</c>, worst node), last
    /// minute over first minute. Kommander 1.6.0 reclaims the log by dropping whole files below a persisted floor
    /// that advances at most <c>max_entries_per_compaction</c> per pass; when passes cannot keep up with ingest the
    /// gauge climbs in a straight line (k175 arm, 2026-09-10: 73 → 1,193 MB in 12 minutes, 16x) and the node
    /// retains gigabytes of dead log per hour. A bounded log sits within a few flush units of its first value.
    /// Only judged once the last-minute value exceeds <see cref="WalLiveFloorMb"/>: a log that grows from 2 to
    /// 10 MB is not a finding. A finding, not an admissibility rule — retention does not make the throughput
    /// window a mixture — so it is reported beside <see cref="Report.Stable"/>, not folded into it.</summary>
    public const double MaxWalLiveGrowth = 3.0;

    public const double WalLiveFloorMb = 64;

    /// <summary>A follower whose Raft WAL writer has died while the process, its health endpoint and its gRPC
    /// service stay up (Kahuna feature caf52e10: an OutOfMemoryException swallowed inside the WAL write left
    /// camus2 with <c>raft_wal_queue_depth</c> pinned at 4,096 and zero batches for four minutes, reported
    /// reachable throughout). Signature: a node's <c>raft_wal_batches_total</c> advances by less than this
    /// fraction of the busiest node's per-minute rate for two consecutive minutes while that node keeps writing.
    /// Disqualifying: the cluster ran on a quorum of two, not the configuration under test.</summary>
    public const double ZombieBatchFraction = 0.01;

    /// <summary>The harness's device preconditioning record (<c>precondition.json</c>), if the run wrote one.</summary>
    public sealed record Precondition(DateTime StartUtc, DateTime EndUtc, long Bytes, double MegabytesPerSecond, string Device, bool OverlapsWindow);

    /// <summary>Worst node's <c>raft_wal_shard_live_sst_bytes</c>: mean of the first measured minute and of the last.</summary>
    public sealed record WalLive(string Node, double FirstMb, double LastMb)
    {
        public double Growth => FirstMb > 0 ? LastMb / FirstMb : (LastMb > 0 ? double.PositiveInfinity : 1);
    }

    /// <summary>A node whose WAL writer stopped while another node's continued (see <see cref="ZombieBatchFraction"/>).</summary>
    public sealed record Zombie(string Node, int FromMinute, double LastQueueDepth, double LeaderBatchesPerMinute);

    public sealed record Window(
        int Index,
        double RaftMeanMs,
        double RaftBatchesPerSecond,
        double ReadMeanMs,
        int RepairEvents,
        double? HostFsyncP50Ms,
        double? HostUtilPercent,
        double? HostReadsPerSecond);

    public sealed record Report(string Run, string? Leader, IReadOnlyList<Window> Windows, Precondition? Precondition = null,
        IReadOnlyList<double>? OpsPerMinute = null, long FailedOps = 0, WalLive? WalLive = null, Zombie? Zombie = null)
    {
        /// <summary>The longest suffix of windows whose Raft means lie within <see cref="MaxRaftWindowSpread"/> of each other:
        /// the part of a run measured in one device regime after the host NVMe's step (feature 80af367a). On a drive whose
        /// cache folds inside the window, the whole-run average is a mixture, but the tail is a clean measurement and two
        /// arms' tails are comparable when both are long enough. First window index of the tail, or null when the run has
        /// fewer than two windows.</summary>
        public int? SteadyTailStart
        {
            get
            {
                double[] raft = [.. Windows.Select(w => w.RaftMeanMs)];
                if (raft.Length < 2) return null;
                int start = raft.Length - 1;
                for (int i = raft.Length - 2; i >= 0; i--)
                {
                    double[] span = raft[i..];
                    double lo = span.Where(v => v > 0).DefaultIfEmpty(0).Min(), hi = span.Max();
                    if (lo <= 0 || hi / lo > MaxRaftWindowSpread) break;
                    start = i;
                }
                return start;
            }
        }

        /// <summary>Mean completed ops/s over the steady tail's minutes (null without a client series or a tail).</summary>
        public double? SteadyTailOps
        {
            get
            {
                if (SteadyTailStart is not int start || OpsPerMinute is null) return null;
                int fromMinute = start * (WindowSeconds / 60);
                double[] m = [.. OpsPerMinute.Skip(fromMinute).Where(v => v > 0)];
                return m.Length == 0 ? null : m.Average();
            }
        }

        /// <summary>The Raft-log live bytes held within <see cref="MaxWalLiveGrowth"/> (or the gauge is absent).</summary>
        public bool WalLiveBounded => WalLive is null || WalLive.LastMb <= WalLiveFloorMb || WalLive.Growth <= MaxWalLiveGrowth;

        /// <summary>Best minute over worst minute of completed ops/s (1 when the client series is absent).</summary>
        public double OpsMinuteSpread
        {
            get
            {
                double[] m = [.. (OpsPerMinute ?? []).Where(v => v > 0)];
                return m.Length < 2 ? 1 : m.Max() / m.Min();
            }
        }

        public int? WorstMinute
        {
            get
            {
                if (OpsPerMinute is null || OpsPerMinute.Count < 2) return null;
                int idx = 0;
                for (int i = 1; i < OpsPerMinute.Count; i++) if (OpsPerMinute[i] < OpsPerMinute[idx]) idx = i;
                return idx;
            }
        }

        /// <summary>Throughput held minute to minute and no write failed. A failed or indeterminate write is
        /// already disqualifying under every scenario's rules; here it also means the window is not one regime.</summary>
        public bool OpsStable => OpsMinuteSpread <= MaxOpsMinuteSpread && FailedOps == 0;

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

        /// <summary>The device ballast, when the run wrote one, must have finished before the measured
        /// window opened; ballast still streaming inside the window is a second workload on the device.</summary>
        public bool PreconditionClean => Precondition is null || !Precondition.OverlapsWindow;

        /// <summary>One measurement, not two: both the durable-write cost and the leader read cost held,
        /// and no preconditioning ballast leaked into the window.</summary>
        public bool Stable => RaftStable && ReadsStable && PreconditionClean && OpsStable && Zombie is null;

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

        (List<double> opsPerMinute, long failedOps) = LoadClientMinutes(artifacts, measureSeconds);
        return new Report(Path.GetFileName(runDir.TrimEnd(Path.DirectorySeparatorChar)), leader, result, LoadPrecondition(runDir, start), opsPerMinute, failedOps,
            WorstWalLive(series, startMs, startMs + (long)measureSeconds * 1000),
            FindZombie(series, startMs, startMs + (long)measureSeconds * 1000));
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
        if (report.SteadyTailStart is int tail && report.Windows.Count > 1)
        {
            int tailWindows = report.Windows.Count - tail;
            double tailRaft = report.Windows.Skip(tail).Select(w => w.RaftMeanMs).Where(v => v > 0).DefaultIfEmpty(0).Average();
            string ops = report.SteadyTailOps is double o ? $"{o:F0} ops/s" : "ops n/a";
            Console.WriteLine(tail == 0
                ? $"  steady tail = whole run ({tailWindows} windows): {ops}, raft {tailRaft:F2} ms"
                : $"  steady tail from window {tail} ({tailWindows} of {report.Windows.Count} windows, {tailWindows * WindowSeconds / 60} min): {ops}, raft {tailRaft:F2} ms — the comparable figure when the device stepped inside the window");
        }
        if (report.Zombie is Zombie z)
            Console.WriteLine($"  *** WAL ZOMBIE: {z.Node} wrote < {ZombieBatchFraction:P0} of the busiest node's Raft batches from minute {z.FromMinute} (busiest {z.LeaderBatchesPerMinute:F0}/min; {z.Node} queue depth at end {z.LastQueueDepth:F0}) — process up, WAL writer dead; the run ran on a reduced quorum ***");
        if (report.WalLive is WalLive wl)
            Console.WriteLine(report.WalLiveBounded
                ? $"  raft-log live SST bounded: {wl.Node} {wl.FirstMb:F0} → {wl.LastMb:F0} MB ({wl.Growth:F2}x)"
                : $"  *** RAFT-LOG RETENTION GROWING: {wl.Node} live SST {wl.FirstMb:F0} → {wl.LastMb:F0} MB ({wl.Growth:F1}x, bar {MaxWalLiveGrowth:F1}x) — compaction floor not keeping up with ingest ***");
        if (report.OpsPerMinute is { Count: > 1 } opm)
        {
            Console.WriteLine($"  client ops/s by minute: {string.Join(' ', opm.Select(v => v.ToString("F0", CultureInfo.InvariantCulture)))}");
            Console.WriteLine(report.OpsMinuteSpread <= MaxOpsMinuteSpread
                ? $"  throughput held: best/worst minute {report.OpsMinuteSpread:F2}x"
                : $"  *** THROUGHPUT COLLAPSE at minute {report.WorstMinute}: best/worst minute {report.OpsMinuteSpread:F2}x (bar {MaxOpsMinuteSpread:F1}x) ***");
            if (report.FailedOps > 0)
                Console.WriteLine($"  *** {report.FailedOps} FAILED WRITES in the window: not a capacity measurement ***");
        }
        Console.WriteLine($"  follower repair events (gap / min-log-index mismatch / backfill pacing): {report.RepairEvents}");
        if (report.Precondition is Precondition pre)
            Console.WriteLine(pre.OverlapsWindow
                ? $"  *** DEVICE BALLAST OVERLAPPED THE WINDOW: {pre.Bytes / (1024.0 * 1024 * 1024):F0} GiB on {pre.Device} finished {pre.EndUtc:HH:mm:ss}Z, after measureStartUtc ***"
                : $"  device preconditioned: {pre.Bytes / (1024.0 * 1024 * 1024):F0} GiB on {pre.Device} at {pre.MegabytesPerSecond:F0} MB/s, finished {pre.EndUtc:HH:mm:ss}Z before the window (steady-regime run; not comparable to an unpreconditioned one)");
        Console.WriteLine(report.Stable
            ? "  -> one regime for the whole window; admissible for a ratio"
            : "  -> two regimes in one run; NOT admissible for a ratio (the window average is a mixture)");
    }

    // ── Readers ────────────────────────────────────────────────────────────────

    /// <summary>Per-minute completed ops/s and the total failed count from the client's <c>intervals.csv</c>
    /// (one row per measured second: second, offered, started, completed, failed, ...). Only whole minutes count.</summary>
    private static (List<double> OpsPerMinute, long FailedOps) LoadClientMinutes(string artifacts, int measureSeconds)
    {
        string path = Path.Combine(artifacts, "intervals.csv");
        List<double> minutes = [];
        long failed = 0;
        if (!File.Exists(path))
            return (minutes, failed);

        double[] sums = new double[Math.Max(1, measureSeconds / 60) + 1];
        int[] counts = new int[sums.Length];
        foreach (string line in File.ReadLines(path).Skip(1))
        {
            string[] f = line.Split(',');
            if (f.Length < 5 || !int.TryParse(f[0], out int second)) continue;
            int m = Math.Max(0, second - 1) / 60;
            if (m >= sums.Length) continue;
            if (double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double completed)) { sums[m] += completed; counts[m]++; }
            if (long.TryParse(f[4], out long fail)) failed += fail;
        }
        for (int i = 0; i < sums.Length; i++)
            if (counts[i] >= 55) minutes.Add(sums[i] / counts[i]);
        return (minutes, failed);
    }

    private static Precondition? LoadPrecondition(string runDir, DateTime measureStartUtc)
    {
        string path = Path.Combine(runDir, "precondition.json");
        if (!File.Exists(path))
            return null;

        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        System.Text.Json.JsonElement r = doc.RootElement;
        DateTime start = r.GetProperty("StartUtc").GetDateTime().ToUniversalTime();
        DateTime end = r.GetProperty("EndUtc").GetDateTime().ToUniversalTime();
        return new Precondition(start, end, r.GetProperty("Bytes").GetInt64(), r.GetProperty("MegabytesPerSecond").GetDouble(),
            r.GetProperty("Device").GetString() ?? "unknown", OverlapsWindow: end > measureStartUtc);
    }

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
                || metric.StartsWith("camus_request_duration_milliseconds_", StringComparison.Ordinal)
                || metric == "raft_wal_shard_live_sst_bytes"
                || metric == "raft_wal_batches_total"
                || metric == "raft_wal_queue_depth";
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
    /// <summary>First-minute and last-minute means of the live Raft-log SST gauge, for the node whose last-minute
    /// value is largest. Null when no node exported the gauge inside the window (pre-1.5.8 Kommander).</summary>
    private static WalLive? WorstWalLive(Dictionary<(string, string), List<(long Ts, double Value)>> series, long lo, long hi)
    {
        WalLive? worst = null;
        foreach (KeyValuePair<(string Node, string Metric), List<(long Ts, double Value)>> kv in series)
        {
            if (kv.Key.Metric != "raft_wal_shard_live_sst_bytes")
                continue;
            (long Ts, double Value)[] inWindow = [.. kv.Value.Where(p => p.Ts >= lo && p.Ts < hi)];
            if (inWindow.Length < 2)
                continue;
            long firstEnd = inWindow[0].Ts + 60_000, lastStart = inWindow[^1].Ts - 60_000;
            double first = inWindow.Where(p => p.Ts < firstEnd).Average(p => p.Value) / 1e6;
            double last = inWindow.Where(p => p.Ts >= lastStart).Average(p => p.Value) / 1e6;
            if (worst is null || last > worst.LastMb)
                worst = new WalLive(kv.Key.Node, first, last);
        }
        return worst;
    }

    /// <summary>Per-minute WAL batch counts per node; a node under <see cref="ZombieBatchFraction"/> of the busiest
    /// node for two consecutive minutes (busiest node > 0) is the zombie signature. Null when the counter is absent.</summary>
    private static Zombie? FindZombie(Dictionary<(string, string), List<(long Ts, double Value)>> series, long lo, long hi)
    {
        Dictionary<string, List<double>> perMinute = [];
        Dictionary<string, double> lastDepth = [];
        int minutes = (int)((hi - lo) / 60_000);
        foreach (KeyValuePair<(string Node, string Metric), List<(long Ts, double Value)>> kv in series)
        {
            if (kv.Key.Metric == "raft_wal_queue_depth")
            {
                (long Ts, double Value)[] d = [.. kv.Value.Where(p => p.Ts >= lo && p.Ts < hi)];
                if (d.Length > 0) lastDepth[kv.Key.Node] = d[^1].Value;
                continue;
            }
            if (kv.Key.Metric != "raft_wal_batches_total")
                continue;
            List<double> mins = [];
            for (int m = 0; m < minutes; m++)
            {
                long a = lo + (long)m * 60_000, b = a + 60_000;
                (long Ts, double Value)[] inMin = [.. kv.Value.Where(p => p.Ts >= a && p.Ts < b)];
                mins.Add(inMin.Length >= 2 ? inMin[^1].Value - inMin[0].Value : double.NaN);
            }
            perMinute[kv.Key.Node] = mins;
        }
        if (perMinute.Count < 2)
            return null;
        for (int m = 1; m < minutes; m++)
        {
            double busiestPrev = perMinute.Values.Select(v => v[m - 1]).Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Max();
            double busiest = perMinute.Values.Select(v => v[m]).Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Max();
            if (busiest <= 0 || busiestPrev <= 0)
                continue;
            foreach ((string node, List<double> v) in perMinute)
                if (!double.IsNaN(v[m]) && !double.IsNaN(v[m - 1]) && v[m] < busiest * ZombieBatchFraction && v[m - 1] < busiestPrev * ZombieBatchFraction)
                    return new Zombie(node, m - 1, lastDepth.GetValueOrDefault(node, double.NaN), busiest);
        }
        return null;
    }

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
