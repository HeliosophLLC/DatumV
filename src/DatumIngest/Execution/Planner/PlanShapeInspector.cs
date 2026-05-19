using Heliosoph.DatumV.Execution.Operators;

namespace Heliosoph.DatumV.Execution.Planner;

/// <summary>
/// Cheap plan-tree introspection helpers used by <see cref="QueryPlanner"/> for
/// EXPLAIN-style trace output and cost-based decisions (e.g. join reordering by
/// row-count estimates). Both helpers walk through the transparent wrappers
/// (<see cref="AliasOperator"/>, <see cref="FilterOperator"/>) the planner emits
/// to reach the underlying <see cref="ScanOperator"/>.
/// </summary>
internal static class PlanShapeInspector
{
    /// <summary>
    /// Walks through wrapping operators (<see cref="AliasOperator"/>,
    /// <see cref="FilterOperator"/>) to find the underlying
    /// <see cref="ScanOperator"/> and returns its table's qualified name. When no
    /// scan is reachable (e.g. a join subtree or compound source), returns the
    /// operator's type name as a fallback label suitable for trace output.
    /// </summary>
    public static string GetOperatorName(QueryOperator op)
    {
        QueryOperator current = op;
        while (true)
        {
            if (current is ScanOperator scan)
                return scan.TableProvider.QualifiedName.ToString();
            if (current is AliasOperator alias)
                current = alias.Source;
            else if (current is FilterOperator filter)
                current = filter.Source;
            else
                return current.GetType().Name;
        }
    }

    /// <summary>
    /// Walks through wrapping operators (<see cref="AliasOperator"/>,
    /// <see cref="FilterOperator"/>) to find the underlying
    /// <see cref="ScanOperator"/> and returns its estimated row count. Returns
    /// <see langword="null"/> when no scan is reachable through the chain.
    /// </summary>
    public static long? GetEstimatedRowCount(QueryOperator operatorNode)
    {
        QueryOperator current = operatorNode;
        while (true)
        {
            switch (current)
            {
                case ScanOperator scan:
                    return scan.TableRowCount;
                case AliasOperator alias:
                    current = alias.Source;
                    break;
                case FilterOperator filter:
                    current = filter.Source;
                    break;
                default:
                    return null;
            }
        }
    }
}
