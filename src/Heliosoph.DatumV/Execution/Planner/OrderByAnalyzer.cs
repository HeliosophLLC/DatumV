using Heliosoph.DatumV.Execution.Operators;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution.Planner;

/// <summary>
/// ORDER BY-related analyses used by <see cref="QueryPlanner"/>:
/// detecting whether a single-column ORDER BY can be served by an indexed sort
/// (used to protect the relevant table during join reordering), and validating
/// that <c>SELECT DISTINCT</c> + <c>ORDER BY</c> combinations name only columns
/// the projection actually exposes.
/// </summary>
internal static class OrderByAnalyzer
{
    /// <summary>
    /// Returns the qualified table alias from a single-column ORDER BY if the
    /// referenced table has a sorted column index on that sort column. The result
    /// is passed to the join reorderer so the relevant table is protected as the
    /// outermost probe, enabling sort elimination via the planner's index-scan
    /// rewrite. Returns <see langword="null"/> when the ORDER BY is multi-column,
    /// unqualified, or the referenced table lacks the required sorted column index.
    /// </summary>
    public static string? GetOrderBySortTableAlias(
        OrderByClause? orderBy,
        QueryOperator fromOperator,
        HashSet<string> fromAliases,
        List<(JoinClause Join, QueryOperator Operator, HashSet<string> Aliases)> plannedJoins)
    {
        if (orderBy is null || orderBy.Items.Count != 1)
        {
            return null;
        }

        if (orderBy.Items[0].Expression is not ColumnReference { TableName: string tableName, ColumnName: string columnName })
        {
            return null;
        }

        // Locate the operator for the ORDER BY table alias.
        QueryOperator? tableOperator = null;

        if (fromAliases.Contains(tableName))
        {
            tableOperator = fromOperator;
        }
        else
        {
            foreach ((_, QueryOperator op, HashSet<string> aliases) in plannedJoins)
            {
                if (aliases.Contains(tableName))
                {
                    tableOperator = op;
                    break;
                }
            }
        }

        if (tableOperator is null)
        {
            return null;
        }

        // Verify the table has a sorted column index on the ORDER BY column. Use
        // the simple chain-only scan finder (no join traversal) since each
        // individual table operator is a scan chain, not a join tree.
        ScanOperator? scan = PlanTreeWalker.FindScanOperatorInChain(tableOperator);

        if (scan is null)
        {
            return null;
        }

        return scan.TableProvider.TryGetColumnIndex(columnName, out _) ? tableName : null;
    }

    /// <summary>
    /// Validates that every ORDER BY expression appears in the SELECT list when
    /// <c>SELECT DISTINCT</c> is active. If an ORDER BY column is not projected,
    /// the result would be ambiguous because DISTINCT collapses rows before
    /// sorting. Wildcard projections trivially satisfy the rule.
    /// </summary>
    public static void ValidateDistinctOrderBy(
        IReadOnlyList<SelectColumn> selectColumns,
        OrderByClause orderBy)
    {
        // Collect the effective output names from the SELECT list.
        HashSet<string> selectedNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (SelectColumn column in selectColumns)
        {
            if (column is SelectAllColumns or SelectTableColumns)
            {
                // SELECT * projects everything — any ORDER BY is valid.
                return;
            }

            if (column.Alias is not null)
            {
                selectedNames.Add(column.Alias);
            }

            // Also accept the raw expression form (e.g. "t.name") so that
            // ORDER BY t.name matches SELECT t.name even without an alias.
            selectedNames.Add(QueryExplainer.FormatExpression(column.Expression));
        }

        foreach (OrderByItem item in orderBy.Items)
        {
            string orderExpression = QueryExplainer.FormatExpression(item.Expression);
            if (!selectedNames.Contains(orderExpression))
            {
                throw new InvalidOperationException(
                    $"ORDER BY expression '{orderExpression}' must appear in the SELECT list " +
                    $"when SELECT DISTINCT is specified.");
            }
        }
    }
}
