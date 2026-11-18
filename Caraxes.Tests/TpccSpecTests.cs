/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using NUnit.Framework;
using Caraxes.Core.Cluster;
using Caraxes.Core.Scenario;
using Caraxes.Core.Workload;

namespace Caraxes.Tests;

[TestFixture]
public sealed class TpccSpecTests
{
    private const string Shape = "  read_percent: 0\n  write_percent: 100\n";

    private static ScenarioSpec Read(string workload) => ScenarioSpecReader.Read($"""
        name: s
        cluster:
          name: c
        workload:
        {workload}
        """);

    private static string? ValueAfter(IReadOnlyList<string> args, string flag)
    {
        for (int i = 0; i < args.Count - 1; i++)
            if (args[i] == flag)
                return args[i + 1];
        return null;
    }

    [Test]
    public void TpccKind_ParsesItsSettings()
    {
        ScenarioSpec spec = Read("  kind: tpcc\n  tpcc_warehouses: 4\n  tpcc_scale: 0.1\n  tpcc_load_connections: 6\n" + Shape);

        Assert.That(spec.Workload.Kind, Is.EqualTo("tpcc"));
        Assert.That(spec.Workload.TpccWarehouses, Is.EqualTo(4));
        Assert.That(spec.Workload.TpccScale, Is.EqualTo(0.1));
        Assert.That(spec.Workload.TpccLoadConnections, Is.EqualTo(6));
    }

    [Test]
    public void TpccSettings_OnAnotherKind_AreRefused()
    {
        ScenarioException? e = Assert.Throws<ScenarioException>(() => Read("  kind: bank\n  tpcc_warehouses: 2"));
        Assert.That(e!.Message, Does.Contain("apply only to 'workload.kind: tpcc'"));

        e = Assert.Throws<ScenarioException>(() => Read("  kind: accounts\n  tpcc_scale: 0.5"));
        Assert.That(e!.Message, Does.Contain("apply only to 'workload.kind: tpcc'"));
    }

    [Test]
    public void TpccKind_TakesEveryOperationFromItsOwnMix()
    {
        ScenarioException? e = Assert.Throws<ScenarioException>(() => Read("  kind: tpcc"));
        Assert.That(e!.Message, Does.Contain("draws each operation from its own transaction mix"));

        e = Assert.Throws<ScenarioException>(() => Read("  kind: tpcc\n  read_percent: 8\n  write_percent: 92"));
        Assert.That(e!.Message, Does.Contain("'read_percent: 0'"));
    }

    [Test]
    public void SizesOutOfRange_AreRefused()
    {
        ScenarioException? e = Assert.Throws<ScenarioException>(() => Read("  kind: tpcc\n  tpcc_warehouses: 1001\n" + Shape));
        Assert.That(e!.Message, Does.Contain("'workload.tpcc_warehouses' must be between 1 and 1000"));

        e = Assert.Throws<ScenarioException>(() => Read("  kind: tpcc\n  tpcc_scale: 0.001\n" + Shape));
        Assert.That(e!.Message, Does.Contain("'workload.tpcc_scale' must be between 0.01 and 1"));

        e = Assert.Throws<ScenarioException>(() => Read("  kind: tpcc\n  tpcc_scale: 2\n" + Shape));
        Assert.That(e!.Message, Does.Contain("'workload.tpcc_scale' must be between 0.01 and 1"));
    }

    [Test]
    public void InitNamesTheKindAndTheSize()
    {
        ScenarioSpec spec = Read("  kind: tpcc\n  tpcc_warehouses: 4\n  tpcc_scale: 0.1\n  tpcc_load_connections: 6\n" + Shape);
        IReadOnlyList<string> args = WorkloadRunner.BuildInitArgs(ClusterPlan.FromSpec(spec.Cluster), spec);

        Assert.That(args[0], Is.EqualTo("init"));
        Assert.That(ValueAfter(args, "--workload"), Is.EqualTo("tpcc"));
        Assert.That(ValueAfter(args, "--tpcc-warehouses"), Is.EqualTo("4"));
        Assert.That(ValueAfter(args, "--tpcc-scale"), Is.EqualTo("0.1"));
        Assert.That(ValueAfter(args, "--tpcc-load-connections"), Is.EqualTo("6"));
        Assert.That(args, Does.Not.Contain("--tpch-scale"));
    }

