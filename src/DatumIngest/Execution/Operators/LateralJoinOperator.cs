using DatumIngest.Execution.Operators.Joins;
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
public sealed class LateralJoinOperator : QueryOperator
{
    private readonly QueryOperator _left;
    private readonly QueryOperator _right;
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
        QueryOperator left,
        QueryOperator right,
        JoinType joinType,
        Expression? onCondition)
    {
        _left = left;
        _right = right;
        _joinType = joinType;
        _onCondition = onCondition;
    }

    /// <summary>The outer (driving) operator.</summary>
    public QueryOperator Left => _left;

    /// <summary>The inner (lateral) operator.</summary>
    public QueryOperator Right => _right;

    /// <summary>The join type (Cross or Left).</summary>
    public JoinType JoinType => _joinType;

    /// <summary>The optional ON condition expression.</summary>
    public Expression? OnCondition => _onCondition;

    /// <inheritdoc/>
    protected override OperatorPlanDescription DescribeForExplainImpl()
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
    protected override async IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context)
    {
        Pool pool = context.Pool;
        ExpressionEvaluator evaluator = new(context);
        JoinOutputWriter writer = new(context);
        Row? residualCheckRow = null;
        DataValue[]? residualCheckBuffer = null;
        NullPadCache cachedNullRight = new(pool);

        // Buffer for unmatched LEFT-LATERAL driving rows seen before any right
        // row has materialised. We can't emit combined-with-null-pad until we
        // have a right-side template, and emitting left-solo would mix schemas
        // in the output batch once a later leftRow matches. Stabilise into
        // context.Store so deferred rows survive their source leftBatch's
        // return. Flushed when the first right row appears (as combined emits
        // preserving input order) or at end-of-execution (as left-solo if no
        // right row ever appears).
        List<Row>? deferredUnmatchedLefts = null;

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

                                    if (_onCondition is not null)
                                    {
                                        JoinSchema schema = writer.GetCombinedSchema(leftRow, rightRow);
                                        if (residualCheckRow is null)
                                        {
                                            (residualCheckRow, residualCheckBuffer) = schema.CreateReusableRow();
                                        }

                                        schema.CombineInto(leftRow, rightRow, residualCheckBuffer!);
                                        if (!await evaluator.EvaluateAsBooleanAsync(_onCondition, residualCheckRow.Value, context.CancellationToken).ConfigureAwait(false))
                                        {
                                            continue;
                                        }
                                    }

                                    matched = true;
                                    bool firstRightRowEver = !cachedNullRight.HasValue;
                                    cachedNullRight.Initialize(rightRow);

                                    // The first right row anywhere unlocks the combined schema.
                                    // Flush any deferred unmatched lefts now — in input order,
                                    // before this leftRow's own match emit — as combined-with-null-pad.
                                    if (firstRightRowEver && deferredUnmatchedLefts is not null)
                                    {
                                        foreach (Row deferred in deferredUnmatchedLefts)
                                        {
                                            if (writer.EmitCombined(deferred, cachedNullRight.Value) is RowBatch flushReady)
                                                yield return flushReady;
                                            pool.ReturnRow(deferred);
                                        }
                                        deferredUnmatchedLefts = null;
                                    }

                                    if (writer.EmitCombined(leftRow, rightRow) is RowBatch ready) yield return ready;
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
                            if (cachedNullRight.HasValue)
                            {
                                // At least one right row materialised earlier — emit this
                                // unmatched left with the cached null-padded right immediately.
                                if (writer.EmitCombined(leftRow, cachedNullRight.Value) is RowBatch ready) yield return ready;
                            }
                            else
                            {
                                // No right row has appeared yet anywhere. Stabilise into
                                // context.Store and defer; we'll emit combined-with-null-pad
                                // if a right row appears later, or fall back to left-solo at
                                // end of execution if not.
                                DataValue[] stableValues = pool.RentAndCopyDataValues(leftRow, leftBatch.Arena, context.Store);
                                deferredUnmatchedLefts ??= new List<Row>();
                                deferredUnmatchedLefts.Add(new Row(leftRow.ColumnLookup, stableValues));
                            }
                        }
                    }
                }
                finally
                {
                    context.ReturnRowBatch(leftBatch);
                }
            }

            // End of driving rows. Anything still deferred means no right row ever
            // materialised — fall back to left-solo emit (the only schema we have).
            if (deferredUnmatchedLefts is not null)
            {
                foreach (Row deferred in deferredUnmatchedLefts)
                {
                    if (writer.EmitPassThrough(deferred, context.Store) is RowBatch ready) yield return ready;
                    pool.ReturnRow(deferred);
                }
                deferredUnmatchedLefts = null;
            }

            if (writer.Flush() is RowBatch trailing) yield return trailing;
        }
        finally
        {
            if (writer.Flush() is RowBatch leftover) context.ReturnRowBatch(leftover);

            cachedNullRight.Return();

            // Exception-path cleanup for any stabilised rows still buffered.
            if (deferredUnmatchedLefts is not null)
            {
                foreach (Row deferred in deferredUnmatchedLefts)
                {
                    pool.ReturnRow(deferred);
                }
            }
        }
    }
}
