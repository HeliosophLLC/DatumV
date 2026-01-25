using DatumIngest.Indexing;
using DatumIngest.Indexing.BTree.Mutable;
using DatumIngest.Model;

namespace DatumIngest.Tests.Indexing;

/// <summary>
/// Shared correctness contract for any mutable B+Tree implementation:
/// create/open lifecycle, insert + lookup, leaf and internal splits,
/// persistence across reopen, duplicate-key rejection, and string keys.
/// </summary>
/// <remarks>
/// Concrete subclasses construct the implementation via <see cref="CreateTree"/>
/// and <see cref="OpenTree"/>. Each tree implementation should have a single
/// subclass running this entire contract; impl-specific file-format tests
/// (torn writes, corrupted slots) live in separate per-impl test classes.
/// </remarks>
public abstract class BPlusTreeContractTests : IDisposable
{
    private readonly string _tempDir;

    protected BPlusTreeContractTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), GetType().Name + "_" + Guid.NewGuid().ToString("N"));
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
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Creates a fresh tree at <paramref name="path"/> with the supplied key kind.
    /// Fails when the file already exists (mirrors <c>FileMode.CreateNew</c>).
    /// </summary>
    protected abstract IMutableBPlusTreeAdapter CreateTree(string path, DataKind keyKind, bool allowDuplicates = false);

    /// <summary>Opens an existing tree at <paramref name="path"/>.</summary>
    protected abstract IMutableBPlusTreeAdapter OpenTree(string path);

    /// <summary>File extension this implementation uses for its tree files.</summary>
    protected abstract string FileExtension { get; }

    private string PathFor(string name) => Path.Combine(_tempDir, name + FileExtension);

    // ───────────────────────── Create / Open basics ─────────────────────────

    [Fact]
    public void Create_EmptyTree_OpensWithZeroEntries()
    {
        string path = PathFor("empty");

        using (IMutableBPlusTreeAdapter tree = CreateTree(path, DataKind.Int32))
        {
            Assert.Equal(0, tree.EntryCount);
            Assert.Equal(0, tree.TreeHeight);
            Assert.Equal(0u, tree.PageCount);
        }

        using IMutableBPlusTreeAdapter reopened = OpenTree(path);
        Assert.Equal(0, reopened.EntryCount);
        Assert.Equal(0, reopened.TreeHeight);
    }

    [Fact]
    public void Create_FailsIfFileExists()
    {
        string path = PathFor("exists");
        File.WriteAllText(path, "marker");

        Assert.Throws<IOException>(() => CreateTree(path, DataKind.Int32));
    }

    // ───────────────────────── Insert + lookup (no split) ─────────────────────────

    [Fact]
    public void Insert_SingleEntry_FoundByLookup()
    {
        string path = PathFor("single");

        using IMutableBPlusTreeAdapter tree = CreateTree(path, DataKind.Int32);
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

        using IMutableBPlusTreeAdapter tree = CreateTree(path, DataKind.Int32);

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

        using IMutableBPlusTreeAdapter tree = CreateTree(path, DataKind.Int32);
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

        using IMutableBPlusTreeAdapter tree = CreateTree(path, DataKind.Int32);
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

        using IMutableBPlusTreeAdapter tree = CreateTree(path, DataKind.Int32);

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

        using IMutableBPlusTreeAdapter tree = CreateTree(path, DataKind.Int64);

        // Enough entries to force at least one level of internal split (height ≥ 2).
        const int Total = 8_000;

        for (int i = 0; i < Total; i++)
        {
            tree.Insert(new ValueIndexEntry(DataValue.FromInt64(i), i / 1000, i % 1000));
        }

        Assert.Equal(Total, tree.EntryCount);
        Assert.True(tree.TreeHeight >= 2,
            $"Expected at least one level of internal split (height ≥ 2) after {Total} entries; height = {tree.TreeHeight}");

        // Spot-check several keys at random positions.
        int[] sampleKeys = { 0, 1, 999, 1000, 3_456, 5_000, 7_999 };

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

        using IMutableBPlusTreeAdapter tree = CreateTree(path, DataKind.Int32);

        const int Total = 3_000;
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

        using (IMutableBPlusTreeAdapter tree = CreateTree(path, DataKind.Int32))
        {
            for (int i = 0; i < 100; i++)
            {
                tree.Insert(new ValueIndexEntry(DataValue.FromInt32(i), 0, i));
            }
        }

        using IMutableBPlusTreeAdapter reopened = OpenTree(path);
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

        using (IMutableBPlusTreeAdapter first = CreateTree(path, DataKind.Int32))
        {
            for (int i = 0; i < 50; i++)
            {
                first.Insert(new ValueIndexEntry(DataValue.FromInt32(i), 0, i));
            }
        }

        using (IMutableBPlusTreeAdapter second = OpenTree(path))
        {
            for (int i = 50; i < 100; i++)
            {
                second.Insert(new ValueIndexEntry(DataValue.FromInt32(i), 0, i));
            }
        }

        using IMutableBPlusTreeAdapter third = OpenTree(path);
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

        using (IMutableBPlusTreeAdapter tree = CreateTree(path, DataKind.Int32))
        {
            tree.Insert(new ValueIndexEntry(DataValue.FromInt32(7), 0, 0));
        }

        using IMutableBPlusTreeAdapter reopened = OpenTree(path);
        Assert.Throws<DuplicatePrimaryKeyException>(
            () => reopened.Insert(new ValueIndexEntry(DataValue.FromInt32(7), 0, 1)));
    }

    /// <summary>
    /// Tight create → insert → dispose → reopen cycle. Catches accidental
    /// file-handle leaks in lifecycle code that wraps the tree.
    /// </summary>
    [Fact]
    public void Create_Insert_Dispose_Reopen_Works()
    {
        string path = PathFor("reopen_sanity");

        using (IMutableBPlusTreeAdapter tree = CreateTree(path, DataKind.Int32))
        {
            tree.Insert(new ValueIndexEntry(DataValue.FromInt32(1), 0, 0));
            tree.Insert(new ValueIndexEntry(DataValue.FromInt32(2), 0, 0));
        } // tree disposed — file should be released

        using IMutableBPlusTreeAdapter reopened = OpenTree(path);
        Assert.Equal(2, reopened.EntryCount);
    }

    // ───────────────────────── String keys ─────────────────────────

    [Fact]
    public void Insert_StringKeys_Works()
    {
        string path = PathFor("strings");

        using IMutableBPlusTreeAdapter tree = CreateTree(path, DataKind.String);

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

    // ───────────────────────── FindAll / FindRange (no duplicates) ─────────────────────────

    [Fact]
    public void FindAll_ExistingKey_ReturnsSingleEntry()
    {
        string path = PathFor("findall_single");
        using IMutableBPlusTreeAdapter tree = CreateTree(path, DataKind.Int32);

        for (int i = 0; i < 10; i++)
        {
            tree.Insert(new ValueIndexEntry(DataValue.FromInt32(i), ChunkIndex: i, RowOffsetInChunk: i * 7));
        }

        IReadOnlyList<ValueIndexEntry> hits = tree.FindAll(DataValue.FromInt32(5));
        Assert.Single(hits);
        Assert.Equal(5, hits[0].ChunkIndex);
        Assert.Equal(35L, hits[0].RowOffsetInChunk);
    }

    [Fact]
    public void FindAll_MissingKey_ReturnsEmpty()
    {
        string path = PathFor("findall_miss");
        using IMutableBPlusTreeAdapter tree = CreateTree(path, DataKind.Int32);

        for (int i = 0; i < 10; i++)
        {
            tree.Insert(new ValueIndexEntry(DataValue.FromInt32(i), 0, i));
        }

        Assert.Empty(tree.FindAll(DataValue.FromInt32(100)));
    }

    [Fact]
    public void FindRange_InclusiveBounds_ReturnsEntriesInOrder()
    {
        string path = PathFor("range_inclusive");
        using IMutableBPlusTreeAdapter tree = CreateTree(path, DataKind.Int32);

        for (int i = 0; i < 100; i++)
        {
            // RowOffsetInChunk = i lets us verify ordering without comparing keys.
            tree.Insert(new ValueIndexEntry(DataValue.FromInt32(i), 0, i));
        }

        IReadOnlyList<ValueIndexEntry> hits = tree.FindRange(DataValue.FromInt32(20), DataValue.FromInt32(29));
        long[] rows = hits.Select(h => h.RowOffsetInChunk).ToArray();

        Assert.Equal(new long[] { 20, 21, 22, 23, 24, 25, 26, 27, 28, 29 }, rows);
    }

    [Fact]
    public void FindRange_AcrossLeafSplits_ReturnsAllEntries()
    {
        string path = PathFor("range_split");
        using IMutableBPlusTreeAdapter tree = CreateTree(path, DataKind.Int32);

        // Enough rows to force splits; verifies parent-page navigation covers
        // entries that span more than one leaf.
        const int Total = 2000;
        for (int i = 0; i < Total; i++)
        {
            tree.Insert(new ValueIndexEntry(DataValue.FromInt32(i), 0, i));
        }
        Assert.True(tree.TreeHeight >= 2);

        IReadOnlyList<ValueIndexEntry> hits = tree.FindRange(DataValue.FromInt32(500), DataValue.FromInt32(1500));
        long[] rows = hits.Select(h => h.RowOffsetInChunk).ToArray();

        Assert.Equal(1001, rows.Length);
        for (int i = 0; i < rows.Length; i++)
        {
            Assert.Equal(500L + i, rows[i]);
        }
    }

    [Fact]
    public void FindRange_OutsideTreeBounds_ReturnsEmpty()
    {
        string path = PathFor("range_outside");
        using IMutableBPlusTreeAdapter tree = CreateTree(path, DataKind.Int32);

        for (int i = 0; i < 20; i++)
        {
            tree.Insert(new ValueIndexEntry(DataValue.FromInt32(i), 0, i));
        }

        Assert.Empty(tree.FindRange(DataValue.FromInt32(100), DataValue.FromInt32(200)));
        Assert.Empty(tree.FindRange(DataValue.FromInt32(-100), DataValue.FromInt32(-1)));
    }

    [Fact]
    public void FindRange_EmptyTree_ReturnsEmpty()
    {
        string path = PathFor("range_empty");
        using IMutableBPlusTreeAdapter tree = CreateTree(path, DataKind.Int32);

        Assert.Empty(tree.FindRange(DataValue.FromInt32(0), DataValue.FromInt32(100)));
    }

    // ───────────────────────── Duplicate keys (acceleration mode) ─────────────────────────

    [Fact]
    public void Insert_Duplicates_WhenAllowed_AllPersisted()
    {
        string path = PathFor("dups_allowed");
        using IMutableBPlusTreeAdapter tree = CreateTree(path, DataKind.Int32, allowDuplicates: true);

        // Three entries with the same key but different (chunk, row) tuples.
        tree.Insert(new ValueIndexEntry(DataValue.FromInt32(42), ChunkIndex: 0, RowOffsetInChunk: 100));
        tree.Insert(new ValueIndexEntry(DataValue.FromInt32(42), ChunkIndex: 1, RowOffsetInChunk: 200));
        tree.Insert(new ValueIndexEntry(DataValue.FromInt32(42), ChunkIndex: 2, RowOffsetInChunk: 300));

        Assert.Equal(3, tree.EntryCount);
        Assert.True(tree.AllowDuplicates);
    }

    [Fact]
    public void FindAll_WithDuplicates_ReturnsAllMatches()
    {
        string path = PathFor("findall_dups");
        using IMutableBPlusTreeAdapter tree = CreateTree(path, DataKind.Int32, allowDuplicates: true);

        tree.Insert(new ValueIndexEntry(DataValue.FromInt32(1), 0, 10));
        tree.Insert(new ValueIndexEntry(DataValue.FromInt32(2), 0, 20));
        tree.Insert(new ValueIndexEntry(DataValue.FromInt32(2), 0, 21));
        tree.Insert(new ValueIndexEntry(DataValue.FromInt32(2), 0, 22));
        tree.Insert(new ValueIndexEntry(DataValue.FromInt32(3), 0, 30));

        IReadOnlyList<ValueIndexEntry> hits = tree.FindAll(DataValue.FromInt32(2));
        long[] rows = hits.Select(h => h.RowOffsetInChunk).OrderBy(r => r).ToArray();
        Assert.Equal(new long[] { 20, 21, 22 }, rows);
    }

    [Fact]
    public void FindAll_WithDuplicatesAcrossLeafSplit_ReturnsAllMatches()
    {
        string path = PathFor("findall_dups_split");
        using IMutableBPlusTreeAdapter tree = CreateTree(path, DataKind.Int32, allowDuplicates: true);

        // Force a leaf split, then pile many duplicates on a single key so the
        // duplicates straddle the split boundary.
        for (int i = 0; i < 800; i++)
        {
            tree.Insert(new ValueIndexEntry(DataValue.FromInt32(i), 0, i));
        }
        for (int rep = 0; rep < 100; rep++)
        {
            tree.Insert(new ValueIndexEntry(DataValue.FromInt32(500), 1, rep));
        }

        Assert.True(tree.TreeHeight >= 2);

        IReadOnlyList<ValueIndexEntry> hits = tree.FindAll(DataValue.FromInt32(500));
        Assert.Equal(101, hits.Count); // 1 original + 100 duplicates
    }

    // ───────────────────────── Traversal ─────────────────────────

    [Fact]
    public void TraverseForward_EmptyTree_YieldsNothing()
    {
        string path = PathFor("trav_empty_fwd");
        using IMutableBPlusTreeAdapter tree = CreateTree(path, DataKind.Int32);

        Assert.Empty(tree.TraverseForward());
    }

    [Fact]
    public void TraverseForward_AfterInserts_YieldsInAscendingOrder()
    {
        string path = PathFor("trav_fwd");
        using IMutableBPlusTreeAdapter tree = CreateTree(path, DataKind.Int32);

        int[] insertOrder = { 50, 10, 80, 30, 70, 20, 90, 40, 60, 5 };
        foreach (int k in insertOrder)
        {
            tree.Insert(new ValueIndexEntry(DataValue.FromInt32(k), 0, k));
        }

        long[] rowsInOrder = tree.TraverseForward().Select(e => e.RowOffsetInChunk).ToArray();
        Assert.Equal(new long[] { 5, 10, 20, 30, 40, 50, 60, 70, 80, 90 }, rowsInOrder);
    }

    [Fact]
    public void TraverseForward_AcrossSplits_YieldsAllInOrder()
    {
        string path = PathFor("trav_fwd_split");
        using IMutableBPlusTreeAdapter tree = CreateTree(path, DataKind.Int32);

        const int Total = 1_500;
        for (int i = 0; i < Total; i++)
        {
            tree.Insert(new ValueIndexEntry(DataValue.FromInt32(i), 0, i));
        }
        Assert.True(tree.TreeHeight >= 2);

        long[] rows = tree.TraverseForward().Select(e => e.RowOffsetInChunk).ToArray();
        Assert.Equal(Total, rows.Length);
        for (int i = 0; i < Total; i++)
        {
            Assert.Equal((long)i, rows[i]);
        }
    }

    [Fact]
    public void TraverseBackward_AfterInserts_YieldsInDescendingOrder()
    {
        string path = PathFor("trav_back");
        using IMutableBPlusTreeAdapter tree = CreateTree(path, DataKind.Int32);

        for (int i = 0; i < 50; i++)
        {
            tree.Insert(new ValueIndexEntry(DataValue.FromInt32(i), 0, i));
        }

        long[] rows = tree.TraverseBackward().Select(e => e.RowOffsetInChunk).ToArray();
        Assert.Equal(50, rows.Length);
        for (int i = 0; i < 50; i++)
        {
            Assert.Equal(49L - i, rows[i]);
        }
    }

    // ───────────────────────── Delete ─────────────────────────

    [Fact]
    public void Delete_ExistingEntry_RemovedAndCountDecremented()
    {
        string path = PathFor("delete_one");
        using IMutableBPlusTreeAdapter tree = CreateTree(path, DataKind.Int32);

        for (int i = 0; i < 10; i++)
        {
            tree.Insert(new ValueIndexEntry(DataValue.FromInt32(i), 0, i));
        }

        bool removed = tree.Delete(new ValueIndexEntry(DataValue.FromInt32(5), 0, 5));
        Assert.True(removed);
        Assert.Equal(9, tree.EntryCount);
        Assert.False(tree.TryFind(DataValue.FromInt32(5), out _));
        // Neighbors still present.
        Assert.True(tree.TryFind(DataValue.FromInt32(4), out _));
        Assert.True(tree.TryFind(DataValue.FromInt32(6), out _));
    }

    [Fact]
    public void Delete_NonExistentKey_ReturnsFalse()
    {
        string path = PathFor("delete_missing");
        using IMutableBPlusTreeAdapter tree = CreateTree(path, DataKind.Int32);

        for (int i = 0; i < 5; i++)
        {
            tree.Insert(new ValueIndexEntry(DataValue.FromInt32(i), 0, i));
        }

        bool removed = tree.Delete(new ValueIndexEntry(DataValue.FromInt32(999), 0, 0));
        Assert.False(removed);
        Assert.Equal(5, tree.EntryCount);
    }

    [Fact]
    public void Delete_AllEntries_TreeBecomesEmpty()
    {
        string path = PathFor("delete_all");
        using IMutableBPlusTreeAdapter tree = CreateTree(path, DataKind.Int32);

        for (int i = 0; i < 5; i++)
        {
            tree.Insert(new ValueIndexEntry(DataValue.FromInt32(i), 0, i));
        }

        for (int i = 0; i < 5; i++)
        {
            Assert.True(tree.Delete(new ValueIndexEntry(DataValue.FromInt32(i), 0, i)));
        }

        Assert.Equal(0, tree.EntryCount);
        Assert.Equal(0, tree.TreeHeight);
        Assert.Empty(tree.TraverseForward());
    }

    [Fact]
    public void Delete_OneOfDuplicates_OthersPreserved()
    {
        string path = PathFor("delete_dup");
        using IMutableBPlusTreeAdapter tree = CreateTree(path, DataKind.Int32, allowDuplicates: true);

        tree.Insert(new ValueIndexEntry(DataValue.FromInt32(7), 0, 100));
        tree.Insert(new ValueIndexEntry(DataValue.FromInt32(7), 0, 200));
        tree.Insert(new ValueIndexEntry(DataValue.FromInt32(7), 0, 300));

        bool removed = tree.Delete(new ValueIndexEntry(DataValue.FromInt32(7), 0, 200));
        Assert.True(removed);
        Assert.Equal(2, tree.EntryCount);

        long[] remaining = tree.FindAll(DataValue.FromInt32(7))
            .Select(h => h.RowOffsetInChunk).OrderBy(r => r).ToArray();
        Assert.Equal(new long[] { 100, 300 }, remaining);
    }

    [Fact]
    public void Delete_PersistsAcrossReopen()
    {
        string path = PathFor("delete_persist");

        using (IMutableBPlusTreeAdapter tree = CreateTree(path, DataKind.Int32))
        {
            for (int i = 0; i < 10; i++)
            {
                tree.Insert(new ValueIndexEntry(DataValue.FromInt32(i), 0, i));
            }
            Assert.True(tree.Delete(new ValueIndexEntry(DataValue.FromInt32(3), 0, 3)));
            Assert.True(tree.Delete(new ValueIndexEntry(DataValue.FromInt32(7), 0, 7)));
        }

        using IMutableBPlusTreeAdapter reopened = OpenTree(path);
        Assert.Equal(8, reopened.EntryCount);
        Assert.False(reopened.TryFind(DataValue.FromInt32(3), out _));
        Assert.False(reopened.TryFind(DataValue.FromInt32(7), out _));
        Assert.True(reopened.TryFind(DataValue.FromInt32(5), out _));
    }
}
