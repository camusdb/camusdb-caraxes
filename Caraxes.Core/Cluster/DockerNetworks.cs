/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

namespace Caraxes.Core.Cluster;

/// <summary>One docker network as the daemon reports it: its name, the subnets its IPAM claims, and
/// the containers still attached to it.</summary>
public sealed record DockerNetwork(string Name, IReadOnlyList<string> Subnets, IReadOnlyList<string> AttachedContainers);

/// <summary>What a network occupying this cluster's subnet is, and therefore what may be done to it.</summary>
public enum SubnetHolderKind
{
    /// <summary>A leaked Caraxes network from an interrupted run. Safe to reclaim: no other tool owns it.</summary>
    LeakedCaraxes,

    /// <summary>Something outside Caraxes. Never removed automatically — it may be a live service.</summary>
    Foreign,
}

/// <summary>A network standing on the subnet this cluster needs, with the verdict on reclaiming it.</summary>
public sealed record SubnetHolder(DockerNetwork Network, SubnetHolderKind Kind);

/// <summary>
/// Detects and reclaims docker networks that block a cluster from starting.
///
/// <para>Every Caraxes cluster is pinned to one fixed /24 (<see cref="ClusterSpec.Subnet"/>,
/// default <c>10.101.0</c>), so only one can exist at a time. That is deliberate — the TLS
/// certificate SANs are IP-based — but it turns a leak into a hard block: an interrupted run leaves
/// its network behind, and because <c>docker network rm</c> refuses a network with active endpoints,
/// a workload container that outlived a killed harness pins it there permanently. Every subsequent
/// run then dies at startup with <c>invalid pool request: Pool overlaps with other one on this
/// address space</c>, which names neither the leftover network nor the container holding it. That
/// cost a full five-cell sweep before being diagnosed.</para>
///
/// <para>The reclaim is deliberately asymmetric. A network whose name carries the Caraxes project
/// prefix was created by this harness, so removing it — and force-disconnecting whatever still hangs
/// off it — only destroys the harness's own wreckage. Anything else is left alone and reported: a
/// developer's unrelated compose project sitting on 10.101.0.0/24 must be a legible error, never a
/// service this harness silently tore down.</para>
/// </summary>
public static class DockerNetworks
{
    /// <summary>Prefix of every network this harness creates — see <see cref="ClusterPlan.ProjectName"/>.</summary>
    public const string CaraxesPrefix = "caraxes-";

    /// <summary>Field separator of the inspect template below. Tab cannot appear in a network or
    /// container name, so a line always splits into exactly three fields.</summary>
    private const char FieldSeparator = '\t';

    /// <summary>
    /// One line per network: name, its IPAM subnets, and the names of its attached containers.
    /// Kept as a single template so the whole fleet costs one <c>docker network inspect</c>.
    /// </summary>
    public const string InspectFormat =
        "{{.Name}}\t{{range .IPAM.Config}}{{.Subnet}} {{end}}\t{{range $id, $c := .Containers}}{{$c.Name}} {{end}}";

    /// <summary>
    /// Parses the output of <c>docker network inspect --format</c> with <see cref="InspectFormat"/>.
    /// Pure, so the classification below is testable without a docker daemon.
    ///
    /// <para>Lines that do not carry the three fields are skipped rather than throwing: this runs on
    /// the failure path of a run that is already in trouble, and a daemon that answers in an
    /// unexpected shape must degrade to "found nothing to reclaim", not mask the original error with
    /// a parse exception.</para>
    /// </summary>
    public static IReadOnlyList<DockerNetwork> Parse(string output)
    {
        List<DockerNetwork> networks = [];

        foreach (string line in output.Split('\n'))
        {
            string[] fields = line.TrimEnd('\r').Split(FieldSeparator);
            if (fields.Length != 3 || string.IsNullOrWhiteSpace(fields[0]))
                continue;

            networks.Add(new DockerNetwork(
                fields[0].Trim(),
                Tokens(fields[1]),
                Tokens(fields[2])));
        }

        return networks;
    }

