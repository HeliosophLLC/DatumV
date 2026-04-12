using System.Runtime.CompilerServices;
using DatumIngest.Catalog;
using DatumIngest.Diagnostics;
using DatumIngest.Execution.Operators.Scans;
using DatumIngest.Indexing;
using DatumIngest.Indexing.Bitmap;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Indexing.Bloom;
using System.Diagnostics.CodeAnalysis;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Reads rows from a table provider, applying projection pushdown
/// to skip unreferenced columns at the source. When a filter hint is present,
/// the provider may skip entire partitions based on column statistics.
/// When a <see cref="SourceIndex"/> is available, chunk-level statistics
/// enable partition pruning for any provider type. Bloom filter pruning
/// allows join operators to pre-filter chunks by providing build-side key
/// values: chunks where no build-side key could possibly exist are skipped.
/// </summary>
public sealed class ScanOperator : QueryOperator
{
    private readonly IReadOnlySet<string>? _requiredColumns;
    private Expression? _filterHint;
    private Dictionary<string, IReadOnlyCollection<DataValue>>? _bloomPruningKeys;
    private Dictionary<string, IReadOnlyCollection<DataValue>>? _sortedIndexPruningKeys;

    /// <summary>
    /// Creates a scan operator for the given table.
    /// </summary>
    /// <param name="tableProvider">The table provider identifying the data source.</param>
    /// <param name="requiredColumns">Columns needed downstream; null means all columns.</param>
    /// <param name="tableRowCount">The row count for the table.</param>
    public ScanOperator(ITableProvider tableProvider, IReadOnlySet<string>? requiredColumns, long tableRowCount)
    {
        TableProvider = tableProvider;
        _requiredColumns = requiredColumns;
        TableRowCount = tableRowCount;
    }

    /// <summary>The table descriptor this operator scans.</summary>
    public ITableProvider TableProvider { get; private set;}

    /// <summary>The set of columns requested for projection pushdown.</summary>
    public IReadOnlySet<string>? RequiredColumns => _requiredColumns;

    /// <summary>The advisory filter hint passed to filterable providers, or <c>null</c>.</summary>
    public Expression? FilterHint => _filterHint;

    /// <summary>
    /// Estimated row count, set at plan time.
    /// <c>null</c> when the provider cannot report a row count.
    /// </summary>
    public long TableRowCount { get; }

    /// <summary>
    /// Per-column statistics from a <see cref="QueryResultsManifest"/>, set at plan time.
    /// </summary>
    public IReadOnlyDictionary<string, FeatureManifest>? ColumnStatistics { get; set; }

    /// <summary>The source index for chunk-based pruning, or <c>null</c> if none is available.</summary>
    public SourceIndex? SourceIndex => TableProvider.GetSourceIndex();

    /// <summary>Total number of index chunks considered during the last execution.</summary>
    public int TotalIndexChunks { get; private set; }

    /// <summary>Number of index chunks pruned (skipped) during the last execution.</summary>
    public int PrunedIndexChunks { get; private set; }

    /// <summary>
    /// Number of rows fetched via exact index seek during the last execution,
    /// or <c>null</c> if the exact seek path was not used.
    /// </summary>
    public int? ExactSeekRowsFetched { get; private set; }

    /// <summary>
    /// Number of positions the composite-index branch contributed during the
    /// last execution. Set when at least one composite index produced a
    /// non-empty result (full or prefix match); <see langword="null"/>
    /// otherwise. Independent of which strategy ultimately won the
    /// fewest-positions tiebreak — this counter records that the composite
    /// path was *consulted*, which is what tests need to prove composite
    /// indexes are doing the work and not just shadowed by single-column
    /// auto-built trees that happen to return the same set.
    /// </summary>
    public int? CompositeIndexSeekHits { get; private set; }

    /// <summary>
    /// Gets a value indicating whether this scan operator has the inputs needed to
    /// usefully apply index-based pruning. True when a source index is available and
    /// at least one pruning input is present: a filter hint, bloom pruning keys, or
    /// sorted index pruning keys. The mere presence of indexes on the source is not
    /// enough — without a filter or build-side keys to probe them with, every chunk
    /// would stay active and the pruning path would only add bookkeeping overhead
    /// before falling through to a full stream.
    /// </summary>
    [MemberNotNullWhen(true, nameof(SourceIndex))]
    public bool HasIndexPruning
    {
        get
        {
            return SourceIndex is not null
                && (_filterHint is not null
                    || _bloomPruningKeys is not null
                    || _sortedIndexPruningKeys is not null);
        }
    }

