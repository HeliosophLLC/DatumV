using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution;

/// <summary>
/// Procedural-runtime helpers: scalar / predicate evaluation, scalar-
/// subquery pre-folding, boundary value lifting, and PRINT-style
/// rendering. Static so they can be reused by every procedural plan
/// class (<c>ProceduralLeafPlan</c>, <c>BlockPlan</c>, <c>IfPlan</c>,
/// <c>WhilePlan</c>, <c>ForCounterPlan</c>, <c>ForInPlan</c>,
/// <c>TryPlan</c>, <c>AssertPlan</c>, <c>RaisePlan</c>,
/// <c>AssignmentSelectPlan</c>, <c>ProcedureCallPlan</c>).
/// </summary>
internal static class ProceduralEvaluator
{
    /// <summary>
    /// Evaluates <paramref name="expression"/> by synthesising
    /// <c>SELECT &lt;expression&gt;</c>, planning it through the catalog,
    /// and reading the single resulting cell. The value is stabilised
    /// into <see cref="ExecutionContext.VariableStore"/> so it remains valid
    /// after the synthetic query's per-batch arena recycles. Throws if
    /// the expression doesn't yield exactly one row.
    /// </summary>
    /// <remarks>
    /// Any <see cref="SubqueryExpression"/> nodes in <paramref name="expression"/>
    /// are pre-folded into <see cref="LiteralExpression"/> nodes before
    /// the synthesise step — the catalog's sync planner doesn't run the
    /// scalar-subquery rewriter, so a surviving <c>SubqueryExpression</c>
    /// would crash at evaluation time. Pre-folding handles common
    /// procedural shapes like <c>DECLARE @c INT64 = (SELECT count(*) FROM t)</c>.
    /// </remarks>
    public static async Task<DataValue> EvaluateScalarAsync(
        Expression expression, ExecutionContext context, CancellationToken ct)
    {
        Expression rewritten = await PrefoldSubqueriesAsync(expression, context, ct)
            .ConfigureAwait(false);

        QueryStatement synthetic = new(
            new SelectQueryExpression(
                new SelectStatement(Columns: [new SelectColumn(rewritten)])));
        StatementPlan plan = await context.Catalog
            .PlanAsync(synthetic, sourceText: null)
            .ConfigureAwait(false);

        // Suppress streaming brackets on the internal SELECT — these
        // synthesised plans evaluate a procedural expression (DECLARE
        // initializer, SET value, IF / WHILE predicate, FOR bounds);
        // the user sees one cell per statement they typed, not per
        // internal expression eval.
        Execution.ExecutionContext silentContext = context.WithoutStreaming();
        DataValue stable = default;
        bool captured = false;
        await foreach (RowBatch batch in plan
            .ExecuteAsync(ct, silentContext)
            .ConfigureAwait(false))
        {
            if (captured) continue; // drain remainder; auto-pool happens on iteration
            if (batch.Count > 0)
            {
                // Stabilise into the variable store while the producing
                // arena is still alive (current batch's lifetime).
                stable = DataValueRetention.Stabilize(
                    batch[0][0], batch.Arena, context.VariableStore);
                captured = true;
            }
        }

        if (!captured)
        {
            throw new InvalidOperationException(
                "Procedural expression evaluation produced zero rows; expected exactly one.");
        }
        return stable;
    }

    /// <summary>
    /// Evaluates <paramref name="expression"/> as a boolean predicate.
    /// NULL is treated as false (T-SQL three-valued logic for control
    /// flow). Non-boolean types throw.
    /// </summary>
    public static async Task<bool> EvaluatePredicateAsync(
        Expression expression, ExecutionContext context, CancellationToken ct)
    {
        DataValue value = await EvaluateScalarAsync(expression, context, ct).ConfigureAwait(false);
        if (value.IsNull) return false;
        if (value.Kind == DataKind.Boolean) return value.AsBoolean();
        throw new InvalidOperationException(
            $"Procedural predicate evaluated to {value.Kind} rather than Boolean. " +
            "IF / WHILE predicates must be boolean-valued expressions.");
    }