    [Test]
    public void RunPassesTheSizeAndNotTheLoadConnections()
    {
        ScenarioSpec spec = Read("  kind: tpcc\n  tpcc_warehouses: 4\n  tpcc_scale: 0.1\n  tpcc_load_connections: 6\n" + Shape);
        IReadOnlyList<string> args = WorkloadRunner.BuildRunArgs(ClusterPlan.FromSpec(spec.Cluster), spec, "run").Args;

        Assert.That(ValueAfter(args, "--workload"), Is.EqualTo("tpcc"));
        Assert.That(ValueAfter(args, "--tpcc-warehouses"), Is.EqualTo("4"));
        Assert.That(ValueAfter(args, "--tpcc-scale"), Is.EqualTo("0.1"));
        Assert.That(ValueAfter(args, "--write-percent"), Is.EqualTo("100"));
        Assert.That(args, Does.Not.Contain("--tpcc-load-connections"), "the load is init's");
    }

    [Test]
    public void DefaultsAreLeftToTheWorkload()
    {
        ScenarioSpec spec = Read("  kind: tpcc\n" + Shape);
        IReadOnlyList<string> run = WorkloadRunner.BuildRunArgs(ClusterPlan.FromSpec(spec.Cluster), spec, "run").Args;
        IReadOnlyList<string> init = WorkloadRunner.BuildInitArgs(ClusterPlan.FromSpec(spec.Cluster), spec);

        Assert.That(run, Does.Not.Contain("--tpcc-warehouses"));
        Assert.That(run, Does.Not.Contain("--tpcc-scale"));
        Assert.That(init, Does.Not.Contain("--tpcc-warehouses"));
        Assert.That(ValueAfter(init, "--workload"), Is.EqualTo("tpcc"));
    }

    [Test]
    public void TpccVerdictAndReport_DeserializeFromTheWorkloadsArtifacts()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"caraxes-tpcc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "reconciliation.json"), """
                {"Passed":false,"Failures":["consistency condition C1 (w_ytd = SUM(d_ytd) of the warehouse) is broken in 1 of 4 case(s)"],
                 "Tpcc":{"Passed":false,"Status":"verified","Warehouses":4,"Scale":"0.1","Executions":6009,"Committed":5978,
                         "NewOrders":2626,"RolledBack":31,"Conflicts":0,"Undecided":2,"Errors":0,"Anomalies":17,
                         "ConditionsChecked":92857,"ConditionViolations":3,"Failures":[]}}
                """);
            File.WriteAllText(Path.Combine(dir, "tpcc.json"), """
                {"Warehouses":4,"Scale":"0.1","Seed":"1847","MeasuredSeconds":120,"NewOrdersPerMinute":1313,"TransactionsPerSecond":50.01,
                 "Transactions":[{"Transaction":"New-Order","MixPercent":45,"Executions":2660,"Committed":2626,"RolledBack":31,
                                  "Conflicts":0,"Undecided":2,"Errors":0,"Anomalies":0,"Retries":3,"MeanMs":101.2,"P50Ms":92,
                                  "P95Ms":180,"P99Ms":313,"MaxMs":650.5}],
                 "Conditions":[{"Name":"C1","Rule":"w_ytd = SUM(d_ytd) of the warehouse","Checked":4,"Violations":1,
                                "Examples":["warehouse 2: w_ytd 607555.11, SUM(d_ytd) 607554.11"]}]}
                """);

            ReconciliationSummary r = WorkloadArtifacts.ReadReconciliation(dir)!;
            Assert.That(r.Passed, Is.False);
            Assert.That(r.Tpcc!.Warehouses, Is.EqualTo(4));
            Assert.That(r.Tpcc.Committed, Is.EqualTo(5978));
            Assert.That(r.Tpcc.Anomalies, Is.EqualTo(17));
            Assert.That(r.Tpcc.ConditionViolations, Is.EqualTo(3));
            Assert.That(r.Tpcc.Status, Is.EqualTo("verified"));

            TpccReport report = WorkloadArtifacts.ReadTpcc(dir)!;
            Assert.That(report.NewOrdersPerMinute, Is.EqualTo(1313));
            Assert.That(report.Transactions.Single().Transaction, Is.EqualTo("New-Order"));
            Assert.That(report.Transactions.Single().P99Ms, Is.EqualTo(313));
            Assert.That(report.Conditions.Single().Examples.Single(), Does.Contain("warehouse 2"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
