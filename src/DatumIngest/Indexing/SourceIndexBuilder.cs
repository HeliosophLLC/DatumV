using CardinalityEstimation;
using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Indexing.Bitmap;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.IO;
using DatumIngest.Indexing.Sorted;
using DatumIngest.Indexing.Bloom;

namespace DatumIngest.Indexing;

/// <summary>
/// Builds a <see cref="SourceIndex"/> in a single pass over all rows from a table provider.
/// Accumulates per-chunk column statistics (min, max, null count, cardinality) using
/// lightweight accumulators, and produces a complete index with schema, fingerprint, and
/// chunk directory.
/// </summary>
public sealed class SourceIndexBuilder
{
    private readonly int _chunkSize;
    private readonly IReadOnlySet<string>? _bloomColumns;
    private readonly IReadOnlySet<string>? _indexColumns;
    private readonly bool _bloomAllColumns;
    private readonly bool _indexAllColumns;
    private readonly bool _autoIndexColumns;
    private readonly bool _computeCardinality;

    /// <summary>
    /// Creates a builder with the specified chunk size and optional column-specific indexes.
    /// </summary>
    /// <param name="chunkSize">Number of rows per index chunk (default: 10,000).</param>
    /// <param name="bloomColumns">Column names to build bloom filters for, or <c>null</c> for no bloom filters.</param>
    /// <param name="indexColumns">Column names to build sorted value indexes for, or <c>null</c> for no sorted indexes.</param>
    /// <param name="computeCardinality">
    /// When <c>true</c> (default), maintains HyperLogLog cardinality estimates per column.
    /// When <c>false</c>, skips HLL updates entirely — reported cardinality is 0.
    /// </param>
    public SourceIndexBuilder(
        int chunkSize = IndexConstants.DefaultChunkSize,
        IReadOnlySet<string>? bloomColumns = null,
        IReadOnlySet<string>? indexColumns = null,
        bool computeCardinality = true)
    {
        _chunkSize = chunkSize;
        _bloomColumns = bloomColumns;
        _indexColumns = indexColumns;
        _bloomAllColumns = false;
        _indexAllColumns = false;
        _autoIndexColumns = false;
        _computeCardinality = computeCardinality;
    }

    /// <summary>
    /// Creates a builder that discovers columns from the data and optionally indexes all of them.
    /// </summary>
    /// <param name="bloomAllColumns">When <c>true</c>, builds bloom filters for every column discovered in the data.</param>
    /// <param name="indexAllColumns">When <c>true</c>, builds sorted value indexes for every column discovered in the data.</param>
    /// <param name="chunkSize">Number of rows per index chunk (default: 10,000).</param>
    /// <param name="autoIndexColumns">
    /// When <c>true</c> and <paramref name="indexAllColumns"/> is <c>false</c>,
    /// automatically selects compact columns for sorted indexing based on their data kind.
    /// </param>
    /// <param name="computeCardinality">
    /// When <c>true</c> (default), maintains HyperLogLog cardinality estimates per column.
    /// When <c>false</c>, skips HLL updates entirely — reported cardinality is 0.
    /// </param>
    public SourceIndexBuilder(
        bool bloomAllColumns,
        bool indexAllColumns,
        int chunkSize = IndexConstants.DefaultChunkSize,
        bool autoIndexColumns = false,
        bool computeCardinality = true)
    {
        _chunkSize = chunkSize;
        _bloomColumns = null;
        _indexColumns = null;
        _bloomAllColumns = bloomAllColumns;
        _indexAllColumns = indexAllColumns;
        _autoIndexColumns = autoIndexColumns;
        _computeCardinality = computeCardinality;
    }

    /// <summary>
    /// Creates an incremental index builder for co-generation during output writing (the <c>--with-index</c> workflow).
    /// Each row is observed but not consumed — the caller still owns the enumeration.
    /// Call <see cref="IncrementalIndexBuilder.AddRow"/> for each row, then <see cref="IncrementalIndexBuilder.Finalize"/> to produce the index.
    /// </summary>
    /// <summary>
    /// Creates an incremental index builder for co-generation during output writing (the <c>--with-index</c> workflow).
    /// Each row is observed but not consumed — the caller still owns the enumeration.
    /// Call <see cref="IncrementalIndexBuilder.AddRow"/> for each row, then <see cref="IncrementalIndexBuilder.Finalize"/> to produce the index.
    /// </summary>
    public IncrementalIndexBuilder CreateIncrementalBuilder(SourceFingerprint fingerprint)
    {
        return new IncrementalIndexBuilder(
            _chunkSize, fingerprint,
            _bloomColumns, _indexColumns,
            _bloomAllColumns, _indexAllColumns, _autoIndexColumns,
            _computeCardinality);
    }

