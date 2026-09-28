using System.Globalization;
using System.Text;

namespace P3c.Tests;

/// <summary>
/// The unit-cost arithmetic on a synthetic run directory whose counters grow at known rates, so every figure has
/// an exact expected value. The 100-second window holds samples at 0, 5, ..., 95 s (span 95 s); samples before and
/// after it carry values that would distort every figure if the window were ignored.
/// </summary>
public class UnitCostsTests
{
    private static readonly DateTime T0 = new(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);

    private string root = "";

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "p3c-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(root, recursive: true);

    [Test]
    public void EveryFigureMatchesTheKnownRates()
    {
        string run = WriteRun("bank-synthetic-a1", "bank-synthetic", T0);

        UnitCosts.Costs c = UnitCosts.Compute(run)!;

        Assert.Multiple(() =>
        {
            Assert.That(c.Leader, Is.EqualTo("camus1"));
            Assert.That(c.SpanSeconds, Is.EqualTo(95));
            Assert.That(c.OpsPerSecond, Is.EqualTo(1000).Within(1e-9));
            Assert.That(c.Stack, Is.EqualTo("Kahuna 1.9.2 / Kommander 1.7.3"));

            // 600 entries/s over 90 one-phase commits + 10 two-phase decisions per second.
            Assert.That(c.CommitsPerSecond, Is.EqualTo(100).Within(1e-9));
            Assert.That(c.EntriesPerCommit, Is.EqualTo(6.0).Within(1e-9));
            Assert.That(c.EntriesPerCommitByClass["ordinary"], Is.EqualTo(3.0).Within(1e-9));
            Assert.That(c.EntriesPerCommitByClass["terminal"], Is.EqualTo(3.0).Within(1e-9));
            // One proposal per Kahuna write batch: 10 batches/s over 100 commits/s.
            Assert.That(c.ProposalsPerCommit, Is.EqualTo(0.1).Within(1e-9));
            // 150 ordinary + 250 terminal scheduler submissions per second over 100 commits/s.
            Assert.That(c.SubmissionsPerCommit, Is.EqualTo(4.0).Within(1e-9));
            // Client-class executor ops on every node's data partition (175 + 25 per second); the system partition's
            // 1,000/s and the Replication class are not the workload's.
            Assert.That(c.ExecutorClientOpsPerOp, Is.EqualTo(0.2).Within(1e-9));
            Assert.That(c.BatchItemsMean, Is.EqualTo(60).Within(1e-9));
            Assert.That(c.RaftMeanMs, Is.EqualTo(2).Within(1e-9));
            Assert.That(c.OrdinaryQueueAgeMs, Is.EqualTo(5).Within(1e-9));

            Assert.That(c.LeaderCores, Is.EqualTo(4).Within(1e-9));
            Assert.That(c.LeaderCpuMsPerOp, Is.EqualTo(4).Within(1e-9));
            // The follower restarted at 50 s: its counter dropped and the increase must still be 1 core-second per second.
            Assert.That(c.FollowerCores, Is.EqualTo(1).Within(1e-9));
            Assert.That(c.FollowerCpuMsPerOp, Is.EqualTo(1).Within(1e-9));
            Assert.That(c.ClusterCpuMsPerOp, Is.EqualTo(5).Within(1e-9));
            Assert.That(c.LeaderAllocKbPerOp, Is.EqualTo(100).Within(1e-9));
            Assert.That(c.LeaderGcPausePercent, Is.EqualTo(4).Within(1e-9));

            Assert.That(c.DeviceWriteMbPerSecond, Is.EqualTo(2).Within(1e-9));
            Assert.That(c.DeviceKbPerOp, Is.EqualTo(2).Within(1e-9));

            // 1,140 requests/s, 500 of them the workload's standalone reads, over 110 BEGINs/s and 100 committed txns/s.
            Assert.That(c.RequestsPerWriteAttempt, Is.EqualTo(640.0 / 110).Within(1e-9));
            Assert.That(c.RequestsPerCommittedTxn, Is.EqualTo(6.4).Within(1e-9));
            Assert.That(c.ServerMs("begin"), Is.EqualTo(0.4).Within(1e-9));
            Assert.That(c.ServerMs("query"), Is.EqualTo(1.0).Within(1e-9));
            // outcome=conflict answers (1,000 ms each) are not the statement's cost.
            Assert.That(c.ServerMs("non_query"), Is.EqualTo(1.5).Within(1e-9));
            Assert.That(c.ServerMs("commit"), Is.EqualTo(11).Within(1e-9));

            Assert.That(c.OnePhaseShare, Is.EqualTo(0.9).Within(1e-9));
            Assert.That(c.OnePhaseFallbacksByReason["foreign_intent"], Is.EqualTo(950).Within(1e-9));
        });
    }

