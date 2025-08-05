using System.Collections;
using DatumIngest.Catalog;
using DatumIngest.Indexing;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Join operator supporting INNER, LEFT, RIGHT, FULL OUTER, CROSS,
/// LEFT SEMI, and LEFT ANTI-SEMI joins.
/// Uses expression-based hash join for any ON condition containing equality
/// conjuncts (including function calls and compound keys), with an optional
/// residual filter for non-equi parts. Falls back to nested-loop only when
/// no equalities can be extracted.
/// </summary>
public sealed class JoinOperator : IQueryOperator
{
    private readonly IQueryOperator _left;
    private readonly IQueryOperator _right;
    private readonly JoinType _joinType;
    private readonly Expression? _onCondition;
    private readonly bool _nullSensitiveAntiSemi;

    /// <summary>
    /// Creates a join operator.
    /// </summary>
    /// <param name="left">The left (probe) side operator.</param>
    /// <param name="right">The right (build) side operator.</param>
    /// <param name="joinType">The type of join.</param>
    /// <param name="onCondition">The ON condition expression (null for CROSS join).</param>
    /// <param name="nullSensitiveAntiSemi">
    /// When true and <paramref name="joinType"/> is <see cref="JoinType.LeftAntiSemi"/>,
    /// applies SQL-standard NOT IN null semantics: if any right-side key is NULL the
    /// entire result is empty, and left rows with a NULL key are excluded.
    /// </param>
    public JoinOperator(
        IQueryOperator left,
        IQueryOperator right,
        JoinType joinType,
        Expression? onCondition,
        bool nullSensitiveAntiSemi = false)
    {
        _left = left;
        _right = right;
        _joinType = joinType;
        _onCondition = onCondition;
        _nullSensitiveAntiSemi = nullSensitiveAntiSemi;
    }

    /// <summary>The left (probe) side operator.</summary>
    public IQueryOperator Left => _left;

    /// <summary>The right (build) side operator.</summary>
    public IQueryOperator Right => _right;

    /// <summary>The type of join.</summary>
    public JoinType Type => _joinType;

    /// <summary>The ON condition expression.</summary>
    public Expression? OnCondition => _onCondition;

    /// <inheritdoc/>
    public async IAsyncEnumerable<Row> ExecuteAsync(ExecutionContext context)
    {
        if (_joinType == JoinType.Cross)
        {
            await foreach (Row row in ExecuteCrossJoinAsync(context).ConfigureAwait(false))
            {
                yield return row;
            }
            yield break;
        }

        // Extract equi-join keys from the ON condition. Supports arbitrary
        // expressions (function calls, CAST, etc.) and compound AND keys.
        // Non-equality conjuncts become a residual filter applied after hash match.
        JoinKeyExtractionResult? extraction = JoinKeyExtractor.TryExtract(_onCondition);

        if (extraction is not null)
        {
            // Try index nested loop join when a sorted index exists on the build-side
            // join column. This is optimal under LIMIT with few probe rows.
            IndexNestedLoopJoinExecutor? indexNlj = TryCreateIndexNestedLoopExecutor(extraction, context);

            if (indexNlj is not null)
            {
                await foreach (Row row in indexNlj.ExecuteAsync(_left, _right, context).ConfigureAwait(false))
                {
                    yield return row;
                }
            }
            else if (context.MemoryBudgetBytes is long memoryBudget)
            {
                ExpressionEvaluator evaluator = new(context.FunctionRegistry, context.QueryMeter, context.OuterRow);
                GraceHashJoinExecutor graceExecutor = new(_joinType, extraction, memoryBudget, evaluator, _nullSensitiveAntiSemi, label: GetOperatorLabel(_right));

                await foreach (Row row in graceExecutor.ExecuteAsync(_left, _right, context).ConfigureAwait(false))
                {
                    yield return row;
                }
            }
            else
            {
                await foreach (Row row in ExecuteHashJoinAsync(context, extraction).ConfigureAwait(false))
                {
                    yield return row;
                }
            }
        }
        else
        {
            await foreach (Row row in ExecuteNestedLoopJoinAsync(context).ConfigureAwait(false))
            {
                yield return row;
            }
        }
    }

