using System.Diagnostics.CodeAnalysis;
using DatumIngest.Indexing.Bitmap;
using DatumIngest.Indexing.Bloom;
using DatumIngest.Model;

namespace DatumIngest.Indexing;

/// <summary>
/// Incrementally builds a <see cref="SourceIndex"/> as rows stream through an
/// <see cref="DatumIngest.Ingestion.Indexer"/> or equivalent producer. Created by
/// <see cref="SourceIndexBuilder.CreateIncrementalBuilder"/>.
/// </summary>
/// <remarks>
/// PR13d (v8): per-column B+Tree acceleration moved to companion
/// <c>.datum-bptree-{col}</c> page-COW files. This builder produces only
/// the unified-sidecar payload — fingerprint, schema, chunk directory +
/// zone maps, bloom filters, bitmap indexes. Columns eligible for B+Tree
/// indexing are written separately by <see cref="DatumIngest.Ingestion.Indexer"/>
/// driving <c>MutableBPlusTree</c> directly.
/// </remarks>
public sealed class IncrementalIndexBuilder : IDisposable
{
    private readonly int _chunkSize;
    private readonly SourceFingerprint _fingerprint;
    private readonly IReadOnlySet<string>? _bloomColumns;
    private readonly bool _bloomAllColumns;
    private readonly bool _computeCardinality;
    private IReadOnlySet<string>? _effectiveBloomColumns;
    private Schema? _schema;
    private readonly List<IndexChunk> _chunks = new();
    private Dictionary<string, ChunkAccumulator>? _currentAccumulators = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, BloomFilter>? _currentBloomFilters;
    private readonly List<Dictionary<string, BloomFilter>>? _allChunkBloomFilters;

    /// <summary>Ordinal-indexed column names, resolved once from the first row's schema.</summary>
    private string[]? _resolvedColumnNames;

    /// <summary>Ordinal-indexed accumulators, rebuilt at each chunk boundary.</summary>
    private ChunkAccumulator?[]? _ordinalAccumulators;

    /// <summary>Ordinal-indexed bloom filters, rebuilt at each chunk boundary.</summary>
    private BloomFilter?[]? _ordinalBloomFilters;

    /// <summary>Per-column bitmap accumulators, created on first schema discovery.</summary>
    private Dictionary<string, BitmapChunkAccumulator>? _bitmapAccumulators;

    /// <summary>Ordinal-indexed bitmap accumulators, resolved once from the first row's schema.</summary>
    private BitmapChunkAccumulator?[]? _ordinalBitmapAccumulators;

    private int _rowsInCurrentChunk;
    private long _totalRowCount;
    private long _currentChunkRowOffset;
    private int _currentChunkIndex;

    internal IncrementalIndexBuilder(
        int chunkSize,
        SourceFingerprint fingerprint,
        IReadOnlySet<string>? bloomColumns = null,
        bool bloomAllColumns = false,
        bool computeCardinality = true)
    {
        _chunkSize = chunkSize;
        _fingerprint = fingerprint;
        _bloomColumns = bloomColumns;
        _bloomAllColumns = bloomAllColumns;
        _computeCardinality = computeCardinality;
        bool needsBloom = bloomAllColumns || (bloomColumns is not null && bloomColumns.Count > 0);
        _allChunkBloomFilters = needsBloom ? new() : null;
    }

    /// <summary>
    /// Gets a value indicating whether this builder has been disposed.
    /// </summary>
    [MemberNotNullWhen(false, nameof(_currentAccumulators))]
    public bool Disposed { get; private set; }

    /// <summary>
    /// Observes every row in the given batch. Convenience wrapper around
    /// <see cref="AddRow"/> for callers that already have a <see cref="RowBatch"/>.
    /// </summary>
    public void AddBatch(RowBatch batch)
    {
        for (int i = 0; i < batch.Count; i++)
        {
            AddRow(batch[i], batch.Arena);
        }
    }

