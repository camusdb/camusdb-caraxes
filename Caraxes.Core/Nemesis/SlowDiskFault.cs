/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Globalization;
using System.Text.Json;
using Caraxes.Core.Cluster;

namespace Caraxes.Core.Nemesis;

/// <summary>
/// Slows or pauses one node's durable writes by capping its container's block I/O with cgroup v2
/// <c>io.max</c> on the disk that backs docker's named volumes. Every write the node's process
/// issues — RocksDB WAL appends, SST flushes, the fsyncs behind them — is throttled at the block
/// layer, so a <c>fsync</c> takes <i>bytes ÷ write_bps</i> seconds and a cap of a few KiB/s is a
/// device pause: the process is alive, its network is fine, its peers are healthy, and only its
/// storage has stopped answering. That is the shape a worn SSD produces naturally (Vorpal
/// <c>b83c72db</c>: fsync excursions of hundreds of ms to seconds while the rest of the cluster is
/// idle), reproduced on demand and on a single node.
/// <para>
/// The cap is applied and lifted through the Docker Engine's container-update API (the per-device
/// throttle resources the CLI does not expose), which the runtime turns into the container's own
/// <c>io.max</c> line in place. Nothing is created or removed. That is not a convenience but a
/// requirement, learned from run <c>sd1</c> (2026-09-14): removing <i>any</i> container unmounts its
/// overlay, which syncs the whole upper filesystem and therefore waits behind the throttled node's
/// writeback; that stuck removal holds the daemon's layer lock, every later create hangs, and the
/// heal that would have released it all can no longer run. A privileged helper container written
/// through <c>docker run --rm</c> wedged the host for eighteen minutes that way. <c>docker exec</c>,
/// <c>inspect</c>, <c>kill</c> and the update API stay usable under a throttle; create and remove do not.
/// </para>
/// <para>
/// Every write is verified by reading the cgroup file back from the host. Needs a volume-backed data
/// mount: on a <c>data_tmpfs_mb</c> rig the data never reaches a block device and the throttle would be
/// a no-op, so the fault refuses to inject there rather than report a pause that never happened.
/// </para>
/// </summary>
public sealed class SlowDiskFault : IFault
{
    /// <summary>The rate that lifts a cap. <c>io.max</c> accepts any value above 1 and docker's
    /// resource document has no "unlimited", so heal sets every capped key to a rate no device can
    /// reach; the kernel clamps the IOPS keys to <c>UINT_MAX</c> itself.</summary>
    public const long LiftRate = 1L << 40;

    private readonly long writeBps;

    private readonly long writeIops;

    private readonly long readBps;

    private readonly string? explicitDevice;

    // Resolved once per process: the disk under docker's volumes does not change between events.
    private static readonly SemaphoreSlim DiskLock = new(1, 1);

    private static BlockDisk? resolvedDisk;

    /// <param name="writeBps">Write cap in bytes per second (0 = unlimited).</param>
    /// <param name="writeIops">Write cap in I/O operations per second (0 = unlimited).</param>
    /// <param name="readBps">Read cap in bytes per second (0 = unlimited).</param>
    /// <param name="device">Explicit <c>MAJ:MIN</c> of the whole disk; null derives it from docker's data root.</param>
    public SlowDiskFault(long writeBps, long writeIops, long readBps, string? device)
    {
        this.writeBps = writeBps;
        this.writeIops = writeIops;
        this.readBps = readBps;
        explicitDevice = string.IsNullOrWhiteSpace(device) ? null : device.Trim();
    }

    public string Kind => "slow-disk";

    public bool Healable => true;

    public string Describe(NodePlan target)
    {
        List<string> parts = [];
        if (writeBps > 0)
            parts.Add($"writes {FormatRate(writeBps)}");
        if (writeIops > 0)
            parts.Add($"writes {writeIops} IO/s");
        if (readBps > 0)
            parts.Add($"reads {FormatRate(readBps)}");
        string device = explicitDevice ?? resolvedDisk?.MajorMinor ?? "docker volume disk";
        return $"throttle /data on {target.ContainerName}: {string.Join(", ", parts)} (cgroup io.max on {device})";
    }

    /// <summary>The docker update document that installs this fault's caps on <paramref name="diskPath"/>.
    /// Only the requested caps are present; the others are left as they are.</summary>
    public string InjectResources(string diskPath) => Resources(diskPath, writeBps, writeIops, readBps);

    /// <summary>The docker update document that lifts every cap this fault installed.</summary>
    public string LiftResources(string diskPath)
        => Resources(diskPath, writeBps > 0 ? LiftRate : 0, writeIops > 0 ? LiftRate : 0, readBps > 0 ? LiftRate : 0);

    /// <summary>Whether a read-back <c>io.max</c> shows the device throttled as requested. The kernel
    /// prints every key on the device's line, so a lifted cap reads <c>max</c> or a huge number.</summary>
    public bool IsApplied(string ioMax, string majorMinor)
    {
        Dictionary<string, string>? values = LimitsOf(ioMax, majorMinor);
        if (values is null)
            return false;

        bool ok = true;
        if (writeBps > 0)
            ok &= values.TryGetValue("wbps", out string? w) && w == Invariant(writeBps);
        if (writeIops > 0)
            ok &= values.TryGetValue("wiops", out string? wi) && wi == Invariant(writeIops);
        if (readBps > 0)
            ok &= values.TryGetValue("rbps", out string? r) && r == Invariant(readBps);
        return ok;
    }

