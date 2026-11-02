/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using NUnit.Framework;
using Caraxes.Core.Cluster;
using Caraxes.Core.Matrix;
using Caraxes.Core.Scenario;
using Caraxes.Core.Workload;

namespace Caraxes.Tests;

/// <summary>
/// The rw-register and predicate arms: what the scenario accepts, what reaches the workload's command
/// line, how a matrix sweeps the isolation and wire-mode axes, and how the artifacts are read. None of
/// it needs Docker.
/// </summary>
[TestFixture]
public sealed class ElleArmsTests
{
    private static string? ValueAfter(IReadOnlyList<string> args, string flag)
    {
        int at = args.ToList().IndexOf(flag);
        return at < 0 || at + 1 >= args.Count ? null : args[at + 1];
    }

    private static IReadOnlyList<string> ArgsOf(string yml)
    {
        ScenarioSpec scenario = ScenarioSpecReader.Read(yml);
        return WorkloadRunner.BuildRunArgs(ClusterPlan.FromSpec(scenario.Cluster), scenario, "run").Args;
    }

    [Test]
    public void RegisterKindPassesItsSettingsAndTheWireMode()
    {
        IReadOnlyList<string> args = ArgsOf("""
            name: rw-test
            cluster:
              name: t
              nodes: 1
              replication_factor: 1
              isolation: serializable
            workload:
              kind: rw
              rw_keys: 6
              rw_max_writes_per_key: 16
              rw_key_type: string
              rw_access: join
              txn_mode: pipeline
              elle_key_distribution: exponential
              stop_after_completed: 1000000
            """);

        Assert.That(ValueAfter(args, "--workload"), Is.EqualTo("rw"));
        Assert.That(ValueAfter(args, "--rw-keys"), Is.EqualTo("6"));
        Assert.That(ValueAfter(args, "--rw-max-writes-per-key"), Is.EqualTo("16"));
        Assert.That(args, Does.Not.Contain("--rw-max-txn-length"), "0 must leave the workload default");
        Assert.That(ValueAfter(args, "--rw-key-type"), Is.EqualTo("string"));
        Assert.That(ValueAfter(args, "--rw-access"), Is.EqualTo("join"));
        Assert.That(ValueAfter(args, "--txn-mode"), Is.EqualTo("pipeline"));
        Assert.That(ValueAfter(args, "--elle-key-distribution"), Is.EqualTo("exponential"));
        Assert.That(ValueAfter(args, "--stop-after-completed"), Is.EqualTo("1000000"));
        Assert.That(ValueAfter(args, "--isolation"), Is.EqualTo("serializable"));
    }

    [Test]
    public void PredicateKindPassesItsSettings()
    {
        IReadOnlyList<string> args = ArgsOf("""
            name: pred-test
            cluster:
              name: t
              nodes: 3
            workload:
              kind: predicate
              predicate_groups: 4
              predicate_group_size: 16
              predicate_max_writes_per_group: 20
              predicate_max_txn_length: 2
              predicate_read: range
              txn_mode: autocommit
            """);

        Assert.That(ValueAfter(args, "--workload"), Is.EqualTo("predicate"));
        Assert.That(ValueAfter(args, "--predicate-groups"), Is.EqualTo("4"));
        Assert.That(ValueAfter(args, "--predicate-group-size"), Is.EqualTo("16"));
        Assert.That(ValueAfter(args, "--predicate-max-writes-per-group"), Is.EqualTo("20"));
        Assert.That(ValueAfter(args, "--predicate-max-txn-length"), Is.EqualTo("2"));
        Assert.That(ValueAfter(args, "--predicate-read"), Is.EqualTo("range"));
        Assert.That(ValueAfter(args, "--txn-mode"), Is.EqualTo("autocommit"));
    }

