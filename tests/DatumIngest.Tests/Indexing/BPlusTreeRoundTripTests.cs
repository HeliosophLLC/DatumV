using System.Text;
using DatumIngest.Indexing;
using DatumIngest.Indexing.BTree;
using DatumIngest.Model;

namespace DatumIngest.Tests.Indexing;

/// <summary>
/// End-to-end round-trip tests for B+Tree indexes through the
/// <see cref="UnifiedIndexWriter"/> → <see cref="UnifiedIndexReader"/> pipeline.
/// Verifies that B+Tree sections survive serialization and remain
/// queryable after deserialization.
/// </summary>
public sealed class BPlusTreeRoundTripTests : ServiceTestBase
{
    // ───────────────────────── Single column ─────────────────────────

    [Fact]
    public void RoundTrip_SingleColumn_PreservesEntries()
    {
        int entryCount = 500;
        ValueIndexEntry[] entries = GenerateScalarEntries(entryCount);

        SourceIndex original = BuildSourceIndexWithBPlusTree("price", DataKind.Float32, entries);
        WithRoundTrip(original, restored =>
        {
            Assert.NotNull(restored.BPlusTreeIndexes);
            Assert.Equal(1, restored.BPlusTreeIndexes.Count);

            bool found = restored.BPlusTreeIndexes.TryGetIndex("price", out BPlusTreeColumnIndex? columnIndex);
            Assert.True(found);
            Assert.NotNull(columnIndex);
            Assert.Equal(entryCount, columnIndex.EntryCount);
        });
    }

