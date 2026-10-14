/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Text.Json;
using System.Text.Json.Serialization;
using Caraxes.Core.Cluster;
using Caraxes.Core.Nemesis;

namespace Caraxes.Core.Backup;

/// <summary>One entry of a node's backup catalog, as CamusDB's <c>/v1/backups</c> reports it.</summary>
public sealed class BackupInfo
{
    public string BackupId { get; set; } = "";

    /// <summary><c>Full</c> or <c>Incremental</c> (a coordinated backup is a Full with a cluster cut).</summary>
    public string Type { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; }

    public string? ParentBackupId { get; set; }

    public int PartitionCount { get; set; }

    public long? ClusterSnapshotPhysical { get; set; }

    public string? RequestedKind { get; set; }

    public string? ActualKind { get; set; }

    /// <summary>Set when the kind taken differs from the kind asked for: an incremental whose parent
    /// aged past the WAL floor comes back as a full, and this says so.</summary>
    public string? SubstitutionReason { get; set; }

    public bool IsInvalid { get; set; }

    public string? InvalidReason { get; set; }

    public long? MinRecoverablePhysicalMs { get; set; }

    public long? MaxRecoverablePhysicalMs { get; set; }

    public string? ClusterId { get; set; }

    public string? CoordinatorNode { get; set; }

    public bool IsFull => string.Equals(Type, "Full", StringComparison.OrdinalIgnoreCase);

    public bool IsIncremental => string.Equals(Type, "Incremental", StringComparison.OrdinalIgnoreCase);

    /// <summary>The first eight characters of the id, for timeline and verdict lines.</summary>
    public string ShortId => BackupId.Length >= 8 ? BackupId[..8] : BackupId;
}

/// <summary>Envelope of the take-backup endpoints.</summary>
public sealed class BackupResponse
{
    public string Status { get; set; } = "";

    public string? Code { get; set; }

    public string? Message { get; set; }

    public BackupInfo? Backup { get; set; }

    public bool Ok => Status == "ok" && Backup is not null;
}

/// <summary>Envelope of the catalog listing and chain endpoints.</summary>
public sealed class BackupListResponse
{
    public string Status { get; set; } = "";

    public string? Code { get; set; }

    public string? Message { get; set; }

    public List<BackupInfo> Backups { get; set; } = [];

    public bool Ok => Status == "ok";
}

/// <summary>Envelope of <c>POST /v1/restore</c>.</summary>
public sealed class RestoreResponse
{
    public string Status { get; set; } = "";

    public string? Code { get; set; }

    public string? Message { get; set; }

    public string? DataRoot { get; set; }

    public int PartitionsRestored { get; set; }

    public long EntriesApplied { get; set; }

    public long LastAppliedPhysicalMs { get; set; }

    public long MinRecoverablePhysicalMs { get; set; }

    public long MaxRecoverablePhysicalMs { get; set; }

    public List<BackupInfo> Chain { get; set; } = [];

    public bool Ok => Status == "ok";
}

/// <summary>What a backup request produced, for the timeline and the verdict. <paramref name="Refusals"/>
/// lists the failure envelopes of the attempts before the one that succeeded, oldest first.</summary>
public sealed record BackupTaken(NodePlan Node, string RequestedKind, BackupInfo Backup, TimeSpan Elapsed, string Note, IReadOnlyList<string> Refusals)
{
    public BackupTaken(NodePlan node, string requestedKind, BackupInfo backup, TimeSpan elapsed, string note)
        : this(node, requestedKind, backup, elapsed, note, [])
    {
    }

    /// <summary>One line: kind, id, parent, partitions, elapsed, any substitution, and any refused attempt.</summary>
    public string Describe()
    {
        string actual = Backup.ActualKind ?? Backup.Type;
        string kind = string.Equals(RequestedKind, actual, StringComparison.OrdinalIgnoreCase)
            ? RequestedKind
            : $"{RequestedKind} taken as {actual}";
        string parent = Backup.ParentBackupId is null ? "" : $", parent {Backup.ParentBackupId[..Math.Min(8, Backup.ParentBackupId.Length)]}";
        string substitution = Backup.SubstitutionReason is null ? "" : $"; SUBSTITUTED: {Backup.SubstitutionReason}";
        string note = Note.Length == 0 ? "" : $"; {Note}";
        string refused = Refusals.Count == 0
            ? ""
            : $"; REFUSED {Refusals.Count} attempt(s) first: {string.Join(" | ", Refusals)}";
        return $"{kind} backup {Backup.ShortId} on {Node.Name}{parent}, {Backup.PartitionCount} partition(s), {Elapsed.TotalSeconds:0.00} s{substitution}{note}{refused}";
    }
}

/// <summary>A backup endpoint answered with a failure envelope.</summary>
public sealed class BackupRequestException : Exception
{
    public BackupRequestException(string code, string message) : base($"{code}: {message}")
    {
        Code = code;
    }

