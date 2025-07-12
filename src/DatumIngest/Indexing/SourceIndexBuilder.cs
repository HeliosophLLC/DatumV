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

    /// <summary>
    /// Creates a builder with the specified chunk size.
    /// </summary>
    /// <param name="chunkSize">Number of rows per index chunk (default: 10,000).</param>
    public SourceIndexBuilder(int chunkSize = IndexConstants.DefaultChunkSize)
    {
        _chunkSize = chunkSize;
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

        Schema? schema = null;
        List<IndexChunk> chunks = new();
        long totalRowCount = 0;
        long currentChunkRowOffset = 0;

        Dictionary<string, ChunkAccumulator> currentAccumulators = new(StringComparer.OrdinalIgnoreCase);
        int rowsInCurrentChunk = 0;

        await foreach (Row row in provider.OpenAsync(descriptor, requiredColumns: null, cancellationToken)
            .ConfigureAwait(false))
        {
            if (schema is null)
            {
                schema = BuildSchemaFromRow(row);
                currentAccumulators = CreateAccumulators(row);
            }

            foreach (string columnName in row.ColumnNames)
            {
                DataValue value = row[columnName];

                if (currentAccumulators.TryGetValue(columnName, out ChunkAccumulator? accumulator))
                {
                    accumulator.Add(value);
                }
            }

            rowsInCurrentChunk++;
            totalRowCount++;

            if (rowsInCurrentChunk >= _chunkSize)
            {
                chunks.Add(FinalizeChunk(currentChunkRowOffset, rowsInCurrentChunk, currentAccumulators));
                currentChunkRowOffset = totalRowCount;
                rowsInCurrentChunk = 0;
                currentAccumulators = CreateAccumulators(schema);
            }
        }

        // Finalize the last partial chunk.
        if (rowsInCurrentChunk > 0)
        {
            chunks.Add(FinalizeChunk(currentChunkRowOffset, rowsInCurrentChunk, currentAccumulators));
        }

        schema ??= new Schema(new[] { new ColumnInfo("empty", DataKind.String, nullable: true) });

        IndexSchema indexSchema = new(schema, totalRowCount);
        return new SourceIndex(fingerprint, indexSchema, chunks);
    }

    /// <summary>
    /// Creates an incremental index builder for co-generation during output writing (the <c>--with-index</c> workflow).
    /// Each row is observed but not consumed — the caller still owns the enumeration.
    /// Call <see cref="IncrementalIndexBuilder.AddRow"/> for each row, then <see cref="IncrementalIndexBuilder.Finalize"/> to produce the index.
    /// </summary>
    public IncrementalIndexBuilder CreateIncrementalBuilder(SourceFingerprint fingerprint)
    {
        return new IncrementalIndexBuilder(_chunkSize, fingerprint);
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
            SourceByteOffset: -1,
            SourceByteLength: -1,
            ColumnStatistics: stats);
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
    private Schema? _schema;
    private readonly List<IndexChunk> _chunks = new();
    private Dictionary<string, SourceIndexBuilder_ChunkAccumulatorProxy> _currentAccumulators = new(StringComparer.OrdinalIgnoreCase);
    private int _rowsInCurrentChunk;
    private long _totalRowCount;
    private long _currentChunkRowOffset;

    internal IncrementalIndexBuilder(int chunkSize, SourceFingerprint fingerprint)
    {
        _chunkSize = chunkSize;
        _fingerprint = fingerprint;
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
        }

        foreach (string columnName in row.ColumnNames)
        {
            DataValue value = row[columnName];

            if (_currentAccumulators.TryGetValue(columnName, out SourceIndexBuilder_ChunkAccumulatorProxy? accumulator))
            {
                accumulator.Add(value);
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
        IndexSchema indexSchema = new(schema, _totalRowCount);
        return new SourceIndex(_fingerprint, indexSchema, _chunks);
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

        _currentChunkRowOffset = _totalRowCount;
        _rowsInCurrentChunk = 0;

        if (_schema is not null)
        {
            InitializeAccumulatorsFromSchema();
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
