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

    /// <summary>
    /// Creates a builder with the specified chunk size and optional column-specific indexes.
    /// </summary>
    /// <param name="chunkSize">Number of rows per index chunk (default: 10,000).</param>
    /// <param name="bloomColumns">Column names to build bloom filters for, or <c>null</c> for no bloom filters.</param>
    /// <param name="indexColumns">Column names to build sorted value indexes for, or <c>null</c> for no sorted indexes.</param>
    public SourceIndexBuilder(
        int chunkSize = IndexConstants.DefaultChunkSize,
        IReadOnlySet<string>? bloomColumns = null,
        IReadOnlySet<string>? indexColumns = null)
    {
        _chunkSize = chunkSize;
        _bloomColumns = bloomColumns;
        _indexColumns = indexColumns;
        _bloomAllColumns = false;
        _indexAllColumns = false;
        _autoIndexColumns = false;
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
    public SourceIndexBuilder(
        bool bloomAllColumns,
        bool indexAllColumns,
        int chunkSize = IndexConstants.DefaultChunkSize,
        bool autoIndexColumns = false)
    {
        _chunkSize = chunkSize;
        _bloomColumns = null;
        _indexColumns = null;
        _bloomAllColumns = bloomAllColumns;
        _indexAllColumns = indexAllColumns;
        _autoIndexColumns = autoIndexColumns;
    }

    /// <summary>
    /// Builds an index for the specified table by streaming all rows.
    /// </summary>
    /// <param name="descriptor">Table descriptor identifying the source.</param>
    /// <param name="provider">The table provider to read rows from.</param>
    /// <param name="sourceStream">
    /// Seekable stream over the source file for fingerprint computation,
    /// or <c>null</c> if fingerprinting is not possible (e.g. stream not available).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A fully constructed source index.</returns>
    public async Task<SourceIndex> BuildAsync(
        TableDescriptor descriptor,
        ITableProvider provider,
        Stream? sourceStream,
        CancellationToken cancellationToken)
    {
        return await BuildAsync(descriptor, provider, sourceStream, fingerprint: null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Builds an index for the specified table by streaming all rows, using an
    /// externally-computed fingerprint to avoid redundant source file hashing.
    /// </summary>
    /// <param name="descriptor">Table descriptor identifying the source.</param>
    /// <param name="provider">The table provider to read rows from.</param>
    /// <param name="sourceStream">
    /// Seekable stream over the source file for fingerprint computation when
    /// <paramref name="fingerprint"/> is <c>null</c>, or <c>null</c> if neither
    /// fingerprinting source is available.
    /// </param>
    /// <param name="fingerprint">
    /// Pre-computed fingerprint to use instead of computing from the stream.
    /// When provided, <paramref name="sourceStream"/> is not read for fingerprinting.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A fully constructed source index.</returns>
    public async Task<SourceIndex> BuildAsync(
        TableDescriptor descriptor,
        ITableProvider provider,
        Stream? sourceStream,
        SourceFingerprint? fingerprint,
        CancellationToken cancellationToken)
    {
        fingerprint ??= sourceStream is not null
            ? await SourceFingerprint.ComputeAsync(sourceStream, cancellationToken).ConfigureAwait(false)
            : new SourceFingerprint(0, Array.Empty<byte>());

        Schema? schema = null;
        List<IndexChunk> chunks = new();
        long totalRowCount = 0;
        long currentChunkRowOffset = 0;

        Dictionary<string, ChunkAccumulator> currentAccumulators = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, BloomFilter>? currentBloomFilters = null;
        bool needsBloom = _bloomAllColumns || (_bloomColumns is not null && _bloomColumns.Count > 0);
        bool needsIndex = _indexAllColumns || (_indexColumns is not null && _indexColumns.Count > 0) || _autoIndexColumns;
        List<Dictionary<string, BloomFilter>>? allChunkBloomFilters = needsBloom ? new() : null;
        SortedIndexSpillWriter? spillWriter = needsIndex ? new SortedIndexSpillWriter() : null;
        IReadOnlySet<string>? effectiveBloomColumns = _bloomColumns;
        int rowsInCurrentChunk = 0;
        int currentChunkIndex = 0;

        Dictionary<string, BitmapChunkAccumulator>? bitmapAccumulators = null;

        string[]? resolvedColumnNames = null;
        ChunkAccumulator?[]? ordinalAccumulators = null;
        BloomFilter?[]? ordinalBloomFilters = null;
        List<ValueIndexEntry>?[]? ordinalSpillEntries = null;
        BitmapChunkAccumulator?[]? ordinalBitmapAccumulators = null;

        List<string>? deferredReindexColumns = null;

        try
        {
            await foreach (RowBatch batch in provider.ScanAsync(descriptor, requiredColumns: null, filterHint: null, cancellationToken)
                .ConfigureAwait(false))
            {
                for (int i = 0; i < batch.Count; i++)
                {
                    Row row = batch[i];
                    if (schema is null)
                {
                    schema = BuildSchemaFromRow(row);
                    currentAccumulators = CreateAccumulators(row);
                    effectiveBloomColumns = ResolveEffectiveColumns(_bloomColumns, _bloomAllColumns, schema);
                    IReadOnlySet<string>? effectiveIndexColumns = ResolveEffectiveIndexColumns(schema);
                    currentBloomFilters = CreateBloomFilters(effectiveBloomColumns, _chunkSize);

                    if (spillWriter is not null && effectiveIndexColumns is not null)
                    {
                        spillWriter.Initialize(effectiveIndexColumns);
                    }

                    int columnCount = row.FieldCount;
                    resolvedColumnNames = new string[columnCount];
                    ordinalAccumulators = new ChunkAccumulator?[columnCount];
                    ordinalBloomFilters = currentBloomFilters is not null ? new BloomFilter?[columnCount] : null;
                    ordinalSpillEntries = spillWriter is not null ? new List<ValueIndexEntry>?[columnCount] : null;

                    bitmapAccumulators = CreateBitmapAccumulators(schema);
                    ordinalBitmapAccumulators = bitmapAccumulators is not null
                        ? new BitmapChunkAccumulator?[columnCount]
                        : null;

                    // Begin the first chunk for bitmap accumulators.
                    if (bitmapAccumulators is not null)
                    {
                        foreach (BitmapChunkAccumulator bitmapAccumulator in bitmapAccumulators.Values)
                        {
                            bitmapAccumulator.BeginChunk(_chunkSize);
                        }
                    }

                    for (int ordinal = 0; ordinal < columnCount; ordinal++)
                    {
                        string name = row.ColumnNames[ordinal];
                        resolvedColumnNames[ordinal] = name;
                        currentAccumulators.TryGetValue(name, out ChunkAccumulator? acc);
                        ordinalAccumulators[ordinal] = acc;

                        if (ordinalBloomFilters is not null && currentBloomFilters is not null)
                        {
                            currentBloomFilters.TryGetValue(name, out BloomFilter? bloom);
                            ordinalBloomFilters[ordinal] = bloom;
                        }

                        if (ordinalSpillEntries is not null && spillWriter is not null)
                        {
                            ordinalSpillEntries[ordinal] = spillWriter.GetEntryListOrNull(name);
                        }

                        if (ordinalBitmapAccumulators is not null && bitmapAccumulators is not null)
                        {
                            bitmapAccumulators.TryGetValue(name, out BitmapChunkAccumulator? bitmapAcc);
                            ordinalBitmapAccumulators[ordinal] = bitmapAcc;
                        }
                    }
                }

                int fieldCount = row.FieldCount;

                for (int ordinal = 0; ordinal < fieldCount; ordinal++)
                {
                    DataValue value = row[ordinal];

                    ChunkAccumulator? accumulator = ordinalAccumulators![ordinal];
                    if (accumulator is not null)
                    {
                        accumulator.Add(value);
                    }

                    if (ordinalBloomFilters is not null)
                    {
                        BloomFilter? bloom = ordinalBloomFilters[ordinal];
                        if (bloom is not null)
                        {
                            bloom.Add(value);
                        }
                    }

                    if (ordinalSpillEntries is not null && !value.IsNull)
                    {
                        List<ValueIndexEntry>? entries = ordinalSpillEntries[ordinal];
                        if (entries is not null)
                        {
                            if (value.Kind == DataKind.String
                                && value.AsString().Length > SortedIndexSpillWriter.AutoIndexMaxStringLength)
                            {
                                string droppedName = resolvedColumnNames![ordinal];
                                spillWriter!.DropColumn(droppedName);
                                ordinalSpillEntries[ordinal] = null;
                                deferredReindexColumns ??= new();
                                deferredReindexColumns.Add(droppedName);
                            }
                            else
                            {
                                entries.Add(new ValueIndexEntry(value, currentChunkIndex, rowsInCurrentChunk));
                            }
                        }
                    }

                    if (ordinalBitmapAccumulators is not null)
                    {
                        BitmapChunkAccumulator? bitmapAccumulator = ordinalBitmapAccumulators[ordinal];
                        if (bitmapAccumulator is not null)
                        {
                            bitmapAccumulator.Add(value, rowsInCurrentChunk);

                            // Remove abandoned accumulators from the per-row path
                            // so high-cardinality columns do not pay method-call overhead
                            // for every remaining row.
                            if (bitmapAccumulator.IsAbandoned)
                            {
                                ordinalBitmapAccumulators[ordinal] = null;
                            }
                        }
                    }
                }

                rowsInCurrentChunk++;
                totalRowCount++;

                if (rowsInCurrentChunk >= _chunkSize)
                {
                    chunks.Add(FinalizeChunk(currentChunkRowOffset, rowsInCurrentChunk, currentAccumulators));
                    if (allChunkBloomFilters is not null && currentBloomFilters is not null)
                    {
                        allChunkBloomFilters.Add(currentBloomFilters);
                    }

                    spillWriter?.FlushChunk();

                    // Finalize bitmap accumulators for this chunk and begin the next.
                    if (bitmapAccumulators is not null)
                    {
                        foreach (BitmapChunkAccumulator bitmapAcc in bitmapAccumulators.Values)
                        {
                            bitmapAcc.FinalizeChunk(rowsInCurrentChunk);
                        }
                    }

                    currentChunkRowOffset = totalRowCount;
                    rowsInCurrentChunk = 0;
                    currentChunkIndex++;
                    currentAccumulators = CreateAccumulators(schema);
                    currentBloomFilters = CreateBloomFilters(effectiveBloomColumns, _chunkSize);

                    // Begin the next chunk for bitmap accumulators.
                    if (bitmapAccumulators is not null)
                    {
                        foreach (BitmapChunkAccumulator bitmapAcc in bitmapAccumulators.Values)
                        {
                            bitmapAcc.BeginChunk(_chunkSize);
                        }
                    }

                    // Rebuild ordinal arrays to point at the new chunk's accumulators/blooms.
                    int columnCount = resolvedColumnNames!.Length;
                    ordinalAccumulators = new ChunkAccumulator?[columnCount];
                    ordinalBloomFilters = currentBloomFilters is not null ? new BloomFilter?[columnCount] : null;

                    for (int ordinal = 0; ordinal < columnCount; ordinal++)
                    {
                        string name = resolvedColumnNames[ordinal];
                        currentAccumulators.TryGetValue(name, out ChunkAccumulator? acc);
                        ordinalAccumulators[ordinal] = acc;

                        if (ordinalBloomFilters is not null && currentBloomFilters is not null)
                        {
                            currentBloomFilters.TryGetValue(name, out BloomFilter? bloom);
                            ordinalBloomFilters[ordinal] = bloom;
                        }
                    }
                }
                }

                batch.Return();
            }

            // Finalize the last partial chunk.
            if (rowsInCurrentChunk > 0)
            {
                chunks.Add(FinalizeChunk(currentChunkRowOffset, rowsInCurrentChunk, currentAccumulators));
                if (allChunkBloomFilters is not null && currentBloomFilters is not null)
                {
                    allChunkBloomFilters.Add(currentBloomFilters);
                }

                if (bitmapAccumulators is not null)
                {
                    foreach (BitmapChunkAccumulator bitmapAcc in bitmapAccumulators.Values)
                    {
                        bitmapAcc.FinalizeChunk(rowsInCurrentChunk);
                    }
                }
            }

            schema ??= new Schema(new[] { new ColumnInfo("empty", DataKind.String, nullable: true) });

            BloomFilterSet? bloomFilterSet = allChunkBloomFilters is not null
                ? BuildBloomFilterSet(allChunkBloomFilters, chunks.Count)
                : null;

            BitmapIndexSet? bitmapIndexSet = bitmapAccumulators is not null
                ? BuildBitmapIndexSet(bitmapAccumulators)
                : null;

            // When sorted columns were auto-selected (not explicitly requested), exclude
            // columns that have a successful bitmap index to avoid duplicate coverage.
            // Explicit indexColumns/indexAllColumns are respected as-is.
            bool sortedColumnsAreAutoSelected = _autoIndexColumns
                && !_indexAllColumns
                && (_indexColumns is null || _indexColumns.Count == 0);

            if (sortedColumnsAreAutoSelected && bitmapIndexSet is not null && spillWriter is not null)
            {
                foreach (string bitmapColumn in bitmapIndexSet.ColumnNames)
                {
                    spillWriter.DropColumn(bitmapColumn);
                }
            }

            // Second pass: rebuild indexes for columns whose hints failed during the
            // primary scan. Sorted-index entries remain inside the spill writer until
            // UnifiedIndexWriter streams them; we only merge bitmap sets here.
            if (deferredReindexColumns is { Count: > 0 })
            {
                BitmapIndexSet? deferredBitmaps =
                    await RebuildDeferredColumnsAsync(
                        deferredReindexColumns, descriptor, provider, schema, cancellationToken)
                        .ConfigureAwait(false);

                bitmapIndexSet = MergeBitmapIndexSets(bitmapIndexSet, deferredBitmaps);
            }

            IndexSchema indexSchema = new(schema, totalRowCount);
            return new SourceIndex(fingerprint, indexSchema, chunks, bloomFilterSet,
                bPlusTreeIndexes: null, bitmapIndexes: bitmapIndexSet);
        }
        finally
        {
            spillWriter?.Dispose();
        }
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
            _bloomAllColumns, _indexAllColumns, _autoIndexColumns);
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

    private static Dictionary<string, ChunkAccumulator> CreateAccumulators(Row row)
    {
        Dictionary<string, ChunkAccumulator> accumulators = new(StringComparer.OrdinalIgnoreCase);

        foreach (string name in row.ColumnNames)
        {
            accumulators[name] = new ChunkAccumulator();
        }

        return accumulators;
    }

    private static Dictionary<string, ChunkAccumulator> CreateAccumulators(Schema schema)
    {
        Dictionary<string, ChunkAccumulator> accumulators = new(StringComparer.OrdinalIgnoreCase);

        foreach (ColumnInfo column in schema.Columns)
        {
            accumulators[column.Name] = new ChunkAccumulator();
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

        await foreach (RowBatch batch in provider.ScanAsync(descriptor, requiredColumns: null, filterHint: null, cancellationToken)
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
    internal static Dictionary<string, BitmapChunkAccumulator>? CreateBitmapAccumulators(
        Schema schema)
    {
        Dictionary<string, BitmapChunkAccumulator>? accumulators = null;

        foreach (ColumnInfo column in schema.Columns)
        {
            if (IsAutoIndexableKind(column.Kind))
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

    /// <summary>
    /// Lightweight per-column accumulator that tracks min, max, null count, and
    /// estimated cardinality for a single chunk. Much lighter than the full
    /// <see cref="Statistics.StatisticsCollector"/> — no histograms, percentiles,
    /// or interaction analysis.
    /// </summary>
    private sealed class ChunkAccumulator
    {
        private DataValue? _minimum;
        private DataValue? _maximum;
        private long _nullCount;
        private readonly CardinalityEstimator _cardinality = new();

        public void Add(DataValue value)
        {
            if (value.IsNull)
            {
                _nullCount++;
                return;
            }

            UpdateMinMax(value);
            AddToCardinality(value);
        }

        public ChunkColumnStatistics ToStatistics(long rowCount)
        {
            return new ChunkColumnStatistics(
                Minimum: _minimum,
                Maximum: _maximum,
                NullCount: _nullCount,
                RowCount: rowCount,
                EstimatedCardinality: (long)_cardinality.Count());
        }

        private void UpdateMinMax(DataValue value)
        {
            // Only track min/max for comparable types.
            if (!IsComparableKind(value.Kind))
            {
                return;
            }

            if (_minimum is null || CompareValues(value, _minimum.Value) < 0)
            {
                _minimum = value;
            }

            if (_maximum is null || CompareValues(value, _maximum.Value) > 0)
            {
                _maximum = value;
            }
        }

        private void AddToCardinality(DataValue value)
        {
            switch (value.Kind)
            {
                case DataKind.Float32:
                    _cardinality.Add(value.AsFloat32());
                    break;
                case DataKind.Float64:
                    _cardinality.Add(value.AsFloat64());
                    break;
                case DataKind.UInt8:
                    _cardinality.Add((int)value.AsUInt8());
                    break;
                case DataKind.Int8:
                    _cardinality.Add((int)value.AsInt8());
                    break;
                case DataKind.Int16:
                    _cardinality.Add((int)value.AsInt16());
                    break;
                case DataKind.UInt16:
                    _cardinality.Add((int)value.AsUInt16());
                    break;
                case DataKind.Int32:
                    _cardinality.Add(value.AsInt32());
                    break;
                case DataKind.UInt32:
                    _cardinality.Add((long)value.AsUInt32());
                    break;
                case DataKind.Int64:
                    _cardinality.Add(value.AsInt64());
                    break;
                case DataKind.UInt64:
                    _cardinality.Add((long)value.AsUInt64());
                    break;
                case DataKind.String:
                    _cardinality.Add(value.AsString());
                    break;
                case DataKind.Date:
                    _cardinality.Add(value.AsDate().DayNumber);
                    break;
                case DataKind.DateTime:
                    _cardinality.Add(value.AsDateTime().ToUnixTimeMilliseconds());
                    break;
                case DataKind.JsonValue:
                    _cardinality.Add(value.AsJsonValue());
                    break;
                default:
                    _cardinality.Add(value.GetHashCode());
                    break;
            }
        }

        private static bool IsComparableKind(DataKind kind)
        {
            return kind is DataKind.Float32 or DataKind.Float64
                or DataKind.UInt8 or DataKind.Int8
                or DataKind.Int16 or DataKind.UInt16
                or DataKind.Int32 or DataKind.UInt32
                or DataKind.Int64 or DataKind.UInt64
                or DataKind.String or DataKind.Date or DataKind.DateTime;
        }

        private static int CompareValues(DataValue left, DataValue right) =>
            DataValueComparer.Compare(left, right);
    }
}
