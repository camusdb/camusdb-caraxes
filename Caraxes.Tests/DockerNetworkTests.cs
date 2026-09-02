/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using NUnit.Framework;
using Caraxes.Core.Cluster;
using Caraxes.Core.Workload;

namespace Caraxes.Tests;

/// <summary>
/// Every Caraxes cluster is pinned to one fixed /24, so a network left behind by an interrupted run
/// blocks every later run with docker's "Pool overlaps with other one on this address space" — a
/// message that names neither the network nor the container pinning it. These tests pin the two
/// halves of the reclaim: that a leftover of this harness's own is recognised as removable, and that
/// a network Caraxes did not create never is.
/// </summary>
[TestFixture]
public sealed class DockerNetworkTests
{
    private const string Inspect =
        "caraxes-capacity-baseline_caraxes\t10.101.0.0/24 \tcaraxes-capacity-baseline-workload \n" +
        "bridge\t172.17.0.0/16 \t\n" +
        "host\t\t\n";

    [Test]
    public void ParsesNameSubnetsAndAttachedContainers()
    {
        IReadOnlyList<DockerNetwork> networks = DockerNetworks.Parse(Inspect);

        Assert.That(networks, Has.Count.EqualTo(3), "a network with neither subnet nor container is still a network");

        DockerNetwork leftover = networks[0];
        Assert.That(leftover.Name, Is.EqualTo("caraxes-capacity-baseline_caraxes"));
        Assert.That(leftover.Subnets, Is.EqualTo(new[] { "10.101.0.0/24" }));
        Assert.That(leftover.AttachedContainers, Is.EqualTo(new[] { "caraxes-capacity-baseline-workload" }),
            "the attached container is the endpoint that makes 'docker network rm' fail");

        Assert.That(networks[2].Subnets, Is.Empty);
        Assert.That(networks[2].AttachedContainers, Is.Empty);
    }

    [Test]
    public void MalformedLinesAreSkippedRatherThanThrowing()
    {
        IReadOnlyList<DockerNetwork> networks = DockerNetworks.Parse("garbage\nname\tonly-two-fields\n" + Inspect);

        Assert.That(networks, Has.Count.EqualTo(3),
            "this runs on the failure path of a run already in trouble; an unexpected shape must " +
            "degrade to 'nothing to reclaim', never mask the original error with a parse exception");
    }

    [Test]
    public void ALeftoverCaraxesNetworkOnTheSubnetIsReclaimable()
    {
        IReadOnlyList<SubnetHolder> blockers = DockerNetworks.FindBlockers(
            DockerNetworks.Parse(Inspect), ownNetworkName: "caraxes-accounts2k_caraxes", subnetCidr: "10.101.0.0/24");

        Assert.That(blockers, Has.Count.EqualTo(1));
        Assert.That(blockers[0].Kind, Is.EqualTo(SubnetHolderKind.LeakedCaraxes));
        Assert.That(blockers[0].Network.AttachedContainers, Is.Not.Empty,
            "the container must come along: the network cannot be removed while it holds an endpoint");
    }

    [Test]
    public void AForeignNetworkOnTheSubnetIsNeverRemovedAutomatically()
    {
        IReadOnlyList<DockerNetwork> networks =
            DockerNetworks.Parse("someones_project_default\t10.101.0.0/24 \tapi db \n");

        IReadOnlyList<SubnetHolder> blockers = DockerNetworks.FindBlockers(
            networks, ownNetworkName: "caraxes-accounts2k_caraxes", subnetCidr: "10.101.0.0/24");

        Assert.That(blockers, Has.Count.EqualTo(1));
        Assert.That(blockers[0].Kind, Is.EqualTo(SubnetHolderKind.Foreign),
            "a project outside Caraxes may be a live service; it is reported, never torn down");

        string message = DockerNetworks.ForeignBlockerMessage(blockers[0].Network, "10.101.0.0/24");
        Assert.That(message, Does.Contain("someones_project_default"));
        Assert.That(message, Does.Contain("10.101.0.0/24"));
        Assert.That(message, Does.Contain("api, db"), "name what holds it, so the operator can judge the risk");
        Assert.That(message, Does.Contain("docker network rm someones_project_default"),
            "the recovery command belongs in the error, not in a spec file someone has to find");
    }

    [Test]
    public void TheClusterSOwnNetworkIsNotItsOwnBlocker()
    {
        IReadOnlyList<SubnetHolder> blockers = DockerNetworks.FindBlockers(
            DockerNetworks.Parse(Inspect),
            ownNetworkName: "caraxes-capacity-baseline_caraxes",
            subnetCidr: "10.101.0.0/24");

        Assert.That(blockers, Is.Empty,
            "compose reuses its own network; removing it between runs would rebuild something identical");
    }

    [Test]
    public void NetworksOnOtherSubnetsAreLeftAlone()
    {
        IReadOnlyList<SubnetHolder> blockers = DockerNetworks.FindBlockers(
            DockerNetworks.Parse(Inspect), ownNetworkName: "caraxes-accounts2k_caraxes", subnetCidr: "10.99.0.0/24");

        Assert.That(blockers, Is.Empty, "a cluster given a different subnet in its spec collides with nothing");
    }

    [Test]
    public void TheWorkloadContainerIsNamedAfterItsClusterAndCarriesTheCaraxesPrefix()
    {
        ClusterSpec spec = new() { Name = "capacity-baseline" };
        ClusterPlan plan = ClusterPlan.FromSpec(spec);

        string name = WorkloadRunner.ContainerName(plan);

        Assert.That(name, Is.EqualTo("caraxes-capacity-baseline-workload"));
        Assert.That(name, Does.StartWith(DockerNetworks.CaraxesPrefix),
            "the prefix is what marks a leftover as this harness's own and therefore safe to reclaim");
    }
}