    /// <summary>
    /// Observes a single row for index building. Call once per row as it streams through
    /// the output writer.
    /// </summary>
    /// <param name="row">The row to index.</param>
    /// <param name="arena">The arena to use for decoding string values if needed for bloom indexes.</param>
    public void AddRow(Row row, Arena arena)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        if (_schema is null)
        {
            _schema = BuildSchemaFromRow(row);
            InitializeAccumulators(row);
            _effectiveBloomColumns = SourceIndexBuilder.ResolveEffectiveColumns(_bloomColumns, _bloomAllColumns, _schema);
            _currentBloomFilters = SourceIndexBuilder.CreateBloomFilters(_effectiveBloomColumns, _chunkSize);

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
        ChunkAccumulator?[] accumulators = _ordinalAccumulators!;
        BloomFilter?[]? bloomFilters = _ordinalBloomFilters;
        BitmapChunkAccumulator?[]? bitmapAccs = _ordinalBitmapAccumulators;

        for (int ordinal = 0; ordinal < fieldCount; ordinal++)
        {
            DataValue value = row[ordinal];

            ChunkAccumulator? accumulator = accumulators[ordinal];
            accumulator?.Add(value);

            if (bloomFilters is not null)
            {
                // Skip sidecar-bound values: the bloom-hash path resolves
                // bytes through arena offsets, which sidecar coordinates
                // are not.
                if (!value.IsInSidecar)
                {
                    BloomFilter? bloom = bloomFilters[ordinal];
                    bloom?.Add(value, arena);
                }
            }

            if (bitmapAccs is not null)
            {
                BitmapChunkAccumulator? bitmapAccumulator = bitmapAccs[ordinal];
                if (bitmapAccumulator is not null)
                {
                    bitmapAccumulator.Add(value, _rowsInCurrentChunk);

                    if (bitmapAccumulator.IsAbandoned)
                    {
                        bitmapAccs[ordinal] = null;
                    }
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
    /// Finalizes the index after all rows have been observed.
    /// </summary>
    public SourceIndex Finalize() => Finalize(_fingerprint);

    /// <summary>
    /// Finalizes the index using <paramref name="fingerprint"/> instead of the
    /// one captured at construction. Used by <c>DatumAppendSession</c>'s
    /// in-line index build (Phase 3a) — the post-commit data-file fingerprint
    /// isn't known when the builder is created (rows haven't been written
    /// yet), so it's swapped in here.
    /// </summary>
    public SourceIndex Finalize(SourceFingerprint fingerprint)
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

        IndexSchema indexSchema = new(schema, _totalRowCount);
        return new SourceIndex(fingerprint, indexSchema, _chunks, bloomFilterSet, bitmapIndexSet);
    }

    /// <summary>
    /// Number of complete chunks finalized so far. Append-session callers use
    /// this to compute (chunkIndex, rowOffsetInChunk) for per-column tree
    /// entries written in lockstep with <see cref="AddRow"/>. Increments
    /// inside <c>FinalizeCurrentChunk</c>; the in-progress chunk is reported
    /// separately via <see cref="RowsInCurrentChunk"/>.
    /// </summary>
    public int CurrentChunkIndex => _currentChunkIndex;

    /// <summary>Rows added to the current (in-progress) chunk.</summary>
    public int RowsInCurrentChunk => _rowsInCurrentChunk;

    /// <inheritdoc />
    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        _currentAccumulators = null;
        _ordinalAccumulators = null;

        Disposed = true;
    }

    private void FinalizeCurrentChunk()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        Dictionary<string, ChunkColumnStatistics> stats = new(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, ChunkAccumulator> entry in _currentAccumulators)
        {
            stats[entry.Key] = entry.Value.ToStatistics(_rowsInCurrentChunk);
        }

        _chunks.Add(new IndexChunk(
            RowOffset: _currentChunkRowOffset,
            RowCount: _rowsInCurrentChunk,
            ColumnStatistics: stats));

        if (_allChunkBloomFilters is not null && _currentBloomFilters is not null)
        {
            _allChunkBloomFilters.Add(_currentBloomFilters);
        }

        if (_bitmapAccumulators is not null)
        {
            foreach (BitmapChunkAccumulator bitmapAcc in _bitmapAccumulators.Values)
            {
                bitmapAcc.FinalizeChunk(_rowsInCurrentChunk);
            }
        }

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
        _currentAccumulators = SourceIndexBuilder.CreateAccumulators(row, _computeCardinality);
    }

    private void InitializeAccumulatorsFromSchema()
    {
        _currentAccumulators = SourceIndexBuilder.CreateAccumulators(_schema!, _computeCardinality);
    }

    private void BuildOrdinalLookups(Row row)
    {
        int columnCount = row.FieldCount;
        _resolvedColumnNames = new string[columnCount];

        for (int ordinal = 0; ordinal < columnCount; ordinal++)
        {
            _resolvedColumnNames[ordinal] = row.ColumnNames[ordinal];
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

    private void RebuildOrdinalAccumulatorsAndBlooms()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        int columnCount = _resolvedColumnNames!.Length;
        _ordinalAccumulators = new ChunkAccumulator?[columnCount];

        for (int ordinal = 0; ordinal < columnCount; ordinal++)
        {
            _currentAccumulators.TryGetValue(_resolvedColumnNames[ordinal], out ChunkAccumulator? accumulator);
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
