/**
 * This file is part of Caraxes
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 */

using NUnit.Framework;
using Caraxes.Core.Cluster;
using Caraxes.Core.Nemesis;

namespace Caraxes.Tests;

[TestFixture]
public sealed class StorageDamageTests
{
    private static byte[] Pseudorandom(int length)
    {
        byte[] bytes = new byte[length];
        new Random(7).NextBytes(bytes);
        return bytes;
    }

    [TestCaseSource(typeof(StorageDamage), nameof(StorageDamage.Kinds))]
    public void EveryKind_ChangesTheBytes_LeavesTheOriginal_AndReproducesForASeed(string kind)
    {
        byte[] original = Pseudorandom(StorageDamage.ChunkBytes * 32);
        byte[] copy = (byte[])original.Clone();

        StorageDamageResult first = StorageDamage.Apply(kind, original, seed: 11, count: 3);
        StorageDamageResult second = StorageDamage.Apply(kind, original, seed: 11, count: 3);

        Assert.That(first.Bytes, Is.Not.EqualTo(original));
        Assert.That(original, Is.EqualTo(copy), "the caller keeps the original for its hash");
        Assert.That(second.Bytes, Is.EqualTo(first.Bytes), "a cell must reproduce from its seed");
        Assert.That(first.Description, Is.Not.Empty);
    }

    [Test]
    public void ABitFlip_ChangesExactlyTheCountedBits()
    {
        byte[] original = Pseudorandom(4096);

        byte[] damaged = StorageDamage.Apply(StorageDamage.BitFlip, original, seed: 3, count: 5).Bytes;

        Assert.That(damaged.Length, Is.EqualTo(original.Length));
        int differing = original.Zip(damaged, (a, b) => System.Numerics.BitOperations.PopCount((uint)(a ^ b))).Sum();
        Assert.That(differing, Is.EqualTo(5));
    }

    [Test]
    public void ATailCut_KeepsAnExactPrefix()
    {
        byte[] original = Pseudorandom(1000);

        byte[] damaged = StorageDamage.Apply(StorageDamage.TruncateTail, original, seed: 1, count: 1).Bytes;

        Assert.That(damaged, Is.EqualTo(original[..500]));
    }

    [Test]
    public void AMiddleCut_KeepsBothEnds()
    {
        byte[] original = Pseudorandom(1000);

        byte[] damaged = StorageDamage.Apply(StorageDamage.TruncateMiddle, original, seed: 1, count: 1).Bytes;

        Assert.That(damaged, Is.EqualTo(original[..250].Concat(original[750..]).ToArray()));
    }

    [Test]
    public void AChunkShuffle_MovesEverySelectedChunk_AndChangesNoOtherByte()
    {
        const int chunks = 40;
        const int moved = 13;
        byte[] original = Pseudorandom(StorageDamage.ChunkBytes * chunks + 100);

        byte[] damaged = StorageDamage.Apply(StorageDamage.ChunkShuffle, original, seed: 5, count: moved).Bytes;

        Assert.That(damaged.Length, Is.EqualTo(original.Length));

        int changed = 0;
        for (int c = 0; c < chunks; c++)
            if (!original.AsSpan(c * StorageDamage.ChunkBytes, StorageDamage.ChunkBytes)
                    .SequenceEqual(damaged.AsSpan(c * StorageDamage.ChunkBytes, StorageDamage.ChunkBytes)))
                changed++;

        Assert.That(changed, Is.EqualTo(moved));
        Assert.That(damaged[^100..], Is.EqualTo(original[^100..]), "the unaligned tail is not a chunk");
    }

    [Test]
    public void ADamageThatChangesNothing_IsRefused()
    {
        byte[] zeros = new byte[StorageDamage.ChunkBytes * 8];

        Assert.That(() => StorageDamage.Apply(StorageDamage.ChunkShuffle, zeros, seed: 1, count: 4),
            Throws.InvalidOperationException.With.Message.Contains("unchanged"));
        Assert.That(() => StorageDamage.Apply(StorageDamage.ChunkDuplicate, zeros, seed: 1, count: 1),
            Throws.InvalidOperationException.With.Message.Contains("unchanged"));
    }

    [TestCase(StorageDamage.ChunkShuffle, StorageDamage.ChunkBytes * 3, 13)]
    [TestCase(StorageDamage.ChunkDuplicate, StorageDamage.ChunkBytes, 1)]
    [TestCase(StorageDamage.ChunkReplace, 100, 1)]
    public void AFileTooSmallForTheDamage_IsRefused(string kind, int length, int count)
    {
        Assert.That(() => StorageDamage.Apply(kind, Pseudorandom(length), seed: 1, count: count), Throws.InvalidOperationException);
    }

    [Test]
    public void AnEmptyFile_IsRefused()
    {
        Assert.That(() => StorageDamage.Apply(StorageDamage.BitFlip, [], seed: 1, count: 1), Throws.InvalidOperationException);
    }

    private static readonly StorageFile[] Listing =
    [
        new("/data/kv/v2/000012.sst", 9_000),
        new("/data/kv/v2/000031.sst", 50_000),
        new("/data/kv/v2/000009.log", 7_000),
        new("/data/kv/v2/000040.log", 100),
        new("/data/kv/v2/LOG", 80_000),
        new("/data/kv/v2/MANIFEST-000005", 300),
        new("/data/kv/v2/MANIFEST-000037", 400),
        new("/data/wal/v2/000100.sst", 1_000),
        new("/data/wal/v2/000099.log", 60_000),
        new("/data/wal/v2/000101.log", 500),
        new("/data/wal/v2/.kommander-sync-ledger", 1_024),
        new("/data/wal/v2/.kommander-hard-state", 2_048),
    ];