    /// <summary>
    /// Walks the operator tree to find the underlying <see cref="ScanOperator"/> and returns
    /// its table name, used as a human-readable label in execution trace output.
    /// Returns the operator's type name when no scan is found (e.g. a derived join result).
    /// </summary>
    private static string GetOperatorLabel(IQueryOperator op)
    {
        IQueryOperator current = op;
        while (true)
        {
            if (current is ScanOperator scan)
                return scan.Descriptor.Name;
            if (current is AliasOperator alias)
                current = alias.Source;
            else if (current is FilterOperator filter)
                current = filter.Source;
            else
                return current.GetType().Name;
        }
    }

    /// <summary>
    /// Attempts to create an <see cref="IndexNestedLoopJoinExecutor"/> for the current join.
    /// Returns <c>null</c> when the preconditions are not met (join type, single key,
    /// sorted index available, seekable provider, bounded row limit).
    /// </summary>
    private IndexNestedLoopJoinExecutor? TryCreateIndexNestedLoopExecutor(
        JoinKeyExtractionResult extraction, ExecutionContext context)
    {
        // Only use index NLJ when a LIMIT is active and small enough that
        // point-seeks are cheaper than building a full hash table.
        const int IndexNestedLoopRowLimitThreshold = 1000;

        if (context.RowLimit is not int rowLimit || rowLimit > IndexNestedLoopRowLimitThreshold)
        {
            return null;
        }

        // Index NLJ only supports INNER and LeftSemi.
        if (_joinType is not (JoinType.Inner or JoinType.LeftSemi))
        {
            return null;
        }

        // Only single-key equi-joins for now.
        if (extraction.KeyPairs.Count != 1)
        {
            return null;
        }

        // Build-side key must be a simple column reference to match against sorted index names.
        Expression buildKeyExpression = extraction.KeyPairs[0].Right;

        if (buildKeyExpression is not ColumnReference buildColumnRef)
        {
            return null;
        }

        // Find the build-side ScanOperator.
        List<ScanOperator> buildScans = new();
        CollectScanOperators(_right, buildScans);

        if (buildScans.Count != 1)
        {
            return null;
        }

        ScanOperator buildScan = buildScans[0];

        if (buildScan.SourceIndex?.SortedIndexes is null)
        {
            return null;
        }

        // Try both qualified and unqualified column names against the sorted index.
        string? indexColumnName = buildColumnRef.QualifiedName ?? buildColumnRef.ColumnName;
        SortedValueIndexSet sortedIndexes = buildScan.SourceIndex.SortedIndexes;

        if (!sortedIndexes.TryGetIndex(indexColumnName, out SortedValueIndex? sortedIndex))
        {
            // Try unqualified name if qualified failed.
            if (buildColumnRef.QualifiedName is not null
                && !sortedIndexes.TryGetIndex(buildColumnRef.ColumnName, out sortedIndex))
            {
                return null;
            }

            if (sortedIndex is null)
            {
                return null;
            }
        }

        // Verify the provider supports seeking.
        ITableProvider provider = context.Catalog.CreateProvider(buildScan.Descriptor);

        if (provider is not ISeekableTableProvider)
        {
            return null;
        }

        ExpressionEvaluator evaluator = new(context.FunctionRegistry, context.QueryMeter, context.OuterRow);

        return new IndexNestedLoopJoinExecutor(
            _joinType,
            extraction,
            sortedIndex,
            buildScan.SourceIndex.Chunks,
            buildScan.Descriptor,
            evaluator);
    }

