/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using Caraxes.Core.Cluster;
using Caraxes.Core.LeaderBalance;
using Caraxes.Core.Scenario;
using Caraxes.Core.Workload;

namespace Caraxes.Tests;

/// <summary>
/// Covers the evidence a scenario tells the workload to collect. The argument construction decides
/// what a finished run can be asked afterwards — whether one leader carried the writes, and which
/// build produced the number — so it is checked directly rather than through a docker invocation.
/// </summary>
[TestFixture]
public sealed class WorkloadEvidenceTests
{
    private const string Minimal = """
        name: s
        cluster:
          name: c
          nodes: 3
        workload:
          rows: 5000
        """;

    private static WorkloadRunPlan Plan(string yml)
    {
        ScenarioSpec scenario = ScenarioSpecReader.Read(yml);
        ClusterPlan plan = ClusterPlan.FromSpec(scenario.Cluster);
        return WorkloadRunner.BuildRunArgs(plan, scenario, "run");
    }

    private static string? ValueAfter(IReadOnlyList<string> args, string flag)
    {
        int index = args.ToList().IndexOf(flag);
        return index >= 0 && index + 1 < args.Count ? args[index + 1] : null;
    }

    [Test]
    public void NamesEveryNodeInTheMetricsEndpointPool()
    {
        // The name is the only thing that attributes a sample to a node: a scrape carries its
        // identity in target_info, not in the samples.
        WorkloadRunPlan plan = Plan(Minimal);

        Assert.That(ValueAfter(plan.Args, "--metrics-endpoint"), Is.EqualTo(
            "camus1=http://camus1:5095/metrics,camus2=http://camus2:5095/metrics,camus3=http://camus3:5095/metrics"));
    }

    [Test]
    public void NamesEveryNodeInTheNodeEndpointPool()
    {
        WorkloadRunPlan plan = Plan(Minimal);

        Assert.That(ValueAfter(plan.Args, "--node-endpoint"), Is.EqualTo(
            "camus1=http://camus1:5095,camus2=http://camus2:5095,camus3=http://camus3:5095"));
    }

    [Test]
    public void PassesTheConfiguredScrapeInterval()
    {
        WorkloadRunPlan plan = Plan("""
            name: s
            cluster:
              name: c
            workload:
              metrics_interval: 2s
            """);

        Assert.That(ValueAfter(plan.Args, "--metrics-interval"), Is.EqualTo("2s"));
    }

    [Test]
    public void SkipsMetricsCollectionAndSaysSoWhenTheClusterHasDiagnosticsOff()
    {
        // Asking for a series from nodes that serve no /metrics would produce nothing but failed
        // scrapes, which reads like a dead fleet.
        WorkloadRunPlan plan = Plan("""
            name: s
            cluster:
              name: c
              diagnostics: false
            """);

        Assert.That(plan.Args, Does.Not.Contain("--metrics-endpoint"));
        Assert.That(string.Join(" ", plan.Notes), Does.Contain("diagnostics: false"));
    }

    [Test]
    public void StillCapturesClusterFactsWithDiagnosticsOff()
    {
        // /v1/version, /v1/cluster/health and SHOW VARIABLES are served regardless of diagnostics.
        WorkloadRunPlan plan = Plan("""
            name: s
            cluster:
              name: c
              diagnostics: false
            """);

        Assert.That(plan.Args, Does.Contain("--node-endpoint"));
    }

    [Test]
    public void HonoursAnExplicitOptOut()
    {
        WorkloadRunPlan plan = Plan("""
            name: s
            cluster:
              name: c
            workload:
              node_metrics: false
              cluster_facts: false
            """);

        Assert.That(plan.Args, Does.Not.Contain("--metrics-endpoint"));
        Assert.That(plan.Args, Does.Not.Contain("--node-endpoint"));
        Assert.That(plan.Notes, Is.Empty);
    }

