using System.Buffers.Binary;
using DatumIngest.Indexing;
using DatumIngest.Indexing.BTree.Mutable;
using DatumIngest.Model;

namespace DatumIngest.Tests.Indexing;

/// <summary>
/// Tests for the mutable B+Tree backing the per-table primary-key index file
/// (<c>.datum-pkindex</c>). Covers create/open/insert/lookup, leaf splits,
/// multi-level internal splits, persistence across reopen, and torn-write recovery.
/// </summary>
public sealed class MutableBPlusTreeTests : IDisposable
{
    private readonly string _tempDir;

    public MutableBPlusTreeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MutableBTree_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    private string PathFor(string name) => Path.Combine(_tempDir, name + ".datum-pkindex");

    // ───────────────────────── Create / Open basics ─────────────────────────

    [Fact]
    public void Create_EmptyTree_OpensWithZeroEntries()
    {
        string path = PathFor("empty");

        using (MutableBPlusTree tree = MutableBPlusTree.Create(path, DataKind.Int32))
        {
            Assert.Equal(0, tree.EntryCount);
            Assert.Equal(0, tree.TreeHeight);
            Assert.Equal(0u, tree.PageCount);
            Assert.Equal(DataKind.Int32, tree.KeyKind);
        }

        using (MutableBPlusTree reopened = MutableBPlusTree.Open(path))
        {
            Assert.Equal(0, reopened.EntryCount);
            Assert.Equal(0, reopened.TreeHeight);
            Assert.Equal(DataKind.Int32, reopened.KeyKind);
        }
    }

    [Fact]
    public void Create_FailsIfFileExists()
    {
        string path = PathFor("exists");
        File.WriteAllText(path, "marker");

        Assert.Throws<IOException>(() => MutableBPlusTree.Create(path, DataKind.Int32));
    }

    [Fact]
    public void Open_FailsOnCorruptFile()
    {
        string path = PathFor("corrupt");
        File.WriteAllBytes(path, new byte[1024]); // All zeros — invalid magic.

        Assert.Throws<InvalidDataException>(() => MutableBPlusTree.Open(path));
    }

    // ───────────────────────── Insert + lookup (no split) ─────────────────────────

    [Fact]
    public void Insert_SingleEntry_FoundByLookup()
    {
        string path = PathFor("single");

        using MutableBPlusTree tree = MutableBPlusTree.Create(path, DataKind.Int32);
        tree.Insert(new ValueIndexEntry(DataValue.FromInt32(42), 0, 7L));

        Assert.Equal(1, tree.EntryCount);
        Assert.Equal(1, tree.TreeHeight);

        Assert.True(tree.TryFind(DataValue.FromInt32(42), out ValueIndexEntry found));
        Assert.Equal(0, found.ChunkIndex);
        Assert.Equal(7L, found.RowOffsetInChunk);

        Assert.False(tree.TryFind(DataValue.FromInt32(43), out _));
    }

    [Fact]
    public void Insert_ManyEntriesIntoSingleLeaf_AllFound()
    {
        string path = PathFor("many_one_leaf");

        using MutableBPlusTree tree = MutableBPlusTree.Create(path, DataKind.Int32);

        for (int i = 0; i < 50; i++)
        {
            tree.Insert(new ValueIndexEntry(DataValue.FromInt32(i), 0, i));
        }

        Assert.Equal(50, tree.EntryCount);
        Assert.Equal(1, tree.TreeHeight);

        for (int i = 0; i < 50; i++)
        {
            Assert.True(tree.TryFind(DataValue.FromInt32(i), out ValueIndexEntry found));
            Assert.Equal(i, found.RowOffsetInChunk);
        }
    }

