using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

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
    public async IAsyncEnumerable<Row> ExecuteAsync(ExecutionContext context)
    {
        ExpressionEvaluator evaluator = new(context.FunctionRegistry, context.QueryMeter, context.OuterRow);
        JoinOperator.CombinedRowSchema? schema = null;
        Row? cachedNullRight = null;

        await foreach (Row leftRow in _left.ExecuteAsync(context).ConfigureAwait(false))
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            // Execute the right side with the current left row as correlation context.
            ExecutionContext lateralContext = context.WithOuterRow(leftRow);
            bool matched = false;

            await foreach (Row rightRow in _right.ExecuteAsync(lateralContext).ConfigureAwait(false))
            {
                schema ??= JoinOperator.CombinedRowSchema.Build(leftRow, rightRow);
                Row combined = schema.Combine(leftRow, rightRow);

                if (_onCondition is not null && !evaluator.EvaluateAsBoolean(_onCondition, combined))
                {
                    continue;
                }

                matched = true;
                cachedNullRight ??= JoinOperator.CreateNullRow(rightRow);
                yield return combined;
            }

            // LEFT LATERAL: emit left + NULLs when no right rows matched.
            if (!matched && _joinType == JoinType.Left)
            {
                if (cachedNullRight is not null)
                {
                    schema ??= JoinOperator.CombinedRowSchema.Build(leftRow, cachedNullRight);
                    yield return schema.Combine(leftRow, cachedNullRight);
                }
                else
                {
                    yield return leftRow;
                }
            }
        }
    }
}
