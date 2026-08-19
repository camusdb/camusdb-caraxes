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
        foreach (string kind in new[] { "kill", "stop", "pause", "partition", "slow", "loss", "fill-disk", "remove-node" })
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