    private async IAsyncEnumerable<Row> ExecuteHashJoinAsync(
        ExecutionContext context, JoinKeyExtractionResult extraction)
    {
        ExpressionEvaluator evaluator = new(context.FunctionRegistry, context.QueryMeter, context.OuterRow);
        IReadOnlyList<(Expression Left, Expression Right)> keyPairs = extraction.KeyPairs;
        bool useSingleKey = keyPairs.Count == 1;
        bool isSemiJoin = _joinType == JoinType.LeftSemi || _joinType == JoinType.LeftAntiSemi;

        // Build phase: materialize right side into hash table.
        // For single-key joins, use DataValue directly as the key to avoid
        // the overhead of CompositeKey allocation.
        Dictionary<DataValue, List<(int Index, Row Row)>>? singleKeyTable =
            useSingleKey ? new() : null;
        Dictionary<CompositeKey, List<(int Index, Row Row)>>? compositeKeyTable =
            useSingleKey ? null : new();

        List<Row> rightRows = new();
        bool hasNullKey = false;

        await foreach (Row rightRow in _right.ExecuteAsync(context).ConfigureAwait(false))
        {
            int rightIndex = rightRows.Count;
            rightRows.Add(rightRow);

            if (useSingleKey)
            {
                DataValue keyValue = evaluator.Evaluate(keyPairs[0].Right, rightRow);
                if (keyValue.IsNull)
                {
                    hasNullKey = true;
                    continue;
                }

                if (!singleKeyTable!.TryGetValue(keyValue, out List<(int, Row)>? bucket))
                {
                    bucket = new List<(int, Row)>();
                    singleKeyTable[keyValue] = bucket;
                }

                bucket.Add((rightIndex, rightRow));
            }
            else
            {
                DataValue[] parts = EvaluateKeyParts(evaluator, keyPairs, rightRow, rightSide: true);
                if (HasNull(parts))
                {
                    hasNullKey = true;
                    continue;
                }

                CompositeKey compositeKey = new(parts);
                if (!compositeKeyTable!.TryGetValue(compositeKey, out List<(int, Row)>? bucket))
                {
                    bucket = new List<(int, Row)>();
                    compositeKeyTable[compositeKey] = bucket;
                }

                bucket.Add((rightIndex, rightRow));
            }
        }

        // NOT IN null semantics: if any right key is NULL, the entire result is empty.
        if (_nullSensitiveAntiSemi && hasNullKey)
        {
            yield break;
        }

        // Bloom pruning: if the probe (left) side has a source index with
        // bloom filters and the join key is a simple column reference, push
        // the build-side key values down so entire chunks can be skipped.
        if (!isSemiJoin)
        {
            ApplyBloomPruning(keyPairs, singleKeyTable, compositeKeyTable, useSingleKey);
        }

        // Track which right rows have been matched (for RIGHT/FULL OUTER).
        bool needRightUnmatched = _joinType == JoinType.Right || _joinType == JoinType.FullOuter;
        BitArray? rightMatched = needRightUnmatched ? new BitArray(rightRows.Count) : null;

        // Probe phase: stream left side.
        // Schema is built lazily from the first left-right pair so that zero-match
        // joins never attempt to build it.
        CombinedRowSchema? schema = null;
        Row? cachedNullRight = null;

        await foreach (Row leftRow in _left.ExecuteAsync(context).ConfigureAwait(false))
        {
            // For null-sensitive anti-semi (NOT IN), NULL left keys are excluded.
            if (_nullSensitiveAntiSemi)
            {
                bool leftKeyIsNull;
                if (useSingleKey)
                {
                    leftKeyIsNull = evaluator.Evaluate(keyPairs[0].Left, leftRow).IsNull;
                }
                else
                {
                    DataValue[] parts = EvaluateKeyParts(evaluator, keyPairs, leftRow, rightSide: false);
                    leftKeyIsNull = HasNull(parts);
                }

                if (leftKeyIsNull)
                {
                    continue;
                }
            }

            bool hasMatch = false;
            List<(int Index, Row Row)>? matches = null;

            if (useSingleKey)
            {
                DataValue leftKeyValue = evaluator.Evaluate(keyPairs[0].Left, leftRow);
                if (!leftKeyValue.IsNull)
                {
                    singleKeyTable!.TryGetValue(leftKeyValue, out matches);
                }
            }
            else
            {
                DataValue[] parts = EvaluateKeyParts(evaluator, keyPairs, leftRow, rightSide: false);
                if (!HasNull(parts))
                {
                    CompositeKey compositeKey = new(parts);
                    compositeKeyTable!.TryGetValue(compositeKey, out matches);
                }
            }

            if (matches is not null)
            {
                foreach ((int rightIndex, Row rightRow) in matches)
                {
                    // Apply residual filter for non-equi conjuncts.
                    if (extraction.Residual is not null)
                    {
                        schema ??= CombinedRowSchema.Build(leftRow, rightRow);
                        Row combinedRow = schema.Combine(leftRow, rightRow);
                        if (!evaluator.EvaluateAsBoolean(extraction.Residual, combinedRow))
                        {
                            continue;
                        }
                    }

                    hasMatch = true;

                    if (isSemiJoin)
                    {
                        // Semi-join: emit left row on first match, skip the rest.
                        break;
                    }

                    if (rightMatched is not null)
                    {
                        rightMatched[rightIndex] = true;
                    }

                    if (extraction.Residual is null)
                    {
                        schema ??= CombinedRowSchema.Build(leftRow, rightRow);
                    }

                    yield return schema!.Combine(leftRow, rightRow);
                }
            }

            if (isSemiJoin)
            {
                // LeftSemi: emit only when matched. LeftAntiSemi: emit only when not matched.
                if ((_joinType == JoinType.LeftSemi && hasMatch) ||
                    (_joinType == JoinType.LeftAntiSemi && !hasMatch))
                {
                    yield return leftRow;
                }
            }
            else if (!hasMatch && (_joinType == JoinType.Left || _joinType == JoinType.FullOuter))
            {
                if (rightRows.Count > 0)
                {
                    cachedNullRight ??= CreateNullRow(rightRows[0]);
                    schema ??= CombinedRowSchema.Build(leftRow, cachedNullRight);
                    yield return schema.Combine(leftRow, cachedNullRight);
                }
                else
                {
                    yield return leftRow;
                }
            }
        }

        // Emit unmatched right rows for RIGHT and FULL OUTER.
        if (rightMatched is not null)
        {
            Row? nullLeft = null;
            CombinedRowSchema? rightUnmatchedSchema = null;

            for (int index = 0; index < rightRows.Count; index++)
            {
                if (!rightMatched[index])
                {
                    nullLeft ??= await GetFirstLeftRowForNullPadAsync(context).ConfigureAwait(false);

                    if (nullLeft is not null)
                    {
                        Row nullLeftRow = CreateNullRow(nullLeft);
                        rightUnmatchedSchema ??= CombinedRowSchema.Build(nullLeftRow, rightRows[index]);
                        yield return rightUnmatchedSchema.Combine(nullLeftRow, rightRows[index]);
                    }
                    else
                    {
                        yield return rightRows[index];
                    }
                }
            }
        }
    }

