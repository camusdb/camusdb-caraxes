/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Text.Json;
using Caraxes.Core.Cluster;
using Caraxes.Core.Scenario;

namespace Caraxes.Core.Backup;

/// <summary>One link of the verified chain, as written to <c>backup-verify.json</c>.</summary>
public sealed record ChainLink(string BackupId, string Type, DateTime CreatedAtUtc, string? ParentBackupId, int PartitionCount, string? SubstitutionReason, bool IsInvalid);

/// <summary>The restore's outcome, as written to <c>backup-verify.json</c>.</summary>
public sealed record RestoreOutcome(string TargetDir, int PartitionsRestored, long EntriesApplied, long LastAppliedPhysicalMs, long MinRecoverablePhysicalMs, long MaxRecoverablePhysicalMs, int ChainLength, double Seconds);

/// <summary>
/// Everything the verification found, serialized to <c>backup-verify.json</c> in the run directory.
/// <see cref="Failures"/> is the verdict: empty means every requested step ran and held.
/// </summary>
public sealed class BackupVerifyResult
{
    public string Node { get; set; } = "";

    /// <summary>Backups the nemesis took on the node while the workload ran (timeline injects).</summary>
    public int BackupsDuringRun { get; set; }

    /// <summary>Backup requests the nemesis recorded as errors, on any node.</summary>
    public int BackupErrorsDuringRun { get; set; }

    public string? FinalBackup { get; set; }

    public string? LeafBackupId { get; set; }

    public List<ChainLink> Chain { get; set; } = [];

    public LedgerFingerprint? LiveLedger { get; set; }

    public RestoreOutcome? Restore { get; set; }

    public bool Booted { get; set; }

    public Dictionary<string, LedgerFingerprint> RestoredLedgers { get; set; } = [];

    public List<string> Notes { get; set; } = [];

    public List<string> Failures { get; set; } = [];

    public bool Passed => Failures.Count == 0;
}

