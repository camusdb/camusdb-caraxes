/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using NUnit.Framework;
using Caraxes.Core.Cluster;

namespace Caraxes.Tests;

[TestFixture]
public sealed class ClusterSpecTests
{
    [Test]
    public void MinimalSpec_GetsDefaults()
    {
        ClusterSpec spec = ClusterSpecReader.Read("name: smoke");

        Assert.That(spec.Nodes, Is.EqualTo(3));
        Assert.That(spec.Partitions, Is.EqualTo(3));
        Assert.That(spec.ReplicationFactor, Is.EqualTo(3), "RF=3 is the standard test posture");
        Assert.That(spec.PlacementRebalancer, Is.True);
        Assert.That(spec.LeaderBalancer, Is.True, "leader balancer on is the standard test posture");
        Assert.That(spec.Locking, Is.EqualTo("optimistic"));
        Assert.That(spec.Isolation, Is.EqualTo("read_committed"));
        Assert.That(spec.Subnet, Is.EqualTo("10.101.0"), "default subnet must not collide with docker/local.yml's 172.31.0");
        Assert.That(spec.Diagnostics, Is.True);
        Assert.That(spec.EffectiveImage, Is.EqualTo("caraxes/camusdb:smoke"));
    }

    [Test]
    public void UnknownKey_IsRejected()
    {
        ClusterSpecException ex = Assert.Throws<ClusterSpecException>(
            () => ClusterSpecReader.Read("name: x\nreplication_facto: 3"))!;
        Assert.That(ex.Message, Does.Contain("replication_facto"));
    }

    [Test]
    public void InvalidName_IsRejected()
    {
        Assert.Throws<ClusterSpecException>(() => ClusterSpecReader.Read("name: Bad_Name"));
        Assert.Throws<ClusterSpecException>(() => ClusterSpecReader.Read("nodes: 3"));
    }

    [Test]
    public void ZonesMustBeParallelToNodes()
    {
        ClusterSpecException ex = Assert.Throws<ClusterSpecException>(
            () => ClusterSpecReader.Read("name: x\nnodes: 3\nzones: [a, b]"))!;
        Assert.That(ex.Message, Does.Contain("zones"));
    }

    [Test]
    public void InvalidLockingAndIsolation_AreRejected()
    {
        Assert.Throws<ClusterSpecException>(() => ClusterSpecReader.Read("name: x\nlocking: hopeful"));
        Assert.Throws<ClusterSpecException>(() => ClusterSpecReader.Read("name: x\nisolation: eventual"));
    }

    [Test]
    public void TildeInCamusdbRepo_Expands()
    {
        ClusterSpec spec = ClusterSpecReader.Read("name: x");
        Assert.That(spec.EffectiveCamusdbRepo, Does.Not.StartWith("~"));
        Assert.That(spec.EffectiveCamusdbRepo, Does.EndWith("camusdb"));
    }
}

[TestFixture]
public sealed class ClusterPlanTests
{
    private static ClusterPlan ThreeNodePlan() => ClusterPlan.FromSpec(ClusterSpecReader.Read("name: plan-test"));

    [Test]
    public void DerivesLocalYmlConventions()
    {
        ClusterPlan plan = ThreeNodePlan();

        Assert.That(plan.Nodes, Has.Count.EqualTo(3));
        Assert.That(plan.ProjectName, Is.EqualTo("caraxes-plan-test"));
        Assert.That(plan.NetworkSubnetCidr, Is.EqualTo("10.101.0.0/24"));

        NodePlan node2 = plan.Nodes[1];
        Assert.That(node2.Name, Is.EqualTo("camus2"));
        Assert.That(node2.ContainerName, Is.EqualTo("plan-test-camus2"));
        Assert.That(node2.Ip, Is.EqualTo("10.101.0.3"));
        Assert.That(node2.RaftPort, Is.EqualTo(7072), "raft ports step by 2, matching docker/local.yml");
        Assert.That(node2.HostRestPort, Is.EqualTo(15096));
        Assert.That(node2.HostGrpcPort, Is.EqualTo(16096));
    }

    [Test]
    public void InitialClusterExcludesSelf()
    {
        ClusterPlan plan = ThreeNodePlan();

        Assert.That(plan.Nodes[0].InitialCluster, Is.EqualTo("10.101.0.3:7072 10.101.0.4:7074"));
        Assert.That(plan.Nodes[1].InitialCluster, Is.EqualTo("10.101.0.2:7070 10.101.0.4:7074"));
        Assert.That(plan.Nodes[2].InitialCluster, Is.EqualTo("10.101.0.2:7070 10.101.0.3:7072"));
    }

    [Test]
    public void WorkloadEndpointPool_ListsEveryGrpcHostPort()
    {
        ClusterPlan plan = ThreeNodePlan();
        Assert.That(plan.WorkloadEndpointPool,
            Is.EqualTo("https://localhost:16095,https://localhost:16096,https://localhost:16097"));
    }

    [Test]
    public void ZonesFlowToNodes()
    {
        ClusterPlan plan = ClusterPlan.FromSpec(
            ClusterSpecReader.Read("name: zoned\nnodes: 3\nzones: [rack-a, rack-b, rack-c]"));

        Assert.That(plan.Nodes.Select(n => n.Zone), Is.EqualTo(new[] { "rack-a", "rack-b", "rack-c" }));
    }
}