    [Test]
    public void ExplicitDefaultsPassNoElleFlags()
    {
        IReadOnlyList<string> args = ArgsOf("""
            name: append-test
            cluster:
              name: t
              nodes: 3
            workload:
              kind: append
            """);

        Assert.That(args, Does.Not.Contain("--txn-mode"), "an older workload build must still run the explicit arm");
        Assert.That(args, Does.Not.Contain("--elle-key-distribution"));
        Assert.That(args, Does.Not.Contain("--stop-after-completed"));
    }

    [Test]
    public void WireModeIsSaidInTheRunNotes()
    {
        ScenarioSpec scenario = ScenarioSpecReader.Read("""
            name: append-pipe
            cluster:
              name: t
              nodes: 3
            workload:
              kind: append
              txn_mode: pipeline
              stop_after_completed: 500
            """);
        WorkloadRunPlan plan = WorkloadRunner.BuildRunArgs(ClusterPlan.FromSpec(scenario.Cluster), scenario, "run");

        Assert.That(plan.Notes, Has.Some.Contains("pipelined frames"));
        Assert.That(plan.Notes, Has.Some.Contains("500 completed operations"));
    }

    [TestCase("kind: bank\n  rw_keys: 5", "rw_*")]
    [TestCase("kind: append\n  rw_access: index", "rw_*")]
    [TestCase("kind: rw\n  predicate_groups: 2", "predicate_*")]
    [TestCase("kind: bank\n  txn_mode: pipeline", "txn_mode")]
    [TestCase("kind: accounts\n  elle_key_distribution: uniform", "elle_key_distribution")]
    [TestCase("kind: rw\n  txn_mode: pipeline\n  rw_access: index", "pipeline")]
    [TestCase("kind: rw\n  txn_mode: pipeline\n  rw_access: secondary", "pipeline")]
    [TestCase("kind: predicate\n  predicate_group_size: 1", "predicate_group_size")]
    [TestCase("kind: rw\n  rw_key_type: float", "rw_key_type")]
    [TestCase("kind: rw\n  rw_access: scan", "rw_access")]
    [TestCase("kind: predicate\n  predicate_read: pk", "predicate_read")]
    [TestCase("kind: rw\n  txn_mode: batch", "txn_mode")]
    [TestCase("kind: rw\n  stop_after_completed: -1", "stop_after_completed")]
    public void MisplacedOrInvalidElleSettingsAreRejected(string workload, string expectedInMessage)
    {
        ScenarioException error = Assert.Throws<ScenarioException>(() => ScenarioSpecReader.Read($"""
            name: bad
            cluster:
              name: t
              nodes: 3
            workload:
              {workload}
            """))!;
        Assert.That(error.Message, Does.Contain(expectedInMessage));
    }

    [Test]
    public void PipelinedRegisterByPrimaryKeyOrJoinIsAccepted()
    {
        foreach (string access in new[] { "pk", "join" })
        {
            ScenarioSpec scenario = ScenarioSpecReader.Read($"""
                name: ok
                cluster:
                  name: t
                  nodes: 3
                workload:
                  kind: rw
                  txn_mode: pipeline
                  rw_access: {access}
                """);
            Assert.That(scenario.Workload.IsElleKind, Is.True);
        }
    }

    [Test]
    public void MinCommittedTxnsAndGatedAreScenarioKeys()
    {
        ScenarioSpec scenario = ScenarioSpecReader.Read("""
            name: volume
            cluster:
              name: t
              nodes: 1
              replication_factor: 1
            workload:
              kind: rw
              stop_after_completed: 1000000
              duration: 3h
            checks:
              min_committed_txns: 1000000
            elle:
              gated: false
              consistency_models: strict-serializable
            """);

        Assert.That(scenario.Checks.MinCommittedTxns, Is.EqualTo(1_000_000));
        Assert.That(scenario.Elle.Gated, Is.False);
        Assert.That(scenario.Elle.EffectiveConsistencyModels("serializable"), Is.EqualTo("strict-serializable"));
        Assert.Throws<ScenarioException>(() => ScenarioSpecReader.Read("""
            name: bad
            cluster:
              name: t
            workload:
              kind: rw
            checks:
              min_committed_txns: -5
            """));
    }

