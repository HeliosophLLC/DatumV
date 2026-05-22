using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Models;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution;

/// <summary>
/// Plan-time validator that walks every <see cref="FunctionCallExpression"/>
/// reachable from a top-level query AST and refuses calls the planner
/// shouldn't be asked to lower. Today it enforces two checks:
/// <list type="bullet">
///   <item><description><strong>Unknown function names.</strong> A misspelled
///     call like <c>bright(file)</c> falls through scalar dispatch at runtime
///     and only throws once the row is evaluated. In a SELECT list whose other
///     projections warm up ONNX sessions, that can be seconds of wasted work
///     before the typo surfaces. Catching it before any operator is built
///     turns "long pipeline fails after model warm-up" into milliseconds.
///     </description></item>
///   <item><description><strong>Body-scoped calls outside a body.</strong>
///     <c>infer()</c> and similar <see cref="BodyScopeRequirement.ModelBody"/>
///     functions are only valid inside CREATE [OR REPLACE] MODEL bodies, which
///     are interpreted by <c>Heliosoph.DatumV.Functions.ProceduralModelFunction</c>
///     directly and never reach this gate. Any body-scoped call we see here
///     is unambiguously out of context.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a dedicated walker.</strong> The planner doesn't itself
/// resolve scalar functions — operator construction keeps the
/// <see cref="FunctionCallExpression"/> AST nodes intact and the runtime
/// evaluator looks them up per-row. Without a dedicated pre-pass, catching
/// these failures at plan time would mean weaving checks through every
/// operator-construction site that handles expressions — brittle and easy
/// to miss. One walker, one entry point.
/// </para>
/// <para>
/// <strong>Why ModelCatalog as a separate parameter.</strong> The planner's
/// real <c>models.X</c> resolution path (<c>ModelInvocationHoister</c>) goes
/// through <see cref="ModelCatalog"/> directly, not through
/// <see cref="FunctionRegistry"/>. <see cref="FunctionRegistry"/> can fall
/// back to the model catalog when a resolver is wired (production sets one
/// up in <c>TableCatalog</c>'s constructor), but tests sometimes construct
/// a registry without that wiring. Consulting the model catalog directly
/// makes the gate's resolution match the planner's regardless.
/// </para>
/// <para>
/// <strong>Defense-in-depth.</strong> The runtime guards stay as a backstop:
/// <c>ExpressionEvaluator.EvaluateFunctionAsValueRefAsync</c> still throws
/// "Unknown function" on a name miss, and body-scoped functions still check
/// <c>frame.CurrentModel</c> in their <c>ExecuteAsync</c>. Anything that
/// evaluates an expression outside the standard <see cref="QueryPlanner"/>
/// entry still hits a hard floor.
/// </para>
/// </remarks>
internal static class PlanTimeFunctionGate
{
    private const string ModelSchema = "models";

    /// <summary>
    /// Walks every reachable <see cref="FunctionCallExpression"/> in
    /// <paramref name="query"/> and throws on the first unknown name or
    /// body-scope violation.
    /// </summary>
    public static void EnforceForQuery(QueryExpression query, FunctionRegistry functions, ModelCatalog? models)
    {
        switch (query)
        {
            case SelectQueryExpression select:
                Visit(select.Statement, functions, models);
                break;
            case CompoundQueryExpression compound:
                EnforceForQuery(compound.Left, functions, models);
                EnforceForQuery(compound.Right, functions, models);
                break;
            case InsertQueryExpression insertQuery:
                Visit(insertQuery.Insert, functions, models);
                break;
        }
    }

    private static void Visit(SelectStatement select, FunctionRegistry functions, ModelCatalog? models)
    {
        if (select.LetBindings is not null)
        {
            foreach (LetBinding binding in select.LetBindings)
            {
                VisitExpression(binding.Expression, functions, models);
            }
        }

        foreach (SelectColumn column in select.Columns)
        {
            VisitExpression(column.Expression, functions, models);
        }

        if (select.From is not null)
        {
            VisitTableSource(select.From.Source, functions, models);
        }

        if (select.Joins is not null)
        {
            foreach (JoinClause join in select.Joins)
            {
                VisitTableSource(join.Source, functions, models);
                if (join.OnCondition is not null) VisitExpression(join.OnCondition, functions, models);
            }
        }

        if (select.Where is not null) VisitExpression(select.Where, functions, models);
        if (select.GroupBy is not null)
        {
            foreach (Expression g in select.GroupBy.Expressions) VisitExpression(g, functions, models);
        }
        if (select.Having is not null) VisitExpression(select.Having, functions, models);
        if (select.Qualify is not null) VisitExpression(select.Qualify, functions, models);
        if (select.OrderBy is not null)
        {
            foreach (OrderByItem item in select.OrderBy.Items) VisitExpression(item.Expression, functions, models);
        }
        if (select.Limit is not null) VisitExpression(select.Limit, functions, models);
        if (select.Offset is not null) VisitExpression(select.Offset, functions, models);
    }

    private static void Visit(InsertStatement insert, FunctionRegistry functions, ModelCatalog? models)
    {
        switch (insert.Source)
        {
            case InsertQuerySource qs:
                EnforceForQuery(qs.Query, functions, models);
                break;
            case InsertValuesSource vs:
                foreach (IReadOnlyList<Expression> tuple in vs.Rows)
                {
                    foreach (Expression e in tuple) VisitExpression(e, functions, models);
                }
                break;
            // InsertDefaultValuesSource has no expressions.
        }
    }

