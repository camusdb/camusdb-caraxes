using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace P3c;

/// <summary>
/// What one unit of work cost in a run: Raft entries and proposals per commit, Raft executor client operations per
/// op, CPU and allocation per op,
/// device bytes per op, requests per write transaction, server-side time per statement kind.
///
/// <para>Why beside the regime verdict. The 2026-09-26 assessment (CamusDB Vorpal <c>84b26503</c>) had to recompute
/// every one of these by hand, and the fs8 → fs12 throughput drift (3,082 → 2,464 ops/s) went unnoticed for five
/// soaks because each report printed a throughput and a verdict but no cost per unit of work. A throughput moves for
/// many reasons, most of them the host's; a unit cost that moves is a code change. Feature <c>da82959a</c>.</para>
///
/// <para>Arithmetic. Every figure is a counter increase over the measured window (<c>run-meta.json</c>'s
/// <c>measureStartUtc</c> + <c>measureSeconds</c>) read from the 5-second <c>node-metrics.csv</c>, divided by the
/// window's span, and set against the client's completed ops/s from <c>intervals.csv</c>. Increases are reset-aware
/// (a counter that drops restarted with its process; its new value is the increase, as Prometheus' <c>increase()</c>
/// does), so a fault soak's killed node does not subtract its history. Cluster ratios (entries, proposals, requests,
/// statement means) sum every node, which is right across a leadership change; CPU, allocation and GC are per node,
/// with the write leader the node that ran the most Kahuna Raft batches.</para>
/// </summary>
public static class UnitCosts
{
    /// <summary>Bumped whenever a definition changes, so a cached <see cref="FileName"/> from an older build is recomputed.</summary>
    public const int SchemaVersion = 2;

    public const string FileName = "unit-costs.json";

    /// <summary>A unit cost that moved more than this against the previous run of the same scenario is flagged. A
    /// finding, not an admissibility rule: the verdict is about whether the window is one measurement, and a
    /// changed cost is a measurement.</summary>
    public const double FlagMove = 0.10;

    /// <summary>Kommander's system partition (<c>RaftSystemConfig.SystemPartition</c>): its proposals are placement
    /// and membership, not the workload's.</summary>
    private const string SystemPartition = "0";

    public sealed record Costs
    {
        public int Version { get; init; } = SchemaVersion;
        public string Run { get; init; } = "";
        public string Scenario { get; init; } = "";
        public DateTime MeasureStartUtc { get; init; }
        public int MeasureSeconds { get; init; }
        public double SpanSeconds { get; init; }
        public string? Stack { get; init; }
        public string? CamusDbCommit { get; init; }
        public string? Leader { get; init; }
        public bool Tmpfs { get; init; }

        public double? OpsPerSecond { get; init; }
        public double? ReadOpsPerSecond { get; init; }
        public double? WriteTxnsPerSecond { get; init; }
        public double? CommitsPerSecond { get; init; }

        public double? EntriesPerCommit { get; init; }
        public Dictionary<string, double> EntriesPerCommitByClass { get; init; } = [];
        public double? EntriesPerSecond { get; init; }
        public double? ProposalsPerCommit { get; init; }
        public double? SubmissionsPerCommit { get; init; }
        public double? ExecutorClientOpsPerOp { get; init; }
        public double? ExecutorClientOpsPerSecond { get; init; }
        public double? BatchItemsMean { get; init; }
        public double? BatchesPerSecond { get; init; }
        public double? RaftMeanMs { get; init; }
        public double? OrdinaryQueueAgeMs { get; init; }

        public double? LeaderCpuMsPerOp { get; init; }
        public double? LeaderCores { get; init; }
        public double? FollowerCpuMsPerOp { get; init; }
        public double? FollowerCores { get; init; }
        public double? ClusterCpuMsPerOp { get; init; }
        public double? LeaderAllocKbPerOp { get; init; }
        public double? LeaderAllocMbPerSecond { get; init; }
        public double? LeaderGcPausePercent { get; init; }

        public string? Device { get; init; }
        public double? DeviceWriteMbPerSecond { get; init; }
        public double? DeviceKbPerOp { get; init; }