    private async IAsyncEnumerable<Row> ExecuteNestedLoopJoinAsync(ExecutionContext context)
    {
        ExpressionEvaluator evaluator = new(context.FunctionRegistry, context.QueryMeter, context.OuterRow);
        bool isSemiJoin = _joinType == JoinType.LeftSemi || _joinType == JoinType.LeftAntiSemi;

        // Materialize right side.
        List<Row> rightRows = new();
        await foreach (Row rightRow in _right.ExecuteAsync(context).ConfigureAwait(false))
        {
            rightRows.Add(rightRow);
        }

        BitArray? rightMatched = (_joinType == JoinType.FullOuter || _joinType == JoinType.Right)
            ? new BitArray(rightRows.Count)
            : null;

        CombinedRowSchema? schema = null;
        Row? cachedNullRight = null;

        await foreach (Row leftRow in _left.ExecuteAsync(context).ConfigureAwait(false))
        {
            bool hasMatch = false;

            for (int index = 0; index < rightRows.Count; index++)
            {
                schema ??= CombinedRowSchema.Build(leftRow, rightRows[index]);
                Row combinedRow = schema.Combine(leftRow, rightRows[index]);

                if (_onCondition is null || evaluator.EvaluateAsBoolean(_onCondition, combinedRow))
                {
                    hasMatch = true;

                    if (isSemiJoin)
                    {
                        break;
                    }

                    if (rightMatched is not null)
                    {
                        rightMatched[index] = true;
                    }

                    yield return combinedRow;
                }
            }

            if (isSemiJoin)
            {
                if ((_joinType == JoinType.LeftSemi && hasMatch) ||
                    (_joinType == JoinType.LeftAntiSemi && !hasMatch))
                {
                    yield return leftRow;
                }
            }
            else if (!hasMatch && (_joinType == JoinType.Left || _joinType == JoinType.FullOuter))
            {
                if (rightRows.Count > 0)
                {
                    cachedNullRight ??= CreateNullRow(rightRows[0]);
                    schema ??= CombinedRowSchema.Build(leftRow, cachedNullRight);
                    yield return schema.Combine(leftRow, cachedNullRight);
                }
                else
                {
                    yield return leftRow;
                }
            }
        }

        // Emit unmatched right rows for RIGHT and FULL OUTER.
        if (rightMatched is not null)
        {
            Row? nullLeft = null;
            CombinedRowSchema? rightUnmatchedSchema = null;

            for (int index = 0; index < rightRows.Count; index++)
            {
                if (!rightMatched[index])
                {
                    nullLeft ??= await GetFirstLeftRowForNullPadAsync(context).ConfigureAwait(false);

                    if (nullLeft is not null)
                    {
                        Row nullLeftRow = CreateNullRow(nullLeft);
                        rightUnmatchedSchema ??= CombinedRowSchema.Build(nullLeftRow, rightRows[index]);
                        yield return rightUnmatchedSchema.Combine(nullLeftRow, rightRows[index]);
                    }
                    else
                    {
                        yield return rightRows[index];
                    }
                }
            }
        }
    }

