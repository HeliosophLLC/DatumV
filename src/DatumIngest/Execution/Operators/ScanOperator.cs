using System.Runtime.CompilerServices;
using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Indexing;
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
/// </summary>
public sealed class ScanOperator : IQueryOperator
{
    private readonly TableDescriptor _descriptor;
    private readonly IReadOnlySet<string>? _requiredColumns;
    private Expression? _filterHint;
    private SourceIndex? _sourceIndex;
    private Dictionary<string, IReadOnlyCollection<DataValue>>? _bloomPruningKeys;

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
    /// The most recent <see cref="IFilterableTableProvider"/> used during execution,
    /// or <c>null</c> if the provider is not filterable. Used by the explain/instrumentation
    /// layer to retrieve pruning statistics after execution.
    /// </summary>
    public IFilterableTableProvider? LastFilterableProvider { get; private set; }

    /// <inheritdoc/>
    public async IAsyncEnumerable<Row> ExecuteAsync(ExecutionContext context)
    {
        CancellationToken cancellationToken = context.CancellationToken;
        ITableProvider provider = context.Catalog.CreateProvider(_descriptor);

        // When a source index is available and either a filter hint, bloom pruning
        // keys, or sorted indexes are present, apply chunk-level pruning.
        bool hasIndexPruning = _sourceIndex is not null
            && (_filterHint is not null || _bloomPruningKeys is not null
                || _sourceIndex.SortedIndexes is not null);

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
        SortedValueIndexSet? sortedIndexes = _sourceIndex.SortedIndexes;
        TotalIndexChunks = chunks.Count;
        PrunedIndexChunks = 0;
        ExactSeekRowsFetched = null;

        // Build a set of non-pruned chunk row ranges and track active chunk indexes.
        List<(long Start, long End)> activeRanges = new();
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

            // Sorted-index-based pruning: if equality predicates are present and
            // sorted indexes exist, check whether the chunk contains the key.
            if (!pruned && _filterHint is not null && sortedIndexes is not null)
            {
                pruned = ShouldPruneWithSortedIndexes(_filterHint, sortedIndexes, chunkIndex);
            }

            if (pruned)
            {
                PrunedIndexChunks++;
            }
            else
            {
                activeRanges.Add((chunk.RowOffset, chunk.RowOffset + chunk.RowCount));
                activeChunkIndexes.Add(chunkIndex);
            }
        }

        // When the provider supports seeking and equality predicates have sorted
        // index hits, seek directly to matching rows rather than reading entire chunks.
        if (provider is ISeekableTableProvider seekable
            && _filterHint is not null && sortedIndexes is not null)
        {
            List<long>? exactPositions = CollectExactSeekPositions(
                _filterHint, sortedIndexes, chunks, activeChunkIndexes);

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

        // If no chunks were pruned, stream all rows without overhead.
        if (PrunedIndexChunks == 0)
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
            foreach ((long start, long end) in activeRanges)
            {
                int count = (int)(end - start);

                await foreach (Row row in chunkSeekable.ReadRowRangeAsync(
                    _descriptor, _requiredColumns, start, count,
                    cancellationToken).ConfigureAwait(false))
                {
                    yield return row;
                }
            }

            yield break;
        }

        // Fallback: stream all rows and skip those in pruned chunks by row index.
        rows = OpenStream();
        long rowIndex = 0;
        int rangeIndex = 0;

        await foreach (Row row in rows.ConfigureAwait(false))
        {
            // Advance past ranges we've gone beyond.
            while (rangeIndex < activeRanges.Count && rowIndex >= activeRanges[rangeIndex].End)
            {
                rangeIndex++;
            }

            if (rangeIndex < activeRanges.Count
                && rowIndex >= activeRanges[rangeIndex].Start
                && rowIndex < activeRanges[rangeIndex].End)
            {
                yield return row;
            }

            rowIndex++;
        }
    }

    /// <summary>
    /// Checks whether a chunk can be pruned based on sorted value indexes.
    /// Extracts equality predicates (column = literal) from the filter expression
    /// and checks the sorted index to see if the chunk contains any matching key.
    /// </summary>
    private static bool ShouldPruneWithSortedIndexes(
        Expression filterHint, SortedValueIndexSet sortedIndexes, int chunkIndex)
    {
        // Walk the expression tree looking for equality comparisons of the form
        // column = literal. Each such predicate can rule out chunks that do not
        // contain the literal in their sorted index.
        return CheckExpressionForPruning(filterHint, sortedIndexes, chunkIndex);
    }

    private static bool CheckExpressionForPruning(
        Expression expression, SortedValueIndexSet sortedIndexes, int chunkIndex)
    {
        if (expression is BinaryExpression binary)
        {
            if (binary.Operator == BinaryOperator.And)
            {
                // AND: prune if either side says we can prune.
                return CheckExpressionForPruning(binary.Left, sortedIndexes, chunkIndex)
                    || CheckExpressionForPruning(binary.Right, sortedIndexes, chunkIndex);
            }

            if (binary.Operator == BinaryOperator.Equal)
            {
                return CheckEqualityForPruning(binary.Left, binary.Right, sortedIndexes, chunkIndex);
            }
        }

        return false;
    }

    private static bool CheckEqualityForPruning(
        Expression left, Expression right, SortedValueIndexSet sortedIndexes, int chunkIndex)
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

        if (!sortedIndexes.TryGetIndex(columnName, out SortedValueIndex? index))
        {
            return false;
        }

        DataValue literalValue = ConvertLiteralToDataValue(rawLiteral);
        IReadOnlySet<int> matchingChunks = index.FindChunksContaining(literalValue);
        return !matchingChunks.Contains(chunkIndex);
    }

    /// <summary>
    /// Converts an AST literal value (<see cref="LiteralExpression.Value"/>) to a <see cref="DataValue"/>.
    /// </summary>
    private static DataValue ConvertLiteralToDataValue(object rawLiteral)
    {
        return rawLiteral switch
        {
            int intValue => DataValue.FromScalar(intValue),
            long longValue => DataValue.FromScalar(longValue),
            float floatValue => DataValue.FromScalar(floatValue),
            double doubleValue => DataValue.FromScalar((float)doubleValue),
            string stringValue => DataValue.FromString(stringValue),
            bool boolValue => DataValue.FromScalar(boolValue ? 1f : 0f),
            _ => DataValue.Null(DataKind.Scalar),
        };
    }

    /// <summary>
    /// Attempts to collect exact row positions from equality predicates in the filter
    /// hint by looking up values in sorted indexes. Only extracts from top-level AND
    /// chains — OR predicates are not eligible for index seek. When multiple indexed
    /// equality predicates exist, uses the most selective (fewest matches).
    /// </summary>
    /// <returns>Sorted list of absolute row positions, or <c>null</c> if no seek is possible.</returns>
    private static List<long>? CollectExactSeekPositions(
        Expression filterHint,
        SortedValueIndexSet sortedIndexes,
        IReadOnlyList<IndexChunk> chunks,
        HashSet<int> activeChunkIndexes)
    {
        List<(string Column, DataValue Value)> equalities = new();
        ExtractTopLevelEqualities(filterHint, equalities);

        if (equalities.Count == 0)
        {
            return null;
        }

        // Find the most selective equality predicate with a sorted index.
        List<long>? bestPositions = null;

        foreach ((string column, DataValue value) in equalities)
        {
            if (!sortedIndexes.TryGetIndex(column, out SortedValueIndex? index))
            {
                continue;
            }

            IReadOnlyList<ValueIndexEntry> entries = index.FindExact(value);
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
}