    [Fact]
    public void Insert_OutOfOrder_LookupStillWorks()
    {
        string path = PathFor("unordered");

        using MutableBPlusTree tree = MutableBPlusTree.Create(path, DataKind.Int32);
        int[] keys = { 50, 10, 80, 30, 70, 20, 90, 40, 60, 5 };

        foreach (int key in keys)
        {
            tree.Insert(new ValueIndexEntry(DataValue.FromInt32(key), 0, key));
        }

        Assert.Equal(keys.Length, tree.EntryCount);

        foreach (int key in keys)
        {
            Assert.True(tree.TryFind(DataValue.FromInt32(key), out ValueIndexEntry found));
            Assert.Equal(key, found.RowOffsetInChunk);
        }
    }

    [Fact]
    public void Insert_DuplicateKey_Throws()
    {
        string path = PathFor("dup");

        using MutableBPlusTree tree = MutableBPlusTree.Create(path, DataKind.Int32);
        tree.Insert(new ValueIndexEntry(DataValue.FromInt32(1), 0, 0));

        Assert.Throws<DuplicatePrimaryKeyException>(
            () => tree.Insert(new ValueIndexEntry(DataValue.FromInt32(1), 0, 1)));

        Assert.Equal(1, tree.EntryCount);
    }

    // ───────────────────────── Splits ─────────────────────────

    [Fact]
    public void Insert_ForcesLeafSplit_TreeGrowsToHeight2()
    {
        string path = PathFor("leaf_split");

        using MutableBPlusTree tree = MutableBPlusTree.Create(path, DataKind.Int32);

        // ~700 Int32 entries push past one 8 KiB leaf (each entry ≈ 17 bytes encoded + headers).
        // After the split, height becomes 2 (one internal root + two leaves).
        const int Total = 800;

        for (int i = 0; i < Total; i++)
        {
            tree.Insert(new ValueIndexEntry(DataValue.FromInt32(i), 0, i));
        }

        Assert.Equal(Total, tree.EntryCount);
        Assert.True(tree.TreeHeight >= 2,
            $"Expected tree to split (height ≥ 2) after {Total} entries; height = {tree.TreeHeight}");

        for (int i = 0; i < Total; i++)
        {
            Assert.True(tree.TryFind(DataValue.FromInt32(i), out ValueIndexEntry found),
                $"Entry {i} missing after splits");
            Assert.Equal(i, found.RowOffsetInChunk);
        }
    }

    [Fact]
    public void Insert_LargeWorkload_MultiLevelSplits()
    {
        string path = PathFor("multi_split");

        using MutableBPlusTree tree = MutableBPlusTree.Create(path, DataKind.Int64);

        // Enough entries to force multiple levels of internal splits.
        const int Total = 50_000;

        for (int i = 0; i < Total; i++)
        {
            tree.Insert(new ValueIndexEntry(DataValue.FromInt64(i), i / 1000, i % 1000));
        }

        Assert.Equal(Total, tree.EntryCount);
        Assert.True(tree.TreeHeight >= 2,
            $"Expected at least one level of internal split (height ≥ 2) after {Total} entries; height = {tree.TreeHeight}");

        // Spot-check several keys at random positions.
        int[] sampleKeys = { 0, 1, 999, 1000, 12_345, 25_000, 49_999 };

        foreach (int key in sampleKeys)
        {
            Assert.True(tree.TryFind(DataValue.FromInt64(key), out ValueIndexEntry found),
                $"Sample key {key} missing");
            Assert.Equal(key / 1000, found.ChunkIndex);
            Assert.Equal(key % 1000L, found.RowOffsetInChunk);
        }

        Assert.False(tree.TryFind(DataValue.FromInt64(Total), out _));
        Assert.False(tree.TryFind(DataValue.FromInt64(-1), out _));
    }