    private async IAsyncEnumerable<Row> ExecuteCrossJoinAsync(ExecutionContext context)
    {
        // Materialize right side.
        List<Row> rightRows = new();
        await foreach (Row rightRow in _right.ExecuteAsync(context).ConfigureAwait(false))
        {
            rightRows.Add(rightRow);
        }

        CombinedRowSchema? schema = null;

        await foreach (Row leftRow in _left.ExecuteAsync(context).ConfigureAwait(false))
        {
            foreach (Row rightRow in rightRows)
            {
                schema ??= CombinedRowSchema.Build(leftRow, rightRow);
                yield return schema.Combine(leftRow, rightRow);
            }
        }
    }

    private async Task<Row?> GetFirstLeftRowForNullPadAsync(ExecutionContext context)
    {
        await foreach (Row row in _left.ExecuteAsync(context).ConfigureAwait(false))
        {
            return row;
        }

        return null;
    }

    /// <summary>
    /// Evaluates the key expressions for a single row, selecting either the left
    /// or right expression from each key pair.
    /// </summary>
    private static DataValue[] EvaluateKeyParts(
        ExpressionEvaluator evaluator,
        IReadOnlyList<(Expression Left, Expression Right)> keyPairs,
        Row row,
        bool rightSide)
    {
        DataValue[] parts = new DataValue[keyPairs.Count];
        for (int index = 0; index < keyPairs.Count; index++)
        {
            Expression expression = rightSide ? keyPairs[index].Right : keyPairs[index].Left;
            parts[index] = evaluator.Evaluate(expression, row);
        }

        return parts;
    }