    /// <summary>
    /// Lifts a <see cref="DataValue"/> produced by
    /// <see cref="EvaluateScalarAsync"/> (anchored in
    /// <see cref="ExecutionContext.VariableStore"/>) into a
    /// <see cref="ValueRef"/> for storage in <see cref="VariableScope"/>.
    /// Byte payloads materialise into managed memory so the binding
    /// survives any future arena recycle; inline scalars pass through
    /// unchanged.
    /// </summary>
    public static ValueRef LiftBoundaryValue(DataValue value, ExecutionContext context)
    {
        EvaluationFrame boundary = new(Row.Empty, context.VariableStore, context);
        return ExpressionEvaluator.ToValueRef(value, boundary);
    }

    /// <summary>
    /// Renders <paramref name="value"/> as a string for PRINT-style
    /// diagnostic output. Booleans render as lowercase
    /// <c>"true"</c>/<c>"false"</c> (SQL style); numerics use invariant
    /// culture so locale doesn't affect diagnostic output. NULL yields a
    /// <see langword="null"/> string so consumers can distinguish
    /// "missing" from the literal text "null".
    /// </summary>
    public static string? RenderForPrint(DataValue value, IValueStore store)
    {
        if (value.IsNull) return null;
        object? managed = value.Kind switch
        {
            DataKind.Boolean => value.AsBoolean(),
            DataKind.UInt8 => value.AsUInt8(),
            DataKind.Int8 => value.AsInt8(),
            DataKind.Int16 => value.AsInt16(),
            DataKind.UInt16 => value.AsUInt16(),
            DataKind.Int32 => value.AsInt32(),
            DataKind.UInt32 => value.AsUInt32(),
            DataKind.Int64 => value.AsInt64(),
            DataKind.UInt64 => value.AsUInt64(),
            DataKind.Float32 => value.AsFloat32(),
            DataKind.Float64 => value.AsFloat64(),
            DataKind.String => value.AsString(store),
            _ => (object?)value,
        };
        return managed switch
        {
            null => null,
            bool b => b ? "true" : "false",
            string s => s,
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => managed.ToString(),
        };
    }

    /// <summary>
    /// Coerces a numeric <see cref="DataValue"/> to <see cref="long"/>
    /// for FOR-counter bounds. Throws on non-numeric or null input;
    /// <paramref name="context"/> is interpolated into the message so
    /// the user sees which side of the loop bound failed.
    /// </summary>
    public static long ToInt64(DataValue value, string context)
    {
        if (value.IsNull)
        {
            throw new InvalidOperationException(
                $"{context} evaluated to NULL; numeric value required.");
        }
        return value.Kind switch
        {
            DataKind.Int8 => value.AsInt8(),
            DataKind.Int16 => value.AsInt16(),
            DataKind.Int32 => value.AsInt32(),
            DataKind.Int64 => value.AsInt64(),
            DataKind.UInt8 => value.AsUInt8(),
            DataKind.UInt16 => value.AsUInt16(),
            DataKind.UInt32 => value.AsUInt32(),
            DataKind.UInt64 => checked((long)value.AsUInt64()),
            DataKind.Float32 => (long)value.AsFloat32(),
            DataKind.Float64 => (long)value.AsFloat64(),
            _ => throw new InvalidOperationException(
                $"{context} evaluated to {value.Kind}; numeric value required."),
        };
    }