    [Test]
    public void KeepsTheExistingRunFlagsUnchanged()
    {
        // The evidence flags are additive; a scenario that predates them must drive the same run.
        WorkloadRunPlan plan = Plan(Minimal);

        Assert.That(ValueAfter(plan.Args, "--endpoint"), Is.EqualTo("https://camus1:5096,https://camus2:5096,https://camus3:5096"));
        Assert.That(ValueAfter(plan.Args, "--rows"), Is.EqualTo("5000"));
        Assert.That(ValueAfter(plan.Args, "--output"), Is.EqualTo("/artifacts/run"));
        Assert.That(plan.Args[0], Is.EqualTo("run"));
    }

    [Test]
    public void RejectsAMisspelledEvidenceKey()
    {
        // A silently ignored key would leave the run collecting nothing while the scenario claims it.
        Assert.Throws<ScenarioException>(() => ScenarioSpecReader.Read("""
            name: s
            cluster:
              name: c
            workload:
              node_metric: false
            """));
    }

    [Test]
    public void AcceptsTheNewChecks()
    {
        ScenarioSpec spec = ScenarioSpecReader.Read("""
            name: s
            cluster:
              name: c
            checks:
              require_client_headroom: true
              require_cluster_facts: true
            """);

        Assert.That(spec.Checks.RequireClientHeadroom, Is.True);
        Assert.That(spec.Checks.RequireClusterFacts, Is.True);
    }

    [Test]
    public void DefaultsToWaitingForLeadershipBeforeMeasuring()
    {
        // A node reports ready before leadership resolves, and seeding creates the ranges that then
        // have to be placed. Measuring across that charges the run for an election it did not cause.
        ScenarioSpec spec = ScenarioSpecReader.Read(Minimal);

        Assert.That(spec.SettleSeconds, Is.EqualTo(30));
    }

    [Test]
    public void AllowsTheSettleWaitToBeTurnedOff()
    {
        ScenarioSpec spec = ScenarioSpecReader.Read("""
            name: s
            cluster:
              name: c
            settle_seconds: 0
            """);

        Assert.That(spec.SettleSeconds, Is.Zero);
    }

    [Test]
    public void RejectsANegativeSettleWait()
    {
        Assert.Throws<ScenarioException>(() => ScenarioSpecReader.Read("""
            name: s
            cluster:
              name: c
            settle_seconds: -1
            """));
    }

    [Test]
    public void DefaultsBothNewChecksOff()
    {
        // A reliability scenario asks whether the cluster stayed correct, and a busy generator does
        // not invalidate that answer. These are for a capacity scenario to turn on.
        ScenarioSpec spec = ScenarioSpecReader.Read(Minimal);

        Assert.That(spec.Checks.RequireClientHeadroom, Is.False);
        Assert.That(spec.Checks.RequireClusterFacts, Is.False);
    }
}

/// <summary>
/// Covers how a settled leader map is read. Leadership concentrated on one node is the
/// hot-partition condition, and it is invisible in a throughput number — the run reads as a
/// three-node result while one node's disk and Raft group did all the durable work.
/// </summary>
[TestFixture]
public sealed class LeaderPlacementReadingTests
{
    private static LeaderSnapshot Snapshot(int camus1, int camus2, int camus3)
    {
        Dictionary<string, int> leaders = new() { ["camus1"] = camus1, ["camus2"] = camus2, ["camus3"] = camus3 };
        int resolved = camus1 + camus2 + camus3;
        return new LeaderSnapshot(DateTime.UtcNow, leaders, resolved, resolved);
    }

    private static readonly List<string> Nodes = ["camus1", "camus2", "camus3"];

    [Test]
    public void ReportsNoImbalanceWhenLeadershipIsEven()
    {
        Assert.That(Snapshot(1, 1, 1).Imbalance(Nodes), Is.Zero);
    }

