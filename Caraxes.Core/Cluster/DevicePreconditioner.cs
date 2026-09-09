using System.Diagnostics;
using System.Text.Json;

namespace Caraxes.Core.Cluster;

/// <summary>
/// Writes a fixed amount of ballast to the device backing the run directory, with periodic fsync, and
/// deletes it — so that the measured window opens with the device already past its write-cache regime.
///
/// <para>Why: the benchmark host's NVMe serves the first ~30-35 GB of a run's writes out of an SLC write
/// cache (fsync 0.4 ms) and the rest from the folded medium (1.9 ms), and it folds the cache back
/// during idle gaps. A soak that writes 100 MB/s therefore crosses the regime boundary at a random
/// minute decided by how much the previous run left in the cache, which is the between-run variable
/// feature 80af367a could not find. Filling the cache immediately before the measured window, while the
/// workload's own warm-up keeps the device busy so nothing folds back, puts every run in the same (slow,
/// steady) regime from its first measured second. The fast regime was never the device's sustained
/// speed; measuring in the steady one is the honest denominator.</para>
///
/// <para>The ballast runs concurrently with the workload's warm-up and must finish before the measured
/// window opens; <c>precondition.json</c> records both timestamps so <c>p3c regime</c> can refuse a run
/// whose ballast overlapped the window. Buffered sequential 8 MiB writes with an fsync per GiB reach
/// 1-2 GB/s on this host, so 48 GiB fits inside the two-minute warm-up; the first attempt with
/// WriteThrough ran at 280 MB/s and did not.</para>
/// </summary>
public sealed class DevicePreconditioner
{
    private const int ChunkBytes = 8 * 1024 * 1024;
    private const long FsyncEveryBytes = 1L * 1024 * 1024 * 1024;

    public sealed record Result(DateTime StartUtc, DateTime EndUtc, long Bytes, double MegabytesPerSecond, string Device);

    private readonly string runDir;
    private readonly long bytes;

    public DevicePreconditioner(string runDir, int gibibytes)
        : this(runDir, (long)gibibytes * 1024 * 1024 * 1024) { }

    /// <summary>Byte-sized form for tests; production sizing is whole GiB.</summary>
    public static DevicePreconditioner ForBytes(string runDir, long bytes) => new(runDir, bytes);

    private DevicePreconditioner(string runDir, long bytes)
    {
        this.runDir = runDir;
        this.bytes = bytes;
    }

    public static string RecordPath(string runDir) => Path.Combine(runDir, "precondition.json");

    public async Task<Result> RunAsync(CancellationToken cancellationToken)
    {
        string path = Path.Combine(runDir, "precondition.bin");
        string device = HostIoMonitor.ResolveDevice(runDir) ?? "unknown";
        DateTime start = DateTime.UtcNow;
        Stopwatch sw = Stopwatch.StartNew();
        byte[] chunk = new byte[ChunkBytes];
        // Non-zero, non-repeating bytes: a controller that dedups or compresses zero pages would fill
        // nothing. A fixed seed keeps the pattern identical across runs.
        new Random(0x5EED).NextBytes(chunk);

        try
        {
            // Buffered sequential writes with an fsync per GiB, not WriteThrough: O_SYNC per 8 MiB write ran
            // at ~280 MB/s beside the warm-up (48 GiB would outlast a two-minute warm-up and leak into the
            // window), while buffered writes reach 1-2 GB/s. What matters is that the bytes have reached
            // the device by the final fsync, which is before the record is written.
            await using (FileStream fs = new(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20,
                FileOptions.SequentialScan))
            {
                long written = 0;
                long sinceSync = 0;
                while (written < bytes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int n = (int)Math.Min(ChunkBytes, bytes - written);
                    await fs.WriteAsync(chunk.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                    written += n;
                    sinceSync += n;
                    if (sinceSync >= FsyncEveryBytes)
                    {
                        fs.Flush(flushToDisk: true);
                        sinceSync = 0;
                    }
                }
                fs.Flush(flushToDisk: true);
            }
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort: the ballast is only a side effect */ }
        }

        sw.Stop();
        Result result = new(start, DateTime.UtcNow, bytes, bytes / 1e6 / Math.Max(0.001, sw.Elapsed.TotalSeconds), device);
        await File.WriteAllTextAsync(RecordPath(runDir),
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>Reads the record a run wrote, or null when the run was not preconditioned.</summary>
    public static Result? TryRead(string runDir)
    {
        string path = RecordPath(runDir);
        if (!File.Exists(path))
            return null;
        return JsonSerializer.Deserialize<Result>(File.ReadAllText(path));
    }
}
