/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

namespace Caraxes.Core.Scenario;

/// <summary>
/// The post-run proof for a backup scenario. A backup that was taken is not a backup that works;
/// the chain has to be closed, resolved, restored, and the restore read back and compared with what
/// the live cluster held. Every step is graded: a step that could not run fails the run rather
/// than being skipped, because "not verified" must never read as "verified".
///
/// <para>Runs after the workload, in this order: (1) <see cref="FinalIncremental"/> — one closing
/// incremental on <see cref="Node"/> once the load has stopped, so the chain covers the final ledger;
/// (2) the live ledger fingerprint (row count, <c>SUM(balance)</c>, <c>SUM(version)</c>,
/// <c>MAX(version)</c>) is read from the live cluster; (3) the chain ending at the newest backup is
/// resolved and checked (a full at the root, every link valid, every backup the run took still in
/// it); (4) <see cref="Restore"/> — the chain is restored on the node into its restore root; (5) after
/// the live cluster is torn down, <see cref="Boot"/> — the disaster-recovery rehearsal: the same fleet
/// is brought back up with every node's data root taken from the restore, and the ledger fingerprint
/// is read from every node and compared with the live one. Equal fingerprints on every node, with
/// the live run's own reconciliation passed, is the no-data-loss claim.</para>
/// </summary>
public sealed class BackupVerifySpec
{
    /// <summary>The node whose chain is verified. Empty picks the node the schedule's <c>backup</c>
    /// events name most often, and <c>camus1</c> when they name none (a <c>random</c> target).</summary>
    public string Node { get; set; } = "";

    /// <summary>Take one closing incremental after the workload stops, so the chain ends at the final
    /// ledger and the comparison is exact. Default true.</summary>
    public bool FinalIncremental { get; set; } = true;

    /// <summary>Restore the chain on the node (<c>POST /v1/restore</c> into its restore root). Default
    /// true. Off, the verification stops at chain resolution and the rehearsal cannot run.</summary>
    public bool Restore { get; set; } = true;

    /// <summary>Boot the cluster from the restore after teardown and read the ledger back. Default
    /// true. Needs every node to host every range (<c>replication_factor</c> 0 or equal to
    /// <c>nodes</c>): one node's chain covers only the partitions that node held, and a fleet seeded
    /// from it would otherwise be missing data by construction. Needs <c>teardown: true</c>, because
    /// the rehearsal fleet takes the live fleet's addresses.</summary>
    public bool Boot { get; set; } = true;

    /// <summary>Seconds the restore request may take before the harness gives up on it. The node aborts the
    /// restore when the request is aborted, so this must cover the whole replay: the base-image copy plus
    /// every incremental's entries at the engine's replay rate (about 55,000 entries per second on the
    /// benchmark host: 327,315 in 6 s on preflight pitr1). A 45-minute soak at ~5,000 ops/s leaves tens of
    /// millions of entries, which the previous 300 s bound cut off (soak i1, 2026-10-14). Default 3600.</summary>
    public int RestoreTimeoutSeconds { get; set; } = 3600;

    /// <summary>Fail the run when the leaf's chain does not contain every backup the run took on the
    /// node: an incremental the engine substituted with a full (its parent aged past the WAL floor)
    /// restarts the chain, and a run whose incrementals were silently fulls did not test incrementals.
    /// Default true.</summary>
    public bool RequireUnbrokenChain { get; set; } = true;

    public void Validate(ScenarioSpec scenario)
    {
        if (!scenario.Cluster.Backups)
            throw new ScenarioException("'backup_verify' needs 'cluster.backups: true'; the nodes have no backup directory otherwise");

        if (Node.Length > 0 && !scenario.Cluster.NodeNames.Contains(Node))
            throw new ScenarioException(
                $"'backup_verify.node' must name a node of this cluster ({string.Join(", ", scenario.Cluster.NodeNames)}), got '{Node}'");

        if (RestoreTimeoutSeconds < 1)
            throw new ScenarioException($"'backup_verify.restore_timeout_seconds' must be >= 1, got {RestoreTimeoutSeconds}");

        if (Boot && !Restore)
            throw new ScenarioException("'backup_verify.boot' needs 'backup_verify.restore: true'; there is nothing to boot from otherwise");

        if (Boot && !scenario.Teardown)
            throw new ScenarioException("'backup_verify.boot' needs 'teardown: true'; the rehearsal fleet reuses the live fleet's addresses");

        if (Boot && scenario.Cluster.ReplicationFactor != 0 && scenario.Cluster.ReplicationFactor < scenario.Cluster.Nodes)
            throw new ScenarioException(
                $"'backup_verify.boot' needs every node to host every range (replication_factor 0 or {scenario.Cluster.Nodes}), " +
                $"got {scenario.Cluster.ReplicationFactor}: one node's chain would seed a fleet missing the ranges it did not hold");
    }

    /// <summary>The node to verify: the explicit one, else the most-scheduled backup target, else camus1.</summary>
    public string EffectiveNode(ScenarioSpec scenario)
    {
        if (Node.Length > 0)
            return Node;

        string? scheduled = scenario.Nemesis?.Events
            .Where(e => e.Fault == "backup" && scenario.Cluster.NodeNames.Contains(e.Target))
            .GroupBy(e => e.Target)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .Select(g => g.Key)
            .FirstOrDefault();

        return scheduled ?? "camus1";
    }
}