    /// <summary>
    /// Recursively walks <paramref name="expression"/>, replacing each
    /// <see cref="SubqueryExpression"/> with a folded
    /// <see cref="LiteralExpression"/>. Returns the original instance
    /// when no subquery is touched (cheap on subquery-free expressions).
    /// </summary>
    /// <remarks>
    /// The set of expression node types walked here is intentionally
    /// conservative: it covers the shapes that appear in well-formed
    /// procedural initializers and assignments. Extend the walker as new
    /// procedural patterns demand it.
    /// </remarks>
    public static async Task<Expression> PrefoldSubqueriesAsync(
        Expression expression, ExecutionContext context, CancellationToken ct)
    {
        switch (expression)
        {
            case SubqueryExpression subquery:
                return await FoldOneSubqueryAsync(subquery, context, ct).ConfigureAwait(false);

            case CastExpression cast:
            {
                Expression inner = await PrefoldSubqueriesAsync(cast.Expression, context, ct)
                    .ConfigureAwait(false);
                return ReferenceEquals(inner, cast.Expression)
                    ? cast
                    : new CastExpression(inner, cast.TargetType, cast.Span);
            }

            case BinaryExpression binary:
            {
                Expression left = await PrefoldSubqueriesAsync(binary.Left, context, ct)
                    .ConfigureAwait(false);
                Expression right = await PrefoldSubqueriesAsync(binary.Right, context, ct)
                    .ConfigureAwait(false);
                return ReferenceEquals(left, binary.Left) && ReferenceEquals(right, binary.Right)
                    ? binary
                    : new BinaryExpression(left, binary.Operator, right);
            }

            case UnaryExpression unary:
            {
                Expression operand = await PrefoldSubqueriesAsync(unary.Operand, context, ct)
                    .ConfigureAwait(false);
                return ReferenceEquals(operand, unary.Operand)
                    ? unary
                    : new UnaryExpression(unary.Operator, operand);
            }

            case IsNullExpression isNull:
            {
                Expression inner = await PrefoldSubqueriesAsync(isNull.Expression, context, ct)
                    .ConfigureAwait(false);
                return ReferenceEquals(inner, isNull.Expression)
                    ? isNull
                    : new IsNullExpression(inner, isNull.Negated);
            }

            case FunctionCallExpression fn:
            {
                Expression[]? rewrittenArgs = null;
                for (int i = 0; i < fn.Arguments.Count; i++)
                {
                    Expression rewritten = await PrefoldSubqueriesAsync(fn.Arguments[i], context, ct)
                        .ConfigureAwait(false);
                    if (!ReferenceEquals(rewritten, fn.Arguments[i]))
                    {
                        rewrittenArgs ??= [.. fn.Arguments];
                        rewrittenArgs[i] = rewritten;
                    }
                }
                return rewrittenArgs is null
                    ? fn
                    : new FunctionCallExpression(fn.FunctionName, rewrittenArgs, fn.OrderBy, fn.Distinct, fn.Span, fn.WithinGroupOrderBy);
            }

            case InExpression inExpr:
            {
                Expression target = await PrefoldSubqueriesAsync(inExpr.Expression, context, ct)
                    .ConfigureAwait(false);
                Expression[]? rewrittenValues = null;
                for (int i = 0; i < inExpr.Values.Count; i++)
                {
                    Expression rewritten = await PrefoldSubqueriesAsync(inExpr.Values[i], context, ct)
                        .ConfigureAwait(false);
                    if (!ReferenceEquals(rewritten, inExpr.Values[i]))
                    {
                        rewrittenValues ??= [.. inExpr.Values];
                        rewrittenValues[i] = rewritten;
                    }
                }
                return ReferenceEquals(target, inExpr.Expression) && rewrittenValues is null
                    ? inExpr
                    : new InExpression(target, rewrittenValues ?? inExpr.Values, inExpr.Negated);
            }

            case BetweenExpression between:
            {
                Expression target = await PrefoldSubqueriesAsync(between.Expression, context, ct)
                    .ConfigureAwait(false);
                Expression low = await PrefoldSubqueriesAsync(between.Low, context, ct)
                    .ConfigureAwait(false);
                Expression high = await PrefoldSubqueriesAsync(between.High, context, ct)
                    .ConfigureAwait(false);
                return ReferenceEquals(target, between.Expression)
                    && ReferenceEquals(low, between.Low)
                    && ReferenceEquals(high, between.High)
                    ? between
                    : new BetweenExpression(target, low, high, between.Negated);
            }

            case CaseExpression caseExpr:
            {
                Expression? operand = caseExpr.Operand;
                if (operand is not null)
                {
                    operand = await PrefoldSubqueriesAsync(operand, context, ct)
                        .ConfigureAwait(false);
                }
                WhenClause[]? rewrittenClauses = null;
                for (int i = 0; i < caseExpr.WhenClauses.Count; i++)
                {
                    WhenClause clause = caseExpr.WhenClauses[i];
                    Expression cond = await PrefoldSubqueriesAsync(clause.Condition, context, ct)
                        .ConfigureAwait(false);
                    Expression result = await PrefoldSubqueriesAsync(clause.Result, context, ct)
                        .ConfigureAwait(false);
                    if (!ReferenceEquals(cond, clause.Condition) || !ReferenceEquals(result, clause.Result))
                    {
                        rewrittenClauses ??= [.. caseExpr.WhenClauses];
                        rewrittenClauses[i] = new WhenClause(cond, result);
                    }
                }
                Expression? elseResult = caseExpr.ElseResult;
                if (elseResult is not null)
                {
                    elseResult = await PrefoldSubqueriesAsync(elseResult, context, ct)
                        .ConfigureAwait(false);
                }
                bool unchanged = ReferenceEquals(operand, caseExpr.Operand)
                    && rewrittenClauses is null
                    && ReferenceEquals(elseResult, caseExpr.ElseResult);
                return unchanged
                    ? caseExpr
                    : new CaseExpression(operand, rewrittenClauses ?? caseExpr.WhenClauses, elseResult, caseExpr.Span);
            }

            default:
                return expression;
        }
    }

