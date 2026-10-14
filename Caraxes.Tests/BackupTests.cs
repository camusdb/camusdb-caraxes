/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Text.Json;
using System.Text.Json.Serialization;
using NUnit.Framework;
using Caraxes.Core.Backup;
using Caraxes.Core.Cluster;
using Caraxes.Core.Nemesis;
using Caraxes.Core.Scenario;

namespace Caraxes.Tests;

[TestFixture]
public sealed class BackupSpecTests
{
    private const string Base = """
        name: b
        cluster:
          name: bc
          backups: true
        workload:
          kind: bank
          rows: 100

        """;

    [Test]
    public void BackupFault_NeedsBackupsOnTheCluster()
    {
        ScenarioException ex = Assert.Throws<ScenarioException>(() => ScenarioSpecReader.Read("""
            name: b
            cluster:
              name: bc
            nemesis:
              events:
                - { at: 10s, fault: backup, target: camus1, duration: 5s }
            """))!;
        Assert.That(ex.Message, Does.Contain("cluster.backups: true"));
    }

    [Test]
    public void BackupFault_ParsesKindAndDefaultsToIncremental()
    {
        ScenarioSpec spec = ScenarioSpecReader.Read(Base + """
            nemesis:
              events:
                - { at: 10s, fault: backup, target: camus1, duration: 5s }
                - { at: 20s, fault: backup, target: camus2, duration: 5s, backup_kind: coordinated }
            """);

        Assert.That(spec.Nemesis!.Events[0].BackupKind, Is.EqualTo("incremental"));
        Assert.That(spec.Nemesis.Events[1].BackupKind, Is.EqualTo("coordinated"));
        Assert.That(FaultFactory.Create(spec.Nemesis.Events[0], new HttpProbes()), Is.InstanceOf<BackupFault>());
    }

    [Test]
    public void BackupKind_IsChecked()
    {
        NemesisException ex = Assert.Throws<NemesisException>(() => ScenarioSpecReader.Read(Base + """
            nemesis:
              events:
                - { at: 10s, fault: backup, target: camus1, backup_kind: differential }
            """))!;
        Assert.That(ex.Message, Does.Contain("differential"));
    }

    [Test]
    public void BackupVerify_DefaultsAndNodeDerivation()
    {
        ScenarioSpec spec = ScenarioSpecReader.Read(Base + """
            nemesis:
              events:
                - { at: 10s, fault: backup, target: camus2, duration: 5s }
                - { at: 20s, fault: backup, target: camus2, duration: 5s }
                - { at: 30s, fault: backup, target: camus1, duration: 5s }
            backup_verify: {}
            """);

        BackupVerifySpec verify = spec.BackupVerify!;
        Assert.That(verify.FinalIncremental, Is.True);
        Assert.That(verify.Restore, Is.True);
        Assert.That(verify.Boot, Is.True);
        Assert.That(verify.RequireUnbrokenChain, Is.True);
        Assert.That(verify.EffectiveNode(spec), Is.EqualTo("camus2"), "the node the schedule names most");
    }

    [Test]
    public void BackupVerify_FallsBackToCamus1WithoutASchedule()
    {
        ScenarioSpec spec = ScenarioSpecReader.Read(Base + "backup_verify: {}\n");
        Assert.That(spec.BackupVerify!.EffectiveNode(spec), Is.EqualTo("camus1"));
    }

    [Test]
    public void BackupVerify_NeedsBackups()
    {
        ScenarioException ex = Assert.Throws<ScenarioException>(() => ScenarioSpecReader.Read("""
            name: b
            cluster:
              name: bc
            backup_verify: {}
            """))!;
        Assert.That(ex.Message, Does.Contain("cluster.backups"));
    }

    [Test]
    public void BackupVerify_BootNeedsEveryNodeToHostEveryRange()
    {
        ScenarioException ex = Assert.Throws<ScenarioException>(() => ScenarioSpecReader.Read("""
            name: b
            cluster:
              name: bc
              nodes: 5
              replication_factor: 3
              backups: true
            backup_verify: {}
            """))!;
        Assert.That(ex.Message, Does.Contain("replication_factor"));

        Assert.DoesNotThrow(() => ScenarioSpecReader.Read("""
            name: b
            cluster:
              name: bc
              nodes: 5
              replication_factor: 3
              backups: true
            backup_verify:
              boot: false
            """));
    }

