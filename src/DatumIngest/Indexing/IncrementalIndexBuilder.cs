using System.Diagnostics.CodeAnalysis;
using DatumIngest.Indexing.Bitmap;
using DatumIngest.Indexing.Bloom;
using DatumIngest.Indexing.Sorted;
using DatumIngest.Model;

namespace DatumIngest.Indexing;

/// <summary>
/// Incrementally builds a <see cref="SourceIndex"/> as rows stream through an
/// <see cref="DatumIngest.Ingestion.Indexer"/> or equivalent producer. Created by
/// <see cref="SourceIndexBuilder.CreateIncrementalBuilder"/>.
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
    private readonly bool _computeCardinality;
    private IReadOnlySet<string>? _effectiveBloomColumns;
    private Schema? _schema;
    private readonly List<IndexChunk> _chunks = new();
    private Dictionary<string, ChunkAccumulator>? _currentAccumulators = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, BloomFilter>? _currentBloomFilters;
    private readonly List<Dictionary<string, BloomFilter>>? _allChunkBloomFilters;
    private SortedIndexSpillWriter? _spillWriter;

    /// <summary>Ordinal-indexed column names, resolved once from the first row's schema.</summary>
    private string[]? _resolvedColumnNames;

    /// <summary>Ordinal-indexed accumulators, rebuilt at each chunk boundary.</summary>
    private ChunkAccumulator?[]? _ordinalAccumulators;

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

    /// <summary>
    /// Columns whose auto-assigned index type failed during the scan. Exposed via
    /// <see cref="DeferredReindexColumns"/> for diagnostic logging. No automatic recovery
    /// is possible in the incremental path because rows are not retained.
    /// </summary>
    private List<string>? _deferredReindexColumns;

    private int _rowsInCurrentChunk;
    private long _totalRowCount;
    private long _currentChunkRowOffset;
    private int _currentChunkIndex;

    internal IncrementalIndexBuilder(
        int chunkSize,
        SourceFingerprint fingerprint,
        IReadOnlySet<string>? bloomColumns = null,
        IReadOnlySet<string>? indexColumns = null,
        bool bloomAllColumns = false,
        bool indexAllColumns = false,
        bool autoIndexColumns = false,
        bool computeCardinality = true)
    {
        _chunkSize = chunkSize;
        _fingerprint = fingerprint;
        _bloomColumns = bloomColumns;
        _indexColumns = indexColumns;
        _bloomAllColumns = bloomAllColumns;
        _indexAllColumns = indexAllColumns;
        _autoIndexColumns = autoIndexColumns;
        _computeCardinality = computeCardinality;
        bool needsBloom = bloomAllColumns || (bloomColumns is not null && bloomColumns.Count > 0);
        bool needsIndex = indexAllColumns || (indexColumns is not null && indexColumns.Count > 0) || autoIndexColumns;
        _allChunkBloomFilters = needsBloom ? new() : null;
        _spillWriter = needsIndex ? new SortedIndexSpillWriter() : null;
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
    /// <param name="arena">The arena to use for decoding string values if needed for bloom or sorted indexes.</param>
    public void AddRow(Row row, Arena arena)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        if (_schema is null)
        {
            _schema = BuildSchemaFromRow(row);
            InitializeAccumulators(row);
            _effectiveBloomColumns = SourceIndexBuilder.ResolveEffectiveColumns(_bloomColumns, _bloomAllColumns, _schema);
            IReadOnlySet<string>? effectiveIndexColumns = ResolveEffectiveIndexColumns(_schema);
            _currentBloomFilters = SourceIndexBuilder.CreateBloomFilters(_effectiveBloomColumns, _chunkSize);

            if (_spillWriter is not null && effectiveIndexColumns is not null)
            {
                _spillWriter.Initialize(effectiveIndexColumns, _chunkSize, _schema);
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
        ChunkAccumulator?[] accumulators = _ordinalAccumulators!;
        BloomFilter?[]? bloomFilters = _ordinalBloomFilters;
        List<ValueIndexEntry>?[]? spillEntries = _ordinalSpillEntries;
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
                // are not. Sidecar payloads tend to be high-cardinality
                // identifiers (filenames, long descriptions, image bytes)
                // where bloom selectivity is low anyway — the trade is
                // accepting reduced recall on those columns rather than
                // plumbing a SidecarRegistry through every accumulator.
                if (!value.IsInSidecar)
                {
                    BloomFilter? bloom = bloomFilters[ordinal];
                    bloom?.Add(value, arena);
                }
            }

            if (spillEntries is not null && !value.IsNull)
            {
                List<ValueIndexEntry>? entries = spillEntries[ordinal];
                if (entries is not null)
                {
                    // Non-inline String/JsonValue can't be retained in the sorted index without
                    // arena plumbing. Drop the column on first sight — "indexable = self-contained."
                    if (value.Kind is DataKind.String or DataKind.JsonValue && !value.IsInline)
                    {
                        string droppedName = _resolvedColumnNames![ordinal];
                        _spillWriter!.DropColumn(droppedName);
                        spillEntries[ordinal] = null;
                        _deferredReindexColumns ??= new();
                        _deferredReindexColumns.Add(droppedName);
                    }
                    else
                    {
                        entries.Add(new ValueIndexEntry(value, _currentChunkIndex, _rowsInCurrentChunk));
                    }
                }
            }

            if (bitmapAccs is not null)
            {
                BitmapChunkAccumulator? bitmapAccumulator = bitmapAccs[ordinal];
                if (bitmapAccumulator is not null)
                {
                    bitmapAccumulator.Add(value, _rowsInCurrentChunk);

                    // Remove abandoned accumulators from the per-row path so high-cardinality
                    // columns do not pay method-call overhead for every remaining row.
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
    /// The internal spill writer holding sorted index data on disk. Available after
    /// <see cref="Finalize"/> for streaming serialization via <see cref="UnifiedIndexWriter"/>.
    /// Disposed when this builder is disposed.
    /// </summary>
    internal SortedIndexSpillWriter? SpillWriter => _spillWriter;

    /// <summary>
    /// Columns whose auto-assigned index type failed during the scan (e.g. a string column
    /// whose values exceeded the sorted-index length threshold). Available for diagnostic
    /// logging after <see cref="Finalize"/>.
    /// </summary>
    public IReadOnlyList<string> DeferredReindexColumns =>
        (IReadOnlyList<string>?)_deferredReindexColumns ?? Array.Empty<string>();

    /// <summary>
    /// Finalizes the index after all rows have been observed. The spill writer is prepared
    /// for reading but not materialised or disposed — callers that need to serialise sorted
    /// indexes should pass <see cref="SpillWriter"/> to
    /// <see cref="UnifiedIndexWriter.Write(SourceIndexSet, Stream, SortedIndexSpillWriter)"/>.
    /// The spill writer is cleaned up when this builder is disposed.
    /// </summary>
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

        // When sorted columns were auto-selected (not explicitly requested), exclude
        // bitmap-covered columns from the spill writer before serialisation to avoid
        // duplicate coverage. The spill writer is handed to IndexWriter which serialises
        // it directly — this is the incremental builder's only chance to dedup.
        bool sortedColumnsAreAutoSelected = _autoIndexColumns
            && !_indexAllColumns
            && (_indexColumns is null || _indexColumns.Count == 0);

        if (sortedColumnsAreAutoSelected && bitmapIndexSet is not null && _spillWriter is not null)
        {
            foreach (string bitmapColumn in bitmapIndexSet.ColumnNames)
            {
                _spillWriter.DropColumn(bitmapColumn);
            }
        }

        _spillWriter?.PrepareForReading();

        IndexSchema indexSchema = new(schema, _totalRowCount);
        return new SourceIndex(_fingerprint, indexSchema, _chunks, bloomFilterSet,
            bPlusTreeIndexes: null, bitmapIndexes: bitmapIndexSet);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        _currentAccumulators = null;
        _ordinalAccumulators = null;

        _spillWriter?.Dispose();
        _spillWriter = null;

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

        _spillWriter?.FlushChunk();

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

    /// <summary>
    /// Builds ordinal-indexed lookup arrays from the first row's schema. Called once during
    /// schema initialisation. Spill entry lists are stable across chunks, so they are
    /// resolved here and not rebuilt.
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

    /// <summary>
    /// Resolves the effective index column set from explicit columns, all-columns mode,
    /// or auto-index mode (in priority order).
    /// </summary>
    private IReadOnlySet<string>? ResolveEffectiveIndexColumns(Schema schema)
    {
        if (_indexColumns is not null && _indexColumns.Count > 0)
        {
            return _indexColumns;
        }

        if (_indexAllColumns)
        {
            return SourceIndexBuilder.ResolveEffectiveColumns(null, true, schema);
        }

        if (_autoIndexColumns)
        {
            return SourceIndexBuilder.ResolveAutoIndexColumns(schema);
        }

        return null;
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
