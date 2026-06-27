using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution.Planner;

/// <summary>
/// Plan-time AST rewrite that lifts a table-valued function call appearing as
/// the top-level expression of a <see cref="SelectStatement"/>'s projection
/// list into a synthesized <see cref="FunctionSource"/> in FROM (joined as
/// CROSS JOIN LATERAL when a FROM already exists), then replaces the
/// projection entry with a table-wildcard pointing at the synthesized source.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors PostgreSQL's behavior of expanding set-returning function calls in
/// the SELECT list into row-multiplying sources. v1 supports a single such
/// call per <see cref="SelectStatement"/> and only at the top level of a
/// projection expression — nested forms (<c>SELECT upper(unnest(arr))</c>),
/// usage in WHERE / HAVING / GROUP BY / ORDER BY / QUALIFY / join conditions /
/// LET bindings, and multiple set-returning calls in the same projection list
/// each raise a clean diagnostic pointing the user at the FROM-clause form.
/// </para>
/// <para>
/// Runs alongside <see cref="NamedArgPermuter"/> in
/// <see cref="QueryPlanner.Plan(QueryExpression)"/>, before
/// <see cref="PlanTimeFunctionGate.EnforceForQuery"/>. After the rewrite no
/// table-valued call remains in scalar position, so the gate's "use it in a
/// FROM clause" diagnostic naturally only fires for cases the rewriter
/// chose not to handle.
/// </para>
/// </remarks>
public static class ProjectionSetReturningRewriter
{
    private const string SyntheticAlias = "__srf_0";

    /// <summary>
    /// Returns a new <see cref="QueryExpression"/> with set-returning calls in
    /// projection lists lifted into synthesized FROM sources. The input tree
    /// is not mutated; statements with nothing to rewrite are returned as-is.
    /// </summary>
    public static QueryExpression Rewrite(QueryExpression query, FunctionRegistry functions) =>
        query switch
        {
            SelectQueryExpression sel => new SelectQueryExpression(RewriteSelect(sel.Statement, functions)),
            CompoundQueryExpression compound => new CompoundQueryExpression(
                Rewrite(compound.Left, functions),
                compound.OperationType,
                compound.All,
                Rewrite(compound.Right, functions),
                compound.OrderBy,
                compound.Limit,
                compound.Offset),
            _ => query,
        };

