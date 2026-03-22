using DatumIngest.Functions;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

/// <summary>
/// Plan-time validator for <see cref="BodyScopeRequirement"/>. Walks every
/// <see cref="FunctionCallExpression"/> reachable from a top-level query
/// AST and refuses calls to body-scoped functions (today: <c>infer()</c>)
/// because the planner is only ever entered for queries that aren't model
/// bodies — model bodies are interpreted by
/// <c>DatumIngest.Functions.ProceduralModelFunction</c> directly.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a dedicated walker.</strong> The planner doesn't itself
/// resolve scalar functions (operator construction keeps the
/// <see cref="FunctionCallExpression"/> AST nodes intact and the runtime
/// evaluator looks them up per-row). Without a dedicated pre-pass,
/// catching scope violations at plan time would mean weaving a check
/// through every operator-construction site that handles expressions —
/// brittle and easy to miss. One walker, one entry point.
/// </para>
/// <para>
/// <strong>Defense-in-depth.</strong> The runtime guard inside the
/// function (e.g. <c>InferFunction.ExecuteAsync</c> checking
/// <c>frame.CurrentModel</c>) stays as a backstop. Anything that
/// constructs a plan or evaluates an expression outside the standard
/// <see cref="QueryPlanner"/> entry still hits a hard floor.
/// </para>
/// </remarks>
internal static class BodyScopeGate
{
    /// <summary>
    /// Walks every reachable <see cref="FunctionCallExpression"/> in
    /// <paramref name="query"/> and throws on the first body-scoped call.
    /// </summary>
    public static void EnforceForQuery(QueryExpression query, FunctionRegistry functions)
    {
        switch (query)
        {
            case SelectQueryExpression select:
                Visit(select.Statement, functions);
                break;
            case CompoundQueryExpression compound:
                EnforceForQuery(compound.Left, functions);
                EnforceForQuery(compound.Right, functions);
                break;
            case InsertQueryExpression insertQuery:
                Visit(insertQuery.Insert, functions);
                break;
        }
    }

    private static void Visit(SelectStatement select, FunctionRegistry functions)
    {
        if (select.LetBindings is not null)
        {
            foreach (LetBinding binding in select.LetBindings)
            {
                VisitExpression(binding.Expression, functions);
            }
        }

        foreach (SelectColumn column in select.Columns)
        {
            VisitExpression(column.Expression, functions);
        }

        if (select.From is not null)
        {
            VisitTableSource(select.From.Source, functions);
        }

        if (select.Joins is not null)
        {
            foreach (JoinClause join in select.Joins)
            {
                VisitTableSource(join.Source, functions);
                if (join.OnCondition is not null) VisitExpression(join.OnCondition, functions);
            }
        }

        if (select.Where is not null) VisitExpression(select.Where, functions);
        if (select.GroupBy is not null)
        {
            foreach (Expression g in select.GroupBy.Expressions) VisitExpression(g, functions);
        }
        if (select.Having is not null) VisitExpression(select.Having, functions);
        if (select.Qualify is not null) VisitExpression(select.Qualify, functions);
        if (select.OrderBy is not null)
        {
            foreach (OrderByItem item in select.OrderBy.Items) VisitExpression(item.Expression, functions);
        }
        if (select.Limit is not null) VisitExpression(select.Limit, functions);
        if (select.Offset is not null) VisitExpression(select.Offset, functions);
    }

    private static void Visit(InsertStatement insert, FunctionRegistry functions)
    {
        switch (insert.Source)
        {
            case InsertQuerySource qs:
                EnforceForQuery(qs.Query, functions);
                break;
            case InsertValuesSource vs:
                foreach (IReadOnlyList<Expression> tuple in vs.Rows)
                {
                    foreach (Expression e in tuple) VisitExpression(e, functions);
                }
                break;
            // InsertDefaultValuesSource has no expressions.
        }
    }

    private static void VisitTableSource(TableSource source, FunctionRegistry functions)
    {
        switch (source)
        {
            case SubquerySource sub:
                Visit(sub.Query, functions);
                break;
            case FunctionSource fn:
                foreach (Expression arg in fn.Arguments) VisitExpression(arg, functions);
                break;
            // TableReference + other leaf sources have no expressions to descend into.
        }
    }

    private static void VisitExpression(Expression expression, FunctionRegistry functions)
    {
        switch (expression)
        {
            case FunctionCallExpression fn:
                EnforceCall(fn, functions);
                foreach (Expression arg in fn.Arguments) VisitExpression(arg, functions);
                break;
            case BinaryExpression b:
                VisitExpression(b.Left, functions);
                VisitExpression(b.Right, functions);
                break;
            case UnaryExpression u:
                VisitExpression(u.Operand, functions);
                break;
            case CastExpression c:
                VisitExpression(c.Expression, functions);
                break;
            case CaseExpression ce:
                if (ce.Operand is not null) VisitExpression(ce.Operand, functions);
                foreach (WhenClause w in ce.WhenClauses)
                {
                    VisitExpression(w.Condition, functions);
                    VisitExpression(w.Result, functions);
                }
                if (ce.ElseResult is not null) VisitExpression(ce.ElseResult, functions);
                break;
            case InExpression ie:
                VisitExpression(ie.Expression, functions);
                foreach (Expression v in ie.Values) VisitExpression(v, functions);
                break;
            case BetweenExpression be:
                VisitExpression(be.Expression, functions);
                VisitExpression(be.Low, functions);
                VisitExpression(be.High, functions);
                break;
            case IsNullExpression isn:
                VisitExpression(isn.Expression, functions);
                break;
            case LikeExpression lk:
                VisitExpression(lk.Expression, functions);
                VisitExpression(lk.Pattern, functions);
                VisitExpression(lk.EscapeCharacter, functions);
                break;
            case AtTimeZoneExpression atz:
                VisitExpression(atz.Expression, functions);
                VisitExpression(atz.TimeZone, functions);
                break;
            case StructLiteralExpression sl:
                foreach (StructField f in sl.Fields) VisitExpression(f.Value, functions);
                break;
            case IndexAccessExpression ix:
                VisitExpression(ix.Source, functions);
                foreach (Expression i in ix.Indices)
                    VisitExpression(i, functions);
                break;
            case SubqueryExpression subq:
                Visit(subq.Query, functions);
                break;
            // Leaves: literals, column refs, parameters, variables — no descent.
        }
    }

    private static void EnforceCall(FunctionCallExpression fn, FunctionRegistry functions)
    {
        FunctionDescriptor? descriptor = functions.TryGetScalarDescriptor(fn.CallName);
        if (descriptor is null) return;

        if (descriptor.BodyScope == BodyScopeRequirement.ModelBody)
        {
            throw new InvalidOperationException(
                $"{fn.CallName}() is only valid inside a CREATE [OR REPLACE] MODEL "
                + "body. Move the call into a model body, or use the underlying "
                + "scalar form for the computation. See `SELECT * FROM "
                + "datum_catalog.functions WHERE body_scope = 'modelbody'` for the "
                + "full list of body-scoped functions.");
        }
    }
}
