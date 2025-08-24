using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Indexing;
using DatumIngest.Indexing.Bitmap;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Reads rows from a table provider, applying projection pushdown
/// to skip unreferenced columns at the source. When the provider implements
/// <see cref="IFilterableTableProvider"/> and a filter hint is present,
/// the provider may skip entire partitions based on column statistics.
/// When a <see cref="SourceIndex"/> is available, chunk-level statistics
/// enable partition pruning for any provider type. Bloom filter pruning
/// allows join operators to pre-filter chunks by providing build-side key
/// values: chunks where no build-side key could possibly exist are skipped.
/// When the provider implements <see cref="IPartitionedTableProvider"/> and
/// <see cref="ExecutionContext.DegreeOfParallelism"/> is greater than one,
/// the scan is split into equal byte-range partitions and read in parallel
/// to maximise I/O and CPU utilisation on large files.
/// </summary>
public sealed class ScanOperator : IQueryOperator
{
    private readonly TableDescriptor _descriptor;
    private readonly IReadOnlySet<string>? _requiredColumns;
    private Expression? _filterHint;
    private SourceIndex? _sourceIndex;
    private Dictionary<string, IReadOnlyCollection<DataValue>>? _bloomPruningKeys;
    private Dictionary<string, IReadOnlyCollection<DataValue>>? _sortedIndexPruningKeys;

    /// <summary>
    /// Minimum estimated row count that activates the parallel partitioned scan path.
    /// Below this threshold the Channel fan-out overhead exceeds the I/O savings.
    /// </summary>
    private const long ParallelScanMinRows = 100_000;

    /// <summary>
    /// Creates a scan operator for the given table.
    /// </summary>
    /// <param name="descriptor">Table descriptor identifying the data source.</param>
    /// <param name="requiredColumns">Columns needed downstream; null means all columns.</param>
    public ScanOperator(TableDescriptor descriptor, IReadOnlySet<string>? requiredColumns)
    {
        _descriptor = descriptor;
        _requiredColumns = requiredColumns;
    }

    /// <summary>The table descriptor this operator scans.</summary>
    public TableDescriptor Descriptor => _descriptor;

    /// <summary>The set of columns requested for projection pushdown.</summary>
    public IReadOnlySet<string>? RequiredColumns => _requiredColumns;

    /// <summary>The advisory filter hint passed to filterable providers, or <c>null</c>.</summary>
    public Expression? FilterHint => _filterHint;

    /// <summary>
    /// Estimated row count from <see cref="ProviderCapabilities"/>, set at plan time.
    /// <c>null</c> when the provider cannot report a row count.
    /// </summary>
    public long? EstimatedRowCount { get; set; }

    /// <summary>
    /// Per-column statistics from a <see cref="QueryResultsManifest"/>, set at plan time.
    /// <c>null</c> when no manifest is available for this table.
    /// </summary>
    public IReadOnlyDictionary<string, FeatureManifest>? ColumnStatistics { get; set; }