    private static SelectStatement RewriteSelect(SelectStatement stmt, FunctionRegistry functions)
    {
        // Recurse into nested SELECTs first so inner statements get their own
        // SRF projections lifted before we look at this statement's columns.
        stmt = stmt with
        {
            From = stmt.From is { } from ? new FromClause(RewriteTableSource(from.Source, functions)) : null,
            Joins = stmt.Joins?.Select(j => RewriteJoin(j, functions)).ToList(),
            Where = stmt.Where is { } w ? RewriteExpressionSubqueries(w, functions) : null,
            Having = stmt.Having is { } h ? RewriteExpressionSubqueries(h, functions) : null,
            Qualify = stmt.Qualify is { } q ? RewriteExpressionSubqueries(q, functions) : null,
            GroupBy = stmt.GroupBy is { } gb
                ? new GroupByClause(gb.Expressions.Select(e => RewriteExpressionSubqueries(e, functions)).ToList(), gb.IsAll)
                : null,
            OrderBy = stmt.OrderBy is { } ob
                ? new OrderByClause(ob.Items
                    .Select(i => new OrderByItem(RewriteExpressionSubqueries(i.Expression, functions), i.Direction))
                    .ToList())
                : null,
            Columns = stmt.Columns
                .Select(c => c is SelectAllColumns or SelectTableColumns
                    ? c
                    : c with { Expression = RewriteExpressionSubqueries(c.Expression, functions) })
                .ToList(),
            LetBindings = stmt.LetBindings
                ?.Select(b => b with { Expression = RewriteExpressionSubqueries(b.Expression, functions) })
                .ToList(),
            CommonTableExpressions = stmt.CommonTableExpressions
                ?.Select(cte => cte with
                {
                    Body = Rewrite(cte.Body, functions),
                    RecursiveQuery = cte.RecursiveQuery is { } rq ? RewriteSelect(rq, functions) : null,
                })
                .ToList(),
        };

        // Reject TVF references in non-projection clauses with a clean diagnostic.
        RejectTvfInClause(stmt.Where, "WHERE", functions);
        RejectTvfInClause(stmt.Having, "HAVING", functions);
        RejectTvfInClause(stmt.Qualify, "QUALIFY", functions);
        if (stmt.GroupBy is not null)
        {
            foreach (Expression e in stmt.GroupBy.Expressions) RejectTvfInClause(e, "GROUP BY", functions);
        }
        if (stmt.OrderBy is not null)
        {
            foreach (OrderByItem i in stmt.OrderBy.Items) RejectTvfInClause(i.Expression, "ORDER BY", functions);
        }
        if (stmt.Joins is not null)
        {
            foreach (JoinClause j in stmt.Joins) RejectTvfInClause(j.OnCondition, "JOIN ON", functions);
        }
        if (stmt.LetBindings is not null)
        {
            foreach (LetBinding b in stmt.LetBindings) RejectTvfInClause(b.Expression, "LET binding", functions);
        }

        // Scan projection columns for a top-level TVF call.
        int srfIndex = -1;
        for (int i = 0; i < stmt.Columns.Count; i++)
        {
            SelectColumn col = stmt.Columns[i];
            if (col is SelectAllColumns or SelectTableColumns) continue;

            FunctionCallExpression? topLevelSrf =
                col.Expression is FunctionCallExpression fce
                && functions.TryGetTableValued(fce.CallName) is not null
                    ? fce
                    : null;

            if (topLevelSrf is null)
            {
                // No top-level TVF — but any TVF nested inside the expression is unsupported.
                string? nested = FindFirstTvfName(col.Expression, functions);
                if (nested is not null)
                {
                    throw new InvalidOperationException(
                        $"Set-returning function '{nested}' is only supported as the top-level expression "
                        + "of a SELECT column. Pull the call into a CTE or FROM-clause subquery to combine "
                        + "it with other expressions.");
                }
                continue;
            }

            // Nested TVFs inside the top-level call's own arguments are also unsupported.
            foreach (Expression arg in topLevelSrf.Arguments)
            {
                string? nested = FindFirstTvfName(arg, functions);
                if (nested is not null)
                {
                    throw new InvalidOperationException(
                        $"Set-returning function '{nested}' cannot be nested inside the arguments of "
                        + $"another set-returning function ('{topLevelSrf.CallName}').");
                }
            }

            if (srfIndex >= 0)
            {
                throw new InvalidOperationException(
                    "Only one set-returning function per SELECT projection is supported. "
                    + "Use FROM <name>(...) for additional set-returning sources, or split into a UNION.");
            }
            srfIndex = i;
        }

        if (srfIndex < 0) return stmt;

        FunctionCallExpression srfCall = (FunctionCallExpression)stmt.Columns[srfIndex].Expression;

        FunctionSource srfSource = new(
            srfCall.FunctionName,
            srfCall.Arguments,
            SyntheticAlias,
            srfCall.Span,
            srfCall.SchemaName);

        FromClause? newFrom;
        IReadOnlyList<JoinClause>? newJoins;

        if (stmt.From is null)
        {
            // No FROM yet — the SRF becomes the primary source. Joins (if any
            // were attached without a base FROM, which the parser shouldn't
            // produce, but defend anyway) carry forward unchanged.
            newFrom = new FromClause(srfSource);
            newJoins = stmt.Joins;
        }
        else
        {
            // Append as CROSS JOIN LATERAL so the SRF's arguments may reference
            // columns from the existing FROM/JOIN sources.
            newFrom = stmt.From;
            JoinClause srfJoin = new(JoinType.Cross, srfSource, OnCondition: null, IsLateral: true);
            newJoins = stmt.Joins is null
                ? new List<JoinClause> { srfJoin }
                : stmt.Joins.Concat(new[] { srfJoin }).ToList();
        }

        // Collect the user-original source aliases — used to expand bare `SELECT *`
        // into per-source `t.*` so the synthesized SRF source stays invisible to
        // wildcard expansion. Done BEFORE adding the SRF join: `stmt.From` /
        // `stmt.Joins` still describe the user's real sources here.
        List<string>? originalAliases = null;
        if (stmt.From is not null)
        {
            originalAliases = [];
            if (SourceAliases.GetSourceAlias(stmt.From.Source) is string fromAlias)
            {
                originalAliases.Add(fromAlias);
            }
            if (stmt.Joins is not null)
            {
                foreach (JoinClause j in stmt.Joins)
                {
                    if (SourceAliases.GetSourceAlias(j.Source) is string joinAlias)
                    {
                        originalAliases.Add(joinAlias);
                    }
                }
            }
        }

        // Rebuild the projection: expand any `SELECT *` into per-original-source
        // table-wildcards (so the synthesized SRF join doesn't pull its rows into
        // `*`), and replace the SRF call site with a wildcard over the synthesized
        // alias. ProjectOperator strips the `alias.` prefix from QualifyOutput:false
        // table-wildcards, matching the unqualified output PG produces for SRFs
        // in projection.
        List<SelectColumn> newColumns = new(stmt.Columns.Count);
        for (int i = 0; i < stmt.Columns.Count; i++)
        {
            if (i == srfIndex)
            {
                newColumns.Add(new SelectTableColumns(SyntheticAlias, srfCall.Span));
                continue;
            }

            SelectColumn col = stmt.Columns[i];
            if (col is SelectAllColumns selectAll && originalAliases is { Count: > 0 })
            {
                foreach (string alias in originalAliases)
                {
                    newColumns.Add(new SelectTableColumns(
                        alias,
                        ExcludedColumns: selectAll.ExcludedColumns,
                        ReplacedColumns: selectAll.ReplacedColumns,
                        QualifyOutput: false));
                }
                continue;
            }

            newColumns.Add(col);
        }

        return stmt with
        {
            Columns = newColumns,
            From = newFrom,
            Joins = newJoins,
        };
    }

