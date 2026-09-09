/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Globalization;

namespace Caraxes.Core.Cluster;

/// <summary>
/// One reading of the block device under the run: how busy the disk was over the sample interval
/// and what one small durable write cost on it.
/// </summary>
/// <param name="Utc">When the sample closed.</param>
/// <param name="ReadsPerSecond">Completed read requests per second.</param>
/// <param name="ReadMbPerSecond">Bytes read per second, in MB (10^6).</param>
/// <param name="WritesPerSecond">Completed write requests per second.</param>
/// <param name="WriteMbPerSecond">Bytes written per second, in MB (10^6).</param>
/// <param name="UtilPercent">Share of the interval the device had at least one request in flight.</param>
/// <param name="FsyncMs">Wall time of one 4 KiB append plus <c>fsync</c> on the run's filesystem.</param>
/// <param name="Load1">Host one-minute load average.</param>
public sealed record HostIoSample(
    DateTime Utc,
    double ReadsPerSecond,
    double ReadMbPerSecond,
    double WritesPerSecond,
    double WriteMbPerSecond,
    double UtilPercent,
    double FsyncMs,
    double Load1);

/// <summary>
/// Raw cumulative counters for one block device, as <c>/proc/diskstats</c> reports them.
/// </summary>
public sealed record DiskCounters(long Reads, long ReadSectors, long Writes, long WriteSectors, long IoTicksMs);

