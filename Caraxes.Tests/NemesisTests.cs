/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using NUnit.Framework;
using Caraxes.Core.Cluster;
using Caraxes.Core.Nemesis;
using Caraxes.Core.Scenario;

namespace Caraxes.Tests;

[TestFixture]
public sealed class DurationParserTests
{
    [TestCase("15s", 15)]
    [TestCase("1m", 60)]
    [TestCase("250ms", 0.25)]
    [TestCase("1h", 3600)]
    [TestCase("30", 30)]
    public void ParsesForms(string input, double expectedSeconds)
    {
        Assert.That(DurationParser.Parse(input).TotalSeconds, Is.EqualTo(expectedSeconds).Within(1e-6));
    }

    [TestCase("")]
    [TestCase("soon")]
    [TestCase("10x")]
    public void RejectsGarbage(string input)
    {
        Assert.Throws<FormatException>(() => DurationParser.Parse(input));
    }
}

[TestFixture]
public sealed class NemesisSpecTests
{
    private static NemesisSpec Read(string nemesisBlock)
    {
        ScenarioSpec spec = ScenarioSpecReader.Read($"""
            name: s
            cluster:
              name: c
            {nemesisBlock}
            """);
        return spec.Nemesis!;
    }

    [Test]
    public void ParsesEventTimeline()
    {
        NemesisSpec n = Read("""
            nemesis:
              seed: 7
              events:
                - at: 20s
                  fault: kill
                  target: random
                  duration: 15s
            """);

        Assert.That(n.Seed, Is.EqualTo(7));
        Assert.That(n.Events, Has.Count.EqualTo(1));
        Assert.That(n.Events[0].Fault, Is.EqualTo("kill"));
        Assert.That(n.Events[0].At, Is.EqualTo("20s"));
    }

    [Test]
    public void ParsesRandomSchedule()
    {
        NemesisSpec n = Read("""
            nemesis:
              seed: 42
              random:
                faults: [kill, pause]
                min_interval: 10s
                max_interval: 18s
                duration: 12s
            """);

        Assert.That(n.Random, Is.Not.Null);
        Assert.That(n.Random!.Faults, Is.EquivalentTo(new[] { "kill", "pause" }));
    }

    [Test]
    public void UnknownFault_IsRejected()
    {
        NemesisException ex = Assert.Throws<NemesisException>(() => Read("""
            nemesis:
              events:
                - fault: nuke
                  at: 1s
            """))!;
        Assert.That(ex.Message, Does.Contain("nuke"));
    }

    [Test]
    public void EventsAndRandomTogether_IsRejected()
    {
        Assert.Throws<NemesisException>(() => Read("""
            nemesis:
              events:
                - fault: kill
                  at: 1s
              random:
                faults: [kill]
            """));
    }

    [Test]
    public void UnknownNemesisKey_IsRejected()
    {
        ScenarioException ex = Assert.Throws<ScenarioException>(() => ScenarioSpecReader.Read("""
            name: s
            cluster:
              name: c
            nemesis:
              speed: 7
            """))!;
        Assert.That(ex.Message, Does.Contain("nemesis.speed"));
    }

    [Test]
    public void RandomIntervalOrder_IsChecked()
    {
        Assert.Throws<NemesisException>(() => Read("""
            nemesis:
              random:
                faults: [kill]
                min_interval: 20s
                max_interval: 10s
            """));
    }
}

[TestFixture]
public sealed class TargetSelectorTests
{
    private static ClusterPlan Plan() => ClusterPlan.FromSpec(ClusterSpecReader.Read("name: t\nnodes: 3"));

    [Test]
    public void ResolvesExplicitNode()
    {
        TargetSelector selector = new(Plan(), new Random(1));
        Assert.That(selector.Resolve("camus2").Name, Is.EqualTo("camus2"));
    }

    [Test]
    public void RandomIsSeededAndDeterministic()
    {
        var a = new TargetSelector(Plan(), new Random(99));
        var b = new TargetSelector(Plan(), new Random(99));

        for (int i = 0; i < 10; i++)
            Assert.That(a.Resolve("random").Name, Is.EqualTo(b.Resolve("random").Name));
    }

    [Test]
    public void UnknownTarget_IsRejected()
    {
        TargetSelector selector = new(Plan(), new Random(1));
        NemesisException ex = Assert.Throws<NemesisException>(() => selector.Resolve("camus9"))!;
        Assert.That(ex.Message, Does.Contain("camus9"));
    }

    private static ClusterPlan ZonedPlan() => ClusterPlan.FromSpec(
        ClusterSpecReader.Read("name: z\nnodes: 6\nzones: [za, za, zb, zb, zc, zc]"));

