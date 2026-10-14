/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using Caraxes.Core.Backup;
using Caraxes.Core.Cluster;

namespace Caraxes.Core.Nemesis;

/// <summary>
/// Takes a backup on the target node while the workload runs: the operator's cron job, scheduled
/// as a fault so the correlator grades what it cost. Not a failure of anything — a backup is an
/// online operation — but it competes with the foreground for the apply barrier, the flush, the
/// base-image copy and the WAL read, and the question a backup scenario asks is exactly how much.
///
/// <para>The window. The injection is the request itself, issued to returned; the heal is a no-op
/// that closes the window <c>duration</c> later, so the in-window throughput covers the backup and
/// its immediate aftermath and the recovery rules then judge the tail. Keep <c>duration</c> short
/// (a few seconds): a long hold dilutes the backup's own cost over seconds nothing happened in.</para>
///
/// <para>The chain. <c>backup_kind: incremental</c> chains on the newest backup in the target node's
/// own catalog, and starts the chain with a full when the catalog is empty — so a schedule is a list
/// of incrementals on one node, exactly as a cron job would issue them. A backup is node-local, so
/// a chain lives on the node that took it; schedule the incrementals on one node, or on several and
/// read each as its own chain. A substitution (an incremental the engine turned into a full because
/// its parent aged out) is written to the timeline in capitals: it is the finding a backup schedule
/// exists to catch.</para>
///
/// <para>A failed request (any non-ok envelope, or a node that does not answer) is a nemesis error:
/// the timeline records it and no window opens. The <c>backup_verify</c> stage counts those errors
/// and fails the run on them, so a backup that did not happen never reads as one that cost nothing.</para>
/// </summary>
public sealed class BackupFault : IFault
{
    public static readonly IReadOnlyList<string> Kinds = ["full", "incremental", "coordinated"];

    private readonly string backupKind;

    private string? lastOutcome;

    public BackupFault(string backupKind)
    {
        this.backupKind = backupKind;
    }

    public string Kind => "backup";

    public bool Healable => true;

    public string Describe(NodePlan target) => lastOutcome ?? $"{backupKind} backup on {target.Name}";

    public async Task InjectAsync(NodePlan target, ClusterPlan plan, CancellationToken cancellationToken)
    {
        if (!plan.Spec.Backups)
            throw new InvalidOperationException(
                $"cluster '{plan.Spec.Name}' was started without backups: true, so its nodes have no backup directory");

        BackupTaken taken = await BackupClient.TakeAsync(target, plan, backupKind, cancellationToken).ConfigureAwait(false);
        lastOutcome = taken.Describe();
    }

    /// <summary>Nothing to undo; the heal only closes the window.</summary>
    public Task HealAsync(NodePlan target, ClusterPlan plan, CancellationToken cancellationToken) => Task.CompletedTask;
}