    [Test]
    public void BackupVerify_BootNeedsTeardown()
    {
        ScenarioException ex = Assert.Throws<ScenarioException>(() => ScenarioSpecReader.Read(Base + """
            teardown: false
            backup_verify: {}
            """))!;
        Assert.That(ex.Message, Does.Contain("teardown"));
    }

    [Test]
    public void BackupVerify_UnknownKeyAndUnknownNode_AreRejected()
    {
        Assert.That(Assert.Throws<ScenarioException>(() => ScenarioSpecReader.Read(Base + "backup_verify:\n  nodes: camus1\n"))!.Message,
            Does.Contain("backup_verify.nodes"));
        Assert.That(Assert.Throws<ScenarioException>(() => ScenarioSpecReader.Read(Base + "backup_verify:\n  node: camus9\n"))!.Message,
            Does.Contain("camus9"));
    }

    [Test]
    public void ThroughputUnderFaultFraction_ParsesAndIsBounded()
    {
        ScenarioSpec spec = ScenarioSpecReader.Read(Base + "checks:\n  min_throughput_under_fault_fraction: 0.8\n");
        Assert.That(spec.Checks.MinThroughputUnderFaultFraction, Is.EqualTo(0.8));
        Assert.That(new ChecksSpec().MinThroughputUnderFaultFraction, Is.EqualTo(0), "off by default");
        Assert.Throws<ScenarioException>(() => ScenarioSpecReader.Read(Base + "checks:\n  min_throughput_under_fault_fraction: 1.5\n"));
    }
}

[TestFixture]
public sealed class BackupGeneratorTests
{
    private static ClusterPlan Plan(string extra = "") => ClusterPlan.FromSpec(ClusterSpecReader.Read("name: bk\n" + extra));

    [Test]
    public void ConfigNamesTheBackupRootsOnlyWhenEnabled()
    {
        string off = NodeConfigGenerator.Generate(Plan(), Plan().Nodes[0]);
        Assert.That(off, Does.Not.Contain("backup_dir"));

        ClusterPlan plan = Plan("backups: true");
        string on = NodeConfigGenerator.Generate(plan, plan.Nodes[1]);
        Assert.That(on, Does.Contain("backup_dir: /backups/catalog"));
        Assert.That(on, Does.Contain("restore_root: /backups/restore"));
        Assert.That(on, Does.Contain("backup_cluster_id: bk"));
    }

    [Test]
    public void PassthroughOutranksTheModeledBackupKeys()
    {
        ClusterPlan plan = Plan("backups: true\nkahuna:\n  backup_cluster_id: shared\n  pitr_window_seconds: 7200");
        string yml = NodeConfigGenerator.Generate(plan, plan.Nodes[0]);
        Assert.That(yml, Does.Contain("backup_cluster_id: shared"));
        Assert.That(yml, Does.Not.Contain("backup_cluster_id: bk"));
        Assert.That(yml, Does.Contain("pitr_window_seconds: 7200"));
    }

    [Test]
    public void ComposeMountsTheBackupsDirectoryPerNode()
    {
        ClusterPlan plan = Plan("backups: true");
        string yml = ComposeGenerator.Generate(plan, "./config", backupsDirInCompose: "/runs/s/backups");
        Assert.That(yml, Does.Contain("/runs/s/backups/camus2:/backups"));
        Assert.That(yml, Does.Contain("camus2-data:/data"), "the data mount is unchanged");

        Assert.Throws<ArgumentException>(() => ComposeGenerator.Generate(plan, "./config"), "backups on needs a directory to mount");
        Assert.That(ComposeGenerator.Generate(Plan(), "./config"), Does.Not.Contain("/backups"));
    }

    [Test]
    public void RestoredComposeBindMountsEveryDataRootAndDropsVolumes()
    {
        ClusterPlan plan = Plan("data_tmpfs_mb: 512");
        string yml = ComposeGenerator.Generate(plan, "./config-restored", restoredDataDirInCompose: "/runs/s/backups/restored");
        Assert.That(yml, Does.Contain("/runs/s/backups/restored/camus3:/data"));
        Assert.That(yml, Does.Not.Contain("tmpfs"), "a tmpfs cannot be seeded before the container starts");
        Assert.That(yml, Does.Not.Contain("camus1-data"));
    }

    [Test]
    public void CloneIsIndependent()
    {
        ClusterSpec spec = ClusterSpecReader.Read("name: bk\nbackups: true\nkahuna:\n  a: 1");
        ClusterSpec copy = spec.Clone();
        copy.Backups = false;
        copy.Kahuna["b"] = 2;
        Assert.That(spec.Backups, Is.True);
        Assert.That(spec.Kahuna, Does.Not.ContainKey("b"));
    }
}