    /// <summary>
    /// Resolves the effective column set for bloom or sorted indexes. When the "all columns"
    /// flag is set, returns a set of all column names from the discovered schema.
    /// Otherwise returns the explicitly specified column set.
    /// </summary>
    internal static IReadOnlySet<string>? ResolveEffectiveColumns(
        IReadOnlySet<string>? explicitColumns, bool allColumns, Schema schema)
    {
        if (allColumns)
        {
            HashSet<string> allColumnNames = new(StringComparer.OrdinalIgnoreCase);

            foreach (ColumnInfo column in schema.Columns)
            {
                allColumnNames.Add(column.Name);
            }

            return allColumnNames;
        }

        return explicitColumns;
    }

    /// <summary>
    /// Resolves the effective index column set from explicit columns, all-columns mode,
    /// or auto-index mode (in priority order).
    /// </summary>
    private IReadOnlySet<string>? ResolveEffectiveIndexColumns(Schema schema)
    {
        IReadOnlySet<string>? result;

        // Explicit column list takes highest priority.
        if (_indexColumns is not null && _indexColumns.Count > 0)
        {
            result = _indexColumns;
        }
        // Index-all overrides auto-index.
        else if (_indexAllColumns)
        {
            result = ResolveEffectiveColumns(null, true, schema);
        }
        // Auto-index selects compact columns by data kind.
        else if (_autoIndexColumns)
        {
            result = ResolveAutoIndexColumns(schema);
        }
        else
        {
            return null;
        }

        return result;
    }

    /// <summary>
    /// Selects columns for automatic sorted indexing based on their <see cref="DataKind"/>.
    /// Compact types (numeric, date/time, boolean, UUID) are always included. String
    /// columns are included tentatively but may be dropped later if values exceed 16 characters.
    /// Wide types (vectors, matrices, tensors, images, JSON, arrays, raw byte arrays) are excluded.
    /// </summary>
    internal static IReadOnlySet<string> ResolveAutoIndexColumns(Schema schema)
    {
        HashSet<string> columns = new(StringComparer.OrdinalIgnoreCase);

        foreach (ColumnInfo column in schema.Columns)
        {
            if (IsAutoIndexableKind(column.Kind))
            {
                columns.Add(column.Name);
            }
        }

        return columns;
    }

    /// <summary>
    /// Returns whether a column of the specified kind should be automatically indexed.
    /// </summary>
    internal static bool IsAutoIndexableKind(DataKind kind)
    {
        return kind is DataKind.Float32
            or DataKind.Float64
            or DataKind.UInt8
            or DataKind.Int8
            or DataKind.Int16
            or DataKind.UInt16
            or DataKind.Int32
            or DataKind.UInt32
            or DataKind.Int64
            or DataKind.UInt64
            or DataKind.Boolean
            or DataKind.Date
            or DataKind.DateTime
            or DataKind.Time
            or DataKind.Duration
            or DataKind.Uuid
            or DataKind.String; // String is tentatively included; dropped later if values exceed 16 chars.
    }

    private static Schema BuildSchemaFromRow(Row row)
    {
        List<ColumnInfo> columns = new();

        foreach (string name in row.ColumnNames)
        {
            DataValue value = row[name];
            columns.Add(new ColumnInfo(name, value.Kind, nullable: true));
        }

        return new Schema(columns);
    }

    internal static Dictionary<string, ChunkAccumulator> CreateAccumulators(Row row, bool computeCardinality = true)
    {
        Dictionary<string, ChunkAccumulator> accumulators = new(StringComparer.OrdinalIgnoreCase);

        foreach (string name in row.ColumnNames)
        {
            accumulators[name] = new ChunkAccumulator(computeCardinality);
        }

        return accumulators;
    }

    internal static Dictionary<string, ChunkAccumulator> CreateAccumulators(Schema schema, bool computeCardinality = true)
    {
        Dictionary<string, ChunkAccumulator> accumulators = new(StringComparer.OrdinalIgnoreCase);

        foreach (ColumnInfo column in schema.Columns)
        {
            accumulators[column.Name] = new ChunkAccumulator(computeCardinality);
        }

        return accumulators;
    }

