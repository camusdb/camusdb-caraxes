/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using System.Globalization;

namespace Caraxes.Core.Nemesis;

/// <summary>One data file a node holds, as a listing of its data mount reports it.</summary>
public sealed record StorageFile(string Path, long Length);

/// <summary>The bytes after a damage, and what was done to them in terms a reader can check by hand.</summary>
public sealed record StorageDamageResult(byte[] Bytes, string Description);

/// <summary>
/// The storage faults that <see cref="DiskCorruptFault"/> applies, as pure functions over a file's bytes:
/// deterministic for a seed, so a cell reproduces exactly, and testable without a container.
///
/// <para>Each kind models something a disk or a filesystem really does, and each one reaches a different
/// defense. A flipped bit is what a checksum exists for. A shuffled, duplicated or replaced 4 KiB chunk is a
/// misdirected write: every byte can be valid at another offset, so only a checksum bound to its place
/// catches it. A truncation removes synced data with no damaged byte left to find, so no checksum sees it,
/// and only a record kept outside the file can.</para>
///
/// <para>A damage that leaves the bytes unchanged is refused rather than returned. A chunk shuffle over
/// identical chunks, for example, changes nothing, and a cell that ran it would report a pass for a fault
/// that never happened.</para>
/// </summary>
public static class StorageDamage
{
    /// <summary>The aligned unit of a misdirected write: a page, as in the report this fault models.</summary>
    public const int ChunkBytes = 4096;

    public const string BitFlip = "bit-flip";

    public const string TruncateTail = "truncate-tail";

    public const string TruncateMiddle = "truncate-middle";

    public const string ChunkShuffle = "chunk-shuffle";

    public const string ChunkDuplicate = "chunk-duplicate";

    public const string ChunkReplace = "chunk-replace";

    public static readonly IReadOnlyList<string> Kinds =
        [BitFlip, TruncateTail, TruncateMiddle, ChunkShuffle, ChunkDuplicate, ChunkReplace];

    public const string LayerStore = "store";

    public const string LayerWal = "wal";

    public static readonly IReadOnlyList<string> Layers = [LayerStore, LayerWal];

    public const string FileSst = "sst";

    public const string FileLiveLog = "live-log";

    public const string FileClosedLog = "closed-log";

    public const string FileManifest = "manifest";

    public static readonly IReadOnlyList<string> Files = [FileSst, FileLiveLog, FileClosedLog, FileManifest];

    /// <summary>
    /// Applies <paramref name="kind"/> to <paramref name="original"/>, which it never changes.
    /// <paramref name="count"/> is the number of bits for <see cref="BitFlip"/> and of chunks for
    /// <see cref="ChunkShuffle"/>; the other kinds ignore it. Throws when the file cannot take the damage or
    /// the damage would change nothing, so a fault that did not happen fails loudly.
    /// </summary>
    public static StorageDamageResult Apply(string kind, byte[] original, int seed, int count)
    {
        if (original.Length == 0)
            throw new InvalidOperationException("the file is empty, so there is nothing to damage");

        Random random = new(seed);

        StorageDamageResult result = kind switch
        {
            BitFlip => FlipBits(original, random, count),
            TruncateTail => CutTail(original),
            TruncateMiddle => CutMiddle(original),
            ChunkShuffle => ShuffleChunks(original, random, count),
            ChunkDuplicate => DuplicateChunk(original, random),
            ChunkReplace => ReplaceChunk(original, random),
            _ => throw new ArgumentException($"unknown damage '{kind}'; known: {string.Join(", ", Kinds)}", nameof(kind)),
        };

        if (result.Bytes.AsSpan().SequenceEqual(original))
            throw new InvalidOperationException($"{kind} left the file unchanged, so it would test nothing: {result.Description}");

        return result;
    }

    /// <summary>
    /// Picks the file to damage from a listing of the node's data mount. A store path holds <c>/kv/</c> and a
    /// Raft log path holds <c>/wal/</c>. Returns null when the layer holds no such file yet: for example, no
    /// SST exists until a memtable flushes, and the caller must fail rather than damage another file.
    ///
    /// <para><see cref="FileLiveLog"/> is the highest-numbered log, which RocksDB has open for writes. It
    /// is the only log whose tail no manifest protects. <see cref="FileClosedLog"/> is the largest of the
    /// others. A listing also holds RocksDB's text log (<c>LOG</c>) and Kommander's ledgers, and none of them
    /// matches, because damaging the proof of a loss is a different fault from the loss itself.</para>
    /// </summary>
    public static StorageFile? SelectFile(IEnumerable<StorageFile> files, string layer, string file)
    {
        string marker = layer switch
        {
            LayerStore => "/kv/",
            LayerWal => "/wal/",
            _ => throw new ArgumentException($"unknown layer '{layer}'; known: {string.Join(", ", Layers)}", nameof(layer)),
        };

        List<StorageFile> inLayer = files.Where(f => f.Path.Contains(marker, StringComparison.Ordinal)).ToList();

        List<StorageFile> logs = inLayer
            .Where(f => f.Path.EndsWith(".log", StringComparison.Ordinal))
            .OrderBy(f => FileNumber(f.Path))
            .ToList();

        return file switch
        {
            FileSst => inLayer
                .Where(f => f.Path.EndsWith(".sst", StringComparison.Ordinal))
                .OrderByDescending(f => f.Length)
                .ThenBy(f => f.Path, StringComparer.Ordinal)
                .FirstOrDefault(),
            FileLiveLog => logs.LastOrDefault(),
            FileClosedLog => logs.Count < 2
                ? null
                : logs.Take(logs.Count - 1).OrderByDescending(f => f.Length).ThenBy(f => f.Path, StringComparer.Ordinal).First(),
            FileManifest => inLayer
                .Where(f => System.IO.Path.GetFileName(f.Path).StartsWith("MANIFEST-", StringComparison.Ordinal))
                .OrderBy(f => FileNumber(f.Path))
                .LastOrDefault(),
            _ => throw new ArgumentException($"unknown file '{file}'; known: {string.Join(", ", Files)}", nameof(file)),
        };
    }