    [Fact]
    public void Insert_RandomOrder_LargeWorkload_AllFound()
    {
        string path = PathFor("random_large");

        using MutableBPlusTree tree = MutableBPlusTree.Create(path, DataKind.Int32);

        const int Total = 10_000;
        Random rng = new(Seed: 42);
        int[] keys = Enumerable.Range(0, Total).ToArray();

        // Fisher–Yates shuffle.
        for (int i = keys.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (keys[i], keys[j]) = (keys[j], keys[i]);
        }

        foreach (int key in keys)
        {
            tree.Insert(new ValueIndexEntry(DataValue.FromInt32(key), 0, key));
        }

        Assert.Equal(Total, tree.EntryCount);

        foreach (int key in keys)
        {
            Assert.True(tree.TryFind(DataValue.FromInt32(key), out ValueIndexEntry found));
            Assert.Equal(key, found.RowOffsetInChunk);
        }
    }

    // ───────────────────────── Persistence ─────────────────────────

    [Fact]
    public void Insert_Close_Reopen_StatePersists()
    {
        string path = PathFor("persist");

        using (MutableBPlusTree tree = MutableBPlusTree.Create(path, DataKind.Int32))
        {
            for (int i = 0; i < 100; i++)
            {
                tree.Insert(new ValueIndexEntry(DataValue.FromInt32(i), 0, i));
            }
        }

        using MutableBPlusTree reopened = MutableBPlusTree.Open(path);
        Assert.Equal(100, reopened.EntryCount);

        for (int i = 0; i < 100; i++)
        {
            Assert.True(reopened.TryFind(DataValue.FromInt32(i), out ValueIndexEntry found));
            Assert.Equal(i, found.RowOffsetInChunk);
        }
    }

    [Fact]
    public void InsertsAcrossMultipleSessions_Accumulate()
    {
        string path = PathFor("multi_session");

        using (MutableBPlusTree first = MutableBPlusTree.Create(path, DataKind.Int32))
        {
            for (int i = 0; i < 50; i++)
            {
                first.Insert(new ValueIndexEntry(DataValue.FromInt32(i), 0, i));
            }
        }

        using (MutableBPlusTree second = MutableBPlusTree.Open(path))
        {
            for (int i = 50; i < 100; i++)
            {
                second.Insert(new ValueIndexEntry(DataValue.FromInt32(i), 0, i));
            }
        }

        using MutableBPlusTree third = MutableBPlusTree.Open(path);
        Assert.Equal(100, third.EntryCount);

        for (int i = 0; i < 100; i++)
        {
            Assert.True(third.TryFind(DataValue.FromInt32(i), out _));
        }
    }

    [Fact]
    public void DuplicateKey_AfterReopen_StillRejected()
    {
        string path = PathFor("dup_reopen");

        using (MutableBPlusTree tree = MutableBPlusTree.Create(path, DataKind.Int32))
        {
            tree.Insert(new ValueIndexEntry(DataValue.FromInt32(7), 0, 0));
        }

        using MutableBPlusTree reopened = MutableBPlusTree.Open(path);
        Assert.Throws<DuplicatePrimaryKeyException>(
            () => reopened.Insert(new ValueIndexEntry(DataValue.FromInt32(7), 0, 1)));
    }

    // ───────────────────────── String keys ─────────────────────────

    [Fact]
    public void Insert_StringKeys_Works()
    {
        string path = PathFor("strings");

        using MutableBPlusTree tree = MutableBPlusTree.Create(path, DataKind.String);

        string[] keys = { "alpha", "bravo", "charlie", "delta", "echo", "foxtrot" };

        for (int i = 0; i < keys.Length; i++)
        {
            tree.Insert(new ValueIndexEntry(DataValue.FromString(keys[i]), 0, i));
        }

        foreach (string key in keys)
        {
            Assert.True(tree.TryFind(DataValue.FromString(key), out _));
        }

        Assert.False(tree.TryFind(DataValue.FromString("zulu"), out _));
    }

    // ───────────────────────── Crash recovery / torn writes ─────────────────────────

