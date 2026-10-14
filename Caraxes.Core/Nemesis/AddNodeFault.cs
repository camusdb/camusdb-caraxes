/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Diagnostics;
using Caraxes.Core.Backup;
using Caraxes.Core.Cluster;

namespace Caraxes.Core.Nemesis;

/// <summary>
/// Replaces capacity at runtime: boots a new node and has it join the running cluster through
/// CamusDB's <c>--join-existing</c>, the operator's half of "a node died, put another one in". The
/// other half is the <c>kill</c> fault with <c>heal: false</c> (or <c>remove-node</c>) scheduled
/// before it; the cluster evicts a dead member on its own (SWIM: 5 s suspicion plus 30 s grace by
/// default), so a replacement scheduled a minute after the kill joins a roster the dead node has
/// already left.
///
/// <para>Seeding. With <c>seed_from_backup: true</c> (the default) the fault takes one closing
/// incremental on the target node, restores that chain on the target into its restore root, copies
/// the restored data root into the joiner's data directory, and boots the joiner from it: the join
/// then starts from the backup's point and the leader ships only the delta since. The target is the
/// node whose catalog holds the chain, usually the one the run's <c>backup</c> events name. With it
/// off the joiner starts empty and the leader seeds it by snapshot.</para>
///
/// <para>The joiner. <c>node_name</c> names it (<c>camusN</c> beyond the fleet; default the next free
/// index); it takes the address, Raft port and host ports that index has in the plan, which the
/// certificate's spare SANs cover. It runs the same image, config, environment and mounts as the
/// fleet (one <c>docker run</c> built from the same generator as the compose file), labelled as a
/// service of the compose project so teardown removes it. Its seeds are the fleet nodes that answer
/// <c>/ping</c> when the fault fires. The injection returns once the node reports ready, which
/// CamusDB does only after Kommander promoted it to Voter; the window then runs <c>duration</c>
/// longer and the heal is a no-op. The node is recorded on the <see cref="NemesisContext"/> so
/// <c>backup_verify</c> can read its ledger back after the workload.</para>
/// </summary>
public sealed class AddNodeFault : IFault
{
    /// <summary>How long a join may take: the restore and copy, the boot, the catch-up (a snapshot
    /// install or a backfill of the delta), and Kommander's promotion window.</summary>
    public static readonly TimeSpan JoinTimeout = TimeSpan.FromMinutes(15);

    private readonly NemesisContext context;

    private readonly string? nodeName;

    private readonly bool seedFromBackup;

    private string? lastOutcome;

    public AddNodeFault(NemesisContext context, string? nodeName, bool seedFromBackup)
    {
        this.context = context;
        this.nodeName = string.IsNullOrWhiteSpace(nodeName) ? null : nodeName.Trim();
        this.seedFromBackup = seedFromBackup;
    }

    public string Kind => "add-node";

    public bool Healable => true;

    public string Describe(NodePlan target) => lastOutcome ?? $"add a node seeded from {target.Name}";

