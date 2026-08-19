/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Text.Json;
using Caraxes.Core.Cluster;
using Caraxes.Core.Nemesis;
using Caraxes.Core.Verdict;
using Caraxes.Core.Workload;

namespace Caraxes.Core.Scenario;

/// <summary>Whole-scenario outcome: the workload's own exit-code verdict plus the harness's read
/// of the collected artifacts, so a caller (and the process exit code) can act on one value.</summary>
public sealed record ScenarioVerdict(
    bool Passed,
    int WorkloadExitCode,
    bool SummaryValid,
    bool ReconciliationPassed,
    string RunDirectory,
    IReadOnlyList<string> Notes)
{
    /// <summary>Fault-window correlation for a run that had a nemesis; null for a fault-free baseline.</summary>
    public Caraxes.Core.Verdict.FaultAnalysis? Analysis { get; init; }
}

/// <summary>
/// Runs a scenario end to end: stand the cluster up, seed the dataset, drive the measured
/// workload, collect its artifacts, render a verdict, and (by default) tear the cluster down.
/// Teardown runs even when the workload fails — a scenario that leaves a fleet running on every
/// failure would exhaust a laptop across a matrix run — unless <c>teardown: false</c> asked to
/// keep it for inspection.
/// </summary>
public sealed class ScenarioRunner
{
    private readonly ScenarioSpec scenario;

    private readonly string runDir;

    private readonly ClusterOrchestrator orchestrator;

    public ScenarioRunner(ScenarioSpec scenario, string? runRoot = null)
    {
        this.scenario = scenario;
        string root = runRoot ?? Path.Combine(Environment.CurrentDirectory, "runs");
        runDir = Path.Combine(root, "scenarios", scenario.Name);
        orchestrator = new ClusterOrchestrator(scenario.Cluster, Path.Combine(root, "clusters"));
    }

