using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution.Planner;

/// <summary>
/// Replaces occurrences of a GROUP BY key expression with a
/// <see cref="ColumnReference"/> to the <see cref="Operators.GroupByOperator"/>
/// output column that carries it. After aggregation the source columns inside a
/// grouping expression no longer exist, so a SELECT / HAVING / ORDER BY item that
/// repeats the expression (e.g. <c>SELECT upper(a) … GROUP BY upper(a)</c>) must
/// read the precomputed key instead of re-evaluating it.
/// </summary>
/// <remarks>
/// A grouping key's output column is named by its formatted form
/// (<see cref="QueryExplainer.FormatExpression"/>), the same naming the
/// GroupByOperator uses. Matching is by that formatted form, so a bare-column key
/// rewrites to an identically-named reference (a harmless no-op) while an
/// expression key rewrites to its group-output column. Runs after
/// <see cref="AggregateRewriter"/>, so aggregate calls are already lifted out and
/// never matched here.
/// </remarks>
internal static class GroupKeyProjectionRewriter
{
    /// <summary>
    /// Builds the set of grouping-key output-column names (their formatted forms)
    /// used to match repeats. Returns <see langword="null"/> when there are no
    /// grouping keys, so callers can skip the pass entirely.
    /// </summary>
    public static IReadOnlySet<string>? BuildKeyNames(IReadOnlyList<Expression> groupByExpressions)
    {
        if (groupByExpressions.Count == 0)
        {
            return null;
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (Expression key in groupByExpressions)
        {
            names.Add(QueryExplainer.FormatExpression(key));
        }

        return names;
    }

    /// <summary>
    /// Rewrites <paramref name="expression"/>, replacing any sub-expression whose
    /// formatted form matches a grouping key with a reference to that key's output
    /// column. Composite shapes are walked top-down so a whole-expression key is
    /// matched before its parts.
    /// </summary>
    public static Expression Rewrite(Expression expression, IReadOnlySet<string> groupKeyNames)
    {
        string formatted = QueryExplainer.FormatExpression(expression);
        if (groupKeyNames.Contains(formatted))
        {
            return new ColumnReference(null, formatted);
        }

        return expression switch
        {
            FunctionCallExpression func => RewriteFunctionArguments(func, groupKeyNames),
            BinaryExpression bin => new BinaryExpression(
                Rewrite(bin.Left, groupKeyNames),
                bin.Operator,
                Rewrite(bin.Right, groupKeyNames)),
            UnaryExpression unary => new UnaryExpression(
                unary.Operator,
                Rewrite(unary.Operand, groupKeyNames)),
            CastExpression cast => new CastExpression(
                Rewrite(cast.Expression, groupKeyNames),
                cast.TargetType),
            CaseExpression caseExpr => RewriteCase(caseExpr, groupKeyNames),
            _ => expression,
        };
    }

    private static Expression RewriteFunctionArguments(
        FunctionCallExpression func, IReadOnlySet<string> groupKeyNames)
    {
        bool changed = false;
        Expression[] rewrittenArgs = new Expression[func.Arguments.Count];
        for (int i = 0; i < func.Arguments.Count; i++)
        {
            rewrittenArgs[i] = Rewrite(func.Arguments[i], groupKeyNames);
            if (!ReferenceEquals(rewrittenArgs[i], func.Arguments[i]))
            {
                changed = true;
            }
        }

        return changed
            ? new FunctionCallExpression(
                func.FunctionName, rewrittenArgs, func.OrderBy, func.Distinct, func.Span, func.WithinGroupOrderBy)
            : func;
    }

    /// <summary>
    /// Adds passthrough projection columns for grouping-key output columns that
    /// ORDER BY references but the SELECT list doesn't emit, so the key survives
    /// until the sort runs. Mirrors
    /// <see cref="AggregateRewriter.AppendOrderByAggregatePassthroughs"/>; the
    /// recorded names are trimmed by a follow-up <see cref="Operators.ProjectOperator"/>
    /// after sorting. Skipped for wildcard projections, which already emit every
    /// GroupBy output column.
    /// </summary>
    public static void AppendOrderByGroupKeyPassthroughs(
        IReadOnlyList<OrderByItem> rewrittenOrderByItems,
        IReadOnlySet<string> groupKeyNames,
        List<SelectColumn> rewrittenColumns,
        ref List<string>? passthroughs)
    {
        foreach (SelectColumn column in rewrittenColumns)
        {
            if (column is SelectAllColumns or SelectTableColumns) return;
        }

        HashSet<string> projectionOutputNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (SelectColumn column in rewrittenColumns)
        {
            projectionOutputNames.Add(column.Alias ?? ColumnNameResolver.GetRawName(column.Expression));
        }

        foreach (OrderByItem item in rewrittenOrderByItems)
        {
            foreach ((string? _, string columnName) in ColumnReferenceCollector.Collect(item.Expression))
            {
                if (!groupKeyNames.Contains(columnName)) continue;
                if (projectionOutputNames.Contains(columnName)) continue;

                passthroughs ??= new List<string>();
                if (passthroughs.Contains(columnName, StringComparer.OrdinalIgnoreCase)) continue;

                passthroughs.Add(columnName);
                projectionOutputNames.Add(columnName);
                rewrittenColumns.Add(new SelectColumn(new ColumnReference(null, columnName), columnName));
            }
        }
    }

    private static CaseExpression RewriteCase(CaseExpression caseExpression, IReadOnlySet<string> groupKeyNames)
    {
        Expression? rewrittenOperand = caseExpression.Operand is not null
            ? Rewrite(caseExpression.Operand, groupKeyNames)
            : null;

        List<WhenClause> rewrittenClauses = new(caseExpression.WhenClauses.Count);
        foreach (WhenClause whenClause in caseExpression.WhenClauses)
        {
            rewrittenClauses.Add(new WhenClause(
                Rewrite(whenClause.Condition, groupKeyNames),
                Rewrite(whenClause.Result, groupKeyNames)));
        }

        Expression? rewrittenElse = caseExpression.ElseResult is not null
            ? Rewrite(caseExpression.ElseResult, groupKeyNames)
            : null;

        return new CaseExpression(rewrittenOperand, rewrittenClauses, rewrittenElse, caseExpression.Span);
    }
}