    /// <summary>Parses the lines of <c>find -printf '%s\t%p\n'</c>. A malformed line is an error, not a skip:
    /// a partial listing could select the wrong file without anyone noticing.</summary>
    public static IReadOnlyList<StorageFile> ParseListing(string listing)
    {
        List<StorageFile> files = [];

        foreach (string line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int tab = line.IndexOf('\t');
            if (tab <= 0 || !long.TryParse(line.AsSpan(0, tab), NumberStyles.None, CultureInfo.InvariantCulture, out long length))
                throw new FormatException($"cannot read a data file listing line: '{line}'");

            files.Add(new StorageFile(line[(tab + 1)..], length));
        }

        return files;
    }

    private static long FileNumber(string path)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        int dash = name.LastIndexOf('-');
        string digits = dash >= 0 ? name[(dash + 1)..] : name;
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long number) ? number : -1;
    }

    private static StorageDamageResult FlipBits(byte[] original, Random random, int count)
    {
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count), count, "bit-flip needs at least one bit");

        byte[] bytes = (byte[])original.Clone();
        HashSet<(long Offset, int Bit)> flipped = [];

        // Two flips of one bit would cancel, so each bit is flipped once.
        while (flipped.Count < Math.Min(count, original.Length * 8L))
        {
            long offset = random.NextInt64(original.Length);
            int bit = random.Next(8);

            if (!flipped.Add((offset, bit)))
                continue;

            bytes[offset] ^= (byte)(1 << bit);
        }

        return new StorageDamageResult(
            bytes,
            $"flipped {flipped.Count} bit(s) at " + string.Join(", ", flipped.OrderBy(f => f.Offset).Select(f => $"{f.Offset}.{f.Bit}")));
    }

    private static StorageDamageResult CutTail(byte[] original)
    {
        int keep = original.Length / 2;
        return new StorageDamageResult(original[..keep], $"kept the first {keep} of {original.Length} bytes");
    }

    private static StorageDamageResult CutMiddle(byte[] original)
    {
        if (original.Length < 4)
            throw new InvalidOperationException($"a file of {original.Length} bytes has no middle to cut");

        int start = original.Length / 4;
        int end = original.Length * 3 / 4;

        byte[] bytes = new byte[original.Length - (end - start)];
        original.AsSpan(0, start).CopyTo(bytes);
        original.AsSpan(end).CopyTo(bytes.AsSpan(start));

        return new StorageDamageResult(bytes, $"removed bytes [{start}, {end}) of {original.Length}");
    }

    private static StorageDamageResult ShuffleChunks(byte[] original, Random random, int count)
    {
        int chunks = original.Length / ChunkBytes;
        if (count < 2)
            throw new ArgumentOutOfRangeException(nameof(count), count, "chunk-shuffle needs at least two chunks");
        if (chunks < count)
            throw new InvalidOperationException($"a file of {original.Length} bytes holds {chunks} whole chunks, fewer than {count}");

        int[] selected = Enumerable.Range(0, chunks).OrderBy(_ => random.Next()).Take(count).OrderBy(c => c).ToArray();

        // A cycle, not a random permutation: every selected chunk leaves its own place.
        byte[] bytes = (byte[])original.Clone();
        for (int i = 0; i < selected.Length; i++)
        {
            int from = selected[i];
            int to = selected[(i + 1) % selected.Length];
            original.AsSpan(from * ChunkBytes, ChunkBytes).CopyTo(bytes.AsSpan(to * ChunkBytes));
        }

        return new StorageDamageResult(bytes, $"moved {count} aligned {ChunkBytes}-byte chunks one place along the cycle {string.Join(",", selected)}");
    }

    private static StorageDamageResult DuplicateChunk(byte[] original, Random random)
    {
        (int from, int to) = TwoChunks(original, random);

        byte[] bytes = (byte[])original.Clone();
        original.AsSpan(from * ChunkBytes, ChunkBytes).CopyTo(bytes.AsSpan(to * ChunkBytes));

        return new StorageDamageResult(bytes, $"copied chunk {from} over chunk {to}");
    }

    private static StorageDamageResult ReplaceChunk(byte[] original, Random random)
    {
        (_, int to) = TwoChunks(original, random);

        byte[] bytes = (byte[])original.Clone();
        random.NextBytes(bytes.AsSpan(to * ChunkBytes, ChunkBytes));

        return new StorageDamageResult(bytes, $"overwrote chunk {to} with random bytes");
    }

    private static (int From, int To) TwoChunks(byte[] original, Random random)
    {
        int chunks = original.Length / ChunkBytes;
        if (chunks < 2)
            throw new InvalidOperationException($"a file of {original.Length} bytes holds {chunks} whole chunks, fewer than 2");

        int from = random.Next(chunks);
        int to = random.Next(chunks - 1);
        return (from, to >= from ? to + 1 : to);
    }
}
