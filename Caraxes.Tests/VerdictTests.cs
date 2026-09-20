/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Globalization;
using NUnit.Framework;
using Caraxes.Core.Cluster;
using Caraxes.Core.Verdict;

namespace Caraxes.Tests;

[TestFixture]
public sealed class IntervalSeriesTests
{
    [Test]
    public void LoadsAndAnchorsToWallClock()
    {
        string dir = TempDir();
        try
        {
            DateTime start = new(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);
            File.WriteAllText(Path.Combine(dir, "run-meta.json"),
                $$"""{ "measureStartUtc": "{{start:O}}", "warmupSeconds": 15, "measureSeconds": 3 }""");
            File.WriteAllText(Path.Combine(dir, "intervals.csv"), string.Join('\n',
                "second,offered,started,completed,failed,in_flight,schedule_drops,read_p50_ms,read_p95_ms,read_p99_ms,write_p50_ms,write_p95_ms,write_p99_ms",
                "0,100,100,100,0,4,0,1,2,3,5,10,12",
                "1,100,100,90,10,4,0,1,2,3,5,10,40",
                "2,100,100,100,0,4,0,1,2,3,5,10,12"));

            IntervalSeries? series = IntervalSeries.Load(dir);

            Assert.That(series, Is.Not.Null);
            Assert.That(series!.Points, Has.Count.EqualTo(3));
            Assert.That(series.MeasureStartUtc, Is.EqualTo(start));
            Assert.That(series.Points[1].AbsoluteUtc, Is.EqualTo(start.AddSeconds(1)));
            Assert.That(series.Points[1].ErrorRate, Is.EqualTo(0.1).Within(1e-9));
            Assert.That(series.Points[1].WriteP99Ms, Is.EqualTo(40));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void MissingMetaOrCsv_ReturnsNull()
    {
        string dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "intervals.csv"), "second\n0");
            Assert.That(IntervalSeries.Load(dir), Is.Null, "no run-meta.json → cannot anchor");
        }
        finally { Directory.Delete(dir, true); }
    }

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "caraxes-iv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}

[TestFixture]
public sealed class FaultTimelineTests
{
    [Test]
    public void PairsInjectWithHeal()
    {
        string path = WriteTimeline(
            Line("note", "start", null, "2026-08-19T12:00:00.0000000Z"),
            Line("inject", "kill", "camus2", "2026-08-19T12:00:20.0000000Z"),
            Line("heal", "kill", "camus2", "2026-08-19T12:00:40.0000000Z"));
        try
        {
            var windows = FaultTimeline.Parse(path);
            Assert.That(windows, Has.Count.EqualTo(1));
            Assert.That(windows[0].Label, Is.EqualTo("kill/camus2"));
            Assert.That(windows[0].Healed, Is.True);
            Assert.That((windows[0].EndUtc!.Value - windows[0].StartUtc).TotalSeconds, Is.EqualTo(20));
        }
        finally { File.Delete(path); }
    }

    [Test]
    public void UnhealedInject_IsOpenEnded()
    {
        string path = WriteTimeline(
            Line("inject", "kill", "camus1", "2026-08-19T12:00:10.0000000Z"));
        try
        {
            var windows = FaultTimeline.Parse(path);
            Assert.That(windows, Has.Count.EqualTo(1));
            Assert.That(windows[0].Healed, Is.False);
            Assert.That(windows[0].EndUtc, Is.Null);
        }
        finally { File.Delete(path); }
    }

    private static string Line(string phase, string kind, string? target, string ts)
    {
        string tgt = target is null ? "null" : $"\"{target}\"";
        return $$"""{"ts":"{{ts}}","phase":"{{phase}}","kind":"{{kind}}","target":{{tgt}},"detail":"x"}""";
    }

