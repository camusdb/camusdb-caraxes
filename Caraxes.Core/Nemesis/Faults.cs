/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using Caraxes.Core.Cluster;

namespace Caraxes.Core.Nemesis;

/// <summary>SIGKILL the node's process; heal by restarting it. The hardest process fault — no
/// graceful shutdown, no chance to drain — so it exercises crash recovery and re-election. With
/// <c>heal: false</c> it becomes crash-then-repair: the node stays down for the cluster to
/// re-replicate around.</summary>
public sealed class KillFault : IFault
{
    public string Kind => "kill";

    public bool Healable => true;

    public string Describe(NodePlan target) => $"SIGKILL {target.ContainerName}";

    public Task InjectAsync(NodePlan target, ClusterPlan plan, CancellationToken cancellationToken)
        => ProcessRunner.RunCheckedAsync("docker", ["kill", "--signal", "KILL", target.ContainerName], cancellationToken: cancellationToken);

    public Task HealAsync(NodePlan target, ClusterPlan plan, CancellationToken cancellationToken)
        => ProcessRunner.RunCheckedAsync("docker", ["start", target.ContainerName], cancellationToken: cancellationToken);
}

/// <summary>Graceful stop (SIGTERM, then SIGKILL after docker's grace period); heal by restarting.
/// Unlike <see cref="KillFault"/> the node gets to shut down cleanly, so this models a planned
/// bounce rather than a crash.</summary>
public sealed class StopFault : IFault
{
    public string Kind => "stop";

    public bool Healable => true;

    public string Describe(NodePlan target) => $"graceful stop {target.ContainerName}";

    public Task InjectAsync(NodePlan target, ClusterPlan plan, CancellationToken cancellationToken)
        => ProcessRunner.RunCheckedAsync("docker", ["stop", target.ContainerName], cancellationToken: cancellationToken);

    public Task HealAsync(NodePlan target, ClusterPlan plan, CancellationToken cancellationToken)
        => ProcessRunner.RunCheckedAsync("docker", ["start", target.ContainerName], cancellationToken: cancellationToken);
}

/// <summary>Freeze the node's process with SIGSTOP (docker pause); heal with SIGCONT (unpause). The
/// node stops responding without its TCP connections dropping — a distinct failure mode from a kill:
/// peers see it as slow/unreachable while its sockets stay open, exercising suspicion timeouts
/// rather than connection resets.</summary>
public sealed class PauseFault : IFault
{
    public string Kind => "pause";

    public bool Healable => true;

    public string Describe(NodePlan target) => $"SIGSTOP (freeze) {target.ContainerName}";

    public Task InjectAsync(NodePlan target, ClusterPlan plan, CancellationToken cancellationToken)
        => ProcessRunner.RunCheckedAsync("docker", ["pause", target.ContainerName], cancellationToken: cancellationToken);

    public Task HealAsync(NodePlan target, ClusterPlan plan, CancellationToken cancellationToken)
        => ProcessRunner.RunCheckedAsync("docker", ["unpause", target.ContainerName], cancellationToken: cancellationToken);
}

/// <summary>
/// Network-isolate one node from every peer with iptables DROP rules on the peer IPs (both
/// directions), leaving its own services up but unreachable — the classic single-node partition.
/// Heal flushes the node's filter table; the containers are single-purpose, so a full flush is safe
/// and leaves no half-removed rules behind.
/// </summary>
public sealed class PartitionFault : IFault
{
    public string Kind => "partition";

    public bool Healable => true;

    public string Describe(NodePlan target) => $"isolate {target.ContainerName} from all peers (iptables DROP)";

    public async Task InjectAsync(NodePlan target, ClusterPlan plan, CancellationToken cancellationToken)
    {
        foreach (NodePlan peer in plan.Nodes.Where(n => n.Index != target.Index))
        {
            await Exec(target, ["iptables", "-A", "INPUT", "-s", peer.Ip, "-j", "DROP"], cancellationToken).ConfigureAwait(false);
            await Exec(target, ["iptables", "-A", "OUTPUT", "-d", peer.Ip, "-j", "DROP"], cancellationToken).ConfigureAwait(false);
        }
    }

    public Task HealAsync(NodePlan target, ClusterPlan plan, CancellationToken cancellationToken)
        => Exec(target, ["iptables", "-F"], cancellationToken);

    private static Task Exec(NodePlan target, IReadOnlyList<string> command, CancellationToken cancellationToken)
    {
        List<string> args = ["exec", target.ContainerName];
        args.AddRange(command);
        return ProcessRunner.RunCheckedAsync("docker", args, cancellationToken: cancellationToken);
    }
}

/// <summary>
/// Degrade a node's network with <c>tc qdisc netem</c> — added delay and/or packet loss on its
/// primary interface. Models a slow or lossy link (a straggler replica, a congested zone link)
/// rather than a clean cut. Heal removes the qdisc, restoring the interface to unshaped.
/// </summary>
public sealed class NetemFault : IFault
{
    private readonly int delayMs;

    private readonly double lossPercent;