    /// <summary>
    /// Returns true if any element in the array is null. NULL keys never match in SQL semantics.
    /// </summary>
    private static bool HasNull(DataValue[] parts)
    {
        for (int index = 0; index < parts.Length; index++)
        {
            if (parts[index].IsNull)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Combines two rows into one, using all columns from both sides.
    /// </summary>
    internal static Row CombineRows(Row left, Row right)
    {
        string[] names = new string[left.FieldCount + right.FieldCount];
        DataValue[] values = new DataValue[left.FieldCount + right.FieldCount];

        for (int index = 0; index < left.FieldCount; index++)
        {
            names[index] = left.ColumnNames[index];
            values[index] = left[index];
        }

        for (int index = 0; index < right.FieldCount; index++)
        {
            names[left.FieldCount + index] = right.ColumnNames[index];
            values[left.FieldCount + index] = right[index];
        }

        return new Row(names, values);
    }

    /// <summary>
    /// Creates a row with the same column names as the source but all null values.
    /// </summary>
    internal static Row CreateNullRow(Row template)
    {
        string[] names = new string[template.FieldCount];
        DataValue[] values = new DataValue[template.FieldCount];

        for (int index = 0; index < template.FieldCount; index++)
        {
            names[index] = template.ColumnNames[index];
            values[index] = DataValue.Null(DataKind.Scalar);
        }

        return new Row(names, values);
    }

    /// <summary>
    /// Pre-computed schema for combined rows in a join. Holds the shared column name
    /// array and name-index dictionary so that each combined row allocates only a
    /// <see cref="DataValue"/> array instead of rebuilding the full schema.
    /// </summary>
    internal sealed class CombinedRowSchema
    {
        private readonly string[] _names;
        private readonly Dictionary<string, int> _nameIndex;
        private readonly int _leftFieldCount;

        private CombinedRowSchema(string[] names, Dictionary<string, int> nameIndex, int leftFieldCount)
        {
            _names = names;
            _nameIndex = nameIndex;
            _leftFieldCount = leftFieldCount;
        }

        /// <summary>
        /// Builds a schema from the first left and right rows encountered in a join.
        /// </summary>
        internal static CombinedRowSchema Build(Row left, Row right)
        {
            int totalFields = left.FieldCount + right.FieldCount;
            string[] names = new string[totalFields];

            for (int index = 0; index < left.FieldCount; index++)
            {
                names[index] = left.ColumnNames[index];
            }

            for (int index = 0; index < right.FieldCount; index++)
            {
                names[left.FieldCount + index] = right.ColumnNames[index];
            }

            Dictionary<string, int> nameIndex = new(totalFields, StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < totalFields; index++)
            {
                nameIndex[names[index]] = index;
            }

            // Add unqualified shortcuts for aliased columns so that expressions
            // like image_to_tensor_chw(image) can resolve unqualified names after
            // a JOIN.  Skip ambiguous names that appear on both sides.
            HashSet<string> ambiguous = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> unqualified = new(StringComparer.OrdinalIgnoreCase);

            for (int index = 0; index < totalFields; index++)
            {
                int dotPosition = names[index].LastIndexOf('.');
                if (dotPosition < 0)
                {
                    continue;
                }

                string shortName = names[index][(dotPosition + 1)..];
                if (ambiguous.Contains(shortName))
                {
                    continue;
                }

                if (!unqualified.TryAdd(shortName, index))
                {
                    // Same unqualified name on both sides — remove and mark ambiguous.
                    unqualified.Remove(shortName);
                    ambiguous.Add(shortName);
                }
            }

            foreach (KeyValuePair<string, int> entry in unqualified)
            {
                nameIndex.TryAdd(entry.Key, entry.Value);
            }

            return new CombinedRowSchema(names, nameIndex, left.FieldCount);
        }

        /// <summary>
        /// Combines two rows using the shared schema. Only a <see cref="DataValue"/> array
        /// is allocated per call.
        /// </summary>
        internal Row Combine(Row left, Row right)
        {
            DataValue[] values = new DataValue[_names.Length];

            for (int index = 0; index < _leftFieldCount; index++)
            {
                values[index] = left[index];
            }

            for (int index = 0; index < _names.Length - _leftFieldCount; index++)
            {
                values[_leftFieldCount + index] = right[index];
            }

            return new Row(_names, values, _nameIndex);
        }
    }

    /// <summary>
    /// Pushes build-side key values to all reachable probe-side <see cref="ScanOperator"/>
    /// instances for bloom-filter-based chunk pruning. Traverses through intermediate
    /// joins, aliases, filters, and projections so that multi-table join trees can
    /// propagate bloom keys to buried scans.
    /// </summary>
    private void ApplyBloomPruning(
        IReadOnlyList<(Expression Left, Expression Right)> keyPairs,
        Dictionary<DataValue, List<(int Index, Row Row)>>? singleKeyTable,
        Dictionary<CompositeKey, List<(int Index, Row Row)>>? compositeKeyTable,
        bool useSingleKey)
    {
        List<ScanOperator> probeScans = new();
        CollectScanOperators(_left, probeScans);

        if (probeScans.Count == 0)
        {
            return;
        }

        for (int keyIndex = 0; keyIndex < keyPairs.Count; keyIndex++)
        {
            if (keyPairs[keyIndex].Left is not ColumnReference columnReference)
            {
                continue;
            }

            string columnName = columnReference.ColumnName;
            HashSet<DataValue>? distinctKeys = null;

            foreach (ScanOperator probeScan in probeScans)
            {
                if (probeScan.SourceIndex?.BloomFilters is not BloomFilterSet bloomFilters)
                {
                    continue;
                }

                if (!bloomFilters.HasColumn(columnName))
                {
                    continue;
                }

                // Lazily collect distinct keys on first matching scan.
                if (distinctKeys is null)
                {
                    distinctKeys = new HashSet<DataValue>();

                    if (useSingleKey && singleKeyTable is not null)
                    {
                        foreach (DataValue key in singleKeyTable.Keys)
                        {
                            distinctKeys.Add(key);
                        }
                    }
                    else if (compositeKeyTable is not null)
                    {
                        foreach (CompositeKey compositeKey in compositeKeyTable.Keys)
                        {
                            DataValue partValue = compositeKey[keyIndex];
                            if (!partValue.IsNull)
                            {
                                distinctKeys.Add(partValue);
                            }
                        }
                    }

                    if (distinctKeys.Count == 0)
                    {
                        break;
                    }
                }

                probeScan.AddBloomPruningKeys(columnName, distinctKeys);
            }
        }
    }

    /// <summary>
    /// Recursively collects all reachable <see cref="ScanOperator"/> instances
    /// in the operator tree, traversing through <see cref="AliasOperator"/>,
    /// <see cref="FilterOperator"/>, <see cref="ProjectOperator"/>, and
    /// <see cref="JoinOperator"/> (both sides). Stops at operators that
    /// break column identity (e.g. aggregation, DISTINCT).
    /// </summary>
    internal static void CollectScanOperators(IQueryOperator operatorNode, List<ScanOperator> results)
    {
        IQueryOperator current = operatorNode;
        while (true)
        {
            switch (current)
            {
                case ScanOperator scan:
                    results.Add(scan);
                    return;
                case AliasOperator alias:
                    current = alias.Source;
                    break;
                case FilterOperator filter:
                    current = filter.Source;
                    break;
                case ProjectOperator project:
                    current = project.Source;
                    break;
                case JoinOperator join:
                    CollectScanOperators(join._left, results);
                    CollectScanOperators(join._right, results);
                    return;
                default:
                    return;
            }
        }
    }
}