    private static string WriteTimeline(params string[] lines)
    {
        string path = Path.Combine(Path.GetTempPath(), "caraxes-tl-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }
}

[TestFixture]
public sealed class FaultCorrelatorTests
{
    // Build a 40s series: clean everywhere except a spike during a fault window at seconds 10-20,
    // with error rate returning to zero by second 23 (3s recovery after the 20s heal).
    private static IntervalSeries BuildSeries(DateTime start, out DateTime injectAt, out DateTime healAt)
    {
        injectAt = start.AddSeconds(10);
        healAt = start.AddSeconds(20);

        string dir = Path.Combine(Path.GetTempPath(), "caraxes-corr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "run-meta.json"),
            $$"""{ "measureStartUtc": "{{start:O}}" }""");

        List<string> rows =
        [
            "second,offered,started,completed,failed,in_flight,schedule_drops,read_p50_ms,read_p95_ms,read_p99_ms,write_p50_ms,write_p95_ms,write_p99_ms"
        ];
        for (int s = 0; s < 40; s++)
        {
            // In-window (10..20) and tail (21..22) carry errors + high latency; elsewhere clean.
            bool degraded = s is >= 10 and <= 22;
            long failed = degraded ? 50 : 0;
            long completed = degraded ? 50 : 100; // still progressing
            double p99 = degraded ? 200 : 10;
            rows.Add($"{s},100,100,{completed},{failed},4,0,1,2,3,5,10,{p99.ToString(CultureInfo.InvariantCulture)}");
        }
        File.WriteAllText(Path.Combine(dir, "intervals.csv"), string.Join('\n', rows));

        IntervalSeries series = IntervalSeries.Load(dir)!;
        Directory.Delete(dir, true);
        return series;
    }

    [Test]
    public void ComputesRecoveryAndImpact()
    {
        DateTime start = new(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);
        IntervalSeries series = BuildSeries(start, out DateTime injectAt, out DateTime healAt);

        var windows = new List<FaultWindow>
        {
            new() { Kind = "kill", Target = "camus2", StartUtc = injectAt, EndUtc = healAt },
        };

        FaultAnalysis a = FaultCorrelator.Analyze(series, windows);

        Assert.That(a.BaselineErrorRate, Is.EqualTo(0).Within(1e-9), "clean seconds have no errors");
        Assert.That(a.InFaultErrorRate, Is.GreaterThan(0.4), "the fault window is heavily errored");
        Assert.That(a.LatencyInflation, Is.GreaterThan(5), "write p99 inflates under fault");

        WindowImpact w = a.Windows.Single();
        Assert.That(w.WorkloadProgressed, Is.True, "workload kept completing ops during the fault");
        Assert.That(w.Recovered, Is.True);
        // Errors persist through second 22; first clean second after the 20s heal is 23 → ~3s.
        Assert.That(w.RecoverySeconds, Is.EqualTo(3).Within(0.001));
        Assert.That(a.MaxRecoverySeconds, Is.EqualTo(3).Within(0.001));
        Assert.That(a.AllHealedFaultsRecovered, Is.True);

        // Throughput gate: pre-fault median is the clean 100/s; the window served 50/s; after the
        // heal at 20 s the seconds 21-22 are still at 50, so the trailing 5-second mean first reaches
        // 90% of 100 at second 26 (50,100,100,100,100) → 6 s.
        Assert.That(w.PreFaultThroughput, Is.EqualTo(100).Within(0.001));
        Assert.That(w.InWindowThroughput, Is.EqualTo(50).Within(0.001));
        Assert.That(w.ThroughputRecovered, Is.True);
        Assert.That(w.ThroughputRecoverySeconds, Is.EqualTo(6).Within(0.001));
        Assert.That(w.ThroughputHeld, Is.True, "the series stays at 100/s after the recovery");
    }

    [Test]
    public void ThroughputGate_FailsARecoveryThatDoesNotHold()
    {
        // Recovers fully 1 s after the heal, then collapses to 45% for the rest of the series (run lk2).
        DateTime start = new(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);
        string dir = Path.Combine(Path.GetTempPath(), "caraxes-corr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "run-meta.json"), "{ \"measureStartUtc\": \"" + start.ToString("O") + "\" }");
        List<string> rows = ["second,offered,started,completed,failed,in_flight,schedule_drops,read_p50_ms,read_p95_ms,read_p99_ms,write_p50_ms,write_p95_ms,write_p99_ms"];
        for (int s = 0; s < 90; s++)
        {
            long completed = s < 10 ? 100 : s <= 20 ? 30 : s <= 40 ? 100 : 45;
            long failed = s is >= 10 and <= 20 ? 20 : 0;
            rows.Add($"{s},100,100,{completed},{failed},4,0,1,2,3,5,10,10");
        }
        File.WriteAllText(Path.Combine(dir, "intervals.csv"), string.Join('\n', rows));
        IntervalSeries series = IntervalSeries.Load(dir)!;
        Directory.Delete(dir, true);

        var windows = new List<FaultWindow>
        {
            new() { Kind = "kill", Target = "camus1", StartUtc = start.AddSeconds(10), EndUtc = start.AddSeconds(20) },
        };

        WindowImpact w = FaultCorrelator.Analyze(series, windows, 0.9).Windows.Single();
        Assert.That(w.ThroughputRecovered, Is.True, "it did cross the bar");
        Assert.That(w.ThroughputHeld, Is.False, "and then fell to 45% until the end");
        Assert.That(w.PostRecoveryMedianThroughput, Is.LessThan(90));
    }

    /// <summary>The lk2/lk11-shaped series: full recovery 1 s after the heal at 20 s, 100/s to 40 s, then 45/s to the end.</summary>
    private static IntervalSeries CollapsingTail(DateTime start)
    {
        string dir = Path.Combine(Path.GetTempPath(), "caraxes-corr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "run-meta.json"), "{ \"measureStartUtc\": \"" + start.ToString("O") + "\" }");
        List<string> rows = ["second,offered,started,completed,failed,in_flight,schedule_drops,read_p50_ms,read_p95_ms,read_p99_ms,write_p50_ms,write_p95_ms,write_p99_ms"];
        for (int s = 0; s < 90; s++)
        {
            long completed = s < 10 ? 100 : s <= 20 ? 30 : s <= 40 ? 100 : 45;
            long failed = s is >= 10 and <= 20 ? 20 : 0;
            rows.Add($"{s},100,100,{completed},{failed},4,0,1,2,3,5,10,10");
        }
        File.WriteAllText(Path.Combine(dir, "intervals.csv"), string.Join('\n', rows));
        IntervalSeries series = IntervalSeries.Load(dir)!;
        Directory.Delete(dir, true);
        return series;
    }

    /// <summary>Host samples every 2 s over 90 s; the device is fast until <paramref name="slowFromSecond"/>, slow after.</summary>
    private static List<HostIoSample> HostSamples(DateTime start, int? slowFromSecond)
    {
        List<HostIoSample> samples = [];
        for (int s = 0; s < 90; s += 2)
        {
            bool slow = slowFromSecond is not null && s >= slowFromSecond;
            samples.Add(new HostIoSample(start.AddSeconds(s), 10, 1, 500, 60, slow ? 85 : 35, slow ? 1.9 : 0.05, 2));
        }
        return samples;
    }

    /// <summary>A closed loop that recovers 1 s after the heal at 20 s and runs at 100/s, except for one
    /// dip to 45/s over <paramref name="dipFrom"/>–<paramref name="dipTo"/>; 240 s long.</summary>
    private static IntervalSeries OneDip(DateTime start, int dipFrom, int dipTo)
    {
        string dir = Path.Combine(Path.GetTempPath(), "caraxes-corr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "run-meta.json"), "{ \"measureStartUtc\": \"" + start.ToString("O") + "\" }");
        List<string> rows = ["second,offered,started,completed,failed,in_flight,schedule_drops,read_p50_ms,read_p95_ms,read_p99_ms,write_p50_ms,write_p95_ms,write_p99_ms"];
        for (int s = 0; s < 240; s++)
        {
            long completed = s < 10 ? 100 : s <= 20 ? 30 : s >= dipFrom && s < dipTo ? 45 : 100;
            long failed = s is >= 10 and <= 20 ? 20 : 0;
            rows.Add($"{s},100,100,{completed},{failed},4,0,1,2,3,5,10,10");
        }
        File.WriteAllText(Path.Combine(dir, "intervals.csv"), string.Join('\n', rows));
        IntervalSeries series = IntervalSeries.Load(dir)!;
        Directory.Delete(dir, true);
        return series;
    }

    /// <summary>Host samples every 2 s over 240 s; the device is slow over <paramref name="slowFrom"/>–<paramref name="slowTo"/> only.</summary>
    private static List<HostIoSample> HostSamplesSlowBetween(DateTime start, int slowFrom, int slowTo)
    {
        List<HostIoSample> samples = [];
        for (int s = 0; s < 240; s += 2)
        {
            bool slow = s >= slowFrom && s < slowTo;
            samples.Add(new HostIoSample(start.AddSeconds(s), 10, 1, 500, 60, slow ? 85 : 35, slow ? 1.9 : 0.05, 2));
        }
        return samples;
    }

    [Test]
    public void DeviceRegime_AMoveConfinedToTheLongestDip_VoidsTheHeldRule()
    {
        // Run lk13 (2026-09-16): the kill-1 tail held for two minutes, then fell for its last 37 s in
        // the sample where the NVMe went from 39% to 75% busy. The tail's own medians stayed on the
        // fast device (the slow part was a fifth of it), so the whole-tail regime reads steady; the
        // dip's regime is what moved, and the held rule must be voided on that.
        DateTime start = new(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);
        var windows = new List<FaultWindow>
        {
            new() { Kind = "kill", Target = "camus1", StartUtc = start.AddSeconds(10), EndUtc = start.AddSeconds(20) },
        };

        WindowImpact w = FaultCorrelator.Analyze(OneDip(start, 180, 220), windows, 0.9, HostSamplesSlowBetween(start, 178, 222)).Windows.Single();

        Assert.That(w.ThroughputHeld, Is.False, "a 40-s dip is longer than the 30 s the held rule tolerates");
        Assert.That(w.LongestPostRecoveryDipSeconds, Is.GreaterThanOrEqualTo(35));
        Assert.That(w.Device!.Moved, Is.False, "the tail as a whole ran on the fast device");
        Assert.That(w.DipDevice, Is.Not.Null, "the dip has its own regime");
        Assert.That(w.DipDevice!.Moved, Is.True, "and that one moved");
        Assert.That(w.DipDevice.PostHealUtilPercent, Is.EqualTo(85).Within(1e-9));
        Assert.That(w.DipDevice.Span, Does.Contain("dip"));
        Assert.That(w.HeldRuleInadmissible, Is.True);
        Assert.That(w.HeldRuleMover, Is.SameAs(w.DipDevice));
    }

    [Test]
    public void DeviceRegime_ADipOnASteadyDevice_KeepsTheHeldRuleJudged()
    {
        DateTime start = new(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);
        var windows = new List<FaultWindow>
        {
            new() { Kind = "kill", Target = "camus1", StartUtc = start.AddSeconds(10), EndUtc = start.AddSeconds(20) },
        };

        WindowImpact w = FaultCorrelator.Analyze(OneDip(start, 180, 220), windows, 0.9, HostSamplesSlowBetween(start, 0, 0)).Windows.Single();

        Assert.That(w.ThroughputHeld, Is.False);
        Assert.That(w.DipDevice, Is.Not.Null);
        Assert.That(w.DipDevice!.Moved, Is.False, "same device through the dip: the dip is the cluster's");
        Assert.That(w.HeldRuleInadmissible, Is.False);
        Assert.That(w.HeldRuleMover, Is.Null);
    }

    [Test]
    public void DeviceRegime_ASlowRegainOnAMovedDevice_VoidsTheRegainTimeRule()
    {
        // Run lk13, kill 2: 66.9 s to regain 90% while the NVMe sat at 75-83% busy from the heal; the
        // regain-time rule (60 s) is judged on the device the recovery ran on, not on the tail's median.
        DateTime start = new(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);
        var windows = new List<FaultWindow>
        {
            new() { Kind = "kill", Target = "camus1", StartUtc = start.AddSeconds(10), EndUtc = start.AddSeconds(20) },
        };

        // Slow from the heal until second 100, so the trailing mean crosses the bar ~84 s after the heal.
        WindowImpact w = FaultCorrelator.Analyze(OneDip(start, 21, 100), windows, 0.9, HostSamplesSlowBetween(start, 20, 100)).Windows.Single();

        Assert.That(w.ThroughputRecovered, Is.True);
        Assert.That(w.ThroughputRecoverySeconds, Is.GreaterThan(60));
        Assert.That(w.ThroughputHeld, Is.True, "after the crossing it ran at full rate to the end");
        Assert.That(w.Device!.Moved, Is.False, "the tail's medians are the fast device's: 140 fast seconds against 80 slow");
        Assert.That(w.RecoveryDevice, Is.Not.Null);
        Assert.That(w.RecoveryDevice!.Moved, Is.True);
        Assert.That(w.RecoveryDevice.Span, Does.Contain("recovery"));
        Assert.That(w.RegainRuleInadmissible, Is.True);
    }

    [Test]
    public void DeviceRegime_ABarSetOnTheSlowRegime_VoidsBothThroughputRules()
    {
        // sd12 pause 3 (2026-09-16): pre-fault fsync p50 1.13 ms at 80% busy, the tail at 1.90 ms and
        // 89% — not a "move" by the 3x/+30-point test, but the bar was set on a device that was already
        // slow and sliding, which on this host is not a bar a tail can be held to.
        DateTime start = new(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);
        var windows = new List<FaultWindow>
        {
            new() { Kind = "slow-disk", Target = "camus3", StartUtc = start.AddSeconds(10), EndUtc = start.AddSeconds(20) },
        };

        WindowImpact w = FaultCorrelator.Analyze(CollapsingTail(start), windows, 0.9, HostSamples(start, slowFromSecond: 0)).Windows.Single();

        Assert.That(w.ThroughputHeld, Is.False);
        Assert.That(w.Device!.Moved, Is.False, "slow before and after: no move");
        Assert.That(w.Device.PreFaultSlow, Is.True);
        Assert.That(w.HeldRuleInadmissible, Is.True);
        Assert.That(w.HeldRuleVoidReason, Does.Contain("slow regime"));
        Assert.That(w.RegainRuleInadmissible, Is.True);
    }

    [Test]
    public void DeviceRegime_AFastPreFaultMinute_IsNotSlow()
    {
        DateTime start = new(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);
        var windows = new List<FaultWindow>
        {
            new() { Kind = "kill", Target = "camus1", StartUtc = start.AddSeconds(10), EndUtc = start.AddSeconds(20) },
        };

        WindowImpact w = FaultCorrelator.Analyze(CollapsingTail(start), windows, 0.9, HostSamples(start, slowFromSecond: null)).Windows.Single();

        Assert.That(w.Device!.PreFaultSlow, Is.False);
        Assert.That(w.HeldRuleInadmissible, Is.False, "a collapse on a steady fast device is judged (run lk2)");
        Assert.That(w.HeldRuleVoidReason, Is.Null);
    }

    [Test]
    public void DeviceRegime_ATailThatCollapsedWhenTheHostDeviceSlowed_IsMarkedMoved()
    {
        // Run lk11: the tail fell to 60% in the same 5-second sample where the NVMe went from 34% to
        // 81% busy and one fsync from 0.04 to 1.9 ms. The held rule is right about the numbers and
        // wrong about the cause; the window must say the device moved.
        DateTime start = new(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);
        var windows = new List<FaultWindow>
        {
            new() { Kind = "kill", Target = "camus1", StartUtc = start.AddSeconds(10), EndUtc = start.AddSeconds(20) },
        };

        WindowImpact w = FaultCorrelator.Analyze(CollapsingTail(start), windows, 0.9, HostSamples(start, slowFromSecond: 40)).Windows.Single();

        Assert.That(w.ThroughputHeld, Is.False, "the numbers did fall");
        Assert.That(w.Device, Is.Not.Null);
        Assert.That(w.Device!.PreFaultFsyncP50Ms, Is.EqualTo(0.05).Within(1e-9), "the minute before the injection ran on the fast device");
        Assert.That(w.Device.PostHealFsyncP50Ms, Is.EqualTo(1.9).Within(1e-9), "the tail mostly ran on the slow one");
        Assert.That(w.Device.PreFaultUtilPercent, Is.EqualTo(35).Within(1e-9));
        Assert.That(w.Device.PostHealUtilPercent, Is.EqualTo(85).Within(1e-9));
        Assert.That(w.Device.Moved, Is.True, "so the held rule is inadmissible on this window");
    }

    [Test]
    public void DeviceRegime_ATailThatCollapsedOnASteadyDevice_IsNotExcused()
    {
        DateTime start = new(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);
        var windows = new List<FaultWindow>
        {
            new() { Kind = "kill", Target = "camus1", StartUtc = start.AddSeconds(10), EndUtc = start.AddSeconds(20) },
        };

        WindowImpact w = FaultCorrelator.Analyze(CollapsingTail(start), windows, 0.9, HostSamples(start, slowFromSecond: null)).Windows.Single();

        Assert.That(w.ThroughputHeld, Is.False);
        Assert.That(w.Device, Is.Not.Null);
        Assert.That(w.Device!.Moved, Is.False, "same device before and after: the collapse is the cluster's (run lk2)");
    }

    [Test]
    public void DeviceRegime_IsOnlyCalledWithSamplesOnBothSides()
    {
        DateTime start = new(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);
        var windows = new List<FaultWindow>
        {
            new() { Kind = "kill", Target = "camus1", StartUtc = start.AddSeconds(10), EndUtc = start.AddSeconds(20) },
        };
        IntervalSeries series = CollapsingTail(start);

        Assert.That(FaultCorrelator.Analyze(series, windows, 0.9).Windows.Single().Device, Is.Null, "no host samples at all");
        Assert.That(FaultCorrelator.Analyze(series, windows, 0.9, []).Windows.Single().Device, Is.Null, "an empty series");

        // Two pre-fault samples only (seconds 6 and 8): too few to call a median on that side.
        List<HostIoSample> sparse = HostSamples(start, null).Where(s => s.Utc >= start.AddSeconds(10) || s.Utc >= start.AddSeconds(6)).ToList();
        Assert.That(FaultCorrelator.Analyze(series, windows, 0.9, sparse).Windows.Single().Device, Is.Null, "fewer than three samples on one side");
    }

    [Test]
    public void DeviceRegime_AMoveUnderAHeldRecovery_IsReportedNotJudged()
    {
        // The device slowed but the cluster held its rate anyway: the window passes and carries the
        // move as information, so a reader can see the tail was measured on a slower device.
        DateTime start = new(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);
        IntervalSeries series = BuildSeries(start, out DateTime injectAt, out DateTime healAt);
        var windows = new List<FaultWindow> { new() { Kind = "kill", Target = "camus2", StartUtc = injectAt, EndUtc = healAt } };

        WindowImpact w = FaultCorrelator.Analyze(series, windows, 0.9, HostSamples(start, slowFromSecond: 25)).Windows.Single();

        Assert.That(w.ThroughputHeld, Is.True);
        Assert.That(w.Device!.Moved, Is.True);
    }

    [Test]
    public void ThroughputGate_ToleratesABriefDip_ButNotASustainedOne()
    {
        static IntervalSeries Build(int dipSeconds)
        {
            DateTime start = new(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);
            string dir = Path.Combine(Path.GetTempPath(), "caraxes-corr-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "run-meta.json"), "{ \"measureStartUtc\": \"" + start.ToString("O") + "\" }");
            List<string> rows = ["second,offered,started,completed,failed,in_flight,schedule_drops,read_p50_ms,read_p95_ms,read_p99_ms,write_p50_ms,write_p95_ms,write_p99_ms"];
            for (int s = 0; s < 240; s++)
            {
                // fault 10-20; recovered by 25; a dip to 50% from 60 for dipSeconds; 100 otherwise
                long completed = s is >= 10 and <= 20 ? 30 : (s >= 60 && s < 60 + dipSeconds) ? 50 : 100;
                long failed = s is >= 10 and <= 20 ? 20 : 0;
                rows.Add($"{s},100,100,{completed},{failed},4,0,1,2,3,5,10,10");
            }
            File.WriteAllText(Path.Combine(dir, "intervals.csv"), string.Join('\n', rows));
            IntervalSeries series = IntervalSeries.Load(dir)!;
            Directory.Delete(dir, true);
            return series;
        }

        DateTime start = new(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);
        var windows = new List<FaultWindow>
        {
            new() { Kind = "kill", Target = "camus1", StartUtc = start.AddSeconds(10), EndUtc = start.AddSeconds(20) },
        };

        WindowImpact brief = FaultCorrelator.Analyze(Build(10), windows, 0.9).Windows.Single();
        Assert.That(brief.ThroughputHeld, Is.True, "a 10-second dip is closed-loop noise");
        Assert.That(brief.LongestPostRecoveryDipSeconds, Is.LessThan(30));

        WindowImpact sustained = FaultCorrelator.Analyze(Build(45), windows, 0.9).Windows.Single();
        Assert.That(sustained.ThroughputHeld, Is.False, "45 seconds at half speed is a degraded cluster");
        Assert.That(sustained.LongestPostRecoveryDipSeconds, Is.GreaterThanOrEqualTo(30));
    }

    [Test]
    public void ThroughputGate_CatchesACleanButSlowRecovery_AndCanBeDisabled()
    {
        // Errors vanish at the heal, but the cluster serves only 40% of its prior rate for the rest of
        // the series: the error-rate rule says "recovered", the throughput rule must not.
        DateTime start = new(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);
        string dir = Path.Combine(Path.GetTempPath(), "caraxes-corr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "run-meta.json"), "{ \"measureStartUtc\": \"" + start.ToString("O") + "\" }");
        List<string> rows = ["second,offered,started,completed,failed,in_flight,schedule_drops,read_p50_ms,read_p95_ms,read_p99_ms,write_p50_ms,write_p95_ms,write_p99_ms"];
        for (int s = 0; s < 60; s++)
        {
            long completed = s < 10 ? 100 : s <= 20 ? 30 : 40;
            long failed = s is >= 10 and <= 20 ? 20 : 0;
            rows.Add($"{s},100,100,{completed},{failed},4,0,1,2,3,5,10,10");
        }
        File.WriteAllText(Path.Combine(dir, "intervals.csv"), string.Join('\n', rows));
        IntervalSeries series = IntervalSeries.Load(dir)!;
        Directory.Delete(dir, true);

        var windows = new List<FaultWindow>
        {
            new() { Kind = "kill", Target = "camus1", StartUtc = start.AddSeconds(10), EndUtc = start.AddSeconds(20) },
        };

        WindowImpact gated = FaultCorrelator.Analyze(series, windows, 0.9).Windows.Single();
        Assert.That(gated.Recovered, Is.True, "error rate is clean right after the heal");
        Assert.That(gated.RecoverySeconds, Is.EqualTo(1).Within(0.001));
        Assert.That(gated.PreFaultThroughput, Is.EqualTo(100).Within(0.001));
        Assert.That(gated.ThroughputRecovered, Is.False, "40% of the prior rate is not a recovery");
        Assert.That(gated.ThroughputRecoverySeconds, Is.Null);

        WindowImpact lenient = FaultCorrelator.Analyze(series, windows, 0.35).Windows.Single();
        Assert.That(lenient.ThroughputRecovered, Is.True, "a 35% bar is met by 40%");

        WindowImpact off = FaultCorrelator.Analyze(series, windows, 0).Windows.Single();
        Assert.That(off.ThroughputRecovered, Is.True, "0 disables the gate");
        Assert.That(off.ThroughputRecoverySeconds, Is.Null);
    }

    [Test]
    public void TotalOutageIsDetected()
    {
        DateTime start = new(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);
        string dir = Path.Combine(Path.GetTempPath(), "caraxes-outage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "run-meta.json"), $$"""{ "measureStartUtc": "{{start:O}}" }""");
            File.WriteAllText(Path.Combine(dir, "intervals.csv"), string.Join('\n',
                "second,offered,started,completed,failed,in_flight,schedule_drops,read_p50_ms,read_p95_ms,read_p99_ms,write_p50_ms,write_p95_ms,write_p99_ms",
                "0,100,100,100,0,4,0,1,2,3,5,10,10",
                "1,100,100,0,100,4,0,1,2,3,5,10,10",   // total outage: 0 completed
                "2,100,100,100,0,4,0,1,2,3,5,10,10"));

            IntervalSeries series = IntervalSeries.Load(dir)!;
            var windows = new List<FaultWindow>
            {
                new() { Kind = "partition", Target = "camus1", StartUtc = start.AddSeconds(1), EndUtc = start.AddSeconds(1) },
            };

            WindowImpact w = FaultCorrelator.Analyze(series, windows).Windows.Single();
            Assert.That(w.WorkloadProgressed, Is.False, "second 1 completed zero ops → total outage");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void HealedButNeverRecovered_YieldsFiniteSerializableRecovery()
    {
        // A fault that heals but whose effect outlasts the measured run (e.g. a full disk that drains
        // only after the workload stops) never returns to the recovered threshold. MaxRecoverySeconds
        // must stay finite and JSON-serializable — the never-recovered case is carried by
        // AllHealedFaultsRecovered, not by an infinite sentinel that System.Text.Json cannot write.
        DateTime start = new(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);
        string dir = Path.Combine(Path.GetTempPath(), "caraxes-noreco-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "run-meta.json"), $$"""{ "measureStartUtc": "{{start:O}}" }""");

            List<string> rows =
            [
                "second,offered,started,completed,failed,in_flight,schedule_drops,read_p50_ms,read_p95_ms,read_p99_ms,write_p50_ms,write_p95_ms,write_p99_ms"
            ];
            // Clean until the inject at second 2; errored from the fault onward and never clearing —
            // the heal at second 4 is followed only by still-errored seconds until the series ends.
            for (int s = 0; s < 10; s++)
            {
                bool errored = s >= 2;
                long failed = errored ? 80 : 0;
                long completed = errored ? 20 : 100; // still progressing, just heavily errored
                rows.Add($"{s},100,100,{completed},{failed},4,0,1,2,3,5,10,10");
            }
            File.WriteAllText(Path.Combine(dir, "intervals.csv"), string.Join('\n', rows));

            IntervalSeries series = IntervalSeries.Load(dir)!;
            var windows = new List<FaultWindow>
            {
                new() { Kind = "fill-disk", Target = "camus2", StartUtc = start.AddSeconds(2), EndUtc = start.AddSeconds(4) },
            };

            FaultAnalysis a = FaultCorrelator.Analyze(series, windows);

            WindowImpact w = a.Windows.Single();
            Assert.That(w.Healed, Is.True);
            Assert.That(w.Recovered, Is.False, "errors never clear before the series ends");
            Assert.That(w.RecoverySeconds, Is.Null);
            Assert.That(a.AllHealedFaultsRecovered, Is.False, "the never-recovered case is signaled here");
            Assert.That(double.IsFinite(a.MaxRecoverySeconds), Is.True, "must stay finite");
            Assert.That(a.MaxRecoverySeconds, Is.EqualTo(0), "no window recovered → no recovery time to report");

            // The whole analysis must round-trip through System.Text.Json without throwing.
            Assert.DoesNotThrow(() => System.Text.Json.JsonSerializer.Serialize(a));
        }
        finally { Directory.Delete(dir, true); }
    }
}
