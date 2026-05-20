using Heliosoph.DatumV.Indexing.Bitmap;
using Heliosoph.DatumV.Indexing.Bloom;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Indexing;

/// <summary>
/// In-memory representation of a v8 unified <c>.datum-index</c> file. Aggregates the
/// fingerprint, cached schema, chunk directory, and bloom + bitmap acceleration
/// structures that live inside the unified sidecar. Constructed by
/// <see cref="SourceIndexBuilder"/> and serialized/deserialized by
/// <see cref="UnifiedIndexWriter"/>/<see cref="UnifiedIndexReader"/>.
/// </summary>
/// <remarks>
/// PR13d (v8) moved per-column B+Tree acceleration out of this in-memory
/// type and the unified sidecar entirely. Per-column lookup now goes
/// through <see cref="Catalog.ITableProvider.TryGetColumnIndex"/>, which
/// resolves against companion <c>.datum-bptree-{col}</c> page-COW files
/// owned by the provider.
/// </remarks>
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
    /// Per-column bitmap indexes for low-cardinality columns,
    /// or <c>null</c> if no bitmap indexes were built.
    /// </summary>
    internal BitmapIndexSet? BitmapIndexes { get; }

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
        : this(fingerprint, schema, chunks, bloomFilters, bitmapIndexes: null)
    {
    }

    internal SourceIndex(
        SourceFingerprint fingerprint,
        IndexSchema schema,
        IReadOnlyList<IndexChunk> chunks,
        BloomFilterSet? bloomFilters,
        BitmapIndexSet? bitmapIndexes)
    {
        Fingerprint = fingerprint;
        Schema = schema;
        Chunks = chunks;
        BloomFilters = bloomFilters;
        BitmapIndexes = bitmapIndexes;
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
            bitmapIndexes: mergedBitmap);
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

        int[] rowCounts = new int[totalChunks];
        for (int i = 0; i < totalChunks; i++)
        {
            rowCounts[i] = (int)mergedChunks[i].RowCount;
        }

        foreach (string column in existing.ColumnNames)
        {
            if (!delta.ColumnNames.Contains(column, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!existing.TryGetIndex(column, out BitmapColumnIndex? eCol)) continue;
            if (!delta.TryGetIndex(column, out BitmapColumnIndex? dCol)) continue;

            IReadOnlyDictionary<DataValue, byte[][]> eMaps = eCol.CompressedBitmaps;
            IReadOnlyDictionary<DataValue, byte[][]> dMaps = dCol.CompressedBitmaps;

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
