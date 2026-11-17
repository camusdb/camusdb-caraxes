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
public sealed class TpchSpecTests
{
    private const string Shape = "  mode: closed\n  read_percent: 100\n  write_percent: 0\n";

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
    public void TpchKind_ParsesItsSettings()
    {
        ScenarioSpec spec = Read("  kind: tpch\n  tpch_scale: 0.1\n  tpch_queries: 1-6,14\n  tpch_load_connections: 4\n" + Shape);

        Assert.That(spec.Workload.Kind, Is.EqualTo("tpch"));
        Assert.That(spec.Workload.TpchScale, Is.EqualTo(0.1));
        Assert.That(spec.Workload.TpchQueries, Is.EqualTo("1-6,14"));
        Assert.That(spec.Workload.TpchLoadConnections, Is.EqualTo(4));
    }

    [Test]
    public void TpchSettings_OnAnotherKind_AreRefused()
    {
        ScenarioException? e = Assert.Throws<ScenarioException>(() => Read("  kind: bank\n  tpch_scale: 0.1"));
        Assert.That(e!.Message, Does.Contain("apply only to 'workload.kind: tpch'"));

        e = Assert.Throws<ScenarioException>(() => Read("  kind: accounts\n  tpch_queries: 1,2"));
        Assert.That(e!.Message, Does.Contain("apply only to 'workload.kind: tpch'"));
    }

    [Test]
    public void TpchKind_MustBeReadOnlyAndClosedLoop()
    {
        ScenarioException? e = Assert.Throws<ScenarioException>(() => Read("  kind: tpch\n  mode: closed"));
        Assert.That(e!.Message, Does.Contain("is read-only"));

        e = Assert.Throws<ScenarioException>(() => Read("  kind: tpch\n  read_percent: 100\n  write_percent: 0"));
        Assert.That(e!.Message, Does.Contain("needs 'mode: closed'"));
    }

    [Test]
    public void AScaleBelowTheMinimum_IsRefused()
    {
        ScenarioException? e = Assert.Throws<ScenarioException>(() => Read("  kind: tpch\n  tpch_scale: 0.001\n" + Shape));
        Assert.That(e!.Message, Does.Contain("'workload.tpch_scale' must be at least 0.01"));
    }

    [Test]
    public void InitNamesTheKindAndTheScale_OnlyForTpch()
    {
        ScenarioSpec tpch = Read("  kind: tpch\n  tpch_scale: 0.1\n  tpch_load_connections: 4\n" + Shape);
        IReadOnlyList<string> args = WorkloadRunner.BuildInitArgs(ClusterPlan.FromSpec(tpch.Cluster), tpch);

        Assert.That(args[0], Is.EqualTo("init"));
        Assert.That(ValueAfter(args, "--workload"), Is.EqualTo("tpch"));
        Assert.That(ValueAfter(args, "--tpch-scale"), Is.EqualTo("0.1"));
        Assert.That(ValueAfter(args, "--tpch-load-connections"), Is.EqualTo("4"));

        ScenarioSpec bank = Read("  kind: bank");
        IReadOnlyList<string> bankArgs = WorkloadRunner.BuildInitArgs(ClusterPlan.FromSpec(bank.Cluster), bank);
        Assert.That(bankArgs, Does.Not.Contain("--workload"), "init seeds the same dataset for every other kind");
        Assert.That(bankArgs, Does.Not.Contain("--tpch-scale"));
    }

    [Test]
    public void RunPassesTheScaleAndTheQuerySelection()
    {
        ScenarioSpec spec = Read("  kind: tpch\n  tpch_scale: 0.1\n  tpch_queries: 1-6, 14\n" + Shape);
        IReadOnlyList<string> args = WorkloadRunner.BuildRunArgs(ClusterPlan.FromSpec(spec.Cluster), spec, "run").Args;

        Assert.That(ValueAfter(args, "--workload"), Is.EqualTo("tpch"));
        Assert.That(ValueAfter(args, "--tpch-scale"), Is.EqualTo("0.1"));
        Assert.That(ValueAfter(args, "--tpch-queries"), Is.EqualTo("1-6,14"));
        Assert.That(ValueAfter(args, "--read-percent"), Is.EqualTo("100"));
        Assert.That(args, Does.Not.Contain("--tpch-load-connections"), "the load is init's");
    }

    [Test]
    public void DefaultsAreLeftToTheWorkload()
    {
        ScenarioSpec spec = Read("  kind: tpch\n" + Shape);
        IReadOnlyList<string> run = WorkloadRunner.BuildRunArgs(ClusterPlan.FromSpec(spec.Cluster), spec, "run").Args;
        IReadOnlyList<string> init = WorkloadRunner.BuildInitArgs(ClusterPlan.FromSpec(spec.Cluster), spec);

        Assert.That(run, Does.Not.Contain("--tpch-scale"));
        Assert.That(run, Does.Not.Contain("--tpch-queries"));
        Assert.That(init, Does.Not.Contain("--tpch-scale"));
        Assert.That(ValueAfter(init, "--workload"), Is.EqualTo("tpch"));
    }

    [Test]
    public void TpchVerdictAndReport_DeserializeFromTheWorkloadsArtifacts()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"caraxes-tpch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "reconciliation.json"), """
                {"Passed":false,"Failures":["1 query execution(s) returned rows that differ from the reference (see tpch-mismatches.jsonl)"],
                 "Tpch":{"Passed":false,"LoadVerified":true,"QueriesEnabled":22,"QueriesChecked":21,"Executions":440,
                         "Verified":437,"Errors":2,"Mismatches":1}}
                """);
            File.WriteAllText(Path.Combine(dir, "tpch.json"), """
                {"Scale":"0.1","Seed":"1847","MeasuredSeconds":600,"Executions":440,"Verified":437,"QueriesPerHour":2622,
                 "Queries":[{"Query":"Q01","ReferenceRows":4,"ReferenceMs":812.5,"Executions":20,"Verified":20,"Errors":0,
                             "Mismatches":0,"MeanMs":790.1,"P50Ms":781,"P95Ms":850,"P99Ms":866,"MaxMs":866.4}]}
                """);

            ReconciliationSummary r = WorkloadArtifacts.ReadReconciliation(dir)!;
            Assert.That(r.Passed, Is.False);
            Assert.That(r.Tpch!.QueriesChecked, Is.EqualTo(21));
            Assert.That(r.Tpch.Mismatches, Is.EqualTo(1));
            Assert.That(r.Tpch.Verified, Is.EqualTo(437));

            TpchReport report = WorkloadArtifacts.ReadTpch(dir)!;
            Assert.That(report.Scale, Is.EqualTo("0.1"));
            Assert.That(report.QueriesPerHour, Is.EqualTo(2622));
            Assert.That(report.Queries.Single().Query, Is.EqualTo("Q01"));
            Assert.That(report.Queries.Single().P99Ms, Is.EqualTo(866));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
