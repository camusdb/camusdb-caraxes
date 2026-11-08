/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Text.Json;
using Caraxes.Core.Cluster;

namespace Caraxes.Core.Nemesis;

/// <summary>What one <c>split</c> event did: the key space it asked to divide, the node that committed the
/// split (or the last one asked), Kahuna's outcome, the split key, and how many asks it took.</summary>
public sealed record SplitOutcome(
    string Table,
    string? Index,
    string Node,
    string Status,
    string KeySpace,
    string? SplitKey,
    int NewPartitionId,
    int Attempts,
    double Seconds,
    bool Ok,
    string? Reason)
{
    public string Describe()
    {
        string space = Index is null ? $"rows of {Table}" : $"index {Index} of {Table}";
        string tries = Attempts > 1 ? $" after {Attempts} asks" : "";
        return Ok
            ? $"split {space} ({KeySpace}) at {SplitKey} on {Node}: upper half on P{NewPartitionId} in {Seconds:0.0} s{tries}"
            : $"split of {space} ({KeySpace}) did NOT commit: {Status} on {Node} after {Seconds:0.0} s{tries}: {Reason}";
    }
}

/// <summary>
/// A range split at a chosen moment: CamusDB's <c>POST /v1/cluster/split-range</c> divides one key space of
/// the workload's table at its median key, through the same quiesce, copy, hand-off and cutover the
/// load-based trigger runs. The trigger's timing is a function of load and settle windows, so a scenario
/// that needs a cutover inside another fault's window (a node down, a partition healing) cannot rely on
/// it; this fault starts the move when the schedule says.
///
/// <para>Where it was needed. The nightly <c>bank-split</c> arm lost acknowledged writes on 2026-11-08 only
/// because the cascade happened to cut the row space over while one node was dead: the restarted node
/// caught up the source and the destination logs out of order, re-installed a settled intent as a phantom
/// holder, rejected the later one-phase bundles of those keys, and served the rows one revision behind
/// once it led. Green nights had the same faults with the cutovers elsewhere. Scheduling the cutover
/// inside the kill window makes that shape a check instead of a coincidence.</para>
///
/// <para>Who answers. Kahuna runs a split only on the node that leads the partition owning the range
/// map, so the fault asks the target first and then every other node, following the leader hint when the
/// answer carries one. A refusal that can clear with time — the halves not yet settled, a quiesce that
/// did not confirm, a transfer that failed — is asked again until <c>split_timeout_seconds</c> runs out
/// or <c>split_retries</c> asks were made; an indeterminate answer is judged by the range count the
/// next answer reports, because the map may have moved after all. A permanent refusal (no range, the
/// partition limit, a malformed request) ends the attempt at once.</para>
///
/// <para>The window. The split completes inside the call, so the heal is a no-op that closes the window
/// <c>duration</c> later for the correlator. The outcome goes to the timeline and to
/// <see cref="NemesisContext.SplitOutcomes"/>, and the verdict fails a run in which a scheduled split did
/// not commit: a cutover that never happened is the scenario's claim not happening.</para>
/// </summary>
public sealed class SplitFault : IFault
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan RequestBound = TimeSpan.FromSeconds(90);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Outcomes after which the same ask can succeed a moment later.</summary>
    private static readonly IReadOnlySet<string> RetryableStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        "BelowMinRangeSize", "PartitionCreationFailed", "QuiesceFailed", "TransferFailed", "CutoverFailed", "ConcurrentSplit", "Indeterminate",
    };

    private readonly NemesisContext context;

    private readonly string table;

    private readonly string? index;

    private readonly TimeSpan budget;

    private readonly int retries;

    private string? lastOutcome;

    public SplitFault(NemesisContext context, string table, string? index, TimeSpan budget, int retries)
    {
        this.context = context;
        this.table = table;
        this.index = string.IsNullOrWhiteSpace(index) ? null : index.Trim();
        this.budget = budget;
        this.retries = retries;
    }

    public string Kind => "split";

    public bool Healable => true;

    public string Describe(NodePlan target) => lastOutcome ?? $"split {(index is null ? "rows" : "index " + index)} of {table} from {target.Name}";

    public async Task InjectAsync(NodePlan target, ClusterPlan plan, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        DateTime deadline = started + budget;

        // The target first, then the rest of the fleet in index order: a dead or partitioned target
        // answers nothing and the next node is asked.
        List<NodePlan> order = [target, .. plan.Nodes.Where(n => n.Name != target.Name)];
        int nodeIndex = 0;

        int attempts = 0;
        int? descriptorsBefore = null;
        SplitRangeResult? last = null;
        string lastNode = target.Name;
        string? transportError = null;

        while (attempts < retries + 1 && DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            NodePlan node = order[nodeIndex % order.Count];
            attempts++;

            SplitRangeResult? result;
            try
            {
                result = await AskAsync(node, cancellationToken).ConfigureAwait(false);
                transportError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // No answer: the node is down or unreachable. Ask the next one.
                transportError = $"{node.Name}: {ex.Message}";
                nodeIndex++;
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            lastNode = node.Name;
            last = result;

            if (result.Success)
                break;

            // An earlier indeterminate ask moved the map after all: the range count grew since the first ask.
            if (descriptorsBefore is int before && result.DescriptorsBefore > before)
            {
                result.Success = true;
                result.Status = "Succeeded";
                result.Reason = $"an earlier ask ended {last.Status} but the range count rose from {before} to {result.DescriptorsBefore}";
                break;
            }

            descriptorsBefore ??= result.DescriptorsBefore > 0 ? result.DescriptorsBefore : null;

            if (result.Status == "NotLeader")
            {
                NodePlan? hinted = result.LeaderHint is null ? null : plan.Nodes.FirstOrDefault(n => LeaderTransferFault.RaftEndpoint(n) == result.LeaderHint);
                nodeIndex = hinted is not null ? order.IndexOf(hinted) : nodeIndex + 1;
                continue;
            }

            if (!RetryableStatuses.Contains(result.Status) && result.Determinate)
                break;

            await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
        }

        double seconds = (DateTime.UtcNow - started).TotalSeconds;
        bool ok = last?.Success == true;

        SplitOutcome outcome = new(
            table, index, lastNode,
            last?.Status ?? "NoAnswer",
            last?.KeySpace ?? "",
            last?.SplitKey,
            last?.NewPartitionId ?? 0,
            attempts, seconds, ok,
            last?.Reason ?? transportError ?? "no node answered");

        context.RecordSplit(outcome);
        lastOutcome = outcome.Describe();

        if (!ok)
            throw new InvalidOperationException(lastOutcome);
    }

    /// <summary>Nothing to undo: the cutover committed inside the injection. The heal only closes the window.</summary>
    public Task HealAsync(NodePlan target, ClusterPlan plan, CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task<SplitRangeResult> AskAsync(NodePlan node, CancellationToken cancellationToken)
    {
        string body = JsonSerializer.Serialize(new { database = context.Database, table, index });
        string answer = await InContainerHttp.PostAsync(node, "/v1/cluster/split-range", body, RequestBound, cancellationToken).ConfigureAwait(false);

        try
        {
            return JsonSerializer.Deserialize<SplitRangeResult>(answer, JsonOptions)
                ?? throw new InvalidOperationException("empty answer");
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException($"split-range on {node.Name} answered something that is not its result: {Truncate(answer)} ({e.Message})");
        }
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "…";
}

/// <summary>Answer of CamusDB's <c>POST /v1/cluster/split-range</c>.</summary>
public sealed class SplitRangeResult
{
    public bool Success { get; set; }

    public string Status { get; set; } = "";

    public bool Determinate { get; set; } = true;

    public string KeySpace { get; set; } = "";

    public string? SplitKey { get; set; }

    public int KeysSeen { get; set; }

    public int DescriptorsBefore { get; set; }

    public int NewPartitionId { get; set; }

    public string? LeaderHint { get; set; }

    public string? Reason { get; set; }
}
