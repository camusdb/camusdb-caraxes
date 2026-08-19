/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

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

    public NodePlan Resolve(string target)
    {
        if (string.Equals(target, "random", StringComparison.OrdinalIgnoreCase))
            return plan.Nodes[random.Next(plan.Nodes.Count)];

        NodePlan? node = plan.Nodes.FirstOrDefault(n => n.Name == target);
        if (node is null)
            throw new NemesisException(
                $"unknown target '{target}'; use a node name ({string.Join(", ", plan.Nodes.Select(n => n.Name))}), 'zone:<name>', or 'random'");

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
