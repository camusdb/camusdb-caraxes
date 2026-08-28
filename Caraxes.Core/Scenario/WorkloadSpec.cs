/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

namespace Caraxes.Core.Scenario;

/// <summary>
/// The SQL workload driven against a cluster, mapping onto <c>CamusDB.Workload</c>'s
/// <c>init</c>/<c>run</c> flags. Defaults are lighter than the workload's own (shorter duration,
/// fewer rows) because a chaos scenario runs many of these and cares about behavior under fault,
/// not peak throughput — override per scenario when a longer measured window is wanted.
/// </summary>
public sealed class WorkloadSpec
{
    /// <summary>Write shape: <c>accounts</c> (shard-disjoint read-modify-write, conflict-free),
    /// <c>bank</c> (contended transfers across the keyspace with a conserved <c>SUM(balance)</c>
    /// atomicity invariant checked post-run), or <c>fanout</c> (bank transfers whose two legs always
    /// land in different tables; requires <see cref="Tables"/> &gt;= 2). Bank is the stronger anomaly
    /// detector under contention and faults; fanout keeps that invariant and adds the placement
    /// pressure — every table is a separate key space, so a many-table dataset loads every partition
    /// and, under <c>key_range_sharding</c>, gives the range splitter something to split.</summary>
    public string Kind { get; set; } = "accounts";

    /// <summary>Database the workload seeds and drives; <c>init</c> creates it if absent.</summary>
    public string Database { get; set; } = "caraxes";

    public ulong Seed { get; set; } = 1847;

    public long Rows { get; set; } = 100_000;

    /// <summary>How many tables the seeded rows are spread over. 1 (the default) is the historical
    /// single-table dataset — one key space, so one partition under hash routing and one range under
    /// key-range routing. A higher count cuts the rows into that many contiguous blocks, one table
    /// each, which is what puts load on every partition and makes auto-split reachable. Must not
    /// exceed <see cref="Rows"/>; <c>fanout</c> needs at least 2.</summary>
    public int Tables { get; set; } = 1;

    public int PayloadBytes { get; set; } = 256;

    /// <summary>Rows per seeding transaction during <c>init</c>.</summary>
    public int Batch { get; set; } = 500;

    /// <summary>Load model: <c>open</c> (fixed arrival rate) or <c>closed</c> (saturation).</summary>
    public string Mode { get; set; } = "open";

    /// <summary>Open-loop submitted operations per second.</summary>
    public int TargetOps { get; set; } = 500;

    public int Workers { get; set; } = 32;

    public int ReadPercent { get; set; } = 60;

    public int WritePercent { get; set; } = 40;

    public int WritesPerTransaction { get; set; } = 1;

    public string Duration { get; set; } = "60s";

    public string Warmup { get; set; } = "15s";

    public string Drain { get; set; } = "10s";

    public int Connections { get; set; } = 8;

    public int MaxInFlight { get; set; } = 4096;

    /// <summary>Write-transaction locking: <c>optimistic</c> or <c>pessimistic</c>. Defaults to
    /// empty, which inherits the cluster's <c>locking</c> so a scenario's two halves agree unless
    /// deliberately mismatched.</summary>
    public string Locking { get; set; } = "";

    /// <summary>Write-transaction isolation: <c>read_committed</c> or <c>serializable</c>. Empty
    /// inherits the cluster's <c>isolation</c>.</summary>
    public string Isolation { get; set; } = "";

    public bool NoAutoPrepare { get; set; }

    /// <summary>Per-request timeout in seconds; 0 leaves the client default.</summary>
    public int RequestTimeout { get; set; }

    /// <summary>Tolerate conflicts and open-loop pacing shortfalls as warnings rather than INVALID,
    /// and give reconciliation an indeterminate-commit band. Default true: a Phase 2 baseline is
    /// fault-free, but a scenario with faults needs this, and leaving it on never loosens a clean
    /// run's verdict beyond the conflict/pacing waivers a chaos run legitimately produces.</summary>
    public bool ExpectFaults { get; set; } = true;

    /// <summary>Seconds the workload's reconciliation keeps retrying its aggregate reads while the
    /// cluster is still settling, before reporting "could not verify". 0 leaves the workload default
    /// (600s).
    ///
    /// This exists because a cluster can finish the measured window while a node is still draining
    /// internal read work, and stay slow to answer an aggregate for minutes. Measured on the
    /// 2026-08-26 bank soak: reconciliation gave up 5.5 minutes after drain and the same
    /// SUM(balance) succeeded 78 seconds later in 7 seconds — a false "cluster stayed unavailable",
    /// with the conservation invariant left unverified. Reconciliation is post-measurement, so
    /// waiting longer costs wall-clock only and never touches the measured numbers. Raise it for
    /// long soaks; lower it for smoke scenarios that should fail fast.</summary>
    public int ReconcileTimeout { get; set; }

    public void Validate()
    {
        if (Kind is not ("accounts" or "bank" or "fanout"))
            throw new ScenarioException($"'workload.kind' must be 'accounts', 'bank' or 'fanout', got '{Kind}'");

        if (string.IsNullOrWhiteSpace(Database) || Database is "default" or "system")
            throw new ScenarioException($"'workload.database' must be a non-empty, non-reserved name, got '{Database}'");

        if (Rows < 1)
            throw new ScenarioException($"'workload.rows' must be >= 1, got {Rows}");

        if (Tables < 1)
            throw new ScenarioException($"'workload.tables' must be >= 1, got {Tables}");

        // An empty table is created, never read, and never splits, so the run would test a smaller
        // placement than the scenario claims — and say nothing about it.
        if (Tables > Rows)
            throw new ScenarioException(
                $"'workload.tables' ({Tables}) must not exceed 'workload.rows' ({Rows}); every table needs at least one row");

        if (Kind == "fanout" && Tables < 2)
            throw new ScenarioException(
                $"'workload.kind: fanout' moves each transfer between two different tables, so 'workload.tables' must be >= 2, got {Tables}");

        if (Mode is not ("open" or "closed"))
            throw new ScenarioException($"'workload.mode' must be 'open' or 'closed', got '{Mode}'");

        if (ReadPercent + WritePercent != 100)
            throw new ScenarioException(
                $"'workload.read_percent' + 'workload.write_percent' must equal 100, got {ReadPercent} + {WritePercent}");

        if (Locking is not ("" or "optimistic" or "pessimistic"))
            throw new ScenarioException($"'workload.locking' must be 'optimistic' or 'pessimistic', got '{Locking}'");

        if (Isolation is not ("" or "read_committed" or "serializable"))
            throw new ScenarioException($"'workload.isolation' must be 'read_committed' or 'serializable', got '{Isolation}'");

        if (Workers < 1)
            throw new ScenarioException($"'workload.workers' must be >= 1, got {Workers}");
    }
}
