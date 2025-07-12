using CardinalityEstimation;
using DatumIngest.Catalog;
using DatumIngest.Execution;
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

    /// <summary>
    /// Creates a builder with the specified chunk size.
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
        SourceFingerprint fingerprint = sourceStream is not null
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
        List<Dictionary<string, BloomFilter>>? allChunkBloomFilters =
            _bloomColumns is not null && _bloomColumns.Count > 0 ? new() : null;
        Dictionary<string, List<ValueIndexEntry>>? indexCollectors =
            _indexColumns is not null && _indexColumns.Count > 0 ? new(StringComparer.OrdinalIgnoreCase) : null;
        int rowsInCurrentChunk = 0;
        int currentChunkIndex = 0;

        await foreach (Row row in provider.OpenAsync(descriptor, requiredColumns: null, cancellationToken)
            .ConfigureAwait(false))
        {
            if (schema is null)
            {
                schema = BuildSchemaFromRow(row);
                currentAccumulators = CreateAccumulators(row);
                currentBloomFilters = CreateBloomFilters(_bloomColumns, _chunkSize);
                InitializeIndexCollectors(indexCollectors, _indexColumns);
            }

            foreach (string columnName in row.ColumnNames)
            {
                DataValue value = row[columnName];

                if (currentAccumulators.TryGetValue(columnName, out ChunkAccumulator? accumulator))
                {
                    accumulator.Add(value);
                }

                if (currentBloomFilters is not null
                    && currentBloomFilters.TryGetValue(columnName, out BloomFilter? bloom))
                {
                    bloom.Add(value);
                }

                if (indexCollectors is not null && !value.IsNull
                    && indexCollectors.TryGetValue(columnName, out List<ValueIndexEntry>? entries))
                {
                    entries.Add(new ValueIndexEntry(value, currentChunkIndex, rowsInCurrentChunk));
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
                currentChunkRowOffset = totalRowCount;
                rowsInCurrentChunk = 0;
                currentChunkIndex++;
                currentAccumulators = CreateAccumulators(schema);
                currentBloomFilters = CreateBloomFilters(_bloomColumns, _chunkSize);
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
        }

        schema ??= new Schema(new[] { new ColumnInfo("empty", DataKind.String, nullable: true) });

        BloomFilterSet? bloomFilterSet = allChunkBloomFilters is not null
            ? BuildBloomFilterSet(allChunkBloomFilters, chunks.Count)
            : null;

        SortedValueIndexSet? sortedIndexSet = indexCollectors is not null
            ? BuildSortedValueIndexSet(indexCollectors)
            : null;

        IndexSchema indexSchema = new(schema, totalRowCount);
        return new SourceIndex(fingerprint, indexSchema, chunks, bloomFilterSet, sortedIndexSet);
    }

    /// <summary>
    /// Creates an incremental index builder for co-generation during output writing (the <c>--with-index</c> workflow).
    /// Each row is observed but not consumed — the caller still owns the enumeration.
    /// Call <see cref="IncrementalIndexBuilder.AddRow"/> for each row, then <see cref="IncrementalIndexBuilder.Finalize"/> to produce the index.
    /// </summary>
    public IncrementalIndexBuilder CreateIncrementalBuilder(SourceFingerprint fingerprint)
    {
        return new IncrementalIndexBuilder(_chunkSize, fingerprint, _bloomColumns, _indexColumns);
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
    /// Initializes per-column entry lists for sorted index collection.
    /// </summary>
    internal static void InitializeIndexCollectors(
        Dictionary<string, List<ValueIndexEntry>>? collectors,
        IReadOnlySet<string>? indexColumns)
    {
        if (collectors is null || indexColumns is null)
        {
            return;
        }

        foreach (string column in indexColumns)
        {
            collectors.TryAdd(column, new List<ValueIndexEntry>());
        }
    }

    /// <summary>
    /// Sorts collected entries per column and assembles a <see cref="SortedValueIndexSet"/>.
    /// </summary>
    internal static SortedValueIndexSet BuildSortedValueIndexSet(
        Dictionary<string, List<ValueIndexEntry>> collectors)
    {
        Dictionary<string, SortedValueIndex> indexes = new(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, List<ValueIndexEntry>> entry in collectors)
        {
            ValueIndexEntry[] entries = entry.Value.ToArray();
            indexes[entry.Key] = SortedValueIndex.BuildFromUnsorted(entries);
        }

        return new SortedValueIndexSet(indexes);
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
            string representation = value.Kind switch
            {
                DataKind.Scalar => value.AsScalar().ToString("R"),
                DataKind.UInt8 => value.AsUInt8().ToString(),
                DataKind.String => value.AsString(),
                DataKind.Date => value.AsDate().ToString("O"),
                DataKind.DateTime => value.AsDateTime().ToString("O"),
                DataKind.JsonValue => value.AsJsonValue(),
                _ => value.GetHashCode().ToString()
            };

            _cardinality.Add(representation);
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
public sealed class IncrementalIndexBuilder
{
    private readonly int _chunkSize;
    private readonly SourceFingerprint _fingerprint;
    private readonly IReadOnlySet<string>? _bloomColumns;
    private readonly IReadOnlySet<string>? _indexColumns;
    private Schema? _schema;
    private readonly List<IndexChunk> _chunks = new();
    private Dictionary<string, SourceIndexBuilder_ChunkAccumulatorProxy> _currentAccumulators = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, BloomFilter>? _currentBloomFilters;
    private readonly List<Dictionary<string, BloomFilter>>? _allChunkBloomFilters;
    private readonly Dictionary<string, List<ValueIndexEntry>>? _indexCollectors;
    private int _rowsInCurrentChunk;
    private long _totalRowCount;
    private long _currentChunkRowOffset;
    private int _currentChunkIndex;

    internal IncrementalIndexBuilder(
        int chunkSize,
        SourceFingerprint fingerprint,
        IReadOnlySet<string>? bloomColumns = null,
        IReadOnlySet<string>? indexColumns = null)
    {
        _chunkSize = chunkSize;
        _fingerprint = fingerprint;
        _bloomColumns = bloomColumns;
        _indexColumns = indexColumns;
        _allChunkBloomFilters = bloomColumns is not null && bloomColumns.Count > 0 ? new() : null;
        _indexCollectors = indexColumns is not null && indexColumns.Count > 0
            ? new(StringComparer.OrdinalIgnoreCase)
            : null;
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
            _currentBloomFilters = SourceIndexBuilder.CreateBloomFilters(_bloomColumns, _chunkSize);
            SourceIndexBuilder.InitializeIndexCollectors(_indexCollectors, _indexColumns);
        }

        foreach (string columnName in row.ColumnNames)
        {
            DataValue value = row[columnName];

            if (_currentAccumulators.TryGetValue(columnName, out SourceIndexBuilder_ChunkAccumulatorProxy? accumulator))
            {
                accumulator.Add(value);
            }

            if (_currentBloomFilters is not null
                && _currentBloomFilters.TryGetValue(columnName, out BloomFilter? bloom))
            {
                bloom.Add(value);
            }

            if (_indexCollectors is not null && !value.IsNull
                && _indexCollectors.TryGetValue(columnName, out List<ValueIndexEntry>? entries))
            {
                entries.Add(new ValueIndexEntry(value, _currentChunkIndex, _rowsInCurrentChunk));
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
    /// Finalizes the index after all rows have been observed.
    /// </summary>
    /// <returns>The completed source index.</returns>
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

        SortedValueIndexSet? sortedIndexSet = _indexCollectors is not null
            ? SourceIndexBuilder.BuildSortedValueIndexSet(_indexCollectors)
            : null;

        IndexSchema indexSchema = new(schema, _totalRowCount);
        return new SourceIndex(_fingerprint, indexSchema, _chunks, bloomFilterSet, sortedIndexSet);
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

        _currentChunkRowOffset = _totalRowCount;
        _rowsInCurrentChunk = 0;
        _currentChunkIndex++;

        if (_schema is not null)
        {
            InitializeAccumulatorsFromSchema();
            _currentBloomFilters = SourceIndexBuilder.CreateBloomFilters(_bloomColumns, _chunkSize);
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
        string representation = value.Kind switch
        {
            DataKind.Scalar => value.AsScalar().ToString("R"),
            DataKind.UInt8 => value.AsUInt8().ToString(),
            DataKind.String => value.AsString(),
            DataKind.Date => value.AsDate().ToString("O"),
            DataKind.DateTime => value.AsDateTime().ToString("O"),
            DataKind.JsonValue => value.AsJsonValue(),
            _ => value.GetHashCode().ToString()
        };

        _cardinality.Add(representation);
    }

    private static bool IsComparableKind(DataKind kind)
    {
        return kind is DataKind.Scalar or DataKind.UInt8 or DataKind.String
            or DataKind.Date or DataKind.DateTime;
    }
}