    [Test]
    public void ReportsTheFullSpreadWhenOneNodeLeadsEverything()
    {
        // The shape a real three-node cluster produced on its first settle: every partition on one
        // node. Whatever throughput follows is that node's, not the fleet's.
        LeaderSnapshot snapshot = Snapshot(0, 3, 0);

        Assert.That(snapshot.Imbalance(Nodes), Is.EqualTo(3));
        Assert.That(snapshot.LeadersOn("camus2"), Is.EqualTo(snapshot.ResolvedPartitions));
    }

    [Test]
    public void ExcludesADownNodeFromTheImbalance()
    {
        // A killed node reports no placement at all, so counting it as a permanent zero would make
        // every fault run look permanently imbalanced.
        LeaderSnapshot snapshot = Snapshot(2, 2, 0);

        Assert.That(snapshot.Imbalance(["camus1", "camus2"]), Is.Zero);
    }

    [Test]
    public void FormatsThePlacementForTheVerdictNote()
    {
        Assert.That(Snapshot(0, 3, 0).Format(Nodes), Is.EqualTo("camus1=0  camus2=3  camus3=0  (resolved 3/3)"));
    }
}

/// <summary>
/// Covers the concurrency-sweep axis. It is separate from the parallelism axis on purpose: one sets
/// how much work the client offers at once, the other how far a single query fans out inside the
/// engine, and only the first answers whether a baseline is concurrency-starved.
/// </summary>
[TestFixture]
public sealed class ConcurrencySweepAxisTests
{
    private const string Sweep = """
        name: sweep
        cluster:
          name: sw
          nodes: 3
        workload:
          rows: 2000
          workers: 32
        axes:
          workers: [8, 32, 128]
        """;

    [Test]
    public void ProducesOneCellPerWorkerCount()
    {
        IReadOnlyList<Caraxes.Core.Matrix.MatrixCell> cells =
            Caraxes.Core.Matrix.MatrixExpander.Expand(Caraxes.Core.Matrix.MatrixReader.Read(Sweep));

        Assert.That(cells.Select(c => c.Scenario.Workload.Workers), Is.EqualTo(new[] { 8, 32, 128 }));
    }

    [Test]
    public void VariesTheClientWorkersNotTheQueryParallelism()
    {
        IReadOnlyList<Caraxes.Core.Matrix.MatrixCell> cells =
            Caraxes.Core.Matrix.MatrixExpander.Expand(Caraxes.Core.Matrix.MatrixReader.Read(Sweep));

        Assert.That(cells.Select(c => c.Scenario.Cluster.MaxQueryParallelism).Distinct().Count(), Is.EqualTo(1),
            "the workers axis must not disturb query parallelism");
    }

    [Test]
    public void GivesEachCellItsOwnName()
    {
        // Cells share a run root, so two cells with one name would overwrite each other's artifacts.
        IReadOnlyList<Caraxes.Core.Matrix.MatrixCell> cells =
            Caraxes.Core.Matrix.MatrixExpander.Expand(Caraxes.Core.Matrix.MatrixReader.Read(Sweep));

        Assert.That(cells.Select(c => c.Scenario.Name).Distinct().Count(), Is.EqualTo(cells.Count));
    }

    [Test]
    public void RejectsANonPositiveWorkerCount()
    {
        Assert.Throws<ScenarioException>(() => Caraxes.Core.Matrix.MatrixExpander.Expand(
            Caraxes.Core.Matrix.MatrixReader.Read("""
                name: sweep
                cluster:
                  name: sw
                axes:
                  workers: [0]
                """)));
    }

    [Test]
    public void RejectsAMisspelledAxis()
    {
        Assert.Throws<ScenarioException>(() => Caraxes.Core.Matrix.MatrixReader.Read("""
            name: sweep
            cluster:
              name: sw
            axes:
              worker: [8]
            """));
    }
}