/// <summary>
/// Runs the <c>backup_verify</c> stage, see <see cref="BackupVerifySpec"/>. Two phases, because the
/// rehearsal needs the live fleet gone: <see cref="VerifyLiveAsync"/> while the cluster is still up
/// (close the chain, read the live ledger, resolve the chain, restore it), <see cref="RehearseAsync"/>
/// after teardown (boot from the restore, read every node's ledger, compare). Both record what they
/// found and never throw past the stage: a failed step is a failure in the result, and the verdict
/// grades the result.
/// </summary>
public sealed class BackupVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ScenarioSpec scenario;

    private readonly BackupVerifySpec spec;

    private readonly ClusterPlan plan;

    private readonly ClusterOrchestrator orchestrator;

    private readonly string runDir;

    public BackupVerifier(ScenarioSpec scenario, ClusterPlan plan, ClusterOrchestrator orchestrator, string runDir)
    {
        this.scenario = scenario;
        spec = scenario.BackupVerify ?? throw new ArgumentException("scenario has no backup_verify block", nameof(scenario));
        this.plan = plan;
        this.orchestrator = orchestrator;
        this.runDir = runDir;
    }

    public string ResultPath => Path.Combine(runDir, "backup-verify.json");

    /// <summary>Host directory the rehearsal fleet's data roots are copied to, one per node.</summary>
    public string RestoredRoot => Path.Combine(orchestrator.BackupsRoot, "restored");

    public async Task<BackupVerifyResult> VerifyLiveAsync(string timelinePath, CancellationToken cancellationToken)
    {
        BackupVerifyResult result = new() { Node = spec.EffectiveNode(scenario) };
        NodePlan node = plan.Nodes.First(n => n.Name == result.Node);

        (result.BackupsDuringRun, result.BackupErrorsDuringRun) = CountTimelineBackups(timelinePath, result.Node);
        if (result.BackupErrorsDuringRun > 0)
            result.Failures.Add($"{result.BackupErrorsDuringRun} backup request(s) failed during the run (see timeline.jsonl); a backup that did not happen cost nothing and proves nothing");

        try
        {
            if (spec.FinalIncremental)
            {
                Console.WriteLine($"==> backup verify: closing the chain on {node.Name} with a final incremental");
                BackupTaken final = await BackupClient.TakeAsync(node, plan, "incremental", cancellationToken).ConfigureAwait(false);
                result.FinalBackup = final.Describe();
                result.Notes.Add($"final backup: {final.Describe()}");
                // The same expectation as the chain rule: a schedule of fulls asks for one closing
                // incremental on purpose, and on an engine that cannot chain it, the substitution is
                // the finding the run exists to record, not a reason to fail the rest of the proof.
                if (final.Backup.SubstitutionReason is not null)
                {
                    if (spec.RequireUnbrokenChain)
                        result.Failures.Add($"the final incremental was substituted with a full: {final.Backup.SubstitutionReason}");
                    else
                        result.Notes.Add($"FINDING: the final incremental was substituted with a full (not graded, require_unbroken_chain: false): {final.Backup.SubstitutionReason}");
                }
                if (!string.Equals(final.Node.Name, node.Name, StringComparison.Ordinal))
                    result.Failures.Add($"the final backup landed on {final.Node.Name}, not {node.Name}");
            }

            Console.WriteLine($"==> backup verify: reading the live ledger on {node.Name}");
            result.LiveLedger = await LedgerProbe.ReadAsync(node, scenario.Workload.Database, scenario.Workload.Tables, cancellationToken).ConfigureAwait(false);
            result.Notes.Add($"live ledger on {node.Name} after the final backup: {result.LiveLedger.Describe()}");

            IReadOnlyList<BackupInfo> catalog = await BackupClient.ListAsync(node, cancellationToken).ConfigureAwait(false);
            BackupInfo? leaf = BackupClient.NewestValid(catalog);
            if (leaf is null)
            {
                result.Failures.Add($"{node.Name} has no backup in its catalog; nothing was taken on it during the run");
                return Write(result);
            }
            result.LeafBackupId = leaf.BackupId;

            Console.WriteLine($"==> backup verify: resolving the chain ending at {leaf.ShortId}");
            IReadOnlyList<BackupInfo> chain = await BackupClient.ChainAsync(node, leaf.BackupId, cancellationToken).ConfigureAwait(false);
            result.Chain = chain.Select(b => new ChainLink(b.BackupId, b.Type, b.CreatedAtUtc, b.ParentBackupId, b.PartitionCount, b.SubstitutionReason, b.IsInvalid)).ToList();
            int expected = result.BackupsDuringRun + (spec.FinalIncremental ? 1 : 0);
            result.Failures.AddRange(CheckChain(chain, expected, catalog.Count, spec.RequireUnbrokenChain));
            BackupInfo head = chain[^1];
            result.Notes.Add(
                $"chain of {chain.Count} ({chain.Count(b => b.IsFull)} full, {chain.Count(b => b.IsIncremental)} incremental) over {head.PartitionCount} partition(s), " +
                $"recoverable {Stamp(head.MinRecoverablePhysicalMs)} .. {Stamp(head.MaxRecoverablePhysicalMs)}; catalog holds {catalog.Count}");

            if (!spec.Restore)
            {
                result.Notes.Add("restore not requested (backup_verify.restore: false); the chain was resolved, not read back");
                return Write(result);
            }

            string targetDir = $"{NodeConfigGenerator.BackupRestoreRoot}/{leaf.BackupId}";
            Console.WriteLine($"==> backup verify: restoring the chain into {targetDir} on {node.Name}");
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            RestoreResponse restore = await BackupClient.RestoreAsync(node, leaf.BackupId, targetDir, cancellationToken).ConfigureAwait(false);
            result.Restore = new RestoreOutcome(
                restore.DataRoot ?? targetDir, restore.PartitionsRestored, restore.EntriesApplied, restore.LastAppliedPhysicalMs,
                restore.MinRecoverablePhysicalMs, restore.MaxRecoverablePhysicalMs, restore.Chain.Count, clock.Elapsed.TotalSeconds);
            result.Notes.Add(
                $"restored {restore.Chain.Count}-link chain in {clock.Elapsed.TotalSeconds:0.0} s: {restore.PartitionsRestored} partition(s), " +
                $"{restore.EntriesApplied:N0} WAL entries replayed, last applied {Stamp(restore.LastAppliedPhysicalMs)}");
            result.Failures.AddRange(CheckRestore(restore, chain));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            result.Failures.Add($"backup verification stopped: {e.Message}");
        }

        return Write(result);
    }

    /// <summary>
    /// The disaster-recovery rehearsal. Copies the restore into one data root per node, brings the
    /// fleet up on them, reads the ledger from every node and compares each with the live one, then
    /// tears the fleet down and removes the copies (the restore itself stays under the node's backup
    /// mount). Must run after the live fleet is down.
    /// </summary>
    public async Task<BackupVerifyResult> RehearseAsync(BackupVerifyResult result, CancellationToken cancellationToken)
    {
        if (!spec.Boot)
        {
            result.Notes.Add("rehearsal not requested (backup_verify.boot: false); the restore was not read back");
            return Write(result);
        }

        if (result.Restore is null || result.LiveLedger is null)
        {
            result.Failures.Add("rehearsal skipped: there is no restore to boot from");
            return Write(result);
        }

        NodePlan node = plan.Nodes.First(n => n.Name == result.Node);
        string restoreHostDir = Path.Combine(orchestrator.BackupsDirOf(node), "restore", result.LeafBackupId!);
        bool up = false;

        try
        {
            Console.WriteLine($"==> backup verify: copying the restore into {plan.Nodes.Count} data root(s) under {RestoredRoot}");
            foreach (NodePlan n in plan.Nodes)
            {
                string target = Path.Combine(RestoredRoot, n.Name);
                Directory.CreateDirectory(target);
                await orchestrator.CopyTreeAsRootAsync(restoreHostDir, target, cancellationToken).ConfigureAwait(false);
            }

            await orchestrator.UpRestoredAsync(RestoredRoot, cancellationToken: cancellationToken).ConfigureAwait(false);
            up = true;
            result.Booted = true;

            foreach (NodePlan n in plan.Nodes)
            {
                try
                {
                    LedgerFingerprint restored = await LedgerProbe.ReadAsync(n, scenario.Workload.Database, scenario.Workload.Tables, cancellationToken).ConfigureAwait(false);
                    result.RestoredLedgers[n.Name] = restored;
                    if (restored == result.LiveLedger)
                        result.Notes.Add($"restored ledger on {n.Name} matches the live ledger: {restored.Describe()}");
                    else
                        result.Failures.Add($"restored ledger on {n.Name} differs from the live ledger: restored {restored.Describe()}; live {result.LiveLedger.Describe()}");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    result.Failures.Add($"restored ledger on {n.Name} could not be read: {e.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            result.Failures.Add($"rehearsal failed: {e.Message}");
        }
        finally
        {
            if (up)
            {
                try
                {
                    await orchestrator.DownRestoredAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    result.Notes.Add($"rehearsal teardown reported: {e.Message}");
                }
            }

            try
            {
                if (Directory.Exists(RestoredRoot))
                {
                    await orchestrator.RemoveContentsAsRootAsync(RestoredRoot, CancellationToken.None).ConfigureAwait(false);
                    Directory.Delete(RestoredRoot, recursive: true);
                }
            }
            catch (Exception e)
            {
                result.Notes.Add($"the restored data roots under {RestoredRoot} could not be removed: {e.Message}");
            }
        }

        return Write(result);
    }

    /// <summary>
    /// The chain rules: a full at the root and incrementals after it, every link readable, and — when
    /// required — every backup the run took on the node still in the leaf's chain. A chain shorter
    /// than the count taken means the engine restarted it with a full somewhere (a substitution), and
    /// the catalog count says whether the earlier links are still there.
    /// </summary>
    public static IReadOnlyList<string> CheckChain(IReadOnlyList<BackupInfo> chain, int expectedLength, int catalogCount, bool requireUnbroken)
    {
        List<string> failures = [];
        if (chain.Count == 0)
        {
            failures.Add("the chain resolved to nothing");
            return failures;
        }

        if (!chain[0].IsFull)
            failures.Add($"the chain's root {chain[0].ShortId} is {chain[0].Type}, not a full backup");

        foreach (BackupInfo link in chain.Skip(1))
            if (!link.IsIncremental)
                failures.Add($"chain link {link.ShortId} is {link.Type}; every link after the root must be an incremental");

        foreach (BackupInfo link in chain.Where(b => b.IsInvalid))
            failures.Add($"chain link {link.ShortId} is invalid: {link.InvalidReason}");

        foreach (BackupInfo link in chain.Where(b => b.SubstitutionReason is not null))
            failures.Add($"chain link {link.ShortId} was requested as {link.RequestedKind} and taken as {link.ActualKind}: {link.SubstitutionReason}");

        if (requireUnbroken && expectedLength > 0 && chain.Count != expectedLength)
            failures.Add(
                $"the leaf's chain has {chain.Count} link(s) but the run took {expectedLength} backup(s) on this node " +
                $"(catalog holds {catalogCount}); the chain was restarted or a backup is missing from it");

        return failures;
    }

    /// <summary>The restore rules: it answered ok, and when the chain carries incrementals it replayed
    /// them into every partition the root covers. The engine counts a partition as restored when a
    /// segment was replayed into it, so a chain of one full reports zero partitions and zero entries,
    /// which is the base image alone and not a failure.</summary>
    public static IReadOnlyList<string> CheckRestore(RestoreResponse restore, IReadOnlyList<BackupInfo> chain)
    {
        List<string> failures = [];
        int partitions = chain.Count == 0 ? 0 : chain[0].PartitionCount;

        if (chain.Count > 1)
        {
            if (restore.EntriesApplied == 0)
                failures.Add($"the chain has {chain.Count - 1} incremental(s) and the restore replayed 0 WAL entries from them");
            if (restore.PartitionsRestored <= 0)
                failures.Add("the restore replayed into no partition");
            else if (partitions > 0 && restore.PartitionsRestored != partitions)
                failures.Add($"the restore replayed into {restore.PartitionsRestored} partition(s) but the chain's root covers {partitions}");
        }

        if (restore.Chain.Count != chain.Count)
            failures.Add($"the restore walked a chain of {restore.Chain.Count} while the resolved chain has {chain.Count}");

        return failures;
    }

    /// <summary>Counts the nemesis's backup injects on <paramref name="node"/> and its backup errors on any node.</summary>
    public static (int Taken, int Errors) CountTimelineBackups(string timelinePath, string node)
    {
        if (!File.Exists(timelinePath))
            return (0, 0);

        int taken = 0;
        int errors = 0;
        foreach (string line in File.ReadLines(timelinePath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;
                if (!root.TryGetProperty("kind", out JsonElement kind) || kind.GetString() != "backup")
                    continue;
                string phase = root.TryGetProperty("phase", out JsonElement p) ? p.GetString() ?? "" : "";
                string? target = root.TryGetProperty("target", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                if (phase == "inject" && target == node)
                    taken++;
                else if (phase == "error")
                    errors++;
            }
            catch (JsonException)
            {
                // a malformed line is not a backup
            }
        }

        return (taken, errors);
    }

    private BackupVerifyResult Write(BackupVerifyResult result)
    {
        File.WriteAllText(ResultPath, JsonSerializer.Serialize(result, JsonOptions));
        return result;
    }

    private static string Stamp(long? physicalMs) => physicalMs is null or 0
        ? "n/a"
        : DateTimeOffset.FromUnixTimeMilliseconds(physicalMs.Value).UtcDateTime.ToString("HH:mm:ss.fff") + "Z";
}