    public async Task<ScenarioVerdict> RunAsync(bool skipBuild = false, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(runDir);
        List<string> notes = [];

        ClusterPlan plan = orchestrator.Plan;
        WorkloadRunner workload = new(plan, Path.Combine(runDir, "workload-bin"));
        string artifactsDir = Path.Combine(runDir, "artifacts");

        WorkloadInvocation? init = null;
        WorkloadInvocation? run = null;

        try
        {
            // Publish the workload while the image builds is not worth the concurrency here; keep
            // the flow linear and legible — a scenario is dominated by the measured window.
            await workload.EnsurePublishedAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            await orchestrator.UpAsync(skipBuild, cancellationToken: cancellationToken).ConfigureAwait(false);

            init = await workload.InitAsync(scenario, artifactsDir, cancellationToken).ConfigureAwait(false);
            if (!init.Ok)
            {
                notes.Add($"init failed (exit {init.ExitCode}); dataset was not seeded, so the run was skipped");
                return Finalize(new ScenarioVerdict(false, init.ExitCode, false, false, runDir, notes), init, run);
            }

            run = await RunWorkloadWithNemesisAsync(workload, plan, artifactsDir, notes, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (scenario.Teardown)
            {
                Console.WriteLine("==> tearing down cluster");
                try
                {
                    await orchestrator.DownAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    notes.Add($"teardown reported: {e.Message}");
                }
            }
            else
            {
                notes.Add("cluster left running (teardown: false); tear down with: caraxes down --spec <cluster>");
            }
        }

        return Finalize(BuildVerdict(run!, artifactsDir, notes), init, run);
    }

    /// <summary>
    /// Runs the measured workload and, if the scenario has a nemesis, drives its fault schedule
    /// concurrently. The nemesis is timed from the workload's launch; when the workload container
    /// exits, the nemesis is signalled to stop and heals everything still in effect before this
    /// returns — so the cluster is quiescent (if not torn down) by the time the verdict is read.
    /// </summary>
    private async Task<WorkloadInvocation> RunWorkloadWithNemesisAsync(
        WorkloadRunner workload, ClusterPlan plan, string artifactsDir, List<string> notes, CancellationToken cancellationToken)
    {
        if (scenario.Nemesis is null)
            return await workload.RunAsync(scenario, artifactsDir, "run", cancellationToken).ConfigureAwait(false);

        using HttpProbes probes = new();
        NemesisRunner nemesis = new(plan, probes);
        string timelinePath = Path.Combine(runDir, "timeline.jsonl");

        // The nemesis stops when the workload finishes; heal-on-stop runs inside NemesisRunner with a
        // fresh token, so cancelling here never leaves a fault un-healed.
        using CancellationTokenSource nemesisStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Task<WorkloadInvocation> workloadTask = workload.RunAsync(scenario, artifactsDir, "run", cancellationToken);
        Task nemesisTask = nemesis.RunAsync(scenario.Nemesis, timelinePath, nemesisStop.Token);

        WorkloadInvocation result;
        try
        {
            result = await workloadTask.ConfigureAwait(false);
        }
        finally
        {
            nemesisStop.Cancel();
            try
            {
                await nemesisTask.ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // The nemesis is best-effort decoration around the workload; its failure is a note,
                // never the thing that decides the verdict.
                notes.Add($"nemesis reported: {e.Message}");
            }
        }

        notes.Add($"nemesis timeline: {timelinePath}");
        return result;
    }

    private ScenarioVerdict BuildVerdict(WorkloadInvocation run, string artifactsDir, List<string> notes)
    {
        string outputDir = Path.Combine(artifactsDir, "run");

        WorkloadSummary? summary = WorkloadArtifacts.ReadSummary(outputDir);
        ReconciliationSummary? reconciliation = WorkloadArtifacts.ReadReconciliation(outputDir);

        if (summary is null)
        {
            notes.Add("no summary.json produced; the workload likely crashed before writing artifacts");
            return new ScenarioVerdict(false, run.ExitCode, false, false, runDir, notes);
        }

        bool reconciliationPassed = reconciliation?.Passed ?? false;

        notes.Add(
            $"completed {summary.Completed:N0} ops ({summary.AchievedOpsPerSec:N0}/s), " +
            $"{summary.Conflicts:N0} conflicts, {summary.Indeterminate:N0} indeterminate, " +
            $"{summary.Failed:N0} failed ({summary.DomainErrors} domain, {summary.InternalErrors} internal)");

        foreach (string warning in summary.ValidityWarnings)
            notes.Add($"validity warning: {warning}");

        if (reconciliation is { Passed: false })
            foreach (string failure in reconciliation.Failures)
                notes.Add($"reconciliation failure: {failure}");

        // A scenario passes when the workload accepted the run AND reconciliation held — that is the
        // real consistency guard (versions, row count, accounting all balanced). Internal errors get
        // graded by context: with NO fault injected, an internal error is unexplained and damns the
        // run (a server-side defect the workload's own validity does not catch); UNDER a fault, a few
        // client-side disposal races as connections are torn down are expected collateral, so they are
        // surfaced loudly but do not by themselves flip a consistent, reconciled run to FAIL.
        bool faultFree = scenario.Nemesis is null;
        bool internalErrorsOk = summary.InternalErrors == 0 || !faultFree;

        if (summary.InternalErrors > 0)
            notes.Add(faultFree
                ? $"{summary.InternalErrors} internal error(s) with no fault injected — fails the run; inspect errors.json"
                : $"{summary.InternalErrors} internal error(s) tolerated under fault (reconciliation still held); inspect errors.json");

        bool passed = run.Ok && summary.Valid && reconciliationPassed && internalErrorsOk;

        // Resilience: for a run with faults, correlate the nemesis timeline with the per-second series
        // and hold every fault to the scenario's recovery/availability rules. A run can be perfectly
        // consistent yet still fail its purpose (a follower death that took two minutes to recover, or
        // a fault window with a total outage), so these are first-class pass/fail conditions.
        Verdict.FaultAnalysis? analysis = null;
        if (scenario.Nemesis is not null)
        {
            analysis = RunFaultCorrelation(outputDir, notes, ref passed);
            if (analysis is not null)
                WriteAnalysisReport(analysis);
        }

        return new ScenarioVerdict(passed, run.ExitCode, summary.Valid, reconciliationPassed, runDir, notes)
        {
            Analysis = analysis,
        };
    }

    private Verdict.FaultAnalysis? RunFaultCorrelation(string outputDir, List<string> notes, ref bool passed)
    {
        IntervalSeries? series = IntervalSeries.Load(outputDir);
        IReadOnlyList<FaultWindow> windows = FaultTimeline.Parse(Path.Combine(runDir, "timeline.jsonl"));

        if (series is null || windows.Count == 0)
        {
            notes.Add("fault correlation skipped (no interval series or no fault windows to correlate)");
            return null;
        }

        FaultAnalysis analysis = FaultCorrelator.Analyze(series, windows);
        ChecksSpec checks = scenario.Checks;

        notes.Add(
            $"fault impact: baseline error {analysis.BaselineErrorRate:P1} / write-p99 {analysis.BaselineWriteP99Ms:N0}ms; " +
            $"in-fault error {analysis.InFaultErrorRate:P1} / write-p99 {analysis.InFaultWriteP99Ms:N0}ms " +
            $"({analysis.LatencyInflation:N1}x); max recovery {analysis.MaxRecoverySeconds:N1}s");

        foreach (WindowImpact w in analysis.Windows)
        {
            string recovery = !w.Healed
                ? "not healed (still active at run end)"
                : w.Recovered ? $"recovered in {w.RecoverySeconds:N1}s" : "NOT recovered before run end";
            notes.Add($"  fault {w.Label}: peak error {w.PeakErrorRate:P0}, {w.FailedDuringWindow:N0} failed, {recovery}");

            if (checks.RequireProgressUnderFault && !w.WorkloadProgressed)
            {
                notes.Add($"  CHECK FAILED: fault {w.Label} caused a total outage (0 ops completed during the window)");
                passed = false;
            }

            if (checks.RequireRecovery && w.Healed && !w.Recovered)
            {
                notes.Add($"  CHECK FAILED: fault {w.Label} never recovered before the run ended");
                passed = false;
            }

            if (w.Healed && w.Recovered && w.RecoverySeconds > checks.MaxRecoverySeconds)
            {
                notes.Add($"  CHECK FAILED: fault {w.Label} recovered in {w.RecoverySeconds:N1}s, over the {checks.MaxRecoverySeconds:N0}s limit");
                passed = false;
            }
        }

        return analysis;
    }

    private void WriteAnalysisReport(FaultAnalysis analysis)
    {
        System.Text.StringBuilder sb = new();
        sb.AppendLine($"# Fault analysis — {scenario.Name}");
        sb.AppendLine();
        sb.AppendLine($"- Baseline (clean seconds): error rate {analysis.BaselineErrorRate:P2}, write p99 {analysis.BaselineWriteP99Ms:N1} ms");
        sb.AppendLine($"- In-fault: error rate {analysis.InFaultErrorRate:P2}, write p99 {analysis.InFaultWriteP99Ms:N1} ms ({analysis.LatencyInflation:N1}x baseline)");
        sb.AppendLine($"- Max recovery time: {analysis.MaxRecoverySeconds:N1} s; all healed faults recovered: {(analysis.AllHealedFaultsRecovered ? "yes" : "no")}");
        sb.AppendLine();
        sb.AppendLine("| fault | healed | window (s) | peak error | failed | progressed | recovery (s) |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (WindowImpact w in analysis.Windows)
        {
            string recovery = !w.Healed ? "—" : w.Recovered ? $"{w.RecoverySeconds:N1}" : "not recovered";
            sb.AppendLine($"| {w.Label} | {(w.Healed ? "yes" : "no")} | {w.DurationSeconds:N1} | {w.PeakErrorRate:P0} | {w.FailedDuringWindow:N0} | {(w.WorkloadProgressed ? "yes" : "NO")} | {recovery} |");
        }

        File.WriteAllText(Path.Combine(runDir, "analysis.md"), sb.ToString());
    }

    private ScenarioVerdict Finalize(ScenarioVerdict verdict, WorkloadInvocation? init, WorkloadInvocation? run)
    {
        WriteManifest(verdict, init, run);
        PrintVerdict(verdict);
        return verdict;
    }

    private void WriteManifest(ScenarioVerdict verdict, WorkloadInvocation? init, WorkloadInvocation? run)
    {
        var manifest = new
        {
            scenario = scenario.Name,
            cluster = new
            {
                scenario.Cluster.Name,
                scenario.Cluster.Nodes,
                scenario.Cluster.Partitions,
                scenario.Cluster.ReplicationFactor,
                scenario.Cluster.PlacementRebalancer,
                scenario.Cluster.LeaderBalancer,
                locking = scenario.EffectiveLocking,
                isolation = scenario.EffectiveIsolation,
            },
            workload = new
            {
                scenario.Workload.Database,
                scenario.Workload.Mode,
                scenario.Workload.Rows,
                scenario.Workload.Duration,
                scenario.Workload.Warmup,
                scenario.Workload.Workers,
                scenario.Workload.TargetOps,
                scenario.Workload.ReadPercent,
                scenario.Workload.WritePercent,
                scenario.Workload.ExpectFaults,
            },
            nemesis = scenario.Nemesis is null
                ? null
                : new
                {
                    scenario.Nemesis.Seed,
                    mode = scenario.Nemesis.Random is not null ? "random" : "events",
                    eventCount = scenario.Nemesis.Events.Count,
                },
            camusdbGitCommit = TryGitCommit(scenario.Cluster.EffectiveCamusdbRepo),
            initExitCode = init?.ExitCode,
            runExitCode = run?.ExitCode,
            verdict = new
            {
                verdict.Passed,
                verdict.SummaryValid,
                verdict.ReconciliationPassed,
                verdict.Notes,
            },
            analysis = verdict.Analysis is null
                ? null
                : new
                {
                    verdict.Analysis.BaselineErrorRate,
                    verdict.Analysis.InFaultErrorRate,
                    verdict.Analysis.BaselineWriteP99Ms,
                    verdict.Analysis.InFaultWriteP99Ms,
                    verdict.Analysis.LatencyInflation,
                    verdict.Analysis.MaxRecoverySeconds,
                    verdict.Analysis.AllHealedFaultsRecovered,
                    windows = verdict.Analysis.Windows,
                },
        };

        File.WriteAllText(
            Path.Combine(runDir, "scenario.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string? TryGitCommit(string repo)
    {
        try
        {
            ProcessResult result = ProcessRunner
                .RunAsync("git", ["-C", repo, "rev-parse", "--short", "HEAD"]).GetAwaiter().GetResult();
            return result.Success ? result.StdOut.Trim() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void PrintVerdict(ScenarioVerdict verdict)
    {
        Console.WriteLine();
        Console.WriteLine($"scenario verdict: {(verdict.Passed ? "PASS" : "FAIL")}");
        Console.WriteLine($"  workload exit: {verdict.WorkloadExitCode}  summary valid: {verdict.SummaryValid}  reconciliation: {(verdict.ReconciliationPassed ? "passed" : "failed")}");
        foreach (string note in verdict.Notes)
            Console.WriteLine($"  - {note}");
        Console.WriteLine($"  artifacts: {verdict.RunDirectory}");
    }
}
