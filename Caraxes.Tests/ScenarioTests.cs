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
public sealed class ScenarioSpecTests
{
    private const string Minimal = """
        name: smoke
        cluster:
          name: smoke-c
        workload:
          rows: 5000
        """;

    [Test]
    public void ParsesClusterAndWorkload()
    {
        ScenarioSpec spec = ScenarioSpecReader.Read(Minimal);

        Assert.That(spec.Name, Is.EqualTo("smoke"));
        Assert.That(spec.Cluster.Name, Is.EqualTo("smoke-c"));
        Assert.That(spec.Cluster.ReplicationFactor, Is.EqualTo(3), "cluster defaults still apply inside a scenario");
        Assert.That(spec.Workload.Rows, Is.EqualTo(5000));
        Assert.That(spec.Teardown, Is.True);
        Assert.That(spec.Workload.ExpectFaults, Is.True);
    }

    [Test]
    public void WorkloadInheritsClusterLockingWhenBlank()
    {
        ScenarioSpec spec = ScenarioSpecReader.Read("""
            name: s
            cluster:
              name: c
              locking: pessimistic
              isolation: serializable
            """);

        Assert.That(spec.EffectiveLocking, Is.EqualTo("pessimistic"));
        Assert.That(spec.EffectiveIsolation, Is.EqualTo("serializable"));
    }

    [Test]
    public void WorkloadLockingOverridesCluster()
    {
        ScenarioSpec spec = ScenarioSpecReader.Read("""
            name: s
            cluster:
              name: c
              locking: pessimistic
            workload:
              locking: optimistic
            """);

        Assert.That(spec.EffectiveLocking, Is.EqualTo("optimistic"));
    }

    [Test]
    public void UnknownRootKey_IsRejected()
    {
        ScenarioException ex = Assert.Throws<ScenarioException>(
            () => ScenarioSpecReader.Read("name: s\ncluster:\n  name: c\nteardwon: true"))!;
        Assert.That(ex.Message, Does.Contain("teardwon"));
    }

    [Test]
    public void UnknownWorkloadKey_IsRejected()
    {
        ScenarioException ex = Assert.Throws<ScenarioException>(
            () => ScenarioSpecReader.Read("name: s\ncluster:\n  name: c\nworkload:\n  rowz: 10"))!;
        Assert.That(ex.Message, Does.Contain("workload.rowz"));
    }

    [Test]
    public void UnknownClusterKey_IsRejectedThroughScenario()
    {
        ScenarioException ex = Assert.Throws<ScenarioException>(
            () => ScenarioSpecReader.Read("name: s\ncluster:\n  name: c\n  replication_facto: 3"))!;
        Assert.That(ex.Message, Does.Contain("replication_facto"));
    }

    [Test]
    public void MixMustSumTo100()
    {
        Assert.Throws<ScenarioException>(() => ScenarioSpecReader.Read(
            "name: s\ncluster:\n  name: c\nworkload:\n  read_percent: 70\n  write_percent: 40"));
    }

    [Test]
    public void ReservedDatabase_IsRejected()
    {
        Assert.Throws<ScenarioException>(() => ScenarioSpecReader.Read(
            "name: s\ncluster:\n  name: c\nworkload:\n  database: system"));
    }
}

[TestFixture]
public sealed class WorkloadEndpointTests
{
    [Test]
    public void InternalPoolUsesContainerDnsAndTls()
    {
        ClusterPlan plan = ClusterPlan.FromSpec(ClusterSpecReader.Read("name: pool\nnodes: 3"));
        Assert.That(plan.InternalWorkloadEndpointPool,
            Is.EqualTo("https://camus1:5096,https://camus2:5096,https://camus3:5096"));
    }

    [Test]
    public void NetworkNameMatchesComposeProject()
    {
        ClusterPlan plan = ClusterPlan.FromSpec(ClusterSpecReader.Read("name: pool"));
        Assert.That(plan.NetworkName, Is.EqualTo("caraxes-pool_caraxes"));
    }
}

[TestFixture]
public sealed class WorkloadArtifactsTests
{
    [Test]
    public void ReadsPascalCaseSummary()
    {
        string dir = Path.Combine(Path.GetTempPath(), "caraxes-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "summary.json"), """
                { "Completed": 12345, "Conflicts": 0, "Indeterminate": 2, "InternalErrors": 0,
                  "AchievedOpsPerSec": 402.5, "Valid": true, "ValidityWarnings": ["a warning"] }
                """);

            WorkloadSummary? summary = WorkloadArtifacts.ReadSummary(dir);

            Assert.That(summary, Is.Not.Null);
            Assert.That(summary!.Completed, Is.EqualTo(12345));
            Assert.That(summary.Indeterminate, Is.EqualTo(2));
            Assert.That(summary.Valid, Is.True);
            Assert.That(summary.ValidityWarnings, Has.Count.EqualTo(1));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void MissingFile_IsNull()
    {
        Assert.That(WorkloadArtifacts.ReadSummary(Path.GetTempPath() + Guid.NewGuid()), Is.Null);
    }
}
