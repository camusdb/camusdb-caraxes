/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Globalization;
using NUnit.Framework;
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