    [Test]
    public void MatrixSweepsIsolationAndWireMode()
    {
        MatrixSpec matrix = MatrixReader.Read("""
            name: elle-rw
            cluster:
              name: mx
              nodes: 3
            workload:
              kind: rw
            axes:
              locking: [optimistic, pessimistic]
              isolation: [read_committed, serializable]
              txn_mode: [explicit, autocommit, pipeline]
            """);

        IReadOnlyList<MatrixCell> cells = MatrixExpander.Expand(matrix);
        Assert.That(cells, Has.Count.EqualTo(12));
        Assert.That(cells.Select(c => c.Name), Is.Unique);
        Assert.That(cells.Select(c => c.Scenario.Cluster.Name), Is.Unique);

        MatrixCell cell = cells.Single(c =>
            c.Coordinates["locking"] == "pessimistic" && c.Coordinates["isolation"] == "ser" && c.Coordinates["txn_mode"] == "pipeline");
        Assert.That(cell.Name, Is.EqualTo("elle-rw-pessimistic-ser-pipeline"));
        Assert.That(cell.Scenario.Cluster.Isolation, Is.EqualTo("serializable"));
        Assert.That(cell.Scenario.EffectiveIsolation, Is.EqualTo("serializable"), "the workload inherits the cell's isolation");
        Assert.That(cell.Scenario.Workload.TxnMode, Is.EqualTo("pipeline"));
        Assert.That(cell.Scenario.Workload.Kind, Is.EqualTo("rw"));

        MatrixCell rc = cells.First(c => c.Coordinates["isolation"] == "rc");
        Assert.That(rc.Scenario.Elle.EffectiveConsistencyModels(rc.Scenario.EffectiveIsolation), Is.EqualTo("read-committed"));
    }

    [Test]
    public void MatrixCellsCarryTheElleBlockAndCanBeListedAndSelected()
    {
        MatrixSpec matrix = MatrixReader.Read("""
            name: elle-vol
            cluster:
              name: mx
              nodes: 1
              replication_factor: 1
            workload:
              kind: predicate
              stop_after_completed: 1000000
            checks:
              min_committed_txns: 1000000
            elle:
              heap_mb: 12288
              cycle_search_timeout_ms: 30000
              gated: false
            axes:
              txn_mode: [explicit, pipeline]
            """);

        IReadOnlyList<MatrixCell> cells = MatrixExpander.Expand(matrix);
        Assert.That(cells.Select(c => c.Scenario.Elle.HeapMb), Is.All.EqualTo(12288));
        Assert.That(cells.Select(c => c.Scenario.Elle.Gated), Is.All.False);
        Assert.That(cells.Select(c => c.Scenario.Checks.MinCommittedTxns), Is.All.EqualTo(1_000_000));
        Assert.That(cells.Select(c => c.Scenario.Workload.StopAfterCompleted), Is.All.EqualTo(1_000_000));
        Assert.That(ReferenceEquals(cells[0].Scenario.Elle, cells[1].Scenario.Elle), Is.False, "cells must not share the Elle block");

        Assert.That(MatrixRunner.ListCells(matrix), Is.EqualTo(new[] { "elle-vol-explicit", "elle-vol-pipeline" }));

        MatrixRunner runner = new(matrix, Path.Combine(Path.GetTempPath(), "caraxes-never-runs"));
        ScenarioException error = Assert.ThrowsAsync<ScenarioException>(
            () => runner.RunAsync(CancellationToken.None, ["elle-vol-batch"]))!;
        Assert.That(error.Message, Does.Contain("elle-vol-batch"));
        Assert.That(error.Message, Does.Contain("elle-vol-explicit"));
    }