    private static IndexChunk FinalizeChunk(
        long rowOffset,
        int rowCount,
        Dictionary<string, ChunkAccumulator> accumulators)
    {
        Dictionary<string, ChunkColumnStatistics> stats = new(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, ChunkAccumulator> entry in accumulators)
        {
            stats[entry.Key] = entry.Value.ToStatistics(rowCount);
        }

        return new IndexChunk(
            RowOffset: rowOffset,
            RowCount: rowCount,
            ColumnStatistics: stats);
    }

    /// <summary>
    /// Re-scans the source data to build indexes for columns whose hint-assigned index
    /// type failed during the primary pass (e.g. bitmap hint on a column that exceeded
    /// the cardinality threshold, or sorted hint on a column with strings too long).
    /// Uses auto-cascade (both bitmap + sorted accumulators) for the deferred columns, then
    /// deduplicates at the end. Requires a re-openable provider.
    /// </summary>
    private async Task<BitmapIndexSet?> RebuildDeferredColumnsAsync(
        IReadOnlyList<string> deferredColumns,
        TableDescriptor descriptor,
        ITableProvider provider,
        Schema schema,
        CancellationToken cancellationToken)
    {
        // Deferred rebuild covers bitmap-eligible columns only after the heap-backed
        // sorted-index set was removed. Sorted coverage for columns that were dropped
        // mid-scan is a follow-up: the primary spill writer is already in read-only
        // state by the time we get here, so adding deferred entries to it requires a
        // writer-reopen API that doesn't exist yet.
        HashSet<string> deferredSet = new(deferredColumns, StringComparer.OrdinalIgnoreCase);

        Dictionary<string, BitmapChunkAccumulator> bitmapAccumulators = new(StringComparer.OrdinalIgnoreCase);

        foreach (ColumnInfo column in schema.Columns)
        {
            if (deferredSet.Contains(column.Name) && IsAutoIndexableKind(column.Kind))
            {
                bitmapAccumulators[column.Name] = new BitmapChunkAccumulator();
            }
        }

        if (bitmapAccumulators.Count == 0)
        {
            return null;
        }

        foreach (BitmapChunkAccumulator accumulator in bitmapAccumulators.Values)
        {
            accumulator.BeginChunk(_chunkSize);
        }

        int rowsInChunk = 0;

        await foreach (RowBatch batch in provider.ScanAsync(requiredColumns: null, filterHint: null, targetArena: null, cancellationToken)
            .ConfigureAwait(false))
        {
            for (int batchRow = 0; batchRow < batch.Count; batchRow++)
            {
                Row row = batch[batchRow];

                foreach (string column in deferredColumns)
                {
                    if (!row.TryGetValue(column, out DataValue value))
                    {
                        continue;
                    }

                    if (bitmapAccumulators.TryGetValue(column, out BitmapChunkAccumulator? bitmapAccumulator)
                        && !bitmapAccumulator.IsAbandoned)
                    {
                        bitmapAccumulator.Add(value, rowsInChunk);
                    }
                }

                rowsInChunk++;

                if (rowsInChunk >= _chunkSize)
                {
                    foreach (BitmapChunkAccumulator accumulator in bitmapAccumulators.Values)
                    {
                        accumulator.FinalizeChunk(rowsInChunk);
                        accumulator.BeginChunk(_chunkSize);
                    }

                    rowsInChunk = 0;
                }
            }

            batch.Return();
        }

        if (rowsInChunk > 0)
        {
            foreach (BitmapChunkAccumulator accumulator in bitmapAccumulators.Values)
            {
                accumulator.FinalizeChunk(rowsInChunk);
            }
        }

        return BuildBitmapIndexSet(bitmapAccumulators);
    }

