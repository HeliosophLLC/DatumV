using System.Diagnostics.CodeAnalysis;
using DatumIngest.Indexing.Bitmap;
using DatumIngest.Indexing.BTree;
using DatumIngest.Indexing.Sorted;
using DatumIngest.Indexing.Bloom;
using DatumIngest.Model;

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

    /// <summary>
    /// Merges an <paramref name="existing"/> index covering rows
    /// <c>[0, existing.TotalRowCount)</c> with a <paramref name="delta"/> index
    /// covering only the appended rows <c>[existing.TotalRowCount, total)</c>.
    /// PR13b's chunk-splice carry-forward — the new index's chunk array is
    /// <c>existing.Chunks ++ shifted(delta.Chunks)</c>; bloom and bitmap
    /// arrays grow per column by appending the delta's chunk slots.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Limitations:
    /// <list type="bullet">
    ///   <item>Schemas must be identical (column count, names, kinds). DDL
    ///     mutations invalidate the index path before reaching this code.</item>
    ///   <item>Sorted (mapped) and B+Tree indexes are not merged — they live
    ///     across all rows in key order, so the carry-forward path drops
    ///     them. Callers fall back to full rebuild when the existing index
    ///     uses sorted/B+Tree.</item>
    ///   <item>Bloom and bitmap merge by column-name intersection — columns
    ///     that appear in only one side (e.g. a bitmap that abandoned mid-build
    ///     in one of the two passes) are dropped from the merged result.</item>
    /// </list>
    /// </para>
    /// <para>
    /// The merged index inherits <paramref name="delta"/>'s fingerprint
    /// (post-mutation file). The merged total row count is
    /// <c>existing.TotalRowCount + delta.TotalRowCount</c>.
    /// </para>
    /// </remarks>
    /// <param name="existing">The pre-mutation index (covers all rows up to the append point).</param>
    /// <param name="delta">An index covering only the appended rows.</param>
    /// <returns>The merged index ready for serialization.</returns>
    /// <exception cref="InvalidOperationException">Schemas mismatch.</exception>
    public static SourceIndex Merge(SourceIndex existing, SourceIndex delta)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(delta);

        if (existing.Schema.Schema.Columns.Count != delta.Schema.Schema.Columns.Count)
        {
            throw new InvalidOperationException(
                "SourceIndex.Merge: existing/delta schemas have different column counts.");
        }

        long existingRowCount = existing.Schema.TotalRowCount;
        int existingChunkCount = existing.Chunks.Count;
        int deltaChunkCount = delta.Chunks.Count;
        int totalChunkCount = existingChunkCount + deltaChunkCount;

        // Concatenate chunks with delta's RowOffsets shifted forward by the
        // existing total row count.
        List<IndexChunk> mergedChunks = new(totalChunkCount);
        mergedChunks.AddRange(existing.Chunks);
        foreach (IndexChunk c in delta.Chunks)
        {
            mergedChunks.Add(new IndexChunk(
                RowOffset: c.RowOffset + existingRowCount,
                RowCount: c.RowCount,
                ColumnStatistics: c.ColumnStatistics));
        }

        BloomFilterSet? mergedBloom = MergeBlooms(
            existing.BloomFilters, existingChunkCount,
            delta.BloomFilters, deltaChunkCount);

        BitmapIndexSet? mergedBitmap = MergeBitmaps(
            existing.BitmapIndexes, existingChunkCount,
            delta.BitmapIndexes, deltaChunkCount,
            mergedChunks);

        IndexSchema mergedSchema = new(
            existing.Schema.Schema,
            existingRowCount + delta.Schema.TotalRowCount);

        return new SourceIndex(
            fingerprint: delta.Fingerprint,
            schema: mergedSchema,
            chunks: mergedChunks,
            bloomFilters: mergedBloom,
            bPlusTreeIndexes: null,
            bitmapIndexes: mergedBitmap,
            mappedSortedIndexes: null);
    }

    private static BloomFilterSet? MergeBlooms(
        BloomFilterSet? existing, int existingChunkCount,
        BloomFilterSet? delta, int deltaChunkCount)
    {
        if (existing is null || delta is null)
        {
            return null;
        }

        Dictionary<string, BloomFilter[]> merged = new(StringComparer.OrdinalIgnoreCase);
        int totalChunks = existingChunkCount + deltaChunkCount;

        foreach (string column in existing.ColumnNames)
        {
            BloomFilter[]? eFilters = existing.GetColumnFilters(column);
            BloomFilter[]? dFilters = delta.GetColumnFilters(column);
            if (eFilters is null || dFilters is null) continue;

            BloomFilter[] combined = new BloomFilter[totalChunks];
            int eCopy = Math.Min(eFilters.Length, existingChunkCount);
            int dCopy = Math.Min(dFilters.Length, deltaChunkCount);
            Array.Copy(eFilters, 0, combined, 0, eCopy);
            Array.Copy(dFilters, 0, combined, existingChunkCount, dCopy);
            merged[column] = combined;
        }

        return merged.Count > 0 ? new BloomFilterSet(merged, totalChunks) : null;
    }

    private static BitmapIndexSet? MergeBitmaps(
        BitmapIndexSet? existing, int existingChunkCount,
        BitmapIndexSet? delta, int deltaChunkCount,
        IReadOnlyList<IndexChunk> mergedChunks)
    {
        if (existing is null || delta is null)
        {
            return null;
        }

        Dictionary<string, BitmapColumnIndex> mergedColumns = new(StringComparer.OrdinalIgnoreCase);
        int totalChunks = existingChunkCount + deltaChunkCount;

        // Merged chunk row counts derived from chunk metadata — the
        // bitmap reader only ever reads rowCount[i] for a chunk it has
        // a bitmap entry for, so any chunk in the array is fine.
        int[] rowCounts = new int[totalChunks];
        for (int i = 0; i < totalChunks; i++)
        {
            rowCounts[i] = (int)mergedChunks[i].RowCount;
        }

        foreach (string column in existing.ColumnNames)
        {
            if (!delta.ColumnNames.Contains(column, StringComparer.OrdinalIgnoreCase))
            {
                // Column abandoned on one side — drop it. Conservative
                // merge keeps only columns where both sides agree.
                continue;
            }

            if (!existing.TryGetIndex(column, out BitmapColumnIndex? eCol)) continue;
            if (!delta.TryGetIndex(column, out BitmapColumnIndex? dCol)) continue;

            IReadOnlyDictionary<DataValue, byte[][]> eMaps = eCol.CompressedBitmaps;
            IReadOnlyDictionary<DataValue, byte[][]> dMaps = dCol.CompressedBitmaps;

            // Union of distinct values across existing and delta. For each
            // value, build a byte[][] of length totalChunks: existing chunks
            // 0..existingChunkCount, then delta chunks. Slots without an
            // entry get an empty array (the reader treats Length==0 as
            // "value absent from chunk").
            Dictionary<DataValue, byte[][]> mergedValueBitmaps = new();

            foreach (KeyValuePair<DataValue, byte[][]> kvp in eMaps)
            {
                byte[][] combined = new byte[totalChunks][];
                int eCopy = Math.Min(kvp.Value.Length, existingChunkCount);
                for (int c = 0; c < eCopy; c++) combined[c] = kvp.Value[c] ?? [];
                for (int c = eCopy; c < totalChunks; c++) combined[c] = [];
                mergedValueBitmaps[kvp.Key] = combined;
            }

            foreach (KeyValuePair<DataValue, byte[][]> kvp in dMaps)
            {
                if (!mergedValueBitmaps.TryGetValue(kvp.Key, out byte[][]? combined))
                {
                    combined = new byte[totalChunks][];
                    for (int c = 0; c < totalChunks; c++) combined[c] = [];
                    mergedValueBitmaps[kvp.Key] = combined;
                }
                int dCopy = Math.Min(kvp.Value.Length, deltaChunkCount);
                for (int c = 0; c < dCopy; c++) combined[existingChunkCount + c] = kvp.Value[c] ?? [];
            }

            if (mergedValueBitmaps.Count == 0) continue;

            mergedColumns[column] = new BitmapColumnIndex(
                mergedValueBitmaps,
                totalChunks,
                rowCounts);
        }

        return mergedColumns.Count > 0 ? new BitmapIndexSet(mergedColumns) : null;
    }
}
