using System.Text;
using DatumIngest.Indexing;
using DatumIngest.Indexing.BTree;
using DatumIngest.Model;

namespace DatumIngest.Tests.Indexing;

/// <summary>
/// Tests for <see cref="BPlusTreeColumnIndex"/> — the <see cref="IColumnIndex"/>
/// adapter over <see cref="BPlusTreeReader"/>. Verifies the full contract: exact
/// lookup, range queries, chunk-based predicates, and traversal.
/// </summary>
public sealed class BPlusTreeColumnIndexTests : ServiceTestBase
{
    // ───────────────────────── FindExact ─────────────────────────

    [Fact]
    public void FindExact_ExistingKey_ReturnsMatchingEntries()
    {
        BPlusTreeColumnIndex index = BuildColumnIndex(GenerateEntries(100));

        IReadOnlyList<ValueIndexEntry> result = index.FindExact(DataValue.FromFloat32(50.0f));
        Assert.Single(result);
        Assert.Equal(DataValue.FromFloat32(50.0f), result[0].Key);
    }

    [Fact]
    public void FindExact_MissingKey_ReturnsEmpty()
    {
        BPlusTreeColumnIndex index = BuildColumnIndex(GenerateEntries(100));

        IReadOnlyList<ValueIndexEntry> result = index.FindExact(DataValue.FromFloat32(999.0f));
        Assert.Empty(result);
    }

