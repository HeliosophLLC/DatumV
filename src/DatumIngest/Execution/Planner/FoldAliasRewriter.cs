using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Planner;

/// <summary>
/// Rewrites CROSS VALIDATE fold-alias references in SELECT columns and GROUP BY
/// expressions so they resolve against the synthetic group-by column the planner
/// emits, rather than the LET binding the fold-alias originally pointed at.
/// </summary>
/// <remarks>
/// Currently unused by the planner — kept here against the day the CROSS VALIDATE
/// pipeline regrows a code path that needs them. Both helpers are pure transforms
/// over AST shapes, so they have no other dependencies that would drift.
/// </remarks>
internal static class FoldAliasRewriter
{
    /// <summary>
    /// Rewrites SELECT column expressions that reference the CROSS VALIDATE fold
    /// alias with the GroupByOperator's formatted output column name. This ensures
    /// the projection resolves against the grouped row rather than through the LET
    /// binding.
    /// </summary>
    public static IReadOnlyList<SelectColumn> RewriteFoldAliasInColumns(
        IReadOnlyList<SelectColumn> columns, string foldAlias, string groupByColumnName)
    {
        List<SelectColumn> result = new(columns.Count);
        foreach (SelectColumn column in columns)
        {
            if (column.Expression is ColumnReference { TableName: null } col
                && col.ColumnName.Equals(foldAlias, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new SelectColumn(
                    new ColumnReference(null, groupByColumnName), column.Alias ?? foldAlias));
            }
            else
            {
                result.Add(column);
            }
        }

        return result;
    }

    /// <summary>
    /// Rewrites GROUP BY expressions that reference the CROSS VALIDATE fold alias
    /// with the fold expression. Necessary because GROUP BY runs before LET
    /// evaluation, so the fold alias doesn't exist as a row column yet at that stage.
    /// </summary>
    public static GroupByClause RewriteFoldAliasInGroupBy(
        GroupByClause groupBy, string foldAlias, Expression foldExpression)
    {
        bool anyRewritten = false;
        List<Expression> rewritten = new(groupBy.Expressions.Count);

        foreach (Expression expr in groupBy.Expressions)
        {
            if (expr is ColumnReference { TableName: null } col
                && col.ColumnName.Equals(foldAlias, StringComparison.OrdinalIgnoreCase))
            {
                rewritten.Add(foldExpression);
                anyRewritten = true;
            }
            else
            {
                rewritten.Add(expr);
            }
        }

        return anyRewritten ? new GroupByClause(rewritten, groupBy.IsAll) : groupBy;
    }
}
