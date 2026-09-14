/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using Caraxes.Core.Cluster;

namespace Caraxes.Core.Nemesis;

/// <summary>
/// Resolves, without root, the two facts a cgroup I/O throttle needs: the <c>MAJ:MIN</c> of the whole
/// block device under docker's data directory (where named volumes live), and the host path of a
/// container's cgroup. Both come from world-readable kernel files — <c>/proc/self/mountinfo</c>,
/// <c>/sys/dev/block</c>, <c>/proc/&lt;pid&gt;/cgroup</c> — because docker's own directories are
/// root-only and cannot be stat'ed by the harness user. The parsers are pure so they can be tested
/// against captured file contents.
/// </summary>
public static class HostBlockDevice
{
    /// <summary>Docker's data root, used as the path whose backing device is throttled. Named volumes
    /// live beneath it; <c>docker info</c> confirms it when reachable.</summary>
    public const string DefaultDockerRoot = "/var/lib/docker";

    /// <summary>
    /// The whole disk backing docker's named volumes, as <c>MAJ:MIN</c> and as a <c>/dev</c> path.
    /// cgroup v2 <c>io.max</c> refuses a partition number, so a partition is walked up to its disk
    /// through sysfs.
    /// </summary>
    public static async Task<BlockDisk> ResolveDockerVolumeDiskAsync(CancellationToken cancellationToken)
    {
        string majorMinor = await ResolveDockerVolumeDeviceAsync(cancellationToken).ConfigureAwait(false);
        return new BlockDisk(majorMinor, DiskPathOf(majorMinor));
    }

