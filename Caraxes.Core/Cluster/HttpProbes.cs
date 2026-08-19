/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Caraxes.Core.Cluster;

/// <summary>Answer of CamusDB's <c>GET /v1/cluster/health</c> readiness probe.</summary>
public sealed class NodeHealth
{
    public bool Ready { get; set; }

    public bool Initialized { get; set; }

    public string LocalRole { get; set; } = "";

    public int HostedPartitions { get; set; }
}

/// <summary>Answer of CamusDB's <c>GET /v1/cluster/placement</c>.</summary>
public sealed class ClusterPlacement
{
    public int ReplicationFactor { get; set; }

    public bool RebalancerEnabled { get; set; }

    public bool Initialized { get; set; }

    public string LocalEndpoint { get; set; } = "";

    public int HostedPartitionCount { get; set; }

    public List<PartitionPlacement> Partitions { get; set; } = [];
}

public sealed class PartitionPlacement
{
    public int PartitionId { get; set; }

    public string State { get; set; } = "";

    public long Generation { get; set; }

    public int EffectiveReplicationFactor { get; set; }

    public bool HostedLocally { get; set; }

    /// <summary>Whether the answering node is the Raft leader of this partition.</summary>
    public bool LeaderLocal { get; set; }

    public List<PartitionReplica> Replicas { get; set; } = [];
}

public sealed class PartitionReplica
{
    public string Endpoint { get; set; } = "";

    public string Role { get; set; } = "";
}

/// <summary>Answer of CamusDB's <c>POST /v1/cluster/leave</c> graceful-decommission endpoint.</summary>
public sealed class LeaveResult
{
    public bool Left { get; set; }

    public bool Drained { get; set; }

    public string Outcome { get; set; } = "";

    public long MembershipVersion { get; set; }

    public bool Retryable { get; set; }

    public string Reason { get; set; } = "";
}

/// <summary>
/// HTTP probes against a node's REST port. Unreachable or not-yet-listening nodes surface as null
/// results rather than exceptions — during bring-up, kills, and partitions "not answering" is an
/// expected state the caller branches on, not an error path.
/// </summary>
public sealed class HttpProbes : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly HttpClient client;

    public HttpProbes(TimeSpan? timeout = null)
    {
        client = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(5) };
    }

    public async Task<bool> PingAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpResponseMessage response = await client.GetAsync($"{baseUrl}/ping", cancellationToken)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception) when (NotCallerCancellation(cancellationToken))
        {
            return false;
        }
    }

    /// <summary>Null = node unreachable. A reachable node that is not ready still returns its
    /// health body (the endpoint answers 503 with the same shape).</summary>
    public async Task<NodeHealth?> GetHealthAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpResponseMessage response = await client.GetAsync($"{baseUrl}/v1/cluster/health", cancellationToken)
                .ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<NodeHealth>(body, JsonOptions);
        }
        catch (Exception) when (NotCallerCancellation(cancellationToken))
        {
            return null;
        }
    }

    public async Task<ClusterPlacement?> GetPlacementAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpResponseMessage response = await client.GetAsync($"{baseUrl}/v1/cluster/placement", cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<ClusterPlacement>(body, JsonOptions);
        }
        catch (Exception) when (NotCallerCancellation(cancellationToken))
        {
            return null;
        }
    }

    /// <summary>Requests graceful decommission of the node at <paramref name="baseUrl"/>. Null =
    /// no response. The result body is returned on any HTTP status (the endpoint reports a
    /// non-committed outcome with the same shape and a non-2xx code), so the caller inspects
    /// <see cref="LeaveResult.Left"/> rather than the status.</summary>
    public async Task<LeaveResult?> LeaveAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpResponseMessage response = await client.PostAsync(
                $"{baseUrl}/v1/cluster/leave", content: null, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<LeaveResult>(body, JsonOptions);
        }
        catch (Exception) when (NotCallerCancellation(cancellationToken))
        {
            return null;
        }
    }

    private static bool NotCallerCancellation(CancellationToken token) => !token.IsCancellationRequested;

    public void Dispose() => client.Dispose();
}