    /// <param name="delayMs">Added one-way latency in milliseconds (0 = none).</param>
    /// <param name="lossPercent">Packet loss percentage (0 = none).</param>
    /// <param name="kind">Timeline kind label (<c>slow</c> or <c>loss</c>) so a scenario reads clearly.</param>
    public NetemFault(int delayMs, double lossPercent, string kind)
    {
        this.delayMs = delayMs;
        this.lossPercent = lossPercent;
        Kind = kind;
    }

    public string Kind { get; }

    public bool Healable => true;

    public string Describe(NodePlan target)
    {
        List<string> parts = [];
        if (delayMs > 0)
            parts.Add($"{delayMs}ms delay");
        if (lossPercent > 0)
            parts.Add($"{lossPercent:0.#}% loss");
        return $"netem on {target.ContainerName}: {(parts.Count == 0 ? "none" : string.Join(" + ", parts))}";
    }

    public Task InjectAsync(NodePlan target, ClusterPlan plan, CancellationToken cancellationToken)
    {
        List<string> netem = ["exec", target.ContainerName, "tc", "qdisc", "add", "dev", "eth0", "root", "netem"];
        if (delayMs > 0)
        {
            netem.Add("delay");
            netem.Add($"{delayMs}ms");
        }
        if (lossPercent > 0)
        {
            netem.Add("loss");
            netem.Add($"{lossPercent.ToString(System.Globalization.CultureInfo.InvariantCulture)}%");
        }
        return ProcessRunner.RunCheckedAsync("docker", netem, cancellationToken: cancellationToken);
    }

    public Task HealAsync(NodePlan target, ClusterPlan plan, CancellationToken cancellationToken)
        => ProcessRunner.RunCheckedAsync(
            "docker", ["exec", target.ContainerName, "tc", "qdisc", "del", "dev", "eth0", "root"],
            cancellationToken: cancellationToken);
}

/// <summary>
/// Exhausts a node's data disk by writing a large filler file into <c>/data</c> until the mount is
/// full, so the database's own writes fail with ENOSPC — a disk-pressure fault. Heal deletes the
/// filler, restoring free space. This is only meaningful when the data mount is size-capped
/// (<c>data_tmpfs_mb</c> on the cluster spec); against an uncapped named volume it would try to fill
/// the whole host disk, so the cluster spec must opt in. The filler write is expected to end in
/// ENOSPC — that is success, not failure — so its non-zero exit is ignored.
/// </summary>
public sealed class FillDiskFault : IFault
{
    private const string FillerPath = "/data/.caraxes-fill";

    public string Kind => "fill-disk";

    public bool Healable => true;

    public string Describe(NodePlan target) => $"exhaust /data on {target.ContainerName} (write filler until ENOSPC)";

    public Task InjectAsync(NodePlan target, ClusterPlan plan, CancellationToken cancellationToken)
        // A huge count that dd never reaches: it writes until the mount is full, then stops with ENOSPC.
        => ProcessRunner.RunCheckedAsync(
            "docker",
            ["exec", target.ContainerName, "sh", "-c", $"dd if=/dev/zero of={FillerPath} bs=1M count=1000000 2>/dev/null || true"],
            cancellationToken: cancellationToken);

    public Task HealAsync(NodePlan target, ClusterPlan plan, CancellationToken cancellationToken)
        => ProcessRunner.RunCheckedAsync(
            "docker", ["exec", target.ContainerName, "rm", "-f", FillerPath], cancellationToken: cancellationToken);
}

/// <summary>
/// Gracefully decommission a node: POST <c>/v1/cluster/leave</c> so its replicas drain onto
/// survivors and its removal commits, then stop the container. A one-way topology change — it is
/// never healed — that exercises the drain path and the placement rebalancer's trim, the reverse of
/// adding a node. The leave is issued over the node's plain-HTTP admin port on the host.
/// </summary>
public sealed class RemoveNodeFault : IFault
{
    private readonly HttpProbes probes;

    public RemoveNodeFault(HttpProbes probes) => this.probes = probes;

    public string Kind => "remove-node";

    public bool Healable => false;

    public string Describe(NodePlan target) => $"drain and decommission {target.ContainerName}";

    public async Task InjectAsync(NodePlan target, ClusterPlan plan, CancellationToken cancellationToken)
    {
        string baseUrl = $"http://localhost:{target.HostRestPort}";
        LeaveResult? result = await probes.LeaveAsync(baseUrl, cancellationToken).ConfigureAwait(false);

        if (result is null)
            throw new InvalidOperationException($"leave request to {target.Name} got no response");
        if (!result.Left)
            throw new InvalidOperationException(
                $"{target.Name} did not leave (outcome={result.Outcome}, retryable={result.Retryable}): {result.Reason}");

        await ProcessRunner.RunCheckedAsync("docker", ["stop", target.ContainerName], cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public Task HealAsync(NodePlan target, ClusterPlan plan, CancellationToken cancellationToken)
        => throw new NotSupportedException("remove-node is a one-way topology change and is never healed");
}