    /// <summary>CamusDB's error code (<c>CADB07xx</c> for the backup family).</summary>
    public string Code { get; }
}

/// <summary>
/// Drives CamusDB's backup and restore admin API on one node, from inside that node's network
/// namespace (the endpoints are loopback-only without authentication, see <see cref="InContainerHttp"/>).
/// Shared by the <c>backup</c> nemesis fault, which takes backups while the workload runs, and the
/// post-run verification, which closes the chain, restores it and reads it back.
/// </summary>
public static class BackupClient
{
    /// <summary>CamusDB's code for a coordinated backup sent to a node that does not lead the meta
    /// partition (<c>BackupNotCoordinator</c>).</summary>
    public const string NotCoordinatorCode = "CADB070E";

    /// <summary>CamusDB's code for a chain that does not resolve (<c>BackupChainInvalid</c>).</summary>
    public const string ChainInvalidCode = "CADB0701";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>A backup is bounded by its apply barrier (30 s) plus the base-image copy; a restore by
    /// the copy plus the replay of every segment. Five minutes covers a long soak's chain.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Attempts a backup request gets before it counts as failed, <see cref="RetryDelay"/> apart. The
    /// engine fails a backup closed whenever it cannot prove something at that instant — the meta
    /// leader's read-index confirmation for the snapshot floor is the case seen (preflight pitr1,
    /// 2026-10-14: the first backup of the run refused as "could not pin MVCC history", the next
    /// request 30 s later fine) — and an operator's job asks again. Every refused attempt is kept
    /// on the result and written to the timeline in capitals, so a retry never hides a refusal.
    /// </summary>
    public const int MaxAttempts = 3;

    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    public static async Task<IReadOnlyList<BackupInfo>> ListAsync(NodePlan node, CancellationToken cancellationToken)
    {
        string body = await InContainerHttp.GetAsync(node, "/v1/backups", RequestTimeout, cancellationToken).ConfigureAwait(false);
        BackupListResponse list = Parse<BackupListResponse>(body, "/v1/backups", node);
        if (!list.Ok)
            throw new BackupRequestException(list.Code ?? "unknown", list.Message ?? "listing failed");
        return list.Backups;
    }

    public static async Task<IReadOnlyList<BackupInfo>> ChainAsync(NodePlan node, string leafBackupId, CancellationToken cancellationToken)
    {
        string body = await InContainerHttp.GetAsync(node, $"/v1/backups/{leafBackupId}/chain", RequestTimeout, cancellationToken).ConfigureAwait(false);
        BackupListResponse list = Parse<BackupListResponse>(body, "chain", node);
        if (!list.Ok)
            throw new BackupRequestException(list.Code ?? "unknown", list.Message ?? "chain resolution failed");
        return list.Backups;
    }