    private static string[] Tokens(string field)
        => field.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// The networks standing on <paramref name="subnetCidr"/> that are not this cluster's own.
    ///
    /// <para>The cluster's own network is excluded by name: <c>compose up</c> reuses it happily, and
    /// removing it between runs would only force a rebuild of something identical. A network with the
    /// same name is the same cluster; a different name on the same subnet is the block.</para>
    /// </summary>
    public static IReadOnlyList<SubnetHolder> FindBlockers(
        IReadOnlyList<DockerNetwork> networks, string ownNetworkName, string subnetCidr)
        => networks
            .Where(n => !string.Equals(n.Name, ownNetworkName, StringComparison.Ordinal))
            .Where(n => n.Subnets.Contains(subnetCidr, StringComparer.Ordinal))
            .Select(n => new SubnetHolder(
                n,
                n.Name.StartsWith(CaraxesPrefix, StringComparison.Ordinal)
                    ? SubnetHolderKind.LeakedCaraxes
                    : SubnetHolderKind.Foreign))
            .ToList();

    /// <summary>Inspects every network the daemon knows about. An empty list when docker answers with
    /// an error — the caller is starting or tearing down a cluster and docker's own command will
    /// report the real problem.</summary>
    public static async Task<IReadOnlyList<DockerNetwork>> ListAsync(CancellationToken cancellationToken = default)
    {
        ProcessResult ids = await ProcessRunner.RunAsync(
            "docker", ["network", "ls", "--quiet"], cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!ids.Success)
            return [];

        string[] networkIds = ids.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (networkIds.Length == 0)
            return [];

        List<string> args = ["network", "inspect", "--format", InspectFormat];
        args.AddRange(networkIds);

        ProcessResult inspected = await ProcessRunner.RunAsync(
            "docker", args, cancellationToken: cancellationToken).ConfigureAwait(false);

        // A network removed between the two calls fails the whole inspect, but the lines for the
        // surviving ones are still on stdout, so parse regardless of the exit code.
        return Parse(inspected.StdOut);
    }

    /// <summary>
    /// Removes one network, force-disconnecting whatever is still attached to it first.
    ///
    /// <para>The disconnect is the point: <c>docker network rm</c> fails on a network with active
    /// endpoints, and the endpoint is normally a workload container that outlived the harness that
    /// started it. Each attached container is removed rather than merely disconnected — it belongs to
    /// an abandoned run, and leaving a dead workload container behind is the other half of the same
    /// leak.</para>
    /// </summary>
    /// <returns>The removal step's messages, for the caller to surface in run notes.</returns>
    public static async Task<IReadOnlyList<string>> RemoveAsync(
        DockerNetwork network, CancellationToken cancellationToken = default)
    {
        List<string> notes = [];

        foreach (string container in network.AttachedContainers)
        {
            ProcessResult removed = await ProcessRunner.RunAsync(
                "docker", ["rm", "--force", container], cancellationToken: cancellationToken).ConfigureAwait(false);

            notes.Add(removed.Success
                ? $"removed container '{container}' still attached to network '{network.Name}'"
                : $"could not remove container '{container}' on network '{network.Name}': {removed.StdErr.Trim()}");
        }

        ProcessResult result = await ProcessRunner.RunAsync(
            "docker", ["network", "rm", network.Name], cancellationToken: cancellationToken).ConfigureAwait(false);

        notes.Add(result.Success
            ? $"removed leftover network '{network.Name}'"
            : $"could not remove network '{network.Name}': {result.StdErr.Trim()}");

        return notes;
    }

    /// <summary>The operator-facing recovery instructions for a network this harness will not touch.</summary>
    public static string ForeignBlockerMessage(DockerNetwork network, string subnetCidr)
        => $"docker network '{network.Name}' already holds {subnetCidr}, which every Caraxes cluster is pinned to, " +
           $"and it was not created by Caraxes so it will not be removed automatically" +
           (network.AttachedContainers.Count > 0
               ? $" (attached: {string.Join(", ", network.AttachedContainers)})"
               : string.Empty) +
           $". Free the subnet — 'docker network rm {network.Name}' once nothing needs it — or give this cluster a " +
           "different 'subnet' in its spec.";
}