        public double? RequestsPerWriteAttempt { get; init; }
        public double? RequestsPerCommittedTxn { get; init; }
        public Dictionary<string, double> ServerMsByKind { get; init; } = [];

        public double? OnePhaseShare { get; init; }
        public Dictionary<string, double> OnePhaseFallbacksByReason { get; init; } = [];

        public double? ServerMs(string kind) => ServerMsByKind.TryGetValue(kind, out double v) ? v : null;
    }

    /// <summary>The figures compared run to run and tabulated by <c>trend</c>, in print order.</summary>
    public static readonly (string Label, string Format, Func<Costs, double?> Read)[] Rows =
    [
        ("entries/commit",     "F2", c => c.EntriesPerCommit),
        ("proposals/commit",   "F3", c => c.ProposalsPerCommit),
        ("submissions/commit", "F2", c => c.SubmissionsPerCommit),
        ("executor ops/op",    "F2", c => c.ExecutorClientOpsPerOp),
        ("leader CPU-ms/op",   "F2", c => c.LeaderCpuMsPerOp),
        ("leader KB/op",       "F0", c => c.LeaderAllocKbPerOp),
        ("GC pause %",         "F1", c => c.LeaderGcPausePercent),
        ("device KB/op",       "F1", c => c.DeviceKbPerOp),
        ("requests/write txn", "F2", c => c.RequestsPerWriteAttempt),
        ("query ms",           "F2", c => c.ServerMs("query")),
        ("non_query ms",       "F2", c => c.ServerMs("non_query")),
        ("commit ms",          "F2", c => c.ServerMs("commit")),
    ];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    // ── Compute ────────────────────────────────────────────────────────────────

