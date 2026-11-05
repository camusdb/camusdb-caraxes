/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Security.Cryptography;
using System.Text.Json;
using Caraxes.Core.Cluster;

namespace Caraxes.Core.Nemesis;

/// <summary>
/// Damages one data file of a stopped node, then restarts it on heal: the offline half of the storage
/// corruption campaign. The node is killed with SIGKILL, never stopped gracefully, so nothing flushes or
/// tidies its files first and the damage lands on what a crash really leaves.
///
/// <para>The edit goes through <c>docker cp</c>, which works on a stopped container and reaches its
/// volume. The bytes are changed here rather than in the container, so <see cref="StorageDamage"/> stays
/// pure and the node's image needs no tool for it. A helper container lists the files, because a stopped
/// container cannot run a command. It needs a volume: a tmpfs data mount is gone once the container stops,
/// so the fault refuses it.</para>
///
/// <para><b>A failed injection must not read as a pass.</b> The runner logs a fault's error and goes on, so
/// a cell whose file did not exist yet would otherwise test nothing and look clean. Every damage that
/// really happened therefore appends one line to <c>storage-damage.jsonl</c> with the file, its length and
/// SHA-256 before and after, and the seed, and a cell without its line has no result. The line is written
/// only after a read-back shows the node's copy holds the damaged bytes.</para>
///
/// <para>Not covered here: damage injected while the process runs, and lost unsynced writes, which need a
/// filesystem that can drop them.</para>
/// </summary>
public sealed class DiskCorruptFault : IFault
{
    private static readonly JsonSerializerOptions EvidenceJson = new() { WriteIndented = false };

    private readonly string damage;

    private readonly string layer;

    private readonly string file;

    private readonly int seed;

    private readonly int count;

    private readonly NemesisContext context;

    private string? lastOutcome;

    public DiskCorruptFault(string damage, string layer, string file, int seed, int count, NemesisContext context)
    {
        this.damage = damage;
        this.layer = layer;
        this.file = file;
        this.seed = seed;
        this.count = count;
        this.context = context;
    }

    public string Kind => "disk-corrupt";

    public bool Healable => true;

    public string Describe(NodePlan target) =>
        lastOutcome ?? $"{damage} of the {layer} {file} on stopped {target.ContainerName} (seed {seed})";

    public async Task InjectAsync(NodePlan target, ClusterPlan plan, CancellationToken cancellationToken)
    {
        if (plan.Spec.DataTmpfsMb > 0)
            throw new InvalidOperationException(
                "disk-corrupt needs a volume-backed data mount: with data_tmpfs_mb the node's files are gone when its container stops");

        ClusterOrchestrator orchestrator = context.Orchestrator
            ?? throw new InvalidOperationException("disk-corrupt needs the orchestrator, to keep its evidence in the run directory");

        await ProcessRunner.RunCheckedAsync(
            "docker", ["kill", "--signal", "KILL", target.ContainerName], cancellationToken: cancellationToken).ConfigureAwait(false);

        IReadOnlyList<StorageFile> listing = await ListDataFilesAsync(target, cancellationToken).ConfigureAwait(false);

        StorageFile selected = StorageDamage.SelectFile(listing, layer, file)
            ?? throw new InvalidOperationException(
                $"{target.Name} holds no {layer} {file} yet ({listing.Count} data files listed), so {damage} would test nothing; " +
                "restart the node first so its memtables reach an SST, or run longer");

        string workDir = Path.Combine(orchestrator.StorageDamageDir, $"{target.Name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        string local = Path.Combine(workDir, Path.GetFileName(selected.Path));

        await CopyOutAsync(target, selected.Path, local, cancellationToken).ConfigureAwait(false);

        byte[] original = await File.ReadAllBytesAsync(local, cancellationToken).ConfigureAwait(false);
        StorageDamageResult result = StorageDamage.Apply(damage, original, seed, count);

        await File.WriteAllBytesAsync(local, result.Bytes, cancellationToken).ConfigureAwait(false);
        await ProcessRunner.RunCheckedAsync(
            "docker", ["cp", local, $"{target.ContainerName}:{selected.Path}"], cancellationToken: cancellationToken).ConfigureAwait(false);

        // Read back what the node will see. The evidence line must describe the node's file, not this copy.
        await CopyOutAsync(target, selected.Path, local, cancellationToken).ConfigureAwait(false);
        byte[] onNode = await File.ReadAllBytesAsync(local, cancellationToken).ConfigureAwait(false);

        if (!onNode.AsSpan().SequenceEqual(result.Bytes))
            throw new InvalidOperationException($"the damage to {selected.Path} on {target.Name} did not reach the node's volume");

        File.Delete(local);
        Directory.Delete(workDir);

        await AppendEvidenceAsync(orchestrator, new StorageDamageEvidence(
            DateTime.UtcNow, target.Name, damage, layer, file, seed, count, selected.Path,
            original.Length, Sha256(original), onNode.Length, Sha256(onNode), result.Description), cancellationToken).ConfigureAwait(false);

        lastOutcome = $"{damage} of {selected.Path} on {target.Name}: {result.Description}";
    }

    public Task HealAsync(NodePlan target, ClusterPlan plan, CancellationToken cancellationToken)
        => ProcessRunner.RunCheckedAsync("docker", ["start", target.ContainerName], cancellationToken: cancellationToken);

    private static async Task<IReadOnlyList<StorageFile>> ListDataFilesAsync(NodePlan target, CancellationToken cancellationToken)
    {
        ProcessResult inspect = await ProcessRunner.RunCheckedAsync(
            "docker", ["inspect", "--format", "{{.Config.Image}}", target.ContainerName], cancellationToken: cancellationToken).ConfigureAwait(false);

        ProcessResult listing = await ProcessRunner.RunCheckedAsync(
            "docker",
            ["run", "--rm", "--network", "none", "--volumes-from", target.ContainerName, "--entrypoint", "find", inspect.StdOut.Trim(),
             "/data", "-type", "f", "-printf", "%s\t%p\n"],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return StorageDamage.ParseListing(listing.StdOut);
    }

    private static Task CopyOutAsync(NodePlan target, string path, string local, CancellationToken cancellationToken)
        => ProcessRunner.RunCheckedAsync("docker", ["cp", $"{target.ContainerName}:{path}", local], cancellationToken: cancellationToken);

    private static async Task AppendEvidenceAsync(ClusterOrchestrator orchestrator, StorageDamageEvidence evidence, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(orchestrator.StorageDamageLogPath)!);
        await File.AppendAllTextAsync(
            orchestrator.StorageDamageLogPath, JsonSerializer.Serialize(evidence, EvidenceJson) + "\n", cancellationToken).ConfigureAwait(false);
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

/// <summary>One damage that reached a node's volume: the line a cell's result is judged against. The two
/// hashes let a later check tell whether a node rewrote or deleted the file it was given.</summary>
public sealed record StorageDamageEvidence(
    DateTime InjectedUtc,
    string Node,
    string Damage,
    string Layer,
    string File,
    int Seed,
    int Count,
    string Path,
    long LengthBefore,
    string Sha256Before,
    long LengthAfter,
    string Sha256After,
    string Description);
