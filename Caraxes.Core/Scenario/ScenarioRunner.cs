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

    /// <summary>Host load sampled before the harness started anything of its own — the only reading
    /// that describes competing work rather than this run's own. See <see cref="GradeHostLoad"/>.</summary>
    private HostLoadSample? ambientHostLoad;

    /// <summary>Watches for the host being suspended mid-run, which silently invalidates every rate
    /// and latency the run reports. See <see cref="SuspendDetector"/>.</summary>
    private SuspendDetector? suspendDetector;

    /// <summary>
    /// <paramref name="runTag"/> suffixes the run directory, so repeated runs of one scenario keep
    /// their artifacts instead of overwriting each other.
    ///
    /// <para>A baseline is established by repetition — the plan asks for at least three matched runs
    /// and a reported median, range and variation — and without a tag the second run deletes the
    /// first run's evidence before producing its own. The cluster directory is deliberately not
    /// tagged: runs are sequential and each tears its fleet down, so they share one cluster identity.</para>
    /// </summary>
    public ScenarioRunner(ScenarioSpec scenario, string? runRoot = null, string? runTag = null)
    {
        this.scenario = scenario;
        string root = runRoot ?? Path.Combine(Environment.CurrentDirectory, "runs");
        string directoryName = string.IsNullOrWhiteSpace(runTag) ? scenario.Name : $"{scenario.Name}-{Sanitize(runTag)}";
        runDir = Path.Combine(root, "scenarios", directoryName);
        orchestrator = new ClusterOrchestrator(scenario.Cluster, Path.Combine(root, "clusters"));
    }

    /// <summary>Keeps a tag usable as a directory name; anything else becomes a hyphen.</summary>
    public static string Sanitize(string tag)
        => string.Concat(tag.Trim().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));

    public async Task<ScenarioVerdict> RunAsync(bool skipBuild = false, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(runDir);
        List<string> notes = [];

        // Started before anything is built or brought up, so the watch covers the whole run rather
        // than only its measured window — a host that sleeps during the image build or the seed
        // invalidates the run just as thoroughly as one that sleeps mid-workload.
        suspendDetector = SuspendDetector.Start();

        // Ambient host load, sampled before the harness starts anything of its own. A machine already
        // busy with unrelated work distorts every number this run produces, and nothing else in the
        // harness can see it: the generator reports its own health, and a load average from inside the
        // container describes the Docker VM rather than the host it competes on.
        HostLoadSample? hostLoad = HostLoad.Read();
        ambientHostLoad = hostLoad;
        if (hostLoad is null)
        {
            notes.Add("ambient host load was not measured on this platform; contention cannot be ruled out");
        }
        else if (HostLoad.IsContended(hostLoad))
        {
            notes.Add(
                $"HOST WAS BUSY BEFORE THE RUN: load {hostLoad.One:N2} over {hostLoad.ProcessorCount} core(s) " +
                $"({hostLoad.PerCore:P0} per core). Unrelated work competed with this measurement; treat its " +
                "throughput as a lower bound and re-run on a quiet machine before quoting a number.");
        }
        else
        {
            notes.Add($"ambient host load before the run: {hostLoad.One:N2} over {hostLoad.ProcessorCount} core(s)");
        }

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

            await SettleAsync(plan, notes, cancellationToken).ConfigureAwait(false);

            run = await RunWorkloadWithNemesisAsync(workload, plan, artifactsDir, notes, cancellationToken)
                .ConfigureAwait(false);

            // After the load stops and before teardown: the only window in which deferred work can be
            // seen draining, and the only collection the harness must do itself — the workload
            // container that scraped during the run has exited by now.
            if (scenario.DrainObservationSeconds > 0)
                notes.AddRange(await DrainObserver.ObserveAsync(
                    plan,
                    Path.Combine(artifactsDir, "run"),
                    TimeSpan.FromSeconds(scenario.DrainObservationSeconds),
                    TimeSpan.FromSeconds(scenario.DrainObservationIntervalSeconds),
                    cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            // Before teardown, always: the log dies with the container, and the run that most needs
            // it is the one that just failed. Captured even when teardown is off, so a run directory
            // is self-contained evidence rather than a pointer to a fleet someone will later remove.
            if (scenario.CaptureNodeLogs)
            {
                Console.WriteLine("==> capturing node logs");
                IReadOnlyList<string> captureNotes = await orchestrator
                    .CaptureLogsAsync(Path.Combine(artifactsDir, "run"), scenario.NodeLogTail, cancellationToken)
                    .ConfigureAwait(false);

                notes.AddRange(captureNotes);
            }
            else
            {
                notes.Add(
                    "node logs were not captured (capture_node_logs: false); log-only diagnostics for " +
                    "this run are gone once the cluster is torn down");
            }

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
    /// Waits for partition leadership to settle before the measured window opens, and records where it
    /// settled.
    ///
    /// <para>This sits after seeding on purpose. A node reports ready long before leadership is
    /// resolved, and seeding creates the very tables whose ranges then have to be placed — so the
    /// settlement that matters is the one that follows the data, not the one that follows startup.</para>
    ///
    /// <para>Best effort by design: an unsettled cluster still runs, and the note says leadership had
    /// not resolved. A scenario that refused to start would report nothing at all, which is worse
    /// evidence than a run with a caveat attached.</para>
    /// </summary>
    private async Task SettleAsync(ClusterPlan plan, List<string> notes, CancellationToken cancellationToken)
    {
        if (scenario.SettleSeconds <= 0)
        {
            notes.Add("leader settling skipped (settle_seconds: 0); the measured window may include an election");
            return;
        }

        Console.WriteLine($"==> waiting up to {scenario.SettleSeconds}s for partition leadership to settle");
        using HttpProbes probes = new();

        // Waits for leadership to be spread, not merely resolved. Both conditions share the one
        // budget: a cluster that resolves instantly and sits concentrated has not settled into
        // anything a capacity run can measure, and the balancer needs the idle seconds after seeding
        // to move it — which is time this wait was already spending.
        List<string> nodeNames = plan.Nodes.Select(n => n.Name).ToList();
        Caraxes.Core.LeaderBalance.LeaderSnapshot snapshot = await Caraxes.Core.LeaderBalance.LeaderObservation
            .WaitForSpreadAsync(plan, probes, TimeSpan.FromSeconds(scenario.SettleSeconds), cancellationToken)
            .ConfigureAwait(false);

        string placement = snapshot.Format(nodeNames);
        leadershipSpread = Caraxes.Core.LeaderBalance.LeaderObservation.IsSpread(snapshot, nodeNames);

        if (leadershipSpread)
        {
            notes.Add($"leadership settled and spread before the measured window: {placement}");
        }
        else if (snapshot.TotalPartitions > 0 && snapshot.ResolvedPartitions == snapshot.TotalPartitions)
        {
            notes.Add(
                $"leadership resolved but did NOT spread within {scenario.SettleSeconds}s: {placement}; " +
                "every partition has a leader, but they are not distributed across the nodes");
        }
        else
        {
            notes.Add(
                $"leadership had NOT settled after {scenario.SettleSeconds}s: {placement}; " +
                "the measured window may include an election");
        }

        // Leadership concentrated on one node is the hot-partition condition, and it is invisible in a
        // throughput number: the run reads as a three-node result while one node's disk and Raft
        // group did all the durable work. Naming it here means a later reader does not have to infer
        // it from the per-node series.
        if (nodeNames.Count > 1 && snapshot.ResolvedPartitions > 1)
        {
            string busiest = nodeNames.OrderByDescending(snapshot.LeadersOn).First();
            if (snapshot.LeadersOn(busiest) == snapshot.ResolvedPartitions)
                notes.Add(
                    $"every resolved partition is led by '{busiest}'; this run loads one leader, so its " +
                    "throughput is that node's capacity rather than the cluster's");
            else if (snapshot.Imbalance(nodeNames) > 1)
                notes.Add($"leadership is uneven across nodes ({placement}); expect one node to carry more durable work");
        }

        Console.WriteLine($"    {placement}");
    }

    /// <summary>
    /// Runs the measured workload with the node monitor sampling health and memory alongside it,
    /// and, if the scenario has a nemesis, drives its fault schedule concurrently. The nemesis is
    /// timed from the workload's launch; when the workload container exits, the nemesis is
    /// signalled to stop and heals everything still in effect before this returns — so the cluster
    /// is quiescent (if not torn down) by the time the verdict is read. The monitor runs for
    /// every scenario, fault-free ones included: the health series is what lets the verdict catch
    /// a node that died while its container stayed <c>Up</c>.
    /// </summary>
    private IReadOnlyList<PlacementSample> placementSamples = [];

    /// <summary>Whether leadership was spread across the nodes when the measured window opened. Set
    /// by the settle step; graded only when the scenario asks for it.</summary>
    private bool leadershipSpread;

    private async Task<WorkloadInvocation> RunWorkloadWithNemesisAsync(
        WorkloadRunner workload, ClusterPlan plan, string artifactsDir, List<string> notes, CancellationToken cancellationToken)
    {
        using HttpProbes probes = new();
        NodeMonitor monitor = new(plan, probes, runDir);
        string timelinePath = Path.Combine(runDir, "timeline.jsonl");

        // Both side tasks stop when the workload finishes; heal-on-stop runs inside NemesisRunner
        // with a fresh token, so cancelling here never leaves a fault un-healed.
        using CancellationTokenSource sideStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Watched across the whole invocation, graded over the measured window alone. Warm-up is
        // exactly when leadership is expected to move, so grading it would report churn the run
        // deliberately paid for outside its numbers.
        PlacementPoller placementPoller = new(plan);

        Task<WorkloadInvocation> workloadTask = workload.RunAsync(scenario, artifactsDir, "run", cancellationToken);
        Task monitorTask = monitor.RunAsync(sideStop.Token);
        Task placementTask = placementPoller.RunAsync(sideStop.Token);
        Task? nemesisTask = scenario.Nemesis is null
            ? null
            : new NemesisRunner(plan, probes).RunAsync(scenario.Nemesis, timelinePath, sideStop.Token);

        WorkloadInvocation result;
        try
        {
            result = await workloadTask.ConfigureAwait(false);
        }
        finally
        {
            sideStop.Cancel();
            try
            {
                if (nemesisTask is not null)
                    await nemesisTask.ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // The nemesis is best-effort decoration around the workload; its failure is a note,
                // never the thing that decides the verdict.
                notes.Add($"nemesis reported: {e.Message}");
            }

            try
            {
                await monitorTask.ConfigureAwait(false);
            }
            catch (Exception e)
            {
                notes.Add($"node monitor reported: {e.Message}");
            }

            try
            {
                await placementTask.ConfigureAwait(false);
            }
            catch (Exception e)
            {
                notes.Add($"placement watch reported: {e.Message}");
            }
        }

        placementSamples = placementPoller.Samples;

        if (scenario.Nemesis is not null)
            notes.Add($"nemesis timeline: {timelinePath}");
        return result;
    }

    /// <summary>
    /// Reports whether the topology held for the measured window, from the samples taken alongside it.
    ///
    /// <para>Cut to the run's own anchor rather than to the scenario's configured warm-up, because the
    /// workload decides when measurement began and a slow seed would put a computed window in the
    /// wrong place. Without an anchor the samples are graded whole and the note says so — a wider
    /// window can only over-report movement, never hide it.</para>
    ///
    /// <para>A note, not a verdict. A topology that moved does not make a run invalid; it makes its
    /// number an average of two clusters, which is a thing the reader has to know and the harness
    /// cannot decide for them.</para>
    /// </summary>
    private void GradePlacementStability(string artifactsDir, List<string> notes)
    {
        if (placementSamples.Count == 0)
            return;

        MeasuredWindow? window = WorkloadArtifacts.ReadMeasuredWindow(Path.Combine(artifactsDir, "run"));

        IReadOnlyList<PlacementSample> measured = window is null
            ? placementSamples
            : placementSamples.Where(sample => window.Contains(sample.Utc)).ToList();

        PlacementStability stability = PlacementWatch.Grade(measured);
        notes.Add(window is null
            ? $"{stability.Note} (graded over the whole run: it recorded no measured-window anchor)"
            : stability.Note);
    }

    /// <summary>
    /// Fails a run that started with leadership concentrated on one node, when the scenario asked for
    /// it to be spread.
    ///
    /// <para>Opt-in for the same reason the quiet-host and client-headroom checks are: a reliability
    /// scenario still answers its question on a lopsided cluster, and some scenarios concentrate
    /// leadership deliberately. A <b>capacity</b> run cannot — one node leading every partition means
    /// the number it produces is that node's, whatever the request distribution looks like.</para>
    /// </summary>
    private void GradeLeadershipSpread(List<string> notes, ref bool passed)
    {
        if (!scenario.Checks.RequireSpreadLeadership || leadershipSpread)
            return;

        passed = false;
        notes.Add(
            "FAIL: leadership was not spread across the nodes when the measured window opened " +
            "(checks.require_spread_leadership). One node leading every partition makes this run a " +
            "measurement of that node, not of the cluster — raise settle_seconds to give the leader " +
            "balancer more idle time, or re-run.");
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

        GradeSuspend(notes, ref passed);
        GradeHostLoad(notes, ref passed);
        GradePlacementStability(artifactsDir, notes);
        GradeLeadershipSpread(notes, ref passed);
        GradeClusterFacts(outputDir, notes, ref passed);
        GradeClientHeadroom(outputDir, notes, ref passed);

        if (scenario.Checks.RequireNodeHealth)
            GradeNodeHealth(notes, ref passed);

        return new ScenarioVerdict(passed, run.ExitCode, summary.Valid, reconciliationPassed, runDir, notes)
        {
            Analysis = analysis,
        };
    }

    /// <summary>
    /// Fails the run when the host was suspended while it was in progress.
    ///
    /// <para>Unconditional, unlike every other check here: this is not a judgement about measurement
    /// quality that a scenario might reasonably waive, it is the observation that the artifact does
    /// not describe a real interval. A correctness scenario survives a suspend in its invariants but
    /// not in its recovery timings, and neither kind of run should be quoted from afterwards.</para>
    /// </summary>
    private void GradeSuspend(List<string> notes, ref bool passed)
    {
        if (suspendDetector is null)
            return;

        if (SuspendDetector.Describe(suspendDetector.Suspended, suspendDetector.Wall) is not string failure)
            return;

        notes.Add($"  CHECK FAILED: {failure}");
        passed = false;
    }

    /// <summary>
    /// Fails the run when the host was busy <b>before it started</b> and the scenario asked for a
    /// quiet machine. Off by default: a reliability scenario still answers its question on a loaded
    /// box, and only a capacity claim depends on the machine being idle.
    ///
    /// <para><b>It grades the ambient sample, never a reading taken after the run.</b> A load average
    /// measured once the workload has finished is dominated by the cluster and the generator this run
    /// itself started, so grading it would fail a scenario precisely for succeeding at loading the
    /// machine — and would do so more often the higher the concurrency, which is the opposite of what
    /// a capacity guard is for. That is not hypothetical: <c>accounts-2k-w128</c> and
    /// <c>accounts-2k-w256</c> (2026-09-01) were failed at load 10.15 and 10.83 on 16 cores after
    /// starting from ambient 3.13 and 3.72, with exact reconciliation, zero failures and the highest
    /// throughput ever measured on the host. The fsync A/B pair <c>k154p3</c> was depressed the same
    /// way, its note recording load rising "3.54 → 5.37 <em>during</em> the run".</para>
    ///
    /// <para>The end-of-run reading is still taken and reported, because how much load a run creates
    /// is worth seeing — but a load average cannot separate this run's own work from a competitor's,
    /// so only the pre-run sample can carry a verdict.</para>
    /// </summary>
    private void GradeHostLoad(List<string> notes, ref bool passed)
    {
        HostLoadSample? after = HostLoad.Read();
        if (after is not null)
            notes.Add(
                $"host load after the run: {after.One:N2} over {after.ProcessorCount} core(s) " +
                "(includes this run's own cluster and generator; not graded)");

        if (!scenario.Checks.RequireQuietHost)
            return;

        if (HostLoad.GradeAmbient(ambientHostLoad) is not string failure)
            return;

        notes.Add($"  CHECK FAILED: {failure}");
        passed = false;
    }

    /// <summary>
    /// Records what the cluster said it was: the build and durability fingerprint, and any node that
    /// could not be asked or answered that it was not ready.
    ///
    /// <para>The fingerprint is the note that matters most in a retained artifact. A throughput number
    /// with no record of the build that produced it can never be compared against another run, and the
    /// most common way two "identical" runs differ is a dependency version nobody changed on
    /// purpose.</para>
    /// </summary>
    private void GradeClusterFacts(string outputDir, List<string> notes, ref bool passed)
    {
        ClusterFactsSummary? facts = WorkloadArtifacts.ReadClusterFacts(outputDir);

        if (facts is null)
        {
            string message = scenario.Workload.ClusterFacts
                ? "no cluster-facts.json produced; this run cannot say which build or durability settings it measured"
                : "cluster facts not captured (workload.cluster_facts: false)";
            notes.Add(message);

            if (scenario.Checks.RequireClusterFacts)
            {
                notes.Add("  CHECK FAILED: checks.require_cluster_facts is on and no cluster facts were captured");
                passed = false;
            }
            return;
        }

        notes.Add($"cluster fingerprint: {facts.DurabilityFingerprint} " +
                  $"({string.Join(", ", Versions(facts))})");

        // Node readiness at capture time is graded only for a fault-free run. Under a nemesis a node
        // is legitimately down or still restarting when the facts are taken, and failing the run for
        // that grades the fault injection rather than the cluster. A chaos run that lost no data would
        // otherwise be reported as a failure — which is exactly what happened on fault seed 17.
        bool faultFree = scenario.Nemesis is null;
        List<string> unhealthy = facts.Nodes.Where(n => n.Ready != true).Select(n => n.Node).ToList();
        if (unhealthy.Count > 0)
        {
            notes.Add($"node(s) not reporting ready when facts were captured: {string.Join(", ", unhealthy)}" +
                      (faultFree ? "" : " (expected under an injected fault; not graded)"));
            if (scenario.Checks.RequireClusterFacts && faultFree)
            {
                notes.Add("  CHECK FAILED: checks.require_cluster_facts is on and a node did not report ready");
                passed = false;
            }
        }

        foreach (ClusterFactsNode node in facts.Nodes)
        {
            foreach (string error in node.Errors)
                notes.Add($"  node '{node.Node}' could not answer {error}");
        }
        foreach (string error in facts.Errors)
            notes.Add($"  cluster fact capture: {error}");

        // Same reasoning: under a fault, a node that cannot answer a probe is the fault working. What
        // the check must still guarantee in both cases is that the run knows WHICH BUILD it measured,
        // which the fingerprint carries as long as any node answered.
        bool incomplete = facts.Nodes.Any(n => n.Errors.Count > 0) || facts.Errors.Count > 0;
        if (incomplete && scenario.Checks.RequireClusterFacts && faultFree)
        {
            notes.Add("  CHECK FAILED: checks.require_cluster_facts is on and part of the capture did not answer");
            passed = false;
        }

        if (scenario.Checks.RequireClusterFacts && facts.Nodes.All(n => n.Components.Count == 0))
        {
            notes.Add("  CHECK FAILED: checks.require_cluster_facts is on and no node reported its build");
            passed = false;
        }
    }

    /// <summary>
    /// One line for the load generator's own headroom, and a failure when a capacity scenario asked
    /// for it. A generator that ran out of CPU or sat at its in-flight cap produces a flat curve that
    /// reads exactly like a saturated cluster.
    /// </summary>
    private void GradeClientHeadroom(string outputDir, List<string> notes, ref bool passed)
    {
        ClientResourcesSummary? client = WorkloadArtifacts.ReadClientResources(outputDir);

        if (client is null)
        {
            notes.Add("no client-resources.json produced; the load generator's own headroom is unknown");
            if (scenario.Checks.RequireClientHeadroom)
            {
                notes.Add("  CHECK FAILED: checks.require_client_headroom is on and the generator was not measured");
                passed = false;
            }
            return;
        }

        notes.Add($"client headroom: {(client.HeadroomAvailable ? "OK" : "SUSPECT")} " +
                  $"(CPU {client.CpuUtilization:P0} of {client.ProcessorCount} core(s), " +
                  $"{client.AllocatedMbPerSecond:N0} MB/s allocated, peak pool queue {client.PeakThreadPoolQueue})");

        foreach (string warning in client.Warnings)
            notes.Add($"  generator warning: {warning}");

        if (!client.HeadroomAvailable && scenario.Checks.RequireClientHeadroom)
        {
            notes.Add("  CHECK FAILED: checks.require_client_headroom is on and the generator may have been the limiter");
            passed = false;
        }
    }

    /// <summary>The engine versions worth naming in a one-line note: the storage and consensus
    /// libraries, whose change explains a throughput difference that no scenario file records.</summary>
    private static IEnumerable<string> Versions(ClusterFactsSummary facts)
    {
        ClusterFactsNode? node = facts.Nodes.FirstOrDefault();
        if (node is null)
            return ["no node answered"];

        List<string> parts = [];
        if (node.Server is not null)
            parts.Add($"server {node.Server}");
        foreach (ClusterFactsComponent component in node.Components)
        {
            if (component.Name is "Kahuna.Core" or "Kommander" or "Nixie")
                parts.Add($"{component.Name} {component.Version}");
        }
        return parts.Count > 0 ? parts : ["no versions reported"];
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

    /// <summary>
    /// Grades the node-health probe series against the fault timeline. An outage no fault explains
    /// fails the run: this is the guard against the dead-but-<c>Up</c> case, where a node process
    /// aborts, its container keeps reporting <c>Up</c>, and every other check still reads green.
    /// A missing series (an old run directory, or the monitor itself failed) is reported as a
    /// note, never silently treated as healthy.
    /// </summary>
    private void GradeNodeHealth(List<string> notes, ref bool passed)
    {
        string healthPath = Path.Combine(runDir, "node-health.csv");
        if (!File.Exists(healthPath))
        {
            notes.Add("node health: no node-health.csv produced; node liveness is UNVERIFIED for this run");
            return;
        }

        IReadOnlyList<FaultWindow> windows = FaultTimeline.Parse(Path.Combine(runDir, "timeline.jsonl"));
        IReadOnlyList<NodeOutage> outages =
            NodeHealthAnalysis.Analyze(healthPath, windows, scenario.Checks.MaxRecoverySeconds);

        if (outages.Count == 0)
        {
            notes.Add("node health: every node answered /ping at every sample");
            return;
        }

        foreach (NodeOutage outage in outages)
        {
            double seconds = (outage.EndUtc - outage.StartUtc).TotalSeconds;
            if (outage.Excused)
            {
                notes.Add(
                    $"node health: {outage.Node} unreachable {outage.StartUtc:HH:mm:ss}–{outage.EndUtc:HH:mm:ss}Z " +
                    $"({outage.Samples} sample(s)) — explained by an active fault or too short to grade");
                continue;
            }

            notes.Add(
                $"  CHECK FAILED: node {outage.Node} stopped answering /ping from {outage.StartUtc:HH:mm:ss}Z " +
                $"for {seconds:N0}+ s ({outage.Samples} samples) with no fault active — the node died on its own");
            passed = false;
        }
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
