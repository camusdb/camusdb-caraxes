/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using Caraxes.Core.Cluster;

namespace Caraxes.Core.LeaderBalance;

/// <summary>One measurement of how partition leadership is spread across the cluster: how many
/// partitions each node leads, and how many partitions had a resolvable single leader at that
/// instant (leadership belief can lag during churn, so a snapshot may not resolve every partition).</summary>
public sealed record LeaderSnapshot(
    DateTime Utc,
    IReadOnlyDictionary<string, int> LeadersByNode,
    int ResolvedPartitions,
    int TotalPartitions)
{
    /// <summary>Spread of leadership across the nodes that are up: max leaders on one node minus the
    /// min. 0 is perfectly even; a large value means leadership is concentrated. Nodes that are down
    /// (no placement answer) are excluded so a killed node does not read as a permanent 0-min.</summary>
    public int Imbalance(IReadOnlyCollection<string> liveNodes)
    {
        List<int> counts = liveNodes.Select(n => LeadersByNode.TryGetValue(n, out int c) ? c : 0).ToList();
        return counts.Count == 0 ? 0 : counts.Max() - counts.Min();
    }

    public int LeadersOn(string node) => LeadersByNode.TryGetValue(node, out int c) ? c : 0;

    public string Format(IReadOnlyList<string> nodeOrder) =>
        string.Join("  ", nodeOrder.Select(n => $"{n}={LeadersOn(n)}")) +
        $"  (resolved {ResolvedPartitions}/{TotalPartitions})";
}

/// <summary>
/// Measures leader distribution by asking every node which partitions it leads. Each node reports
/// <c>LeaderLocal</c> for the partitions it believes it leads, so counting those per node gives the
/// whole distribution directly. A node that does not answer (it is down) simply contributes nothing.
/// </summary>
public static class LeaderObservation
{
    public static async Task<LeaderSnapshot> MeasureAsync(ClusterPlan plan, HttpProbes probes, CancellationToken cancellationToken = default)
    {
        Dictionary<string, int> leadersByNode = new();
        int resolved = 0;
        int total = 0;

        foreach (NodePlan node in plan.Nodes)
        {
            ClusterPlacement? placement = await probes
                .GetPlacementAsync($"http://localhost:{node.HostRestPort}", cancellationToken).ConfigureAwait(false);

            if (placement is null)
                continue; // node down or not answering — contributes no leaders

            // Every node returns the same committed partition set, so read the total once from any
            // answering node.
            total = Math.Max(total, placement.Partitions.Count);

            int led = placement.Partitions.Count(p => p.LeaderLocal);
            leadersByNode[node.Name] = led;
            resolved += led;
        }

        return new LeaderSnapshot(DateTime.UtcNow, leadersByNode, resolved, total);
    }

    /// <summary>Polls until every partition has exactly one resolvable leader (leadership settled) or
    /// the timeout elapses, returning the last snapshot either way.</summary>
    public static async Task<LeaderSnapshot> WaitForStableAsync(
        ClusterPlan plan, HttpProbes probes, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        LeaderSnapshot snapshot = await MeasureAsync(plan, probes, cancellationToken).ConfigureAwait(false);

        while (DateTime.UtcNow < deadline && (snapshot.TotalPartitions == 0 || snapshot.ResolvedPartitions != snapshot.TotalPartitions))
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            snapshot = await MeasureAsync(plan, probes, cancellationToken).ConfigureAwait(false);
        }

        return snapshot;
    }

    /// <summary>
    /// Polls until leadership is both resolved and <b>spread</b> across the nodes — no node leading
    /// more than one partition more than any other — or the timeout elapses. Returns the last
    /// snapshot either way, so the caller reports what it got rather than assuming it got what it
    /// waited for.
    ///
    /// <para>Resolution alone is not enough for a capacity run, and the difference is not
    /// theoretical: a measured run settled at <c>camus1=3 camus2=0 camus3=0</c> in under a second,
    /// passed every readiness check, and then spent its whole measured window with one node's Raft
    /// group and one node's disk carrying 100% of the durable work while three gateways split the
    /// requests evenly. Every partition had a leader. That is exactly the state this waits past.</para>
    ///
    /// <para>Waiting is worth it because the balancer <i>does</i> converge when the cluster is idle:
    /// measured from a fully concentrated start, it planned two moves and completed both in about
    /// twenty seconds with no load offered. Under load the same moves have been observed timing out,
    /// so the moment to spend this patience is here — after seeding, before the measured window
    /// opens — and not during a run where the balancer competes with the workload.</para>
    ///
    /// <para>An imbalance of 1 is accepted rather than 0 because partitions rarely divide evenly by
    /// nodes: four partitions on three nodes cannot do better than 2/1/1. Demanding 0 would wait out
    /// the whole budget on a cluster that is already as balanced as it can be.</para>
    /// </summary>
    public static async Task<LeaderSnapshot> WaitForSpreadAsync(
        ClusterPlan plan, HttpProbes probes, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        List<string> nodes = plan.Nodes.Select(n => n.Name).ToList();
        DateTime deadline = DateTime.UtcNow + timeout;
        LeaderSnapshot snapshot = await MeasureAsync(plan, probes, cancellationToken).ConfigureAwait(false);

        while (DateTime.UtcNow < deadline && !IsSpread(snapshot, nodes))
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            snapshot = await MeasureAsync(plan, probes, cancellationToken).ConfigureAwait(false);
        }

        return snapshot;
    }

    /// <summary>
    /// Whether a snapshot shows leadership both fully resolved and spread to within one partition
    /// across the cluster's nodes. A single-node cluster is trivially spread.
    /// </summary>
    public static bool IsSpread(LeaderSnapshot snapshot, IReadOnlyCollection<string> nodes)
    {
        if (snapshot.TotalPartitions == 0 || snapshot.ResolvedPartitions != snapshot.TotalPartitions)
            return false;

        return nodes.Count <= 1 || snapshot.Imbalance(nodes) <= 1;
    }
}
