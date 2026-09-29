/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using NUnit.Framework;
using Caraxes.Core.Cluster;
using Caraxes.Core.Verdict;

namespace Caraxes.Tests;

[TestFixture]
public class NodeHealthTests
{
    private static readonly DateTime T0 = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);

    private string tempDir = "";

    [SetUp]
    public void SetUp()
    {
        tempDir = Path.Combine(Path.GetTempPath(), $"caraxes-health-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string WriteSeries(params (int OffsetSeconds, string Node, bool Reachable)[] samples)
    {
        string path = Path.Combine(tempDir, "node-health.csv");
        List<string> lines = ["ts,node,reachable"];
        foreach ((int offset, string node, bool reachable) in samples)
            lines.Add($"{T0.AddSeconds(offset):o},{node},{(reachable ? "true" : "false")}");
        File.WriteAllLines(path, lines);
        return path;
    }

    private static FaultWindow Window(string? target, int startOffset, int? endOffset) => new()
    {
        Kind = "pause",
        Target = target,
        StartUtc = T0.AddSeconds(startOffset),
        EndUtc = endOffset is null ? null : T0.AddSeconds(endOffset.Value),
    };

    [Test]
    public void AllReachableYieldsNoOutages()
    {
        string path = WriteSeries((0, "camus1", true), (30, "camus1", true), (60, "camus1", true));

        Assert.That(NodeHealthAnalysis.Analyze(path, [], 45), Is.Empty);
    }

    [Test]
    public void MissingSeriesFileYieldsNoOutages()
    {
        string path = Path.Combine(tempDir, "does-not-exist.csv");

        Assert.That(NodeHealthAnalysis.Analyze(path, [], 45), Is.Empty);
    }

    [Test]
    public void SingleMissedSampleIsReportedButNotFailed()
    {
        string path = WriteSeries((0, "camus1", true), (30, "camus1", false), (60, "camus1", true));

        IReadOnlyList<NodeOutage> outages = NodeHealthAnalysis.Analyze(path, [], 45);

        Assert.That(outages, Has.Count.EqualTo(1));
        Assert.That(outages[0].Samples, Is.EqualTo(1));
        Assert.That(outages[0].Excused, Is.True, "one missed probe can be a timeout race, never a verdict");
    }

    [Test]
    public void TwoConsecutiveMissesWithNoFaultFailTheNode()
    {
        string path = WriteSeries(
            (0, "camus3", true), (30, "camus3", false), (60, "camus3", false), (90, "camus3", false));

        IReadOnlyList<NodeOutage> outages = NodeHealthAnalysis.Analyze(path, [], 45);

        Assert.That(outages, Has.Count.EqualTo(1));
        Assert.That(outages[0].Node, Is.EqualTo("camus3"));
        Assert.That(outages[0].Samples, Is.EqualTo(3));
        Assert.That(outages[0].Excused, Is.False);
        Assert.That(outages[0].StartUtc, Is.EqualTo(T0.AddSeconds(30)));
        Assert.That(outages[0].EndUtc, Is.EqualTo(T0.AddSeconds(90)));
    }

    [Test]
    public void MissesInsideATargetedFaultWindowAreExcused()
    {
        string path = WriteSeries(
            (0, "camus1", true), (30, "camus1", false), (60, "camus1", false), (90, "camus1", true));

        IReadOnlyList<NodeOutage> outages =
            NodeHealthAnalysis.Analyze(path, [Window("camus1", 20, 70)], 45);

        Assert.That(outages, Has.Count.EqualTo(1));
        Assert.That(outages[0].Excused, Is.True);
    }

    [Test]
    public void RecoveryGraceAfterHealExcusesLateMisses()
    {
        // Fault heals at +40; probes at +60 and +80 sit inside a 45 s grace, +90 does not exist.
        string path = WriteSeries(
            (0, "camus2", true), (60, "camus2", false), (80, "camus2", false), (110, "camus2", true));

        IReadOnlyList<NodeOutage> outages =
            NodeHealthAnalysis.Analyze(path, [Window("camus2", 20, 40)], 45);

        Assert.That(outages, Has.Count.EqualTo(1));
        Assert.That(outages[0].Excused, Is.True);
    }

    [Test]
    public void FaultOnAnotherNodeDoesNotExcuseAnOutage()
    {
        string path = WriteSeries(
            (0, "camus3", true), (30, "camus3", false), (60, "camus3", false));

        IReadOnlyList<NodeOutage> outages =
            NodeHealthAnalysis.Analyze(path, [Window("camus1", 20, 70)], 45);

        Assert.That(outages, Has.Count.EqualTo(1));
        Assert.That(outages[0].Excused, Is.False);
    }

    [Test]
    public void ClusterWideFaultExcusesEveryNode()
    {
        string path = WriteSeries(
            (0, "camus2", true), (30, "camus2", false), (60, "camus2", false), (90, "camus2", true));

        IReadOnlyList<NodeOutage> outages =
            NodeHealthAnalysis.Analyze(path, [Window(target: null, 20, 70)], 45);

        Assert.That(outages, Has.Count.EqualTo(1));
        Assert.That(outages[0].Excused, Is.True);
    }

    [Test]
    public void UnhealedCrashFaultExcusesEverythingAfterInjection()
    {
        string path = WriteSeries(
            (0, "camus1", true), (30, "camus1", false), (600, "camus1", false), (1200, "camus1", false));

        IReadOnlyList<NodeOutage> outages =
            NodeHealthAnalysis.Analyze(path, [Window("camus1", 10, endOffset: null)], 45);

        Assert.That(outages, Has.Count.EqualTo(1));
        Assert.That(outages[0].Excused, Is.True);
    }

    [Test]
    public void OutagePartlyOutsideTheWindowIsNotExcused()
    {
        // The window covers the first miss only; the outage keeps running after grace expires.
        string path = WriteSeries(
            (0, "camus1", true), (30, "camus1", false), (120, "camus1", false), (150, "camus1", false));

        IReadOnlyList<NodeOutage> outages =
            NodeHealthAnalysis.Analyze(path, [Window("camus1", 20, 40)], 45);

        Assert.That(outages, Has.Count.EqualTo(1));
        Assert.That(outages[0].Excused, Is.False);
    }

    [Test]
    public void ProbeIssuedWithinItsTimeoutBeforeTheFaultIsExcused()
    {
        // Issued 2 s before the kill, failed because the node died while it waited; the next probe
        // lands inside the window.
        string path = WriteSeries(
            (0, "camus2", true), (28, "camus2", false), (60, "camus2", false), (90, "camus2", true));

        Assert.That(NodeHealthAnalysis.Analyze(path, [Window("camus2", 30, 55)], 45, probeTimeoutSeconds: 5)[0].Excused,
            Is.True);
        Assert.That(NodeHealthAnalysis.Analyze(path, [Window("camus2", 30, 55)], 45, probeTimeoutSeconds: 0)[0].Excused,
            Is.False, "without the timeout allowance the in-flight probe reads as a node that died on its own");
    }

    [Test]
    public void ProbeIssuedBeforeItsTimeoutCouldCoverTheFaultIsNotExcused()
    {
        // Issued 6 s before the kill: a 5 s probe had already failed before the fault existed.
        string path = WriteSeries(
            (0, "camus2", true), (24, "camus2", false), (60, "camus2", false), (90, "camus2", true));

        Assert.That(NodeHealthAnalysis.Analyze(path, [Window("camus2", 30, 55)], 45, probeTimeoutSeconds: 5)[0].Excused,
            Is.False);
    }

    /// <summary>
    /// The two recorded soaks whose verdicts failed on "died on its own": fs13 (a probe issued 0.28 s
    /// before its kill's SIGKILL) and rl4b-mor (the same race at kill 1, and a <c>docker kill</c> that
    /// took 6.8 s to return on a saturated device while the node was already dead). Their timelines
    /// predate <c>issuedTs</c>, so the windows open at the leader-resolution notes.
    /// </summary>
    [TestCase("fs13", 11)]
    [TestCase("rl4b-mor", 12)]
    public void RecordedSoaksGradeEveryOutageAsExplained(string run, int failedProbes)
    {
        string dir = Path.Combine(TestContext.CurrentContext.TestDirectory, "Recordings", run);
        string health = Path.Combine(dir, "node-health.csv");

        IReadOnlyList<NodeOutage> outages = NodeHealthAnalysis.Analyze(
            health, FaultTimeline.Parse(Path.Combine(dir, "timeline.jsonl")), 60, HttpProbes.DefaultTimeout.TotalSeconds);

        Assert.That(outages.Sum(o => o.Samples), Is.EqualTo(failedProbes));
        Assert.That(outages.Where(o => !o.Excused), Is.Empty);

        // The same series against windows opened when each inject returned: the verdict the runs got.
        string injectReturned = Path.Combine(tempDir, "timeline.jsonl");
        File.WriteAllLines(injectReturned, File.ReadLines(Path.Combine(dir, "timeline.jsonl"))
            .Where(line => !line.Contains("\"phase\":\"note\"")));
        Assert.That(NodeHealthAnalysis.Analyze(health, FaultTimeline.Parse(injectReturned), 60).Where(o => !o.Excused),
            Is.Not.Empty);
    }

    [Test]
    public void RecordedSlowKillOpensItsWindowWhenTheLeaderWasResolved()
    {
        string timeline = Path.Combine(TestContext.CurrentContext.TestDirectory, "Recordings", "rl4b-mor", "timeline.jsonl");

        FaultWindow last = FaultTimeline.Parse(timeline).Last();

        Assert.That(last.Label, Is.EqualTo("kill/camus1"));
        Assert.That(last.StartUtc, Is.EqualTo(DateTime.Parse("2026-09-29T01:40:17.2693730Z").ToUniversalTime()));
    }

    [TestCase("570.9MiB / 1.5GiB", 570.9, 1536.0)]
    [TestCase("1.024GiB / 2GiB", 1048.576, 2048.0)]
    [TestCase("512KiB / 1GiB", 0.5, 1024.0)]
    [TestCase("garbage", 0.0, 0.0)]
    [TestCase("12MiB", 0.0, 0.0)]
    public void ParsesDockerMemUsageCells(string cell, double expectedMib, double expectedLimitMib)
    {
        (double memMib, double limitMib) = NodeMonitor.ParseMemUsage(cell);

        Assert.That(memMib, Is.EqualTo(expectedMib).Within(0.001));
        Assert.That(limitMib, Is.EqualTo(expectedLimitMib).Within(0.001));
    }
}
