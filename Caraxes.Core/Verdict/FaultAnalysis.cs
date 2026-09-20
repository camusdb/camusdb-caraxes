/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using Caraxes.Core.Cluster;

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
    int LongestPostRecoveryDipSeconds,
    DeviceRegime? Device = null,
    DeviceRegime? DipDevice = null,
    DeviceRegime? RecoveryDevice = null)
{
    /// <summary>
    /// The held rule cannot be judged when the device moved over the tail as a whole or over the very
    /// seconds of the longest dip. Run lk13 (2026-09-16): a 150-s tail held for two minutes and then
    /// fell for its last 37 s, in the sample where the NVMe went from 39% to 75% busy; the tail's
    /// medians (0.21 ms, 42%) still read steady, so the whole-tail test alone graded a device move as
    /// a cluster fault.
    /// </summary>
    public bool HeldRuleInadmissible => HeldRuleVoidReason is not null;

    /// <summary>The regain-time rule is judged on the device the recovery ran on: the span from the
    /// heal to the crossing when it is long enough to sample, otherwise the tail.</summary>
    public bool RegainRuleInadmissible => RegainRuleVoidReason is not null;

    /// <summary>The regime that voids the held rule, for the verdict line: the dip's when the tail's
    /// own medians did not move, else the tail's.</summary>
    public DeviceRegime? HeldRuleMover => Device?.Moved == true ? Device : DipDevice?.Moved == true ? DipDevice : null;

    /// <summary>Why the held rule is not judged on this window, as a verdict phrase; null when it is.
    /// A bar set on the device's slow regime voids the rule too (<see cref="DeviceRegime.PreFaultSlow"/>):
    /// sd11 and sd12 (2026-09-15/16) both drifted down through their slow-regime minutes with nothing
    /// changing on the nodes, so a pre-fault median taken there is not a bar the tail can be held to.</summary>
    public string? HeldRuleVoidReason =>
        HeldRuleMover is { } mover ? $"the host device left its regime ({mover.DescribeSpan()})"
        : Device?.PreFaultSlow == true ? $"the bar was set on the host device's slow regime ({Device.DescribePre()})"
        : null;

    /// <summary>Why the regain-time rule is not judged on this window; null when it is.</summary>
    public string? RegainRuleVoidReason =>
        (RecoveryDevice ?? Device) is { Moved: true } span ? $"the host device left its regime ({span.DescribeSpan()})"
        : Device?.PreFaultSlow == true ? $"the bar was set on the host device's slow regime ({Device.DescribePre()})"
        : null;
}

