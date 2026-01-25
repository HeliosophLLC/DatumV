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
    protected abstract IMutableBPlusTreeAdapter CreateTree(string path, DataKind keyKind);

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

        using IMutableBPlusTreeAdapter tree = CreateTree(path, DataKind.Int32);

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
}
