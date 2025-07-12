using DatumIngest.Indexing;
using DatumIngest.Model;

namespace DatumIngest.Tests.Indexing;

/// <summary>
/// Tests for <see cref="SortedValueIndex"/> binary search, range queries,
/// and chunk lookup functionality.
/// </summary>
public sealed class SortedValueIndexTests
{
    [Fact]
    public void FindExact_SingleMatch_ReturnsEntry()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromScalar(1.0f), 0, 0),
            new(DataValue.FromScalar(2.0f), 0, 1),
            new(DataValue.FromScalar(3.0f), 1, 0),
        ];

        SortedValueIndex index = new(entries);

        IReadOnlyList<ValueIndexEntry> results = index.FindExact(DataValue.FromScalar(2.0f));

        Assert.Single(results);
        Assert.Equal(0, results[0].ChunkIndex);
        Assert.Equal(1, results[0].RowOffsetInChunk);
    }

    [Fact]
    public void FindExact_NotFound_ReturnsEmpty()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromScalar(1.0f), 0, 0),
            new(DataValue.FromScalar(3.0f), 1, 0),
        ];

        SortedValueIndex index = new(entries);

        IReadOnlyList<ValueIndexEntry> results = index.FindExact(DataValue.FromScalar(2.0f));

        Assert.Empty(results);
    }

    [Fact]
    public void FindExact_DuplicateKeys_ReturnsAll()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromScalar(1.0f), 0, 0),
            new(DataValue.FromScalar(2.0f), 0, 1),
            new(DataValue.FromScalar(2.0f), 1, 0),
            new(DataValue.FromScalar(2.0f), 1, 5),
            new(DataValue.FromScalar(3.0f), 2, 0),
        ];

        SortedValueIndex index = new(entries);

        IReadOnlyList<ValueIndexEntry> results = index.FindExact(DataValue.FromScalar(2.0f));

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void FindExact_EmptyIndex_ReturnsEmpty()
    {
        SortedValueIndex index = new(Array.Empty<ValueIndexEntry>());

        IReadOnlyList<ValueIndexEntry> results = index.FindExact(DataValue.FromScalar(1.0f));

        Assert.Empty(results);
    }

    [Fact]
    public void FindExact_StringValues_WorksCorrectly()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromString("alice"), 0, 0),
            new(DataValue.FromString("bob"), 0, 1),
            new(DataValue.FromString("charlie"), 1, 0),
        ];

        SortedValueIndex index = new(entries);

        IReadOnlyList<ValueIndexEntry> results = index.FindExact(DataValue.FromString("bob"));

        Assert.Single(results);
        Assert.Equal(0, results[0].ChunkIndex);
        Assert.Equal(1, results[0].RowOffsetInChunk);
    }

    [Fact]
    public void FindRange_InclusiveBounds_ReturnsCorrectEntries()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromScalar(1.0f), 0, 0),
            new(DataValue.FromScalar(2.0f), 0, 1),
            new(DataValue.FromScalar(3.0f), 1, 0),
            new(DataValue.FromScalar(4.0f), 1, 1),
            new(DataValue.FromScalar(5.0f), 2, 0),
        ];

        SortedValueIndex index = new(entries);

        IReadOnlyList<ValueIndexEntry> results = index.FindRange(
            DataValue.FromScalar(2.0f), DataValue.FromScalar(4.0f));

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void FindRange_NoMatchesInRange_ReturnsEmpty()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromScalar(1.0f), 0, 0),
            new(DataValue.FromScalar(5.0f), 1, 0),
        ];

        SortedValueIndex index = new(entries);

        IReadOnlyList<ValueIndexEntry> results = index.FindRange(
            DataValue.FromScalar(2.0f), DataValue.FromScalar(4.0f));

        Assert.Empty(results);
    }

    [Fact]
    public void FindRange_AllEntriesInRange_ReturnsAll()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromScalar(2.0f), 0, 0),
            new(DataValue.FromScalar(3.0f), 0, 1),
            new(DataValue.FromScalar(4.0f), 1, 0),
        ];

        SortedValueIndex index = new(entries);

        IReadOnlyList<ValueIndexEntry> results = index.FindRange(
            DataValue.FromScalar(1.0f), DataValue.FromScalar(5.0f));

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void FindChunksContaining_ReturnsDistinctChunks()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromScalar(1.0f), 0, 0),
            new(DataValue.FromScalar(2.0f), 0, 1),
            new(DataValue.FromScalar(2.0f), 1, 0),
            new(DataValue.FromScalar(3.0f), 2, 0),
        ];

        SortedValueIndex index = new(entries);

        IReadOnlySet<int> chunks = index.FindChunksContaining(DataValue.FromScalar(2.0f));

        Assert.Equal(2, chunks.Count);
        Assert.Contains(0, chunks);
        Assert.Contains(1, chunks);
    }

    [Fact]
    public void FindChunksContaining_NotFound_ReturnsEmpty()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromScalar(1.0f), 0, 0),
            new(DataValue.FromScalar(3.0f), 1, 0),
        ];

        SortedValueIndex index = new(entries);

        IReadOnlySet<int> chunks = index.FindChunksContaining(DataValue.FromScalar(2.0f));

        Assert.Empty(chunks);
    }

    [Fact]
    public void FindChunksInRange_ReturnsDistinctChunks()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromScalar(1.0f), 0, 0),
            new(DataValue.FromScalar(3.0f), 1, 0),
            new(DataValue.FromScalar(5.0f), 2, 0),
            new(DataValue.FromScalar(7.0f), 3, 0),
        ];

        SortedValueIndex index = new(entries);

        IReadOnlySet<int> chunks = index.FindChunksInRange(
            DataValue.FromScalar(2.0f), DataValue.FromScalar(6.0f));

        Assert.Equal(2, chunks.Count);
        Assert.Contains(1, chunks);
        Assert.Contains(2, chunks);
    }

    [Fact]
    public void BuildFromUnsorted_SortsEntries()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromScalar(3.0f), 1, 0),
            new(DataValue.FromScalar(1.0f), 0, 0),
            new(DataValue.FromScalar(2.0f), 0, 1),
        ];

        SortedValueIndex index = SortedValueIndex.BuildFromUnsorted(entries);

        Assert.Equal(3, index.Count);
        ReadOnlySpan<ValueIndexEntry> sorted = index.Entries;
        Assert.Equal(1.0f, sorted[0].Key.AsScalar());
        Assert.Equal(2.0f, sorted[1].Key.AsScalar());
        Assert.Equal(3.0f, sorted[2].Key.AsScalar());
    }

    [Fact]
    public void BuildFromUnsorted_EmptyArray_ReturnsEmptyIndex()
    {
        SortedValueIndex index = SortedValueIndex.BuildFromUnsorted(Array.Empty<ValueIndexEntry>());

        Assert.Equal(0, index.Count);
    }

    [Fact]
    public void FindExact_DateValues_WorksCorrectly()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromDate(new DateOnly(2024, 1, 1)), 0, 0),
            new(DataValue.FromDate(new DateOnly(2024, 6, 15)), 0, 1),
            new(DataValue.FromDate(new DateOnly(2024, 12, 31)), 1, 0),
        ];

        SortedValueIndex index = new(entries);

        IReadOnlyList<ValueIndexEntry> results = index.FindExact(
            DataValue.FromDate(new DateOnly(2024, 6, 15)));

        Assert.Single(results);
        Assert.Equal(0, results[0].ChunkIndex);
        Assert.Equal(1, results[0].RowOffsetInChunk);
    }

    [Fact]
    public void FindExact_FirstElement_Found()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromScalar(1.0f), 0, 0),
            new(DataValue.FromScalar(2.0f), 0, 1),
            new(DataValue.FromScalar(3.0f), 1, 0),
        ];

        SortedValueIndex index = new(entries);

        IReadOnlyList<ValueIndexEntry> results = index.FindExact(DataValue.FromScalar(1.0f));

        Assert.Single(results);
    }

    [Fact]
    public void FindExact_LastElement_Found()
    {
        ValueIndexEntry[] entries =
        [
            new(DataValue.FromScalar(1.0f), 0, 0),
            new(DataValue.FromScalar(2.0f), 0, 1),
            new(DataValue.FromScalar(3.0f), 1, 0),
        ];

        SortedValueIndex index = new(entries);

        IReadOnlyList<ValueIndexEntry> results = index.FindExact(DataValue.FromScalar(3.0f));

        Assert.Single(results);
        Assert.Equal(1, results[0].ChunkIndex);
    }
}
