using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Executes a lateral (correlated) join by re-executing the right-hand operator
/// for every row from the left side, with the left row set as
/// <see cref="ExecutionContext.OuterRow"/> so the right side can reference left-side columns.
/// Supports <see cref="JoinType.Cross"/> (skip left rows with no right matches) and
/// <see cref="JoinType.Left"/> (emit left + NULLs when no right rows match).
/// This is an O(N × M) nested-loop strategy; no hash acceleration is possible
/// because the right side is recomputed per outer row.
/// </summary>
public sealed class LateralJoinOperator : IQueryOperator
{
    private readonly IQueryOperator _left;
    private readonly IQueryOperator _right;
    private readonly JoinType _joinType;
    private readonly Expression? _onCondition;

    /// <summary>
    /// Creates a lateral join operator.
    /// </summary>
    /// <param name="left">The outer (driving) operator.</param>
    /// <param name="right">The inner (lateral) operator, re-executed per outer row.</param>
    /// <param name="joinType">
    /// <see cref="JoinType.Cross"/> for cross lateral or <see cref="JoinType.Left"/> for left lateral.
    /// </param>
    /// <param name="onCondition">Optional ON condition applied after combining rows.</param>
    public LateralJoinOperator(
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

    /// <summary>The outer (driving) operator.</summary>
    public IQueryOperator Left => _left;

    /// <summary>The inner (lateral) operator.</summary>
    public IQueryOperator Right => _right;

    /// <summary>The join type (Cross or Left).</summary>
    public JoinType JoinType => _joinType;

    /// <summary>The optional ON condition expression.</summary>
    public Expression? OnCondition => _onCondition;

    /// <inheritdoc/>
    public OperatorPlanDescription DescribeForExplain()
    {
        string joinTypeName = _joinType switch
        {
            JoinType.Cross => "Cross",
            JoinType.Left => "Left",
            _ => _joinType.ToString(),
        };

        Dictionary<string, string> properties = new()
        {
            ["type"] = joinTypeName,
        };

        if (_onCondition is not null)
        {
            properties["on"] = QueryExplainer.FormatExpression(_onCondition);
        }

        return new OperatorPlanDescription($"Lateral {joinTypeName} Join")
        {
            Properties = properties,
            Children = [(Left, "driving"), (Right, "lateral")],
            Warnings = ["re-executes lateral side per driving row"],
        };
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
    {
        Pool pool = context.Pool;
        LocalBufferPool bufferPool = context.LocalBufferPool;
        ExpressionEvaluator evaluator = new(context);
        JoinOperator.CombinedRowSchema? schema = null;
        Row? residualCheckRow = null;
        DataValue[]? residualCheckBuffer = null;
        Row? cachedNullRight = null;
        RowBatch? outputBatch = null;

        try
        {
            await foreach (RowBatch leftBatch in _left.ExecuteAsync(context).ConfigureAwait(false))
            {
                try
                {
                    for (int leftIndex = 0; leftIndex < leftBatch.Count; leftIndex++)
                    {
                        Row leftRow = leftBatch[leftIndex];
                        context.CancellationToken.ThrowIfCancellationRequested();

                        // Execute the right side with the current left row as correlation context.
                        ExecutionContext lateralContext = context.WithOuterRow(leftRow);
                        bool matched = false;

                        await foreach (RowBatch rightBatch in _right.ExecuteAsync(lateralContext).ConfigureAwait(false))
                        {
                            try
                            {
                                for (int rightIndex = 0; rightIndex < rightBatch.Count; rightIndex++)
                                {
                                    Row rightRow = rightBatch[rightIndex];
                                    schema ??= JoinOperator.CombinedRowSchema.Build(leftRow, rightRow);

                                    if (_onCondition is not null)
                                    {
                                        if (residualCheckRow is null)
                                        {
                                            (residualCheckRow, residualCheckBuffer) = schema.CreateReusableRow();
                                        }

                                        schema.CombineInto(leftRow, rightRow, residualCheckBuffer!);
                                        if (!evaluator.EvaluateAsBoolean(_onCondition, residualCheckRow.Value))
                                        {
                                            continue;
                                        }
                                    }

                                    matched = true;
                                    cachedNullRight ??= JoinOperator.CreateNullRow(rightRow, pool);
                                    outputBatch ??= context.RentRowBatch(schema.ColumnLookup);
                                    outputBatch.Add(schema.CombinePooledValues(leftRow, rightRow, bufferPool));

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
                                context.ReturnRowBatch(rightBatch);
                            }
                        }

                        // LEFT LATERAL: emit left + NULLs when no right rows matched.
                        if (!matched && _joinType == JoinType.Left)
                        {
                            if (schema is not null && cachedNullRight is not null)
                            {
                                outputBatch ??= context.RentRowBatch(schema.ColumnLookup);
                                outputBatch.Add(schema.CombinePooledValues(leftRow, cachedNullRight.Value, bufferPool));
                            }
                            else
                            {
                                // No right row has ever been observed — emit the left row solo
                                // so the empty-lateral case still surfaces the driving row. Copy
                                // the values into a pool-rented buffer so the output batch owns
                                // its rows independent of the left batch's rental.
                                outputBatch ??= context.RentRowBatch(leftRow.ColumnLookup);
                                DataValue[] copy = pool.RentAndCopyDataValues(
                                    leftRow, leftBatch.Arena, outputBatch.Arena);
                                outputBatch.Add(copy);
                            }

                            if (outputBatch.IsFull)
                            {
                                RowBatch toYield = outputBatch;
                                outputBatch = null;
                                yield return toYield;
                            }
                        }
                    }
                }
                finally
                {
                    context.ReturnRowBatch(leftBatch);
                }
            }

            if (outputBatch is not null)
            {
                RowBatch toYield = outputBatch;
                outputBatch = null;
                yield return toYield;
            }
        }
        finally
        {
            if (outputBatch is not null)
            {
                context.ReturnRowBatch(outputBatch);
            }

            if (cachedNullRight is not null)
            {
                pool.ReturnRow(cachedNullRight.Value);
            }
        }
    }
}