    private static void VisitTableSource(TableSource source, FunctionRegistry functions, ModelCatalog? models)
    {
        switch (source)
        {
            case SubquerySource sub:
                Visit(sub.Query, functions, models);
                break;
            case FunctionSource fn:
                foreach (Expression arg in fn.Arguments) VisitExpression(arg, functions, models);
                break;
            // TableReference + other leaf sources have no expressions to descend into.
        }
    }

    private static void VisitExpression(Expression expression, FunctionRegistry functions, ModelCatalog? models)
    {
        switch (expression)
        {
            case FunctionCallExpression fn:
                EnforceCall(fn, functions, models);
                foreach (Expression arg in fn.Arguments) VisitExpression(arg, functions, models);
                break;
            case BinaryExpression b:
                VisitExpression(b.Left, functions, models);
                VisitExpression(b.Right, functions, models);
                break;
            case UnaryExpression u:
                VisitExpression(u.Operand, functions, models);
                break;
            case CastExpression c:
                VisitExpression(c.Expression, functions, models);
                break;
            case CaseExpression ce:
                if (ce.Operand is not null) VisitExpression(ce.Operand, functions, models);
                foreach (WhenClause w in ce.WhenClauses)
                {
                    VisitExpression(w.Condition, functions, models);
                    VisitExpression(w.Result, functions, models);
                }
                if (ce.ElseResult is not null) VisitExpression(ce.ElseResult, functions, models);
                break;
            case InExpression ie:
                VisitExpression(ie.Expression, functions, models);
                foreach (Expression v in ie.Values) VisitExpression(v, functions, models);
                break;
            case BetweenExpression be:
                VisitExpression(be.Expression, functions, models);
                VisitExpression(be.Low, functions, models);
                VisitExpression(be.High, functions, models);
                break;
            case IsNullExpression isn:
                VisitExpression(isn.Expression, functions, models);
                break;
            case LikeExpression lk:
                VisitExpression(lk.Expression, functions, models);
                VisitExpression(lk.Pattern, functions, models);
                VisitExpression(lk.EscapeCharacter, functions, models);
                break;
            case AtTimeZoneExpression atz:
                VisitExpression(atz.Expression, functions, models);
                VisitExpression(atz.TimeZone, functions, models);
                break;
            case StructLiteralExpression sl:
                foreach (StructField f in sl.Fields) VisitExpression(f.Value, functions, models);
                break;
            case IndexAccessExpression ix:
                VisitExpression(ix.Source, functions, models);
                foreach (Expression i in ix.Indices)
                    VisitExpression(i, functions, models);
                break;
            case SubqueryExpression subq:
                Visit(subq.Query, functions, models);
                break;
            // Leaves: literals, column refs, parameters, variables — no descent.
        }
    }

    private static void EnforceCall(FunctionCallExpression fn, FunctionRegistry functions, ModelCatalog? models)
    {
        // Scalar — the dominant case. Existing scalar means body-scope is the
        // only check left to run.
        IScalarFunction? scalar = functions.TryGetScalar(fn.CallName);
        if (scalar is not null)
        {
            FunctionDescriptor? descriptor = functions.TryGetScalarDescriptor(fn.CallName);
            if (descriptor is not null && descriptor.BodyScope == BodyScopeRequirement.ModelBody)
            {
                throw new InvalidOperationException(
                    $"{fn.CallName}() is only valid inside a CREATE [OR REPLACE] MODEL "
                    + "body. Move the call into a model body, or use the underlying "
                    + "scalar form for the computation. See `SELECT * FROM "
                    + "system.functions WHERE body_scope = 'modelbody'` for the "
                    + "full list of body-scoped functions.");
            }
            return;
        }

        // `models.X` — the planner's ModelInvocationHoister resolves these
        // through ModelCatalog directly. Mirror that here so the gate works
        // even when the registry isn't wired with a model-catalog resolver.
        if (string.Equals(fn.SchemaName, ModelSchema, StringComparison.OrdinalIgnoreCase)
            && models?.TryGetEntry(fn.FunctionName) is not null)
        {
            return;
        }

        // Not a scalar. Aggregates and window functions are also legal in
        // SELECT / HAVING / QUALIFY etc. — defer their semantic placement
        // checks to the planner's existing resolution; we only care here
        // that the name resolves somewhere.
        if (functions.TryGetAggregate(fn.CallName) is not null) return;
        if (functions.TryGetWindow(fn.CallName) is not null) return;

        // Name exists as a TVF — helpful redirect mirroring the runtime's
        // ExpressionEvaluator.EvaluateFunctionAsValueRefAsync message so users
        // get the same nudge whether the typo is caught here or later.
        if (functions.TryGetTableValued(fn.CallName) is not null)
        {
            throw new InvalidOperationException(
                $"'{fn.CallName}' is a table-valued function; use it in a FROM clause "
                + $"(e.g. SELECT * FROM {fn.CallName}(...)) rather than as a scalar expression.");
        }

        throw new InvalidOperationException($"Unknown function: '{fn.CallName}'.");
    }
}