    [Fact]
    public void FindExact_DuplicateKeys_ReturnsAll()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromFloat32(1.0f), 0, 0L),
            new(DataValue.FromFloat32(1.0f), 0, 10L),
            new(DataValue.FromFloat32(1.0f), 1, 0L),
            new(DataValue.FromFloat32(2.0f), 1, 10L),
        ];

        BPlusTreeColumnIndex index = BuildColumnIndex(entries);

        IReadOnlyList<ValueIndexEntry> result = index.FindExact(DataValue.FromFloat32(1.0f));
        Assert.Equal(3, result.Count);
    }

    // ───────────────────────── FindRange ─────────────────────────

    [Fact]
    public void FindRange_InclusiveBounds_ReturnsCorrectEntries()
    {
        BPlusTreeColumnIndex index = BuildColumnIndex(GenerateEntries(100));

        IReadOnlyList<ValueIndexEntry> result = index.FindRange(
            DataValue.FromFloat32(10.0f), DataValue.FromFloat32(20.0f));

        Assert.Equal(11, result.Count); // 10, 11, 12, ..., 20

        foreach (ValueIndexEntry entry in result)
        {
            float key = entry.Key.AsFloat32();
            Assert.True(key >= 10.0f && key <= 20.0f);
        }
    }

    [Fact]
    public void FindRange_SingleValue_ReturnsThatEntry()
    {
        BPlusTreeColumnIndex index = BuildColumnIndex(GenerateEntries(100));

        IReadOnlyList<ValueIndexEntry> result = index.FindRange(
            DataValue.FromFloat32(50.0f), DataValue.FromFloat32(50.0f));

        Assert.Single(result);
        Assert.Equal(DataValue.FromFloat32(50.0f), result[0].Key);
    }

    [Fact]
    public void FindRange_NoMatch_ReturnsEmpty()
    {
        BPlusTreeColumnIndex index = BuildColumnIndex(GenerateEntries(100));

        IReadOnlyList<ValueIndexEntry> result = index.FindRange(
            DataValue.FromFloat32(200.0f), DataValue.FromFloat32(300.0f));

        Assert.Empty(result);
    }

    // ───────────────────────── FindChunksContaining ─────────────────────────

    [Fact]
    public void FindChunksContaining_ExistingKey_ReturnsChunk()
    {
        BPlusTreeColumnIndex index = BuildColumnIndex(GenerateEntries(100));

        IReadOnlySet<int> chunks = index.FindChunksContaining(DataValue.FromFloat32(50.0f));
        Assert.Single(chunks);
        Assert.Contains(0, chunks); // Chunk 0 (entries 0-99 all in chunk 0 with chunkSize=1000)
    }

    [Fact]
    public void FindChunksContaining_MissingKey_ReturnsEmpty()
    {
        BPlusTreeColumnIndex index = BuildColumnIndex(GenerateEntries(100));

        IReadOnlySet<int> chunks = index.FindChunksContaining(DataValue.FromFloat32(999.0f));
        Assert.Empty(chunks);
    }

    // ───────────────────────── FindChunksInRange ─────────────────────────

    [Fact]
    public void FindChunksInRange_MultipleChunks_ReturnsAll()
    {
        // Entries spanning multiple chunks.
        ValueIndexEntry[] entries = GenerateMultiChunkEntries(2000, 500);
        BPlusTreeColumnIndex index = BuildColumnIndex(entries);

        IReadOnlySet<int> chunks = index.FindChunksInRange(
            DataValue.FromFloat32(0.0f), DataValue.FromFloat32(1999.0f));

        // Should include all 4 chunks (0, 1, 2, 3).
        Assert.Equal(4, chunks.Count);

        for (int chunkIndex = 0; chunkIndex < 4; chunkIndex++)
        {
            Assert.Contains(chunkIndex, chunks);
        }
    }

    // ───────────────────────── FindChunksLessThan ─────────────────────────

    [Fact]
    public void FindChunksLessThan_ReturnsChunksWithSmallerKeys()
    {
        ValueIndexEntry[] entries = GenerateMultiChunkEntries(2000, 500);
        BPlusTreeColumnIndex index = BuildColumnIndex(entries);

        IReadOnlySet<int> chunks = index.FindChunksLessThan(DataValue.FromFloat32(500.0f));

        // Entries 0-499 are in chunk 0, entry 500 is boundary — so only chunk 0.
        Assert.Contains(0, chunks);
        Assert.DoesNotContain(3, chunks);
    }

    // ───────────────────────── FindChunksLessThanOrEqual ─────────────────────────

    [Fact]
    public void FindChunksLessThanOrEqual_IncludesBoundaryChunk()
    {
        ValueIndexEntry[] entries = GenerateMultiChunkEntries(2000, 500);
        BPlusTreeColumnIndex index = BuildColumnIndex(entries);

        IReadOnlySet<int> chunks = index.FindChunksLessThanOrEqual(DataValue.FromFloat32(500.0f));

        Assert.Contains(0, chunks);
        // Entry 500 (key=500.0f) is in chunk 1, row 0 — should be included.
        Assert.Contains(1, chunks);
    }

    // ───────────────────────── FindChunksGreaterThan ─────────────────────────

    [Fact]
    public void FindChunksGreaterThan_ReturnsChunksWithLargerKeys()
    {
        ValueIndexEntry[] entries = GenerateMultiChunkEntries(2000, 500);
        BPlusTreeColumnIndex index = BuildColumnIndex(entries);

        IReadOnlySet<int> chunks = index.FindChunksGreaterThan(DataValue.FromFloat32(1499.0f));

        // Entries 1500-1999 are in chunk 3.
        Assert.Contains(3, chunks);
        Assert.DoesNotContain(0, chunks);
    }

    // ───────────────────────── FindChunksGreaterThanOrEqual ─────────────────────────

    [Fact]
    public void FindChunksGreaterThanOrEqual_IncludesBoundaryChunk()
    {
        ValueIndexEntry[] entries = GenerateMultiChunkEntries(2000, 500);
        BPlusTreeColumnIndex index = BuildColumnIndex(entries);

        IReadOnlySet<int> chunks = index.FindChunksGreaterThanOrEqual(DataValue.FromFloat32(1500.0f));

        Assert.Contains(3, chunks);
    }

    // ───────────────────────── Traversal ─────────────────────────

    [Fact]
    public void TraverseForward_ReturnsAllEntriesAscending()
    {
        ValueIndexEntry[] entries = GenerateEntries(200);
        BPlusTreeColumnIndex index = BuildColumnIndex(entries);

        List<ValueIndexEntry> traversed = index.TraverseForward().ToList();
        Assert.Equal(200, traversed.Count);

        for (int i = 1; i < traversed.Count; i++)
        {
            Assert.True(traversed[i].Key.AsFloat32() >= traversed[i - 1].Key.AsFloat32());
        }
    }

    [Fact]
    public void TraverseBackward_ReturnsAllEntriesDescending()
    {
        ValueIndexEntry[] entries = GenerateEntries(200);
        BPlusTreeColumnIndex index = BuildColumnIndex(entries);

        List<ValueIndexEntry> traversed = index.TraverseBackward().ToList();
        Assert.Equal(200, traversed.Count);

        for (int i = 1; i < traversed.Count; i++)
        {
            Assert.True(traversed[i].Key.AsFloat32() <= traversed[i - 1].Key.AsFloat32());
        }
    }

    // ───────────────────────── EntryCount ─────────────────────────

    [Fact]
    public void EntryCount_MatchesInputSize()
    {
        BPlusTreeColumnIndex index = BuildColumnIndex(GenerateEntries(300));
        Assert.Equal(300, index.EntryCount);
    }

    // ───────────────────────── Helpers ─────────────────────────

    /// <summary>
    /// Generates sorted scalar entries with unique keys, all in a single chunk.
    /// </summary>
    private static ValueIndexEntry[] GenerateEntries(int count)
    {
        ValueIndexEntry[] entries = new ValueIndexEntry[count];

        for (int index = 0; index < count; index++)
        {
            entries[index] = new ValueIndexEntry(
                DataValue.FromFloat32((float)index), 0, (long)index);
        }

        return entries;
    }

    /// <summary>
    /// Generates sorted scalar entries distributed across chunks.
    /// </summary>
    private static ValueIndexEntry[] GenerateMultiChunkEntries(int count, int chunkSize)
    {
        ValueIndexEntry[] entries = new ValueIndexEntry[count];

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
    /// Builds a B+Tree and wraps it in a <see cref="BPlusTreeColumnIndex"/>.
    /// </summary>
    private static BPlusTreeColumnIndex BuildColumnIndex(ValueIndexEntry[] entries)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);

        BPlusTreeSectionHeader? header = BPlusTreeBulkLoader.Build(
            entries, "test_column", DataKind.Float32, writer);

        Assert.NotNull(header);

        stream.Position = 0;
        using BinaryReader binaryReader = new(stream, Encoding.UTF8, leaveOpen: true);
        BPlusTreeSectionHeader readHeader = BPlusTreeBulkLoader.ReadSectionHeader(binaryReader);

        byte[][] pages = new byte[readHeader.PageCount][];

        for (uint pageIndex = 0; pageIndex < readHeader.PageCount; pageIndex++)
        {
            pages[pageIndex] = binaryReader.ReadBytes(BPlusTreeConstants.PageSize);
        }

        BPlusTreeReader reader = new(readHeader, pages);
        return new BPlusTreeColumnIndex(reader);
    }
}