    /// <summary>
    /// Gets a value indicating whether bitmap-index-based row filtering is possible during execution.
    /// </summary>
    [MemberNotNullWhen(true, nameof(SourceIndex))]
    public bool HasBitmapRowFilter
    {
        get
        {
            return _filterHint is not null
                && SourceIndex?.BitmapIndexes is not null
                && SourceIndex.BitmapIndexes.Count > 0;
        }
    }

    /// <summary>
    /// Adds an advisory filter predicate for statistics-based partition pruning.
    /// Multiple calls combine predicates with AND.
    /// </summary>
    /// <param name="predicate">The predicate to add as a filter hint.</param>
    public void AddFilterHint(Expression predicate)
    {
        _filterHint = _filterHint is null
            ? predicate
            : new BinaryExpression(_filterHint, BinaryOperator.And, predicate);
    }

    /// <summary>
    /// Registers join key values for bloom-filter-based chunk pruning.
    /// During execution, chunks whose bloom filters report that none of the
    /// provided key values could be present are skipped entirely.
    /// </summary>
    /// <param name="columnName">The column name to check bloom filters for.</param>
    /// <param name="keyValues">The build-side key values from the join partner.</param>
    public void AddBloomPruningKeys(string columnName, IReadOnlyCollection<DataValue> keyValues)
    {
        _bloomPruningKeys ??= new(StringComparer.OrdinalIgnoreCase);
        _bloomPruningKeys[columnName] = keyValues;
    }

    /// <summary>
    /// Registers join key values for sorted-index-based chunk pruning.
    /// During execution, chunks whose sorted indexes report that none of the
    /// provided key values are present are skipped entirely.
    /// </summary>
    /// <param name="columnName">The column name to check sorted indexes for.</param>
    /// <param name="keyValues">The build-side key values from the join partner.</param>
    public void AddSortedIndexPruningKeys(string columnName, IReadOnlyCollection<DataValue> keyValues)
    {
        _sortedIndexPruningKeys ??= new(StringComparer.OrdinalIgnoreCase);
        _sortedIndexPruningKeys[columnName] = keyValues;
    }

    /// <inheritdoc/>
    protected override OperatorPlanDescription DescribeForExplainImpl()
    {
        SourceIndex? sourceIndex = TableProvider.GetSourceIndex();

        Dictionary<string, string> properties = new()
        {
            ["table"] = TableProvider.QualifiedName.ToString(),
            ["columns"] = _requiredColumns is not null
                ? string.Join(", ", _requiredColumns)
                : "*",
        };

        if (_filterHint is not null)
        {
            properties["statistics filter"] = QueryExplainer.FormatExpression(_filterHint);
        }

        // Build access strategy with pruning capabilities.
        List<PruningCapability> pruningCapabilities = [];

        if (_filterHint is not null && sourceIndex is not null)
        {
            pruningCapabilities.Add(new PruningCapability(
                PruningTechnique.StatisticsPruning, [], pendingRuntime: false));
        }

        if (sourceIndex?.BloomFilters is { ColumnCount: > 0 } bloomFilters)
        {
            pruningCapabilities.Add(new PruningCapability(
                PruningTechnique.BloomFilterPruning, [.. bloomFilters.ColumnNames], pendingRuntime: true));
        }

        if (sourceIndex?.BitmapIndexes is { Count: > 0 } bitmapIndexes)
        {
            pruningCapabilities.Add(new PruningCapability(
                PruningTechnique.BitmapPruning, [.. bitmapIndexes.ColumnNames], pendingRuntime: false));
        }

        // Per-column B+Tree pruning capability is reported by the provider
        // (PR13d moved per-column trees out of SourceIndex into companion
        // .datum-bptree-{col} files). The capability surfaces in EXPLAIN
        // output once the provider override returns trees.

        AccessStrategyDescription accessStrategy = new(
            AccessMethod.TableScan,
            pruningCapabilities.Count > 0 ? pruningCapabilities : null);

        return new OperatorPlanDescription("Scan")
        {
            Properties = properties,
            EstimatedRows = TableRowCount,
            AccessStrategy = accessStrategy,
        };
    }

