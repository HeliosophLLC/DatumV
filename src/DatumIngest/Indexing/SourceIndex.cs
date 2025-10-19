using System.Diagnostics.CodeAnalysis;
using DatumIngest.Indexing.Bitmap;
using DatumIngest.Indexing.BTree;
using DatumIngest.Indexing.Sorted;
using DatumIngest.Indexing.Bloom;

namespace DatumIngest.Indexing;

/// <summary>
/// In-memory representation of a unified <c>.datum-index</c> file. Aggregates the
/// fingerprint, cached schema, chunk directory, and optional acceleration structures
/// (bloom filters, memory-mapped sorted indexes, B+Tree, bitmap). Constructed by
/// <see cref="SourceIndexBuilder"/> and serialized/deserialized by
/// <see cref="UnifiedIndexWriter"/>/<see cref="UnifiedIndexReader"/>.
/// </summary>
public sealed class SourceIndex
{
    /// <summary>Source file fingerprint for staleness detection.</summary>
    public SourceFingerprint Fingerprint { get; }

    /// <summary>Cached schema and total row count.</summary>
    public IndexSchema Schema { get; }

    /// <summary>Ordered list of chunks with per-column statistics.</summary>
    public IReadOnlyList<IndexChunk> Chunks { get; }

    /// <summary>
    /// Per-column, per-chunk bloom filters for membership testing,
    /// or <c>null</c> if bloom filters were not built.
    /// </summary>
    public BloomFilterSet? BloomFilters { get; }

    /// <summary>
    /// Per-column B+Tree indexes for demand-paged key lookup on large datasets,
    /// or <c>null</c> if no B+Tree indexes were built.
    /// </summary>
    internal BPlusTreeIndexSet? BPlusTreeIndexes { get; }

    /// <summary>
    /// Per-column bitmap indexes for low-cardinality columns,
    /// or <c>null</c> if no bitmap indexes were built.
    /// </summary>
    internal BitmapIndexSet? BitmapIndexes { get; }

    /// <summary>
    /// Per-column memory-mapped sorted indexes for zero-copy key lookup,
    /// or <c>null</c> if no mapped sorted indexes are available.
    /// </summary>
    internal Dictionary<string, SortedIndex>? MappedSortedIndexes { get; }

    /// <summary>
    /// Creates a new source index.
    /// </summary>
    /// <param name="fingerprint">Source file fingerprint.</param>
    /// <param name="schema">Cached schema and row count.</param>
    /// <param name="chunks">Ordered list of row chunks with column statistics.</param>
    /// <param name="bloomFilters">Optional bloom filter set for membership testing.</param>
    public SourceIndex(
        SourceFingerprint fingerprint,
        IndexSchema schema,
        IReadOnlyList<IndexChunk> chunks,
        BloomFilterSet? bloomFilters = null)
        : this(fingerprint, schema, chunks, bloomFilters, bPlusTreeIndexes: null)
    {
    }

    /// <summary>
    /// Creates a new source index with optional B+Tree, bitmap, and mapped sorted indexes.
    /// </summary>
    internal SourceIndex(
        SourceFingerprint fingerprint,
        IndexSchema schema,
        IReadOnlyList<IndexChunk> chunks,
        BloomFilterSet? bloomFilters,
        BPlusTreeIndexSet? bPlusTreeIndexes,
        BitmapIndexSet? bitmapIndexes = null,
        Dictionary<string, SortedIndex>? mappedSortedIndexes = null)
    {
        Fingerprint = fingerprint;
        Schema = schema;
        Chunks = chunks;
        BloomFilters = bloomFilters;
        BPlusTreeIndexes = bPlusTreeIndexes;
        BitmapIndexes = bitmapIndexes;
        MappedSortedIndexes = mappedSortedIndexes;
    }

    /// <summary>
    /// Retrieves the best available column index for the specified column,
    /// returning whichever implementation (mapped sorted or B+Tree) is present.
    /// This is the single entry point for operators and the query planner —
    /// callers never need to know which concrete index type backs the column.
    /// </summary>
    /// <param name="columnName">Column name (case-insensitive lookup).</param>
    /// <param name="index">The column index, or <c>null</c> if no index exists for this column.</param>
    /// <returns><c>true</c> if an index exists for the specified column.</returns>
    public bool TryGetColumnIndex(string columnName, [NotNullWhen(true)] out IColumnIndex? index)
    {
        if (MappedSortedIndexes is not null && MappedSortedIndexes.TryGetValue(columnName, out SortedIndex? mappedIndex))
        {
            index = mappedIndex;
            return true;
        }

        if (BPlusTreeIndexes is not null && BPlusTreeIndexes.TryGetIndex(columnName, out BPlusTreeColumnIndex? btreeIndex))
        {
            index = btreeIndex;
            return true;
        }

        index = null;
        return false;
    }

    /// <summary>
    /// Retrieves a sorted column index for the specified column. Unlike
    /// <see cref="TryGetColumnIndex"/>, this method never returns a B+Tree
    /// index — use it when the caller requires data that is physically ordered
    /// on disk (e.g. merge join). B+Tree indexes enumerate entries in key order
    /// but the underlying rows are scattered throughout the datum file, making
    /// full-scan sequential access prohibitively expensive.
    /// </summary>
    /// <param name="columnName">Column name (case-insensitive lookup).</param>
    /// <param name="index">The sorted column index, or <c>null</c> if none exists.</param>
    /// <returns><c>true</c> if a sorted index exists for the specified column.</returns>
    public bool TryGetSortedColumnIndex(string columnName, [NotNullWhen(true)] out IColumnIndex? index)
    {
        if (MappedSortedIndexes is not null && MappedSortedIndexes.TryGetValue(columnName, out SortedIndex? mappedIndex))
        {
            index = mappedIndex;
            return true;
        }

        index = null;
        return false;
    }
}