[TestFixture]
public sealed class BackupClientTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private static BackupInfo Info(string id, string type, DateTime at, bool invalid = false, string? parent = null, string? substitution = null) => new()
    {
        BackupId = id, Type = type, CreatedAtUtc = at, IsInvalid = invalid, ParentBackupId = parent, PartitionCount = 2,
        RequestedKind = substitution is null ? type : "Incremental", ActualKind = type, SubstitutionReason = substitution,
    };

    [Test]
    public void NewestValid_SkipsUnreadableManifests()
    {
        DateTime t = new(2026, 10, 14, 10, 0, 0, DateTimeKind.Utc);
        var catalog = new[]
        {
            Info("a", "Full", t),
            Info("b", "Incremental", t.AddMinutes(5)),
            Info("c", "Incremental", t.AddMinutes(9), invalid: true),
        };
        Assert.That(BackupClient.NewestValid(catalog)!.BackupId, Is.EqualTo("b"));
        Assert.That(BackupClient.NewestValid([]), Is.Null);
    }

    [Test]
    public void ResponsesDeserializeFromCamusDbShapes()
    {
        BackupResponse ok = JsonSerializer.Deserialize<BackupResponse>("""
            {"status":"ok","backup":{"backupId":"0f6c8a1e-1111-2222-3333-444444444444","formatVersion":3,"type":"Incremental",
             "createdAtUtc":"2026-10-14T10:05:00Z","parentBackupId":"aaaaaaaa-0000-0000-0000-000000000000","partitionCount":2,
             "requestedKind":"Incremental","actualKind":"Incremental","isInvalid":false}}
            """, Options)!;
        Assert.That(ok.Ok, Is.True);
        Assert.That(ok.Backup!.IsIncremental, Is.True);
        Assert.That(ok.Backup.ShortId, Is.EqualTo("0f6c8a1e"));

        BackupResponse failed = JsonSerializer.Deserialize<BackupResponse>("""{"status":"failed","code":"CADB070E","message":"not the coordinator"}""", Options)!;
        Assert.That(failed.Ok, Is.False);
        Assert.That(failed.Code, Is.EqualTo(BackupClient.NotCoordinatorCode));

        RestoreResponse restore = JsonSerializer.Deserialize<RestoreResponse>("""
            {"status":"ok","dataRoot":"/backups/restore/x","partitionsRestored":2,"entriesApplied":"1200","lastAppliedPhysicalMs":1760436300000,
             "minRecoverablePhysicalMs":1760436000000,"maxRecoverablePhysicalMs":1760436300000,"chain":[{"backupId":"a","type":"Full"},{"backupId":"b","type":"Incremental"}]}
            """, Options)!;
        Assert.That(restore.Ok, Is.True);
        Assert.That(restore.EntriesApplied, Is.EqualTo(1200));
        Assert.That(restore.Chain, Has.Count.EqualTo(2));
    }

    [Test]
    public void Describe_NamesASubstitutionLoudly()
    {
        NodePlan node = ClusterPlan.FromSpec(ClusterSpecReader.Read("name: d")).Nodes[0];
        BackupInfo full = Info("deadbeef-0000-0000-0000-000000000000", "Full", DateTime.UtcNow, substitution: "parent aged out");
        string line = new BackupTaken(node, "incremental", full, TimeSpan.FromSeconds(1.5), "").Describe();
        Assert.That(line, Does.Contain("incremental taken as Full"));
        Assert.That(line, Does.Contain("SUBSTITUTED: parent aged out"));
        Assert.That(line, Does.Contain("1.50 s"));
    }
}

[TestFixture]
public sealed class BackupVerifierTests
{
    private static BackupInfo Link(string id, string type, bool invalid = false, string? substitution = null, int partitions = 2) => new()
    {
        BackupId = id, Type = type, IsInvalid = invalid, PartitionCount = partitions, SubstitutionReason = substitution,
        RequestedKind = substitution is null ? type : "Incremental", ActualKind = type, InvalidReason = invalid ? "bad digest" : null,
    };

    [Test]
    public void Chain_PassesWhenWellFormedAndComplete()
    {
        var chain = new[] { Link("a", "Full"), Link("b", "Incremental"), Link("c", "Incremental") };
        Assert.That(BackupVerifier.CheckChain(chain, expectedLength: 3, catalogCount: 3, requireUnbroken: true), Is.Empty);
    }

