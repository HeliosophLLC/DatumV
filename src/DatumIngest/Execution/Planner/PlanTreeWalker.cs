using DatumIngest.Execution.Operators;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Planner;

/// <summary>
/// Stateless utilities for walking and rewriting the operator-tree shape produced by
/// <see cref="QueryPlanner"/>. Exposed to the planner so optimization passes
/// (sort-elimination via index scan, filter-hint propagation) can locate the leaf
/// <see cref="ScanOperator"/> of a probe chain without re-implementing the traversal.
/// </summary>
/// <remarks>
/// The walker recognises the wrapper shapes the planner emits — <see cref="AliasOperator"/>,
/// <see cref="FilterOperator"/>, <see cref="ProjectOperator"/>,
/// <see cref="DistinctOperator"/>, and the probe (left) side of
/// <see cref="JoinOperator"/> / <see cref="MergeJoinOperator"/>. Operators not in this
/// list end the walk because the planner never emits a sortable / hintable scan beneath
/// them today.
/// </remarks>
internal static class PlanTreeWalker
{
    /// <summary>
    /// Finds the <see cref="ScanOperator"/> at the outermost probe position. Walks through
    /// alias / filter / project / distinct wrappers and the left (probe) side of join nodes.
    /// Returns <see langword="null"/> if no scan is reachable via this path.
    /// </summary>
    public static ScanOperator? FindScanOperator(QueryOperator operatorNode)
    {
        QueryOperator current = operatorNode;

        while (true)
        {
            switch (current)
            {
                case ScanOperator scan:
                    return scan;
                case AliasOperator alias:
                    current = alias.Source;
                    break;
                case FilterOperator filter:
                    current = filter.Source;
                    break;
                case ProjectOperator project:
                    current = project.Source;
                    break;
                case DistinctOperator distinct:
                    current = distinct.Source;
                    break;
                case JoinOperator join:
                    // Follow the probe (left) side of the join to find the outermost
                    // driving scan. This enables sort elimination when the ORDER BY table
                    // has been placed as the outermost probe by join reordering.
                    current = join.Left;
                    break;
                case MergeJoinOperator merge:
                    // Follow the left side of the merge join — the left input preserves
                    // its sorted order, so the outermost scan is reachable.
                    current = merge.Left;
                    break;
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// Finds the <see cref="ScanOperator"/> in a simple operator chain without crossing
    /// into join subtrees. Used when inspecting a single planned table operator (always
    /// a chain, never a join).
    /// </summary>
    public static ScanOperator? FindScanOperatorInChain(QueryOperator operatorNode)
    {
        QueryOperator current = operatorNode;

        while (true)
        {
            switch (current)
            {
                case ScanOperator scan:
                    return scan;
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

    /// <summary>
    /// Walks down a simple scan chain (alias / filter wrappers) and adds
    /// <paramref name="predicate"/> as an advisory filter hint to the underlying
    /// <see cref="ScanOperator"/>. Filterable providers may use the hint plus
    /// statistics to skip partitions at scan time. Non-scan-chain shapes (joins,
    /// projects, etc.) terminate the walk silently because filter hints are only
    /// meaningful on the leaf scan.
    /// </summary>
    public static void AddFilterHintToScan(QueryOperator operatorNode, Expression predicate)
    {
        QueryOperator current = operatorNode;
        while (true)
        {
            switch (current)
            {
                case ScanOperator scan:
                    scan.AddFilterHint(predicate);
                    return;
                case AliasOperator alias:
                    current = alias.Source;
                    break;
                case FilterOperator filter:
                    current = filter.Source;
                    break;
                default:
                    return; // Not a simple scan chain — cannot push hints.
            }
        }
    }

    /// <summary>
    /// Replaces <paramref name="target"/> in the operator tree with
    /// <paramref name="replacement"/>, preserving the wrapping operators
    /// (alias / filter / project / distinct / join). For <see cref="JoinOperator"/>
    /// nodes the left (probe) chain is searched recursively; the right (build) side is
    /// never modified. <see cref="MergeJoinOperator"/> recurses both sides.
    /// </summary>
    public static QueryOperator ReplaceScanOperator(
        QueryOperator root, ScanOperator target, IndexScanOperator replacement)
    {
        if (ReferenceEquals(root, target))
        {
            return replacement;
        }

        return root switch
        {
            AliasOperator alias => new AliasOperator(
                ReplaceScanOperator(alias.Source, target, replacement), alias.Alias),
            FilterOperator filter => new FilterOperator(
                ReplaceScanOperator(filter.Source, target, replacement), filter.Predicate),
            ProjectOperator project => new ProjectOperator(
                ReplaceScanOperator(project.Source, target, replacement),
                project.Columns, project.LetBindings),
            DistinctOperator distinct => new DistinctOperator(
                ReplaceScanOperator(distinct.Source, target, replacement)),
            JoinOperator join => new JoinOperator(
                ReplaceScanOperator(join.Left, target, replacement),
                join.Right,
                join.Type,
                join.OnCondition,
                join.NullSensitiveAntiSemi,
                join.Flipped),
            MergeJoinOperator merge => new MergeJoinOperator(
                ReplaceScanOperator(merge.Left, target, replacement),
                ReplaceScanOperator(merge.Right, target, replacement),
                merge.Type,
                merge.Extraction,
                merge.LeftSortColumn,
                merge.RightSortColumn),
            _ => root,
        };
    }
}
