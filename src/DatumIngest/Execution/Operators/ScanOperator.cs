using System.Runtime.CompilerServices;
using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Indexing;
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
    public async IAsyncEnumerable<Row> ExecuteAsync(ExecutionContext context)
    {
        CancellationToken cancellationToken = context.CancellationToken;
        ITableProvider provider = context.Catalog.CreateProvider(_descriptor);

        // When a source index is available and either a filter hint, bloom pruning
        // keys, sorted index pruning keys, or sorted indexes are present, apply chunk-level pruning.
        bool hasIndexPruning = _sourceIndex is not null
            && (_filterHint is not null || _bloomPruningKeys is not null
                || _sortedIndexPruningKeys is not null
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
    private static bool ShouldPruneWithColumnIndexes(
        Expression filterHint, SourceIndex sourceIndex, int chunkIndex)
    {
        // Walk the expression tree looking for equality comparisons of the form
        // column = literal. Each such predicate can rule out chunks that do not
        // contain the literal in their sorted index.
        return CheckExpressionForPruning(filterHint, sourceIndex, chunkIndex);
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
}
