/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

namespace Caraxes.Core.Verdict;

/// <summary>Per-fault-window impact and recovery, measured against the workload's per-second series.</summary>
public sealed record WindowImpact(
    string Label,
    bool Healed,
    double DurationSeconds,
    double PeakErrorRate,
    long FailedDuringWindow,
    bool WorkloadProgressed,
    double? RecoverySeconds,
    bool Recovered);

/// <summary>
/// The correlation of a nemesis timeline with the workload's per-second series: what each fault did
/// to error rate and latency, and how long the cluster took to recover after each heal. This is the
/// resilience signal a chaos run exists to produce — a run can be "consistent" (reconciliation held)
/// yet still fail its purpose if it took two minutes to serve traffic again after a follower died.
/// </summary>
public sealed record FaultAnalysis(
    double BaselineErrorRate,
    double BaselineWriteP99Ms,
    double InFaultErrorRate,
    double InFaultWriteP99Ms,
    double MaxRecoverySeconds,
    bool AllHealedFaultsRecovered,
    IReadOnlyList<WindowImpact> Windows)
{
    public double LatencyInflation => BaselineWriteP99Ms > 0 ? InFaultWriteP99Ms / BaselineWriteP99Ms : 0;
}

/// <summary>
/// Overlays fault windows on the interval series to produce a <see cref="FaultAnalysis"/>. Seconds are
/// classified as in-fault (inside any window) or clean; "clean" establishes the baseline the in-fault
/// numbers and recovery are judged against. Recovery for a healed window is the time from its heal
/// until the first second whose error rate falls back to the recovered threshold.
/// </summary>
public static class FaultCorrelator
{
    /// <summary>A second is "recovered" once its error rate is at or below this (near-zero, allowing a
    /// stray retry). Kept absolute rather than relative to baseline so a clean run's ~0 baseline does
    /// not make the bar unreachably tight.</summary>
    private const double RecoveredErrorRate = 0.01;

    public static FaultAnalysis Analyze(IntervalSeries series, IReadOnlyList<FaultWindow> windows)
    {
        DateTime seriesEnd = series.Points.Count > 0 ? series.Points[^1].AbsoluteUtc : series.MeasureStartUtc;

        bool InAnyWindow(DateTime t) =>
            windows.Any(w => t >= w.StartUtc && t <= (w.EndUtc ?? seriesEnd));

        List<IntervalPoint> clean = series.Points.Where(p => !InAnyWindow(p.AbsoluteUtc)).ToList();
        List<IntervalPoint> inFault = series.Points.Where(p => InAnyWindow(p.AbsoluteUtc)).ToList();

        // Baseline uses the MEDIAN of clean seconds, not the mean: the error/latency tail after a heal
        // sits outside the fault window (it is the recovery we are measuring) and would inflate a mean
        // baseline, making the in-fault comparison and the "recovered" bar meaningless. A genuinely
        // quiet run has a median of ~0 regardless of a few recovery-tail spikes. In-fault stays a mean
        // because there we want the whole window's degradation, not just its typical second.
        double baselineErr = Median(clean.Select(p => p.ErrorRate));
        double baselineP99 = Median(clean.Select(p => p.WriteP99Ms));
        double inFaultErr = Mean(inFault.Select(p => p.ErrorRate));
        double inFaultP99 = Mean(inFault.Select(p => p.WriteP99Ms));

        List<WindowImpact> impacts = [];
        foreach (FaultWindow w in windows)
            impacts.Add(AnalyzeWindow(series, w, seriesEnd));

        List<WindowImpact> healed = impacts.Where(i => i.Healed).ToList();

        // Max over the windows that actually recovered, so this stays finite and serializable. A window
        // that healed but never returned to baseline before the series ended contributes no recovery
        // time here — that case is carried separately by allRecovered (and per-window by Recovered), so
        // it does not need an infinite sentinel that System.Text.Json cannot write anyway. A sustained
        // fault whose effect outlasts the measured run (e.g. a full disk that drains after the workload
        // stops) lands here: allRecovered is false, maxRecovery reflects only the recovered windows.
        double maxRecovery = healed
            .Where(i => i.Recovered)
            .Select(i => i.RecoverySeconds!.Value)
            .DefaultIfEmpty(0)
            .Max();
        bool allRecovered = healed.All(i => i.Recovered);

        return new FaultAnalysis(baselineErr, baselineP99, inFaultErr, inFaultP99, maxRecovery, allRecovered, impacts);
    }

    private static WindowImpact AnalyzeWindow(IntervalSeries series, FaultWindow w, DateTime seriesEnd)
    {
        DateTime end = w.EndUtc ?? seriesEnd;

        List<IntervalPoint> during = series.Points
            .Where(p => p.AbsoluteUtc >= w.StartUtc && p.AbsoluteUtc <= end)
            .ToList();

        double peakErr = during.Count == 0 ? 0 : during.Max(p => p.ErrorRate);
        long failed = during.Sum(p => p.Failed);
        bool progressed = during.Count == 0 || during.Any(p => p.Completed > 0);

        double? recovery = null;
        bool recovered = false;

        if (w.Healed)
        {
            // First second at or after the heal whose error rate is back to the recovered threshold.
            IntervalPoint? first = series.Points
                .Where(p => p.AbsoluteUtc >= w.EndUtc!.Value)
                .OrderBy(p => p.AbsoluteUtc)
                .FirstOrDefault(p => p.ErrorRate <= RecoveredErrorRate);

            if (first is not null)
            {
                recovery = Math.Max(0, (first.AbsoluteUtc - w.EndUtc!.Value).TotalSeconds);
                recovered = true;
            }
            // No such second before the series ended → never observed to recover.
        }

        return new WindowImpact(w.Label, w.Healed, (end - w.StartUtc).TotalSeconds, peakErr, failed, progressed, recovery, recovered);
    }

    private static double Mean(IEnumerable<double> values)
    {
        double sum = 0;
        int n = 0;
        foreach (double v in values)
        {
            sum += v;
            n++;
        }
        return n == 0 ? 0 : sum / n;
    }

    private static double Median(IEnumerable<double> values)
    {
        List<double> sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0)
            return 0;
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