    public async Task InjectAsync(NodePlan target, ClusterPlan plan, CancellationToken cancellationToken)
    {
        ClusterOrchestrator orchestrator = context.Orchestrator
            ?? throw new InvalidOperationException("add-node needs the cluster orchestrator; the nemesis was started without one");
        if (seedFromBackup && !plan.Spec.Backups)
            throw new InvalidOperationException($"cluster '{plan.Spec.Name}' was started without backups: true, so there is no chain to seed from");

        Stopwatch clock = Stopwatch.StartNew();
        NodePlan joiner = PickJoiner(plan);

        // Seeds: whoever answers now. A dead node in the list costs the joiner a connection attempt
        // at best; a list with nothing alive is a scenario error worth saying out loud.
        List<NodePlan> alive = [];
        foreach (NodePlan node in plan.Nodes)
            if (await context.Probes.PingAsync($"http://localhost:{node.HostRestPort}", cancellationToken).ConfigureAwait(false))
                alive.Add(node);
        foreach (JoinedNode earlier in context.JoinedNodes)
            if (await context.Probes.PingAsync($"http://localhost:{earlier.Node.HostRestPort}", cancellationToken).ConfigureAwait(false))
                alive.Add(earlier.Node);
        if (alive.Count == 0)
            throw new InvalidOperationException("no node answers /ping; there is no cluster for the new node to join");
        joiner.InitialCluster = string.Join(' ', alive.Select(ClusterPlan.RaftEndpoint));

        // The data root: a host directory the container binds at /data, seeded or empty.
        string dataDir = Path.Combine(orchestrator.BackupsRoot, "joiner", joiner.Name);
        if (Directory.Exists(dataDir))
            await orchestrator.RemoveContentsAsRootAsync(dataDir, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(dataDir);

        string? leaf = null;
        RestoreOutcome? restore = null;
        string seededFrom = "nothing (empty data root; the leader seeds it)";
        if (seedFromBackup)
        {
            BackupTaken closing = await BackupClient.TakeAsync(target, plan, "incremental", cancellationToken).ConfigureAwait(false);
            leaf = closing.Backup.BackupId;
            string targetDir = $"{NodeConfigGenerator.BackupRestoreRoot}/{leaf}";
            Stopwatch restoreClock = Stopwatch.StartNew();
            RestoreResponse response = await BackupClient.RestoreAsync(target, leaf, targetDir, context.RestoreTimeout, cancellationToken).ConfigureAwait(false);
            restore = new RestoreOutcome(
                response.DataRoot ?? targetDir, response.PartitionsRestored, response.EntriesApplied, response.LastAppliedPhysicalMs,
                response.MinRecoverablePhysicalMs, response.MaxRecoverablePhysicalMs, response.Chain.Count, restoreClock.Elapsed.TotalSeconds);
            await orchestrator.CopyTreeAsRootAsync(Path.Combine(orchestrator.BackupsDirOf(target), "restore", leaf), dataDir, cancellationToken)
                .ConfigureAwait(false);
            seededFrom = $"{target.Name}'s chain at {closing.Backup.ShortId} ({closing.Describe()}; restore of {response.Chain.Count} link(s) replayed {response.EntriesApplied:N0} entries in {restoreClock.Elapsed.TotalSeconds:0.0} s)";
        }

        await orchestrator.LaunchJoinerAsync(joiner, dataDir, cancellationToken).ConfigureAwait(false);
        context.LaunchedNodes.Add(joiner);

        // CamusDB reports ready while Kommander still has the node as a Learner (run replace-node r2,
        // 2026-10-14: ready at 115 s, roster said Learner), so readiness is only the first mark. The
        // join is done when the roster — read from the joiner and from a seed — says Voter.
        DateTime deadline = DateTime.UtcNow + JoinTimeout;
        NodeHealth health = await WaitForReadyAsync(joiner, deadline, cancellationToken).ConfigureAwait(false);
        double readySeconds = clock.Elapsed.TotalSeconds;
        (string role, ClusterMembership? membership) = await WaitForVoterAsync(joiner, alive[0], deadline, cancellationToken).ConfigureAwait(false);
        health = await context.Probes.GetHealthAsync($"http://localhost:{joiner.HostRestPort}", cancellationToken).ConfigureAwait(false) ?? health;

        JoinedNode joined = new(joiner, seededFrom, leaf, restore, alive.Select(n => n.Name).ToList(), clock.Elapsed.TotalSeconds, health.HostedPartitions);
        context.JoinedNodes.Add(joined);
        lastOutcome =
            $"{joiner.Name} ({joiner.Ip}) joined as {role} in {clock.Elapsed.TotalSeconds:0.0} s (ready at {readySeconds:0.0} s), hosting {health.HostedPartitions} partition(s), " +
            $"seeds {string.Join(",", joined.Seeds)}, roster of {membership?.Members.Count ?? 0} at version {membership?.MembershipVersion ?? 0} " +
            $"({string.Join(", ", membership?.Members.Select(m => $"{m.Endpoint}={m.Role}") ?? [])}); seeded from {seededFrom}";
    }

    /// <summary>Polls the roster until the joiner is a Voter in both its own view and a seed's.</summary>
    private async Task<(string Role, ClusterMembership? Membership)> WaitForVoterAsync(
        NodePlan joiner, NodePlan seed, DateTime deadline, CancellationToken cancellationToken)
    {
        string endpoint = ClusterPlan.RaftEndpoint(joiner);
        string lastRole = "absent";
        while (DateTime.UtcNow < deadline)
        {
            ClusterMembership? own = await context.Probes.GetMembershipAsync($"http://localhost:{joiner.HostRestPort}", cancellationToken).ConfigureAwait(false);
            ClusterMembership? seen = await context.Probes.GetMembershipAsync($"http://localhost:{seed.HostRestPort}", cancellationToken).ConfigureAwait(false);
            string ownRole = own?.Members.FirstOrDefault(m => m.Endpoint == endpoint)?.Role ?? own?.LocalRole ?? "absent";
            string seenRole = seen?.Members.FirstOrDefault(m => m.Endpoint == endpoint)?.Role ?? "absent";
            lastRole = $"{ownRole} (own view) / {seenRole} ({seed.Name}'s view)";
            if (string.Equals(ownRole, "Voter", StringComparison.OrdinalIgnoreCase) && string.Equals(seenRole, "Voter", StringComparison.OrdinalIgnoreCase))
                return ("Voter", seen ?? own);

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"{joiner.Name} was not promoted to Voter within {JoinTimeout.TotalMinutes:0} minutes; last roster role {lastRole}");
    }

    /// <summary>Nothing to undo; the heal only closes the window. Teardown removes the node.</summary>
    public Task HealAsync(NodePlan target, ClusterPlan plan, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>The joiner's plan: the named slot, or the first index after the fleet and the nodes
    /// already added. Validated against the certificate's spare SANs by <see cref="ClusterPlan.NodeAt"/>.</summary>
    private NodePlan PickJoiner(ClusterPlan plan)
    {
        int index;
        if (nodeName is null)
        {
            index = plan.Nodes.Count + context.JoinedNodes.Count + 1;
        }
        else
        {
            index = NemesisEvent.ParseNodeIndex(nodeName)
                ?? throw new NemesisException($"add-node: node_name must be camusN, got '{nodeName}'");
            if (plan.Nodes.Any(n => n.Index == index) || context.JoinedNodes.Any(j => j.Node.Index == index))
                throw new NemesisException($"add-node: {nodeName} is already in the cluster");
        }

        return ClusterPlan.NodeAt(plan.Spec, index);
    }

    private async Task<NodeHealth> WaitForReadyAsync(NodePlan joiner, DateTime deadline, CancellationToken cancellationToken)
    {
        string baseUrl = $"http://localhost:{joiner.HostRestPort}";
        while (DateTime.UtcNow < deadline)
        {
            NodeHealth? health = await context.Probes.GetHealthAsync(baseUrl, cancellationToken).ConfigureAwait(false);
            if (health is { Ready: true })
                return health;

            // A joiner that exited (a refused join fails CamusDB's startup loudly) must not be waited on.
            ProcessResult state = await ProcessRunner.RunAsync(
                "docker", ["inspect", "-f", "{{.State.Status}} {{.State.ExitCode}}", joiner.ContainerName], cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (state.Success && !state.StdOut.StartsWith("running", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"{joiner.Name} stopped before it became ready (container {state.StdOut.Trim()}); see its log in the run artifacts");

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"{joiner.Name} did not report ready within {JoinTimeout.TotalMinutes:0} minutes");
    }
}
