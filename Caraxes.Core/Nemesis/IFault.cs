/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using Caraxes.Core.Cluster;

namespace Caraxes.Core.Nemesis;

/// <summary>
/// One adversarial condition applied to a single node. A fault is a matched inject/heal pair unless
/// <see cref="Healable"/> is false (a permanent change like decommissioning a node or a deliberate
/// crash left for the cluster to repair). Faults are best-effort: the runner catches and logs their
/// errors rather than failing the whole scenario, because a docker hiccup mid-nemesis must not
/// invalidate the workload result the run exists to produce.
/// </summary>
public interface IFault
{
    /// <summary>Stable kind name, as written in the scenario and the timeline (e.g. <c>kill</c>).</summary>
    string Kind { get; }

    /// <summary>Whether <see cref="HealAsync"/> reverses this fault. False for one-way topology
    /// changes and deliberate crashes, which the runner injects and never heals.</summary>
    bool Healable { get; }

    /// <summary>Human-readable one-line description for the timeline detail field.</summary>
    string Describe(NodePlan target);

    Task InjectAsync(NodePlan target, ClusterPlan plan, CancellationToken cancellationToken);

    Task HealAsync(NodePlan target, ClusterPlan plan, CancellationToken cancellationToken);
}
