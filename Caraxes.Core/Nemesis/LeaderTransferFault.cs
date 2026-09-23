/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Text.Json;
using Caraxes.Core.Cluster;

namespace Caraxes.Core.Nemesis;

/// <summary>
/// Graceful leadership handover: the target node, which must currently lead at least one data
/// partition, is asked to hand each of them to another voter through CamusDB's
/// <c>POST /v1/cluster/transfer-leadership</c>. Nothing dies and nothing is paused — this is the
/// fault-matrix row "leader transfer at chosen commit points": the handover path with commits in
/// flight, which no crash fault reaches (a kill has no step-down, a pause has no handover).
///
/// <para>The window. A transfer completes within the call (consensus waits up to 10 s for the new
/// leader to be observed), so the fault has nothing to undo; it is still <see cref="Healable"/> with
/// a no-op heal so the event's <c>duration</c> closes a window the correlator can grade — recovery
/// and held throughput are measured from the heal, i.e. from <c>duration</c> after the handover.</para>
///
/// <para>Where the request is sent from. The mutation is superuser-only with authentication on and
/// loopback-only with it off, and the harness cluster runs without authentication; a request from
/// the host arrives through Docker's port proxy with the bridge gateway as its source, not loopback.
/// So the POST runs inside the target container's network namespace — a throwaway
/// <c>curlimages/curl</c> container sharing it — where 127.0.0.1:5095 is the node's REST port.</para>
/// </summary>
public sealed class LeaderTransferFault : IFault
{
    private const string CurlImage = "curlimages/curl:latest";

    /// <summary>
    /// Attempts while consensus answers ReplicationFailed (target momentarily behind), 250 ms apart.
    /// One since Kommander 1.7.3: the converging handover (Kommander feature 31098233) catches a
    /// lagging target up inside the request and answers TargetNotCaughtUp after its bounded wait, so
    /// a transfer never reports ReplicationFailed again and re-asking would only mask a regression.
    /// Its acceptance is "every handover on the first ask", which this bound enforces rather than
    /// leaving it to be read off the timeline.
    /// </summary>
    public const int MaxAttempts = 1;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpProbes probes;
    private readonly string? to;
    private string? lastOutcome;

    /// <param name="to">Node name to hand leadership to; null picks the first other voter of each partition.</param>
    public LeaderTransferFault(HttpProbes probes, string? to)
    {
        this.probes = probes;
        this.to = string.IsNullOrWhiteSpace(to) ? null : to.Trim();
    }

    public string Kind => "leader-transfer";

    public bool Healable => true;

    public string Describe(NodePlan target) => lastOutcome ?? $"transfer leadership away from {target.Name}";

