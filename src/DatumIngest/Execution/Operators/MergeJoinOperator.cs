using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using static DatumIngest.Execution.Operators.JoinOperator;
using static DatumIngest.Execution.StatisticsPredicateEvaluator;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Sort-merge join operator that joins two index-ordered input streams using a
/// two-pointer algorithm. Each input is backed by an <see cref="IndexScanOperator"/>
/// traversing either a <see cref="Indexing.SortedValueIndex"/> (sorted array) or a
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
    public async IAsyncEnumerable<Row> ExecuteAsync(ExecutionContext context)
    {
        ExpressionEvaluator evaluator = new(context.FunctionRegistry, context.QueryMeter, context.OuterRow);
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

        await using IAsyncEnumerator<Row> leftEnumerator = _left.ExecuteAsync(context).GetAsyncEnumerator(context.CancellationToken);
        await using IAsyncEnumerator<Row> rightEnumerator = _right.ExecuteAsync(context).GetAsyncEnumerator(context.CancellationToken);

        bool hasLeft = await leftEnumerator.MoveNextAsync().ConfigureAwait(false);
        bool hasRight = await rightEnumerator.MoveNextAsync().ConfigureAwait(false);

        while (hasLeft && hasRight)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            Row leftRow = leftEnumerator.Current;
            Row rightRow = rightEnumerator.Current;

            DataValue leftKey = evaluator.Evaluate(leftKeyExpression, leftRow);
            DataValue rightKey = evaluator.Evaluate(rightKeyExpression, rightRow);

            // NULL keys never match in SQL — emit with null-padded counterpart if required.
            if (leftKey.IsNull)
            {
                if (leftMustAppear)
                {
                    cachedNullRight ??= CreateNullRow(rightRow);
                    schema ??= CombinedRowSchema.Build(leftRow, cachedNullRight);
                    yield return schema.Combine(leftRow, cachedNullRight);
                }

                hasLeft = await leftEnumerator.MoveNextAsync().ConfigureAwait(false);
                continue;
            }

            if (rightKey.IsNull)
            {
                if (rightMustAppear)
                {
                    cachedNullLeft ??= CreateNullRow(leftRow);
                    schema ??= CombinedRowSchema.Build(cachedNullLeft, rightRow);
                    yield return schema.Combine(cachedNullLeft, rightRow);
                }

                hasRight = await rightEnumerator.MoveNextAsync().ConfigureAwait(false);
                continue;
            }

            int comparison = CompareValues(leftKey, rightKey);

            if (comparison < 0)
            {
                // Left key is smaller — no match on the right side.
                if (leftMustAppear)
                {
                    cachedNullRight ??= CreateNullRow(rightRow);
                    schema ??= CombinedRowSchema.Build(leftRow, cachedNullRight);
                    yield return schema.Combine(leftRow, cachedNullRight);
                }

                hasLeft = await leftEnumerator.MoveNextAsync().ConfigureAwait(false);
            }
            else if (comparison > 0)
            {
                // Right key is smaller — no match on the left side.
                if (rightMustAppear)
                {
                    cachedNullLeft ??= CreateNullRow(leftRow);
                    schema ??= CombinedRowSchema.Build(cachedNullLeft, rightRow);
                    yield return schema.Combine(cachedNullLeft, rightRow);
                }

                hasRight = await rightEnumerator.MoveNextAsync().ConfigureAwait(false);
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
                    hasRight = await rightEnumerator.MoveNextAsync().ConfigureAwait(false);

                    if (!hasRight)
                    {
                        break;
                    }

                    DataValue nextRightKey = evaluator.Evaluate(rightKeyExpression, rightEnumerator.Current);

                    if (nextRightKey.IsNull || CompareValues(rightKey, nextRightKey) != 0)
                    {
                        break;
                    }

                    rightGroup.Add(rightEnumerator.Current);
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
                            yield return candidateRow;
                        }
                        else
                        {
                            leftRowMatched = true;
                            schema ??= CombinedRowSchema.Build(leftRow, matchedRight);
                            yield return schema.Combine(leftRow, matchedRight);
                        }
                    }

                    if (!leftRowMatched && leftMustAppear)
                    {
                        // All right-group rows filtered out by residual — emit unmatched left.
                        cachedNullRight ??= CreateNullRow(rightGroup[0]);
                        schema ??= CombinedRowSchema.Build(leftRow, cachedNullRight);
                        yield return schema.Combine(leftRow, cachedNullRight);
                    }

                    // Advance left and check if the next left row shares the same key.
                    hasLeft = await leftEnumerator.MoveNextAsync().ConfigureAwait(false);

                    if (!hasLeft)
                    {
                        break;
                    }

                    leftRow = leftEnumerator.Current;
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
                Row leftRow = leftEnumerator.Current;

                if (cachedNullRight is not null)
                {
                    schema ??= CombinedRowSchema.Build(leftRow, cachedNullRight);
                    yield return schema.Combine(leftRow, cachedNullRight);
                }
                else
                {
                    // No right rows were ever seen — emit left row alone.
                    yield return leftRow;
                }
            }

            hasLeft = await leftEnumerator.MoveNextAsync().ConfigureAwait(false);
        }

        // Drain remaining right rows (no match on the left side).
        while (hasRight)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (rightMustAppear)
            {
                Row rightRow = rightEnumerator.Current;

                if (cachedNullLeft is not null)
                {
                    schema ??= CombinedRowSchema.Build(cachedNullLeft, rightRow);
                    yield return schema.Combine(cachedNullLeft, rightRow);
                }
                else
                {
                    // No left rows were ever seen — emit right row alone.
                    yield return rightRow;
                }
            }

            hasRight = await rightEnumerator.MoveNextAsync().ConfigureAwait(false);
        }
    }
}
