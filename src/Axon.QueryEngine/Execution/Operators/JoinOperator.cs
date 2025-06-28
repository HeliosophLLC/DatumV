using System.Collections;
using Axon.QueryEngine.Model;
using Axon.QueryEngine.Parsing.Ast;

namespace Axon.QueryEngine.Execution.Operators;

/// <summary>
/// Hash join operator supporting INNER, LEFT, RIGHT, FULL OUTER, and CROSS joins.
/// Materializes the build side into a hash table keyed by the join column, then
/// streams the probe side and looks up matches.
/// </summary>
public sealed class JoinOperator : IQueryOperator
{
    private readonly IQueryOperator _left;
    private readonly IQueryOperator _right;
    private readonly JoinType _joinType;
    private readonly Expression? _onCondition;

    /// <summary>
    /// Creates a join operator.
    /// </summary>
    /// <param name="left">The left (probe) side operator.</param>
    /// <param name="right">The right (build) side operator.</param>
    /// <param name="joinType">The type of join.</param>
    /// <param name="onCondition">The ON condition expression (null for CROSS join).</param>
    public JoinOperator(
        IQueryOperator left,
        IQueryOperator right,
        JoinType joinType,
        Expression? onCondition)
    {
        _left = left;
        _right = right;
        _joinType = joinType;
        _onCondition = onCondition;
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

        // For equi-joins, try to extract join key columns from the ON condition.
        // If the ON condition is a simple equality (left.col = right.col), use hash join.
        // Otherwise fall back to nested-loop with condition evaluation.
        (string? leftKey, string? rightKey) = TryExtractEquiJoinKeys(_onCondition);

        if (leftKey is not null && rightKey is not null)
        {
            await foreach (Row row in ExecuteHashJoinAsync(context, leftKey, rightKey).ConfigureAwait(false))
            {
                yield return row;
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

    private async IAsyncEnumerable<Row> ExecuteHashJoinAsync(
        ExecutionContext context, string leftKey, string rightKey)
    {
        // Build phase: materialize right side into hash table.
        Dictionary<DataValue, List<(int Index, Row Row)>> hashTable = new();
        List<Row> rightRows = new();

        await foreach (Row rightRow in _right.ExecuteAsync(context).ConfigureAwait(false))
        {
            int rightIndex = rightRows.Count;
            rightRows.Add(rightRow);

            if (!rightRow.TryGetValue(rightKey, out DataValue? keyValue) || keyValue!.IsNull)
            {
                continue; // NULL keys never match.
            }

            if (!hashTable.TryGetValue(keyValue, out List<(int, Row)>? bucket))
            {
                bucket = new List<(int, Row)>();
                hashTable[keyValue] = bucket;
            }

            bucket.Add((rightIndex, rightRow));
        }

        // Track which right rows have been matched (for RIGHT/FULL OUTER).
        bool needRightUnmatched = _joinType == JoinType.Right || _joinType == JoinType.FullOuter;
        BitArray? rightMatched = needRightUnmatched ? new BitArray(rightRows.Count) : null;

        // Probe phase: stream left side.
        await foreach (Row leftRow in _left.ExecuteAsync(context).ConfigureAwait(false))
        {
            bool hasMatch = false;

            if (leftRow.TryGetValue(leftKey, out DataValue? leftKeyValue) && !leftKeyValue!.IsNull)
            {
                if (hashTable.TryGetValue(leftKeyValue, out List<(int Index, Row Row)>? matches))
                {
                    foreach ((int rightIndex, Row rightRow) in matches)
                    {
                        hasMatch = true;
                        if (rightMatched is not null)
                        {
                            rightMatched[rightIndex] = true;
                        }

                        yield return CombineRows(leftRow, rightRow);
                    }
                }
            }

            if (!hasMatch && (_joinType == JoinType.Left || _joinType == JoinType.FullOuter))
            {
                if (rightRows.Count > 0)
                {
                    yield return CombineRows(leftRow, CreateNullRow(rightRows[0]));
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

            for (int index = 0; index < rightRows.Count; index++)
            {
                if (!rightMatched[index])
                {
                    nullLeft ??= await GetFirstLeftRowForNullPadAsync(context).ConfigureAwait(false);

                    if (nullLeft is not null)
                    {
                        yield return CombineRows(CreateNullRow(nullLeft), rightRows[index]);
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
        ExpressionEvaluator evaluator = new(context.FunctionRegistry);

        // Materialize right side.
        List<Row> rightRows = new();
        await foreach (Row rightRow in _right.ExecuteAsync(context).ConfigureAwait(false))
        {
            rightRows.Add(rightRow);
        }

        BitArray? rightMatched = (_joinType == JoinType.FullOuter || _joinType == JoinType.Right)
            ? new BitArray(rightRows.Count)
            : null;

        await foreach (Row leftRow in _left.ExecuteAsync(context).ConfigureAwait(false))
        {
            bool hasMatch = false;

            for (int index = 0; index < rightRows.Count; index++)
            {
                Row combinedRow = CombineRows(leftRow, rightRows[index]);

                if (_onCondition is null || evaluator.EvaluateAsBoolean(_onCondition, combinedRow))
                {
                    hasMatch = true;
                    if (rightMatched is not null)
                    {
                        rightMatched[index] = true;
                    }

                    yield return combinedRow;
                }
            }

            if (!hasMatch && (_joinType == JoinType.Left || _joinType == JoinType.FullOuter))
            {
                Row? nullRight = rightRows.Count > 0
                    ? CreateNullRow(rightRows[0])
                    : null;

                if (nullRight is not null)
                {
                    yield return CombineRows(leftRow, nullRight);
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

            for (int index = 0; index < rightRows.Count; index++)
            {
                if (!rightMatched[index])
                {
                    nullLeft ??= await GetFirstLeftRowForNullPadAsync(context).ConfigureAwait(false);

                    if (nullLeft is not null)
                    {
                        yield return CombineRows(CreateNullRow(nullLeft), rightRows[index]);
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

        await foreach (Row leftRow in _left.ExecuteAsync(context).ConfigureAwait(false))
        {
            foreach (Row rightRow in rightRows)
            {
                yield return CombineRows(leftRow, rightRow);
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
    /// Tries to extract column names from a simple equi-join condition (a.col = b.col).
    /// Returns null keys if the condition is not a simple equality on column references.
    /// </summary>
    private static (string? leftKey, string? rightKey) TryExtractEquiJoinKeys(Expression? condition)
    {
        if (condition is BinaryExpression binary
            && binary.Operator == BinaryOperator.Equal
            && binary.Left is ColumnReference leftCol
            && binary.Right is ColumnReference rightCol)
        {
            string leftName = leftCol.TableName is not null
                ? $"{leftCol.TableName}.{leftCol.ColumnName}"
                : leftCol.ColumnName;
            string rightName = rightCol.TableName is not null
                ? $"{rightCol.TableName}.{rightCol.ColumnName}"
                : rightCol.ColumnName;

            return (leftName, rightName);
        }

        return (null, null);
    }
}