/// <summary>
/// The host block device's regime on either side of one fault: the median fsync cost and utilisation
/// over the clean minute before the injection, and over the tail from the heal to the next fault or
/// the series end. <see cref="Moved"/> says the tail was measured on a different device than the
/// bar was set on, which makes the held-throughput rule unjudgeable for that window.
///
/// <para>Why. Run lk11 (2026-09-15) regained its pre-fault rate 20 s after a leader restart, held it
/// for a minute, then fell to 60% for the last 210 s — in the same 5-second sample where the host
/// NVMe went from 34% to 81% busy and one fsync from 0.04 to 1.9 ms, and stayed there. Nothing in the
/// cluster had changed. Read as a database fault, that tail sends a false finding upstream; read as a
/// device regime move it is simply not a measurement of the recovery. sd11 and lk10 before it had the
/// same shape with the run-wide fsync median hiding the flip.</para>
/// </summary>
public sealed record DeviceRegime(
    double PreFaultFsyncP50Ms,
    double PostHealFsyncP50Ms,
    double PreFaultUtilPercent,
    double PostHealUtilPercent,
    int PreFaultSamples,
    int PostHealSamples,
    string Span = "the tail")
{
    /// <summary>The fsync median must grow by this factor to count as a regime move …</summary>
    public const double FsyncMoveFactor = 3.0;

    /// <summary>… and reach this absolute cost: a fast NVMe's 0.03 → 0.10 ms is jitter, not a regime.</summary>
    public const double MinMovedFsyncMs = 0.5;

    /// <summary>Alternatively, utilisation up by this many points at a rate the run did not raise.</summary>
    public const double UtilMovePoints = 30.0;

    /// <summary>A pre-fault minute whose fsync median is at or above this is in the device's slow regime
    /// (this host's fast regime is 0.03-0.05 ms, its mixed minutes 0.4, its slow one 1-2 ms) …</summary>
    public const double SlowRegimeFsyncMs = 1.0;

    /// <summary>… or whose utilisation is at or above this (fast-regime minutes run at 35-45% busy at
    /// the same write volume; slow ones at 73-90%).</summary>
    public const double SlowRegimeUtilPercent = 70.0;

    /// <summary>The bar was set while the device was already in its slow regime. The throughput rules
    /// are not judged on such a window: on this host the slow regime is not stationary — the rate
    /// drifts down through it with nothing changing on the nodes — so a pre-fault median taken there
    /// is not a bar the tail can be held to (sd11 pause 1, sd12 pause 3, lk13 kill 2).</summary>
    public bool PreFaultSlow => PreFaultFsyncP50Ms >= SlowRegimeFsyncMs || PreFaultUtilPercent >= SlowRegimeUtilPercent;

    public bool Moved =>
        PostHealFsyncP50Ms >= Math.Max(MinMovedFsyncMs, FsyncMoveFactor * PreFaultFsyncP50Ms)
        || PostHealUtilPercent >= PreFaultUtilPercent + UtilMovePoints;

    /// <summary>One phrase for a verdict line: "fsync p50 0.04→1.92 ms, util 34%→85%".</summary>
    public string Describe() =>
        $"fsync p50 {PreFaultFsyncP50Ms:0.00}→{PostHealFsyncP50Ms:0.00} ms, util {PreFaultUtilPercent:0}%→{PostHealUtilPercent:0}%";

    /// <summary>The same phrase naming the span the post side was measured over: "… over the 37-s dip".</summary>
    public string DescribeSpan() => $"{Describe()} over {Span}";

    /// <summary>The pre-fault side alone: "pre-fault fsync p50 1.13 ms, util 80%".</summary>
    public string DescribePre() => $"pre-fault fsync p50 {PreFaultFsyncP50Ms:0.00} ms, util {PreFaultUtilPercent:0}%";
}

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

    /// <summary>Fewer host samples than this on either side of a fault and the device regime is not judged.</summary>
    private const int MinRegimeSamples = 3;

    /// <param name="recoveredThroughputFraction">Share of the pre-fault throughput a healed window must
    /// regain (trailing 5-second mean) to count as recovered on throughput; 0 disables that judgement.
    /// The error-rate recovery is always computed.</param>
    /// <param name="hostIo">The host device samples taken alongside the run (<c>host-io.csv</c>), or
    /// null/empty when none were; with them each healed window carries a <see cref="DeviceRegime"/>.</param>
    public static FaultAnalysis Analyze(
        IntervalSeries series, IReadOnlyList<FaultWindow> windows, double recoveredThroughputFraction = 0.9,
        IReadOnlyList<HostIoSample>? hostIo = null)
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
            impacts.Add(AnalyzeWindow(series, w, windows, seriesEnd, InAnyWindow, recoveredThroughputFraction, hostIo));

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
        IntervalSeries series, FaultWindow w, IReadOnlyList<FaultWindow> windows, DateTime seriesEnd,
        Func<DateTime, bool> inAnyWindow, double throughputFraction, IReadOnlyList<HostIoSample>? hostIo)
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
        DateTime? regainUtc = null;
        DateTime? longestDipStart = null;
        DateTime? longestDipEnd = null;

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
                        regainUtc = ordered[i].AbsoluteUtc;

                        // A recovery has to hold. Run lk2 (2026-09-15) crossed the bar 15.7 s after a
                        // restart, then fell to 45% of pre-fault for the last three and a half minutes
                        // of the window; a single crossing must not pass that. From the crossing to the
                        // next fault window (or the series end), the trailing mean must stay above the
                        // bar on clean seconds — a later fault's own window is judged by that fault.
                        List<double> post = [];
                        int dip = 0;
                        DateTime? dipStart = null;
                        for (int j = i + 1; j < ordered.Count; j++)
                        {
                            if (inAnyWindow(ordered[j].AbsoluteUtc))
                                break;
                            int f = Math.Max(0, j - ThroughputWindowSeconds + 1);
                            double t = Mean(ordered.Skip(f).Take(j - f + 1).Select(p => (double)p.Completed));
                            post.Add(t);
                            if (t < throughputFraction * preFault)
                            {
                                // The trailing mean lags the drop by up to its window, so the dip's
                                // first second is the window's first second, not the crossing's.
                                dipStart ??= ordered[f].AbsoluteUtc;
                                dip++;
                                if (dip > longestDip)
                                {
                                    longestDip = dip;
                                    longestDipStart = dipStart;
                                    longestDipEnd = ordered[j].AbsoluteUtc;
                                }
                            }
                            else
                            {
                                dip = 0;
                                dipStart = null;
                            }
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

        DeviceRegime? device = null;
        DeviceRegime? dipDevice = null;
        DeviceRegime? recoveryDevice = null;
        if (w.Healed)
        {
            DateTime tailEnd = windows
                .Where(o => o.StartUtc > w.EndUtc!.Value)
                .Select(o => o.StartUtc)
                .DefaultIfEmpty(seriesEnd)
                .Min();
            device = DeviceRegimeOver(w, w.EndUtc!.Value, tailEnd, "the tail", hostIo);
            if (!throughputHeld && longestDipStart is not null && longestDipEnd is not null)
                dipDevice = DeviceRegimeOver(w, longestDipStart.Value, longestDipEnd.Value, $"the {longestDip}-s dip", hostIo);
            if (regainUtc is not null && throughputRecovery > 0)
                recoveryDevice = DeviceRegimeOver(w, w.EndUtc!.Value, regainUtc.Value, $"the {throughputRecovery:N0}-s recovery", hostIo);
        }

        return new WindowImpact(
            w.Label, w.Healed, (end - w.StartUtc).TotalSeconds, peakErr, failed, progressed, recovery, recovered,
            preFault, inWindowThroughput, throughputRecovery, throughputRecovered, throughputHeld,
            postRecoveryMedian, longestDip, device, dipDevice, recoveryDevice);
    }

    /// <summary>
    /// The device on either side of a healed window: the samples in the clean minute before the
    /// injection against the samples over <paramref name="postFrom"/>–<paramref name="postTo"/> — the
    /// whole tail to the next fault's injection or the series end, the longest dip, or the recovery
    /// span. Null without enough samples on both sides to call a median.
    /// </summary>
    private static DeviceRegime? DeviceRegimeOver(
        FaultWindow w, DateTime postFrom, DateTime postTo, string span, IReadOnlyList<HostIoSample>? hostIo)
    {
        if (hostIo is null || hostIo.Count == 0 || w.EndUtc is null)
            return null;

        DateTime preStart = w.StartUtc.AddSeconds(-PreFaultSeconds);

        List<HostIoSample> pre = hostIo.Where(s => s.Utc >= preStart && s.Utc < w.StartUtc && s.FsyncMs >= 0).ToList();
        List<HostIoSample> post = hostIo.Where(s => s.Utc >= postFrom && s.Utc <= postTo && s.FsyncMs >= 0).ToList();

        if (pre.Count < MinRegimeSamples || post.Count < MinRegimeSamples)
            return null;

        return new DeviceRegime(
            Median(pre.Select(s => s.FsyncMs)),
            Median(post.Select(s => s.FsyncMs)),
            Median(pre.Select(s => s.UtilPercent)),
            Median(post.Select(s => s.UtilPercent)),
            pre.Count,
            post.Count,
            span);
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
