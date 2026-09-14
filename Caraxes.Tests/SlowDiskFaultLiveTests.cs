/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Diagnostics;
using NUnit.Framework;
using Caraxes.Core.Cluster;
using Caraxes.Core.Nemesis;

namespace Caraxes.Tests;

/// <summary>
/// Drives the real <see cref="SlowDiskFault"/> inject/heal path against a throw-away container on a
/// named volume and measures the effect on dsync writes. Needs docker (docker-group membership) and
/// a cgroup v2 host, so it is <c>[Explicit]</c>: run it with
/// <c>dotnet test --filter FullyQualifiedName~SlowDiskFaultLive</c> before trusting a slow-disk
/// scenario on a new machine.
/// </summary>
[TestFixture]
[Explicit("needs docker and a cgroup v2 host")]
public sealed class SlowDiskFaultLiveTests
{
    private const string SpecName = "slowdisklive";

    private const string Image = "debian:stable-slim";

    private ClusterPlan plan = null!;

    private string container = null!;

    private string volume = null!;

    [SetUp]
    public async Task StartContainer()
    {
        ClusterSpec spec = ClusterSpecReader.Read($"name: {SpecName}\nnodes: 1\nimage: {Image}");
        plan = ClusterPlan.FromSpec(spec);
        container = plan.Nodes[0].ContainerName;
        volume = $"{container}-data";

        await ProcessRunner.RunAsync("docker", ["rm", "-f", container]);
        await ProcessRunner.RunAsync("docker", ["volume", "rm", "-f", volume]);
        await ProcessRunner.RunCheckedAsync(
            "docker", ["run", "-d", "--name", container, "-v", $"{volume}:/data", Image, "sleep", "600"]);
    }

    [TearDown]
    public async Task RemoveContainer()
    {
        await ProcessRunner.RunAsync("docker", ["rm", "-f", container]);
        await ProcessRunner.RunAsync("docker", ["volume", "rm", "-f", volume]);
    }

    [Test]
    public async Task ThrottleSlowsDsyncWrites_AndHealLiftsIt()
    {
        BlockDisk disk = await HostBlockDevice.ResolveDockerVolumeDiskAsync(CancellationToken.None);
        Assert.That(disk.MajorMinor, Does.Match("^[0-9]+:[0-9]+$"));
        Assert.That(File.Exists($"/sys/dev/block/{disk.MajorMinor}/partition"), Is.False, "io.max keys on the whole disk");
        Assert.That(File.Exists(disk.Path), Is.True, disk.Path);

        string cgroup = await HostBlockDevice.ResolveContainerCgroupAsync(container, CancellationToken.None);
        Assert.That(File.Exists(Path.Combine(cgroup, "io.max")), Is.True, cgroup);

        double baseline = await TimeDsyncWriteSecondsAsync("base");
        TestContext.Out.WriteLine($"disk {disk.MajorMinor} {disk.Path}, cgroup {cgroup}, baseline 6.4 MB dsync: {baseline:0.000} s");

        SlowDiskFault fault = new(writeBps: 1_048_576, writeIops: 0, readBps: 0, device: null);
        await fault.InjectAsync(plan.Nodes[0], plan, CancellationToken.None);
        try
        {
            string ioMax = await File.ReadAllTextAsync(Path.Combine(cgroup, "io.max"));
            Assert.That(fault.IsApplied(ioMax, disk.MajorMinor), Is.True, ioMax);

            double throttled = await TimeDsyncWriteSecondsAsync("slow");
            TestContext.Out.WriteLine($"throttled at 1 MiB/s: {throttled:0.000} s");
            Assert.That(throttled, Is.GreaterThan(5.0), "6.4 MB at 1 MiB/s must take about 6 s");
        }
        finally
        {
            await fault.HealAsync(plan.Nodes[0], plan, CancellationToken.None);
        }

        string lifted = await File.ReadAllTextAsync(Path.Combine(cgroup, "io.max"));
        Assert.That(fault.IsLifted(lifted, disk.MajorMinor), Is.True, lifted);
        Assert.That(fault.IsApplied(lifted, disk.MajorMinor), Is.False, lifted);

        double after = await TimeDsyncWriteSecondsAsync("after");
        TestContext.Out.WriteLine($"after heal: {after:0.000} s (io.max: '{lifted.Trim()}')");
        Assert.That(after, Is.LessThan(2.0), "heal must restore device speed");
    }

    /// <summary>The property that run sd1 lacked: while a node is throttled to a pause, creating
    /// another container must still be possible — which it is only because inject and heal never
    /// create or remove one themselves. Guards against reintroducing a helper container.</summary>
    [Test]
    public async Task ContainerCreationStaysPossibleWhileThrottled_BecauseNothingIsRemovedUnderTheCap()
    {
        SlowDiskFault pause = new(writeBps: 4096, writeIops: 0, readBps: 0, device: null);
        await pause.InjectAsync(plan.Nodes[0], plan, CancellationToken.None);
        try
        {
            // Dirty some pages under the cap so writeback is genuinely throttled, without waiting on it.
            await ProcessRunner.RunAsync(
                "docker", ["exec", container, "dd", "if=/dev/zero", "of=/data/dirty", "bs=1M", "count=8"]);

            ProcessResult exec = await ProcessRunner.RunAsync("docker", ["exec", container, "true"]);
            Assert.That(exec.Success, Is.True, "exec must work under the cap");
        }
        finally
        {
            await pause.HealAsync(plan.Nodes[0], plan, CancellationToken.None);
        }

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        ProcessResult create = await ProcessRunner.RunAsync(
            "docker", ["run", "--rm", "--network", "none", Image, "true"], cancellationToken: cts.Token);
        Assert.That(create.Success, Is.True, "a create+remove after the heal must complete: " + create.StdErr);
    }

    [Test]
    public void TmpfsRig_IsRefused()
    {
        ClusterSpec tmpfs = ClusterSpecReader.Read($"name: {SpecName}\nnodes: 1\nimage: {Image}\ndata_tmpfs_mb: 64");
        ClusterPlan tmpfsPlan = ClusterPlan.FromSpec(tmpfs);
        SlowDiskFault fault = new(writeBps: 4096, writeIops: 0, readBps: 0, device: null);

        NemesisException ex = Assert.ThrowsAsync<NemesisException>(
            () => fault.InjectAsync(tmpfsPlan.Nodes[0], tmpfsPlan, CancellationToken.None))!;
        Assert.That(ex.Message, Does.Contain("tmpfs"));
    }

    private async Task<double> TimeDsyncWriteSecondsAsync(string file)
    {
        Stopwatch sw = Stopwatch.StartNew();
        await ProcessRunner.RunCheckedAsync(
            "docker", ["exec", container, "dd", $"if=/dev/zero", $"of=/data/{file}", "bs=64k", "count=100", "oflag=dsync"]);
        return sw.Elapsed.TotalSeconds;
    }
}