    public static Costs? Compute(string runDir)
    {
        string artifacts = Path.Combine(runDir, "artifacts", "run");
        string metricsPath = Path.Combine(artifacts, "node-metrics.csv");
        string metaPath = Path.Combine(artifacts, "run-meta.json");
        if (!File.Exists(metricsPath) || !File.Exists(metaPath))
            return null;

        (DateTime start, int measureSeconds) = ReadMeta(metaPath);
        long lo = new DateTimeOffset(start).ToUnixTimeMilliseconds();
        long hi = lo + measureSeconds * 1000L;

        CounterSet c = CounterSet.Load(metricsPath, lo, hi);
        if (c.SpanSeconds <= 0)
            return null;
        double span = c.SpanSeconds;

        (string scenario, string? stack, string? commit, bool tmpfs) = ReadScenario(runDir);
        (double? readOps, double? writeTxns) = ReadSummary(artifacts);
        double? ops = ReadClientOps(artifacts);

        string? leader = c.Nodes
            .Select(n => (Node: n, Batches: c.Sum("kahuna_kv_write_raft_duration_milliseconds_count", n) ?? 0))
            .Where(p => p.Batches > 0)
            .OrderByDescending(p => p.Batches)
            .Select(p => p.Node)
            .FirstOrDefault();
        string[] followers = [.. c.Nodes.Where(n => n != leader)];

        // Commits: one-phase bundles plus two-phase decisions (the finalizer's decision stage only runs on a fallback).
        double? onePhase = c.Sum("kahuna_durable_tx_one_phase_commits_total");
        double? decisions = c.Sum("kahuna_durable_tx_finalize_decision_ms_milliseconds_count");
        double? commits = onePhase is null && decisions is null ? null : (onePhase ?? 0) + (decisions ?? 0);

        double? entries = c.Sum("kahuna_kv_write_entries_total");
        // One Kahuna write batch is one Raft proposal (the leader's WAL takes two writes per batch: Proposed, Committed).
        double? batches = c.Sum("kahuna_kv_write_raft_duration_milliseconds_count");
        // Scheduler submissions (one-phase bundle, each materialization, each settle): what the aggregator coalesces into batches.
        double? submissions = c.Sum("kahuna_kv_write_admitted_total");
        // Kommander's Client class is proposals and commits plus read-index confirmations, local-apply waits and
        // ticket/state reads (RaftOperationMapper), so it is a per-op cost of reads as much as of writes — not proposals.
        double? executorClientOps = c.Sum("raft_executor_operations_total",
            labels: l => Label(l, "operation_class") == "Client" && Label(l, "partition_id") != SystemPartition);

        double? leaderCpu = leader is null ? null : c.Sum("dotnet_process_cpu_time_seconds_total", leader);
        double[] followerCpus = [.. followers.Select(f => c.Sum("dotnet_process_cpu_time_seconds_total", f)).OfType<double>()];
        double? followerCpu = followerCpus.Length > 0 ? followerCpus.Average() : null;
        double? clusterCpu = c.Sum("dotnet_process_cpu_time_seconds_total");
        double? leaderAlloc = leader is null ? null : c.Sum("dotnet_gc_heap_total_allocated_bytes_total", leader);
        double? leaderPause = leader is null ? null : c.Sum("dotnet_gc_pause_time_seconds_total", leader);

        double? requests = c.Sum("camus_request_count_total");
        double? begins = c.Sum("camus_request_count_total", labels: l => Label(l, "operation") == "begin");
        double? writeRequestsPerSecond = requests is double rq && readOps is double ro ? rq / span - ro : null;

        Dictionary<string, double> serverMs = [];
        foreach (string kind in c.LabelValues("camus_request_duration_milliseconds_count", "operation"))
        {
            bool Ok(string l) => Label(l, "operation") == kind && Label(l, "outcome") is null or "ok";
            if (Ratio(c.Sum("camus_request_duration_milliseconds_sum", labels: Ok), c.Sum("camus_request_duration_milliseconds_count", labels: Ok)) is double ms)
                serverMs[kind] = ms;
        }

        Dictionary<string, double> entriesByClass = [];
        if (commits is > 0)
            foreach ((string cls, double n) in c.ByLabel("kahuna_kv_write_entries_total", "class"))
                entriesByClass[cls] = n / commits.Value;

        (string? device, double? deviceMbps) = ReadHostWrites(runDir, start, measureSeconds);

        return new Costs
        {
            Run = Path.GetFileName(runDir.TrimEnd(Path.DirectorySeparatorChar)),
            Scenario = scenario,
            MeasureStartUtc = start,
            MeasureSeconds = measureSeconds,
            SpanSeconds = span,
            Stack = stack,
            CamusDbCommit = commit,
            Leader = leader,
            Tmpfs = tmpfs,

            OpsPerSecond = ops,
            ReadOpsPerSecond = readOps,
            WriteTxnsPerSecond = writeTxns,
            CommitsPerSecond = commits / span,

            EntriesPerCommit = Ratio(entries, commits),
            EntriesPerCommitByClass = entriesByClass,
            EntriesPerSecond = entries / span,
            ProposalsPerCommit = Ratio(batches, commits),
            SubmissionsPerCommit = Ratio(submissions, commits),
            ExecutorClientOpsPerOp = PerOp(executorClientOps / span, ops),
            ExecutorClientOpsPerSecond = executorClientOps / span,
            BatchItemsMean = Ratio(c.Sum("kahuna_kv_write_batch_items_sum"), c.Sum("kahuna_kv_write_batch_items_count")),
            BatchesPerSecond = batches / span,
            RaftMeanMs = Ratio(c.Sum("kahuna_kv_write_raft_duration_milliseconds_sum"), c.Sum("kahuna_kv_write_raft_duration_milliseconds_count")),
            OrdinaryQueueAgeMs = Ratio(
                c.Sum("kahuna_kv_write_queue_age_milliseconds_sum", labels: l => Label(l, "class") == "ordinary"),
                c.Sum("kahuna_kv_write_queue_age_milliseconds_count", labels: l => Label(l, "class") == "ordinary")),

            LeaderCpuMsPerOp = PerOp(leaderCpu / span * 1000, ops),
            LeaderCores = leaderCpu / span,
            FollowerCpuMsPerOp = PerOp(followerCpu / span * 1000, ops),
            FollowerCores = followerCpu / span,
            ClusterCpuMsPerOp = PerOp(clusterCpu / span * 1000, ops),
            LeaderAllocKbPerOp = PerOp(leaderAlloc / span / 1000, ops),
            LeaderAllocMbPerSecond = leaderAlloc / span / 1e6,
            LeaderGcPausePercent = leaderPause / span * 100,

            Device = device,
            DeviceWriteMbPerSecond = deviceMbps,
            // On tmpfs the data directories never reach the device; what host-io.csv saw is the host's other writers.
            DeviceKbPerOp = tmpfs ? null : PerOp(deviceMbps * 1000, ops),

            RequestsPerWriteAttempt = Ratio(writeRequestsPerSecond, begins / span),
            RequestsPerCommittedTxn = Ratio(writeRequestsPerSecond, writeTxns),
            ServerMsByKind = serverMs,

            OnePhaseShare = Ratio(onePhase, commits),
            OnePhaseFallbacksByReason = c.ByLabel("kahuna_durable_tx_one_phase_fallbacks_total", "reason"),
        };
    }

