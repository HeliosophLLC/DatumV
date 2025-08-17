using CardinalityEstimation;
using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Indexing.Bitmap;
using DatumIngest.Model;

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
    private readonly int? _maxIndexedColumns;

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
    /// <param name="maxIndexedColumns">
    /// Maximum number of columns to include in the sorted index. When not <c>null</c>, only
    /// the first N eligible columns (in schema order) are indexed. <c>null</c> means no limit.
    /// </param>
    public SourceIndexBuilder(
        bool bloomAllColumns,
        bool indexAllColumns,
        int chunkSize = IndexConstants.DefaultChunkSize,
        bool autoIndexColumns = false,
        int? maxIndexedColumns = null)
    {
        _chunkSize = chunkSize;
        _bloomColumns = null;
        _indexColumns = null;
        _bloomAllColumns = bloomAllColumns;
        _indexAllColumns = indexAllColumns;
        _autoIndexColumns = autoIndexColumns;
        _maxIndexedColumns = maxIndexedColumns;
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

        IReadOnlyList<ChunkByteRange>? chunkByteRanges = null;
        if (provider is IChunkMeasuringProvider measuringProvider)
        {
            chunkByteRanges = await measuringProvider.MeasureChunkByteRangesAsync(
                descriptor, _chunkSize, cancellationToken).ConfigureAwait(false);
        }

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

        try
        {
            await foreach (Row row in provider.OpenAsync(descriptor, requiredColumns: null, cancellationToken)
                .ConfigureAwait(false))
            {
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
                                spillWriter!.DropColumn(resolvedColumnNames![ordinal]);
                                ordinalSpillEntries[ordinal] = null;
                            }
                            else
                            {
                                entries.Add(new ValueIndexEntry(value, currentChunkIndex, rowsInCurrentChunk));
                            }
                        }
                    }

                    if (ordinalBitmapAccumulators is not null)
                    {
                        BitmapChunkAccumulator? bitmapAcc = ordinalBitmapAccumulators[ordinal];
                        if (bitmapAcc is not null)
                        {
                            bitmapAcc.Add(value, rowsInCurrentChunk);
                        }
                    }
                }

                rowsInCurrentChunk++;
                totalRowCount++;

                if (rowsInCurrentChunk >= _chunkSize)
                {
                    (long byteOffset, long byteLength) = GetByteRange(chunkByteRanges, currentChunkIndex);
                    chunks.Add(FinalizeChunk(currentChunkRowOffset, rowsInCurrentChunk, currentAccumulators, byteOffset, byteLength));
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

            // Finalize the last partial chunk.
            if (rowsInCurrentChunk > 0)
            {
                (long byteOffset, long byteLength) = GetByteRange(chunkByteRanges, currentChunkIndex);
                chunks.Add(FinalizeChunk(currentChunkRowOffset, rowsInCurrentChunk, currentAccumulators, byteOffset, byteLength));
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

            SortedValueIndexSet? sortedIndexSet = spillWriter?.BuildSortedValueIndexSet();

            IndexSchema indexSchema = new(schema, totalRowCount);
            return new SourceIndex(fingerprint, indexSchema, chunks, bloomFilterSet, sortedIndexSet,
                zipDirectory: null, bPlusTreeIndexes: null, bitmapIndexes: bitmapIndexSet);
        }
        finally
        {
            spillWriter?.Dispose();
        }
    }

    /// <summary>
    /// Builds a complete <see cref="SourceIndexSet"/> for one or more tables that share the
    /// same source file. Computes the fingerprint once and builds each table's index in turn.
    /// </summary>
    /// <param name="tables">
    /// One or more (descriptor, provider) pairs representing the logical tables within a single
    /// source file. Each entry produces one keyed index in the resulting set.
    /// </param>
    /// <param name="sourceStream">
    /// Seekable stream over the source file for fingerprint computation,
    /// or <c>null</c> if fingerprinting is not possible.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="SourceIndexSet"/> containing all per-table indexes and a shared fingerprint.</returns>
    public async Task<SourceIndexSet> BuildSetAsync(
        IReadOnlyList<(TableDescriptor Descriptor, ITableProvider Provider)> tables,
        Stream? sourceStream,
        CancellationToken cancellationToken)
    {
        return await BuildSetAsync(tables, sourceStream, fingerprint: null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a complete <see cref="SourceIndexSet"/> for one or more tables that share the
    /// same source file, using an externally-computed fingerprint.
    /// </summary>
    /// <param name="tables">
    /// One or more (descriptor, provider) pairs representing the logical tables within a single
    /// source file. Each entry produces one keyed index in the resulting set.
    /// </param>
    /// <param name="sourceStream">
    /// Seekable stream over the source file, used only when <paramref name="fingerprint"/> is
    /// <c>null</c>. Otherwise ignored.
    /// </param>
    /// <param name="fingerprint">
    /// Pre-computed fingerprint, or <c>null</c> to compute from <paramref name="sourceStream"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="SourceIndexSet"/> containing all per-table indexes and a shared fingerprint.</returns>
    public async Task<SourceIndexSet> BuildSetAsync(
        IReadOnlyList<(TableDescriptor Descriptor, ITableProvider Provider)> tables,
        Stream? sourceStream,
        SourceFingerprint? fingerprint,
        CancellationToken cancellationToken)
    {
        fingerprint ??= sourceStream is not null
            ? await SourceFingerprint.ComputeAsync(sourceStream, cancellationToken).ConfigureAwait(false)
            : new SourceFingerprint(0, Array.Empty<byte>());

        Dictionary<string, SourceIndex> tableIndexes = new();

        foreach ((TableDescriptor descriptor, ITableProvider provider) in tables)
        {
            SourceIndex index = await BuildAsync(
                descriptor, provider, sourceStream: null, fingerprint, cancellationToken)
                .ConfigureAwait(false);

            string sidecarTableName = GetSidecarTableName(descriptor);
            tableIndexes[sidecarTableName] = index;
        }

        return new SourceIndexSet(fingerprint, tableIndexes);
    }

    private static string GetSidecarTableName(TableDescriptor descriptor)
    {
        if (descriptor.Options.ContainsKey(TableCatalog.SubTableKeyOption))
        {
            return descriptor.Name;
        }

        return FileFormatDetector.DeriveTableName(descriptor.FilePath);
    }

    /// <summary>
    /// Creates an incremental index builder for co-generation during output writing (the <c>--with-index</c> workflow).
    /// Each row is observed but not consumed — the caller still owns the enumeration.
    /// Call <see cref="IncrementalIndexBuilder.AddRow"/> for each row, then <see cref="IncrementalIndexBuilder.Finalize"/> to produce the index.
    /// </summary>
    /// <summary>
    /// Creates an incremental index builder for co-generation during output writing (the <c>--with-index</c> workflow).
    /// Each row is observed but not consumed — the caller still owns the enumeration.
    /// Call <see cref="IncrementalIndexBuilder.AddRow"/> for each row, then <see cref="IncrementalIndexBuilder.Finalize"/> to produce the index.
    /// </summary>
    public IncrementalIndexBuilder CreateIncrementalBuilder(SourceFingerprint fingerprint)
    {
        return new IncrementalIndexBuilder(_chunkSize, fingerprint, _bloomColumns, _indexColumns, _bloomAllColumns, _indexAllColumns, _autoIndexColumns, _maxIndexedColumns);
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
    /// Resolves the effective index column set considering explicit columns,
    /// all-columns mode, and auto-index mode (in priority order).
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

        return ApplyMaxIndexedColumns(result);
    }

    /// <summary>
    /// Trims a column set to at most <see cref="_maxIndexedColumns"/> entries when set.
    /// Preserves schema ordering (which matches the insertion order from resolution).
    /// </summary>
    private IReadOnlySet<string>? ApplyMaxIndexedColumns(IReadOnlySet<string>? columns)
    {
        if (columns is null || _maxIndexedColumns is not int max || columns.Count <= max)
        {
            return columns;
        }

        HashSet<string> trimmed = new(max, StringComparer.OrdinalIgnoreCase);

        foreach (string column in columns)
        {
            trimmed.Add(column);

            if (trimmed.Count >= max)
            {
                break;
            }
        }

        return trimmed;
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
        return kind is DataKind.Scalar
            or DataKind.UInt8
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
        Dictionary<string, ChunkAccumulator> accumulators,
        long sourceByteOffset = -1,
        long sourceByteLength = -1)
    {
        Dictionary<string, ChunkColumnStatistics> stats = new(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, ChunkAccumulator> entry in accumulators)
        {
            stats[entry.Key] = entry.Value.ToStatistics(rowCount);
        }

        return new IndexChunk(
            RowOffset: rowOffset,
            RowCount: rowCount,
            SourceByteOffset: sourceByteOffset,
            SourceByteLength: sourceByteLength,
            ColumnStatistics: stats);
    }

    /// <summary>
    /// Retrieves the byte offset and length for the specified chunk index from
    /// pre-scanned byte ranges, or returns <c>(-1, -1)</c> when no byte ranges
    /// are available.
    /// </summary>
    private static (long ByteOffset, long ByteLength) GetByteRange(
        IReadOnlyList<ChunkByteRange>? chunkByteRanges, int chunkIndex)
    {
        if (chunkByteRanges is not null && chunkIndex < chunkByteRanges.Count)
        {
            ChunkByteRange range = chunkByteRanges[chunkIndex];
            return (range.ByteOffset, range.ByteLength);
        }

        return (-1, -1);
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
    /// Creates a <see cref="BitmapChunkAccumulator"/> for each auto-indexable column
    /// in the schema. Returns <c>null</c> if no columns are eligible.
    /// </summary>
    internal static Dictionary<string, BitmapChunkAccumulator>? CreateBitmapAccumulators(Schema schema)
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
    /// <see cref="Statistics.StatisticsCollector"/> — no histograms, percentiles,
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

            if (_minimum is null || CompareValues(value, _minimum) < 0)
            {
                _minimum = value;
            }

            if (_maximum is null || CompareValues(value, _maximum) > 0)
            {
                _maximum = value;
            }
        }

        private void AddToCardinality(DataValue value)
        {
            switch (value.Kind)
            {
                case DataKind.Scalar:
                    _cardinality.Add(value.AsScalar());
                    break;
                case DataKind.UInt8:
                    _cardinality.Add((int)value.AsUInt8());
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
            return kind is DataKind.Scalar or DataKind.UInt8 or DataKind.String
                or DataKind.Date or DataKind.DateTime;
        }

        /// <summary>
        /// Reuses the same comparison semantics as
        /// <see cref="StatisticsPredicateEvaluator.CompareValues"/>.
        /// </summary>
        private static int CompareValues(DataValue left, DataValue right)
        {
            return StatisticsPredicateEvaluator.CompareValues(left, right);
        }
    }
}

/// <summary>
/// Incrementally builds an index as rows stream through during output writing.
/// Created by <see cref="SourceIndexBuilder.CreateIncrementalBuilder"/>.
/// </summary>
public sealed class IncrementalIndexBuilder : IDisposable
{
    private readonly int _chunkSize;
    private readonly SourceFingerprint _fingerprint;
    private readonly IReadOnlySet<string>? _bloomColumns;
    private readonly IReadOnlySet<string>? _indexColumns;
    private readonly bool _bloomAllColumns;
    private readonly bool _indexAllColumns;
    private readonly bool _autoIndexColumns;
    private readonly int? _maxIndexedColumns;
    private IReadOnlySet<string>? _effectiveBloomColumns;
    private Schema? _schema;
    private readonly List<IndexChunk> _chunks = new();
    private Dictionary<string, SourceIndexBuilder_ChunkAccumulatorProxy> _currentAccumulators = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, BloomFilter>? _currentBloomFilters;
    private readonly List<Dictionary<string, BloomFilter>>? _allChunkBloomFilters;
    private SortedIndexSpillWriter? _spillWriter;

    /// <summary>Ordinal-indexed column names, resolved once from the first row's schema.</summary>
    private string[]? _resolvedColumnNames;

    /// <summary>Ordinal-indexed accumulators, rebuilt at each chunk boundary.</summary>
    private SourceIndexBuilder_ChunkAccumulatorProxy?[]? _ordinalAccumulators;

    /// <summary>Ordinal-indexed bloom filters, rebuilt at each chunk boundary.</summary>
    private BloomFilter?[]? _ordinalBloomFilters;

    /// <summary>
    /// Ordinal-indexed spill entry lists, resolved once from the spill writer. List
    /// references are stable across chunks. Entries are nulled out if a column is dropped.
    /// </summary>
    private List<ValueIndexEntry>?[]? _ordinalSpillEntries;

    /// <summary>Per-column bitmap accumulators, created on first schema discovery.</summary>
    private Dictionary<string, BitmapChunkAccumulator>? _bitmapAccumulators;

    /// <summary>Ordinal-indexed bitmap accumulators, resolved once from the first row's schema.</summary>
    private BitmapChunkAccumulator?[]? _ordinalBitmapAccumulators;

    private int _rowsInCurrentChunk;
    private long _totalRowCount;
    private long _currentChunkRowOffset;
    private int _currentChunkIndex;
    private bool _disposed;

    internal IncrementalIndexBuilder(
        int chunkSize,
        SourceFingerprint fingerprint,
        IReadOnlySet<string>? bloomColumns = null,
        IReadOnlySet<string>? indexColumns = null,
        bool bloomAllColumns = false,
        bool indexAllColumns = false,
        bool autoIndexColumns = false,
        int? maxIndexedColumns = null)
    {
        _chunkSize = chunkSize;
        _fingerprint = fingerprint;
        _bloomColumns = bloomColumns;
        _indexColumns = indexColumns;
        _bloomAllColumns = bloomAllColumns;
        _indexAllColumns = indexAllColumns;
        _autoIndexColumns = autoIndexColumns;
        _maxIndexedColumns = maxIndexedColumns;
        bool needsBloom = bloomAllColumns || (bloomColumns is not null && bloomColumns.Count > 0);
        bool needsIndex = indexAllColumns || (indexColumns is not null && indexColumns.Count > 0) || autoIndexColumns;
        _allChunkBloomFilters = needsBloom ? new() : null;
        _spillWriter = needsIndex ? new SortedIndexSpillWriter() : null;
    }

    /// <summary>
    /// Observes a single row for index building. Call once per row
    /// as it streams through the output writer.
    /// </summary>
    /// <param name="row">The row to index.</param>
    public void AddRow(Row row)
    {
        if (_schema is null)
        {
            _schema = BuildSchemaFromRow(row);
            InitializeAccumulators(row);
            _effectiveBloomColumns = SourceIndexBuilder.ResolveEffectiveColumns(_bloomColumns, _bloomAllColumns, _schema);
            IReadOnlySet<string>? effectiveIndexColumns = ResolveEffectiveIndexColumns(_schema);
            _currentBloomFilters = SourceIndexBuilder.CreateBloomFilters(_effectiveBloomColumns, _chunkSize);

            if (_spillWriter is not null && effectiveIndexColumns is not null)
            {
                _spillWriter.Initialize(effectiveIndexColumns);
            }

            _bitmapAccumulators = SourceIndexBuilder.CreateBitmapAccumulators(_schema);

            if (_bitmapAccumulators is not null)
            {
                foreach (BitmapChunkAccumulator bitmapAccumulator in _bitmapAccumulators.Values)
                {
                    bitmapAccumulator.BeginChunk(_chunkSize);
                }
            }

            BuildOrdinalLookups(row);
        }

        int fieldCount = row.FieldCount;
        SourceIndexBuilder_ChunkAccumulatorProxy?[] accumulators = _ordinalAccumulators!;
        BloomFilter?[]? bloomFilters = _ordinalBloomFilters;
        List<ValueIndexEntry>?[]? spillEntries = _ordinalSpillEntries;
        BitmapChunkAccumulator?[]? bitmapAccs = _ordinalBitmapAccumulators;

        for (int ordinal = 0; ordinal < fieldCount; ordinal++)
        {
            DataValue value = row[ordinal];

            SourceIndexBuilder_ChunkAccumulatorProxy? accumulator = accumulators[ordinal];
            if (accumulator is not null)
            {
                accumulator.Add(value);
            }

            if (bloomFilters is not null)
            {
                BloomFilter? bloom = bloomFilters[ordinal];
                if (bloom is not null)
                {
                    bloom.Add(value);
                }
            }

            if (spillEntries is not null && !value.IsNull)
            {
                List<ValueIndexEntry>? entries = spillEntries[ordinal];
                if (entries is not null)
                {
                    if (value.Kind == DataKind.String
                        && value.AsString().Length > SortedIndexSpillWriter.AutoIndexMaxStringLength)
                    {
                        _spillWriter!.DropColumn(_resolvedColumnNames![ordinal]);
                        spillEntries[ordinal] = null;
                    }
                    else
                    {
                        entries.Add(new ValueIndexEntry(value, _currentChunkIndex, _rowsInCurrentChunk));
                    }
                }
            }

            if (bitmapAccs is not null)
            {
                BitmapChunkAccumulator? bitmapAcc = bitmapAccs[ordinal];
                if (bitmapAcc is not null)
                {
                    bitmapAcc.Add(value, _rowsInCurrentChunk);
                }
            }
        }

        _rowsInCurrentChunk++;
        _totalRowCount++;

        if (_rowsInCurrentChunk >= _chunkSize)
        {
            FinalizeCurrentChunk();
        }
    }

    /// <summary>
    /// The internal spill writer holding sorted index data on disk.
    /// Available after <see cref="Finalize"/> for streaming serialization via
    /// <see cref="IndexWriter"/>. Disposed when this builder is disposed.
    /// </summary>
    internal SortedIndexSpillWriter? SpillWriter => _spillWriter;

    /// <summary>
    /// Optional callback invoked when a chunk is finalized and flushed.
    /// Parameters: zero-based chunk index, total row count processed so far.
    /// </summary>
    public Action<int, long>? OnChunkFlushed { get; set; }

    /// <summary>
    /// Finalizes the index after all rows have been observed.
    /// The spill writer is prepared for reading but not materialized or disposed —
    /// callers that need to serialize sorted indexes should pass <see cref="SpillWriter"/>
    /// to <see cref="IndexWriter.Write(SourceIndexSet, Stream, SortedIndexSpillWriter?, bool)"/>.
    /// The spill writer is cleaned up when this builder is disposed.
    /// </summary>
    /// <returns>The completed source index (with <see cref="SourceIndex.SortedIndexes"/>
    /// set to <c>null</c>; sorted index data remains on disk in the spill writer).</returns>
    public SourceIndex Finalize()
    {
        if (_rowsInCurrentChunk > 0)
        {
            FinalizeCurrentChunk();
        }

        Schema schema = _schema ?? new Schema(new[] { new ColumnInfo("empty", DataKind.String, nullable: true) });

        BloomFilterSet? bloomFilterSet = _allChunkBloomFilters is not null
            ? SourceIndexBuilder.BuildBloomFilterSet(_allChunkBloomFilters, _chunks.Count)
            : null;

        BitmapIndexSet? bitmapIndexSet = _bitmapAccumulators is not null
            ? SourceIndexBuilder.BuildBitmapIndexSet(_bitmapAccumulators)
            : null;

        _spillWriter?.PrepareForReading();

        IndexSchema indexSchema = new(schema, _totalRowCount);
        return new SourceIndex(_fingerprint, indexSchema, _chunks, bloomFilterSet, sortedIndexes: null,
            zipDirectory: null, bPlusTreeIndexes: null, bitmapIndexes: bitmapIndexSet);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _spillWriter?.Dispose();
        _spillWriter = null;
    }

    private void FinalizeCurrentChunk()
    {
        Dictionary<string, ChunkColumnStatistics> stats = new(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, SourceIndexBuilder_ChunkAccumulatorProxy> entry in _currentAccumulators)
        {
            stats[entry.Key] = entry.Value.ToStatistics(_rowsInCurrentChunk);
        }

        _chunks.Add(new IndexChunk(
            RowOffset: _currentChunkRowOffset,
            RowCount: _rowsInCurrentChunk,
            SourceByteOffset: -1,
            SourceByteLength: -1,
            ColumnStatistics: stats));

        if (_allChunkBloomFilters is not null && _currentBloomFilters is not null)
        {
            _allChunkBloomFilters.Add(_currentBloomFilters);
        }

        _spillWriter?.FlushChunk();

        if (_bitmapAccumulators is not null)
        {
            foreach (BitmapChunkAccumulator bitmapAcc in _bitmapAccumulators.Values)
            {
                bitmapAcc.FinalizeChunk(_rowsInCurrentChunk);
            }
        }

        OnChunkFlushed?.Invoke(_currentChunkIndex, _totalRowCount);

        _currentChunkRowOffset = _totalRowCount;
        _rowsInCurrentChunk = 0;
        _currentChunkIndex++;

        if (_schema is not null)
        {
            InitializeAccumulatorsFromSchema();
            _currentBloomFilters = SourceIndexBuilder.CreateBloomFilters(_effectiveBloomColumns, _chunkSize);

            if (_bitmapAccumulators is not null)
            {
                foreach (BitmapChunkAccumulator bitmapAcc in _bitmapAccumulators.Values)
                {
                    bitmapAcc.BeginChunk(_chunkSize);
                }
            }

            RebuildOrdinalAccumulatorsAndBlooms();
        }
    }

    private void InitializeAccumulators(Row row)
    {
        _currentAccumulators = new Dictionary<string, SourceIndexBuilder_ChunkAccumulatorProxy>(StringComparer.OrdinalIgnoreCase);

        foreach (string name in row.ColumnNames)
        {
            _currentAccumulators[name] = new SourceIndexBuilder_ChunkAccumulatorProxy();
        }
    }

    private void InitializeAccumulatorsFromSchema()
    {
        _currentAccumulators = new Dictionary<string, SourceIndexBuilder_ChunkAccumulatorProxy>(StringComparer.OrdinalIgnoreCase);

        foreach (ColumnInfo column in _schema!.Columns)
        {
            _currentAccumulators[column.Name] = new SourceIndexBuilder_ChunkAccumulatorProxy();
        }
    }

    /// <summary>
    /// Builds ordinal-indexed lookup arrays from the first row's schema. Called once
    /// during schema initialization. Spill entry lists are stable across chunks, so
    /// they are resolved here and not rebuilt.
    /// </summary>
    private void BuildOrdinalLookups(Row row)
    {
        int columnCount = row.FieldCount;
        _resolvedColumnNames = new string[columnCount];

        for (int ordinal = 0; ordinal < columnCount; ordinal++)
        {
            _resolvedColumnNames[ordinal] = row.ColumnNames[ordinal];
        }

        if (_spillWriter is not null)
        {
            _ordinalSpillEntries = new List<ValueIndexEntry>?[columnCount];

            for (int ordinal = 0; ordinal < columnCount; ordinal++)
            {
                _ordinalSpillEntries[ordinal] = _spillWriter.GetEntryListOrNull(_resolvedColumnNames[ordinal]);
            }
        }

        if (_bitmapAccumulators is not null)
        {
            _ordinalBitmapAccumulators = new BitmapChunkAccumulator?[columnCount];

            for (int ordinal = 0; ordinal < columnCount; ordinal++)
            {
                _bitmapAccumulators.TryGetValue(_resolvedColumnNames[ordinal], out BitmapChunkAccumulator? bitmapAcc);
                _ordinalBitmapAccumulators[ordinal] = bitmapAcc;
            }
        }

        RebuildOrdinalAccumulatorsAndBlooms();
    }

    /// <summary>
    /// Rebuilds the ordinal-indexed accumulator and bloom filter arrays to point at the
    /// current chunk's instances. Called after each chunk boundary when new accumulators
    /// and bloom filters are created.
    /// </summary>
    private void RebuildOrdinalAccumulatorsAndBlooms()
    {
        int columnCount = _resolvedColumnNames!.Length;
        _ordinalAccumulators = new SourceIndexBuilder_ChunkAccumulatorProxy?[columnCount];

        for (int ordinal = 0; ordinal < columnCount; ordinal++)
        {
            _currentAccumulators.TryGetValue(_resolvedColumnNames[ordinal], out SourceIndexBuilder_ChunkAccumulatorProxy? accumulator);
            _ordinalAccumulators[ordinal] = accumulator;
        }

        if (_currentBloomFilters is not null)
        {
            _ordinalBloomFilters = new BloomFilter?[columnCount];

            for (int ordinal = 0; ordinal < columnCount; ordinal++)
            {
                _currentBloomFilters.TryGetValue(_resolvedColumnNames[ordinal], out BloomFilter? bloom);
                _ordinalBloomFilters[ordinal] = bloom;
            }
        }
        else
        {
            _ordinalBloomFilters = null;
        }
    }

    /// <summary>
    /// Resolves the effective index column set considering explicit columns,
    /// all-columns mode, and auto-index mode (in priority order).
    /// </summary>
    private IReadOnlySet<string>? ResolveEffectiveIndexColumns(Schema schema)
    {
        IReadOnlySet<string>? result;

        if (_indexColumns is not null && _indexColumns.Count > 0)
        {
            result = _indexColumns;
        }
        else if (_indexAllColumns)
        {
            result = SourceIndexBuilder.ResolveEffectiveColumns(null, true, schema);
        }
        else if (_autoIndexColumns)
        {
            result = SourceIndexBuilder.ResolveAutoIndexColumns(schema);
        }
        else
        {
            return null;
        }

        if (result is not null && _maxIndexedColumns is int max && result.Count > max)
        {
            HashSet<string> trimmed = new(max, StringComparer.OrdinalIgnoreCase);

            foreach (string column in result)
            {
                trimmed.Add(column);

                if (trimmed.Count >= max)
                {
                    break;
                }
            }

            return trimmed;
        }

        return result;
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
}

/// <summary>
/// Proxy accumulator identical to SourceIndexBuilder.ChunkAccumulator,
/// used by IncrementalIndexBuilder. Extracted to avoid nesting complexity.
/// </summary>
internal sealed class SourceIndexBuilder_ChunkAccumulatorProxy
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
        if (!IsComparableKind(value.Kind))
        {
            return;
        }

        if (_minimum is null || StatisticsPredicateEvaluator.CompareValues(value, _minimum) < 0)
        {
            _minimum = value;
        }

        if (_maximum is null || StatisticsPredicateEvaluator.CompareValues(value, _maximum) > 0)
        {
            _maximum = value;
        }
    }

    private void AddToCardinality(DataValue value)
    {
        switch (value.Kind)
        {
            case DataKind.Scalar:
                _cardinality.Add(value.AsScalar());
                break;
            case DataKind.UInt8:
                _cardinality.Add((int)value.AsUInt8());
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
        return kind is DataKind.Scalar or DataKind.UInt8 or DataKind.String
            or DataKind.Date or DataKind.DateTime;
    }
}