    /// <summary>The source index for chunk-based pruning, or <c>null</c> if none is available.</summary>
    public SourceIndex? SourceIndex => _sourceIndex;

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
    /// Attaches a source index for chunk-based partition pruning.
    /// </summary>
    /// <param name="index">The source index to use during execution.</param>
    public void SetSourceIndex(SourceIndex index)
    {
        _sourceIndex = index;
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

    /// <summary>
    /// The most recent <see cref="IFilterableTableProvider"/> used during execution,
    /// or <c>null</c> if the provider is not filterable. Used by the explain/instrumentation
    /// layer to retrieve pruning statistics after execution.
    /// </summary>
    public IFilterableTableProvider? LastFilterableProvider { get; private set; }

    /// <inheritdoc/>
    public OperatorPlanDescription DescribeForExplain()
    {
        Dictionary<string, string> properties = new()
        {
            ["table"] = _descriptor.Name,
            ["provider"] = _descriptor.Provider,
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

        if (_filterHint is not null && _sourceIndex is not null)
        {
            pruningCapabilities.Add(new PruningCapability(
                PruningTechnique.StatisticsPruning, [], pendingRuntime: false));
        }

        if (_sourceIndex?.BloomFilters is not null)
        {
            List<string> bloomColumns = [.. _sourceIndex.BloomFilters.ColumnNames];
            if (bloomColumns.Count > 0)
            {
                pruningCapabilities.Add(new PruningCapability(
                    PruningTechnique.BloomFilterPruning, bloomColumns, pendingRuntime: true));
            }
        }

        if (_sourceIndex?.SortedIndexes is { Count: > 0 } sortedIndexes)
        {
            pruningCapabilities.Add(new PruningCapability(
                PruningTechnique.SortedIndexPruning, [.. sortedIndexes.ColumnNames], pendingRuntime: false));
        }

        if (_sourceIndex?.BitmapIndexes is { Count: > 0 } bitmapIndexes)
        {
            pruningCapabilities.Add(new PruningCapability(
                PruningTechnique.BitmapPruning, [.. bitmapIndexes.ColumnNames], pendingRuntime: false));
        }

        AccessStrategyDescription accessStrategy = new(
            AccessMethod.TableScan,
            pruningCapabilities.Count > 0 ? pruningCapabilities : null);

        return new OperatorPlanDescription("Scan")
        {
            Properties = properties,
            EstimatedRows = EstimatedRowCount,
            AccessStrategy = accessStrategy,
        };
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<Row> ExecuteAsync(ExecutionContext context)
    {
        CancellationToken cancellationToken = context.CancellationToken;
        ITableProvider provider = context.Catalog.CreateProvider(_descriptor);

        // When a source index is available and either a filter hint, bloom pruning
        // keys, sorted index pruning keys, bitmap indexes, or column indexes are present,
        // apply chunk-level pruning.
        bool hasIndexPruning = _sourceIndex is not null
            && (_filterHint is not null || _bloomPruningKeys is not null
                || _sortedIndexPruningKeys is not null
                || _sourceIndex.SortedIndexes is not null
                || _sourceIndex.BPlusTreeIndexes is not null
                || _sourceIndex.BitmapIndexes is not null);

        if (hasIndexPruning)
        {
            await foreach (Row row in ExecuteWithIndexPruningAsync(
                provider, cancellationToken).ConfigureAwait(false))
            {
                yield return row;
            }
        }
        else
        {
            // Parallel scan: when the provider supports byte-range partitioning,
            // DOP > 1, and the table is large enough, fan out to N concurrent
            // reader tasks and merge their output through a channel. This hides
            // both OS I/O latency and per-row CPU cost for large files.
            // Only activated when no filter hint is present; filterable providers
            // manage their own partition strategy.
            if (context.DegreeOfParallelism > 1
                && EstimatedRowCount is >= ParallelScanMinRows
                && _filterHint is null
                && provider is IPartitionedTableProvider partitionedProvider)
            {
                IReadOnlyList<IAsyncEnumerable<Row>>? partitions =
                    await partitionedProvider.OpenPartitionsAsync(
                        _descriptor, _requiredColumns,
                        context.DegreeOfParallelism,
                        cancellationToken).ConfigureAwait(false);

                if (partitions is { Count: > 1 })
                {
                    await foreach (Row row in MergePartitionsAsync(
                        partitions, context).ConfigureAwait(false))
                    {
                        yield return row;
                    }

                    yield break;
                }
            }

            IAsyncEnumerable<Row> rows;

            if (_filterHint is not null && provider is IFilterableTableProvider filterable)
            {
                LastFilterableProvider = filterable;
                rows = filterable.OpenAsync(
                    _descriptor, _requiredColumns, _filterHint, cancellationToken);
            }
            else
            {
                rows = provider.OpenAsync(_descriptor, _requiredColumns, cancellationToken);
            }

            await foreach (Row row in rows.ConfigureAwait(false))
            {
                yield return row;
            }
        }
    }

    private async IAsyncEnumerable<Row> ExecuteWithIndexPruningAsync(
        ITableProvider provider,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IReadOnlyList<IndexChunk> chunks = _sourceIndex!.Chunks;
        BloomFilterSet? bloomFilters = _sourceIndex.BloomFilters;
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
                Dictionary<string, ColumnStatisticsRange> statistics = new(StringComparer.OrdinalIgnoreCase);

                foreach (KeyValuePair<string, ChunkColumnStatistics> entry in chunk.ColumnStatistics)
                {
                    statistics[entry.Key] = entry.Value.ToColumnStatisticsRange();
                }

                if (StatisticsPredicateEvaluator.CanSkipPartition(_filterHint, statistics))
                {
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
                            if (filter.MayContain(keyValue))
                            {
                                anyMayMatch = true;
                                break;
                            }
                        }

                        if (!anyMayMatch)
                        {
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
                    if (_sourceIndex.TryGetColumnIndex(entry.Key, out IColumnIndex? index))
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
                pruned = ShouldPruneWithColumnIndexes(_filterHint, _sourceIndex, chunkIndex);
            }

            // Bitmap-index-based pruning: for equality predicates on columns with
            // bitmap indexes, check whether the value appears in this chunk.
            if (!pruned && _filterHint is not null && _sourceIndex.BitmapIndexes is not null)
            {
                pruned = ShouldPruneWithBitmapIndexes(_filterHint, _sourceIndex.BitmapIndexes, chunkIndex);
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

        // When the provider supports seeking and equality predicates have
        // index hits, seek directly to matching rows rather than reading entire chunks.
        if (provider is ISeekableTableProvider seekable
            && _filterHint is not null)
        {
            List<long>? exactPositions = CollectExactSeekPositions(
                _filterHint, _sourceIndex, chunks, activeChunkIndexes);

            if (exactPositions is not null)
            {
                ExactSeekRowsFetched = exactPositions.Count;

                foreach (long rowPosition in exactPositions)
                {
                    await foreach (Row row in seekable.ReadRowRangeAsync(
                        _descriptor, _requiredColumns, rowPosition, 1,
                        cancellationToken).ConfigureAwait(false))
                    {
                        yield return row;
                    }
                }

                yield break;
            }
        }

        // Open the source stream (needed for non-seekable fallback and no-pruning path).
        IAsyncEnumerable<Row>? rows = null;

        IAsyncEnumerable<Row> OpenStream()
        {
            if (_filterHint is not null && provider is IFilterableTableProvider filterable)
            {
                LastFilterableProvider = filterable;
                return filterable.OpenAsync(
                    _descriptor, _requiredColumns, _filterHint, cancellationToken);
            }

            return provider.OpenAsync(_descriptor, _requiredColumns, cancellationToken);
        }

        // If no chunks were pruned and no bitmap row filtering is needed, stream all rows.
        bool hasBitmapRowFilter = _filterHint is not null
            && _sourceIndex.BitmapIndexes is not null
            && _sourceIndex.BitmapIndexes.Count > 0;

        if (PrunedIndexChunks == 0 && !hasBitmapRowFilter)
        {
            rows = OpenStream();

            await foreach (Row row in rows.ConfigureAwait(false))
            {
                yield return row;
            }

            yield break;
        }

        // When the provider supports seeking, read only the surviving chunks directly
        // instead of streaming all rows and discarding pruned ones.
        if (provider is ISeekableTableProvider chunkSeekable)
        {
            foreach ((long start, long end, int activeChunkIndex) in activeRanges)
            {
                int count = (int)(end - start);
                byte[]? bitmapMask = EvaluateBitmapFilter(
                    _filterHint, _sourceIndex.BitmapIndexes, activeChunkIndex, count);

                if (bitmapMask is not null)
                {
                    int rowInChunk = 0;

                    await foreach (Row row in chunkSeekable.ReadRowRangeAsync(
                        _descriptor, _requiredColumns, start, count,
                        cancellationToken).ConfigureAwait(false))
                    {
                        if (IsBitmapBitSet(bitmapMask, rowInChunk))
                        {
                            yield return row;
                        }

                        rowInChunk++;
                    }
                }
                else
                {
                    await foreach (Row row in chunkSeekable.ReadRowRangeAsync(
                        _descriptor, _requiredColumns, start, count,
                        cancellationToken).ConfigureAwait(false))
                    {
                        yield return row;
                    }
                }
            }

            yield break;
        }

        // Fallback: stream all rows and skip those in pruned chunks by row index.
        // When bitmap row filters apply, also skip non-matching rows within active chunks.
        rows = OpenStream();
        long rowIndex = 0;
        int rangeIndex = 0;
        byte[]? fallbackBitmapMask = null;
        int fallbackRowInChunk = 0;

        // Pre-compute bitmap mask for the first active chunk.
        if (rangeIndex < activeRanges.Count)
        {
            fallbackBitmapMask = EvaluateBitmapFilter(
                _filterHint, _sourceIndex.BitmapIndexes,
                activeRanges[rangeIndex].ChunkIndex,
                (int)(activeRanges[rangeIndex].End - activeRanges[rangeIndex].Start));
        }

        await foreach (Row row in rows.ConfigureAwait(false))
        {
            // Advance past ranges we've gone beyond.
            while (rangeIndex < activeRanges.Count && rowIndex >= activeRanges[rangeIndex].End)
            {
                rangeIndex++;
                fallbackRowInChunk = 0;

                if (rangeIndex < activeRanges.Count)
                {
                    fallbackBitmapMask = EvaluateBitmapFilter(
                        _filterHint, _sourceIndex.BitmapIndexes,
                        activeRanges[rangeIndex].ChunkIndex,
                        (int)(activeRanges[rangeIndex].End - activeRanges[rangeIndex].Start));
                }
            }

            if (rangeIndex < activeRanges.Count
                && rowIndex >= activeRanges[rangeIndex].Start
                && rowIndex < activeRanges[rangeIndex].End)
            {
                if (fallbackBitmapMask is null || IsBitmapBitSet(fallbackBitmapMask, fallbackRowInChunk))
                {
                    yield return row;
                }

                fallbackRowInChunk++;
            }

            rowIndex++;
        }
    }

    /// <summary>
    /// Checks whether a chunk can be pruned based on sorted value indexes.
    /// Extracts equality predicates (column = literal) from the filter expression
    /// and checks the sorted index to see if the chunk contains any matching key.
    /// </summary>
    private static bool ShouldPruneWithColumnIndexes(
        Expression filterHint, SourceIndex sourceIndex, int chunkIndex)
    {
        // Walk the expression tree looking for equality comparisons of the form
        // column = literal. Each such predicate can rule out chunks that do not
        // contain the literal in their sorted index.
        return CheckExpressionForPruning(filterHint, sourceIndex, chunkIndex);
    }

    /// <summary>
    /// Checks whether a chunk can be pruned based on bitmap indexes.
    /// Walks the filter expression tree extracting equality predicates
    /// (<c>column = literal</c>) and IN predicates against bitmap-indexed columns.
    /// AND chains prune when any branch proves the chunk empty; OR chains
    /// require all branches to prove the chunk empty.
    /// </summary>
    private static bool ShouldPruneWithBitmapIndexes(
        Expression filterHint, BitmapIndexSet bitmapIndexes, int chunkIndex)
    {
        return CheckExpressionForBitmapPruning(filterHint, bitmapIndexes, chunkIndex);
    }

    private static bool CheckExpressionForBitmapPruning(
        Expression expression, BitmapIndexSet bitmapIndexes, int chunkIndex)
    {
        if (expression is BinaryExpression binary)
        {
            if (binary.Operator == BinaryOperator.And)
            {
                // AND: prune if either side proves the chunk empty.
                return CheckExpressionForBitmapPruning(binary.Left, bitmapIndexes, chunkIndex)
                    || CheckExpressionForBitmapPruning(binary.Right, bitmapIndexes, chunkIndex);
            }

            if (binary.Operator == BinaryOperator.Equal)
            {
                return CheckEqualityForBitmapPruning(binary.Left, binary.Right, bitmapIndexes, chunkIndex);
            }
        }

        if (expression is InExpression inExpression && !inExpression.Negated)
        {
            return CheckInForBitmapPruning(inExpression, bitmapIndexes, chunkIndex);
        }

        return false;
    }

    private static bool CheckEqualityForBitmapPruning(
        Expression left, Expression right, BitmapIndexSet bitmapIndexes, int chunkIndex)
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

        DataValue literalValue = ConvertLiteralToDataValue(rawLiteral);
        return !bitmapIndex.ChunkContainsValue(literalValue, chunkIndex);
    }

    private static bool CheckInForBitmapPruning(
        InExpression inExpression, BitmapIndexSet bitmapIndexes, int chunkIndex)
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

            DataValue value = ConvertLiteralToDataValue(literal.Value);

            if (bitmapIndex.ChunkContainsValue(value, chunkIndex))
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
        int chunkIndex, int rowCount)
    {
        if (filterHint is null || bitmapIndexes is null || bitmapIndexes.Count == 0)
        {
            return null;
        }

        return EvaluateBitmapExpression(filterHint, bitmapIndexes, chunkIndex, rowCount);
    }

    /// <summary>
    /// Recursively evaluates a filter expression against bitmap indexes, composing
    /// per-value bitmaps with AND/OR/NOT to produce a row-inclusion bitset.
    /// Returns <c>null</c> when the sub-expression has no bitmap-eligible predicates.
    /// </summary>
    private static byte[]? EvaluateBitmapExpression(
        Expression expression, BitmapIndexSet bitmapIndexes,
        int chunkIndex, int rowCount)
    {
        if (expression is BinaryExpression binary)
        {
            if (binary.Operator == BinaryOperator.And)
            {
                byte[]? leftBits = EvaluateBitmapExpression(binary.Left, bitmapIndexes, chunkIndex, rowCount);
                byte[]? rightBits = EvaluateBitmapExpression(binary.Right, bitmapIndexes, chunkIndex, rowCount);

                if (leftBits is not null && rightBits is not null)
                {
                    return BitmapComposer.And(leftBits, rightBits);
                }

                // Return whichever side produced a bitmap (AND with unknown = keep the known constraint).
                return leftBits ?? rightBits;
            }

            if (binary.Operator == BinaryOperator.Or)
            {
                byte[]? leftBits = EvaluateBitmapExpression(binary.Left, bitmapIndexes, chunkIndex, rowCount);
                byte[]? rightBits = EvaluateBitmapExpression(binary.Right, bitmapIndexes, chunkIndex, rowCount);

                if (leftBits is not null && rightBits is not null)
                {
                    return BitmapComposer.Or(leftBits, rightBits);
                }

                // OR with an unknown side: cannot constrain (either side might match any row).
                return null;
            }

            if (binary.Operator == BinaryOperator.Equal)
            {
                return EvaluateBitmapEquality(binary.Left, binary.Right, bitmapIndexes, chunkIndex, rowCount);
            }

            if (binary.Operator == BinaryOperator.NotEqual)
            {
                byte[]? equalBits = EvaluateBitmapEquality(
                    binary.Left, binary.Right, bitmapIndexes, chunkIndex, rowCount);

                if (equalBits is not null)
                {
                    return BitmapComposer.Not(equalBits, rowCount);
                }

                return null;
            }
        }

        if (expression is InExpression inExpression && !inExpression.Negated)
        {
            return EvaluateBitmapIn(inExpression, bitmapIndexes, chunkIndex, rowCount);
        }

        return null;
    }

    private static byte[]? EvaluateBitmapEquality(
        Expression left, Expression right, BitmapIndexSet bitmapIndexes,
        int chunkIndex, int rowCount)
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

        DataValue literalValue = ConvertLiteralToDataValue(rawLiteral);
        ChunkBitmap bitmap = bitmapIndex.GetChunkBitmap(literalValue, chunkIndex);
        return bitmap.Bits.ToArray();
    }

    private static byte[]? EvaluateBitmapIn(
        InExpression inExpression, BitmapIndexSet bitmapIndexes,
        int chunkIndex, int rowCount)
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

            DataValue value = ConvertLiteralToDataValue(literal.Value);
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
        Expression expression, SourceIndex sourceIndex, int chunkIndex)
    {
        if (expression is BinaryExpression binary)
        {
            if (binary.Operator == BinaryOperator.And)
            {
                // AND: prune if either side says we can prune.
                return CheckExpressionForPruning(binary.Left, sourceIndex, chunkIndex)
                    || CheckExpressionForPruning(binary.Right, sourceIndex, chunkIndex);
            }

            if (binary.Operator == BinaryOperator.Equal)
            {
                return CheckEqualityForPruning(binary.Left, binary.Right, sourceIndex, chunkIndex);
            }

            if (binary.Operator is BinaryOperator.LessThan or BinaryOperator.LessThanOrEqual
                or BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual)
            {
                return CheckComparisonForPruning(
                    binary.Left, binary.Right, binary.Operator, sourceIndex, chunkIndex);
            }
        }

        if (expression is BetweenExpression between && !between.Negated)
        {
            return CheckBetweenForPruning(between, sourceIndex, chunkIndex);
        }

        if (expression is InExpression inExpression && !inExpression.Negated)
        {
            return CheckInForPruning(inExpression, sourceIndex, chunkIndex);
        }

        return false;
    }