    /// <summary>Whether a read-back <c>io.max</c> shows none of this fault's caps in force: no line for
    /// the device, or each capped key at <c>max</c> or at least <see cref="LiftRate"/> (the IOPS keys are
    /// clamped by the kernel, so anything at or above <c>UINT_MAX</c> counts as lifted there).</summary>
    public bool IsLifted(string ioMax, string majorMinor)
    {
        Dictionary<string, string>? values = LimitsOf(ioMax, majorMinor);
        if (values is null)
            return true;

        bool lifted = true;
        if (writeBps > 0)
            lifted &= Unlimited(values, "wbps", LiftRate);
        if (writeIops > 0)
            lifted &= Unlimited(values, "wiops", uint.MaxValue);
        if (readBps > 0)
            lifted &= Unlimited(values, "rbps", LiftRate);
        return lifted;
    }

    public async Task InjectAsync(NodePlan target, ClusterPlan plan, CancellationToken cancellationToken)
    {
        if (plan.Spec.DataTmpfsMb > 0)
            throw new NemesisException(
                "slow-disk needs a volume-backed data mount: with data_tmpfs_mb the node's writes never reach a " +
                "block device, so a cgroup io.max cap would throttle nothing");

        BlockDisk disk = await ResolveDiskAsync(cancellationToken).ConfigureAwait(false);
        string cgroup = await HostBlockDevice.ResolveContainerCgroupAsync(target.ContainerName, cancellationToken).ConfigureAwait(false);

        await UpdateAsync(target, InjectResources(disk.Path), cancellationToken).ConfigureAwait(false);

        string applied = await File.ReadAllTextAsync(Path.Combine(cgroup, "io.max"), cancellationToken).ConfigureAwait(false);
        if (!IsApplied(applied, disk.MajorMinor))
            throw new NemesisException(
                $"io.max of {target.ContainerName} does not show the requested cap after the update; " +
                $"file reads: '{applied.Trim()}'");
    }

    public async Task HealAsync(NodePlan target, ClusterPlan plan, CancellationToken cancellationToken)
    {
        BlockDisk disk = await ResolveDiskAsync(cancellationToken).ConfigureAwait(false);
        string cgroup = await HostBlockDevice.ResolveContainerCgroupAsync(target.ContainerName, cancellationToken).ConfigureAwait(false);

        await UpdateAsync(target, LiftResources(disk.Path), cancellationToken).ConfigureAwait(false);

        string lifted = await File.ReadAllTextAsync(Path.Combine(cgroup, "io.max"), cancellationToken).ConfigureAwait(false);
        if (!IsLifted(lifted, disk.MajorMinor))
            throw new NemesisException(
                $"io.max of {target.ContainerName} still shows a cap after the lift; file reads: '{lifted.Trim()}'");
    }

    private static async Task UpdateAsync(NodePlan target, string resources, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> warnings = await DockerEngineApi
            .UpdateContainerAsync(target.ContainerName, resources, cancellationToken).ConfigureAwait(false);

        // Docker downgrades an unsupported throttle to a warning and drops it; that is a failed inject,
        // not a note, because the run would otherwise report a pause that never happened.
        string? unsupported = warnings.FirstOrDefault(w => w.Contains("not support", StringComparison.OrdinalIgnoreCase));
        if (unsupported is not null)
            throw new NemesisException($"docker refused the block I/O cap on {target.ContainerName}: {unsupported}");
    }

    private async Task<BlockDisk> ResolveDiskAsync(CancellationToken cancellationToken)
    {
        if (explicitDevice is not null)
            return new BlockDisk(explicitDevice, HostBlockDevice.DiskPathOf(explicitDevice));

        await DiskLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            resolvedDisk ??= await HostBlockDevice.ResolveDockerVolumeDiskAsync(cancellationToken).ConfigureAwait(false);
            return resolvedDisk;
        }
        finally
        {
            DiskLock.Release();
        }
    }

    private static string Resources(string diskPath, long wbps, long wiops, long rbps)
    {
        Dictionary<string, object> doc = [];
        if (wbps > 0)
            doc["BlkioDeviceWriteBps"] = new[] { new { Path = diskPath, Rate = wbps } };
        if (wiops > 0)
            doc["BlkioDeviceWriteIOps"] = new[] { new { Path = diskPath, Rate = wiops } };
        if (rbps > 0)
            doc["BlkioDeviceReadBps"] = new[] { new { Path = diskPath, Rate = rbps } };
        return JsonSerializer.Serialize(doc);
    }

    /// <summary>The key=value limits on the device's <c>io.max</c> line, or null when it has none.</summary>
    private static Dictionary<string, string>? LimitsOf(string ioMax, string majorMinor)
    {
        foreach (string line in ioMax.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0 || fields[0] != majorMinor)
                continue;

            return fields.Skip(1).Select(f => f.Split('=', 2)).Where(kv => kv.Length == 2)
                .ToDictionary(kv => kv[0], kv => kv[1], StringComparer.Ordinal);
        }

        return null;
    }

    private static bool Unlimited(Dictionary<string, string> values, string key, long atLeast)
    {
        if (!values.TryGetValue(key, out string? value) || value == "max")
            return true;
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long rate) && rate >= atLeast;
    }

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string FormatRate(long bytesPerSecond)
    {
        if (bytesPerSecond >= 1024 * 1024)
            return $"{bytesPerSecond / (1024.0 * 1024.0):0.#} MiB/s";
        if (bytesPerSecond >= 1024)
            return $"{bytesPerSecond / 1024.0:0.#} KiB/s";
        return $"{bytesPerSecond} B/s";
    }
}

/// <summary>A whole disk as the kernel numbers it and as docker addresses it.</summary>
public sealed record BlockDisk(string MajorMinor, string Path);