    /// <summary>
    /// Plans + executes the inner SELECT of a
    /// <see cref="SubqueryExpression"/> and returns a
    /// <see cref="LiteralExpression"/> wrapping its single-row,
    /// single-column result. Mirrors SQL standard scalar-subquery
    /// semantics: zero rows → NULL literal, more than one row → error.
    /// </summary>
    public static async Task<Expression> FoldOneSubqueryAsync(
        SubqueryExpression subquery, ExecutionContext context, CancellationToken ct)
    {
        QueryStatement innerStatement = new(new SelectQueryExpression(subquery.Query));
        StatementPlan innerPlan = await context.Catalog
            .PlanAsync(innerStatement, sourceText: null)
            .ConfigureAwait(false);

        DataValue captured = default;
        bool haveValue = false;
        bool tooManyRows = false;
        await foreach (RowBatch batch in innerPlan
            .ExecuteAsync(ct, context)
            .ConfigureAwait(false))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (haveValue)
                {
                    tooManyRows = true;
                    break;
                }
                Row row = batch[i];
                if (row.FieldCount != 1)
                {
                    throw new InvalidOperationException(
                        $"Scalar subquery must return exactly one column, but returned {row.FieldCount}.");
                }
                // Stabilise into the procedure-lifetime store before the
                // producing arena recycles on the next iteration.
                captured = DataValueRetention.Stabilize(
                    row[0], batch.Arena, context.VariableStore);
                haveValue = true;
            }
            if (tooManyRows) break;
        }

        if (tooManyRows)
        {
            throw new InvalidOperationException(
                "Scalar subquery returned more than one row.");
        }

        if (!haveValue || captured.IsNull)
        {
            return new LiteralExpression(null);
        }

        // Materialise the DataValue into a CLR object suitable for
        // LiteralExpression. The synthesise-SELECT path will re-pack via
        // the literal lowerer, so primitives flow back through the engine
        // without needing arena access.
        object literal = captured.Kind switch
        {
            DataKind.Int8 => (object)(sbyte)captured.AsInt8(),
            DataKind.Int16 => captured.AsInt16(),
            DataKind.Int32 => captured.AsInt32(),
            DataKind.Int64 => captured.AsInt64(),
            DataKind.UInt8 => (sbyte)captured.AsUInt8(),
            DataKind.Float32 => captured.AsFloat32(),
            DataKind.Float64 => captured.AsFloat64(),
            DataKind.String => captured.AsString(),
            DataKind.Boolean => captured.AsBoolean(),
            _ => captured.ToFloat(),
        };
        return new LiteralExpression(literal);
    }
}