    // ── Cache, previous run, comparison ────────────────────────────────────────

    public static void Save(string runDir, Costs costs)
        => File.WriteAllText(Path.Combine(runDir, FileName), JsonSerializer.Serialize(costs, Json) + "\n");

    public static Costs? TryLoad(string runDir)
    {
        string path = Path.Combine(runDir, FileName);
        if (!File.Exists(path))
            return null;
        try
        {
            Costs? costs = JsonSerializer.Deserialize<Costs>(File.ReadAllText(path), Json);
            return costs?.Version == SchemaVersion ? costs : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The cached figures when they were written by this schema, else computed and cached.</summary>
    public static Costs? LoadOrCompute(string runDir)
    {
        if (TryLoad(runDir) is Costs cached)
            return cached;
        Costs? costs = Compute(runDir);
        if (costs is not null)
            Save(runDir, costs);
        return costs;
    }

    /// <summary>The sibling run directory of the same scenario name whose window opened last before this one's.</summary>
    public static string? FindPrevious(string runDir, Costs costs)
    {
        string? parent = Path.GetDirectoryName(Path.GetFullPath(runDir).TrimEnd(Path.DirectorySeparatorChar));
        if (parent is null)
            return null;

        string? best = null;
        DateTime bestStart = DateTime.MinValue;
        foreach (string dir in Directory.EnumerateDirectories(parent))
        {
            if (Path.GetFileName(dir) == costs.Run || ReadScenarioName(dir) != costs.Scenario)
                continue;
            string meta = Path.Combine(dir, "artifacts", "run", "run-meta.json");
            if (!File.Exists(meta))
                continue;
            DateTime start = ReadMeta(meta).Start;
            if (start < costs.MeasureStartUtc && start > bestStart)
                (best, bestStart) = (dir, start);
        }
        return best;
    }

    public sealed record Move(string Label, string Format, double Before, double After)
    {
        public double Change => After / Before - 1;
    }

    /// <summary>Every unit cost present in both runs, with its relative change.</summary>
    public static IReadOnlyList<Move> Compare(Costs before, Costs after)
    {
        List<Move> moves = [];
        foreach ((string label, string format, Func<Costs, double?> read) in Rows)
            if (read(before) is double b && read(after) is double a && b > 0)
                moves.Add(new Move(label, format, b, a));
        return moves;
    }

    // ── Print ──────────────────────────────────────────────────────────────────

    public static void Print(Costs c, Costs? previous)
    {
        string ops = c.OpsPerSecond is double o
            ? $"{o:N0} ops/s" + (c.ReadOpsPerSecond is double r && c.WriteTxnsPerSecond is double w ? $" ({r:N0} reads + {w:N0} write txns/s)" : "")
            : "ops/s n/a";
        Console.WriteLine($"  unit costs — measured window {c.SpanSeconds:N0} s, write leader {c.Leader ?? "unknown"}, {ops}{(c.Stack is null ? "" : $", {c.Stack}")}");

        string byClass = c.EntriesPerCommitByClass.Count == 0 ? ""
            : string.Join(" + ", c.EntriesPerCommitByClass.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key} {p.Value:F2}")) + "; ";
        Line("raft entries per commit", F(c.EntriesPerCommit, "F2"),
            c.EntriesPerSecond is double eps ? $"({byClass}{eps:N0} entries/s over {c.CommitsPerSecond:N0} commits/s)" : "");
        Line("raft proposals per commit", F(c.ProposalsPerCommit, "F3"),
            $"({F(c.BatchesPerSecond, "F0")} Kahuna write batches/s of {F(c.BatchItemsMean, "F0")} entries, raft {F(c.RaftMeanMs, "F2")} ms, ordinary queue age {F(c.OrdinaryQueueAgeMs, "F2")} ms)");
        Line("write submissions per commit", F(c.SubmissionsPerCommit, "F2"), "(kahuna_kv_write_admitted_total: bundle, materializations, settles — coalesced into the batches above)");
        Line("executor client ops per op", F(c.ExecutorClientOpsPerOp, "F2"),
            c.ExecutorClientOpsPerSecond is double xps ? $"({xps:N0}/s, all nodes, data partitions: proposals, commits, read-index confirmations, apply waits)" : "");
        Line("CPU-ms per op", $"leader {F(c.LeaderCpuMsPerOp, "F2")}",
            $"({F(c.LeaderCores, "F2")} cores), followers {F(c.FollowerCpuMsPerOp, "F2")} each ({F(c.FollowerCores, "F2")} cores), cluster {F(c.ClusterCpuMsPerOp, "F2")}");
        Line("allocation KB per op", $"leader {F(c.LeaderAllocKbPerOp, "F1")}",
            $"({F(c.LeaderAllocMbPerSecond, "F0")} MB/s), GC pause {F(c.LeaderGcPausePercent, "F1")}% of wall");
        if (c.Tmpfs)
            Line("device KB per op", "n/a", $"tmpfs data directories (host {c.Device ?? "device"} wrote {F(c.DeviceWriteMbPerSecond, "F1")} MB/s, not the cluster's)");
        else if (c.DeviceWriteMbPerSecond is null)
            Line("device KB per op", "n/a", "no host-io.csv (use tools/p3c/writeprobe-bytes.sh)");
        else
            Line("device KB per op", F(c.DeviceKbPerOp, "F2"), $"(host {c.Device} {F(c.DeviceWriteMbPerSecond, "F1")} MB/s, every writer on it)");
        Line("requests per write txn", $"{F(c.RequestsPerWriteAttempt, "F2")} per attempt", $"{F(c.RequestsPerCommittedTxn, "F2")} per committed txn");
        string[] kindOrder = ["begin", "query", "non_query", "commit", "rollback"];
        Line("server ms by statement", "",
            string.Join(" · ", c.ServerMsByKind.OrderBy(p => Array.IndexOf(kindOrder, p.Key) is int i and >= 0 ? i : 99).ThenBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => $"{p.Key} {p.Value:F2}")));
        string fallbacks = c.OnePhaseFallbacksByReason.Count == 0 ? "no fallbacks"
            : "fallbacks: " + string.Join(", ", c.OnePhaseFallbacksByReason.OrderByDescending(p => p.Value).Select(p => $"{p.Key} {p.Value:N0}"));
        Line("one-phase commits", c.OnePhaseShare is double s ? $"{100 * s:F1}%" : "-", $"({fallbacks})");

        if (previous is null)
        {
            Console.WriteLine($"  no earlier run of scenario '{c.Scenario}' beside this one to compare with");
            return;
        }

        IReadOnlyList<Move> moves = Compare(previous, c);
        Move[] flagged = [.. moves.Where(m => Math.Abs(m.Change) > FlagMove)];
        string head = $"  vs previous run of this scenario, {previous.Run} ({previous.MeasureStartUtc:yyyy-MM-dd HH:mm}Z)";
        Console.WriteLine(flagged.Length == 0
            ? $"{head}: no unit cost moved more than {FlagMove:P0} ({moves.Count} compared)"
            : $"{head}: *** MOVED > {FlagMove:P0}: "
              + string.Join(", ", flagged.Select(m => $"{m.Label} {m.Before.ToString(m.Format, CultureInfo.InvariantCulture)} → {m.After.ToString(m.Format, CultureInfo.InvariantCulture)} ({m.Change:+0.0%;-0.0%})"))
              + " *** — a finding, not part of the verdict");
    }

    private static void Line(string label, string value, string detail)
        => Console.WriteLine($"    {label,-30}{value,-18}{detail}".TrimEnd());

    private static string F(double? value, string format)
        => value is double v ? v.ToString(format, CultureInfo.InvariantCulture) : "-";

    // ── Readers ────────────────────────────────────────────────────────────────

    private static double? Ratio(double? numerator, double? denominator)
        => numerator is double n && denominator is double d && d > 0 ? n / d : null;

    private static double? PerOp(double? perSecond, double? ops) => Ratio(perSecond, ops);

    public static (DateTime Start, int MeasureSeconds) ReadMeta(string metaPath)
    {
        using JsonDocument meta = JsonDocument.Parse(File.ReadAllText(metaPath));
        DateTime start = meta.RootElement.GetProperty("measureStartUtc").GetDateTime().ToUniversalTime();
        int seconds = meta.RootElement.TryGetProperty("measureSeconds", out JsonElement s) ? s.GetInt32() : 2700;
        return (start, seconds);
    }

    public static string? ReadScenarioName(string runDir)
    {
        string path = Path.Combine(runDir, "scenario.json");
        if (!File.Exists(path))
            return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("scenario", out JsonElement s) ? s.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly Regex KahunaVersion = new(@"Kahuna\.Core ([^+,)\s]+)", RegexOptions.Compiled);
    private static readonly Regex KommanderVersion = new(@"Kommander ([^+,)\s]+)", RegexOptions.Compiled);

    /// <summary>"Kahuna 1.8.2 / Kommander 1.6.7" from the verdict's <c>cluster fingerprint</c> note.</summary>
    public static string? ParseStack(string? fingerprintNote)
    {
        if (fingerprintNote is null)
            return null;
        Match k = KahunaVersion.Match(fingerprintNote), ko = KommanderVersion.Match(fingerprintNote);
        if (!k.Success && !ko.Success)
            return null;
        return $"Kahuna {(k.Success ? k.Groups[1].Value : "?")} / Kommander {(ko.Success ? ko.Groups[1].Value : "?")}";
    }

    private static (string Scenario, string? Stack, string? Commit, bool Tmpfs) ReadScenario(string runDir)
    {
        string name = Path.GetFileName(runDir.TrimEnd(Path.DirectorySeparatorChar));
        string path = Path.Combine(runDir, "scenario.json");
        if (!File.Exists(path))
            return (name, null, null, name.Contains("tmpfs", StringComparison.Ordinal));

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = doc.RootElement;
        string scenario = root.TryGetProperty("scenario", out JsonElement s) ? s.GetString() ?? name : name;
        string? commit = root.TryGetProperty("camusdbGitCommit", out JsonElement c) ? c.GetString() : null;
        string? fingerprint = null;
        if (root.TryGetProperty("verdict", out JsonElement verdict) && verdict.TryGetProperty("Notes", out JsonElement notes))
            fingerprint = notes.EnumerateArray().Select(n => n.GetString()).FirstOrDefault(n => n?.Contains("cluster fingerprint", StringComparison.Ordinal) == true);
        string cluster = root.TryGetProperty("cluster", out JsonElement cl) && cl.TryGetProperty("Name", out JsonElement cn) ? cn.GetString() ?? "" : "";

        // The run directory does not record data_tmpfs_mb; every tmpfs scenario and cluster on this host says so in its name.
        bool tmpfs = scenario.Contains("tmpfs", StringComparison.Ordinal) || cluster.Contains("tmpfs", StringComparison.Ordinal);
        return (scenario, ParseStack(fingerprint), commit, tmpfs);
    }

    private static (double? ReadOps, double? WriteTxns) ReadSummary(string artifacts)
    {
        string path = Path.Combine(artifacts, "summary.json");
        if (!File.Exists(path))
            return (null, null);
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement r = doc.RootElement;
        return (r.TryGetProperty("ReadOpsPerSec", out JsonElement read) ? read.GetDouble() : null,
            r.TryGetProperty("WriteTxnsPerSec", out JsonElement write) ? write.GetDouble() : null);
    }

    /// <summary>Mean completed ops/s over the client's measured seconds.</summary>
    private static double? ReadClientOps(string artifacts)
    {
        string path = Path.Combine(artifacts, "intervals.csv");
        if (!File.Exists(path))
            return null;
        using IEnumerator<string> lines = File.ReadLines(path).GetEnumerator();
        if (!lines.MoveNext())
            return null;
        int iCompleted = Array.IndexOf(lines.Current.Split(','), "completed");
        if (iCompleted < 0)
            return null;

        double sum = 0;
        int n = 0;
        while (lines.MoveNext())
        {
            string[] cells = lines.Current.Split(',');
            if (cells.Length > iCompleted && double.TryParse(cells[iCompleted], NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            {
                sum += v;
                n++;
            }
        }
        return n > 0 ? sum / n : null;
    }

    /// <summary>Mean device write rate (decimal MB/s) over the window from the harness's <c>host-io.csv</c>.</summary>
    private static (string? Device, double? MegabytesPerSecond) ReadHostWrites(string runDir, DateTime start, int measureSeconds)
    {
        string path = Path.Combine(runDir, "host-io.csv");
        if (!File.Exists(path))
            return (null, null);

        using IEnumerator<string> lines = File.ReadLines(path).GetEnumerator();
        if (!lines.MoveNext())
            return (null, null);
        string[] header = lines.Current.Split(',');
        int iTs = Array.IndexOf(header, "ts"), iDevice = Array.IndexOf(header, "device"), iWrite = Array.IndexOf(header, "w_mb_s");
        if (iTs < 0 || iWrite < 0)
            return (null, null);

        DateTime end = start.AddSeconds(measureSeconds);
        string? device = null;
        double sum = 0;
        int n = 0;
        while (lines.MoveNext())
        {
            string[] c = lines.Current.Split(',');
            if (c.Length <= Math.Max(iTs, iWrite)
                || !DateTime.TryParse(c[iTs], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime ts)
                || ts < start || ts >= end
                || !double.TryParse(c[iWrite], NumberStyles.Float, CultureInfo.InvariantCulture, out double mbps))
                continue;
            device ??= iDevice >= 0 && iDevice < c.Length ? c[iDevice] : null;
            sum += mbps;
            n++;
        }
        return n > 0 ? (device, sum / n) : (null, null);
    }

    /// <summary>The value of one label in a <c>node-metrics.csv</c> label cell (<c>k=v;k=v</c>), or null.</summary>
    public static string? Label(string labels, string name)
    {
        foreach (string pair in labels.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq > 0 && pair.AsSpan(0, eq).SequenceEqual(name))
                return pair[(eq + 1)..];
        }
        return null;
    }
}

/// <summary>
/// Reset-aware counter increases over one window of <c>node-metrics.csv</c>, per (node, metric, label cell), for the
/// metrics <see cref="UnitCosts"/> reads. Streams the file: a two-hour soak's series is ~300 MB.
/// </summary>
public sealed class CounterSet
{
    public static readonly HashSet<string> Metrics = new(StringComparer.Ordinal)
    {
        "kahuna_kv_write_entries_total",
        "kahuna_kv_write_admitted_total",
        "kahuna_kv_write_batch_items_sum",
        "kahuna_kv_write_batch_items_count",
        "kahuna_kv_write_raft_duration_milliseconds_sum",
        "kahuna_kv_write_raft_duration_milliseconds_count",
        "kahuna_kv_write_queue_age_milliseconds_sum",
        "kahuna_kv_write_queue_age_milliseconds_count",
        "kahuna_durable_tx_one_phase_commits_total",
        "kahuna_durable_tx_one_phase_fallbacks_total",
        "kahuna_durable_tx_finalize_decision_ms_milliseconds_count",
        "raft_executor_operations_total",
        "dotnet_process_cpu_time_seconds_total",
        "dotnet_gc_heap_total_allocated_bytes_total",
        "dotnet_gc_pause_time_seconds_total",
        "camus_request_count_total",
        "camus_request_duration_milliseconds_sum",
        "camus_request_duration_milliseconds_count",
    };

    private struct Acc
    {
        public long LastTs;
        public double Last;
        public double Increase;
    }

    private readonly Dictionary<(string Node, string Metric, string Labels), double> increases;

    /// <summary>First to last sample timestamp inside the window, across every node and metric.</summary>
    public double SpanSeconds { get; }

    public IReadOnlyList<string> Nodes { get; }

    private CounterSet(Dictionary<(string Node, string Metric, string Labels), double> increases, double spanSeconds)
    {
        this.increases = increases;
        SpanSeconds = spanSeconds;
        Nodes = [.. increases.Keys.Select(k => k.Node).Distinct().Order(StringComparer.Ordinal)];
    }

    public static CounterSet Load(string path, long lo, long hi)
    {
        HashSet<string>.AlternateLookup<ReadOnlySpan<char>> wanted = Metrics.GetAlternateLookup<ReadOnlySpan<char>>();
        Dictionary<(string, string, string), Acc> acc = [];
        long first = long.MaxValue, last = long.MinValue;
        bool header = true;

        foreach (string line in File.ReadLines(path))
        {
            if (header) { header = false; continue; }

            ReadOnlySpan<char> s = line;
            int c1 = s.IndexOf(',');
            if (c1 < 0 || !long.TryParse(s[..c1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ts) || ts < lo || ts >= hi)
                continue;
            first = Math.Min(first, ts);
            last = Math.Max(last, ts);

            int c2 = c1 + 1 + s[(c1 + 1)..].IndexOf(',');
            if (c2 <= c1) continue;
            int c3 = c2 + 1 + s[(c2 + 1)..].IndexOf(',');
            if (c3 <= c2) continue;
            ReadOnlySpan<char> metric = s[(c2 + 1)..c3];
            if (!wanted.TryGetValue(metric, out string? metricName))
                continue;
            int c4 = s.LastIndexOf(',');
            if (c4 <= c3 || !double.TryParse(s[(c4 + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                continue;

            (string, string, string) key = (line[(c1 + 1)..c2], metricName, line[(c3 + 1)..c4]);
            if (!acc.TryGetValue(key, out Acc a))
            {
                acc[key] = new Acc { LastTs = ts, Last = value };
                continue;
            }
            if (ts <= a.LastTs)
                continue;
            // A drop is a restart: the counter began again at zero, so its new value is what accrued since.
            a.Increase += value >= a.Last ? value - a.Last : value;
            a.Last = value;
            a.LastTs = ts;
            acc[key] = a;
        }

        // Series that differ only by OpenTelemetry scope labels are one series for every question asked here.
        Dictionary<(string Node, string Metric, string Labels), double> increases = [];
        foreach (((string node, string metric, string labels), Acc a) in acc)
        {
            (string, string, string) key = (node, metric, StripScope(labels));
            increases[key] = increases.GetValueOrDefault(key) + a.Increase;
        }
        return new CounterSet(increases, last > first ? (last - first) / 1000.0 : 0);
    }

    /// <summary>Sum of the increases of one metric, optionally on one node and filtered by label cell; null when no
    /// such series was sampled in the window (an older build that did not export it).</summary>
    public double? Sum(string metric, string? node = null, Func<string, bool>? labels = null)
    {
        double total = 0;
        bool found = false;
        foreach (((string n, string m, string l), double v) in increases)
        {
            if (m != metric || (node is not null && n != node) || (labels is not null && !labels(l)))
                continue;
            total += v;
            found = true;
        }
        return found ? total : null;
    }

    /// <summary>Increases of one metric summed over every node, grouped by one label's value.</summary>
    public Dictionary<string, double> ByLabel(string metric, string label)
    {
        Dictionary<string, double> result = [];
        foreach (((_, string m, string l), double v) in increases)
            if (m == metric && UnitCosts.Label(l, label) is string value)
                result[value] = result.GetValueOrDefault(value) + v;
        return result;
    }

    public IEnumerable<string> LabelValues(string metric, string label)
        => increases.Keys.Where(k => k.Metric == metric).Select(k => UnitCosts.Label(k.Labels, label)).OfType<string>().Distinct();

    private static string StripScope(string labels)
        => labels.Contains("otel_scope", StringComparison.Ordinal)
            ? string.Join(';', labels.Split(';').Where(p => !p.StartsWith("otel_scope_", StringComparison.Ordinal)))
            : labels;
}
