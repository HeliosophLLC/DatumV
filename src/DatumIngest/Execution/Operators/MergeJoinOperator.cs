using DatumIngest.Diagnostics;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;
using static DatumIngest.Execution.Operators.JoinOperator;
using static DatumIngest.Execution.StatisticsPredicateEvaluator;
using DatumIngest.Indexing.Sorted;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Sort-merge join operator that joins two index-ordered input streams using a
/// two-pointer algorithm. Each input is backed by an <see cref="IndexScanOperator"/>
/// traversing either a <see cref="Indexing.Sorted.SortedIndex"/> (memory-mapped) or a
/// <see cref="Indexing.BTree.BPlusTreeColumnIndex"/> (B+Tree leaf chain), both of
/// which yield entries in ascending key order. Requires O(k) memory where k is the
/// maximum number of right-side duplicates for a single key value — no hash table
/// is constructed.
/// <para>
/// Supports INNER, LEFT, RIGHT, and FULL OUTER joins. NULL keys never match
/// (SQL semantics) and are emitted with null-padded counterparts when the join
/// type requires all rows to appear.
/// </para>
/// </summary>
public sealed class MergeJoinOperator : IQueryOperator
{
    private readonly IQueryOperator _left;
    private readonly IQueryOperator _right;
    private readonly JoinType _joinType;
    private readonly JoinKeyExtractionResult _extraction;
    private readonly string _leftSortColumn;
    private readonly string _rightSortColumn;

    /// <summary>
    /// Creates a merge join operator.
    /// </summary>
    /// <param name="left">The left input operator, pre-sorted ascending by the left join key.</param>
    /// <param name="right">The right input operator, pre-sorted ascending by the right join key.</param>
    /// <param name="joinType">The type of join (INNER, LEFT, RIGHT, or FULL OUTER).</param>
    /// <param name="extraction">
    /// The extracted equi-join key pairs and optional residual filter. Only the first
    /// key pair is used for merge ordering; any residual is applied after key match.
    /// </param>
    /// <param name="leftSortColumn">The name of the sorted column on the left side (for plan descriptions).</param>
    /// <param name="rightSortColumn">The name of the sorted column on the right side (for plan descriptions).</param>
    public MergeJoinOperator(
        IQueryOperator left,
        IQueryOperator right,
        JoinType joinType,
        JoinKeyExtractionResult extraction,
        string leftSortColumn,
        string rightSortColumn)
    {
        _left = left;
        _right = right;
        _joinType = joinType;
        _extraction = extraction;
        _leftSortColumn = leftSortColumn;
        _rightSortColumn = rightSortColumn;
    }

    /// <summary>The left input operator.</summary>
    public IQueryOperator Left => _left;

    /// <summary>The right input operator.</summary>
    public IQueryOperator Right => _right;

    /// <summary>The type of join.</summary>
    public JoinType Type => _joinType;

    /// <summary>The ON condition from which equi-join keys were extracted.</summary>
    public JoinKeyExtractionResult Extraction => _extraction;

    /// <summary>The name of the sorted column on the left side.</summary>
    public string LeftSortColumn => _leftSortColumn;

    /// <summary>The name of the sorted column on the right side.</summary>
    public string RightSortColumn => _rightSortColumn;

