using System.Runtime.CompilerServices;
using DatumIngest.Catalog;
using DatumIngest.Diagnostics;
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
public sealed class ScanOperator : IQueryOperator
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
    public OperatorPlanDescription DescribeForExplain()
    {
        SourceIndex? sourceIndex = TableProvider.GetSourceIndex();

        Dictionary<string, string> properties = new()
        {
            ["table"] = TableProvider.Name,
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
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
    {
        CancellationToken cancellationToken = context.CancellationToken;
        SourceIndex? sourceIndex = TableProvider.GetSourceIndex();

        ExecutionTracer.Write($"SCAN start  table={TableProvider.Name}  hasIndex={sourceIndex is not null}  filterHint={_filterHint is not null}  tableRowCount={TableRowCount}");
        ExecutionTracer.Write($"SCAN path  table={TableProvider.Name}  indexPruning={HasIndexPruning}");

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
                _requiredColumns, _filterHint, context.Store, cancellationToken).ConfigureAwait(false))
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

        // Build a set of non-pruned chunk row ranges and track active chunk indexes.
        List<(long Start, long End, int ChunkIndex)> activeRanges = new();
        HashSet<int> activeChunkIndexes = new();

        for (int chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
        {
            IndexChunk chunk = chunks[chunkIndex];
            bool pruned = false;

            // Statistics-based pruning: check filter predicates against min/max stats.
            if (!pruned && _filterHint is not null)
            {
                using ColumnStatisticsRangeLookup statistics = chunk.CreateStatisticsLookup();

                if (StatisticsPredicateEvaluator.CanSkipPartition(_filterHint, statistics, context.Store))
                {
                    if (chunkIndex == 0)
                    {
                        ExecutionTracer.Write($"SCAN PRUNE chunk=0  reason=zonemap  table={TableProvider.Name}  statsKeys=[{string.Join(",", statistics.Keys)}]");
                    }
                    pruned = true;
                }
            }

            // Bloom-filter-based pruning: check if any build-side join key could
            // be present in this chunk. If no key could possibly match, skip it.
            if (!pruned && _bloomPruningKeys is not null && bloomFilters is not null)
            {
                foreach (KeyValuePair<string, IReadOnlyCollection<DataValue>> entry in _bloomPruningKeys)
                {
                    if (bloomFilters.TryGetFilter(entry.Key, chunkIndex, out BloomFilter? filter))
                    {
                        bool anyMayMatch = false;
                        foreach (DataValue keyValue in entry.Value)
                        {
                            if (filter.MayContain(keyValue, context.Store))
                            {
                                anyMayMatch = true;
                                break;
                            }
                        }

                        if (!anyMayMatch)
                        {
                            if (chunkIndex == 0) ExecutionTracer.Write($"SCAN PRUNE chunk=0  reason=bloom  table={TableProvider.Name}");
                            pruned = true;
                            break;
                        }
                    }
                }
            }

            // Sorted-index-based pruning via join keys: if build-side key values
            // were passed and sorted indexes exist, check whether any build key
            // is present in this chunk. If none are, skip the chunk.
            if (!pruned && _sortedIndexPruningKeys is not null)
            {
                foreach (KeyValuePair<string, IReadOnlyCollection<DataValue>> entry in _sortedIndexPruningKeys)
                {
                    if (provider.TryGetColumnIndex(entry.Key, out IColumnIndex? index))
                    {
                        bool anyPresent = false;
                        foreach (DataValue keyValue in entry.Value)
                        {
                            IReadOnlySet<int> matchingChunks = index.FindChunksContaining(keyValue);
                            if (matchingChunks.Contains(chunkIndex))
                            {
                                anyPresent = true;
                                break;
                            }
                        }

                        if (!anyPresent)
                        {
                            if (chunkIndex == 0) ExecutionTracer.Write($"SCAN PRUNE chunk=0  reason=sorted_join_key  table={TableProvider.Name}");
                            pruned = true;
                            break;
                        }
                    }
                }
            }

            // Sorted-index-based pruning: if equality predicates are present and
            // a column index exists, check whether the chunk contains the key.
            if (!pruned && _filterHint is not null)
            {
                if (ShouldPruneWithColumnIndexes(_filterHint, provider, chunkIndex, context.Store))
                {
                    if (chunkIndex == 0) ExecutionTracer.Write($"SCAN PRUNE chunk=0  reason=column_index  table={TableProvider.Name}");
                    pruned = true;
                }
            }

            // Bitmap-index-based pruning: for equality predicates on columns with
            // bitmap indexes, check whether the value appears in this chunk.
            if (!pruned && _filterHint is not null && sourceIndex.BitmapIndexes is not null)
            {
                if (ShouldPruneWithBitmapIndexes(_filterHint, sourceIndex.BitmapIndexes, chunkIndex, context.Store))
                {
                    if (chunkIndex == 0) ExecutionTracer.Write($"SCAN PRUNE chunk=0  reason=bitmap  table={TableProvider.Name}");
                    pruned = true;
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

        ExecutionTracer.Write($"SCAN index pruning  table={TableProvider.Name}  totalChunks={chunks.Count}  pruned={PrunedIndexChunks}  active={activeRanges.Count}");

        // Invariant: outputBatch != null ⟺ producer still owns it. Yielding transfers
        // ownership, so we null the local *before* yield. The outer finally cleans up
        // only the not-yet-yielded leftover, closing the leak window for mid-fill
        // exceptions and upstream throws during the next MoveNextAsync. The middle branch
        // (pure pass-through) doesn't accumulate, so it's safe inside the same try.
        RowBatch? outputBatch = null;

        try
        {
            // When the provider supports seeking and equality predicates have
            // index hits, seek directly to matching rows rather than reading entire chunks.
            if (_filterHint is not null
                && CollectExactSeekPositions(_filterHint, provider, chunks, activeChunkIndexes, context.Store) is List<long> exactPositions)
            {
                ExecutionTracer.Write($"SCAN exact seek  table={TableProvider.Name}  positions={exactPositions.Count}");
                ExactSeekRowsFetched = exactPositions.Count;

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
                                outputBatch ??= context.RentRowBatch(inputBatch.ColumnLookup);

                                context.Pool.RentAndCopyToOutput(inputBatch, i, outputBatch);

                                if (outputBatch.IsFull)
                                {
                                    RowBatch toYield = outputBatch;
                                    outputBatch = null;
                                    yield return toYield;
                                }
                            }
                        }
                        finally
                        {
                            context.ReturnRowBatch(inputBatch);
                        }
                    }
                }

                if (outputBatch is not null)
                {
                    RowBatch toYield = outputBatch;
                    outputBatch = null;
                    yield return toYield;
                }

                yield break;
            }
            else if (PrunedIndexChunks == 0 && !HasBitmapRowFilter)
            {
                // No pruning and no bitmap row filtering — stream all rows from the
                // provider, materialising each into a context.Store-bound output
                // batch. The copy looks wasteful but it's the price of the
                // one-arena-per-query invariant: providers manage their own arenas
                // (mmap, decode buffers); we re-bind into the query's single arena
                // so downstream operators can read without "which arena?" routing.
                // ScanOperator is the boundary where that re-binding happens.
                await foreach (RowBatch inputBatch in provider.ScanAsync(_requiredColumns, _filterHint, context.Store, cancellationToken).ConfigureAwait(false))
                {
                    try
                    {
                        for (int i = 0; i < inputBatch.Count; i++)
                        {
                            outputBatch ??= context.RentRowBatch(inputBatch.ColumnLookup);
                            context.Pool.RentAndCopyToOutput(inputBatch, i, outputBatch);

                            if (outputBatch.IsFull)
                            {
                                RowBatch toYield = outputBatch;
                                outputBatch = null;
                                yield return toYield;
                            }
                        }
                    }
                    finally
                    {
                        context.ReturnRowBatch(inputBatch);
                    }
                }

                if (outputBatch is not null)
                {
                    RowBatch toYield = outputBatch;
                    outputBatch = null;
                    yield return toYield;
                }

                yield break;
            }
            else
            {
                using ISeekSession seekSession = provider.OpenSeekSession(_requiredColumns, context.Store);

                foreach ((long start, long end, int activeChunkIndex) in activeRanges)
                {
                    int count = (int)(end - start);
                    byte[]? bitmapMask = EvaluateBitmapFilter(
                        _filterHint, sourceIndex.BitmapIndexes, activeChunkIndex, count, context.Store);

                    if (bitmapMask is not null)
                    {
                        int rowInChunk = 0;

                        await foreach (RowBatch inputBatch in seekSession.SeekAsync(start, count, cancellationToken).ConfigureAwait(false))
                        {
                            try
                            {
                                for (int i = 0; i < inputBatch.Count; i++)
                                {
                                    if (IsBitmapBitSet(bitmapMask, rowInChunk))
                                    {
                                        outputBatch ??= context.RentRowBatch(inputBatch.ColumnLookup);

                                        context.Pool.RentAndCopyToOutput(inputBatch, i, outputBatch);

                                        if (outputBatch.IsFull)
                                        {
                                            RowBatch toYield = outputBatch;
                                            outputBatch = null;
                                            yield return toYield;
                                        }
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
                        await foreach (RowBatch inputBatch in seekSession.SeekAsync(start, count, cancellationToken).ConfigureAwait(false))
                        {
                            try
                            {
                                for (int i = 0; i < inputBatch.Count; i++)
                                {
                                    outputBatch ??= context.RentRowBatch(inputBatch.ColumnLookup);

                                    context.Pool.RentAndCopyToOutput(inputBatch, i, outputBatch);

                                    if (outputBatch.IsFull)
                                    {
                                        RowBatch toYield = outputBatch;
                                        outputBatch = null;
                                        yield return toYield;
                                    }
                                }
                            }
                            finally
                            {
                                context.ReturnRowBatch(inputBatch);
                            }
                        }
                    }
                }

                if (outputBatch is not null)
                {
                    RowBatch toYield = outputBatch;
                    outputBatch = null;
                    yield return toYield;
                }

                yield break;
            }
        }
        finally
        {
            if (outputBatch is not null)
            {
                context.ReturnRowBatch(outputBatch);
            }
        }
    }

    /// <summary>
    /// Checks whether a chunk can be pruned based on sorted value indexes.
    /// Extracts equality predicates (column = literal) from the filter expression
    /// and checks the sorted index to see if the chunk contains any matching key.
    /// </summary>
    private static bool ShouldPruneWithColumnIndexes(
        Expression filterHint, ITableProvider provider, int chunkIndex, Arena arena)
    {
        // Walk the expression tree looking for equality comparisons of the form
        // column = literal. Each such predicate can rule out chunks that do not
        // contain the literal in their sorted index.
        return CheckExpressionForPruning(filterHint, provider, chunkIndex, arena);
    }

    /// <summary>
    /// Checks whether a chunk can be pruned based on bitmap indexes.
    /// Walks the filter expression tree extracting equality predicates
    /// (<c>column = literal</c>) and IN predicates against bitmap-indexed columns.
    /// AND chains prune when any branch proves the chunk empty; OR chains
    /// require all branches to prove the chunk empty.
    /// </summary>
    private static bool ShouldPruneWithBitmapIndexes(
        Expression filterHint, BitmapIndexSet bitmapIndexes, int chunkIndex, Arena arena)
    {
        return CheckExpressionForBitmapPruning(filterHint, bitmapIndexes, chunkIndex, arena);
    }

    private static bool CheckExpressionForBitmapPruning(
        Expression expression, BitmapIndexSet bitmapIndexes, int chunkIndex, Arena arena)
    {
        if (expression is BinaryExpression binary)
        {
            if (binary.Operator == BinaryOperator.And)
            {
                // AND: prune if either side proves the chunk empty.
                return CheckExpressionForBitmapPruning(binary.Left, bitmapIndexes, chunkIndex, arena)
                    || CheckExpressionForBitmapPruning(binary.Right, bitmapIndexes, chunkIndex, arena);
            }

            if (binary.Operator == BinaryOperator.Equal)
            {
                return CheckEqualityForBitmapPruning(binary.Left, binary.Right, bitmapIndexes, chunkIndex, arena);
            }
        }

        if (expression is InExpression inExpression && !inExpression.Negated)
        {
            return CheckInForBitmapPruning(inExpression, bitmapIndexes, chunkIndex, arena);
        }

        return false;
    }

    private static bool CheckEqualityForBitmapPruning(
        Expression left, Expression right, BitmapIndexSet bitmapIndexes, int chunkIndex, Arena arena)
    {
        string? columnName = null;
        object? rawLiteral = null;

        if (left is ColumnReference columnRef && right is LiteralExpression literal)
        {
            columnName = columnRef.ColumnName;
            rawLiteral = literal.Value;
        }
        else if (left is LiteralExpression literalLeft && right is ColumnReference columnRight)
        {
            columnName = columnRight.ColumnName;
            rawLiteral = literalLeft.Value;
        }

        if (columnName is null || rawLiteral is null)
        {
            return false;
        }

        if (!bitmapIndexes.TryGetIndex(columnName, out BitmapColumnIndex? bitmapIndex))
        {
            return false;
        }

        DataValue literalValue = DataValue.FromLiteral(rawLiteral, arena);
        return literalValue.IsInline
            && !bitmapIndex.ChunkContainsValue(literalValue, chunkIndex);
    }

    private static bool CheckInForBitmapPruning(
        InExpression inExpression, BitmapIndexSet bitmapIndexes, int chunkIndex, Arena arena)
    {
        if (inExpression.Expression is not ColumnReference columnRef)
        {
            return false;
        }

        if (!bitmapIndexes.TryGetIndex(columnRef.ColumnName, out BitmapColumnIndex? bitmapIndex))
        {
            return false;
        }

        // If any IN value exists in this chunk, the chunk cannot be pruned.
        foreach (Expression valueExpression in inExpression.Values)
        {
            if (valueExpression is not LiteralExpression { Value: not null } literal)
            {
                return false;
            }

            DataValue value = DataValue.FromLiteral(literal.Value, arena);

            if (value.IsInline && bitmapIndex.ChunkContainsValue(value, chunkIndex))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Builds a combined bitmap row-inclusion mask for a single chunk by evaluating
    /// bitmap-eligible sub-expressions in the filter hint. Returns <c>null</c> when
    /// no bitmap-eligible predicates exist (all rows should pass through).
    /// </summary>
    private static byte[]? EvaluateBitmapFilter(
        Expression? filterHint, BitmapIndexSet? bitmapIndexes,
        int chunkIndex, int rowCount, Arena arena)
    {
        if (filterHint is null || bitmapIndexes is null || bitmapIndexes.Count == 0)
        {
            return null;
        }

        return EvaluateBitmapExpression(filterHint, bitmapIndexes, chunkIndex, rowCount, arena);
    }

    /// <summary>
    /// Recursively evaluates a filter expression against bitmap indexes, composing
    /// per-value bitmaps with AND/OR/NOT to produce a row-inclusion bitset.
    /// Returns <c>null</c> when the sub-expression has no bitmap-eligible predicates.
    /// </summary>
    private static byte[]? EvaluateBitmapExpression(
        Expression expression, BitmapIndexSet bitmapIndexes,
        int chunkIndex, int rowCount, Arena arena)
    {
        if (expression is BinaryExpression binary)
        {
            if (binary.Operator == BinaryOperator.And)
            {
                byte[]? leftBits = EvaluateBitmapExpression(binary.Left, bitmapIndexes, chunkIndex, rowCount, arena);
                byte[]? rightBits = EvaluateBitmapExpression(binary.Right, bitmapIndexes, chunkIndex, rowCount, arena);

                if (leftBits is not null && rightBits is not null)
                {
                    return BitmapComposer.And(leftBits, rightBits);
                }

                // Return whichever side produced a bitmap (AND with unknown = keep the known constraint).
                return leftBits ?? rightBits;
            }

            if (binary.Operator == BinaryOperator.Or)
            {
                byte[]? leftBits = EvaluateBitmapExpression(binary.Left, bitmapIndexes, chunkIndex, rowCount, arena);
                byte[]? rightBits = EvaluateBitmapExpression(binary.Right, bitmapIndexes, chunkIndex, rowCount, arena);

                if (leftBits is not null && rightBits is not null)
                {
                    return BitmapComposer.Or(leftBits, rightBits);
                }

                // OR with an unknown side: cannot constrain (either side might match any row).
                return null;
            }

            if (binary.Operator == BinaryOperator.Equal)
            {
                return EvaluateBitmapEquality(binary.Left, binary.Right, bitmapIndexes, chunkIndex, rowCount, arena);
            }

            if (binary.Operator == BinaryOperator.NotEqual)
            {
                byte[]? equalBits = EvaluateBitmapEquality(
                    binary.Left, binary.Right, bitmapIndexes, chunkIndex, rowCount, arena);

                if (equalBits is not null)
                {
                    return BitmapComposer.Not(equalBits, rowCount);
                }

                return null;
            }
        }

        if (expression is InExpression inExpression && !inExpression.Negated)
        {
            return EvaluateBitmapIn(inExpression, bitmapIndexes, chunkIndex, rowCount, arena);
        }

        return null;
    }

    private static byte[]? EvaluateBitmapEquality(
        Expression left, Expression right, BitmapIndexSet bitmapIndexes,
        int chunkIndex, int rowCount, Arena arena)
    {
        string? columnName = null;
        object? rawLiteral = null;

        if (left is ColumnReference columnRef && right is LiteralExpression literal)
        {
            columnName = columnRef.ColumnName;
            rawLiteral = literal.Value;
        }
        else if (left is LiteralExpression literalLeft && right is ColumnReference columnRight)
        {
            columnName = columnRight.ColumnName;
            rawLiteral = literalLeft.Value;
        }

        if (columnName is null || rawLiteral is null)
        {
            return null;
        }

        if (!bitmapIndexes.TryGetIndex(columnName, out BitmapColumnIndex? bitmapIndex))
        {
            return null;
        }

        DataValue literalValue = DataValue.FromLiteral(rawLiteral, arena);
        if (!literalValue.IsInline)
        {
            return null;
        }

        ChunkBitmap bitmap = bitmapIndex.GetChunkBitmap(literalValue, chunkIndex);
        return bitmap.Bits.ToArray();
    }

    private static byte[]? EvaluateBitmapIn(
        InExpression inExpression, BitmapIndexSet bitmapIndexes,
        int chunkIndex, int rowCount, Arena arena)
    {
        if (inExpression.Expression is not ColumnReference columnRef)
        {
            return null;
        }

        if (!bitmapIndexes.TryGetIndex(columnRef.ColumnName, out BitmapColumnIndex? bitmapIndex))
        {
            return null;
        }

        byte[]? result = null;

        foreach (Expression valueExpression in inExpression.Values)
        {
            if (valueExpression is not LiteralExpression { Value: not null } literal)
            {
                return null;
            }

            DataValue value = DataValue.FromLiteral(literal.Value, arena);
            if (!value.IsInline)
            {
                return null;
            }

            ChunkBitmap bitmap = bitmapIndex.GetChunkBitmap(value, chunkIndex);

            if (result is null)
            {
                result = bitmap.Bits.ToArray();
            }
            else
            {
                BitmapComposer.Or(bitmap.Bits, result, result);
            }
        }

        return result;
    }

    /// <summary>
    /// Returns whether the bit at the given row offset is set in a bitmap mask.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsBitmapBitSet(byte[] mask, int rowOffset)
    {
        int byteIndex = rowOffset >> 3;
        int bitIndex = rowOffset & 7;
        return (mask[byteIndex] & (1 << bitIndex)) != 0;
    }

    private static bool CheckExpressionForPruning(
        Expression expression, ITableProvider provider, int chunkIndex, Arena arena)
    {
        if (expression is BinaryExpression binary)
        {
            if (binary.Operator == BinaryOperator.And)
            {
                // AND: prune if either side says we can prune.
                return CheckExpressionForPruning(binary.Left, provider, chunkIndex, arena)
                    || CheckExpressionForPruning(binary.Right, provider, chunkIndex, arena);
            }

            if (binary.Operator == BinaryOperator.Equal)
            {
                return CheckEqualityForPruning(binary.Left, binary.Right, provider, chunkIndex, arena);
            }

            if (binary.Operator is BinaryOperator.LessThan or BinaryOperator.LessThanOrEqual
                or BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual)
            {
                return CheckComparisonForPruning(
                    binary.Left, binary.Right, binary.Operator, provider, chunkIndex, arena);
            }
        }

        if (expression is BetweenExpression between && !between.Negated)
        {
            return CheckBetweenForPruning(between, provider, chunkIndex, arena);
        }

        if (expression is InExpression inExpression && !inExpression.Negated)
        {
            return CheckInForPruning(inExpression, provider, chunkIndex, arena);
        }

        return false;
    }

    private static bool CheckEqualityForPruning(
        Expression left, Expression right, ITableProvider provider, int chunkIndex, Arena arena)
    {
        // Match: column = literal  or  literal = column
        string? columnName = null;
        object? rawLiteral = null;

        if (left is ColumnReference columnRef && right is LiteralExpression literal)
        {
            columnName = columnRef.ColumnName;
            rawLiteral = literal.Value;
        }
        else if (left is LiteralExpression literalLeft && right is ColumnReference columnRight)
        {
            columnName = columnRight.ColumnName;
            rawLiteral = literalLeft.Value;
        }

        if (columnName is null || rawLiteral is null)
        {
            return false;
        }

        if (!provider.TryGetColumnIndex(columnName, out IColumnIndex? index))
        {
            return false;
        }

        DataValue literalValue = DataValue.FromLiteral(rawLiteral, arena);

        if (!literalValue.IsInline)
        {
            return false;
        }

        IReadOnlySet<int> matchingChunks = index.FindChunksContaining(literalValue);
        return !matchingChunks.Contains(chunkIndex);
    }

    /// <summary>
    /// Checks whether a chunk can be pruned based on a range comparison
    /// (<c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c>, <c>&gt;=</c>) against a sorted index.
    /// Handles both <c>column op literal</c> and <c>literal op column</c> orientations.
    /// </summary>
    private static bool CheckComparisonForPruning(
        Expression left, Expression right, BinaryOperator op,
        ITableProvider provider, int chunkIndex, Arena arena)
    {
        string? columnName = null;
        object? rawLiteral = null;
        BinaryOperator effectiveOperator = op;

        if (left is ColumnReference columnRef && right is LiteralExpression literal)
        {
            columnName = columnRef.ColumnName;
            rawLiteral = literal.Value;
        }
        else if (left is LiteralExpression literalLeft && right is ColumnReference columnRight)
        {
            columnName = columnRight.ColumnName;
            rawLiteral = literalLeft.Value;
            effectiveOperator = FlipComparisonOperator(op);
        }

        if (columnName is null || rawLiteral is null)
        {
            return false;
        }

        if (!provider.TryGetColumnIndex(columnName, out IColumnIndex? index))
        {
            return false;
        }

        DataValue literalValue = DataValue.FromLiteral(rawLiteral, arena);

        if (!literalValue.IsInline)
        {
            return false;
        }

        IReadOnlySet<int> matchingChunks = effectiveOperator switch
        {
            BinaryOperator.LessThan => index.FindChunksLessThan(literalValue),
            BinaryOperator.LessThanOrEqual => index.FindChunksLessThanOrEqual(literalValue),
            BinaryOperator.GreaterThan => index.FindChunksGreaterThan(literalValue),
            BinaryOperator.GreaterThanOrEqual => index.FindChunksGreaterThanOrEqual(literalValue),
            _ => throw new InvalidOperationException($"Unexpected operator: {effectiveOperator}"),
        };

        return !matchingChunks.Contains(chunkIndex);
    }

    /// <summary>
    /// Flips a comparison operator to account for reversed operand order
    /// (e.g. <c>5 &lt; col</c> becomes <c>col &gt; 5</c>).
    /// </summary>
    private static BinaryOperator FlipComparisonOperator(BinaryOperator op)
    {
        return op switch
        {
            BinaryOperator.LessThan => BinaryOperator.GreaterThan,
            BinaryOperator.LessThanOrEqual => BinaryOperator.GreaterThanOrEqual,
            BinaryOperator.GreaterThan => BinaryOperator.LessThan,
            BinaryOperator.GreaterThanOrEqual => BinaryOperator.LessThanOrEqual,
            _ => op,
        };
    }

    /// <summary>
    /// Checks whether a chunk can be pruned based on a BETWEEN predicate
    /// by looking up the inclusive range in a sorted index.
    /// </summary>
    private static bool CheckBetweenForPruning(
        BetweenExpression between, ITableProvider provider, int chunkIndex, Arena arena)
    {
        if (between.Expression is not ColumnReference columnRef)
        {
            return false;
        }

        if (between.Low is not LiteralExpression { Value: not null } lowLiteral
            || between.High is not LiteralExpression { Value: not null } highLiteral)
        {
            return false;
        }

        if (!provider.TryGetColumnIndex(columnRef.ColumnName, out IColumnIndex? index))
        {
            return false;
        }

        DataValue low = DataValue.FromLiteral(lowLiteral.Value, arena);
        DataValue high = DataValue.FromLiteral(highLiteral.Value, arena);

        if (!low.IsInline || !high.IsInline)
        {
            return false;
        }

        IReadOnlySet<int> matchingChunks = index.FindChunksInRange(low, high);
        return !matchingChunks.Contains(chunkIndex);
    }

    /// <summary>
    /// Checks whether a chunk can be pruned based on an IN predicate
    /// by looking up each value in a sorted index.
    /// </summary>
    private static bool CheckInForPruning(
        InExpression inExpression, ITableProvider provider, int chunkIndex, Arena arena)
    {
        if (inExpression.Expression is not ColumnReference columnRef)
        {
            return false;
        }

        if (!provider.TryGetColumnIndex(columnRef.ColumnName, out IColumnIndex? index))
        {
            return false;
        }

        // If any IN value exists in this chunk, the chunk cannot be pruned.
        foreach (Expression valueExpression in inExpression.Values)
        {
            if (valueExpression is not LiteralExpression { Value: not null } literal)
            {
                return false;
            }

            DataValue value = DataValue.FromLiteral(literal.Value, arena);
            if (!value.IsInline)
            {
                return false;
            }

            IReadOnlySet<int> matchingChunks = index.FindChunksContaining(value);

            if (matchingChunks.Contains(chunkIndex))
            {
                return false;
            }
        }

        return true;
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
        HashSet<int> activeChunkIndexes, Arena arena)
    {
        List<long>? bestPositions = null;

        // Equality predicates: col = literal → FindExact
        List<(string Column, DataValue Value)> equalities = new();
        ExtractTopLevelEqualities(filterHint, equalities, arena);

        foreach ((string column, DataValue value) in equalities)
        {
            if (!provider.TryGetColumnIndex(column, out IColumnIndex? index))
            {
                continue;
            }

            IReadOnlyList<ValueIndexEntry> entries = index.FindExact(value);
            List<long> positions = CollectPositionsFromEntries(entries, chunks, activeChunkIndexes);

            if (bestPositions is null || positions.Count < bestPositions.Count)
            {
                bestPositions = positions;
            }
        }

        // BETWEEN predicates: col BETWEEN low AND high → FindRange
        List<(string Column, DataValue Low, DataValue High)> betweens = new();
        ExtractTopLevelBetweens(filterHint, betweens, arena);

        foreach ((string column, DataValue low, DataValue high) in betweens)
        {
            if (!provider.TryGetColumnIndex(column, out IColumnIndex? index))
            {
                continue;
            }

            IReadOnlyList<ValueIndexEntry> entries = index.FindRange(low, high);
            List<long> positions = CollectPositionsFromEntries(entries, chunks, activeChunkIndexes);

            if (bestPositions is null || positions.Count < bestPositions.Count)
            {
                bestPositions = positions;
            }
        }

        // IN predicates: col IN (v1, v2, ...) → union of FindExact per value
        List<(string Column, List<DataValue> Values)> inPredicates = new();
        ExtractTopLevelIns(filterHint, inPredicates, arena);

        foreach ((string column, List<DataValue> values) in inPredicates)
        {
            if (!provider.TryGetColumnIndex(column, out IColumnIndex? index))
            {
                continue;
            }

            List<long> positions = new();

            foreach (DataValue value in values)
            {
                IReadOnlyList<ValueIndexEntry> entries = index.FindExact(value);
                positions.AddRange(
                    CollectPositionsFromEntries(entries, chunks, activeChunkIndexes));
            }

            if (bestPositions is null || positions.Count < bestPositions.Count)
            {
                bestPositions = positions;
            }
        }

        if (bestPositions is null || bestPositions.Count == 0)
        {
            return bestPositions;
        }

        bestPositions.Sort();
        return bestPositions;
    }

    /// <summary>
    /// Converts index entries to absolute row positions, keeping only entries
    /// in active (non-pruned) chunks.
    /// </summary>
    private static List<long> CollectPositionsFromEntries(
        IReadOnlyList<ValueIndexEntry> entries,
        IReadOnlyList<IndexChunk> chunks,
        HashSet<int> activeChunkIndexes)
    {
        List<long> positions = new(entries.Count);

        foreach (ValueIndexEntry entry in entries)
        {
            if (activeChunkIndexes.Contains(entry.ChunkIndex))
            {
                long absoluteRow = chunks[entry.ChunkIndex].RowOffset
                    + entry.RowOffsetInChunk;
                positions.Add(absoluteRow);
            }
        }

        return positions;
    }

    /// <summary>
    /// Extracts <c>column = literal</c> equality predicates from the top-level
    /// AND chain of the filter expression. OR branches are ignored since they
    /// cannot guarantee that all matching rows are in the index result set.
    /// </summary>
    private static void ExtractTopLevelEqualities(
        Expression expression,
        List<(string Column, DataValue Value)> results, Arena arena)
    {
        if (expression is BinaryExpression binary)
        {
            if (binary.Operator == BinaryOperator.And)
            {
                ExtractTopLevelEqualities(binary.Left, results, arena);
                ExtractTopLevelEqualities(binary.Right, results, arena);
                return;
            }

            if (binary.Operator == BinaryOperator.Equal)
            {
                string? columnName = null;
                object? rawLiteral = null;

                if (binary.Left is ColumnReference columnRef
                    && binary.Right is LiteralExpression literal)
                {
                    columnName = columnRef.ColumnName;
                    rawLiteral = literal.Value;
                }
                else if (binary.Left is LiteralExpression literalLeft
                    && binary.Right is ColumnReference columnRight)
                {
                    columnName = columnRight.ColumnName;
                    rawLiteral = literalLeft.Value;
                }

                if (columnName is not null && rawLiteral is not null)
                {
                    DataValue literalValue = DataValue.FromLiteral(rawLiteral, arena);
                    if (!literalValue.IsInline) return;
                    results.Add((columnName, literalValue));
                }

            }
        }
    }

    /// <summary>
    /// Extracts <c>column BETWEEN low AND high</c> predicates from the top-level
    /// AND chain. Only non-negated BETWEEN with literal bounds is extracted.
    /// </summary>
    private static void ExtractTopLevelBetweens(
        Expression expression,
        List<(string Column, DataValue Low, DataValue High)> results, Arena arena)
    {
        if (expression is BinaryExpression binary && binary.Operator == BinaryOperator.And)
        {
            ExtractTopLevelBetweens(binary.Left, results, arena);
            ExtractTopLevelBetweens(binary.Right, results, arena);
            return;
        }

        if (expression is BetweenExpression between && !between.Negated
            && between.Expression is ColumnReference columnRef
            && between.Low is LiteralExpression { Value: not null } lowLiteral
            && between.High is LiteralExpression { Value: not null } highLiteral)
        {
            DataValue low = DataValue.FromLiteral(lowLiteral.Value, arena);
            DataValue high = DataValue.FromLiteral(highLiteral.Value, arena);

            if (!low.IsInline || !high.IsInline) return;

            results.Add((columnRef.ColumnName, low, high));
        }
    }

    /// <summary>
    /// Extracts <c>column IN (v1, v2, ...)</c> predicates from the top-level
    /// AND chain. Only non-negated IN with all-literal values is extracted.
    /// </summary>
    private static void ExtractTopLevelIns(
        Expression expression,
        List<(string Column, List<DataValue> Values)> results, Arena arena)
    {
        if (expression is BinaryExpression binary && binary.Operator == BinaryOperator.And)
        {
            ExtractTopLevelIns(binary.Left, results, arena);
            ExtractTopLevelIns(binary.Right, results, arena);
            return;
        }

        if (expression is InExpression inExpression && !inExpression.Negated
            && inExpression.Expression is ColumnReference columnRef)
        {
            List<DataValue> values = new(inExpression.Values.Count);

            foreach (Expression valueExpression in inExpression.Values)
            {
                if (valueExpression is not LiteralExpression { Value: not null } literal)
                {
                    return;
                }

                DataValue dv = DataValue.FromLiteral(literal.Value, arena);
                
                if (!dv.IsInline) return;

                values.Add(dv);
            }

            results.Add((columnRef.ColumnName, values));
        }
    }

}