    [Test]
    public void ZoneTargetResolvesEveryNodeInTheZone()
    {
        TargetSelector selector = new(ZonedPlan(), new Random(1));
        var group = selector.ResolveGroup("zone:zb");

        Assert.That(group.Select(n => n.Name), Is.EquivalentTo(new[] { "camus3", "camus4" }));
    }

    [Test]
    public void NodeAndRandomResolveToGroupOfOne()
    {
        TargetSelector selector = new(ZonedPlan(), new Random(1));
        Assert.That(selector.ResolveGroup("camus5"), Has.Count.EqualTo(1));
        Assert.That(selector.ResolveGroup("random"), Has.Count.EqualTo(1));
    }

    [Test]
    public void EmptyZone_IsRejected()
    {
        TargetSelector selector = new(ZonedPlan(), new Random(1));
        NemesisException ex = Assert.Throws<NemesisException>(() => selector.ResolveGroup("zone:zx"))!;
        Assert.That(ex.Message, Does.Contain("zx"));
    }
}

[TestFixture]
public sealed class FaultFactoryTests
{
    [Test]
    public void BuildsEachKnownProcessAndNetworkFault()
    {
        using HttpProbes probes = new();
        foreach (string kind in new[] { "kill", "stop", "pause", "partition", "slow", "loss", "fill-disk", "slow-disk", "remove-node" })
        {
            IFault fault = FaultFactory.Create(new NemesisEvent { Fault = kind }, probes);
            Assert.That(fault.Kind, Is.EqualTo(kind));
        }
    }

    [Test]
    public void RemoveNodeIsNotHealable()
    {
        using HttpProbes probes = new();
        Assert.That(FaultFactory.Create(new NemesisEvent { Fault = "remove-node" }, probes).Healable, Is.False);
        Assert.That(FaultFactory.Create(new NemesisEvent { Fault = "kill" }, probes).Healable, Is.True);
    }
}

[TestFixture]
public sealed class SlowDiskFaultTests
{
    private static NemesisSpec Read(string nemesisBlock)
    {
        ScenarioSpec spec = ScenarioSpecReader.Read($"""
            name: s
            cluster:
              name: c
            {nemesisBlock}
            """);
        return spec.Nemesis!;
    }

    [Test]
    public void ParsesSlowDiskEventWithCaps()
    {
        NemesisSpec n = Read("""
            nemesis:
              events:
                - { at: 60s, fault: slow-disk, target: camus2, duration: 30s, write_bps: 4096, read_bps: 65536, device: "259:0" }
            """);

        NemesisEvent e = n.Events[0];
        Assert.That(e.Fault, Is.EqualTo("slow-disk"));
        Assert.That(e.WriteBps, Is.EqualTo(4096));
        Assert.That(e.ReadBps, Is.EqualTo(65536));
        Assert.That(e.Device, Is.EqualTo("259:0"));
    }

    [Test]
    public void DefaultIsADevicePause()
    {
        NemesisEvent e = Read("""
            nemesis:
              events:
                - { at: 1s, fault: slow-disk }
            """).Events[0];

        Assert.That(e.WriteBps, Is.EqualTo(4096));
        Assert.That(e.WriteIops, Is.Zero);
        Assert.That(e.ReadBps, Is.Zero);
    }

    [Test]
    public void AllCapsZero_IsRejected()
    {
        NemesisException ex = Assert.Throws<NemesisException>(() => Read("""
            nemesis:
              events:
                - { at: 1s, fault: slow-disk, write_bps: 0 }
            """))!;
        Assert.That(ex.Message, Does.Contain("at least one"));
    }

    [Test]
    public void MalformedDevice_IsRejected()
    {
        Assert.Throws<NemesisException>(() => Read("""
            nemesis:
              events:
                - { at: 1s, fault: slow-disk, device: nvme0n1 }
            """));
    }

    [Test]
    public void FactoryBuildsIt_AndItHeals()
    {
        using HttpProbes probes = new();
        IFault fault = FaultFactory.Create(new NemesisEvent { Fault = "slow-disk", WriteBps = 1_048_576 }, probes);
        Assert.That(fault.Kind, Is.EqualTo("slow-disk"));
        Assert.That(fault.Healable, Is.True);
    }