/// <summary>
/// Covers what a matrix cell inherits from its base. The expander used to copy the workload and
/// checks with hand-written field lists, and those lists had fallen behind the specs: a matrix that
/// asked for <c>kind: bank</c> silently ran <c>accounts</c>, reporting a conflict-free workload's
/// throughput under a contended workload's name. These tests exist so a field added later cannot
/// reintroduce that.
/// </summary>
[TestFixture]
public sealed class MatrixCellInheritanceTests
{
    private static Caraxes.Core.Matrix.MatrixCell FirstCell(string yml) =>
        Caraxes.Core.Matrix.MatrixExpander.Expand(Caraxes.Core.Matrix.MatrixReader.Read(yml))[0];

    [Test]
    public void CarriesTheWorkloadKind()
    {
        // The defect: the cell ran the default 'accounts' while the file said 'bank'.
        Caraxes.Core.Matrix.MatrixCell cell = FirstCell("""
            name: m
            cluster:
              name: c
            workload:
              kind: bank
              rows: 2000
            axes:
              workers: [8]
            """);

        Assert.That(cell.Scenario.Workload.Kind, Is.EqualTo("bank"));
    }

    [Test]
    public void CarriesEveryWorkloadFieldTheBaseSet()
    {
        Caraxes.Core.Matrix.MatrixCell cell = FirstCell("""
            name: m
            cluster:
              name: c
            workload:
              kind: fanout
              tables: 4
              rows: 4000
              reconcile_timeout: 900
              metrics_interval: 2s
              node_metrics: false
              cluster_facts: false
            """);

        Assert.That(cell.Scenario.Workload.Kind, Is.EqualTo("fanout"));
        Assert.That(cell.Scenario.Workload.Tables, Is.EqualTo(4));
        Assert.That(cell.Scenario.Workload.ReconcileTimeout, Is.EqualTo(900));
        Assert.That(cell.Scenario.Workload.MetricsInterval, Is.EqualTo("2s"));
        Assert.That(cell.Scenario.Workload.NodeMetrics, Is.False);
        Assert.That(cell.Scenario.Workload.ClusterFacts, Is.False);
    }

    [Test]
    public void CarriesEveryCheckTheBaseSet()
    {
        Caraxes.Core.Matrix.MatrixCell cell = FirstCell("""
            name: m
            cluster:
              name: c
            checks:
              require_node_health: false
              require_client_headroom: true
              require_cluster_facts: true
            """);

        Assert.That(cell.Scenario.Checks.RequireNodeHealth, Is.False);
        Assert.That(cell.Scenario.Checks.RequireClientHeadroom, Is.True);
        Assert.That(cell.Scenario.Checks.RequireClusterFacts, Is.True);
    }

    [Test]
    public void CarriesTheSettleWait()
    {
        Caraxes.Core.Matrix.MatrixCell cell = FirstCell("""
            name: m
            cluster:
              name: c
            settle_seconds: 60
            """);

        Assert.That(cell.Scenario.SettleSeconds, Is.EqualTo(60));
    }

    [Test]
    public void GivesEachCellAnIndependentWorkload()
    {
        // Cells must not share mutable state: the axis sets Workers on one, and the others must keep
        // the base value.
        IReadOnlyList<Caraxes.Core.Matrix.MatrixCell> cells = Caraxes.Core.Matrix.MatrixExpander.Expand(
            Caraxes.Core.Matrix.MatrixReader.Read("""
                name: m
                cluster:
                  name: c
                workload:
                  kind: bank
                  rows: 2000
                axes:
                  workers: [8, 64]
                """));

        Assert.That(cells[0].Scenario.Workload.Workers, Is.EqualTo(8));
        Assert.That(cells[1].Scenario.Workload.Workers, Is.EqualTo(64));
        Assert.That(cells.All(c => c.Scenario.Workload.Kind == "bank"), Is.True);
        Assert.That(cells[0].Scenario.Workload, Is.Not.SameAs(cells[1].Scenario.Workload));
    }
}
