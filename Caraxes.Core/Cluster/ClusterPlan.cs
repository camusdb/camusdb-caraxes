/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

namespace Caraxes.Core.Cluster;

/// <summary>
/// Everything derived from a <see cref="ClusterSpec"/>: one <see cref="NodePlan"/> per node plus
/// the cluster-wide docker names. Pure computation — building a plan touches neither the
/// filesystem nor docker, which is what makes the generators unit-testable.
/// </summary>
public sealed class ClusterPlan
{
    public ClusterSpec Spec { get; }

    public IReadOnlyList<NodePlan> Nodes { get; }

    /// <summary>Compose project name; also prefixes the bridge network and the volumes.</summary>
    public string ProjectName => $"caraxes-{Spec.Name}";

    /// <summary>Docker network the compose project creates (<c>{project}_{service-network}</c>);
    /// the workload container attaches to it to reach nodes by their in-cluster DNS names.</summary>
    public string NetworkName => $"{ProjectName}_caraxes";

    public string NetworkSubnetCidr => $"{Spec.Subnet}.0/24";

    private ClusterPlan(ClusterSpec spec, IReadOnlyList<NodePlan> nodes)
    {
        Spec = spec;
        Nodes = nodes;
    }

    public static ClusterPlan FromSpec(ClusterSpec spec)
    {
        spec.Validate();

        List<NodePlan> nodes = new(spec.Nodes);
        for (int i = 1; i <= spec.Nodes; i++)
        {
            nodes.Add(new NodePlan
            {
                Index = i,
                Name = $"camus{i}",
                ContainerName = $"{spec.Name}-camus{i}",
                Ip = $"{spec.Subnet}.{spec.FirstIp + i - 1}",
                // Step-2 raft ports match the repo's docker/local.yml convention (7070/7072/7074).
                RaftPort = spec.BaseRaftPort + 2 * (i - 1),
                HostRestPort = spec.BaseRestPort + i - 1,
                HostGrpcPort = spec.BaseGrpcPort + i - 1,
                Zone = spec.Zones.Count > 0 ? spec.Zones[i - 1] : null,
                VolumeName = $"camus{i}-data",
            });
        }

        // A node's initial-cluster lists the OTHER nodes' raft endpoints, mirroring docker/local.yml:
        // each node discovers itself from its own --raft-host/--raft-port flags.
        foreach (NodePlan node in nodes)
            node.InitialCluster = string.Join(' ',
                nodes.Where(n => n.Index != node.Index).Select(n => $"{n.Ip}:{n.RaftPort}"));

        return new ClusterPlan(spec, nodes);
    }

    /// <summary>Every node's REST base URL on the host, for probes.</summary>
    public IEnumerable<string> HostRestUrls => Nodes.Select(n => $"http://localhost:{n.HostRestPort}");

    /// <summary>Every node's gRPC endpoint on the host, comma-separated — the CamusDB.Client
    /// endpoint-pool string for a client running on the host. The cluster's gRPC port is TLS
    /// (the raft cert is reused), so a host client must trust the dev CA; the harness instead
    /// runs the workload inside the network (see <see cref="InternalWorkloadEndpointPool"/>).</summary>
    public string WorkloadEndpointPool =>
        string.Join(',', Nodes.Select(n => $"https://localhost:{n.HostGrpcPort}"));

    /// <summary>Every node's in-cluster gRPC endpoint by container DNS name, comma-separated. Used
    /// by a workload container attached to the cluster network: the node image already trusts the
    /// baked dev CA, and each node's cert SAN covers its <c>camusN</c> DNS name, so TLS validates
    /// with no host-side trust changes. The container gRPC port is the fixed 5096.</summary>
    public string InternalWorkloadEndpointPool =>
        string.Join(',', Nodes.Select(n => $"https://{n.Name}:5096"));

    /// <summary>
    /// The in-cluster gRPC endpoint of one node, for a run that drives all of its load through a
    /// single gateway instead of the pool. An unknown name throws rather than falling back to the
    /// pool: silently measuring the pool while the scenario says one gateway would put a wrong label
    /// on a retained result.
    /// </summary>
    public string InternalWorkloadEndpoint(string nodeName)
        => Nodes.Any(n => n.Name == nodeName)
            ? $"https://{nodeName}:5096"
            : throw new ArgumentException(
                $"no node named '{nodeName}' in cluster '{Spec.Name}' ({string.Join(", ", Nodes.Select(n => n.Name))})",
                nameof(nodeName));

    /// <summary>
    /// Every node's in-cluster Prometheus endpoint as <c>name=url</c> pairs, for the workload's
    /// per-node metric collection.
    ///
    /// <para>Named by the node, because a scrape carries no identity of its own: the resource
    /// attributes ride in <c>target_info</c>, not in the samples, so only the collector — which chose
    /// the URL — can say which node produced a series. Getting the name right here is what makes
    /// "one leader carried every write" a readable finding rather than a flat total.</para>
    ///
    /// <para>Plain HTTP on the container's fixed 5095: unlike the gRPC port, the REST listener is not
    /// TLS inside the network, which is also why <see cref="HostRestUrls"/> uses <c>http</c>.</para>
    /// </summary>
    public string InternalMetricsEndpoints =>
        string.Join(',', Nodes.Select(n => $"{n.Name}=http://{n.Name}:5095/metrics"));

    /// <summary>
    /// Every node's in-cluster HTTP API base as <c>name=url</c> pairs, for the workload's cluster
    /// fact capture: <c>/v1/version</c>, <c>/v1/cluster/health</c> and a node-local
    /// <c>SHOW VARIABLES</c>. Asking each node separately is the point — the configuration statement
    /// answers for whichever node served it, and nodes in a cluster can legitimately differ.
    /// </summary>
    public string InternalNodeEndpoints =>
        string.Join(',', Nodes.Select(n => $"{n.Name}=http://{n.Name}:5095"));
}

/// <summary>One node's derived identity: docker names, addresses, ports, and zone.</summary>
public sealed class NodePlan
{
    public int Index { get; init; }

    /// <summary>Logical name (camus1..N); the compose service name and the cert's DNS SAN.</summary>
    public string Name { get; init; } = "";

    /// <summary>Docker container name, prefixed by the cluster so clusters can coexist.</summary>
    public string ContainerName { get; init; } = "";

    public string Ip { get; init; } = "";

    public int RaftPort { get; init; }

    /// <summary>Host port mapped to the container's REST port (5095).</summary>
    public int HostRestPort { get; init; }

    /// <summary>Host port mapped to the container's gRPC port (5096).</summary>
    public int HostGrpcPort { get; init; }

    public string? Zone { get; init; }

    public string VolumeName { get; init; } = "";

    /// <summary>Space-separated raft endpoints of the other nodes (set after all nodes exist).</summary>
    public string InitialCluster { get; set; } = "";
}