    [Test]
    public void InjectResourcesCarryOnlyTheRequestedCaps()
    {
        SlowDiskFault pause = new(writeBps: 4096, writeIops: 0, readBps: 0, device: null);
        Assert.That(pause.InjectResources("/dev/nvme0n1"),
            Is.EqualTo("""{"BlkioDeviceWriteBps":[{"Path":"/dev/nvme0n1","Rate":4096}]}"""));

        SlowDiskFault all = new(writeBps: 1_048_576, writeIops: 50, readBps: 2048, device: null);
        string json = all.InjectResources("/dev/sda");
        Assert.That(json, Does.Contain("\"BlkioDeviceWriteBps\":[{\"Path\":\"/dev/sda\",\"Rate\":1048576}]"));
        Assert.That(json, Does.Contain("\"BlkioDeviceWriteIOps\":[{\"Path\":\"/dev/sda\",\"Rate\":50}]"));
        Assert.That(json, Does.Contain("\"BlkioDeviceReadBps\":[{\"Path\":\"/dev/sda\",\"Rate\":2048}]"));
    }

    [Test]
    public void LiftResourcesRaiseExactlyTheCapsThatWereSet()
    {
        SlowDiskFault pause = new(writeBps: 4096, writeIops: 0, readBps: 0, device: null);
        Assert.That(pause.LiftResources("/dev/nvme0n1"),
            Is.EqualTo($$"""{"BlkioDeviceWriteBps":[{"Path":"/dev/nvme0n1","Rate":{{SlowDiskFault.LiftRate}}}]}"""));
        Assert.That(pause.LiftResources("/dev/nvme0n1"), Does.Not.Contain("ReadBps"));
    }

    [Test]
    public void IsApplied_ReadsTheKernelsEchoOfTheLine()
    {
        SlowDiskFault fault = new(writeBps: 1_048_576, writeIops: 0, readBps: 0, device: null);
        Assert.That(fault.IsApplied("259:0 rbps=max wbps=1048576 riops=max wiops=max\n", "259:0"), Is.True);
        Assert.That(fault.IsApplied("259:0 rbps=max wbps=max riops=max wiops=max\n", "259:0"), Is.False, "lifted");
        Assert.That(fault.IsApplied("", "259:0"), Is.False, "no line at all");
        Assert.That(fault.IsApplied("8:0 rbps=max wbps=1048576 riops=max wiops=max\n", "259:0"), Is.False, "other device");
    }

    [Test]
    public void IsLifted_AcceptsMaxAHugeRateOrNoLine()
    {
        SlowDiskFault fault = new(writeBps: 4096, writeIops: 10, readBps: 0, device: null);
        Assert.That(fault.IsLifted("", "259:0"), Is.True, "systemd may drop the line entirely");
        Assert.That(fault.IsLifted("259:0 rbps=max wbps=max riops=max wiops=max\n", "259:0"), Is.True);
        Assert.That(fault.IsLifted($"259:0 rbps=max wbps={SlowDiskFault.LiftRate} riops=max wiops=4294967295\n", "259:0"), Is.True,
            "the runtime writes the lift rate literally and the kernel clamps iops to UINT_MAX");
        Assert.That(fault.IsLifted("259:0 rbps=max wbps=4096 riops=max wiops=max\n", "259:0"), Is.False, "still capped");
        Assert.That(fault.IsLifted($"259:0 rbps=max wbps={SlowDiskFault.LiftRate} riops=max wiops=10\n", "259:0"), Is.False, "iops still capped");
    }

    [Test]
    public void DescribeNamesTheNodeAndTheCap()
    {
        NodePlan node = ClusterPlan.FromSpec(ClusterSpecReader.Read("name: t\nnodes: 3")).Nodes[1];
        SlowDiskFault fault = new(writeBps: 4096, writeIops: 0, readBps: 0, device: "259:0");
        string text = fault.Describe(node);
        Assert.That(text, Does.Contain(node.ContainerName));
        Assert.That(text, Does.Contain("4 KiB/s"));
        Assert.That(text, Does.Contain("259:0"));
    }

    [Test]
    public void RandomScheduleAcceptsSlowDisk()
    {
        NemesisSpec n = Read("""
            nemesis:
              random:
                faults: [slow-disk, pause]
            """);
        Assert.That(n.Random!.Faults, Does.Contain("slow-disk"));
    }
}

[TestFixture]
public sealed class HostBlockDeviceTests
{
    private const string MountInfo =
        "24 30 0:22 / /sys rw,nosuid,nodev,noexec,relatime shared:7 - sysfs sysfs rw\n" +
        "29 1 259:2 / / rw,relatime shared:1 - ext4 /dev/nvme0n1p2 rw,errors=remount-ro\n" +
        "41 29 8:17 / /mnt/data\\040disk rw,relatime shared:20 - xfs /dev/sdb1 rw\n" +
        "55 29 0:45 / /var/lib/docker/overlay2/abc/merged rw,relatime - overlay overlay rw,lowerdir=x\n";

