using DatumIngest.Execution.Operators;
using DatumIngest.Indexing;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Planner;

/// <summary>
/// Sort-elimination and streaming-aggregation analysis. Used by
/// <see cref="QueryPlanner"/> to decide whether an explicit
/// <see cref="OrderByOperator"/> can be elided (because the source already
/// produces the requested order) and whether a hash <see cref="GroupByOperator"/>
/// can be replaced with its streaming variant (because the source's output is
/// already sorted on the GROUP BY keys).
/// </summary>
internal static class SortAggregateAnalyzer
{
    /// <summary>
    /// Inspects the operator tree to determine the output sort ordering, if any.
    /// Walks through transparent wrappers (<see cref="AliasOperator"/>,
    /// <see cref="FilterOperator"/>, <see cref="ProjectOperator"/>) that preserve
    /// row order and extracts ordering from operators that produce sorted output
    /// (<see cref="IndexScanOperator"/>, <see cref="MergeJoinOperator"/>, a
    /// streaming-sorted <see cref="GroupByOperator"/>, a
    /// <see cref="JoinOperator"/> in index-nested-loop mode, and any
    /// <see cref="OrderByOperator"/>). Returns <see langword="null"/> when the
    /// ordering is unknown or destroyed by a blocking operator.
    /// </summary>
    public static IReadOnlyList<(string ColumnName, bool Descending)>? GetOutputOrdering(
        QueryOperator operatorNode)
    {
        QueryOperator current = operatorNode;
        while (true)
        {
            switch (current)
            {
                case IndexScanOperator indexScan:
                    return [(indexScan.ColumnName, indexScan.Descending)];
                case MergeJoinOperator mergeJoin:
                    // Merge join preserves left-side ordering.
                    current = mergeJoin.Left;
                    break;
                case JoinOperator { PreferIndexNestedLoop: true } indexNlj:
                    // Index nested-loop join streams probe rows in left-side order,
                    // so the output ordering is inherited from the probe (left) side.
                    current = indexNlj.Left;
                    break;
                case GroupByOperator { StreamingSorted: true } groupBy:
                {
                    // Streaming GROUP BY emits groups in key order, inheriting the sort
                    // direction from the source. Build the output ordering from the
                    // GROUP BY key expressions matched against the source ordering.
                    IReadOnlyList<(string ColumnName, bool Descending)>? sourceOrdering =
                        GetOutputOrdering(groupBy.Source);

                    if (sourceOrdering is null)
                    {
                        return null;
                    }

                    List<(string, bool)> result = new(groupBy.GroupByExpressions.Count);

                    for (int index = 0; index < groupBy.GroupByExpressions.Count; index++)
                    {
                        if (groupBy.GroupByExpressions[index] is not ColumnReference column)
                        {
                            return null;
                        }

                        // Find the direction from the source ordering for this column.
                        bool found = false;
                        for (int orderIndex = 0; orderIndex < sourceOrdering.Count; orderIndex++)
                        {
                            if (string.Equals(column.ColumnName, sourceOrdering[orderIndex].ColumnName,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                result.Add((column.ColumnName, sourceOrdering[orderIndex].Descending));
                                found = true;
                                break;
                            }
                        }

                        if (!found)
                        {
                            return null;
                        }
                    }

                    return result;
                }
                case AliasOperator alias:
                    current = alias.Source;
                    break;
                case FilterOperator filter:
                    current = filter.Source;
                    break;
                case ProjectOperator project:
                    current = project.Source;
                    break;
                case OrderByOperator orderBy:
                {
                    // An ORDER BY operator establishes a known output ordering from its
                    // sort criteria, provided every item is a simple ColumnReference.
                    List<(string, bool)> ordering = new(orderBy.OrderByItems.Count);
                    foreach (OrderByItem item in orderBy.OrderByItems)
                    {
                        if (item.Expression is not ColumnReference col)
                        {
                            return null;
                        }

                        ordering.Add((col.ColumnName, item.Direction == SortDirection.Descending));
                    }

                    return ordering;
                }
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when the source operator's output ordering
    /// already satisfies every item in the <paramref name="orderBy"/> clause,
    /// making a separate <see cref="OrderByOperator"/> unnecessary.
    /// </summary>
    public static bool OutputOrderingSatisfiesOrderBy(
        QueryOperator source,
        OrderByClause orderBy)
    {
        IReadOnlyList<(string ColumnName, bool Descending)>? ordering = GetOutputOrdering(source);

        if (ordering is null || ordering.Count < orderBy.Items.Count)
        {
            return false;
        }

        for (int index = 0; index < orderBy.Items.Count; index++)
        {
            OrderByItem item = orderBy.Items[index];

            if (item.Expression is not ColumnReference columnReference)
            {
                return false;
            }

            if (!string.Equals(columnReference.ColumnName, ordering[index].ColumnName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            bool descending = item.Direction == SortDirection.Descending;

            if (descending != ordering[index].Descending)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks whether the GROUP BY key expressions match the output ordering of
    /// the source operator, enabling streaming aggregation. All GROUP BY
    /// expressions must be simple <see cref="ColumnReference"/> nodes whose
    /// column names match the ordering columns in the same sequence.
    /// </summary>
    public static bool CanUseStreamingAggregate(
        QueryOperator source,
        IReadOnlyList<Expression> groupByExpressions)
    {
        if (groupByExpressions.Count == 0)
        {
            // Global aggregation (no GROUP BY) always produces one group — streaming not applicable.
            return false;
        }

        IReadOnlyList<(string ColumnName, bool Descending)>? ordering = GetOutputOrdering(source);

        if (ordering is null || ordering.Count == 0)
        {
            return false;
        }

        // The ordering must cover at least the first GROUP BY key. For full streaming,
        // all GROUP BY keys must match the ordering prefix.
        if (ordering.Count < groupByExpressions.Count)
        {
            // Source produces fewer sorted columns than GROUP BY keys — the remaining
            // keys could interleave within a single sort-key partition. Only safe when
            // all GROUP BY keys are covered.
            return false;
        }

        for (int index = 0; index < groupByExpressions.Count; index++)
        {
            if (groupByExpressions[index] is not ColumnReference columnReference)
            {
                return false;
            }

            if (!string.Equals(columnReference.ColumnName, ordering[index].ColumnName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks whether each GROUP BY key expression is a simple
    /// <see cref="ColumnReference"/> whose unqualified column name matches the
    /// corresponding ORDER BY item at the same position. The ORDER BY clause may
    /// contain more items than the GROUP BY list; the check is a prefix match on
    /// the first <c>groupByExpressions.Count</c> ORDER BY items.
    /// </summary>
    /// <remarks>
    /// Gates sort injection: when this returns <see langword="true"/>, an
    /// <see cref="OrderByOperator"/> keyed on the GROUP BY columns (in ORDER BY
    /// directions) can be placed before the <see cref="GroupByOperator"/>,
    /// enabling streaming aggregation and allowing the downstream ORDER BY to be elided.
    /// </remarks>
    public static bool GroupByKeysMatchOrderByPrefix(
        IReadOnlyList<Expression> groupByExpressions,
        OrderByClause orderBy)
    {
        if (groupByExpressions.Count == 0)
        {
            return false;
        }

        if (orderBy.Items.Count < groupByExpressions.Count)
        {
            return false;
        }

        for (int index = 0; index < groupByExpressions.Count; index++)
        {
            if (groupByExpressions[index] is not ColumnReference groupColumn)
            {
                return false;
            }

            if (orderBy.Items[index].Expression is not ColumnReference orderColumn)
            {
                return false;
            }

            if (!string.Equals(groupColumn.ColumnName, orderColumn.ColumnName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Builds the <see cref="OrderByItem"/> list for the sort injected before a
    /// <see cref="GroupByOperator"/>. One item is emitted per GROUP BY expression,
    /// using the sort direction from the corresponding ORDER BY item at the same
    /// index (ascending when ORDER BY is shorter).
    /// </summary>
    public static IReadOnlyList<OrderByItem> BuildGroupBySortItems(
        IReadOnlyList<Expression> groupByExpressions,
        OrderByClause orderBy)
    {
        List<OrderByItem> items = new(groupByExpressions.Count);

        for (int index = 0; index < groupByExpressions.Count; index++)
        {
            SortDirection direction = index < orderBy.Items.Count
                ? orderBy.Items[index].Direction
                : SortDirection.Ascending;

            items.Add(new OrderByItem(groupByExpressions[index], direction));
        }

        return items;
    }

    /// <summary>
    /// Attempts to replace the <see cref="ScanOperator"/> in the operator tree
    /// with an <see cref="IndexScanOperator"/> when the ORDER BY clause is a
    /// single column reference covered by a sorted value index and the provider
    /// supports seeking. Mutates <paramref name="source"/> in place when the
    /// substitution succeeds.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if the index scan was substituted and the ORDER BY
    /// can be elided.
    /// </returns>
    public static bool TryReplaceWithIndexScan(ref QueryOperator source, OrderByClause orderBy)
    {
        // Only single-column, simple column reference ORDER BY is eligible.
        if (orderBy.Items.Count != 1)
        {
            return false;
        }

        OrderByItem item = orderBy.Items[0];

        if (item.Expression is not ColumnReference columnRef)
        {
            return false;
        }

        string sortColumn = columnRef.ColumnName;

        // Walk the operator tree to find the ScanOperator.
        ScanOperator? scan = PlanTreeWalker.FindScanOperator(source);

        if (scan is null)
        {
            return false;
        }

        // The scan must have a source index with a column index for the sort column.
        if (scan.SourceIndex is null)
        {
            return false;
        }

        if (!scan.TableProvider.TryGetColumnIndex(sortColumn, out IColumnIndex? columnIndex))
        {
            return false;
        }

        bool descending = item.Direction == SortDirection.Descending;

        IndexScanOperator indexScan = new(
            scan.TableProvider,
            scan.RequiredColumns,
            columnIndex,
            scan.SourceIndex.Chunks,
            descending,
            sortColumn);

        // Replace the ScanOperator in the tree with the IndexScanOperator.
        source = PlanTreeWalker.ReplaceScanOperator(source, scan, indexScan);
        return true;
    }
}