    [TestCase(StorageDamage.LayerStore, StorageDamage.FileSst, "/data/kv/v2/000031.sst")]
    [TestCase(StorageDamage.LayerStore, StorageDamage.FileLiveLog, "/data/kv/v2/000040.log")]
    [TestCase(StorageDamage.LayerStore, StorageDamage.FileClosedLog, "/data/kv/v2/000009.log")]
    [TestCase(StorageDamage.LayerStore, StorageDamage.FileManifest, "/data/kv/v2/MANIFEST-000037")]
    [TestCase(StorageDamage.LayerWal, StorageDamage.FileSst, "/data/wal/v2/000100.sst")]
    [TestCase(StorageDamage.LayerWal, StorageDamage.FileLiveLog, "/data/wal/v2/000101.log")]
    [TestCase(StorageDamage.LayerWal, StorageDamage.FileClosedLog, "/data/wal/v2/000099.log")]
    public void TheFileIsSelectedFromItsLayer(string layer, string file, string expected)
    {
        Assert.That(StorageDamage.SelectFile(Listing, layer, file)?.Path, Is.EqualTo(expected));
    }

    [Test]
    public void AFileTheLayerDoesNotHoldYet_SelectsNothing()
    {
        StorageFile[] fresh = [new("/data/kv/v2/000004.log", 100), new("/data/wal/v2/000005.sst", 100)];

        Assert.That(StorageDamage.SelectFile(fresh, StorageDamage.LayerStore, StorageDamage.FileSst), Is.Null,
            "the WAL's SST must not stand in for the store's");
        Assert.That(StorageDamage.SelectFile(fresh, StorageDamage.LayerStore, StorageDamage.FileClosedLog), Is.Null,
            "the live log must not stand in for a closed one");
    }

    [Test]
    public void AListingIsParsed_AndAMalformedLineIsAnError()
    {
        IReadOnlyList<StorageFile> files = StorageDamage.ParseListing("120\t/data/kv/v2/a b.sst\n7\t/data/kv/v2/LOG\n");

        Assert.That(files, Is.EqualTo(new[] { new StorageFile("/data/kv/v2/a b.sst", 120), new StorageFile("/data/kv/v2/LOG", 7) }));
        Assert.That(() => StorageDamage.ParseListing("oops\t/data/x"), Throws.TypeOf<FormatException>());
        Assert.That(() => StorageDamage.ParseListing("/data/x"), Throws.TypeOf<FormatException>());
    }

    [Test]
    public void TheFactory_BuildsTheFault_AsHealable()
    {
        using HttpProbes probes = new();

        IFault fault = FaultFactory.Create(
            new NemesisEvent { Fault = "disk-corrupt", Damage = StorageDamage.BitFlip, DamageLayer = StorageDamage.LayerStore, DamageFile = StorageDamage.FileSst },
            probes);

        Assert.That(fault.Kind, Is.EqualTo("disk-corrupt"));
        Assert.That(fault.Healable, Is.True, "a heal restarts the node, and that is the moment the damage is judged");
    }

    [Test]
    public void AValidEvent_PassesValidation()
    {
        Assert.DoesNotThrow(() => new NemesisEvent
        {
            Fault = "disk-corrupt", Damage = StorageDamage.ChunkShuffle, DamageLayer = StorageDamage.LayerWal, DamageFile = StorageDamage.FileClosedLog, DamageCount = 13,
        }.Validate());
    }

    [Test]
    public void AnEventWithoutItsDamage_IsRefused()
    {
        Assert.That(() => new NemesisEvent { Fault = "disk-corrupt" }.Validate(), Throws.TypeOf<NemesisException>().With.Message.Contains("damage"));
    }

    [Test]
    public void AnUnknownDamageLayerOrFile_IsRefused()
    {
        Assert.That(() => new NemesisEvent { Fault = "disk-corrupt", Damage = "melt" }.Validate(),
            Throws.TypeOf<NemesisException>().With.Message.Contains("damage must be one of"));
        Assert.That(() => new NemesisEvent { Fault = "disk-corrupt", Damage = StorageDamage.BitFlip, DamageLayer = "cache" }.Validate(),
            Throws.TypeOf<NemesisException>().With.Message.Contains("damage_layer"));
        Assert.That(() => new NemesisEvent { Fault = "disk-corrupt", Damage = StorageDamage.BitFlip, DamageFile = "wal" }.Validate(),
            Throws.TypeOf<NemesisException>().With.Message.Contains("damage_file"));
    }

    [Test]
    public void TheDamageKeys_AreRefusedOnAnotherFault()
    {
        Assert.That(() => new NemesisEvent { Fault = "kill", Damage = StorageDamage.BitFlip }.Validate(),
            Throws.TypeOf<NemesisException>().With.Message.Contains("disk-corrupt"));
    }

    [Test]
    public void AChunkShuffleOfOneChunk_IsRefused()
    {
        Assert.That(() => new NemesisEvent { Fault = "disk-corrupt", Damage = StorageDamage.ChunkShuffle, DamageCount = 1 }.Validate(),
            Throws.TypeOf<NemesisException>().With.Message.Contains("damage_count"));
    }
}