    /// <summary>
    /// Merges two <see cref="BitmapIndexSet"/> instances into one. Returns <c>null</c> if
    /// both inputs are <c>null</c>.
    /// </summary>
    private static BitmapIndexSet? MergeBitmapIndexSets(BitmapIndexSet? primary, BitmapIndexSet? deferred)
    {
        if (deferred is null)
        {
            return primary;
        }

        if (primary is null)
        {
            return deferred;
        }

        Dictionary<string, BitmapColumnIndex> merged = new(StringComparer.OrdinalIgnoreCase);

        foreach (string column in primary.ColumnNames)
        {
            if (primary.TryGetIndex(column, out BitmapColumnIndex? index))
            {
                merged[column] = index;
            }
        }

        foreach (string column in deferred.ColumnNames)
        {
            if (deferred.TryGetIndex(column, out BitmapColumnIndex? index))
            {
                merged[column] = index;
            }
        }

        return new BitmapIndexSet(merged);
    }

    /// <summary>
    /// Creates bloom filters for the specified columns, sized for the chunk capacity.
    /// Returns <c>null</c> if no bloom columns are specified.
    /// </summary>
    internal static Dictionary<string, BloomFilter>? CreateBloomFilters(
        IReadOnlySet<string>? bloomColumns, int expectedElements)
    {
        if (bloomColumns is null || bloomColumns.Count == 0)
        {
            return null;
        }

        Dictionary<string, BloomFilter> filters = new(StringComparer.OrdinalIgnoreCase);

        foreach (string column in bloomColumns)
        {
            filters[column] = new BloomFilter(expectedElements);
        }

        return filters;
    }

    /// <summary>
    /// Assembles per-chunk bloom filter dictionaries into a <see cref="BloomFilterSet"/>.
    /// </summary>
    internal static BloomFilterSet BuildBloomFilterSet(
        List<Dictionary<string, BloomFilter>> chunkBloomFilters, int chunkCount)
    {
        // Collect all column names across chunks.
        HashSet<string> columnNames = new(StringComparer.OrdinalIgnoreCase);

        foreach (Dictionary<string, BloomFilter> chunkFilters in chunkBloomFilters)
        {
            foreach (string name in chunkFilters.Keys)
            {
                columnNames.Add(name);
            }
        }

        Dictionary<string, BloomFilter[]> filters = new(StringComparer.OrdinalIgnoreCase);

        foreach (string columnName in columnNames)
        {
            BloomFilter[] columnFilters = new BloomFilter[chunkCount];

            for (int i = 0; i < chunkBloomFilters.Count && i < chunkCount; i++)
            {
                if (chunkBloomFilters[i].TryGetValue(columnName, out BloomFilter? filter))
                {
                    columnFilters[i] = filter;
                }
                else
                {
                    // Create an empty filter for chunks that didn't see this column.
                    columnFilters[i] = new BloomFilter(1);
                }
            }

            filters[columnName] = columnFilters;
        }

        return new BloomFilterSet(filters, chunkCount);
    }

    /// <summary>
    /// Creates a <see cref="BitmapChunkAccumulator"/> for each bitmap-eligible column in
    /// the schema. Returns <c>null</c> if no columns are eligible.
    /// </summary>
    internal static Dictionary<string, BitmapChunkAccumulator>? CreateBitmapAccumulators(Schema schema)
    {
        Dictionary<string, BitmapChunkAccumulator>? accumulators = null;

        foreach (ColumnInfo column in schema.Columns)
        {
            if (IsAutoIndexableKind(column.Kind) && column.IsArray == false)
            {
                accumulators ??= new Dictionary<string, BitmapChunkAccumulator>(StringComparer.OrdinalIgnoreCase);
                accumulators[column.Name] = new BitmapChunkAccumulator();
            }
        }

        return accumulators;
    }

    /// <summary>
    /// Builds a <see cref="BitmapIndexSet"/> from per-column accumulators, keeping only
    /// columns whose cardinality stayed within the bitmap threshold (i.e. not abandoned).
    /// Returns <c>null</c> if no columns qualify.
    /// </summary>
    internal static BitmapIndexSet? BuildBitmapIndexSet(
        Dictionary<string, BitmapChunkAccumulator> accumulators)
    {
        Dictionary<string, BitmapColumnIndex>? indexes = null;

        foreach (KeyValuePair<string, BitmapChunkAccumulator> entry in accumulators)
        {
            BitmapColumnIndex? columnIndex = entry.Value.Build();

            if (columnIndex is not null)
            {
                indexes ??= new Dictionary<string, BitmapColumnIndex>(StringComparer.OrdinalIgnoreCase);
                indexes[entry.Key] = columnIndex;
            }
        }

        return indexes is not null ? new BitmapIndexSet(indexes) : null;
    }
}