    public async Task InjectAsync(NodePlan target, ClusterPlan plan, CancellationToken cancellationToken)
    {
        ClusterPlacement? placement = await probes
            .GetPlacementAsync($"http://localhost:{target.HostRestPort}", cancellationToken).ConfigureAwait(false);
        if (placement is null)
            throw new InvalidOperationException($"{target.Name} did not answer its placement; cannot tell what it leads");

        List<PartitionPlacement> led = placement.Partitions
            .Where(p => p.LeaderLocal && p.PartitionId > 0 && p.State != "Removed")
            .OrderBy(p => p.PartitionId)
            .ToList();
        if (led.Count == 0)
            throw new InvalidOperationException($"{target.Name} leads no data partition; nothing to transfer");

        NodePlan? destination = null;
        if (to is not null)
            destination = plan.Nodes.FirstOrDefault(n => string.Equals(n.Name, to, StringComparison.OrdinalIgnoreCase))
                ?? throw new NemesisException($"leader-transfer: unknown destination '{to}'; nodes are {string.Join(", ", plan.Nodes.Select(n => n.Name))}");

        // The placement table lists a partition's replicas only when they are a subset of the
        // membership; a partition hosted by every member (rf = nodes, the usual harness shape) comes
        // back with an empty list, and the voters are then the membership roster itself.
        IReadOnlyList<PartitionReplica> roster = [];
        if (led.Any(p => p.Replicas.Count == 0))
        {
            ClusterMembership? membership = await probes
                .GetMembershipAsync($"http://localhost:{target.HostRestPort}", cancellationToken).ConfigureAwait(false);
            roster = membership?.Members.Select(m => new PartitionReplica { Endpoint = m.Endpoint, Role = m.Role }).ToList()
                ?? throw new InvalidOperationException($"{target.Name} lists no replicas for what it leads and did not answer its membership");
        }

        List<string> outcomes = [];
        foreach (PartitionPlacement partition in led)
        {
            string endpoint = PickDestination(partition, placement.LocalEndpoint, destination is null ? null : RaftEndpoint(destination), roster);
            // Consensus used to refuse the handover with ReplicationFailed whenever the target's
            // known log tail was behind the leader's at that instant, which under a full-rate
            // workload is most instants (run lt1: the 4m transfer was refused, the 6m and 8m ones
            // went through), so the fault kept asking for a few seconds. Kommander 1.7.3 converges
            // the target inside the request instead, so MaxAttempts is 1 and a ReplicationFailed
            // answer now fails the run rather than being retried away.
            TransferLeadershipResult result = null!;
            int attempt = 0;
            for (; attempt < MaxAttempts; attempt++)
            {
                result = await TransferAsync(target, partition.PartitionId, endpoint, cancellationToken).ConfigureAwait(false);
                if (result.Success || result.Status != "ReplicationFailed")
                    break;
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
            if (!result.Success)
                throw new InvalidOperationException(
                    $"transfer of p{partition.PartitionId} from {target.Name} to {endpoint} did not complete after {attempt} attempt(s) (status={result.Status}): {result.Reason}");

            string name = plan.Nodes.FirstOrDefault(n => RaftEndpoint(n) == endpoint)?.Name ?? endpoint;
            outcomes.Add($"p{partition.PartitionId} {target.Name}→{name}" + (attempt > 0 ? $" (attempt {attempt + 1})" : ""));
        }

        lastOutcome = $"transferred leadership of {string.Join(", ", outcomes)}";
    }

    /// <summary>Nothing to undo: the handover completed inside the injection. The heal only closes the window.</summary>
    public Task HealAsync(NodePlan target, ClusterPlan plan, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// The replica to hand a partition to: <paramref name="preferred"/> when given (it must be a voter
    /// of the partition other than the leader), else the first voter that is not the leader. Learners
    /// cannot campaign, so they are never chosen. A partition that lists no replicas is hosted by the
    /// whole membership, and <paramref name="membership"/> stands in for its replica list.
    /// </summary>
    public static string PickDestination(
        PartitionPlacement partition, string leaderEndpoint, string? preferred, IReadOnlyList<PartitionReplica>? membership = null)
    {
        IEnumerable<PartitionReplica> replicas = partition.Replicas.Count > 0 ? partition.Replicas : membership ?? [];
        List<string> voters = replicas
            .Where(r => r.Endpoint != leaderEndpoint && string.Equals(r.Role, "Voter", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Endpoint)
            .ToList();

        if (preferred is not null)
        {
            if (preferred == leaderEndpoint)
                throw new NemesisException($"leader-transfer: {preferred} already leads p{partition.PartitionId}");
            if (!voters.Contains(preferred))
                throw new NemesisException(
                    $"leader-transfer: {preferred} is not a voter of p{partition.PartitionId} (voters other than the leader: {string.Join(", ", voters)})");
            return preferred;
        }

        return voters.Count > 0
            ? voters[0]
            : throw new NemesisException($"leader-transfer: p{partition.PartitionId} has no other voter to hand leadership to");
    }

    public static string RaftEndpoint(NodePlan node) => $"{node.Ip}:{node.RaftPort}";

    private static async Task<TransferLeadershipResult> TransferAsync(
        NodePlan target, int partitionId, string endpoint, CancellationToken cancellationToken)
    {
        string body = JsonSerializer.Serialize(new { partitionId, targetEndpoint = endpoint });
        ProcessResult run = await ProcessRunner.RunCheckedAsync("docker",
        [
            "run", "--rm", "--network", $"container:{target.ContainerName}", CurlImage,
            "-sS", "--max-time", "30", "-X", "POST", "-H", "Content-Type: application/json", "-d", body,
            "http://127.0.0.1:5095/v1/cluster/transfer-leadership",
        ], cancellationToken: cancellationToken).ConfigureAwait(false);

        try
        {
            return JsonSerializer.Deserialize<TransferLeadershipResult>(run.StdOut, JsonOptions)
                ?? throw new InvalidOperationException("empty answer");
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException($"transfer-leadership on {target.Name} answered something that is not its result: {Truncate(run.StdOut)} ({e.Message})");
        }
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "…";
}

/// <summary>Answer of CamusDB's <c>POST /v1/cluster/transfer-leadership</c>.</summary>
public sealed class TransferLeadershipResult
{
    public bool Success { get; set; }

    public string Status { get; set; } = "";

    public int PartitionId { get; set; }

    public string TargetEndpoint { get; set; } = "";

    public string? Reason { get; set; }
}
