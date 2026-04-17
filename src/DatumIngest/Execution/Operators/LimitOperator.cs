using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Takes a limited number of rows from a child operator, optionally
/// skipping an offset. Propagates cancellation upstream once the
/// limit is reached.
/// </summary>
/// <remarks>
/// LIMIT and OFFSET accept arbitrary scalar expressions
/// (<c>LIMIT @rowCount</c>, <c>OFFSET random(0, 1000)</c>,
/// <c>LIMIT udf.computeLimit()</c>, etc.). The expressions are
/// evaluated <strong>once</strong> at the start of
/// <see cref="QueryOperator.ExecuteAsync(ExecutionContext)"/> against the operator's
/// <see cref="ExecutionContext"/> — no per-row evaluation cost.
/// Constant literals (the common case) are folded by the planner
/// when it builds the operator, so the expression here is typically
/// a <c>LiteralExpression</c> wrapping the int.
/// </remarks>
public sealed class LimitOperator : QueryOperator
{
    private readonly QueryOperator _source;
    private readonly Expression _limitExpression;
    private readonly Expression? _offsetExpression;

    /// <summary>
    /// Creates a limit operator with literal int values. Convenience
    /// constructor for tests and call sites that already have folded
    /// constants in hand. Wraps the values in
    /// <see cref="LiteralExpression"/>s for the canonical Expression-based
    /// shape.
    /// </summary>
    public LimitOperator(QueryOperator source, int limit, int offset = 0)
        : this(
            source,
            new LiteralExpression(limit),
            offset == 0 ? null : new LiteralExpression(offset))
    {
    }

    /// <summary>
    /// Creates a limit operator with expressions evaluated at execute time.
    /// </summary>
    /// <param name="source">The child operator producing rows.</param>
    /// <param name="limitExpression">Expression yielding the maximum row count.</param>
    /// <param name="offsetExpression">Optional expression yielding the row count to skip; <see langword="null"/> means no offset.</param>
    public LimitOperator(
        QueryOperator source,
        Expression limitExpression,
        Expression? offsetExpression = null)
    {
        _source = source;
        _limitExpression = limitExpression;
        _offsetExpression = offsetExpression;
    }

    /// <summary>The child operator producing rows.</summary>
    public QueryOperator Source => _source;

    /// <summary>The unevaluated limit expression. <see cref="LiteralExpression"/> for plan-time folded constants.</summary>
    public Expression LimitExpression => _limitExpression;

    /// <summary>The unevaluated offset expression, or <see langword="null"/> when no offset was specified.</summary>
    public Expression? OffsetExpression => _offsetExpression;

    /// <inheritdoc/>
    public override QueryOperator RewriteExpressions(Func<Expression, Expression> rewriter) =>
        new LimitOperator(
            _source.RewriteExpressions(rewriter),
            rewriter(_limitExpression),
            _offsetExpression is null ? null : rewriter(_offsetExpression));

    /// <inheritdoc/>
    protected override OperatorPlanDescription DescribeForExplainImpl()
    {
        Dictionary<string, string> properties = new()
        {
            ["limit"] = QueryExplainer.FormatExpression(_limitExpression),
        };

        if (_offsetExpression is not null)
        {
            properties["offset"] = QueryExplainer.FormatExpression(_offsetExpression);
        }

        // EstimatedRows is best-effort: when the limit is a constant the
        // planner-side fold lands a literal, which surfaces as a clean
        // numeric. For runtime-evaluated limits we don't know the row
        // budget at plan time, so we leave it null.
        int? estimatedRows = TryExtractConstantInt(_limitExpression);

        return new OperatorPlanDescription("Limit")
        {
            Properties = properties,
            Children = [(Source, null)],
            EstimatedRows = estimatedRows,
        };
    }

    private static int? TryExtractConstantInt(Expression expr)
    {
        // Numeric literal narrowing in the parser picks the smallest integer
        // type that fits, so a literal LIMIT 10 boxes as sbyte, LIMIT 1000
        // as short, and so on. Accept every integer kind so the EXPLAIN
        // estimated-row-count path lands a value whenever the literal does.
        if (expr is LiteralExpression lit)
        {
            return lit.Value switch
            {
                sbyte sb => sb,
                byte b => b,
                short sh => sh,
                ushort us => us,
                int i => i,
                uint u when u <= int.MaxValue => (int)u,
                long l when l >= int.MinValue && l <= int.MaxValue => (int)l,
                _ => null,
            };
        }
        return null;
    }

