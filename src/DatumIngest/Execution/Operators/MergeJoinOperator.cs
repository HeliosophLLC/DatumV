using DatumIngest.Diagnostics;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
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
        ExpressionEvaluator evaluator = new(context.FunctionRegistry, context.QueryMeter, context.OuterRow, store: context.Store);
        Expression leftKeyExpression = _extraction.KeyPairs[0].Left;
        Expression rightKeyExpression = _extraction.KeyPairs[0].Right;

        bool leftMustAppear = _joinType is JoinType.Left or JoinType.FullOuter;
        bool rightMustAppear = _joinType is JoinType.Right or JoinType.FullOuter;

        CombinedRowSchema? schema = null;
        Row? cachedNullRight = null;
        Row? cachedNullLeft = null;

        // Materialize right-side duplicates for a given key value. For merge join,
        // when the left side has multiple rows with the same key, we must re-scan
        // the right-side group for each left row — so we buffer the right group.
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

            currentLeftBatch?.Return();
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

            currentRightBatch?.Return();
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

        bool hasLeft = await AdvanceLeftCursorAsync().ConfigureAwait(false);
        bool hasRight = await AdvanceRightCursorAsync().ConfigureAwait(false);

        if (ExecutionTracer.IsEnabled)
        {
            ExecutionTracer.Write(
                $"MergeJoin  start  type={_joinType}  leftKey={_leftSortColumn}  rightKey={_rightSortColumn}");
        }

        while (hasLeft && hasRight)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            Row leftRow = GetCurrentLeftRow();
            Row rightRow = GetCurrentRightRow();

            DataValue leftKey = evaluator.Evaluate(leftKeyExpression, leftRow);
            DataValue rightKey = evaluator.Evaluate(rightKeyExpression, rightRow);

            // NULL keys never match in SQL — emit with null-padded counterpart if required.
            if (leftKey.IsNull)
            {
                if (leftMustAppear)
                {
                    cachedNullRight ??= CreateNullRow(rightRow);
                    schema ??= CombinedRowSchema.Build(leftRow, cachedNullRight.Value);
                    outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                    outputBatch.Add(schema.Combine(leftRow, cachedNullRight.Value));
                    if (outputBatch.IsFull) { yield return outputBatch; outputBatch = null; }
                }

                hasLeft = await AdvanceLeftCursorAsync().ConfigureAwait(false);
                continue;
            }

            if (rightKey.IsNull)
            {
                if (rightMustAppear)
                {
                    cachedNullLeft ??= CreateNullRow(leftRow);
                    schema ??= CombinedRowSchema.Build(cachedNullLeft.Value, rightRow);
                    outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                    outputBatch.Add(schema.Combine(cachedNullLeft.Value, rightRow));
                    if (outputBatch.IsFull) { yield return outputBatch; outputBatch = null; }
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
                    cachedNullRight ??= CreateNullRow(rightRow);
                    schema ??= CombinedRowSchema.Build(leftRow, cachedNullRight.Value);
                    outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                    outputBatch.Add(schema.Combine(leftRow, cachedNullRight.Value));
                    if (outputBatch.IsFull) { yield return outputBatch; outputBatch = null; }
                }

                hasLeft = await AdvanceLeftCursorAsync().ConfigureAwait(false);
            }
            else if (comparison > 0)
            {
                // Right key is smaller — no match on the left side.
                if (rightMustAppear)
                {
                    cachedNullLeft ??= CreateNullRow(leftRow);
                    schema ??= CombinedRowSchema.Build(cachedNullLeft.Value, rightRow);
                    outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                    outputBatch.Add(schema.Combine(cachedNullLeft.Value, rightRow));
                    if (outputBatch.IsFull) { yield return outputBatch; outputBatch = null; }
                }

                hasRight = await AdvanceRightCursorAsync().ConfigureAwait(false);
            }
            else
            {
                // Keys match — collect all right-side rows with this key value,
                // then cross-product with all left-side rows sharing the same key.
                rightGroup.Clear();
                rightGroup.Add(rightRow);

                // Advance right to collect all duplicates.
                while (true)
                {
                    hasRight = await AdvanceRightCursorAsync().ConfigureAwait(false);

                    if (!hasRight)
                    {
                        break;
                    }

                    DataValue nextRightKey = evaluator.Evaluate(rightKeyExpression, GetCurrentRightRow());

                    if (nextRightKey.IsNull || CompareValues(rightKey, nextRightKey) != 0)
                    {
                        break;
                    }

                    rightGroup.Add(GetCurrentRightRow());
                }

                // Cross-product all left rows with the buffered right group.
                while (true)
                {
                    bool leftRowMatched = false;

                    foreach (Row matchedRight in rightGroup)
                    {
                        // Apply residual filter for non-equi conjuncts.
                        if (_extraction.Residual is not null)
                        {
                            schema ??= CombinedRowSchema.Build(leftRow, matchedRight);
                            Row candidateRow = schema.Combine(leftRow, matchedRight);

                            if (!evaluator.EvaluateAsBoolean(_extraction.Residual, candidateRow))
                            {
                                continue;
                            }

                            leftRowMatched = true;
                            outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                            outputBatch.Add(candidateRow);
                            if (outputBatch.IsFull) { yield return outputBatch; outputBatch = null; }
                        }
                        else
                        {
                            leftRowMatched = true;
                            schema ??= CombinedRowSchema.Build(leftRow, matchedRight);
                            outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                            outputBatch.Add(schema.Combine(leftRow, matchedRight));
                            if (outputBatch.IsFull) { yield return outputBatch; outputBatch = null; }
                        }
                    }

                    if (!leftRowMatched && leftMustAppear)
                    {
                        // All right-group rows filtered out by residual — emit unmatched left.
                        cachedNullRight ??= CreateNullRow(rightGroup[0]);
                        schema ??= CombinedRowSchema.Build(leftRow, cachedNullRight.Value);
                        outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                        outputBatch.Add(schema.Combine(leftRow, cachedNullRight.Value));
                        if (outputBatch.IsFull) { yield return outputBatch; outputBatch = null; }
                    }

                    // Advance left and check if the next left row shares the same key.
                    hasLeft = await AdvanceLeftCursorAsync().ConfigureAwait(false);

                    if (!hasLeft)
                    {
                        break;
                    }

                    leftRow = GetCurrentLeftRow();
                    DataValue nextLeftKey = evaluator.Evaluate(leftKeyExpression, leftRow);

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
                    outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                    outputBatch.Add(schema.Combine(leftRow, cachedNullRight.Value));
                    if (outputBatch.IsFull) { yield return outputBatch; outputBatch = null; }
                }
                else
                {
                    // No right rows were ever seen — emit left row alone.
                    outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                    outputBatch.Add(leftRow);
                    if (outputBatch.IsFull) { yield return outputBatch; outputBatch = null; }
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
                    outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                    outputBatch.Add(schema.Combine(cachedNullLeft.Value, rightRow));
                    if (outputBatch.IsFull) { yield return outputBatch; outputBatch = null; }
                }
                else
                {
                    // No left rows were ever seen — emit right row alone.
                    outputBatch ??= context.LocalBufferPool.RentBatch(context.BatchSize);
                    outputBatch.Add(rightRow);
                    if (outputBatch.IsFull) { yield return outputBatch; outputBatch = null; }
                }
            }

            hasRight = await AdvanceRightCursorAsync().ConfigureAwait(false);
        }

        if (outputBatch is not null)
        {
            yield return outputBatch;
        }

        if (ExecutionTracer.IsEnabled)
        {
            ExecutionTracer.Write(
                $"MergeJoin  done  type={_joinType}");
        }
    }
}