/// <summary>
/// Samples the <b>host's</b> block device and fsync cost for the whole measured window and writes
/// <c>host-io.csv</c> into the run directory.
///
/// <para>Why this exists. Four 45-minute soaks of two arms produced matched ratios of 1.78x and
/// 7.95x for the same pair (CamusDB feature 80af367a). In every one of them the write leader's
/// per-batch Raft latency stepped up 3-4x at some point — inside one 5-second interval, with nothing
/// in any node log — and stayed there. The step decides the run's throughput, because the per-partition
/// write pipeline runs one Raft batch at a time, and nothing the harness recorded could say whether
/// the disk, the filesystem journal, or the software had moved. The nodes' own metrics cannot see
/// the device: three containers and the load generator fsync into one filesystem, and only the host
/// sees their sum.</para>
///
/// <para>What it records, every few seconds: request and byte rates and utilisation from
/// <c>/proc/diskstats</c> for the device backing the run directory, one 4 KiB append-plus-fsync on
/// that filesystem (the cost every Raft WAL write on this host pays), and the load average. Linux
/// only; elsewhere it records nothing and says so once, rather than writing a file that looks like
/// a measurement.</para>
///
/// <para>The probe file is under the run directory, which on this harness lives on the same
/// filesystem as the docker volumes. If they differ the fsync column measures the wrong disk; the
/// device column names which one was watched so a reader can tell.</para>
/// </summary>
public sealed class HostIoMonitor
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    private readonly string runDir;
    private readonly string csvPath;
    private readonly TimeSpan interval;
    private readonly List<HostIoSample> samples = [];

    public HostIoMonitor(string runDir, TimeSpan? interval = null)
    {
        this.runDir = runDir;
        this.interval = interval ?? DefaultInterval;
        csvPath = Path.Combine(runDir, "host-io.csv");
    }

    /// <summary>Every sample taken so far, oldest first.</summary>
    public IReadOnlyList<HostIoSample> Samples => samples;

    /// <summary>The device watched, or null when none could be resolved (non-Linux, unknown mount).</summary>
    public string? Device { get; private set; }

    public async Task RunAsync(CancellationToken stopToken)
    {
        if (!OperatingSystem.IsLinux())
            return;

        Device = ResolveDevice(runDir);
        if (Device is null)
            return;

        string probePath = Path.Combine(runDir, ".host-io-probe");
        try
        {
            File.WriteAllText(csvPath, "ts,device,r_per_s,r_mb_s,w_per_s,w_mb_s,util_pct,fsync_ms,load1\n");
        }
        catch (IOException)
        {
            return;
        }

        DiskCounters? previous = ReadCounters(Device);
        DateTime previousAt = DateTime.UtcNow;

        using FileStream probe = new(probePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        byte[] block = new byte[4096];

        try
        {
            while (!stopToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, stopToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                DiskCounters? current = ReadCounters(Device);
                DateTime now = DateTime.UtcNow;
                double fsyncMs = ProbeFsync(probe, block);

                if (previous is not null && current is not null)
                {
                    HostIoSample sample = Diff(previous, current, previousAt, now, fsyncMs, ReadLoad1());
                    samples.Add(sample);
                    TryAppend(sample);
                }

                previous = current;
                previousAt = now;
            }
        }
        finally
        {
            try { File.Delete(probePath); } catch (IOException) { }
        }
    }

    /// <summary>Rates over the interval between two counter readings. Pure, so it can be tested without a disk.</summary>
    public static HostIoSample Diff(DiskCounters previous, DiskCounters current, DateTime from, DateTime to, double fsyncMs, double load1)
    {
        double seconds = Math.Max(0.001, (to - from).TotalSeconds);
        return new HostIoSample(
            to,
            (current.Reads - previous.Reads) / seconds,
            (current.ReadSectors - previous.ReadSectors) * 512.0 / seconds / 1e6,
            (current.Writes - previous.Writes) / seconds,
            (current.WriteSectors - previous.WriteSectors) * 512.0 / seconds / 1e6,
            Math.Min(100.0, (current.IoTicksMs - previous.IoTicksMs) / seconds / 10.0),
            fsyncMs,
            load1);
    }

    /// <summary>
    /// Parses one <c>/proc/diskstats</c> line for <paramref name="device"/>. Fields (1-based, after
    /// major/minor/name): 4 reads completed, 6 sectors read, 8 writes completed, 10 sectors written,
    /// 13 milliseconds spent doing I/O.
    /// </summary>
    public static DiskCounters? ParseDiskstats(IEnumerable<string> lines, string device)
    {
        foreach (string line in lines)
        {
            string[] f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 13 || f[2] != device)
                continue;

            if (long.TryParse(f[3], out long reads) && long.TryParse(f[5], out long readSectors)
                && long.TryParse(f[7], out long writes) && long.TryParse(f[9], out long writeSectors)
                && long.TryParse(f[12], out long ioTicks))
                return new DiskCounters(reads, readSectors, writes, writeSectors, ioTicks);
        }

        return null;
    }

    /// <summary>
    /// The block device (as <c>/proc/diskstats</c> names it) backing <paramref name="path"/>: the
    /// longest mount point in <c>/proc/mounts</c> that prefixes the path, with its <c>/dev/</c> stripped.
    /// Null for anything that is not a plain block device (overlay, tmpfs, network).
    /// </summary>
    public static string? ResolveDevice(string path, IEnumerable<string>? mounts = null)
    {
        string full = Path.GetFullPath(path).TrimEnd('/') + "/";
        string? bestMount = null;
        string? bestDevice = null;

        foreach (string line in mounts ?? SafeReadLines("/proc/mounts"))
        {
            string[] f = line.Split(' ');
            if (f.Length < 2)
                continue;

            string mount = f[1] == "/" ? "/" : f[1] + "/";
            if (!full.StartsWith(mount, StringComparison.Ordinal))
                continue;
            if (bestMount is not null && mount.Length <= bestMount.Length)
                continue;

            bestMount = mount;
            bestDevice = f[0].StartsWith("/dev/", StringComparison.Ordinal) ? f[0]["/dev/".Length..] : null;
        }

        return bestDevice;
    }

    private static DiskCounters? ReadCounters(string device) => ParseDiskstats(SafeReadLines("/proc/diskstats"), device);

    private static double ReadLoad1()
    {
        foreach (string line in SafeReadLines("/proc/loadavg"))
        {
            string[] f = line.Split(' ');
            if (f.Length > 0 && double.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double load))
                return load;
        }
        return 0;
    }

    private static double ProbeFsync(FileStream probe, byte[] block)
    {
        try
        {
            probe.Write(block, 0, block.Length);
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            probe.Flush(flushToDisk: true);
            return System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        catch (IOException)
        {
            return -1;
        }
    }

    private static IEnumerable<string> SafeReadLines(string path)
    {
        try
        {
            return File.ReadAllLines(path);
        }
        catch (Exception)
        {
            return [];
        }
    }

    private void TryAppend(HostIoSample s)
    {
        try
        {
            File.AppendAllText(csvPath, string.Create(CultureInfo.InvariantCulture,
                $"{s.Utc:o},{Device},{s.ReadsPerSecond:0},{s.ReadMbPerSecond:0.0},{s.WritesPerSecond:0},{s.WriteMbPerSecond:0.0},{s.UtilPercent:0},{s.FsyncMs:0.00},{s.Load1:0.00}\n"));
        }
        catch (IOException)
        {
            // A transient write failure loses one sample, never the run.
        }
    }
}
