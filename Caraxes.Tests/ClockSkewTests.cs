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
public sealed class ClockSkewTests
{
    private static ScenarioSpec Read(string clusterExtra, string nemesisBlock) => ScenarioSpecReader.Read($"""
        name: s
        cluster:
          name: c
        {clusterExtra}
        {nemesisBlock}
        """);

    [Test]
    public void ShimIsOffByDefault()
    {
        ClusterPlan plan = ClusterPlan.FromSpec(ClusterSpecReader.Read("name: plain"));
        string compose = ComposeGenerator.Generate(plan, "./config");

        Assert.That(plan.Spec.NodeImage, Is.EqualTo(plan.Spec.EffectiveImage));
        Assert.That(compose, Does.Not.Contain("LD_PRELOAD"));
        Assert.That(compose, Does.Not.Contain(ClockSkewShim.ImageSuffix));
    }

    [Test]
    public void ClusterWithShimRunsLayerAndPreloadsIt()
    {
        ClusterPlan plan = ClusterPlan.FromSpec(ClusterSpecReader.Read("name: skewed\nclock_skew: true"));
        string compose = ComposeGenerator.Generate(plan, "./config");

        Assert.That(plan.Spec.NodeImage, Is.EqualTo("caraxes/camusdb:skewed-clockskew"));
        Assert.That(compose, Does.Contain("image: caraxes/camusdb:skewed-clockskew"));
        Assert.That(compose, Does.Contain($"LD_PRELOAD: {ClockSkewShim.LibraryPath}"));
        Assert.That(compose, Does.Contain($"CARAXES_SKEW_FILE: {ClockSkewShim.OffsetFile}"));
    }

    [Test]
    public void ParsesClockSkewEvent()
    {
        ScenarioSpec spec = Read("  clock_skew: true", """
            nemesis:
              events:
                - { at: 10s, fault: clock-skew, target: camus2, duration: 30s, offset_ms: -2500 }
            """);

        NemesisEvent e = spec.Nemesis!.Events[0];
        Assert.That(e.Fault, Is.EqualTo("clock-skew"));
        Assert.That(e.OffsetMs, Is.EqualTo(-2500));
        Assert.That(FaultFactory.Create(e, null!).Describe(ClusterPlan.FromSpec(spec.Cluster).Nodes[1]),
            Does.Contain("by -2500 ms"));
    }

    [Test]
    public void ClockSkewWithoutShimIsRejected()
    {
        ScenarioException ex = Assert.Throws<ScenarioException>(() => Read("", """
            nemesis:
              events:
                - { at: 10s, fault: clock-skew, target: camus1 }
            """))!;
        Assert.That(ex.Message, Does.Contain("clock_skew: true"));
    }

    [Test]
    public void RandomSoakWithClockSkewNeedsShimToo()
    {
        Assert.Throws<ScenarioException>(() => Read("", """
            nemesis:
              random:
                faults: [kill, clock-skew]
            """));
    }

    [TestCase(0)]
    [TestCase(12L * 60 * 60 * 1000 + 1)]
    [TestCase(-12L * 60 * 60 * 1000 - 1)]
    public void RejectsZeroOrOversizedOffsets(long offsetMs)
    {
        Assert.Throws<NemesisException>(() => Read("  clock_skew: true", $$"""
            nemesis:
              events:
                - { at: 10s, fault: clock-skew, target: camus1, offset_ms: {{offsetMs}} }
            """));
    }

    [Test]
    public void ScriptReplacesOffsetFileAtomicallyAndMeasuresBothClocks()
    {
        string script = ClockSkewFault.Script(-2500);

        Assert.That(script, Does.StartWith($"printf '%s\\n' -2500 > {ClockSkewShim.OffsetFile}.tmp && mv -f "));
        Assert.That(script, Does.Contain("env -u LD_PRELOAD date +%s%3N"));
    }

    [TestCase("1790642731146 1790642741150\n", 10_004)]
    [TestCase("1790642731146 1790642728646", -2_500)]
    public void ParsesMeasuredOffset(string output, long expected)
    {
        Assert.That(ClockSkewFault.ParseMeasuredOffset(output), Is.EqualTo(expected));
    }

    [TestCase("")]
    [TestCase("1790642731146")]
    [TestCase("date: invalid option")]
    public void UnreadableMeasurementThrows(string output)
    {
        Assert.Throws<InvalidOperationException>(() => ClockSkewFault.ParseMeasuredOffset(output));
    }
}
