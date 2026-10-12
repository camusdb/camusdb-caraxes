/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Diagnostics;
using Caraxes.Core.Cluster;

namespace Caraxes.Core.Nemesis;

/// <summary>
/// Resolves a scenario's <c>target</c> string to a concrete node. Supports an explicit node name
/// and <c>random</c> (drawn from a seeded RNG so a run reproduces). Leader-targeting is intentionally
/// absent: the cluster exposes no per-partition leader over HTTP, and with replicas on every node a
/// random pick hits a partition leader with near certainty — so leader targeting waits for a leader
/// probe rather than shipping a guess.
/// </summary>
public sealed class TargetSelector
{
    private readonly ClusterPlan plan;

    private readonly Random random;

    /// <param name="random">The nemesis's single seeded RNG stream, shared so target picks and the
    /// random schedule draw from one reproducible sequence.</param>
    public TargetSelector(ClusterPlan plan, Random random)
    {
        this.plan = plan;
        this.random = random;
    }

    /// <summary>The target that resolves, at inject time, to the node currently leading the most
    /// partitions. Unlike a node name it cannot be resolved when the schedule is built: leadership
    /// moves during a run (a paused leader steps down), and the whole point of targeting the leader is
    /// to hit whoever leads at that moment.</summary>
    public const string Leader = "leader";

    public static bool IsLeaderTarget(string target) => string.Equals(target, Leader, StringComparison.OrdinalIgnoreCase);

    /// <summary>Picks the leader from per-node counts of partitions each node reports leading: the
    /// node leading the most, ties to the lowest index. Throws when nobody leads anything (an
    /// election in progress), so the runner records an error event rather than guessing.</summary>
    public static NodePlan PickLeader(IReadOnlyList<(NodePlan Node, int Led)> observations)
    {
        return TryPickLeader(observations) ?? throw new NemesisException(
            "target 'leader': no node reports leading any partition right now (election in progress, or placement unreachable)");
    }

    /// <summary>How long the runner keeps asking before it gives up on a leader. An election after a
    /// WAL-stall step-down resolves in one to three seconds, and two or three can chain when the device
    /// is saturated; a single snapshot taken mid-election saw nobody leading and skipped the fault
    /// (rl7, 2026-10-11, one leader pause lost on each arm).</summary>
    public static readonly TimeSpan LeaderResolveWindow = TimeSpan.FromSeconds(10);

    /// <summary>The pause between two placement sweeps while nobody leads.</summary>
    public static readonly TimeSpan LeaderResolveInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>The outcome of a leader resolution: who leads, and how long the runner had to wait.</summary>
    public sealed record LeaderResolution(NodePlan Leader, int Attempts, TimeSpan Waited);

    /// <summary>
    /// Asks <paramref name="observe"/> for the per-node led counts and picks the leader, repeating
    /// every <paramref name="interval"/> while nobody leads anything, for at most
    /// <paramref name="window"/>. Throws when the window closes with still no leader, naming how many
    /// sweeps were made and how long they took, so the timeline says "no leader for 10 s" rather than
    /// "no leader right now". Cancellation propagates unchanged.
    /// </summary>
    public static async Task<LeaderResolution> ResolveLeaderAsync(
        Func<CancellationToken, Task<IReadOnlyList<(NodePlan Node, int Led)>>> observe,
        TimeSpan window, TimeSpan interval, CancellationToken cancellationToken)
    {
        Stopwatch clock = Stopwatch.StartNew();
        int attempts = 0;
        while (true)
        {
            attempts++;
            IReadOnlyList<(NodePlan Node, int Led)> observations = await observe(cancellationToken).ConfigureAwait(false);
            NodePlan? leader = TryPickLeader(observations);
            if (leader is not null)
                return new LeaderResolution(leader, attempts, clock.Elapsed);

            if (clock.Elapsed + interval > window)
                throw new NemesisException(
                    $"target 'leader': no node reported leading any partition in {attempts} sweep(s) over " +
                    $"{clock.Elapsed.TotalSeconds:F1} s (election not settling, or placement unreachable)");

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary><see cref="PickLeader"/> without the throw: null when nobody leads anything.</summary>
    private static NodePlan? TryPickLeader(IReadOnlyList<(NodePlan Node, int Led)> observations)
    {
        NodePlan? best = null;
        int bestLed = 0;
        foreach ((NodePlan node, int led) in observations.OrderBy(o => o.Node.Index))
        {
            if (led > bestLed)
            {
                best = node;
                bestLed = led;
            }
        }

        return best;
    }

    public NodePlan Resolve(string target)
    {
        if (IsLeaderTarget(target))
            throw new NemesisException("target 'leader' is resolved at inject time by the runner, not up front");

        if (string.Equals(target, "random", StringComparison.OrdinalIgnoreCase))
            return plan.Nodes[random.Next(plan.Nodes.Count)];

        NodePlan? node = plan.Nodes.FirstOrDefault(n => n.Name == target);
        if (node is null)
            throw new NemesisException(
                $"unknown target '{target}'; use a node name ({string.Join(", ", plan.Nodes.Select(n => n.Name))}), 'zone:<name>', 'leader', or 'random'");

        return node;
    }

    /// <summary>
    /// Resolves a target that may name a whole group. <c>zone:&lt;name&gt;</c> expands to every node
    /// in that failure zone (for a zone-failure test); a node name or <c>random</c> yields a group of
    /// one. The fault is then applied to each node in the group, so <c>kill</c> of a zone kills every
    /// node in it together.
    /// </summary>
    public IReadOnlyList<NodePlan> ResolveGroup(string target)
    {
        if (IsLeaderTarget(target))
            return [];

        const string zonePrefix = "zone:";
        if (target.StartsWith(zonePrefix, StringComparison.OrdinalIgnoreCase))
        {
            string zone = target[zonePrefix.Length..];
            List<NodePlan> nodes = plan.Nodes.Where(n => n.Zone == zone).ToList();
            if (nodes.Count == 0)
                throw new NemesisException(
                    $"no nodes in zone '{zone}'; configured zones: " +
                    $"{string.Join(", ", plan.Nodes.Select(n => n.Zone).Where(z => z is not null).Distinct())}");
            return nodes;
        }

        return [Resolve(target)];
    }
}