    /// <inheritdoc/>
    protected override async IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context)
    {
        CancellationToken cancellationToken = context.CancellationToken;
        SourceIndex? sourceIndex = TableProvider.GetSourceIndex();

        DatumActivity.Operators.Trace($"SCAN start  table={TableProvider.QualifiedName}  hasIndex={sourceIndex is not null}  filterHint={_filterHint is not null}  tableRowCount={TableRowCount}");
        DatumActivity.Operators.Trace($"SCAN path  table={TableProvider.QualifiedName}  indexPruning={HasIndexPruning}");

        // Datum-format providers may carry a per-file struct type table that
        // needs to be deserialized into this query's TypeRegistry and
        // registered on TypeIdTranslations so per-element TypeIds in any
        // Array<Struct> values yielded below resolve to the right runtime
        // shapes. No-op for providers that don't carry a type table.
        if (TableProvider is Catalog.Providers.IDatumFileTableProvider datumProvider)
        {
            datumProvider.EnsureTypeTableLoaded(context);
        }

        if (HasIndexPruning)
        {
            await foreach (RowBatch batch in ExecuteWithIndexPruningAsync(
                TableProvider, SourceIndex, context).ConfigureAwait(false))
            {
                yield return batch;
            }
        }
        else
        {
            await foreach (RowBatch batch in TableProvider.ScanAsync(
                _requiredColumns, _filterHint, context.Store, cancellationToken,
                context.TypeIdTranslations).ConfigureAwait(false))
            {
                yield return batch;
            }
        }
    }

    private async IAsyncEnumerable<RowBatch> ExecuteWithIndexPruningAsync(
        ITableProvider provider,
        SourceIndex sourceIndex,
        ExecutionContext context)
    {
        CancellationToken cancellationToken = context.CancellationToken;
        IReadOnlyList<IndexChunk> chunks = sourceIndex.Chunks;
        BloomFilterSet? bloomFilters = sourceIndex.BloomFilters;
        TotalIndexChunks = chunks.Count;
        PrunedIndexChunks = 0;
        ExactSeekRowsFetched = null;
        CompositeIndexSeekHits = null;

        // Build the pruner pipeline once per execution. Each pruner enforces one
        // constraint (zone-map stats, bloom filters, sorted-index membership via
        // join keys, sorted-index pruning via filter literals, or bitmap-index
        // pruning); the per-chunk loop short-circuits at the first match.
        string tableName = TableProvider.QualifiedName.ToString();
        List<IChunkPruner> pruners = new();
        if (_filterHint is not null)
        {
            pruners.Add(new StatisticsChunkPruner(_filterHint, tableName));
        }
        if (_bloomPruningKeys is not null && bloomFilters is not null)
        {
            pruners.Add(new BloomChunkPruner(_bloomPruningKeys, bloomFilters, tableName));
        }
        if (_sortedIndexPruningKeys is not null)
        {
            pruners.Add(new SortedJoinKeyChunkPruner(_sortedIndexPruningKeys, provider, tableName));
        }
        if (_filterHint is not null)
        {
            pruners.Add(new SortedFilterChunkPruner(_filterHint, provider, tableName));
        }
        if (_filterHint is not null && sourceIndex.BitmapIndexes is not null)
        {
            pruners.Add(new BitmapChunkPruner(_filterHint, sourceIndex.BitmapIndexes, tableName));
        }

        // Build a set of non-pruned chunk row ranges and track active chunk indexes.
        List<(long Start, long End, int ChunkIndex)> activeRanges = new();
        HashSet<int> activeChunkIndexes = new();

        for (int chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
        {
            IndexChunk chunk = chunks[chunkIndex];
            bool pruned = false;

            foreach (IChunkPruner pruner in pruners)
            {
                if (pruner.ShouldPrune(chunkIndex, chunk, context.Store))
                {
                    pruned = true;
                    break;
                }
            }

            if (pruned)
            {
                PrunedIndexChunks++;
            }
            else
            {
                activeRanges.Add((chunk.RowOffset, chunk.RowOffset + chunk.RowCount, chunkIndex));
                activeChunkIndexes.Add(chunkIndex);
            }
        }

        DatumActivity.Operators.Trace($"SCAN index pruning  table={TableProvider.QualifiedName}  totalChunks={chunks.Count}  pruned={PrunedIndexChunks}  active={activeRanges.Count}");

        // The writer owns the rent / IsFull / yield cycle and the trailing-batch
        // ownership across the three execution branches. Its Flush() in the finally
        // recovers the leftover on mid-fill exceptions and upstream throws during
        // the next MoveNextAsync. All three branches accumulate through it; the
        // ReferenceEquals lookup-check guards against schema drift mid-stream.
        RowCopyOutputWriter writer = new(context);

        try
        {
            // When the provider supports seeking and equality predicates have
            // index hits, seek directly to matching rows rather than reading entire chunks.
            if (_filterHint is not null
                && CollectExactSeekPositions(_filterHint, provider, chunks, activeChunkIndexes, context.Store, out int? compositeHits) is List<long> exactPositions)
            {
                DatumActivity.Operators.Trace($"SCAN exact seek  table={TableProvider.QualifiedName}  positions={exactPositions.Count}");
                ExactSeekRowsFetched = exactPositions.Count;
                CompositeIndexSeekHits = compositeHits;

                await foreach (RowBatch batch in ExecuteExactSeekAsync(provider, exactPositions, writer, context).ConfigureAwait(false))
                {
                    yield return batch;
                }
            }
            else if (PrunedIndexChunks == 0 && !HasBitmapRowFilter)
            {
                await foreach (RowBatch batch in ExecuteStreamAllAsync(provider, writer, context).ConfigureAwait(false))
                {
                    yield return batch;
                }
            }
            else
            {
                await foreach (RowBatch batch in ExecuteRangeScanWithBitmapAsync(provider, sourceIndex, activeRanges, writer, context).ConfigureAwait(false))
                {
                    yield return batch;
                }
            }
        }
        finally
        {
            if (writer.Flush() is RowBatch leftover) context.ReturnRowBatch(leftover);
        }
    }

    /// <summary>
    /// Exact-seek path: point-seeks each pre-collected absolute row position
    /// via the provider's <see cref="ISeekSession"/>. Used when at least one
    /// indexed equality / BETWEEN / IN predicate in the filter resolves to a
    /// concrete position set.
    /// </summary>
    private async IAsyncEnumerable<RowBatch> ExecuteExactSeekAsync(
        ITableProvider provider,
        List<long> exactPositions,
        RowCopyOutputWriter writer,
        ExecutionContext context)
    {
        CancellationToken cancellationToken = context.CancellationToken;
        using ISeekSession seekSession = provider.OpenSeekSession(_requiredColumns, context.Store);

        foreach (long rowPosition in exactPositions)
        {
            await foreach (RowBatch inputBatch in seekSession.SeekAsync(
                rowPosition, 1, cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    for (int i = 0; i < inputBatch.Count; i++)
                    {
                        if (writer.Add(inputBatch, i) is RowBatch ready) yield return ready;
                    }
                }
                finally
                {
                    context.ReturnRowBatch(inputBatch);
                }
            }
        }

        if (writer.Flush() is RowBatch trailing) yield return trailing;
    }

    /// <summary>
    /// Stream-all path: passes the filter hint to the provider and streams
    /// every row through, materialising each into a <c>context.Store</c>-bound
    /// output batch. Used when no chunk was pruned and no bitmap row filter
    /// applies — the per-chunk seek path would just re-walk the same rows
    /// with extra overhead.
    /// </summary>
    /// <remarks>
    /// The copy through <c>RentAndCopyToOutput</c> looks wasteful but it's
    /// the price of the one-arena-per-query invariant: providers manage their
    /// own arenas (mmap, decode buffers); the scan re-binds into the query's
    /// single arena so downstream operators can read without "which arena?"
    /// routing. ScanOperator is the boundary where that re-binding happens.
    /// </remarks>
    private async IAsyncEnumerable<RowBatch> ExecuteStreamAllAsync(
        ITableProvider provider,
        RowCopyOutputWriter writer,
        ExecutionContext context)
    {
        CancellationToken cancellationToken = context.CancellationToken;

        await foreach (RowBatch inputBatch in provider.ScanAsync(
            _requiredColumns, _filterHint, context.Store, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                for (int i = 0; i < inputBatch.Count; i++)
                {
                    if (writer.Add(inputBatch, i) is RowBatch ready) yield return ready;
                }
            }
            finally
            {
                context.ReturnRowBatch(inputBatch);
            }
        }

        if (writer.Flush() is RowBatch trailing) yield return trailing;
    }

    /// <summary>
    /// Per-chunk seek path: walks the surviving (non-pruned) chunk ranges and
    /// seeks each contiguous range. When a bitmap row mask exists for the
    /// chunk's filter predicates, each row is gated on its mask bit.
    /// </summary>
    private async IAsyncEnumerable<RowBatch> ExecuteRangeScanWithBitmapAsync(
        ITableProvider provider,
        SourceIndex sourceIndex,
        List<(long Start, long End, int ChunkIndex)> activeRanges,
        RowCopyOutputWriter writer,
        ExecutionContext context)
    {
        CancellationToken cancellationToken = context.CancellationToken;
        using ISeekSession seekSession = provider.OpenSeekSession(_requiredColumns, context.Store);

        foreach ((long start, long end, int activeChunkIndex) in activeRanges)
        {
            int count = (int)(end - start);
            byte[]? bitmapMask = BitmapRowMaskBuilder.Build(
                _filterHint, sourceIndex.BitmapIndexes, activeChunkIndex, count, context.Store);

            if (bitmapMask is not null)
            {
                int rowInChunk = 0;
                await foreach (RowBatch inputBatch in seekSession.SeekAsync(
                    start, count, cancellationToken).ConfigureAwait(false))
                {
                    try
                    {
                        for (int i = 0; i < inputBatch.Count; i++)
                        {
                            if (BitmapRowMaskBuilder.IsBitSet(bitmapMask, rowInChunk))
                            {
                                if (writer.Add(inputBatch, i) is RowBatch ready) yield return ready;
                            }

                            rowInChunk++;
                        }
                    }
                    finally
                    {
                        context.ReturnRowBatch(inputBatch);
                    }
                }
            }
            else
            {
                await foreach (RowBatch inputBatch in seekSession.SeekAsync(
                    start, count, cancellationToken).ConfigureAwait(false))
                {
                    try
                    {
                        for (int i = 0; i < inputBatch.Count; i++)
                        {
                            if (writer.Add(inputBatch, i) is RowBatch ready) yield return ready;
                        }
                    }
                    finally
                    {
                        context.ReturnRowBatch(inputBatch);
                    }
                }
            }
        }

        if (writer.Flush() is RowBatch trailing) yield return trailing;
    }

    /// <summary>
    /// Attempts to collect row positions from index-seekable predicates (equality,
    /// BETWEEN, IN) in the filter hint. Only extracts from top-level AND chains —
    /// OR predicates are not eligible for index seek. When multiple indexed predicates
    /// exist, uses the most selective (fewest matches).
    /// </summary>
    /// <returns>Sorted list of absolute row positions, or <c>null</c> if no seek is possible.</returns>
    private static List<long>? CollectExactSeekPositions(
        Expression filterHint,
        ITableProvider provider,
        IReadOnlyList<IndexChunk> chunks,
        HashSet<int> activeChunkIndexes, Arena arena,
        out int? compositeHits)
    {
        SeekPlanningContext predicates = new(filterHint, arena);
        SeekPlanner planner = new(chunks, activeChunkIndexes);
        Schema providerSchema = provider.GetSchema();

        // Strategies run in series; each submits zero or more candidate
        // position lists to the planner. The planner retains the
        // fewest-positions winner across all of them, and (via
        // SubmitCompositeEntries) tracks the composite-index counter
        // independently of whether the composite path wins the tiebreak.
        ISeekStrategy[] strategies =
        [
            new EqualitySeekStrategy(),
            new BetweenSeekStrategy(),
            new InSeekStrategy(),
            new CompositeSeekStrategy(),
        ];

        foreach (ISeekStrategy strategy in strategies)
        {
            strategy.Contribute(predicates, provider, providerSchema, planner, arena);
        }

        compositeHits = planner.CompositeHits;
        return planner.Finalize();
    }

}