    /// <summary>The whole-disk <c>MAJ:MIN</c> backing docker's named volumes.</summary>
    public static async Task<string> ResolveDockerVolumeDeviceAsync(CancellationToken cancellationToken)
    {
        string dockerRoot = DefaultDockerRoot;
        ProcessResult info = await ProcessRunner.RunAsync(
            "docker", ["info", "-f", "{{.DockerRootDir}}"], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (info.Success && !string.IsNullOrWhiteSpace(info.StdOut))
            dockerRoot = info.StdOut.Trim();

        string mountinfo = await File.ReadAllTextAsync("/proc/self/mountinfo", cancellationToken).ConfigureAwait(false);
        MountEntry mount = FindMount(mountinfo, dockerRoot)
            ?? throw new NemesisException($"no mount in /proc/self/mountinfo covers {dockerRoot}");

        string device = mount.MajorMinor;
        if (device.StartsWith("0:", StringComparison.Ordinal))
        {
            // Anonymous device number (btrfs, overlay, tmpfs): fall back to the source path's block name.
            string name = Path.GetFileName(mount.Source);
            string devFile = $"/sys/class/block/{name}/dev";
            if (!File.Exists(devFile))
                throw new NemesisException(
                    $"{dockerRoot} is on {mount.FsType} ({mount.Source}) whose block device cannot be resolved; " +
                    "set 'device: MAJ:MIN' on the slow-disk event");
            device = (await File.ReadAllTextAsync(devFile, cancellationToken).ConfigureAwait(false)).Trim();
        }

        return WholeDiskOf(device);
    }

    /// <summary>The mount whose target is the longest prefix of <paramref name="path"/> — the one that
    /// actually serves it.</summary>
    public static MountEntry? FindMount(string mountinfo, string path)
    {
        string wanted = path.TrimEnd('/');
        if (wanted.Length == 0)
            wanted = "/";

        MountEntry? best = null;
        foreach (string raw in mountinfo.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            MountEntry? entry = ParseMountLine(raw);
            if (entry is null)
                continue;

            string target = entry.MountPoint.TrimEnd('/');
            bool covers = target.Length == 0
                          || wanted == target
                          || wanted.StartsWith(target + "/", StringComparison.Ordinal);
            if (!covers)
                continue;

            if (best is null || entry.MountPoint.Length > best.MountPoint.Length)
                best = entry;
        }

        return best;
    }

    /// <summary>One <c>/proc/self/mountinfo</c> line: <c>id parent MAJ:MIN root mount-point options
    /// [optional…] - fstype source super-options</c>. Mount points with spaces are octal-escaped by
    /// the kernel, so a whitespace split is exact.</summary>
    public static MountEntry? ParseMountLine(string line)
    {
        string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int separator = Array.IndexOf(fields, "-");
        if (fields.Length < 7 || separator < 6 || separator + 2 >= fields.Length)
            return null;

        return new MountEntry(
            MajorMinor: fields[2],
            MountPoint: Unescape(fields[4]),
            FsType: fields[separator + 1],
            Source: fields[separator + 2]);
    }

    /// <summary>Walks a partition's <c>MAJ:MIN</c> up to its disk via <c>/sys/dev/block</c>; a disk
    /// number is returned unchanged. Throws when the number is unknown to the kernel.</summary>
    public static string WholeDiskOf(string majorMinor)
    {
        string node = $"/sys/dev/block/{majorMinor}";
        if (!Directory.Exists(node))
            throw new NemesisException($"block device {majorMinor} does not exist under /sys/dev/block");

        if (!File.Exists(Path.Combine(node, "partition")))
            return majorMinor;

        // /sys/dev/block/MAJ:MIN is a symlink into /sys/devices/…/<disk>/<partition>; ".." must be
        // taken from the link's target, not collapsed lexically against /sys/dev/block.
        string real = Directory.ResolveLinkTarget(node, returnFinalTarget: true)?.FullName ?? node;
        string parentDev = Path.Combine(Path.GetDirectoryName(real) ?? real, "dev");
        if (!File.Exists(parentDev))
            throw new NemesisException($"{majorMinor} is a partition but its parent disk ({real}) has no dev file");

        return File.ReadAllText(parentDev).Trim();
    }

    /// <summary>The <c>/dev</c> path of a whole disk given its <c>MAJ:MIN</c>: the kernel names the
    /// sysfs directory after the device node (<c>/sys/devices/…/nvme0n1</c> → <c>/dev/nvme0n1</c>).</summary>
    public static string DiskPathOf(string majorMinor)
    {
        string node = $"/sys/dev/block/{majorMinor}";
        if (!Directory.Exists(node))
            throw new NemesisException($"block device {majorMinor} does not exist under /sys/dev/block");

        string real = Directory.ResolveLinkTarget(node, returnFinalTarget: true)?.FullName ?? node;
        string name = Path.GetFileName(real.TrimEnd('/'));
        string path = $"/dev/{name}";
        if (!File.Exists(path))
            throw new NemesisException($"{majorMinor} resolves to {path}, which does not exist");
        return path;
    }

    /// <summary>
    /// Host path of the container's cgroup v2 directory, read from its init process's
    /// <c>/proc/&lt;pid&gt;/cgroup</c> — the only source that is right whichever cgroup driver docker
    /// runs with (systemd: <c>system.slice/docker-&lt;id&gt;.scope</c>; cgroupfs: <c>docker/&lt;id&gt;</c>).
    /// </summary>
    public static async Task<string> ResolveContainerCgroupAsync(string containerName, CancellationToken cancellationToken)
    {
        ProcessResult pid = await ProcessRunner.RunAsync(
            "docker", ["inspect", "-f", "{{.State.Pid}}", containerName], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!pid.Success || !int.TryParse(pid.StdOut.Trim(), out int init) || init <= 0)
            throw new NemesisException($"{containerName} has no running init process (is it up?): {pid.StdErr.Trim()}");

        string cgroup = await File.ReadAllTextAsync($"/proc/{init}/cgroup", cancellationToken).ConfigureAwait(false);
        return "/sys/fs/cgroup" + ParseUnifiedCgroup(cgroup);
    }

    /// <summary>The cgroup v2 path (<c>0::/…</c> line) from a <c>/proc/&lt;pid&gt;/cgroup</c> file.</summary>
    public static string ParseUnifiedCgroup(string procCgroup)
    {
        foreach (string line in procCgroup.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("0::", StringComparison.Ordinal))
            {
                string path = line[3..].Trim();
                if (path.Length == 0 || path == "/")
                    throw new NemesisException("the process is in the root cgroup, which cannot be throttled");
                return path;
            }
        }

        throw new NemesisException("no cgroup v2 (0::) entry found; slow-disk needs a cgroup v2 host");
    }

    private static string Unescape(string field)
        => field.Replace("\\040", " ").Replace("\\011", "\t").Replace("\\012", "\n").Replace("\\134", "\\");
}

/// <summary>A parsed <c>/proc/self/mountinfo</c> entry.</summary>
public sealed record MountEntry(string MajorMinor, string MountPoint, string FsType, string Source);
