using System.Text;
using DatumIngest.Indexing;
using DatumIngest.Indexing.BTree;
using DatumIngest.Model;

namespace DatumIngest.Tests.Indexing;

/// <summary>
/// Tests for <see cref="BPlusTreeBulkLoader"/> — tree construction from sorted
/// entries, section header serialization, and structural correctness of the
/// resulting B+Tree.
/// </summary>
public sealed class BPlusTreeBulkLoaderTests : ServiceTestBase
{
    // ───────────────────────── Empty input ─────────────────────────

    [Fact]
    public void Build_EmptyInput_ReturnsNull()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);

        BPlusTreeSectionHeader? result = BPlusTreeBulkLoader.Build(
            Array.Empty<ValueIndexEntry>(),
            "empty_column",
            DataKind.Float32,
            writer);

        Assert.Null(result);
    }

    // ───────────────────────── Single entry (leaf-only tree) ─────────────────────────

    [Fact]
    public void Build_SingleEntry_ProducesLeafOnlyTree()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromFloat32(42.0f), 0, 0L),
        ];

        BPlusTreeSectionHeader header = BuildAndGetHeader(entries, "single", DataKind.Float32);

        Assert.Equal("single", header.ColumnName);
        Assert.Equal(DataKind.Float32, header.KeyKind);
        Assert.Equal(1L, header.EntryCount);
        Assert.Equal(1, header.TreeHeight);
        Assert.Equal(1u, header.PageCount);
        Assert.Equal((ushort)BPlusTreeConstants.PageSize, header.PageSize);
    }

    [Fact]
    public void Build_SingleEntry_FindExactReturnsIt()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromFloat32(42.0f), 0, 100L),
        ];

        BPlusTreeReader reader = BuildAndCreateReader(entries, "col", DataKind.Float32);

        IReadOnlyList<ValueIndexEntry> found = reader.FindExact(DataValue.FromFloat32(42.0f));
        Assert.Single(found);
        Assert.Equal(0, found[0].ChunkIndex);
        Assert.Equal(100L, found[0].RowOffsetInChunk);
    }

    // ───────────────────────── Multiple entries in single leaf ─────────────────────────

    [Fact]
    public void Build_FewEntries_AllRetrievableByExactLookup()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromFloat32(1.0f), 0, 0L),
            new(DataValue.FromFloat32(2.0f), 0, 1L),
            new(DataValue.FromFloat32(3.0f), 0, 2L),
            new(DataValue.FromFloat32(4.0f), 1, 0L),
            new(DataValue.FromFloat32(5.0f), 1, 1L),
        ];

        BPlusTreeReader reader = BuildAndCreateReader(entries, "x", DataKind.Float32);

        for (int index = 0; index < entries.Length; index++)
        {
            IReadOnlyList<ValueIndexEntry> found = reader.FindExact(entries[index].Key);
            Assert.Single(found);
            Assert.Equal(entries[index].ChunkIndex, found[0].ChunkIndex);
            Assert.Equal(entries[index].RowOffsetInChunk, found[0].RowOffsetInChunk);
        }
    }

    // ───────────────────────── Multi-leaf tree (forces internal nodes) ─────────────────────────

    [Fact]
    public void Build_ManyEntries_CreatesMultipleLeaves()
    {
        // Generate enough entries to span multiple leaf pages.
        int entryCount = 2000;
        ValueIndexEntry[] entries = GenerateScalarEntries(entryCount);

        BPlusTreeSectionHeader header = BuildAndGetHeader(entries, "big", DataKind.Float32);

        Assert.Equal(entryCount, header.EntryCount);
        Assert.True(header.PageCount > 1, "Should have more than one page.");
        Assert.True(header.TreeHeight >= 2, "Should have at least height 2 with internal nodes.");
    }

    [Fact]
    public void Build_ManyEntries_AllRetrievableByExactLookup()
    {
        int entryCount = 2000;
        ValueIndexEntry[] entries = GenerateScalarEntries(entryCount);

        BPlusTreeReader reader = BuildAndCreateReader(entries, "big", DataKind.Float32);

        // Spot-check entries at various positions (first, middle, last).
        int[] sampleIndexes = [0, 1, entryCount / 4, entryCount / 2, 3 * entryCount / 4, entryCount - 2, entryCount - 1];

        foreach (int sampleIndex in sampleIndexes)
        {
            IReadOnlyList<ValueIndexEntry> found = reader.FindExact(entries[sampleIndex].Key);
            Assert.True(found.Count > 0, $"Entry at index {sampleIndex} should be found.");
            Assert.Contains(found, entry =>
                entry.ChunkIndex == entries[sampleIndex].ChunkIndex &&
                entry.RowOffsetInChunk == entries[sampleIndex].RowOffsetInChunk);
        }
    }

    [Fact]
    public void Build_ManyEntries_RangeQueryReturnsCorrectSubset()
    {
        int entryCount = 2000;
        ValueIndexEntry[] entries = GenerateScalarEntries(entryCount);

        BPlusTreeReader reader = BuildAndCreateReader(entries, "range", DataKind.Float32);

        DataValue low = DataValue.FromFloat32(100.0f);
        DataValue high = DataValue.FromFloat32(200.0f);

        IReadOnlyList<ValueIndexEntry> rangeResult = reader.FindRange(low, high);

        // All returned entries should have keys in [100, 200].
        foreach (ValueIndexEntry entry in rangeResult)
        {
            float keyValue = entry.Key.AsFloat32();
            Assert.True(keyValue >= 100.0f && keyValue <= 200.0f,
                $"Key {keyValue} should be in range [100, 200].");
        }

        // Count expected entries.
        int expectedCount = entries.Count(entry =>
            entry.Key.AsFloat32() >= 100.0f && entry.Key.AsFloat32() <= 200.0f);
        Assert.Equal(expectedCount, rangeResult.Count);
    }

    // ───────────────────────── Duplicate keys ─────────────────────────

    [Fact]
    public void Build_DuplicateKeys_AllPreserved()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromFloat32(5.0f), 0, 0L),
            new(DataValue.FromFloat32(5.0f), 0, 10L),
            new(DataValue.FromFloat32(5.0f), 1, 0L),
            new(DataValue.FromFloat32(10.0f), 1, 10L),
            new(DataValue.FromFloat32(10.0f), 2, 0L),
        ];

        BPlusTreeReader reader = BuildAndCreateReader(entries, "dup", DataKind.Float32);

        IReadOnlyList<ValueIndexEntry> fives = reader.FindExact(DataValue.FromFloat32(5.0f));
        Assert.Equal(3, fives.Count);

        IReadOnlyList<ValueIndexEntry> tens = reader.FindExact(DataValue.FromFloat32(10.0f));
        Assert.Equal(2, tens.Count);
    }

    // ───────────────────────── String keys ─────────────────────────

    [Fact]
    public void Build_StringKeys_FindExactWorks()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromString("alpha"), 0, 0L),
            new(DataValue.FromString("beta"), 0, 1L),
            new(DataValue.FromString("gamma"), 1, 0L),
            new(DataValue.FromString("zeta"), 1, 1L),
        ];

        BPlusTreeReader reader = BuildAndCreateReader(entries, "name", DataKind.String);

        IReadOnlyList<ValueIndexEntry> result = reader.FindExact(DataValue.FromString("beta"));
        Assert.Single(result);
        Assert.Equal(0, result[0].ChunkIndex);
        Assert.Equal(1L, result[0].RowOffsetInChunk);

        IReadOnlyList<ValueIndexEntry> missing = reader.FindExact(DataValue.FromString("delta"));
        Assert.Empty(missing);
    }

    // ───────────────────────── Traversal ─────────────────────────

    [Fact]
    public void Build_ForwardTraversal_ReturnsAscendingOrder()
    {
        int entryCount = 500;
        ValueIndexEntry[] entries = GenerateScalarEntries(entryCount);

        BPlusTreeReader reader = BuildAndCreateReader(entries, "traverse", DataKind.Float32);

        List<ValueIndexEntry> traversed = reader.TraverseForward().ToList();
        Assert.Equal(entryCount, traversed.Count);

        for (int index = 1; index < traversed.Count; index++)
        {
            Assert.True(
                traversed[index].Key.AsFloat32() >= traversed[index - 1].Key.AsFloat32(),
                $"Entry {index} should be >= entry {index - 1} in forward traversal.");
        }
    }

    [Fact]
    public void Build_BackwardTraversal_ReturnsDescendingOrder()
    {
        int entryCount = 500;
        ValueIndexEntry[] entries = GenerateScalarEntries(entryCount);

        BPlusTreeReader reader = BuildAndCreateReader(entries, "traverse", DataKind.Float32);

        List<ValueIndexEntry> traversed = reader.TraverseBackward().ToList();
        Assert.Equal(entryCount, traversed.Count);

        for (int index = 1; index < traversed.Count; index++)
        {
            Assert.True(
                traversed[index].Key.AsFloat32() <= traversed[index - 1].Key.AsFloat32(),
                $"Entry {index} should be <= entry {index - 1} in backward traversal.");
        }
    }

    // ───────────────────────── Section header round-trip ─────────────────────────

    [Fact]
    public void SectionHeader_WriteRead_RoundTrips()
    {
        BPlusTreeSectionHeader original = new(
            "test_column", DataKind.String, 42, 1_000_000, 3, 8192, 500);

        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
        BPlusTreeBulkLoader.WriteSectionHeader(writer, original);

        stream.Position = 0;
        using BinaryReader binaryReader = new(stream, Encoding.UTF8, leaveOpen: true);
        BPlusTreeSectionHeader roundTripped = BPlusTreeBulkLoader.ReadSectionHeader(binaryReader);

        Assert.Equal(original.ColumnName, roundTripped.ColumnName);
        Assert.Equal(original.KeyKind, roundTripped.KeyKind);
        Assert.Equal(original.RootPageIndex, roundTripped.RootPageIndex);
        Assert.Equal(original.EntryCount, roundTripped.EntryCount);
        Assert.Equal(original.TreeHeight, roundTripped.TreeHeight);
        Assert.Equal(original.PageSize, roundTripped.PageSize);
        Assert.Equal(original.PageCount, roundTripped.PageCount);
    }

    // ───────────────────────── Leaf chain integrity ─────────────────────────

    [Fact]
    public void Build_LeafChain_FirstLeafHasNoPrevious()
    {
        int entryCount = 1000;
        ValueIndexEntry[] entries = GenerateScalarEntries(entryCount);

        (BPlusTreeSectionHeader header, byte[][] pages) = BuildAndGetPages(entries, "chain", DataKind.Float32);

        // Find the first leaf page (leftmost child from root).
        uint firstLeafIndex = FindFirstLeafIndex(header, pages);
        BPlusTreeLeafPage firstLeaf = BPlusTreePageCodec.DecodeLeafPage(pages[firstLeafIndex], firstLeafIndex);

        Assert.Equal(BPlusTreeConstants.NoLinkedPage, firstLeaf.PreviousLeafPageIndex);
    }

    [Fact]
    public void Build_LeafChain_LastLeafHasNoNext()
    {
        int entryCount = 1000;
        ValueIndexEntry[] entries = GenerateScalarEntries(entryCount);

        (BPlusTreeSectionHeader header, byte[][] pages) = BuildAndGetPages(entries, "chain", DataKind.Float32);

        // Find the last leaf page (rightmost child from root).
        uint lastLeafIndex = FindLastLeafIndex(header, pages);
        BPlusTreeLeafPage lastLeaf = BPlusTreePageCodec.DecodeLeafPage(pages[lastLeafIndex], lastLeafIndex);

        Assert.Equal(BPlusTreeConstants.NoLinkedPage, lastLeaf.NextLeafPageIndex);
    }

    [Fact]
    public void Build_LeafChain_TotalEntriesMatchHeader()
    {
        int entryCount = 1500;
        ValueIndexEntry[] entries = GenerateScalarEntries(entryCount);

        (BPlusTreeSectionHeader header, byte[][] pages) = BuildAndGetPages(entries, "count", DataKind.Float32);

        // Walk the leaf chain and count entries.
        long totalEntries = 0;
        uint leafIndex = FindFirstLeafIndex(header, pages);

        while (leafIndex != BPlusTreeConstants.NoLinkedPage)
        {
            BPlusTreeLeafPage leaf = BPlusTreePageCodec.DecodeLeafPage(pages[leafIndex], leafIndex);
            totalEntries += leaf.EntryCount;
            leafIndex = leaf.NextLeafPageIndex;
        }

        Assert.Equal(header.EntryCount, totalEntries);
        Assert.Equal(entryCount, totalEntries);
    }

    // ───────────────────────── Large tree (3+ levels) ─────────────────────────

    [Fact]
    public void Build_LargeTree_AllEntriesAccessible()
    {
        // With ~500 entries per leaf and thousands of entries, we should get
        // multiple internal levels.
        int entryCount = 50_000;
        ValueIndexEntry[] entries = GenerateScalarEntries(entryCount);

        BPlusTreeSectionHeader header = BuildAndGetHeader(entries, "large", DataKind.Float32);

        Assert.Equal(entryCount, header.EntryCount);
        Assert.True(header.PageCount > 10, "Large tree should have many pages.");

        // Verify the reader can traverse all entries.
        BPlusTreeReader reader = BuildAndCreateReader(entries, "large", DataKind.Float32);
        long traversedCount = reader.TraverseForward().LongCount();
        Assert.Equal(entryCount, traversedCount);
    }

    // ───────────────────────── Long string keys (overflow regression) ─────────────────────────

    /// <summary>
    /// Regression: when string keys are long enough that internal-page separator keys
    /// exceed the 8 KiB page capacity, <c>FindMaxInternalKeys</c> must correctly fall
    /// back to fewer keys per page. Previously the probing threw
    /// <see cref="NotSupportedException"/> from a non-expandable <see cref="MemoryStream"/>,
    /// which was not caught by the <see cref="InvalidOperationException"/> handler.
    /// </summary>
    [Fact]
    public void Build_LongStringKeys_ProducesValidTree()
    {
        using Arena arena = new();
        arena.AddReference();

        // Use strings ~500 chars each — enough that only a handful of separator keys
        // fit per 8 KiB internal page, forcing the probing loop.
        int entryCount = 500;
        ValueIndexEntry[] entries = new ValueIndexEntry[entryCount];
        int chunkSize = 100;

        for (int index = 0; index < entryCount; index++)
        {
            string key = new string((char)('A' + (index % 26)), 500) + index.ToString("D5");
            int chunkIndex = index / chunkSize;
            long rowOffset = index % chunkSize;
            entries[index] = new ValueIndexEntry(
                DataValue.FromString(key, arena), chunkIndex, rowOffset);
        }

        Array.Sort(entries, (left, right) =>
            string.Compare(left.Key.AsString(), right.Key.AsString(), StringComparison.Ordinal));

        BPlusTreeSectionHeader header = BuildAndGetHeader(entries, "long_strings", DataKind.String);

        Assert.Equal(entryCount, header.EntryCount);
        Assert.True(header.TreeHeight >= 1, "Tree should have at least one level.");

        BPlusTreeReader reader = BuildAndCreateReader(entries, "long_strings", DataKind.String);

        // Verify every entry is retrievable.
        for (int index = 0; index < entryCount; index++)
        {
            IReadOnlyList<ValueIndexEntry> found = reader.FindExact(entries[index].Key);
            Assert.True(found.Count >= 1, $"Entry {index} not found by exact lookup.");
        }
    }

    // ───────────────────────── Helpers ─────────────────────────

    /// <summary>
    /// Generates sorted scalar entries with unique keys (0.0f, 1.0f, 2.0f, ...).
    /// </summary>
    private static ValueIndexEntry[] GenerateScalarEntries(int count)
    {
        ValueIndexEntry[] entries = new ValueIndexEntry[count];
        int chunkSize = 1000;

        for (int index = 0; index < count; index++)
        {
            int chunkIndex = index / chunkSize;
            long rowOffset = index % chunkSize;
            entries[index] = new ValueIndexEntry(
                DataValue.FromFloat32((float)index), chunkIndex, rowOffset);
        }

        return entries;
    }

    /// <summary>
    /// Builds a B+Tree and returns the section header.
    /// </summary>
    private static BPlusTreeSectionHeader BuildAndGetHeader(
        ValueIndexEntry[] entries, string columnName, DataKind keyKind)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);

        BPlusTreeSectionHeader? header = BPlusTreeBulkLoader.Build(
            entries, columnName, keyKind, writer);

        Assert.NotNull(header);
        return header.Value;
    }

    /// <summary>
    /// Builds a B+Tree and creates a reader from the written pages.
    /// </summary>
    private static BPlusTreeReader BuildAndCreateReader(
        ValueIndexEntry[] entries, string columnName, DataKind keyKind)
    {
        (BPlusTreeSectionHeader header, byte[][] pages) = BuildAndGetPages(entries, columnName, keyKind);
        return new BPlusTreeReader(header, pages);
    }

    /// <summary>
    /// Builds a B+Tree and returns the header plus raw pages.
    /// </summary>
    private static (BPlusTreeSectionHeader Header, byte[][] Pages) BuildAndGetPages(
        ValueIndexEntry[] entries, string columnName, DataKind keyKind)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);

        BPlusTreeSectionHeader? header = BPlusTreeBulkLoader.Build(
            entries, columnName, keyKind, writer);

        Assert.NotNull(header);

        // Read back the section header to find its byte length, then extract pages.
        stream.Position = 0;
        using BinaryReader binaryReader = new(stream, Encoding.UTF8, leaveOpen: true);
        BPlusTreeSectionHeader readHeader = BPlusTreeBulkLoader.ReadSectionHeader(binaryReader);

        // Remaining bytes are pages.
        byte[][] pages = new byte[readHeader.PageCount][];

        for (uint pageIndex = 0; pageIndex < readHeader.PageCount; pageIndex++)
        {
            pages[pageIndex] = binaryReader.ReadBytes(BPlusTreeConstants.PageSize);
        }

        return (readHeader, pages);
    }

    /// <summary>
    /// Navigates from the root to the first (leftmost) leaf page.
    /// </summary>
    private static uint FindFirstLeafIndex(BPlusTreeSectionHeader header, byte[][] pages)
    {
        uint currentPage = header.RootPageIndex;

        for (int level = 0; level < header.TreeHeight - 1; level++)
        {
            BPlusTreeInternalPage internalPage = BPlusTreePageCodec.DecodeInternalPage(
                pages[currentPage], currentPage);
            currentPage = internalPage.GetChildPageIndex(0);
        }

        return currentPage;
    }

    /// <summary>
    /// Navigates from the root to the last (rightmost) leaf page.
    /// </summary>
    private static uint FindLastLeafIndex(BPlusTreeSectionHeader header, byte[][] pages)
    {
        uint currentPage = header.RootPageIndex;

        for (int level = 0; level < header.TreeHeight - 1; level++)
        {
            BPlusTreeInternalPage internalPage = BPlusTreePageCodec.DecodeInternalPage(
                pages[currentPage], currentPage);
            currentPage = internalPage.GetChildPageIndex(internalPage.ChildCount - 1);
        }

        return currentPage;
    }
}