    private static bool CheckEqualityForPruning(
        Expression left, Expression right, SourceIndex sourceIndex, int chunkIndex)
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

        if (!sourceIndex.TryGetColumnIndex(columnName, out IColumnIndex? index))
        {
            return false;
        }

        DataValue literalValue = ConvertLiteralToDataValue(rawLiteral);
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
        SourceIndex sourceIndex, int chunkIndex)
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

        if (!sourceIndex.TryGetColumnIndex(columnName, out IColumnIndex? index))
        {
            return false;
        }

        DataValue literalValue = ConvertLiteralToDataValue(rawLiteral);

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
        BetweenExpression between, SourceIndex sourceIndex, int chunkIndex)
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

        if (!sourceIndex.TryGetColumnIndex(columnRef.ColumnName, out IColumnIndex? index))
        {
            return false;
        }

        DataValue low = ConvertLiteralToDataValue(lowLiteral.Value);
        DataValue high = ConvertLiteralToDataValue(highLiteral.Value);
        IReadOnlySet<int> matchingChunks = index.FindChunksInRange(low, high);
        return !matchingChunks.Contains(chunkIndex);
    }

    /// <summary>
    /// Checks whether a chunk can be pruned based on an IN predicate
    /// by looking up each value in a sorted index.
    /// </summary>
    private static bool CheckInForPruning(
        InExpression inExpression, SourceIndex sourceIndex, int chunkIndex)
    {
        if (inExpression.Expression is not ColumnReference columnRef)
        {
            return false;
        }

        if (!sourceIndex.TryGetColumnIndex(columnRef.ColumnName, out IColumnIndex? index))
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

            DataValue value = ConvertLiteralToDataValue(literal.Value);
            IReadOnlySet<int> matchingChunks = index.FindChunksContaining(value);

            if (matchingChunks.Contains(chunkIndex))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Converts an AST literal value (<see cref="LiteralExpression.Value"/>) to a <see cref="DataValue"/>.
    /// </summary>
    private static DataValue ConvertLiteralToDataValue(object rawLiteral)
    {
        return rawLiteral switch
        {
            int intValue => DataValue.FromFloat32(intValue),
            long longValue => DataValue.FromFloat32(longValue),
            float floatValue => DataValue.FromFloat32(floatValue),
            double doubleValue => DataValue.FromFloat32((float)doubleValue),
            string stringValue => DataValue.FromString(stringValue),
            bool boolValue => DataValue.FromFloat32(boolValue ? 1f : 0f),
            _ => DataValue.Null(DataKind.Float32),
        };
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
        SourceIndex sourceIndex,
        IReadOnlyList<IndexChunk> chunks,
        HashSet<int> activeChunkIndexes)
    {
        List<long>? bestPositions = null;

        // Equality predicates: col = literal → FindExact
        List<(string Column, DataValue Value)> equalities = new();
        ExtractTopLevelEqualities(filterHint, equalities);

        foreach ((string column, DataValue value) in equalities)
        {
            if (!sourceIndex.TryGetColumnIndex(column, out IColumnIndex? index))
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
        ExtractTopLevelBetweens(filterHint, betweens);

        foreach ((string column, DataValue low, DataValue high) in betweens)
        {
            if (!sourceIndex.TryGetColumnIndex(column, out IColumnIndex? index))
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
        ExtractTopLevelIns(filterHint, inPredicates);

        foreach ((string column, List<DataValue> values) in inPredicates)
        {
            if (!sourceIndex.TryGetColumnIndex(column, out IColumnIndex? index))
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
        List<(string Column, DataValue Value)> results)
    {
        if (expression is BinaryExpression binary)
        {
            if (binary.Operator == BinaryOperator.And)
            {
                ExtractTopLevelEqualities(binary.Left, results);
                ExtractTopLevelEqualities(binary.Right, results);
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
                    results.Add((columnName, ConvertLiteralToDataValue(rawLiteral)));
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
        List<(string Column, DataValue Low, DataValue High)> results)
    {
        if (expression is BinaryExpression binary && binary.Operator == BinaryOperator.And)
        {
            ExtractTopLevelBetweens(binary.Left, results);
            ExtractTopLevelBetweens(binary.Right, results);
            return;
        }

        if (expression is BetweenExpression between && !between.Negated
            && between.Expression is ColumnReference columnRef
            && between.Low is LiteralExpression { Value: not null } lowLiteral
            && between.High is LiteralExpression { Value: not null } highLiteral)
        {
            results.Add((
                columnRef.ColumnName,
                ConvertLiteralToDataValue(lowLiteral.Value),
                ConvertLiteralToDataValue(highLiteral.Value)));
        }
    }

    /// <summary>
    /// Extracts <c>column IN (v1, v2, ...)</c> predicates from the top-level
    /// AND chain. Only non-negated IN with all-literal values is extracted.
    /// </summary>
    private static void ExtractTopLevelIns(
        Expression expression,
        List<(string Column, List<DataValue> Values)> results)
    {
        if (expression is BinaryExpression binary && binary.Operator == BinaryOperator.And)
        {
            ExtractTopLevelIns(binary.Left, results);
            ExtractTopLevelIns(binary.Right, results);
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

                values.Add(ConvertLiteralToDataValue(literal.Value));
            }

            results.Add((columnRef.ColumnName, values));
        }
    }

    /// <summary>
    /// Fans out each partition enumerable to its own <see cref="Task"/> and merges
    /// all rows through a bounded <see cref="Channel{T}"/>. The channel is completed
    /// when every partition finishes (or faults). On fault, the first exception is
    /// surfaced to the consumer via the channel's completion state.
    /// </summary>
    private static async IAsyncEnumerable<Row> MergePartitionsAsync(
        IReadOnlyList<IAsyncEnumerable<Row>> partitions,
        ExecutionContext context)
    {
        int capacity = partitions.Count * 64;
        Channel<Row> output = Channel.CreateBounded<Row>(
            new BoundedChannelOptions(capacity)
            {
                SingleWriter = false,
                SingleReader = true,
            });

        int remaining = partitions.Count;

        foreach (IAsyncEnumerable<Row> partition in partitions)
        {
            IAsyncEnumerable<Row> captured = partition;

            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (Row row in captured
                        .WithCancellation(context.CancellationToken)
                        .ConfigureAwait(false))
                    {
                        await output.Writer.WriteAsync(row, context.CancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    output.Writer.TryComplete(exception);
                    return;
                }
                finally
                {
                    if (Interlocked.Decrement(ref remaining) == 0)
                    {
                        output.Writer.TryComplete();
                    }
                }
            }, context.CancellationToken);
        }

        await foreach (Row row in output.Reader
            .ReadAllAsync(context.CancellationToken)
            .ConfigureAwait(false))
        {
            yield return row;
        }
    }
}