    /// <inheritdoc/>
    protected override async IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context)
    {
        // Evaluate LIMIT / OFFSET expressions once at the start. Plain
        // literal expressions short-circuit without touching the evaluator;
        // anything else (variable references, arithmetic, function calls)
        // resolves through the standard expression pipeline against the
        // active variable scope and store.
        int Limit = await EvaluateRowCountAsync(_limitExpression, "LIMIT", context).ConfigureAwait(false);
        int Offset = _offsetExpression is null
            ? 0
            : await EvaluateRowCountAsync(_offsetExpression, "OFFSET", context).ConfigureAwait(false);

        if (Limit < 0)
        {
            throw new InvalidOperationException($"LIMIT must be non-negative, got {Limit}.");
        }
        if (Offset < 0)
        {
            throw new InvalidOperationException($"OFFSET must be non-negative, got {Offset}.");
        }

        // Propagate the row limit hint so downstream operators (e.g. join,
        // model invocation) can choose cheaper strategies when only a small
        // result set is needed. Build the limited context FIRST, then alias
        // to it for the source dispatch — the previous shape captured the
        // alias from the unmodified context, so the new RowLimit never
        // reached upstream operators.
        ExecutionContext limitedContext = context;

        if (context.RowLimit is null || Limit + Offset < context.RowLimit)
        {
            limitedContext = new ExecutionContext(context)
            {
                OuterRow = context.OuterRow,
                MaxRecursionDepth = context.MaxRecursionDepth,
                RowLimit = Limit + Offset,
                DegreeOfParallelism = context.DegreeOfParallelism,
                ParallelismBudget = context.ParallelismBudget,
            };
        }

        int skipped = 0;
        int emitted = 0;
        RowCopyOutputWriter writer = new(context);

        try
        {
            await foreach (RowBatch inputBatch in _source.ExecuteAsync(limitedContext).ConfigureAwait(false))
            {
                // Fast path 1: the entire batch lies inside the skip region — drop it.
                if (skipped + inputBatch.Count <= Offset)
                {
                    skipped += inputBatch.Count;
                    context.ReturnRowBatch(inputBatch);
                    continue;
                }

                // The skip region is behind us (fully or partially consumed by this batch).
                int startIndex = Offset - skipped;
                skipped = Offset;
                int take = Math.Min(inputBatch.Count - startIndex, Limit - emitted);

                // Fast path 2: no partial start, whole batch fits inside the remaining limit,
                // and the writer has nothing pending — pass the input batch straight through
                // to the consumer without touching the arena. HasPendingBatch guard preserves
                // batch-boundary semantics (no extra small flush before the full batch).
                if (!writer.HasPendingBatch && startIndex == 0 && take == inputBatch.Count)
                {
                    emitted += take;
                    yield return inputBatch;

                    if (emitted >= Limit) yield break;
                    continue;
                }

                // Mixed path: copy the [startIndex, startIndex + take) slice into outputBatch.
                try
                {
                    int end = startIndex + take;
                    for (int index = startIndex; index < end; index++)
                    {
                        RowBatch? full = writer.Add(inputBatch, index);
                        emitted++;
                        if (full is not null) yield return full;
                    }
                }
                finally
                {
                    context.ReturnRowBatch(inputBatch);
                }

                if (emitted >= Limit)
                {
                    RowBatch? trailing = writer.Flush();
                    if (trailing is not null) yield return trailing;
                    yield break;
                }
            }

            RowBatch? remaining = writer.Flush();
            if (remaining is not null) yield return remaining;
        }
        finally
        {
            RowBatch? leftover = writer.Flush();
            if (leftover is not null) context.ReturnRowBatch(leftover);
        }
    }

    /// <summary>
    /// Evaluates a LIMIT or OFFSET expression to a row count. Constant
    /// literals short-circuit without invoking the evaluator; everything
    /// else (variable references, arithmetic, UDF calls) goes through the
    /// standard expression pipeline against the operator's
    /// <see cref="ExecutionContext"/>. Errors include the clause name so
    /// the user can tell LIMIT vs OFFSET apart at a glance.
    /// </summary>
    private static async ValueTask<int> EvaluateRowCountAsync(
        Expression expression, string clauseName, ExecutionContext context)
    {
        if (expression is LiteralExpression lit)
        {
            // Plan-time constant fold for the common case: no evaluator
            // construction, no async machinery beyond the ValueTask wrap.
            // Accept every integer kind because the parser narrows literals
            // to the smallest fitting type (LIMIT 10 → sbyte, LIMIT 1000 →
            // short, etc.).
            return lit.Value switch
            {
                sbyte sb => sb,
                byte b => b,
                short sh => sh,
                ushort us => us,
                int i => i,
                uint u => checked((int)u),
                long l => checked((int)l),
                _ => throw new InvalidOperationException(
                    $"{clauseName} expects an integer; got literal of kind {lit.Value?.GetType().Name ?? "null"}."),
            };
        }

        ExpressionEvaluator evaluator = context.CreateEvaluator();
        EvaluationFrame frame = new(
            DatumIngest.Model.Row.Empty,
            context.Store,
            context,
            outerRow: context.OuterRow);
        DatumIngest.Model.DataValue value = await evaluator
            .EvaluateAsync(expression, frame, context.CancellationToken)
            .ConfigureAwait(false);

        if (value.IsNull)
        {
            throw new InvalidOperationException($"{clauseName} expression evaluated to NULL.");
        }
        return value.Kind switch
        {
            DatumIngest.Model.DataKind.Int32 => value.AsInt32(),
            DatumIngest.Model.DataKind.Int64 => checked((int)value.AsInt64()),
            DatumIngest.Model.DataKind.Int16 => value.AsInt16(),
            DatumIngest.Model.DataKind.Int8 => value.AsInt8(),
            DatumIngest.Model.DataKind.UInt32 => checked((int)value.AsUInt32()),
            DatumIngest.Model.DataKind.UInt16 => value.AsUInt16(),
            DatumIngest.Model.DataKind.UInt8 => value.AsUInt8(),
            _ => throw new InvalidOperationException(
                $"{clauseName} expression must yield an integer; got {value.Kind}."),
        };
    }
}
