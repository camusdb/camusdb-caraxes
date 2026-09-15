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
    bool Recovered,
    double PreFaultThroughput,
    double InWindowThroughput,
    double? ThroughputRecoverySeconds,
    bool ThroughputRecovered,
    bool ThroughputHeld,
    double PostRecoveryMedianThroughput,
    int LongestPostRecoveryDipSeconds);

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

    /// <summary>Clean seconds before a window that establish its pre-fault throughput (median).</summary>
    private const int PreFaultSeconds = 60;

    /// <summary>Trailing seconds averaged when judging whether throughput is back: one second is
    /// too noisy (a closed loop completes in bursts), a longer window would hide a slow return.</summary>
    private const int ThroughputWindowSeconds = 5;

    /// <summary>A recovery "holds" when the post-recovery trailing means have a median at or above
    /// the bar and never stay below it for this many consecutive clean seconds. A healthy closed loop
    /// dips under 90% of its own median on a fifth of its 5-second windows (measured on run lk3), so a
    /// single dip must not fail a run; a tail that sits at half speed for minutes (run lk2) must.</summary>
    private const int MaxHeldDipSeconds = 30;

    /// <param name="recoveredThroughputFraction">Share of the pre-fault throughput a healed window must
    /// regain (trailing 5-second mean) to count as recovered on throughput; 0 disables that judgement.
    /// The error-rate recovery is always computed.</param>
    public static FaultAnalysis Analyze(
        IntervalSeries series, IReadOnlyList<FaultWindow> windows, double recoveredThroughputFraction = 0.9)
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
            impacts.Add(AnalyzeWindow(series, w, seriesEnd, InAnyWindow, recoveredThroughputFraction));

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

    private static WindowImpact AnalyzeWindow(
        IntervalSeries series, FaultWindow w, DateTime seriesEnd, Func<DateTime, bool> inAnyWindow, double throughputFraction)
    {
        DateTime end = w.EndUtc ?? seriesEnd;

        List<IntervalPoint> during = series.Points
            .Where(p => p.AbsoluteUtc >= w.StartUtc && p.AbsoluteUtc <= end)
            .ToList();

        double peakErr = during.Count == 0 ? 0 : during.Max(p => p.ErrorRate);
        long failed = during.Sum(p => p.Failed);
        bool progressed = during.Count == 0 || during.Any(p => p.Completed > 0);
        double inWindowThroughput = Mean(during.Select(p => (double)p.Completed));

        // Pre-fault throughput: the median of the clean seconds in the minute before the injection.
        // Median, for the same reason as the error baseline — a previous fault's recovery tail must
        // not drag the bar down. Clean-only, so back-to-back faults judge against real service.
        DateTime preStart = w.StartUtc.AddSeconds(-PreFaultSeconds);
        double preFault = Median(series.Points
            .Where(p => p.AbsoluteUtc >= preStart && p.AbsoluteUtc < w.StartUtc && !inAnyWindow(p.AbsoluteUtc))
            .Select(p => (double)p.Completed));

        double? recovery = null;
        bool recovered = false;
        double? throughputRecovery = null;
        bool throughputRecovered = throughputFraction <= 0;
        bool throughputHeld = true;
        double postRecoveryMedian = 0;
        int longestDip = 0;

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

            // Throughput recovery: first second at or after the heal whose trailing mean of completed
            // ops is back to the required share of the pre-fault median. A cluster whose error rate
            // is clean but which serves a third of its prior rate (run U's read-only wedges, a
            // replica being waited on beyond quorum) is caught here and nowhere else.
            if (throughputFraction > 0 && preFault > 0)
            {
                List<IntervalPoint> ordered = series.Points.OrderBy(p => p.AbsoluteUtc).ToList();
                for (int i = 0; i < ordered.Count; i++)
                {
                    if (ordered[i].AbsoluteUtc < w.EndUtc!.Value)
                        continue;
                    int from = Math.Max(0, i - ThroughputWindowSeconds + 1);
                    double trailing = Mean(ordered.Skip(from).Take(i - from + 1).Select(p => (double)p.Completed));
                    if (trailing >= throughputFraction * preFault)
                    {
                        throughputRecovery = Math.Max(0, (ordered[i].AbsoluteUtc - w.EndUtc!.Value).TotalSeconds);
                        throughputRecovered = true;

                        // A recovery has to hold. Run lk2 (2026-09-15) crossed the bar 15.7 s after a
                        // restart, then fell to 45% of pre-fault for the last three and a half minutes
                        // of the window; a single crossing must not pass that. From the crossing to the
                        // next fault window (or the series end), the trailing mean must stay above the
                        // bar on clean seconds — a later fault's own window is judged by that fault.
                        List<double> post = [];
                        int dip = 0;
                        for (int j = i + 1; j < ordered.Count; j++)
                        {
                            if (inAnyWindow(ordered[j].AbsoluteUtc))
                                break;
                            int f = Math.Max(0, j - ThroughputWindowSeconds + 1);
                            double t = Mean(ordered.Skip(f).Take(j - f + 1).Select(p => (double)p.Completed));
                            post.Add(t);
                            dip = t < throughputFraction * preFault ? dip + 1 : 0;
                            longestDip = Math.Max(longestDip, dip);
                        }
                        if (post.Count > 0)
                        {
                            postRecoveryMedian = Median(post);
                            throughputHeld = postRecoveryMedian >= throughputFraction * preFault && longestDip < MaxHeldDipSeconds;
                        }
                        break;
                    }
                }
            }
            else if (throughputFraction > 0)
            {
                // Nothing was being served before the fault: there is no rate to regain.
                throughputRecovered = true;
            }
        }

        return new WindowImpact(
            w.Label, w.Healed, (end - w.StartUtc).TotalSeconds, peakErr, failed, progressed, recovery, recovered,
            preFault, inWindowThroughput, throughputRecovery, throughputRecovered, throughputHeld,
            postRecoveryMedian, longestDip);
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