    [Test]
    public void MatrixRefusesAWireModeAxisOnANonElleShapeAndAnUnpipelinableAccess()
    {
        Assert.Throws<ScenarioException>(() => MatrixReader.Read("""
            name: bad
            cluster:
              name: mx
            workload:
              kind: bank
            axes:
              txn_mode: [explicit, pipeline]
            """));

        Assert.Throws<ScenarioException>(() => MatrixReader.Read("""
            name: bad
            cluster:
              name: mx
            workload:
              kind: rw
              rw_access: index
            axes:
              txn_mode: [explicit, pipeline]
            """));

        Assert.Throws<ScenarioException>(() => MatrixReader.Read("""
            name: bad
            cluster:
              name: mx
            workload:
              kind: rw
            axes:
              isolation: [snapshot]
            """));
    }

    [Test]
    public void ElleFactsAndFinalStateAreReadFromTheArtifacts()
    {
        string dir = Path.Combine(Path.GetTempPath(), "caraxes-elle-arms-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "elle.json"), """
                {
                  "model": "rw-register", "shape": "rw", "txnMode": "autocommit", "isolation": "Serializable",
                  "locking": "Pessimistic", "keyDistribution": "exponential", "measuredSeconds": 61.5,
                  "invocations": 1200, "ok": 1000, "fail": 150, "info": 50, "openAtClose": 2, "processes": 20,
                  "okPerSecond": 16.26, "keysUsed": 300, "acknowledgedWrites": 600, "unresolvedWrites": 30, "failedWrites": 90
                }
                """);
            File.WriteAllText(Path.Combine(dir, "reconciliation.json"), """
                {
                  "ExpectedMin": 0, "ExpectedMax": 0, "Observed": 0, "IndeterminateTxns": 0, "VersionsMatch": true,
                  "RowCount": 10, "RowCountMatches": true, "AccountingBalances": true, "NoConflicts": true,
                  "ConflictsWaived": true, "Failures": [], "Passed": true,
                  "Elle": {
                    "Status": "verified", "Model": "rw-register", "Keys": 300, "KeysRead": 300, "OkWrites": 600,
                    "InfoWrites": 30, "FailWrites": 90, "RowsPresent": 280, "LostWrites": 0, "AbortedVisible": 0,
                    "GarbageValues": 0, "DuplicateRows": 0, "PathMismatches": 0, "RealtimeStaleFinals": 3,
                    "CountMismatches": 0, "Failures": [], "Passed": true
                  }
                }
                """);

            ElleFacts? facts = WorkloadArtifacts.ReadElleFacts(dir);
            Assert.That(facts, Is.Not.Null);
            Assert.That(facts!.Ok, Is.EqualTo(1000));
            Assert.That(facts.TxnMode, Is.EqualTo("autocommit"));
            Assert.That(facts.MeasuredSeconds, Is.EqualTo(61.5));
            Assert.That(facts.KeysUsed, Is.EqualTo(300));

            ReconciliationSummary? reconciliation = WorkloadArtifacts.ReadReconciliation(dir);
            Assert.That(reconciliation?.Elle, Is.Not.Null);
            Assert.That(reconciliation!.Elle!.RealtimeStaleFinals, Is.EqualTo(3));
            Assert.That(reconciliation.Elle.Passed, Is.True);
            Assert.That(WorkloadArtifacts.ReadElleFacts(Path.Combine(dir, "missing")), Is.Null);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void VerdictCarriesTheElleEvidence()
    {
        ScenarioVerdict verdict = new(true, 0, true, true, "/tmp/x", [])
        {
            CommittedTxns = 1_000_000,
            MeasuredSeconds = 1800,
            ElleVerdict = "true",
        };
        Assert.That(verdict.CommittedTxns, Is.EqualTo(1_000_000));
        Assert.That(verdict.ElleVerdict, Is.EqualTo("true"));
    }
}