    [Test]
    public void ParsesAMountInfoLine()
    {
        MountEntry e = HostBlockDevice.ParseMountLine("29 1 259:2 / / rw,relatime shared:1 - ext4 /dev/nvme0n1p2 rw,errors=remount-ro")!;
        Assert.That(e.MajorMinor, Is.EqualTo("259:2"));
        Assert.That(e.MountPoint, Is.EqualTo("/"));
        Assert.That(e.FsType, Is.EqualTo("ext4"));
        Assert.That(e.Source, Is.EqualTo("/dev/nvme0n1p2"));
    }

    [Test]
    public void FindsTheLongestCoveringMount()
    {
        Assert.That(HostBlockDevice.FindMount(MountInfo, "/var/lib/docker")!.MajorMinor, Is.EqualTo("259:2"), "root serves docker");
        Assert.That(HostBlockDevice.FindMount(MountInfo, "/var/lib/docker/overlay2/abc/merged/x")!.FsType, Is.EqualTo("overlay"));
        Assert.That(HostBlockDevice.FindMount(MountInfo, "/mnt/data disk/volumes")!.Source, Is.EqualTo("/dev/sdb1"), "octal-escaped space");
        Assert.That(HostBlockDevice.FindMount(MountInfo, "/mnt/data")!.MountPoint, Is.EqualTo("/"), "prefix but not a path component");
    }

    [Test]
    public void GarbageLines_AreSkipped()
    {
        Assert.That(HostBlockDevice.ParseMountLine("not a mount line"), Is.Null);
        Assert.That(HostBlockDevice.FindMount("\n\nnope\n", "/var/lib/docker"), Is.Null);
    }

    [Test]
    public void ParsesTheUnifiedCgroupPath()
    {
        string systemd = "0::/system.slice/docker-9edddc968fcb.scope\n";
        Assert.That(HostBlockDevice.ParseUnifiedCgroup(systemd), Is.EqualTo("/system.slice/docker-9edddc968fcb.scope"));

        string cgroupfs = "0::/docker/9edddc968fcb\n";
        Assert.That(HostBlockDevice.ParseUnifiedCgroup(cgroupfs), Is.EqualTo("/docker/9edddc968fcb"));
    }

    [Test]
    public void WalksThisHostsRootPartitionUpToItsDisk()
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/sys/dev/block"))
            Assert.Ignore("Linux sysfs only");

        MountEntry root = HostBlockDevice.FindMount(File.ReadAllText("/proc/self/mountinfo"), "/")!;
        if (root.MajorMinor.StartsWith("0:", StringComparison.Ordinal))
            Assert.Ignore($"root is on an anonymous device ({root.FsType})");

        string disk = HostBlockDevice.WholeDiskOf(root.MajorMinor);
        Assert.That(disk, Does.Match("^[0-9]+:[0-9]+$"));
        Assert.That(File.Exists($"/sys/dev/block/{disk}/partition"), Is.False, $"{disk} must be a whole disk");
        Assert.That(HostBlockDevice.WholeDiskOf(disk), Is.EqualTo(disk), "a disk maps to itself");
    }

    [Test]
    public void RootCgroupOrV1_IsRejected()
    {
        Assert.Throws<NemesisException>(() => HostBlockDevice.ParseUnifiedCgroup("0::/\n"));
        Assert.Throws<NemesisException>(() => HostBlockDevice.ParseUnifiedCgroup("12:blkio:/docker/abc\n"));
    }
}

[TestFixture]
public sealed class DockerEngineApiTests
{
    [Test]
    public void ParsesWarnings()
    {
        Assert.That(DockerEngineApi.ParseWarnings("""{"Warnings":null}"""), Is.Empty);
        Assert.That(DockerEngineApi.ParseWarnings(""), Is.Empty);
        Assert.That(DockerEngineApi.ParseWarnings("""{"Warnings":["Your kernel does not support BlkioDeviceWriteBps"]}"""),
            Is.EqualTo(new[] { "Your kernel does not support BlkioDeviceWriteBps" }));
    }

    [Test]
    public void SocketPathHonoursUnixDockerHost()
    {
        Assert.That(DockerEngineApi.SocketPath(null), Is.EqualTo("/var/run/docker.sock"));
        Assert.That(DockerEngineApi.SocketPath("unix:///run/user/1000/docker.sock"), Is.EqualTo("/run/user/1000/docker.sock"));
        Assert.That(DockerEngineApi.SocketPath("tcp://10.0.0.1:2375"), Is.EqualTo("/var/run/docker.sock"), "only unix sockets are supported");
    }
}