    /// <inheritdoc/>
    public OperatorPlanDescription DescribeForExplain()
    {
        string joinTypeName = _joinType switch
        {
            JoinType.Inner => "Inner",
            JoinType.Left => "Left",
            JoinType.Right => "Right",
            JoinType.FullOuter => "Full Outer",
            _ => _joinType.ToString(),
        };

        Dictionary<string, string> properties = new()
        {
            ["type"] = joinTypeName,
            ["leftKey"] = _leftSortColumn,
            ["rightKey"] = _rightSortColumn,
        };

        return new OperatorPlanDescription($"{joinTypeName} Merge Join")
        {
            Properties = properties,
            Children = [(Left, "left (sorted)"), (Right, "right (sorted)")],
            Annotations = ["streaming merge — no hash table constructed"],
        };
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
    {
        Pool pool = context.Pool;
        LocalBufferPool bufferPool = context.LocalBufferPool;
        ExpressionEvaluator evaluator = new(context);
        Expression leftKeyExpression = _extraction.KeyPairs[0].Left;
        Expression rightKeyExpression = _extraction.KeyPairs[0].Right;

        bool leftMustAppear = _joinType is JoinType.Left or JoinType.FullOuter;
        bool rightMustAppear = _joinType is JoinType.Right or JoinType.FullOuter;

        CombinedRowSchema? schema = null;
        Row? residualCheckRow = null;
        DataValue[]? residualCheckBuffer = null;
        Row? cachedNullRight = null;
        Row? cachedNullLeft = null;

        // Materialize right-side duplicates for a given key value. For merge join,
        // when the left side has multiple rows with the same key, we must re-scan
        // the right-side group for each left row — so we buffer the right group.
        // Rows are stabilised into pool-rented DataValue[] arrays so advancing the
        // right cursor (which may return the source batch and its per-row arrays)
        // does not invalidate the references held here. Released in finally below.
        List<Row> rightGroup = new();

        await using IAsyncEnumerator<RowBatch> leftBatchEnumerator = _left.ExecuteAsync(context).GetAsyncEnumerator(context.CancellationToken);
        RowBatch? currentLeftBatch = null;
        int leftRowIndex = -1;

        await using IAsyncEnumerator<RowBatch> rightBatchEnumerator = _right.ExecuteAsync(context).GetAsyncEnumerator(context.CancellationToken);
        RowBatch? currentRightBatch = null;
        int rightRowIndex = -1;

        RowBatch? outputBatch = null;

        // Advance the left cursor to the next row, fetching new batches as needed.
        async ValueTask<bool> AdvanceLeftCursorAsync()
        {
            leftRowIndex++;

            if (currentLeftBatch is not null && leftRowIndex < currentLeftBatch.Count)
            {
                return true;
            }

            if (currentLeftBatch is not null) context.ReturnRowBatch(currentLeftBatch);
            currentLeftBatch = null;

            if (await leftBatchEnumerator.MoveNextAsync().ConfigureAwait(false))
            {
                currentLeftBatch = leftBatchEnumerator.Current;
                leftRowIndex = 0;
                return true;
            }

            return false;
        }

        // Advance the right cursor to the next row, fetching new batches as needed.
        async ValueTask<bool> AdvanceRightCursorAsync()
        {
            rightRowIndex++;

            if (currentRightBatch is not null && rightRowIndex < currentRightBatch.Count)
            {
                return true;
            }

            if (currentRightBatch is not null) context.ReturnRowBatch(currentRightBatch);
            currentRightBatch = null;

            if (await rightBatchEnumerator.MoveNextAsync().ConfigureAwait(false))
            {
                currentRightBatch = rightBatchEnumerator.Current;
                rightRowIndex = 0;
                return true;
            }

            return false;
        }

        // Retrieve the current row from the left batch cursor.
        Row GetCurrentLeftRow()
        {
            return (currentLeftBatch ?? throw new InvalidOperationException("Left batch cursor is null when hasLeft is true."))[leftRowIndex];
        }

        // Retrieve the current row from the right batch cursor.
        Row GetCurrentRightRow()
        {
            return (currentRightBatch ?? throw new InvalidOperationException("Right batch cursor is null when hasRight is true."))[rightRowIndex];
        }

        // Stabilise a right row into a pool-rented DataValue[] so the row can be
        // held across cursor advances that may return the source batch. Under
        // one-arena-per-query the arena args are the same reference, so
        // RentAndCopyDataValues takes its bulk-CopyTo fast path.
        Row StabiliseRightRow(Row row)
        {
            DataValue[] stable = pool.RentAndCopyDataValues(
                row, currentRightBatch!.Arena, context.Store);
            return new Row(row.ColumnLookup, stable);
        }

        bool hasLeft = await AdvanceLeftCursorAsync().ConfigureAwait(false);
        bool hasRight = await AdvanceRightCursorAsync().ConfigureAwait(false);

        if (ExecutionTracer.IsEnabled)
        {
            ExecutionTracer.Write(
                $"MergeJoin  start  type={_joinType}  leftKey={_leftSortColumn}  rightKey={_rightSortColumn}");
        }

        try
        {
            while (hasLeft && hasRight)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                Row leftRow = GetCurrentLeftRow();
                Row rightRow = GetCurrentRightRow();

                DataValue leftKey = await evaluator.EvaluateAsync(leftKeyExpression, leftRow, context.CancellationToken).ConfigureAwait(false);
                DataValue rightKey = await evaluator.EvaluateAsync(rightKeyExpression, rightRow, context.CancellationToken).ConfigureAwait(false);

                // NULL keys never match in SQL — emit with null-padded counterpart if required.
                if (leftKey.IsNull)
                {
                    if (leftMustAppear)
                    {
                        cachedNullRight ??= CreateNullRow(rightRow, pool);
                        schema ??= CombinedRowSchema.Build(leftRow, cachedNullRight.Value);
                        outputBatch ??= context.RentRowBatch(schema.ColumnLookup);
                        outputBatch.Add(schema.CombinePooledValues(leftRow, cachedNullRight.Value, bufferPool));
                        if (outputBatch.IsFull) { RowBatch b = outputBatch; outputBatch = null; yield return b; }
                    }

                    hasLeft = await AdvanceLeftCursorAsync().ConfigureAwait(false);
                    continue;
                }

                if (rightKey.IsNull)
                {
                    if (rightMustAppear)
                    {
                        cachedNullLeft ??= CreateNullRow(leftRow, pool);
                        schema ??= CombinedRowSchema.Build(cachedNullLeft.Value, rightRow);
                        outputBatch ??= context.RentRowBatch(schema.ColumnLookup);
                        outputBatch.Add(schema.CombinePooledValues(cachedNullLeft.Value, rightRow, bufferPool));
                        if (outputBatch.IsFull) { RowBatch b = outputBatch; outputBatch = null; yield return b; }
                    }

                    hasRight = await AdvanceRightCursorAsync().ConfigureAwait(false);
                    continue;
                }

                int comparison = CompareValues(leftKey, rightKey);

                if (comparison < 0)
                {
                    // Left key is smaller — no match on the right side.
                    if (leftMustAppear)
                    {
                        cachedNullRight ??= CreateNullRow(rightRow, pool);
                        schema ??= CombinedRowSchema.Build(leftRow, cachedNullRight.Value);
                        outputBatch ??= context.RentRowBatch(schema.ColumnLookup);
                        outputBatch.Add(schema.CombinePooledValues(leftRow, cachedNullRight.Value, bufferPool));
                        if (outputBatch.IsFull) { RowBatch b = outputBatch; outputBatch = null; yield return b; }
                    }

                    hasLeft = await AdvanceLeftCursorAsync().ConfigureAwait(false);
                }
                else if (comparison > 0)
                {
                    // Right key is smaller — no match on the left side.
                    if (rightMustAppear)
                    {
                        cachedNullLeft ??= CreateNullRow(leftRow, pool);
                        schema ??= CombinedRowSchema.Build(cachedNullLeft.Value, rightRow);
                        outputBatch ??= context.RentRowBatch(schema.ColumnLookup);
                        outputBatch.Add(schema.CombinePooledValues(cachedNullLeft.Value, rightRow, bufferPool));
                        if (outputBatch.IsFull) { RowBatch b = outputBatch; outputBatch = null; yield return b; }
                    }

                    hasRight = await AdvanceRightCursorAsync().ConfigureAwait(false);
                }
                else
                {
                    // Keys match — collect all right-side rows with this key value,
                    // then cross-product with all left-side rows sharing the same key.
                    // Return any previously stabilised group rows before clearing.
                    foreach (Row old in rightGroup) pool.ReturnRow(old);
                    rightGroup.Clear();

                    // Stabilise first right row before the first advance may return its batch.
                    rightGroup.Add(StabiliseRightRow(rightRow));

                    // Advance right to collect all duplicates.
                    while (true)
                    {
                        hasRight = await AdvanceRightCursorAsync().ConfigureAwait(false);

                        if (!hasRight)
                        {
                            break;
                        }

                        Row nextRightRow = GetCurrentRightRow();
                        DataValue nextRightKey = await evaluator.EvaluateAsync(rightKeyExpression, nextRightRow, context.CancellationToken).ConfigureAwait(false);

                        if (nextRightKey.IsNull || CompareValues(rightKey, nextRightKey) != 0)
                        {
                            break;
                        }

                        // Stabilise before the next advance may return this batch.
                        rightGroup.Add(StabiliseRightRow(nextRightRow));
                    }

                    // Cross-product all left rows with the buffered right group.
                    while (true)
                    {
                        bool leftRowMatched = false;

                        foreach (Row matchedRight in rightGroup)
                        {
                            schema ??= CombinedRowSchema.Build(leftRow, matchedRight);

                            // Apply residual filter for non-equi conjuncts.
                            if (_extraction.Residual is not null)
                            {
                                if (residualCheckRow is null)
                                {
                                    (residualCheckRow, residualCheckBuffer) = schema.CreateReusableRow();
                                }

                                schema.CombineInto(leftRow, matchedRight, residualCheckBuffer!);
                                if (!await evaluator.EvaluateAsBooleanAsync(_extraction.Residual, residualCheckRow.Value, context.CancellationToken).ConfigureAwait(false))
                                {
                                    continue;
                                }
                            }

                            leftRowMatched = true;
                            outputBatch ??= context.RentRowBatch(schema.ColumnLookup);
                            outputBatch.Add(schema.CombinePooledValues(leftRow, matchedRight, bufferPool));
                            if (outputBatch.IsFull) { RowBatch b = outputBatch; outputBatch = null; yield return b; }
                        }

                        if (!leftRowMatched && leftMustAppear)
                        {
                            // All right-group rows filtered out by residual — emit unmatched left.
                            cachedNullRight ??= CreateNullRow(rightGroup[0], pool);
                            schema ??= CombinedRowSchema.Build(leftRow, cachedNullRight.Value);
                            outputBatch ??= context.RentRowBatch(schema.ColumnLookup);
                            outputBatch.Add(schema.CombinePooledValues(leftRow, cachedNullRight.Value, bufferPool));
                            if (outputBatch.IsFull) { RowBatch b = outputBatch; outputBatch = null; yield return b; }
                        }

                        // Advance left and check if the next left row shares the same key.
                        hasLeft = await AdvanceLeftCursorAsync().ConfigureAwait(false);

                        if (!hasLeft)
                        {
                            break;
                        }

                        leftRow = GetCurrentLeftRow();
                        DataValue nextLeftKey = await evaluator.EvaluateAsync(leftKeyExpression, leftRow, context.CancellationToken).ConfigureAwait(false);

                        if (nextLeftKey.IsNull || CompareValues(leftKey, nextLeftKey) != 0)
                        {
                            break;
                        }
                    }
                }
            }

            // Drain remaining left rows (no match on the right side).
            while (hasLeft)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                if (leftMustAppear)
                {
                    Row leftRow = GetCurrentLeftRow();

                    if (cachedNullRight is not null)
                    {
                        schema ??= CombinedRowSchema.Build(leftRow, cachedNullRight.Value);
                        outputBatch ??= context.RentRowBatch(schema.ColumnLookup);
                        outputBatch.Add(schema.CombinePooledValues(leftRow, cachedNullRight.Value, bufferPool));
                        if (outputBatch.IsFull) { RowBatch b = outputBatch; outputBatch = null; yield return b; }
                    }
                    else
                    {
                        // No right rows were ever seen — emit left row alone.
                        outputBatch ??= context.RentRowBatch(leftRow.ColumnLookup);
                        DataValue[] copy = pool.RentAndCopyDataValues(
                            leftRow, currentLeftBatch!.Arena, outputBatch.Arena);
                        outputBatch.Add(copy);
                        if (outputBatch.IsFull) { RowBatch b = outputBatch; outputBatch = null; yield return b; }
                    }
                }

                hasLeft = await AdvanceLeftCursorAsync().ConfigureAwait(false);
            }

            // Drain remaining right rows (no match on the left side).
            while (hasRight)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                if (rightMustAppear)
                {
                    Row rightRow = GetCurrentRightRow();

                    if (cachedNullLeft is not null)
                    {
                        schema ??= CombinedRowSchema.Build(cachedNullLeft.Value, rightRow);
                        outputBatch ??= context.RentRowBatch(schema.ColumnLookup);
                        outputBatch.Add(schema.CombinePooledValues(cachedNullLeft.Value, rightRow, bufferPool));
                        if (outputBatch.IsFull) { RowBatch b = outputBatch; outputBatch = null; yield return b; }
                    }
                    else
                    {
                        // No left rows were ever seen — emit right row alone.
                        outputBatch ??= context.RentRowBatch(rightRow.ColumnLookup);
                        DataValue[] copy = pool.RentAndCopyDataValues(
                            rightRow, currentRightBatch!.Arena, outputBatch.Arena);
                        outputBatch.Add(copy);
                        if (outputBatch.IsFull) { RowBatch b = outputBatch; outputBatch = null; yield return b; }
                    }
                }

                hasRight = await AdvanceRightCursorAsync().ConfigureAwait(false);
            }

            if (outputBatch is not null)
            {
                RowBatch b = outputBatch;
                outputBatch = null;
                yield return b;
            }

            if (ExecutionTracer.IsEnabled)
            {
                ExecutionTracer.Write(
                    $"MergeJoin  done  type={_joinType}");
            }
        }
        finally
        {
            if (outputBatch is not null)
            {
                context.ReturnRowBatch(outputBatch);
            }

            foreach (Row row in rightGroup)
            {
                pool.ReturnRow(row);
            }

            if (cachedNullRight is not null)
            {
                pool.ReturnRow(cachedNullRight.Value);
            }

            if (cachedNullLeft is not null)
            {
                pool.ReturnRow(cachedNullLeft.Value);
            }

            // Return any open input batches not yet consumed by the cursor advances.
            if (currentLeftBatch is not null)
            {
                context.ReturnRowBatch(currentLeftBatch);
                currentLeftBatch = null;
            }

            if (currentRightBatch is not null)
            {
                context.ReturnRowBatch(currentRightBatch);
                currentRightBatch = null;
            }
        }
    }
}