    /// <summary>
    /// Takes a backup of <paramref name="kind"/> on <paramref name="node"/>: <c>full</c>, <c>coordinated</c>,
    /// or <c>incremental</c> on top of the newest valid backup in the node's own catalog. An
    /// incremental on a node whose catalog is empty is taken as a full and says so — the chain has to
    /// start somewhere, and a schedule of incrementals should not need a hand-placed full before it.
    /// A coordinated backup refused as not-the-coordinator is retried once on each other node of
    /// <paramref name="plan"/>, because the meta leader moves and a schedule cannot name it in advance.
    /// </summary>
    public static async Task<BackupTaken> TakeAsync(NodePlan node, ClusterPlan plan, string kind, CancellationToken cancellationToken)
    {
        string requested = kind.ToLowerInvariant();
        if (!BackupFault.Kinds.Contains(requested))
            throw new ArgumentException($"unknown backup kind '{kind}'; use full, incremental or coordinated", nameof(kind));

        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        List<string> refusals = [];

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                BackupTaken taken = await TakeOnceAsync(node, plan, requested, cancellationToken).ConfigureAwait(false);
                return taken with { Elapsed = clock.Elapsed, Refusals = refusals };
            }
            catch (BackupRequestException e) when (attempt < MaxAttempts)
            {
                refusals.Add($"attempt {attempt} {e.Message}");
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (BackupRequestException e)
            {
                throw new BackupRequestException(e.Code,
                    $"{e.Message.Substring(e.Code.Length + 2)} (attempt {attempt} of {MaxAttempts}" +
                    (refusals.Count == 0 ? ")" : $"; earlier: {string.Join(" | ", refusals)})"));
            }
        }
    }

    private static async Task<BackupTaken> TakeOnceAsync(NodePlan node, ClusterPlan plan, string requested, CancellationToken cancellationToken)
    {
        switch (requested)
        {
            case "full":
                return new BackupTaken(node, requested, await TakeOneAsync(node, "/v1/backups/full", null, cancellationToken).ConfigureAwait(false), TimeSpan.Zero, "");

            case "incremental":
            {
                BackupInfo? parent = NewestValid(await ListAsync(node, cancellationToken).ConfigureAwait(false));
                if (parent is null)
                {
                    BackupInfo full = await TakeOneAsync(node, "/v1/backups/full", null, cancellationToken).ConfigureAwait(false);
                    return new BackupTaken(node, requested, full, TimeSpan.Zero, "no backup to chain on in this node's catalog, so the chain starts here with a full");
                }

                string body = JsonSerializer.Serialize(new { parentBackupId = parent.BackupId });
                BackupInfo incremental = await TakeOneAsync(node, "/v1/backups/incremental", body, cancellationToken).ConfigureAwait(false);

                // The engine publishes an incremental whose chain it then refuses to resolve when the
                // topology generation moved since the parent (a member evicted or joined): the link
                // exists and no restore can use it (run replace-node r1, 2026-10-14; vorpal kahuna).
                // Its own rule says a new full is required across such a change, so the harness takes
                // that full and says so in capitals; the orphan stays in the catalog as the evidence.
                try
                {
                    await ChainAsync(node, incremental.BackupId, cancellationToken).ConfigureAwait(false);
                }
                catch (BackupRequestException e) when (e.Code == ChainInvalidCode)
                {
                    BackupInfo full = await TakeOneAsync(node, "/v1/backups/full", null, cancellationToken).ConfigureAwait(false);
                    return new BackupTaken(node, requested, full, TimeSpan.Zero,
                        $"CHAIN BROKEN BY THE ENGINE: incremental {incremental.ShortId} was published but its chain does not resolve ({e.Message}); the harness started a new chain with this full");
                }

                return new BackupTaken(node, requested, incremental, TimeSpan.Zero, "");
            }

            case "coordinated":
            {
                List<NodePlan> order = [node, .. plan.Nodes.Where(n => n.Index != node.Index)];
                foreach (NodePlan candidate in order)
                {
                    try
                    {
                        BackupInfo taken = await TakeOneAsync(candidate, "/v1/backups/coordinated", null, cancellationToken).ConfigureAwait(false);
                        string note = candidate.Index == node.Index ? "" : $"{node.Name} is not the backup coordinator; taken on {candidate.Name}";
                        return new BackupTaken(candidate, requested, taken, TimeSpan.Zero, note);
                    }
                    catch (BackupRequestException e) when (e.Code == NotCoordinatorCode)
                    {
                        // try the next node
                    }
                }
                throw new InvalidOperationException("no node accepted the coordinated backup: none reported leading the meta partition");
            }

            default:
                throw new ArgumentException($"unknown backup kind '{requested}'", nameof(requested));
        }
    }

    /// <summary>Restores the chain ending at <paramref name="leafBackupId"/> into <paramref name="targetDir"/>
    /// on the node (under its restore root), at the chain's latest recoverable point. The node aborts the
    /// restore when the request is aborted, so <paramref name="timeout"/> must cover the whole replay.</summary>
    public static async Task<RestoreResponse> RestoreAsync(
        NodePlan node, string leafBackupId, string targetDir, TimeSpan timeout, CancellationToken cancellationToken)
    {
        string body = JsonSerializer.Serialize(new { leafBackupId, targetDir, targetTimeMs = 0 });
        string answer = await InContainerHttp.PostAsync(node, "/v1/restore", body, timeout, cancellationToken).ConfigureAwait(false);
        RestoreResponse restore = Parse<RestoreResponse>(answer, "/v1/restore", node);
        if (!restore.Ok)
            throw new BackupRequestException(restore.Code ?? "unknown", restore.Message ?? "restore failed");
        return restore;
    }

    /// <summary>The backup an incremental should chain on: the newest entry whose manifest read. Null for
    /// an empty catalog.</summary>
    public static BackupInfo? NewestValid(IEnumerable<BackupInfo> catalog)
        => catalog.Where(b => !b.IsInvalid).OrderByDescending(b => b.CreatedAtUtc).FirstOrDefault();

    private static async Task<BackupInfo> TakeOneAsync(NodePlan node, string path, string? jsonBody, CancellationToken cancellationToken)
    {
        string answer = await InContainerHttp.PostAsync(node, path, jsonBody, RequestTimeout, cancellationToken).ConfigureAwait(false);
        BackupResponse response = Parse<BackupResponse>(answer, path, node);
        if (!response.Ok)
            throw new BackupRequestException(response.Code ?? "unknown", response.Message ?? $"{path} failed");
        return response.Backup!;
    }

    private static T Parse<T>(string body, string what, NodePlan node)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOptions)
                ?? throw new InvalidOperationException($"{what} on {node.Name} answered nothing");
        }
        catch (JsonException e)
        {
            string shown = body.Length <= 200 ? body : body[..200] + "…";
            throw new InvalidOperationException($"{what} on {node.Name} answered something that is not its result: {shown} ({e.Message})");
        }
    }
}