    [Test]
    public void TmpfsRunHasNoDeviceBytesPerOp()
    {
        string run = WriteRun("bank-synthetic-tmpfs-a1", "bank-synthetic-tmpfs", T0);

        UnitCosts.Costs c = UnitCosts.Compute(run)!;

        Assert.That(c.Tmpfs, Is.True);
        Assert.That(c.DeviceKbPerOp, Is.Null);
        Assert.That(c.DeviceWriteMbPerSecond, Is.EqualTo(2).Within(1e-9));
    }

    [Test]
    public void PreviousRunIsTheLatestEarlierRunOfTheSameScenario()
    {
        WriteRun("bank-synthetic-a1", "bank-synthetic", T0);
        string b = WriteRun("bank-synthetic-b1", "bank-synthetic", T0.AddHours(1));
        WriteRun("bank-other-c1", "bank-other", T0.AddHours(1.5));
        WriteRun("bank-synthetic-e1", "bank-synthetic", T0.AddHours(3));
        string d = WriteRun("bank-synthetic-d1", "bank-synthetic", T0.AddHours(2), leaderCpuScale: 1.2);

        UnitCosts.Costs current = UnitCosts.Compute(d)!;
        string? previous = UnitCosts.FindPrevious(d, current);

        Assert.That(previous, Is.EqualTo(b));

        IReadOnlyList<UnitCosts.Move> moves = UnitCosts.Compare(UnitCosts.Compute(previous!)!, current);
        UnitCosts.Move[] flagged = [.. moves.Where(m => Math.Abs(m.Change) > UnitCosts.FlagMove)];
        Assert.That(flagged.Select(m => m.Label), Is.EqualTo(new[] { "leader CPU-ms/op" }));
        Assert.That(flagged[0].Change, Is.EqualTo(0.2).Within(1e-9));
    }

    [Test]
    public void StaleCacheIsRecomputedAndCurrentCacheIsReused()
    {
        string run = WriteRun("bank-synthetic-a1", "bank-synthetic", T0);
        string cache = Path.Combine(run, UnitCosts.FileName);
        File.WriteAllText(cache, """{ "version": 0, "run": "stale", "entriesPerCommit": 99 }""");

        UnitCosts.Costs recomputed = UnitCosts.LoadOrCompute(run)!;
        Assert.That(recomputed.EntriesPerCommit, Is.EqualTo(6.0).Within(1e-9));
        Assert.That(UnitCosts.TryLoad(run)!.Version, Is.EqualTo(UnitCosts.SchemaVersion));

        // A current-schema cache is trusted as written: the backfill does not reparse a two-hour series twice.
        UnitCosts.Save(run, recomputed with { EntriesPerCommit = 7.5 });
        Assert.That(UnitCosts.LoadOrCompute(run)!.EntriesPerCommit, Is.EqualTo(7.5));
    }

    [Test]
    public void TrendKeepsThePrefixAndOrdersByWindowStart()
    {
        WriteRun("bank-synthetic-late", "bank-synthetic", T0.AddHours(2));
        WriteRun("bank-synthetic-early", "bank-synthetic-nvme", T0);
        WriteRun("append-smoke-x", "append-smoke", T0.AddHours(1));

        IReadOnlyList<UnitCosts.Costs> rows = Trend.Collect(root, "bank-synthetic", since: null);
        Assert.That(rows.Select(r => r.Run), Is.EqualTo(new[] { "bank-synthetic-early", "bank-synthetic-late" }));

        IReadOnlyList<UnitCosts.Costs> since = Trend.Collect(root, "bank-synthetic", since: T0.AddHours(1));
        Assert.That(since.Select(r => r.Run), Is.EqualTo(new[] { "bank-synthetic-late" }));
        Assert.That(File.Exists(Path.Combine(root, "bank-synthetic-late", UnitCosts.FileName)), Is.True);
    }