    private static TableSource RewriteTableSource(TableSource source, FunctionRegistry functions) =>
        source switch
        {
            SubquerySource sub => new SubquerySource(RewriteSelect(sub.Query, functions), sub.Alias),
            // FunctionSource arguments may themselves contain expression subqueries
            // (e.g. unnest((SELECT array_agg(x) FROM t))) — descend into them.
            FunctionSource fn => fn with
            {
                Arguments = fn.Arguments.Select(a => RewriteExpressionSubqueries(a, functions)).ToList(),
            },
            _ => source,
        };

    private static JoinClause RewriteJoin(JoinClause join, FunctionRegistry functions) =>
        new(join.Type,
            RewriteTableSource(join.Source, functions),
            join.OnCondition is { } cond ? RewriteExpressionSubqueries(cond, functions) : null,
            join.IsLateral);

    /// <summary>
    /// Recursively rebuilds <paramref name="expression"/> so that any embedded
    /// SELECT statements (subquery / EXISTS / IN-subquery) are themselves
    /// rewritten. Does not touch outer-level expression nodes; the rewriter's
    /// job at this layer is solely to descend into nested SELECTs.
    /// </summary>
    private static Expression RewriteExpressionSubqueries(Expression expression, FunctionRegistry functions) =>
        expression switch
        {
            FunctionCallExpression f => f with
            {
                Arguments = f.Arguments.Select(a => RewriteExpressionSubqueries(a, functions)).ToList(),
            },
            BinaryExpression b => new BinaryExpression(
                RewriteExpressionSubqueries(b.Left, functions),
                b.Operator,
                RewriteExpressionSubqueries(b.Right, functions)),
            UnaryExpression u => new UnaryExpression(u.Operator, RewriteExpressionSubqueries(u.Operand, functions)),
            CastExpression c => c with { Expression = RewriteExpressionSubqueries(c.Expression, functions) },
            CaseExpression ce => new CaseExpression(
                ce.Operand is { } o ? RewriteExpressionSubqueries(o, functions) : null,
                ce.WhenClauses.Select(w => new WhenClause(
                    RewriteExpressionSubqueries(w.Condition, functions),
                    RewriteExpressionSubqueries(w.Result, functions))).ToList(),
                ce.ElseResult is { } er ? RewriteExpressionSubqueries(er, functions) : null,
                ce.Span),
            InExpression ie => new InExpression(
                RewriteExpressionSubqueries(ie.Expression, functions),
                ie.Values.Select(v => RewriteExpressionSubqueries(v, functions)).ToList(),
                ie.Negated),
            BetweenExpression be => new BetweenExpression(
                RewriteExpressionSubqueries(be.Expression, functions),
                RewriteExpressionSubqueries(be.Low, functions),
                RewriteExpressionSubqueries(be.High, functions),
                be.Negated),
            IsNullExpression isn => new IsNullExpression(
                RewriteExpressionSubqueries(isn.Expression, functions), isn.Negated),
            LikeExpression lk => new LikeExpression(
                RewriteExpressionSubqueries(lk.Expression, functions),
                RewriteExpressionSubqueries(lk.Pattern, functions),
                RewriteExpressionSubqueries(lk.EscapeCharacter, functions),
                lk.CaseInsensitive),
            AtTimeZoneExpression atz => atz with
            {
                Expression = RewriteExpressionSubqueries(atz.Expression, functions),
                TimeZone = RewriteExpressionSubqueries(atz.TimeZone, functions),
            },
            IndexAccessExpression ix => ix with
            {
                Source = RewriteExpressionSubqueries(ix.Source, functions),
                Indices = ix.Indices.Select(i => RewriteExpressionSubqueries(i, functions)).ToArray(),
            },
            StructLiteralExpression sl => sl with
            {
                Fields = sl.Fields.Select(f => new StructField(f.Name, RewriteExpressionSubqueries(f.Value, functions))).ToList(),
            },
            SubqueryExpression sq => new SubqueryExpression(RewriteSelect(sq.Query, functions)),
            InSubqueryExpression isq => new InSubqueryExpression(
                RewriteExpressionSubqueries(isq.Expression, functions),
                RewriteSelect(isq.Query, functions),
                isq.Negated),
            ExistsExpression ex => new ExistsExpression(RewriteSelect(ex.Query, functions), ex.Negated),
            _ => expression,
        };