    [Test]
    public void Chain_FailsOnBadRootInvalidLinkSubstitutionAndShortChain()
    {
        var chain = new[] { Link("a", "Incremental"), Link("b", "Full", substitution: "floor advanced"), Link("c", "Incremental", invalid: true) };
        IReadOnlyList<string> failures = BackupVerifier.CheckChain(chain, expectedLength: 5, catalogCount: 5, requireUnbroken: true);

        Assert.That(failures, Has.Some.Contains("root a is Incremental"));
        Assert.That(failures, Has.Some.Contains("link b is Full"));
        Assert.That(failures, Has.Some.Contains("link c is invalid"));
        Assert.That(failures, Has.Some.Contains("floor advanced"));
        Assert.That(failures, Has.Some.Contains("has 3 link(s) but the run took 5"));
    }

    [Test]
    public void Chain_ShortChainIsToleratedWhenNotRequiredUnbroken()
    {
        var chain = new[] { Link("a", "Full"), Link("b", "Incremental") };
        Assert.That(BackupVerifier.CheckChain(chain, expectedLength: 5, catalogCount: 5, requireUnbroken: false), Is.Empty);
        Assert.That(BackupVerifier.CheckChain([], 1, 0, true), Has.Some.Contains("resolved to nothing"));
    }

    [Test]
    public void Restore_MustCoverThePartitionsAndReplayTheIncrementals()
    {
        var chain = new[] { Link("a", "Full"), Link("b", "Incremental") };
        RestoreResponse good = new() { Status = "ok", PartitionsRestored = 2, EntriesApplied = 10, Chain = [new(), new()] };
        Assert.That(BackupVerifier.CheckRestore(good, chain), Is.Empty);

        RestoreResponse empty = new() { Status = "ok", PartitionsRestored = 1, EntriesApplied = 0, Chain = [new()] };
        IReadOnlyList<string> failures = BackupVerifier.CheckRestore(empty, chain);
        Assert.That(failures, Has.Some.Contains("into 1 partition(s) but the chain's root covers 2"));
        Assert.That(failures, Has.Some.Contains("replayed 0 WAL entries"));
        Assert.That(failures, Has.Some.Contains("chain of 1 while the resolved chain has 2"));
    }

    [Test]
    public void Restore_OfAFullAloneReplaysNothingAndThatIsFine()
    {
        // The engine counts a partition as restored when a segment was replayed into it (run preflight
        // v1, 2026-10-14: a one-link chain restored with 0 partitions, 0 entries, and booted).
        var chain = new[] { Link("a", "Full") };
        RestoreResponse fullOnly = new() { Status = "ok", PartitionsRestored = 0, EntriesApplied = 0, Chain = [new()] };
        Assert.That(BackupVerifier.CheckRestore(fullOnly, chain), Is.Empty);
    }

