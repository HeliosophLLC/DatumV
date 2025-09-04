using DatumIngest.Indexing;
using DatumIngest.Model;

namespace DatumIngest.Tests.Indexing;

/// <summary>
/// Round-trip tests for the v4 memory-mapped sorted index. Writes entries through
/// <see cref="MappedSortedIndexWriter"/>, reads them back via <see cref="MappedSortedIndexReader"/>,
/// and verifies that <see cref="MappedSortedIndex"/> produces correct results for all
/// <see cref="IColumnIndex"/> operations.
/// </summary>
public sealed class MappedSortedIndexRoundTripTests : IDisposable
{
    private readonly string _tempDirectory;

    /// <summary>Creates a temporary directory for test files.</summary>
    public MappedSortedIndexRoundTripTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "MappedSortedIndexTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    /// <summary>Cleans up test files.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    // ──────────────── Int32 column tests ────────────────

    [Fact]
    public void Int32_RoundTrip_FindExact()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromInt32(10), 0, 0),
            new(DataValue.FromInt32(20), 0, 1),
            new(DataValue.FromInt32(20), 1, 0),
            new(DataValue.FromInt32(30), 1, 1),
        ];

        using MappedSortedIndexSet indexSet = WriteAndReopen("int32_exact",
            [("score", DataKind.Int32, entries)]);

        Assert.True(indexSet.TryGetIndex("score", out MappedSortedIndex? index));
        Assert.Equal(4, index.EntryCount);

        IReadOnlyList<ValueIndexEntry> results = index.FindExact(DataValue.FromInt32(20));
        Assert.Equal(2, results.Count);
        Assert.Equal(0, results[0].ChunkIndex);
        Assert.Equal(1, results[0].RowOffsetInChunk);
        Assert.Equal(1, results[1].ChunkIndex);
        Assert.Equal(0, results[1].RowOffsetInChunk);
    }

    [Fact]
    public void Int32_FindExact_NotFound_ReturnsEmpty()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromInt32(10), 0, 0),
            new(DataValue.FromInt32(30), 1, 0),
        ];

        using MappedSortedIndexSet indexSet = WriteAndReopen("int32_notfound",
            [("value", DataKind.Int32, entries)]);

        Assert.True(indexSet.TryGetIndex("value", out MappedSortedIndex? index));
        IReadOnlyList<ValueIndexEntry> results = index.FindExact(DataValue.FromInt32(20));
        Assert.Empty(results);
    }

    [Fact]
    public void Int32_FindRange()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromInt32(-100), 0, 0),
            new(DataValue.FromInt32(0), 0, 1),
            new(DataValue.FromInt32(50), 1, 0),
            new(DataValue.FromInt32(100), 1, 1),
            new(DataValue.FromInt32(200), 2, 0),
        ];

        using MappedSortedIndexSet indexSet = WriteAndReopen("int32_range",
            [("value", DataKind.Int32, entries)]);

        Assert.True(indexSet.TryGetIndex("value", out MappedSortedIndex? index));

        IReadOnlyList<ValueIndexEntry> results = index.FindRange(
            DataValue.FromInt32(0), DataValue.FromInt32(100));

        Assert.Equal(3, results.Count);
        Assert.Equal(0, results[0].AsInt32());
        Assert.Equal(50, results[1].AsInt32());
        Assert.Equal(100, results[2].AsInt32());
    }

    [Fact]
    public void Int32_FindChunksLessThan()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromInt32(10), 0, 0),
            new(DataValue.FromInt32(20), 1, 0),
            new(DataValue.FromInt32(30), 2, 0),
        ];

        using MappedSortedIndexSet indexSet = WriteAndReopen("int32_lt",
            [("value", DataKind.Int32, entries)]);

        Assert.True(indexSet.TryGetIndex("value", out MappedSortedIndex? index));

        IReadOnlySet<int> chunks = index.FindChunksLessThan(DataValue.FromInt32(25));
        Assert.Equal(2, chunks.Count);
        Assert.Contains(0, chunks);
        Assert.Contains(1, chunks);
    }

    [Fact]
    public void Int32_FindChunksGreaterThanOrEqual()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromInt32(10), 0, 0),
            new(DataValue.FromInt32(20), 1, 0),
            new(DataValue.FromInt32(30), 2, 0),
        ];

        using MappedSortedIndexSet indexSet = WriteAndReopen("int32_gte",
            [("value", DataKind.Int32, entries)]);

        Assert.True(indexSet.TryGetIndex("value", out MappedSortedIndex? index));

        IReadOnlySet<int> chunks = index.FindChunksGreaterThanOrEqual(DataValue.FromInt32(20));
        Assert.Equal(2, chunks.Count);
        Assert.Contains(1, chunks);
        Assert.Contains(2, chunks);
    }

    [Fact]
    public void Int32_TraverseForward()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromInt32(-5), 0, 0),
            new(DataValue.FromInt32(0), 0, 1),
            new(DataValue.FromInt32(42), 1, 0),
        ];

        using MappedSortedIndexSet indexSet = WriteAndReopen("int32_fwd",
            [("value", DataKind.Int32, entries)]);

        Assert.True(indexSet.TryGetIndex("value", out MappedSortedIndex? index));

        List<ValueIndexEntry> traversed = index.TraverseForward().ToList();
        Assert.Equal(3, traversed.Count);
        Assert.Equal(-5, traversed[0].Key.AsInt32());
        Assert.Equal(0, traversed[1].Key.AsInt32());
        Assert.Equal(42, traversed[2].Key.AsInt32());
    }

    [Fact]
    public void Int32_TraverseBackward()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromInt32(-5), 0, 0),
            new(DataValue.FromInt32(0), 0, 1),
            new(DataValue.FromInt32(42), 1, 0),
        ];

        using MappedSortedIndexSet indexSet = WriteAndReopen("int32_bwd",
            [("value", DataKind.Int32, entries)]);

        Assert.True(indexSet.TryGetIndex("value", out MappedSortedIndex? index));

        List<ValueIndexEntry> traversed = index.TraverseBackward().ToList();
        Assert.Equal(3, traversed.Count);
        Assert.Equal(42, traversed[0].Key.AsInt32());
        Assert.Equal(0, traversed[1].Key.AsInt32());
        Assert.Equal(-5, traversed[2].Key.AsInt32());
    }

    // ──────────────── String column tests ────────────────

    [Fact]
    public void String_RoundTrip_FindExact()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromString("apple"), 0, 0),
            new(DataValue.FromString("banana"), 0, 1),
            new(DataValue.FromString("banana"), 1, 0),
            new(DataValue.FromString("cherry"), 1, 1),
        ];

        using MappedSortedIndexSet indexSet = WriteAndReopen("string_exact",
            [("fruit", DataKind.String, entries)]);

        Assert.True(indexSet.TryGetIndex("fruit", out MappedSortedIndex? index));

        IReadOnlyList<ValueIndexEntry> results = index.FindExact(DataValue.FromString("banana"));
        Assert.Equal(2, results.Count);
        Assert.Equal("banana", results[0].Key.AsString());
        Assert.Equal("banana", results[1].Key.AsString());
    }

    [Fact]
    public void String_FindRange()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromString("apple"), 0, 0),
            new(DataValue.FromString("banana"), 0, 1),
            new(DataValue.FromString("cherry"), 1, 0),
            new(DataValue.FromString("date"), 1, 1),
            new(DataValue.FromString("elderberry"), 2, 0),
        ];

        using MappedSortedIndexSet indexSet = WriteAndReopen("string_range",
            [("fruit", DataKind.String, entries)]);

        Assert.True(indexSet.TryGetIndex("fruit", out MappedSortedIndex? index));

        IReadOnlyList<ValueIndexEntry> results = index.FindRange(
            DataValue.FromString("banana"), DataValue.FromString("date"));

        Assert.Equal(3, results.Count);
        Assert.Equal("banana", results[0].Key.AsString());
        Assert.Equal("cherry", results[1].Key.AsString());
        Assert.Equal("date", results[2].Key.AsString());
    }

    [Fact]
    public void String_DuplicateValues_ShareStringTableEntries()
    {
        // Verify round-trip works correctly when many entries share the same string.
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromString("repeated"), 0, 0),
            new(DataValue.FromString("repeated"), 0, 1),
            new(DataValue.FromString("repeated"), 1, 0),
            new(DataValue.FromString("unique"), 1, 1),
        ];

        using MappedSortedIndexSet indexSet = WriteAndReopen("string_dedup",
            [("status", DataKind.String, entries)]);

        Assert.True(indexSet.TryGetIndex("status", out MappedSortedIndex? index));

        IReadOnlyList<ValueIndexEntry> results = index.FindExact(DataValue.FromString("repeated"));
        Assert.Equal(3, results.Count);
    }

    // ──────────────── Float64 column tests ────────────────

    [Fact]
    public void Float64_RoundTrip_FindExact()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromFloat64(-1.5), 0, 0),
            new(DataValue.FromFloat64(0.0), 0, 1),
            new(DataValue.FromFloat64(3.14), 1, 0),
        ];

        using MappedSortedIndexSet indexSet = WriteAndReopen("float64_exact",
            [("price", DataKind.Float64, entries)]);

        Assert.True(indexSet.TryGetIndex("price", out MappedSortedIndex? index));

        IReadOnlyList<ValueIndexEntry> results = index.FindExact(DataValue.FromFloat64(3.14));
        Assert.Single(results);
        Assert.Equal(3.14, results[0].Key.AsFloat64());
    }

    // ──────────────── Date column tests ────────────────

    [Fact]
    public void Date_RoundTrip_FindRange()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromDate(new DateOnly(2024, 1, 1)), 0, 0),
            new(DataValue.FromDate(new DateOnly(2025, 6, 15)), 0, 1),
            new(DataValue.FromDate(new DateOnly(2026, 4, 7)), 1, 0),
            new(DataValue.FromDate(new DateOnly(2027, 12, 31)), 1, 1),
        ];

        using MappedSortedIndexSet indexSet = WriteAndReopen("date_range",
            [("order_date", DataKind.Date, entries)]);

        Assert.True(indexSet.TryGetIndex("order_date", out MappedSortedIndex? index));

        IReadOnlyList<ValueIndexEntry> results = index.FindRange(
            DataValue.FromDate(new DateOnly(2025, 1, 1)),
            DataValue.FromDate(new DateOnly(2026, 12, 31)));

        Assert.Equal(2, results.Count);
        Assert.Equal(new DateOnly(2025, 6, 15), results[0].Key.AsDate());
        Assert.Equal(new DateOnly(2026, 4, 7), results[1].Key.AsDate());
    }

    // ──────────────── Multi-column tests ────────────────

    [Fact]
    public void MultiColumn_IndependentLookup()
    {
        ValueIndexEntry[] intEntries =
        [
            new(DataValue.FromInt32(1), 0, 0),
            new(DataValue.FromInt32(2), 0, 1),
            new(DataValue.FromInt32(3), 1, 0),
        ];

        ValueIndexEntry[] stringEntries =
        [
            new(DataValue.FromString("a"), 0, 0),
            new(DataValue.FromString("b"), 0, 1),
            new(DataValue.FromString("c"), 1, 0),
        ];

        using MappedSortedIndexSet indexSet = WriteAndReopen("multi_col",
        [
            ("id", DataKind.Int32, intEntries),
            ("name", DataKind.String, stringEntries),
        ]);

        Assert.Equal(2, indexSet.Count);
        Assert.True(indexSet.HasColumn("id"));
        Assert.True(indexSet.HasColumn("name"));

        Assert.True(indexSet.TryGetIndex("id", out MappedSortedIndex? idIndex));
        Assert.Equal(3, idIndex.EntryCount);
        Assert.Single(idIndex.FindExact(DataValue.FromInt32(2)));

        Assert.True(indexSet.TryGetIndex("name", out MappedSortedIndex? nameIndex));
        Assert.Equal(3, nameIndex.EntryCount);
        Assert.Single(nameIndex.FindExact(DataValue.FromString("b")));
    }

    [Fact]
    public void CaseInsensitive_ColumnLookup()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromInt32(1), 0, 0),
        ];

        using MappedSortedIndexSet indexSet = WriteAndReopen("case_insensitive",
            [("OrderId", DataKind.Int32, entries)]);

        Assert.True(indexSet.TryGetIndex("orderid", out _));
        Assert.True(indexSet.TryGetIndex("ORDERID", out _));
        Assert.True(indexSet.TryGetIndex("OrderId", out _));
    }

    // ──────────────── Edge cases ────────────────

    [Fact]
    public void EmptyIndex_ReturnsEmpty()
    {
        ValueIndexEntry[] entries = [];

        using MappedSortedIndexSet indexSet = WriteAndReopen("empty",
            [("value", DataKind.Int32, entries)]);

        Assert.True(indexSet.TryGetIndex("value", out MappedSortedIndex? index));
        Assert.Equal(0, index.EntryCount);
        Assert.Empty(index.FindExact(DataValue.FromInt32(42)));
        Assert.Empty(index.TraverseForward());
    }

    [Fact]
    public void SingleEntry_AllOperationsWork()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromInt32(42), 0, 100),
        ];

        using MappedSortedIndexSet indexSet = WriteAndReopen("single",
            [("value", DataKind.Int32, entries)]);

        Assert.True(indexSet.TryGetIndex("value", out MappedSortedIndex? index));

        Assert.Single(index.FindExact(DataValue.FromInt32(42)));
        Assert.Empty(index.FindExact(DataValue.FromInt32(41)));

        Assert.Contains(0, index.FindChunksLessThanOrEqual(DataValue.FromInt32(42)));
        Assert.Empty(index.FindChunksLessThan(DataValue.FromInt32(42)));
        Assert.Contains(0, index.FindChunksGreaterThanOrEqual(DataValue.FromInt32(42)));
        Assert.Empty(index.FindChunksGreaterThan(DataValue.FromInt32(42)));

        List<ValueIndexEntry> forward = index.TraverseForward().ToList();
        Assert.Single(forward);
        Assert.Equal(42, forward[0].Key.AsInt32());
        Assert.Equal(100, forward[0].RowOffsetInChunk);
    }

    [Fact]
    public void FindChunksLessThanOrEqual_IncludesBoundary()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromInt32(10), 0, 0),
            new(DataValue.FromInt32(20), 1, 0),
            new(DataValue.FromInt32(30), 2, 0),
        ];

        using MappedSortedIndexSet indexSet = WriteAndReopen("lte_boundary",
            [("value", DataKind.Int32, entries)]);

        Assert.True(indexSet.TryGetIndex("value", out MappedSortedIndex? index));

        IReadOnlySet<int> chunks = index.FindChunksLessThanOrEqual(DataValue.FromInt32(20));
        Assert.Equal(2, chunks.Count);
        Assert.Contains(0, chunks);
        Assert.Contains(1, chunks);
    }

    [Fact]
    public void FindChunksGreaterThan_ExcludesBoundary()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromInt32(10), 0, 0),
            new(DataValue.FromInt32(20), 1, 0),
            new(DataValue.FromInt32(30), 2, 0),
        ];

        using MappedSortedIndexSet indexSet = WriteAndReopen("gt_boundary",
            [("value", DataKind.Int32, entries)]);

        Assert.True(indexSet.TryGetIndex("value", out MappedSortedIndex? index));

        IReadOnlySet<int> chunks = index.FindChunksGreaterThan(DataValue.FromInt32(20));
        Assert.Single(chunks);
        Assert.Contains(2, chunks);
    }

    [Fact]
    public void NonexistentColumn_ReturnsFalse()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromInt32(1), 0, 0),
        ];

        using MappedSortedIndexSet indexSet = WriteAndReopen("nonexistent",
            [("value", DataKind.Int32, entries)]);

        Assert.False(indexSet.TryGetIndex("does_not_exist", out _));
    }

    [Fact]
    public void FindChunksInRange_ReturnsDistinctChunks()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromInt32(1), 0, 0),
            new(DataValue.FromInt32(2), 0, 1),
            new(DataValue.FromInt32(3), 0, 2),
            new(DataValue.FromInt32(4), 1, 0),
            new(DataValue.FromInt32(5), 1, 1),
        ];

        using MappedSortedIndexSet indexSet = WriteAndReopen("chunks_range",
            [("value", DataKind.Int32, entries)]);

        Assert.True(indexSet.TryGetIndex("value", out MappedSortedIndex? index));

        IReadOnlySet<int> chunks = index.FindChunksInRange(
            DataValue.FromInt32(1), DataValue.FromInt32(5));
        Assert.Equal(2, chunks.Count);
        Assert.Contains(0, chunks);
        Assert.Contains(1, chunks);
    }

    // ──────────────── Additional DataKind round-trips ────────────────

    [Fact]
    public void Boolean_RoundTrip()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromBoolean(false), 0, 0),
            new(DataValue.FromBoolean(true), 0, 1),
        ];

        using MappedSortedIndexSet indexSet = WriteAndReopen("bool_rt",
            [("flag", DataKind.Boolean, entries)]);

        Assert.True(indexSet.TryGetIndex("flag", out MappedSortedIndex? index));
        Assert.Single(index.FindExact(DataValue.FromBoolean(true)));
        Assert.Single(index.FindExact(DataValue.FromBoolean(false)));
    }

    [Fact]
    public void Int64_RoundTrip()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromInt64(long.MinValue), 0, 0),
            new(DataValue.FromInt64(0L), 0, 1),
            new(DataValue.FromInt64(long.MaxValue), 1, 0),
        ];

        using MappedSortedIndexSet indexSet = WriteAndReopen("int64_rt",
            [("big_id", DataKind.Int64, entries)]);

        Assert.True(indexSet.TryGetIndex("big_id", out MappedSortedIndex? index));

        Assert.Single(index.FindExact(DataValue.FromInt64(0L)));
        Assert.Empty(index.FindExact(DataValue.FromInt64(1L)));

        IReadOnlySet<int> chunks = index.FindChunksLessThan(DataValue.FromInt64(1L));
        Assert.Single(chunks);
        Assert.Contains(0, chunks);
    }

    // ──────────────── Helpers ────────────────

    /// <summary>
    /// Writes the v4 index to a temp file, then re-opens it via <see cref="MappedSortedIndexReader"/>.
    /// </summary>
    private MappedSortedIndexSet WriteAndReopen(
        string testName,
        (string ColumnName, DataKind Kind, ValueIndexEntry[] Entries)[] columns)
    {
        string filePath = Path.Combine(_tempDirectory, testName + ".datum-index-v4");

        List<(string, DataKind, ReadOnlyMemory<ValueIndexEntry>)> columnList = new();

        foreach ((string columnName, DataKind kind, ValueIndexEntry[] entries) in columns)
        {
            columnList.Add((columnName, kind, entries.AsMemory()));
        }

        using (FileStream fileStream = new(filePath, FileMode.Create, FileAccess.ReadWrite))
        {
            MappedSortedIndexWriter.Write(fileStream, columnList);
        }

        return MappedSortedIndexReader.Open(filePath);
    }
}

/// <summary>
/// Extension methods for test assertions on <see cref="ValueIndexEntry"/>.
/// </summary>
internal static class ValueIndexEntryTestExtensions
{
    /// <summary>Extracts the Int32 key value for assertion readability.</summary>
    public static int AsInt32(this ValueIndexEntry entry) => entry.Key.AsInt32();
}
