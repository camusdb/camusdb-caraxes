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
/// An answer of a node that is not a split result: the error page of an HTTP 500, an empty body, a JSON
/// body without a status. The node is up and the call failed inside it. A node that did not answer at
/// all (a dead container, a refused connection) fails the call with another exception.
/// </summary>
public sealed class SplitAnswerException(string message) : Exception(message);

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
/// did not confirm, a transfer that failed, a scan for the split key that did not settle — is asked
/// again until <c>split_timeout_seconds</c> runs out or <c>split_retries</c> asks were made; an
/// indeterminate answer is judged by the range count the next answer reports, because the map may have
/// moved after all. A permanent refusal (no range, the partition limit, a malformed request) ends the
/// attempt at once.</para>
///
/// <para>Which node is asked next. A node that does not answer gives way to the next one. A node that
/// answers an error instead of a result (<see cref="SplitAnswerException"/>) is asked again when it is
/// the node the last <c>NotLeader</c> answer named, because no other node can run the split; on
/// 2026-11-23 the fault left a leader whose scan had failed, spent the rest of a 12 s budget on a node
/// that answered <c>NotLeader</c>, and the split never ran. A node that already answered
/// <c>NotLeader</c> is asked again only after a pause: during an election every node answers that at
/// once, and asks without a pause would use up <c>split_retries</c> before a leader exists.</para>
///
/// <para>The window. The split completes inside the call, so the heal is a no-op that closes the window
/// <c>duration</c> later for the correlator. The outcome goes to the timeline and to
/// <see cref="NemesisContext.SplitOutcomes"/>, and the verdict fails a run in which a scheduled split did
/// not commit: a cutover that never happened is the scenario's claim not happening. A failed outcome
/// names the last ask, and keeps the error of an earlier ask that a later answer followed.</para>
/// </summary>
public sealed class SplitFault : IFault
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan RequestBound = TimeSpan.FromSeconds(90);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Outcomes after which the same ask can succeed a moment later. <c>MedianScanFailed</c> is
    /// CamusDB's own: Kahuna gave up on the scan that picks the split key, and no split was attempted.</summary>
    private static readonly IReadOnlySet<string> RetryableStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        "BelowMinRangeSize", "PartitionCreationFailed", "QuiesceFailed", "TransferFailed", "CutoverFailed", "ConcurrentSplit", "Indeterminate",
        "MedianScanFailed",
    };

    private readonly NemesisContext context;

    private readonly string table;

    private readonly string? index;

    private readonly TimeSpan budget;

    private readonly int retries;

    private readonly Func<NodePlan, CancellationToken, Task<SplitRangeResult>> ask;

    private readonly Func<CancellationToken, Task> pause;

    private string? lastOutcome;

    public SplitFault(NemesisContext context, string table, string? index, TimeSpan budget, int retries)
        : this(context, table, index, budget, retries, ask: null, pause: null)
    {
    }

    /// <summary>
    /// The fault with the call to the node and the pause between asks replaced, for tests of the order
    /// of the asks. A null <paramref name="ask"/> is the call to the node; a null <paramref name="pause"/>
    /// waits half a second.
    /// </summary>
    public SplitFault(
        NemesisContext context, string table, string? index, TimeSpan budget, int retries,
        Func<NodePlan, CancellationToken, Task<SplitRangeResult>>? ask,
        Func<CancellationToken, Task>? pause)
    {
        this.context = context;
        this.table = table;
        this.index = string.IsNullOrWhiteSpace(index) ? null : index.Trim();
        this.budget = budget;
        this.retries = retries;
        this.ask = ask ?? AskNodeAsync;
        this.pause = pause ?? (cancellationToken => Task.Delay(RetryDelay, cancellationToken));
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

        // The node the last NotLeader answer named, and the nodes that answered NotLeader.
        string? leader = null;
        HashSet<string> answeredNotLeader = new(StringComparer.Ordinal);

        // An ask that ended without a split result. failedStatus is set while the most recent ask is
        // such an ask; lastFailure keeps its text after a later ask answered.
        string? failedStatus = null;
        string? lastFailure = null;

        while (attempts < retries + 1 && DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            NodePlan node = order[nodeIndex % order.Count];
            attempts++;
            lastNode = node.Name;

            SplitRangeResult result;
            try
            {
                result = await ask(node, cancellationToken).ConfigureAwait(false);
            }
            catch (SplitAnswerException ex)
            {
                // The node is up and the call failed inside it. The node the cluster named as the leader
                // is the only one that can run the split, so it is asked again; another node gives way.
                failedStatus = "NodeError";
                lastFailure = ex.Message;

                if (node.Name != leader)
                    nodeIndex++;

                await pause(cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // No answer: the node is down or unreachable. Ask the next one.
                failedStatus = "NoAnswer";
                lastFailure = $"{node.Name} did not answer: {ex.Message}";

                if (node.Name == leader)
                    leader = null;

                nodeIndex++;
                await pause(cancellationToken).ConfigureAwait(false);
                continue;
            }

            failedStatus = null;
            string? previousStatus = last?.Status;
            last = result;

            if (result.Success)
                break;

            // An earlier indeterminate ask moved the map after all: the range count grew since the first ask.
            if (descriptorsBefore is int before && result.DescriptorsBefore > before)
            {
                result.Success = true;
                result.Status = "Succeeded";
                result.Reason = $"an earlier ask ended {previousStatus ?? "without an answer"} but the range count rose from {before} to {result.DescriptorsBefore}";
                break;
            }

            descriptorsBefore ??= result.DescriptorsBefore > 0 ? result.DescriptorsBefore : null;

            if (result.Status == "NotLeader")
            {
                answeredNotLeader.Add(node.Name);

                NodePlan? hinted = result.LeaderHint is null
                    ? null
                    : plan.Nodes.FirstOrDefault(n => LeaderTransferFault.RaftEndpoint(n) == result.LeaderHint && n.Name != node.Name);

                leader = hinted?.Name;
                nodeIndex = hinted is not null ? order.FindIndex(n => n.Name == hinted.Name) : nodeIndex + 1;

                // During an election every node answers NotLeader at once. A node that already answered
                // it is asked again after a pause, so the asks last as long as the budget.
                if (answeredNotLeader.Contains(order[nodeIndex % order.Count].Name))
                    await pause(cancellationToken).ConfigureAwait(false);

                continue;
            }

            if (!RetryableStatuses.Contains(result.Status) && result.Determinate)
                break;

            await pause(cancellationToken).ConfigureAwait(false);
        }

        double seconds = (DateTime.UtcNow - started).TotalSeconds;
        bool ok = failedStatus is null && last?.Success == true;

        // The outcome names the last ask. When that ask answered, an earlier ask that did not is the
        // usual reason the split did not commit, so its text stays in the reason.
        string status = failedStatus ?? last?.Status ?? "NoAnswer";
        string? reason = failedStatus is not null ? lastFailure : last?.Reason;

        if (!ok && failedStatus is null && lastFailure is not null)
            reason = $"{reason} An earlier ask failed: {lastFailure}";

        SplitOutcome outcome = new(
            table, index, lastNode,
            status,
            last?.KeySpace ?? "",
            last?.SplitKey,
            last?.NewPartitionId ?? 0,
            attempts, seconds, ok,
            reason ?? "no node answered");

        context.RecordSplit(outcome);
        lastOutcome = outcome.Describe();

        if (!ok)
            throw new InvalidOperationException(lastOutcome);
    }

    /// <summary>Nothing to undo: the cutover committed inside the injection. The heal only closes the window.</summary>
    public Task HealAsync(NodePlan target, ClusterPlan plan, CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task<SplitRangeResult> AskNodeAsync(NodePlan node, CancellationToken cancellationToken)
    {
        string body = JsonSerializer.Serialize(new { database = context.Database, table, index });
        string answer = await InContainerHttp.PostAsync(node, "/v1/cluster/split-range", body, RequestBound, cancellationToken).ConfigureAwait(false);

        return ParseAnswer(node.Name, answer);
    }

    /// <summary>
    /// Reads the split result in a node's answer. Every result names its status, so a body that is not
    /// JSON, or is JSON without a status, is some other answer of a node that is up: a
    /// <see cref="SplitAnswerException"/>.
    /// </summary>
    public static SplitRangeResult ParseAnswer(string node, string answer)
    {
        SplitRangeResult? result;

        try
        {
            result = JsonSerializer.Deserialize<SplitRangeResult>(answer, JsonOptions);
        }
        catch (JsonException e)
        {
            throw new SplitAnswerException($"split-range on {node} answered something that is not its result: {Truncate(answer)} ({e.Message})");
        }

        if (result is null || string.IsNullOrEmpty(result.Status))
            throw new SplitAnswerException($"split-range on {node} answered something that is not its result: {Truncate(answer)}");

        return result;
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";
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