    [Test]
    public void TimelineCount_SeesInjectsOnTheNodeAndErrorsAnywhere()
    {
        string path = Path.Combine(Path.GetTempPath(), $"caraxes-tl-{Guid.NewGuid():N}.jsonl");
        File.WriteAllLines(path,
        [
            """{"ts":"2026-10-14T10:00:00Z","offsetSeconds":0,"phase":"note","kind":"backup","target":null,"detail":"start"}""",
            """{"ts":"2026-10-14T10:01:00Z","offsetSeconds":60,"phase":"inject","kind":"backup","target":"camus1","detail":"full backup"}""",
            """{"ts":"2026-10-14T10:01:05Z","offsetSeconds":65,"phase":"heal","kind":"backup","target":"camus1","detail":"healed"}""",
            """{"ts":"2026-10-14T10:02:00Z","offsetSeconds":120,"phase":"inject","kind":"backup","target":"camus2","detail":"incremental"}""",
            """{"ts":"2026-10-14T10:03:00Z","offsetSeconds":180,"phase":"error","kind":"backup","target":"camus1","detail":"inject failed: CADB0700"}""",
            """{"ts":"2026-10-14T10:04:00Z","offsetSeconds":240,"phase":"inject","kind":"kill","target":"camus1","detail":"SIGKILL"}""",
            "not json",
        ]);
        try
        {
            Assert.That(BackupVerifier.CountTimelineBackups(path, "camus1"), Is.EqualTo((1, 1)));
            Assert.That(BackupVerifier.CountTimelineBackups(path + ".missing", "camus1"), Is.EqualTo((0, 0)));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

[TestFixture]
public sealed class LedgerProbeTests
{
    [Test]
    public void ParsesThePositionalAggregateRow()
    {
        LedgerFingerprint f = LedgerProbe.Parse(
            """{"status":"ok","total":1,"columns":[{"name":"count"},{"name":"sum"},{"name":"sum"},{"name":"max"}],"rows":[[2000,"2999501234",41820,97]],"serverTimeMs":3.2}""",
            "camus1", "workload_accounts");
        Assert.That(f, Is.EqualTo(new LedgerFingerprint(2000, 2999501234, 41820, 97)));
        Assert.That(f.Describe(), Does.Contain("SUM(version) 41,820"));
    }

    [Test]
    public void FailureEnvelopeAndMissingRowThrow()
    {
        Assert.That(Assert.Throws<InvalidOperationException>(() => LedgerProbe.Parse("""{"status":"failed","code":"CADB0001","message":"no such table"}""", "camus1", "t"))!.Message,
            Does.Contain("CADB0001"));
        Assert.Throws<InvalidOperationException>(() => LedgerProbe.Parse("""{"status":"ok","rows":[]}""", "camus1", "t"));
    }

    [Test]
    public void TableNamesFollowTheWorkloadLayout()
    {
        Assert.That(LedgerProbe.TableNames(1), Is.EqualTo(new[] { "workload_accounts" }));
        Assert.That(LedgerProbe.TableNames(3), Is.EqualTo(new[] { "workload_accounts_00", "workload_accounts_01", "workload_accounts_02" }));
    }

    [Test]
    public void FingerprintsAddAcrossTables()
    {
        LedgerFingerprint sum = new LedgerFingerprint(10, 100, 5, 3) + new LedgerFingerprint(20, 200, 7, 9);
        Assert.That(sum, Is.EqualTo(new LedgerFingerprint(30, 300, 12, 9)));
    }
}

[TestFixture]
public sealed class ShippedScenariosTests
{
    /// <summary>The repository root, found from the test assembly by walking up to the solution file.</summary>
    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Caraxes.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Caraxes.slnx not found above the test assembly");
    }

    [Test]
    public void EveryShippedScenarioReads()
    {
        string scenarios = Path.Combine(RepoRoot(), "scenarios");
        List<string> failures = [];
        int read = 0;
        foreach (string path in Directory.GetFiles(scenarios, "*.yml").OrderBy(p => p))
        {
            // Matrix files and bare cluster specs (no `cluster:` block of their own) share the
            // directory and have their own readers.
            string file = Path.GetFileName(path);
            if (file.StartsWith("matrix-", StringComparison.Ordinal)
                || !File.ReadLines(path).Any(line => line.StartsWith("cluster:", StringComparison.Ordinal)))
                continue;
            try
            {
                ScenarioSpecReader.ReadFile(path);
                read++;
            }
            catch (Exception e)
            {
                failures.Add($"{Path.GetFileName(path)}: {e.Message}");
            }
        }

        Assert.That(failures, Is.Empty, string.Join("\n", failures));
        Assert.That(read, Is.GreaterThan(0));
    }

    [Test]
    public void BackupScenarios_AreShapedAsDocumented()
    {
        string scenarios = Path.Combine(RepoRoot(), "scenarios");

        ScenarioSpec incremental = ScenarioSpecReader.ReadFile(Path.Combine(scenarios, "bank-backup-soak-nvme-rel1119pkc.yml"));
        Assert.That(incremental.Cluster.Backups, Is.True);
        Assert.That(incremental.Nemesis!.Events.Count(e => e.Fault == "backup"), Is.EqualTo(11));
        Assert.That(incremental.Nemesis.Events.All(e => e.BackupKind == "incremental"), Is.True);
        Assert.That(incremental.BackupVerify!.RequireUnbrokenChain, Is.True);
        Assert.That(incremental.Checks.MinThroughputUnderFaultFraction, Is.EqualTo(0.8));

        ScenarioSpec full = ScenarioSpecReader.ReadFile(Path.Combine(scenarios, "bank-backup-soak-nvme-rel1119pkc-full.yml"));
        Assert.That(full.Nemesis!.Events.Count(e => e.Fault == "backup" && e.BackupKind == "full"), Is.EqualTo(11));
        Assert.That(full.BackupVerify!.RequireUnbrokenChain, Is.False);
        Assert.That(full.Cluster.Name, Is.Not.EqualTo(incremental.Cluster.Name), "the two arms must not share a cluster directory");
    }
}