    [Test]
    public void StackComesFromTheFingerprintNote()
    {
        Assert.That(UnitCosts.ParseStack(
                "cluster fingerprint: sha256:0eb6df8b596d0054 (server 0.12.1, Kahuna.Core 1.8.5-fence.1+ab43c7a0, Kommander 1.6.10-fence.1+dd7340c9, Nixie 1.3.1+46996)"),
            Is.EqualTo("Kahuna 1.8.5-fence.1 / Kommander 1.6.10-fence.1"));
        Assert.That(UnitCosts.ParseStack(null), Is.Null);
        Assert.That(UnitCosts.ParseStack("placement held"), Is.Null);
    }

    // ── Synthetic run ──────────────────────────────────────────────────────────

    /// <summary>One counter: node, metric, label cell, growth per second.</summary>
    private sealed record Series(string Node, string Metric, string Labels, double Rate, int? ResetAtSecond = null);

    private string WriteRun(string name, string scenario, DateTime start, double leaderCpuScale = 1.0)
    {
        string dir = Path.Combine(root, name);
        string artifacts = Path.Combine(dir, "artifacts", "run");
        Directory.CreateDirectory(artifacts);

        File.WriteAllText(Path.Combine(artifacts, "run-meta.json"),
            $$"""{ "measureStartUtc": "{{start:yyyy-MM-ddTHH:mm:ss.fffffffZ}}", "warmupSeconds": 10, "measureSeconds": 100, "drainSeconds": 10 }""");
        File.WriteAllText(Path.Combine(artifacts, "summary.json"),
            """{ "AchievedOpsPerSec": 1000, "ReadOpsPerSec": 500, "WriteTxnsPerSec": 100 }""");
        File.WriteAllText(Path.Combine(dir, "scenario.json"), $$"""
            { "scenario": "{{scenario}}", "cluster": { "Name": "synthetic" }, "camusdbGitCommit": "abc1234",
              "verdict": { "Passed": true, "Notes": [ "cluster fingerprint: sha256:00 (server 0.13.0, Kahuna.Core 1.9.2+aa, Kommander 1.7.3+bb, Nixie 1.3.1+cc)" ] } }
            """);

        StringBuilder intervals = new("second,offered,started,completed,failed\n");
        for (int s = 0; s < 100; s++)
            intervals.Append(CultureInfo.InvariantCulture, $"{s},1000,1000,1000,0\n");
        File.WriteAllText(Path.Combine(artifacts, "intervals.csv"), intervals.ToString());

        StringBuilder hostIo = new("ts,device,r_per_s,r_mb_s,w_per_s,w_mb_s,util_pct,fsync_ms,load1\n");
        for (int t = -10; t <= 110; t += 5)
            hostIo.Append(CultureInfo.InvariantCulture,
                $"{start.AddSeconds(t):yyyy-MM-ddTHH:mm:ss.fffffffZ},nvme0n1p2,0,0.0,100,{(t is >= 0 and < 100 ? "2.0" : "50.0")},10,0.05,1.0\n");
        File.WriteAllText(Path.Combine(dir, "host-io.csv"), hostIo.ToString());

        const string Scope = "otel_scope_name=Kahuna;otel_scope_version=1.0";
        double cpu = leaderCpuScale;
        Series[] series =
        [
            new("camus1", "kahuna_kv_write_raft_duration_milliseconds_count", Scope, 10),
            new("camus1", "kahuna_kv_write_raft_duration_milliseconds_sum", Scope, 20),
            new("camus1", "kahuna_kv_write_batch_items_count", Scope, 10),
            new("camus1", "kahuna_kv_write_batch_items_sum", Scope, 600),
            new("camus1", "kahuna_kv_write_entries_total", "class=ordinary;" + Scope, 300),
            new("camus1", "kahuna_kv_write_entries_total", "class=terminal;" + Scope, 300),
            new("camus1", "kahuna_kv_write_admitted_total", "class=ordinary;" + Scope, 150),
            new("camus1", "kahuna_kv_write_admitted_total", "class=terminal;" + Scope, 250),
            new("camus1", "kahuna_kv_write_queue_age_milliseconds_count", "class=ordinary;" + Scope, 10),
            new("camus1", "kahuna_kv_write_queue_age_milliseconds_sum", "class=ordinary;" + Scope, 50),
            new("camus1", "kahuna_kv_write_queue_age_milliseconds_count", "class=terminal;" + Scope, 1),
            new("camus1", "kahuna_kv_write_queue_age_milliseconds_sum", "class=terminal;" + Scope, 1000),
            new("camus1", "kahuna_durable_tx_one_phase_commits_total", Scope, 90),
            new("camus1", "kahuna_durable_tx_finalize_decision_ms_milliseconds_count", Scope, 10),
            new("camus1", "kahuna_durable_tx_one_phase_fallbacks_total", "reason=foreign_intent;" + Scope, 10),
            new("camus1", "raft_executor_operations_total", "operation_class=Client;otel_scope_name=Kommander;partition_id=1", 175),
            new("camus1", "raft_executor_operations_total", "operation_class=Client;otel_scope_name=Kommander;partition_id=0", 1000),
            new("camus1", "raft_executor_operations_total", "operation_class=Replication;otel_scope_name=Kommander;partition_id=1", 500),
            new("camus1", "dotnet_process_cpu_time_seconds_total", "cpu_mode=user;otel_scope_name=System.Runtime", 3 * cpu),
            new("camus1", "dotnet_process_cpu_time_seconds_total", "cpu_mode=system;otel_scope_name=System.Runtime", 1 * cpu),
            new("camus1", "dotnet_gc_heap_total_allocated_bytes_total", "otel_scope_name=System.Runtime", 100e6),
            new("camus1", "dotnet_gc_pause_time_seconds_total", "otel_scope_name=System.Runtime", 0.04),
            .. Requests("begin", "ok", 110, 0.4),
            .. Requests("query", "ok", 700, 1.0),
            .. Requests("non_query", "ok", 220, 1.5),
            .. Requests("commit", "ok", 100, 11),
            .. Requests("rollback", "ok", 10, 1.2),
            new("camus1", "camus_request_duration_milliseconds_count", "operation=non_query;outcome=conflict", 1),
            new("camus1", "camus_request_duration_milliseconds_sum", "operation=non_query;outcome=conflict", 1000),

            new("camus2", "kahuna_kv_write_raft_duration_milliseconds_count", Scope, 0),
            new("camus2", "raft_executor_operations_total", "operation_class=Client;otel_scope_name=Kommander;partition_id=1", 25),
            new("camus2", "dotnet_process_cpu_time_seconds_total", "cpu_mode=user;otel_scope_name=System.Runtime", 1, ResetAtSecond: 50),
            new("camus2", "workload_scrape_ok", "", 0),
        ];

        StringBuilder csv = new("﻿unix_ms,node,metric,labels,value\n");
        long startMs = new DateTimeOffset(start).ToUnixTimeMilliseconds();
        for (int t = -10; t <= 110; t += 5)
            foreach (Series s in series)
                csv.Append(CultureInfo.InvariantCulture, $"{startMs + t * 1000L},{s.Node},{s.Metric},{s.Labels},{Value(s, t)}\n");
        File.WriteAllText(Path.Combine(artifacts, "node-metrics.csv"), csv.ToString());

        return dir;
    }

    private static IEnumerable<Series> Requests(string operation, string outcome, double perSecond, double meanMs)
    {
        string labels = $"operation={operation};otel_scope_name=CamusDB.Server;otel_scope_version=1.0.0;outcome={outcome};transport=grpc_batch";
        yield return new("camus1", "camus_request_count_total", labels, perSecond);
        yield return new("camus1", "camus_request_duration_milliseconds_count", labels, perSecond);
        yield return new("camus1", "camus_request_duration_milliseconds_sum", labels, perSecond * meanMs);
    }

    /// <summary>The counter's value at second <paramref name="t"/>: linear inside the window from a non-zero base; zero
    /// before the window and ten times the window's growth after it, so reading outside the window is visible. A
    /// series with a reset restarts from zero at the sample 5 s before <see cref="Series.ResetAtSecond"/>.</summary>
    private static string Value(Series s, int t)
    {
        double v;
        if (t < 0)
            v = 0;
        else if (t >= 100)
            v = s.Rate * (1000 + 100 * 10);
        else if (s.ResetAtSecond is int reset && t >= reset)
            v = s.Rate * (t - reset + 5);
        else
            v = s.Rate * (1000 + t);
        return v.ToString("R", CultureInfo.InvariantCulture);
    }
}