    private static void RejectTvfInClause(Expression? expression, string clauseName, FunctionRegistry functions)
    {
        if (expression is null) return;
        string? name = FindFirstTvfName(expression, functions);
        if (name is null) return;
        throw new InvalidOperationException(
            $"Set-returning function '{name}' is not allowed in {clauseName}. "
            + $"Move it into FROM as `FROM {name}(...)` or wrap it in a subquery.");
    }

    /// <summary>
    /// Walks <paramref name="expression"/> and returns the call name of the
    /// first <see cref="FunctionCallExpression"/> that resolves as a
    /// table-valued function, or <see langword="null"/> if none is found.
    /// Does not descend through subquery boundaries — subqueries are
    /// independent <see cref="SelectStatement"/>s with their own rewrite pass.
    /// </summary>
    private static string? FindFirstTvfName(Expression expression, FunctionRegistry functions)
    {
        switch (expression)
        {
            case FunctionCallExpression fn:
                if (functions.TryGetTableValued(fn.CallName) is not null) return fn.CallName;
                foreach (Expression arg in fn.Arguments)
                {
                    string? r = FindFirstTvfName(arg, functions);
                    if (r is not null) return r;
                }
                return null;
            case BinaryExpression b:
                return FindFirstTvfName(b.Left, functions) ?? FindFirstTvfName(b.Right, functions);
            case UnaryExpression u:
                return FindFirstTvfName(u.Operand, functions);
            case CastExpression c:
                return FindFirstTvfName(c.Expression, functions);
            case CaseExpression ce:
                if (ce.Operand is not null && FindFirstTvfName(ce.Operand, functions) is { } op) return op;
                foreach (WhenClause w in ce.WhenClauses)
                {
                    if (FindFirstTvfName(w.Condition, functions) is { } wc) return wc;
                    if (FindFirstTvfName(w.Result, functions) is { } wr) return wr;
                }
                if (ce.ElseResult is not null) return FindFirstTvfName(ce.ElseResult, functions);
                return null;
            case InExpression ie:
                if (FindFirstTvfName(ie.Expression, functions) is { } iex) return iex;
                foreach (Expression v in ie.Values)
                {
                    if (FindFirstTvfName(v, functions) is { } vr) return vr;
                }
                return null;
            case BetweenExpression be:
                return FindFirstTvfName(be.Expression, functions)
                    ?? FindFirstTvfName(be.Low, functions)
                    ?? FindFirstTvfName(be.High, functions);
            case IsNullExpression isn:
                return FindFirstTvfName(isn.Expression, functions);
            case LikeExpression lk:
                return FindFirstTvfName(lk.Expression, functions)
                    ?? FindFirstTvfName(lk.Pattern, functions)
                    ?? FindFirstTvfName(lk.EscapeCharacter, functions);
            case AtTimeZoneExpression atz:
                return FindFirstTvfName(atz.Expression, functions)
                    ?? FindFirstTvfName(atz.TimeZone, functions);
            case IndexAccessExpression ix:
                if (FindFirstTvfName(ix.Source, functions) is { } ixs) return ixs;
                foreach (Expression i in ix.Indices)
                {
                    if (FindFirstTvfName(i, functions) is { } ir) return ir;
                }
                return null;
            case StructLiteralExpression sl:
                foreach (StructField f in sl.Fields)
                {
                    if (FindFirstTvfName(f.Value, functions) is { } fr) return fr;
                }
                return null;
            default:
                return null;
        }
    }
}
