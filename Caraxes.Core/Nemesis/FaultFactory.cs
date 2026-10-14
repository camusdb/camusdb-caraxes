/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using Caraxes.Core.Cluster;

namespace Caraxes.Core.Nemesis;

/// <summary>Builds an <see cref="IFault"/> from a scenario event's kind and parameters, and is the
/// single source of truth for which kinds exist (so validation and construction never drift).</summary>
public static class FaultFactory
{
    public static readonly IReadOnlyList<string> KnownKinds =
        ["kill", "stop", "pause", "partition", "slow", "loss", "fill-disk", "slow-disk", "remove-node", "leader-transfer", "clock-skew", "backup", "add-node"];

    public static void EnsureKnownKind(string kind)
    {
        if (!KnownKinds.Contains(kind))
            throw new NemesisException(
                $"unknown fault '{kind}'; known faults: {string.Join(", ", KnownKinds)}");
    }

    /// <param name="probes">Shared HTTP probes, needed by faults that read the cluster's placement
    /// (leader-transfer). Process and network faults ignore it.</param>
    public static IFault Create(NemesisEvent e, HttpProbes probes) => Create(e, new NemesisContext(probes));

    /// <param name="context">What a fault may need beyond its target: probes, the orchestrator and the
    /// registry of joined nodes (add-node). Process and network faults ignore it.</param>
    public static IFault Create(NemesisEvent e, NemesisContext context) => e.Fault switch
    {
        "kill" => new KillFault(),
        "stop" => new StopFault(),
        "pause" => new PauseFault(),
        "partition" => new PartitionFault(),
        "slow" => new NetemFault(e.DelayMs, lossPercent: 0, kind: "slow"),
        "loss" => new NetemFault(delayMs: 0, e.LossPercent, kind: "loss"),
        "fill-disk" => new FillDiskFault(),
        "slow-disk" => new SlowDiskFault(e.WriteBps, e.WriteIops, e.ReadBps, e.Device),
        "remove-node" => new RemoveNodeFault(),
        "leader-transfer" => new LeaderTransferFault(context.Probes, e.To),
        "clock-skew" => new ClockSkewFault(e.OffsetMs),
        "backup" => new BackupFault(e.BackupKind),
        "add-node" => new AddNodeFault(context, e.NodeName, e.SeedFromBackup),
        _ => throw new NemesisException($"unknown fault '{e.Fault}'"),
    };

    /// <summary>Whether a fault of this kind heals by default. A random schedule uses this to decide
    /// if it should schedule a heal; an explicit event can override via <see cref="NemesisEvent.Heal"/>.</summary>
    public static bool DefaultHealable(string kind) => kind != "remove-node";
}
