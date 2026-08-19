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
public sealed class NodeConfigGeneratorTests
{
    [Test]
    public void EmitsSpecShapedConfig()
    {
        ClusterPlan plan = ClusterPlan.FromSpec(ClusterSpecReader.Read(string.Join('\n',
            "name: cfg",
            "locking: pessimistic",
            "isolation: serializable",
            "key_range_sharding: true",
            "distributed_query_execution: true",
            "max_query_parallelism: 4")));

        string yml = NodeConfigGenerator.Generate(plan, plan.Nodes[0]);

        Assert.That(yml, Does.Contain("data_dir: /data/"));
        Assert.That(yml, Does.Contain("default_transaction_locking: pessimistic"));
        Assert.That(yml, Does.Contain("default_isolation_level: serializable"));
        Assert.That(yml, Does.Contain("key_range_sharding: true"));
        Assert.That(yml, Does.Contain("distributed_query_execution: true"));
        Assert.That(yml, Does.Contain("max_query_parallelism: 4"));
        Assert.That(yml, Does.Contain("replication_factor: 3"));
        Assert.That(yml, Does.Contain("enable_placement_rebalancer: true"));
        Assert.That(yml, Does.Contain("enable_leader_balancer: true"));
        Assert.That(yml, Does.Contain("prometheus_enabled: true"));
        Assert.That(yml, Does.Not.Contain("zone:"), "no zones configured");
    }

    [Test]
    public void ZoneIsPerNode()
    {
        ClusterPlan plan = ClusterPlan.FromSpec(
            ClusterSpecReader.Read("name: zoned\nnodes: 2\nzones: [left, right]"));

        Assert.That(NodeConfigGenerator.Generate(plan, plan.Nodes[0]), Does.Contain("zone: left"));
        Assert.That(NodeConfigGenerator.Generate(plan, plan.Nodes[1]), Does.Contain("zone: right"));
    }

    [Test]
    public void KahunaPassthroughWinsOverModeledFields()
    {
        ClusterPlan plan = ClusterPlan.FromSpec(ClusterSpecReader.Read(string.Join('\n',
            "name: raw",
            "kahuna:",
            "  replication_factor: 5",
            "  heartbeat_interval_ms: 250")));

        string yml = NodeConfigGenerator.Generate(plan, plan.Nodes[0]);

        Assert.That(yml, Does.Contain("replication_factor: 5"), "explicit kahuna key overrides the modeled field");
        Assert.That(yml, Does.Not.Contain("replication_factor: 3"));
        Assert.That(yml, Does.Contain("heartbeat_interval_ms: 250"));
    }
}

[TestFixture]
public sealed class ComposeGeneratorTests
{
    [Test]
    public void EmitsRuntimeIdentityAndMounts()
    {
        ClusterPlan plan = ClusterPlan.FromSpec(ClusterSpecReader.Read("name: comp"));
        string yml = ComposeGenerator.Generate(plan, "./config");

        Assert.That(yml, Does.Contain("name: caraxes-comp"));
        Assert.That(yml, Does.Contain("image: caraxes/camusdb:comp"));
        Assert.That(yml, Does.Contain("container_name: comp-camus1"));
        Assert.That(yml, Does.Contain("CAMUS_RAFT_NODENAME: camus2"));
        Assert.That(yml, Does.Contain("CAMUS_INITIAL_CLUSTER: 10.101.0.3:7072 10.101.0.4:7074"));
        Assert.That(yml, Does.Contain("CAMUS_CONFIG_PATH: /app/caraxes-config/camus1.yml"));
        Assert.That(yml, Does.Contain("- NET_ADMIN"));
        Assert.That(yml, Does.Contain("15095:5095"));
        Assert.That(yml, Does.Contain("16097:5096"));
        Assert.That(yml, Does.Contain("./config:/app/caraxes-config:ro"));
        Assert.That(yml, Does.Contain("subnet: 10.101.0.0/24"));
        Assert.That(yml, Does.Contain("ipv4_address: 10.101.0.4"));
        Assert.That(yml, Does.Contain("camus3-data:/data"));
    }

    [Test]
    public void NoSingleFileMounts()
    {
        // Single-file bind mounts pin content across host-side regeneration on Docker Desktop:
        // a mounted .pfx served a previous generation's bytes and broke inter-node TLS with
        // UntrustedRoot. Certs are baked into the image; configs are mounted as a directory.
        ClusterPlan plan = ClusterPlan.FromSpec(ClusterSpecReader.Read("name: comp"));
        string yml = ComposeGenerator.Generate(plan, "./config");

        Assert.That(yml, Does.Not.Contain(".pfx"));
        Assert.That(yml, Does.Not.Contain(".yml:/"));
    }

    [Test]
    public void NamedVolumeByDefault()
    {
        ClusterPlan plan = ClusterPlan.FromSpec(ClusterSpecReader.Read("name: comp"));
        string yml = ComposeGenerator.Generate(plan, "./config");

        Assert.That(yml, Does.Contain("camus1-data:/data"));
        Assert.That(yml, Does.Not.Contain("tmpfs"));
    }

    [Test]
    public void DataTmpfsMountWhenRequested()
    {
        ClusterPlan plan = ClusterPlan.FromSpec(ClusterSpecReader.Read("name: comp\ndata_tmpfs_mb: 256"));
        string yml = ComposeGenerator.Generate(plan, "./config");

        Assert.That(yml, Does.Contain("tmpfs"));
        Assert.That(yml, Does.Contain("268435456"), "256 MiB in bytes");
        Assert.That(yml, Does.Contain("target: /data"));
        Assert.That(yml, Does.Not.Contain("camus1-data:/data"), "no named volume when tmpfs is used");
    }

    [Test]
    public void KillsStayKilled()
    {
        ClusterPlan plan = ClusterPlan.FromSpec(ClusterSpecReader.Read("name: comp"));
        string yml = ComposeGenerator.Generate(plan, "./config");

        // A restart policy would silently undo nemesis kills mid-scenario.
        Assert.That(yml, Does.Contain("restart: \"no\"").Or.Contain("restart: no"));
        Assert.That(yml, Does.Not.Contain("restart: always"));
    }
}

[TestFixture]
public sealed class CertProvisionerTests
{
    [Test]
    public void FingerprintCoversSpareAndSubnet()
    {
        ClusterSpec spec = ClusterSpecReader.Read("name: certs\nnodes: 5\nspare_certs: 2\nfirst_ip: 10");
        Assert.That(CertProvisioner.SanFingerprint(spec), Is.EqualTo("nodes=7;subnet=10.101.0;first_ip=10"));
    }

    [Test]
    public void AddingNodesWithinSpare_KeepsFingerprintDifferent()
    {
        // The fingerprint is parameter-based, not coverage-based: growing the node count always
        // regenerates, which errs toward valid certs at the cost of an occasional extra rebuild.
        ClusterSpec small = ClusterSpecReader.Read("name: certs\nnodes: 3");
        ClusterSpec grown = ClusterSpecReader.Read("name: certs\nnodes: 4");
        Assert.That(CertProvisioner.SanFingerprint(small), Is.Not.EqualTo(CertProvisioner.SanFingerprint(grown)));
    }
}