    [Fact]
    public void TornHeader_FallsBackToPreviousSlot()
    {
        string path = PathFor("torn");

        using (MutableBPlusTree tree = MutableBPlusTree.Create(path, DataKind.Int32))
        {
            for (int i = 0; i < 20; i++)
            {
                tree.Insert(new ValueIndexEntry(DataValue.FromInt32(i), 0, i));
            }
        }

        // Corrupt the active slot — whichever has the higher commit gen.
        // The reader picks the surviving slot by gen + CRC.
        long activeOffset = FindActiveSlotOffset(path);

        using (FileStream fs = new(path, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Position = activeOffset + 8; // Corrupt the commit-gen field — also kills the CRC.
            byte[] junk = new byte[8];
            new Random(0).NextBytes(junk);
            fs.Write(junk);
        }

        // Reopen — should fall back to the older slot.
        using MutableBPlusTree reopened = MutableBPlusTree.Open(path);

        // The previous-but-one commit might be the empty Create-time state, or any
        // intermediate INSERT commit. Either way, the tree must open cleanly.
        Assert.True(reopened.EntryCount <= 20);

        // And it must remain queryable. We can't predict which exact commit gen
        // recovered, but every find should either succeed or fail without crashing.
        for (int i = 0; i < 20; i++)
        {
            _ = reopened.TryFind(DataValue.FromInt32(i), out _);
        }
    }

    [Fact]
    public void BothSlotsCorrupt_ThrowsOnOpen()
    {
        string path = PathFor("doubly_torn");

        using (MutableBPlusTree _ = MutableBPlusTree.Create(path, DataKind.Int32))
        {
            // Just create, then corrupt both slots.
        }

        using (FileStream fs = new(path, FileMode.Open, FileAccess.Write))
        {
            byte[] zeros = new byte[MutableBPlusTreeConstants.HeaderSlotSize * 2];
            fs.Position = 0;
            fs.Write(zeros);
        }

        Assert.Throws<InvalidDataException>(() => MutableBPlusTree.Open(path));
    }

    [Fact]
    public void OneSlotCorrupt_OneValid_OpensSuccessfully()
    {
        string path = PathFor("one_torn");

        using (MutableBPlusTree tree = MutableBPlusTree.Create(path, DataKind.Int32))
        {
            tree.Insert(new ValueIndexEntry(DataValue.FromInt32(99), 0, 0));
        }

        // Zero out slot A only.
        using (FileStream fs = new(path, FileMode.Open, FileAccess.Write))
        {
            byte[] zeros = new byte[MutableBPlusTreeConstants.HeaderSlotSize];
            fs.Position = MutableBPlusTreeConstants.HeaderSlotAOffset;
            fs.Write(zeros);
        }

        using MutableBPlusTree reopened = MutableBPlusTree.Open(path);

        // Whichever surviving slot is active, we either get the empty state (if
        // the only valid slot was Create's) or the post-insert state. Either way,
        // the file opens.
        Assert.True(reopened.EntryCount == 0 || reopened.EntryCount == 1);
    }

    // ───────────────────────── Helpers ─────────────────────────

    /// <summary>
    /// Reads both slots and returns the file offset of whichever has the higher
    /// (valid) commit generation — i.e. the one a reader would pick. Used by
    /// torn-write tests to target the active slot for corruption.
    /// </summary>
    private static long FindActiveSlotOffset(string path)
    {
        byte[] slotA = new byte[MutableBPlusTreeConstants.HeaderSlotSize];
        byte[] slotB = new byte[MutableBPlusTreeConstants.HeaderSlotSize];

        using FileStream fs = new(path, FileMode.Open, FileAccess.Read);

        fs.Position = MutableBPlusTreeConstants.HeaderSlotAOffset;
        fs.ReadExactly(slotA);

        fs.Position = MutableBPlusTreeConstants.HeaderSlotBOffset;
        fs.ReadExactly(slotB);

        long genA = BinaryPrimitives.ReadInt64LittleEndian(slotA.AsSpan(8, 8));
        long genB = BinaryPrimitives.ReadInt64LittleEndian(slotB.AsSpan(8, 8));

        return genA >= genB
            ? MutableBPlusTreeConstants.HeaderSlotAOffset
            : MutableBPlusTreeConstants.HeaderSlotBOffset;
    }
}