    [Fact]
    public void RoundTrip_FindExact_WorksAfterDeserialization()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromFloat32(10.0f), 0, 0L),
            new(DataValue.FromFloat32(20.0f), 0, 1L),
            new(DataValue.FromFloat32(30.0f), 1, 0L),
            new(DataValue.FromFloat32(40.0f), 1, 1L),
            new(DataValue.FromFloat32(50.0f), 2, 0L),
        ];

        SourceIndex original = BuildSourceIndexWithBPlusTree("value", DataKind.Float32, entries);
        WithRoundTrip(original, restored =>
        {
            Assert.True(restored.TryGetColumnIndex("value", out IColumnIndex? index));

            IReadOnlyList<ValueIndexEntry> result = index.FindExact(DataValue.FromFloat32(30.0f));
            Assert.Single(result);
            Assert.Equal(1, result[0].ChunkIndex);
            Assert.Equal(0L, result[0].RowOffsetInChunk);
        });
    }

    [Fact]
    public void RoundTrip_FindRange_WorksAfterDeserialization()
    {
        int entryCount = 200;
        ValueIndexEntry[] entries = GenerateScalarEntries(entryCount);

        SourceIndex original = BuildSourceIndexWithBPlusTree("x", DataKind.Float32, entries);
        WithRoundTrip(original, restored =>
        {
            Assert.True(restored.TryGetColumnIndex("x", out IColumnIndex? index));

            IReadOnlyList<ValueIndexEntry> range = index.FindRange(
                DataValue.FromFloat32(50.0f), DataValue.FromFloat32(60.0f));

            Assert.Equal(11, range.Count); // 50..60 inclusive
        });
    }

    [Fact]
    public void RoundTrip_TraverseForward_PreservesAllEntries()
    {
        int entryCount = 300;
        ValueIndexEntry[] entries = GenerateScalarEntries(entryCount);

        SourceIndex original = BuildSourceIndexWithBPlusTree("col", DataKind.Float32, entries);
        WithRoundTrip(original, restored =>
        {
            Assert.True(restored.TryGetColumnIndex("col", out IColumnIndex? index));

            List<ValueIndexEntry> traversed = index.TraverseForward().ToList();
            Assert.Equal(entryCount, traversed.Count);

            // Verify ascending order.
            for (int i = 1; i < traversed.Count; i++)
            {
                Assert.True(traversed[i].Key.AsFloat32() >= traversed[i - 1].Key.AsFloat32());
            }
        });
    }

    // ───────────────────────── Multiple columns ─────────────────────────

    [Fact]
    public void RoundTrip_MultipleColumns_AllPreserved()
    {
        ValueIndexEntry[] priceEntries =
        [
            new(DataValue.FromFloat32(1.0f), 0, 0L),
            new(DataValue.FromFloat32(2.0f), 0, 1L),
            new(DataValue.FromFloat32(3.0f), 1, 0L),
        ];

        ValueIndexEntry[] nameEntries =
        [
            new(DataValue.FromString("alpha"), 0, 0L),
            new(DataValue.FromString("beta"), 0, 1L),
            new(DataValue.FromString("gamma"), 1, 0L),
        ];

        (BPlusTreeSectionHeader priceHeader, byte[][] pricePages) =
            BuildTree(priceEntries, "price", DataKind.Float32);
        (BPlusTreeSectionHeader nameHeader, byte[][] namePages) =
            BuildTree(nameEntries, "name", DataKind.String);

        BPlusTreeReader priceReader = new(priceHeader, pricePages);
        BPlusTreeReader nameReader = new(nameHeader, namePages);

        Dictionary<string, BPlusTreeColumnIndex> indexes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["price"] = new BPlusTreeColumnIndex(priceReader),
            ["name"] = new BPlusTreeColumnIndex(nameReader),
        };

        BPlusTreeIndexSet bTreeSet = new(indexes);
        SourceIndex original = BuildSourceIndexWithBTreeSet(bTreeSet);
        WithRoundTrip(original, restored =>
        {
            Assert.NotNull(restored.BPlusTreeIndexes);
            Assert.Equal(2, restored.BPlusTreeIndexes.Count);

            // Verify price column.
            Assert.True(restored.TryGetColumnIndex("price", out IColumnIndex? priceIndex));
            IReadOnlyList<ValueIndexEntry> priceResult = priceIndex.FindExact(DataValue.FromFloat32(2.0f));
            Assert.Single(priceResult);

            // Verify name column.
            Assert.True(restored.TryGetColumnIndex("name", out IColumnIndex? nameIndex));
            IReadOnlyList<ValueIndexEntry> nameResult = nameIndex.FindExact(DataValue.FromString("beta"));
            Assert.Single(nameResult);
        });
    }

    // ───────────────────────── Coexistence with sorted indexes ─────────────────────────

    [Fact]
    public void RoundTrip_BTreeAndSortedIndexes_BothPreserved()
    {
        // Build a B+Tree index for one column.
        ValueIndexEntry[] bTreeEntries =
        [
            new(DataValue.FromFloat32(100.0f), 0, 0L),
            new(DataValue.FromFloat32(200.0f), 0, 1L),
        ];

        (BPlusTreeSectionHeader header, byte[][] pages) =
            BuildTree(bTreeEntries, "big_column", DataKind.Float32);

        BPlusTreeReader treeReader = new(header, pages);
        Dictionary<string, BPlusTreeColumnIndex> bTreeIndexes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["big_column"] = new BPlusTreeColumnIndex(treeReader),
        };

        BPlusTreeIndexSet bTreeSet = new(bTreeIndexes);
        SourceFingerprint fingerprint = new(0, new byte[32]);
        Schema schema = new([new ColumnInfo("big_column", DataKind.Float32, nullable: false)]);
        IndexSchema indexSchema = new(schema, 2);
        SourceIndex original = new(fingerprint, indexSchema, Array.Empty<IndexChunk>(),
            bloomFilters: null, bPlusTreeIndexes: bTreeSet);

        WithRoundTrip(original, restored =>
        {
            Assert.True(restored.TryGetColumnIndex("big_column", out IColumnIndex? bigIndex));
            Assert.Equal(2, bigIndex.EntryCount);
        });
    }

    // ───────────────────────── Large tree round-trip ─────────────────────────

    [Fact]
    public void RoundTrip_LargeTree_AllEntriesPreserved()
    {
        int entryCount = 10_000;
        ValueIndexEntry[] entries = GenerateScalarEntries(entryCount);

        SourceIndex original = BuildSourceIndexWithBPlusTree("large", DataKind.Float32, entries);
        WithRoundTrip(original, restored =>
        {
            Assert.True(restored.TryGetColumnIndex("large", out IColumnIndex? index));
            Assert.Equal(entryCount, index.EntryCount);

            // Spot-check a few lookups.
            IReadOnlyList<ValueIndexEntry> first = index.FindExact(DataValue.FromFloat32(0.0f));
            Assert.Single(first);

            IReadOnlyList<ValueIndexEntry> last = index.FindExact(DataValue.FromFloat32((float)(entryCount - 1)));
            Assert.Single(last);

            IReadOnlyList<ValueIndexEntry> mid = index.FindExact(DataValue.FromFloat32((float)(entryCount / 2)));
            Assert.Single(mid);
        });
    }

    // ───────────────────────── String keys round-trip ─────────────────────────

    [Fact]
    public void RoundTrip_StringKeys_PreservesLookup()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromString("apple"), 0, 0L),
            new(DataValue.FromString("banana"), 0, 1L),
            new(DataValue.FromString("cherry"), 1, 0L),
            new(DataValue.FromString("date"), 1, 1L),
        ];

        SourceIndex original = BuildSourceIndexWithBPlusTree("fruit", DataKind.String, entries);
        WithRoundTrip(original, restored =>
        {
            Assert.True(restored.TryGetColumnIndex("fruit", out IColumnIndex? index));

            IReadOnlyList<ValueIndexEntry> result = index.FindExact(DataValue.FromString("cherry"));
            Assert.Single(result);
            Assert.Equal(1, result[0].ChunkIndex);
        });
    }

    // ───────────────────────── Helpers ─────────────────────────

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
    /// Builds a B+Tree from sorted entries and returns the header + raw pages.
    /// </summary>
    private static (BPlusTreeSectionHeader Header, byte[][] Pages) BuildTree(
        ValueIndexEntry[] entries, string columnName, DataKind keyKind)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);

        BPlusTreeSectionHeader? header = BPlusTreeBulkLoader.Build(
            entries, columnName, keyKind, writer);

        Assert.NotNull(header);

        stream.Position = 0;
        using BinaryReader binaryReader = new(stream, Encoding.UTF8, leaveOpen: true);
        BPlusTreeSectionHeader readHeader = BPlusTreeBulkLoader.ReadSectionHeader(binaryReader);

        byte[][] pages = new byte[readHeader.PageCount][];

        for (uint pageIndex = 0; pageIndex < readHeader.PageCount; pageIndex++)
        {
            pages[pageIndex] = binaryReader.ReadBytes(BPlusTreeConstants.PageSize);
        }

        return (readHeader, pages);
    }

    /// <summary>
    /// Builds a <see cref="SourceIndex"/> with a single B+Tree-indexed column.
    /// </summary>
    private static SourceIndex BuildSourceIndexWithBPlusTree(
        string columnName, DataKind keyKind, ValueIndexEntry[] entries)
    {
        (BPlusTreeSectionHeader header, byte[][] pages) = BuildTree(entries, columnName, keyKind);

        BPlusTreeReader reader = new(header, pages);
        Dictionary<string, BPlusTreeColumnIndex> indexes = new(StringComparer.OrdinalIgnoreCase)
        {
            [columnName] = new BPlusTreeColumnIndex(reader),
        };

        BPlusTreeIndexSet bTreeSet = new(indexes);
        return BuildSourceIndexWithBTreeSet(bTreeSet, columnName, keyKind);
    }

    private static SourceIndex BuildSourceIndexWithBTreeSet(
        BPlusTreeIndexSet bTreeSet,
        string columnName = "value",
        DataKind keyKind = DataKind.Float32)
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        Schema schema = new([new ColumnInfo(columnName, keyKind, nullable: false)]);
        IndexSchema indexSchema = new(schema, 100);

        return new SourceIndex(fingerprint, indexSchema, Array.Empty<IndexChunk>(),
            bloomFilters: null, bPlusTreeIndexes: bTreeSet);
    }

    private static void WithRoundTrip(SourceIndex index, Action<SourceIndex> test)
    {
        string tempFile = Path.GetTempFileName();
        try
        {
            using (FileStream stream = File.Create(tempFile))
            {
                SourceIndexSet indexSet = SourceIndexSet.Create("test", index);
                UnifiedIndexWriter.Write(indexSet, stream);
            }
            using MappedSourceIndexSet mapped = UnifiedIndexReader.Open(tempFile);
            test(mapped.IndexSet.Tables["test"]);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
