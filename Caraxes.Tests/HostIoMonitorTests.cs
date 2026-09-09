/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using Caraxes.Core.Cluster;

namespace Caraxes.Tests;

[TestFixture]
public class HostIoMonitorTests
{
    private const string Diskstats =
        " 259       0 nvme0n1 100 0 8000 50 200 0 16000 90 0 1000 140 0 0 0 0 0 0\n" +
        " 259       2 nvme0n1p2 1000 0 80000 500 2000 0 160000 900 0 10000 1400 0 0 0 0 0 0\n";

    [Test]
    public void ParsesTheNamedDeviceOnly()
    {
        DiskCounters? c = HostIoMonitor.ParseDiskstats(Diskstats.Split('\n'), "nvme0n1p2");

        Assert.That(c, Is.Not.Null);
        Assert.That(c!.Reads, Is.EqualTo(1000));
        Assert.That(c.ReadSectors, Is.EqualTo(80000));
        Assert.That(c.Writes, Is.EqualTo(2000));
        Assert.That(c.WriteSectors, Is.EqualTo(160000));
        Assert.That(c.IoTicksMs, Is.EqualTo(10000));
        Assert.That(HostIoMonitor.ParseDiskstats(Diskstats.Split('\n'), "sda"), Is.Null);
    }

    [Test]
    public void DiffTurnsCountersIntoRatesAndUtilisation()
    {
        DiskCounters a = new(1000, 80000, 2000, 160000, 10000);
        DiskCounters b = new(1500, 96000, 12000, 1184000, 12500);   // +500 reads, +16000 sectors, +10000 writes, +1024000 sectors, +2500 ms busy
        DateTime t0 = new(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc);

        HostIoSample s = HostIoMonitor.Diff(a, b, t0, t0.AddSeconds(5), fsyncMs: 0.8, load1: 12.5);

        Assert.That(s.ReadsPerSecond, Is.EqualTo(100).Within(0.01));
        Assert.That(s.ReadMbPerSecond, Is.EqualTo(16000 * 512.0 / 5 / 1e6).Within(1e-6));
        Assert.That(s.WritesPerSecond, Is.EqualTo(2000).Within(0.01));
        Assert.That(s.WriteMbPerSecond, Is.EqualTo(1024000 * 512.0 / 5 / 1e6).Within(1e-6));
        Assert.That(s.UtilPercent, Is.EqualTo(50).Within(0.01));
        Assert.That(s.FsyncMs, Is.EqualTo(0.8));
        Assert.That(s.Load1, Is.EqualTo(12.5));
    }

    [Test]
    public void UtilisationIsCappedAtOneHundredPercent()
    {
        DiskCounters a = new(0, 0, 0, 0, 0);
        DiskCounters b = new(0, 0, 0, 0, 60000);   // more busy-ms than the interval has (multi-queue devices report that)
        DateTime t0 = DateTime.UnixEpoch;

        Assert.That(HostIoMonitor.Diff(a, b, t0, t0.AddSeconds(5), 0, 0).UtilPercent, Is.EqualTo(100));
    }

    [Test]
    public void ResolvesTheLongestMatchingBlockDeviceMount()
    {
        string[] mounts =
        [
            "/dev/nvme0n1p2 / ext4 rw,relatime 0 0",
            "tmpfs /tmp tmpfs rw 0 0",
            "/dev/sdb1 /home/kahuna/runs ext4 rw 0 0",
            "overlay /var/lib/docker/overlay2/abc/merged overlay rw 0 0",
        ];

        Assert.That(HostIoMonitor.ResolveDevice("/home/kahuna/runs/scenarios/x", mounts), Is.EqualTo("sdb1"));
        Assert.That(HostIoMonitor.ResolveDevice("/home/kahuna/camusdb-caraxes/runs/x", mounts), Is.EqualTo("nvme0n1p2"));
        Assert.That(HostIoMonitor.ResolveDevice("/tmp/x", mounts), Is.Null, "tmpfs is not a block device");
        Assert.That(HostIoMonitor.ResolveDevice("/var/lib/docker/overlay2/abc/merged/y", mounts), Is.Null, "overlay is not a block device");
    }
}
