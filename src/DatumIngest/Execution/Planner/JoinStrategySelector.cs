using System.Diagnostics.CodeAnalysis;
using DatumIngest.Execution.Operators;
using DatumIngest.Indexing;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Planner;

/// <summary>
/// Plan-time strategy selection between hash join, merge join, and index
/// nested-loop join. <see cref="QueryPlanner"/> consults these predicates per
/// join node to choose the executor that best matches the query's downstream
/// shape (ORDER BY / GROUP BY alignment, LIMIT presence) and physical-layout
/// availability (sorted indexes on the join keys).
/// </summary>
internal static class JoinStrategySelector
{
    /// <summary>
    /// Returns <see langword="true"/> when a merge join on the given condition
    /// would produce output whose sort order benefits a downstream operator.
    /// </summary>
    /// <remarks>
    /// Two independent conditions justify merge join:
    /// <list type="bullet">
    /// <item>The leading ORDER BY or GROUP BY column matches the join key so the
    /// merge join's sorted output eliminates a downstream
    /// <see cref="OrderByOperator"/> or enables streaming aggregation.</item>
    /// <item>Either side of the join has more than
    /// <see cref="IndexConstants.BPlusTreeAutoThreshold"/> estimated rows,
    /// meaning a hash table would need to materialise millions of rows — more
    /// expensive than the index-ordered random-access reads used by
    /// <see cref="MergeJoinOperator"/>.</item>
    /// </list>
    /// When neither condition holds, the hash join's sequential scan pattern is
    /// cheaper.
    /// </remarks>
    public static bool ShouldUseMergeJoin(
        SelectStatement statement,
        Expression? onCondition,
        QueryOperator leftOperator,
        QueryOperator rightOperator)
    {
        if (onCondition is null)
        {
            return false;
        }

        JoinKeyExtractionResult? extraction = JoinKeyExtractor.TryExtract(onCondition);

        if (extraction is null
            || extraction.KeyPairs.Count != 1
            || extraction.KeyPairs[0].Left is not ColumnReference leftKey
            || extraction.KeyPairs[0].Right is not ColumnReference rightKey)
        {
            return false;
        }

        // Check whether the leading ORDER BY column matches the join key.
        if (statement.OrderBy is not null
            && statement.OrderBy.Items.Count > 0
            && statement.OrderBy.Items[0].Expression is ColumnReference orderColumn)
        {
            string orderColumnName = orderColumn.ColumnName;

            if (string.Equals(orderColumnName, leftKey.ColumnName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(orderColumnName, rightKey.ColumnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Check whether the leading GROUP BY column matches the join key, enabling
        // streaming aggregation with LIMIT short-circuit.
        if (statement.GroupBy is not null
            && statement.GroupBy.Expressions.Count > 0
            && statement.GroupBy.Expressions[0] is ColumnReference groupColumn)
        {
            string groupColumnName = groupColumn.ColumnName;

            if (string.Equals(groupColumnName, leftKey.ColumnName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(groupColumnName, rightKey.ColumnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Large-build override: when either side exceeds the B+Tree auto-threshold the
        // cost of allocating and probing a hash table outweighs the random-access penalty
        // of index-ordered merge join reads. Merge join is only attempted if
        // TryCreateMergeJoin subsequently confirms that sorted indexes actually exist on
        // both sides.
        long? leftRows = PlanShapeInspector.GetEstimatedRowCount(leftOperator);
        long? rightRows = PlanShapeInspector.GetEstimatedRowCount(rightOperator);

        if ((leftRows > IndexConstants.BPlusTreeAutoThreshold)
            || (rightRows > IndexConstants.BPlusTreeAutoThreshold))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the given join should be executed as
    /// an index nested-loop join at plan time. Three conditions must all hold:
    /// </summary>
    /// <remarks>
    /// <list type="number">
    /// <item>The statement has a LIMIT clause (so only a small top-N result is needed).</item>
    /// <item>The join has a single equi-join key expressed as two simple
    /// <see cref="ColumnReference"/> nodes.</item>
    /// <item>The build side (right operator) is a simple scan chain whose
    /// <see cref="ScanOperator.SourceIndex"/> contains a sorted index on the build
    /// key column — confirming that index point-seeks are available.</item>
    /// </list>
    /// Join types are restricted to <see cref="JoinType.Inner"/> and
    /// <see cref="JoinType.LeftSemi"/>; all others fall back to hash join. The
    /// seekable-provider check is intentionally deferred to runtime inside
    /// <see cref="JoinOperator.TryCreateIndexNestedLoopExecutor"/>; using
    /// <see cref="ScanOperator.SourceIndex"/> presence as the plan-time proxy is
    /// safe because <c>.datum</c> files with an index always expose a seekable
    /// provider.
    /// </remarks>
    public static bool ShouldPreferIndexNestedLoop(
        SelectStatement statement,
        QueryOperator buildSide,
        JoinClause join)
    {
        // NLJ only pays off when LIMIT restricts the result to a small top-N.
        if (statement.Limit is null)
        {
            return false;
        }

        // Blocking operators (GROUP BY, DISTINCT, HAVING) must consume all join output
        // before LIMIT can take effect, so the LIMIT cannot short-circuit the join. In
        // that situation index NLJ degrades to per-row seeks across the entire probe
        // side — catastrophically worse than a hash join.
        if (statement.GroupBy is not null || statement.Distinct || statement.Having is not null)
        {
            return false;
        }

        // Only INNER and LeftSemi joins — must match what IndexNestedLoopJoinExecutor supports.
        if (join.Type is not (JoinType.Inner or JoinType.LeftSemi))
        {
            return false;
        }

        // Single equi-join key with simple column references on both sides.
        JoinKeyExtractionResult? extraction = JoinKeyExtractor.TryExtract(join.OnCondition);

        if (extraction is null
            || extraction.KeyPairs.Count != 1
            || extraction.KeyPairs[0].Right is not ColumnReference buildKeyRef)
        {
            return false;
        }

        // Build side must be a simple scan chain with a source index.
        ScanOperator? buildScan = PlanTreeWalker.FindScanOperatorInChain(buildSide);

        if (buildScan?.SourceIndex is null)
        {
            return false;
        }

        // The build key column must exist in the index.
        string buildKeyColumn = buildKeyRef.QualifiedName ?? buildKeyRef.ColumnName;

        return buildScan.TableProvider.TryGetColumnIndex(buildKeyColumn, out _)
            || buildScan.TableProvider.TryGetColumnIndex(buildKeyRef.ColumnName, out _);
    }

    /// <summary>
    /// Attempts to create a <see cref="MergeJoinOperator"/> for the given join
    /// when both sides have sorted value indexes on their respective equi-join
    /// key columns. Only single-key equi-joins with simple
    /// <see cref="ColumnReference"/> keys are eligible. Both sides must be
    /// simple scan chains (Scan → Alias → Filter) — if the left side is already
    /// a join tree, the hash join output is unordered so merge join cannot apply.
    /// </summary>
    /// <remarks>
    /// When eligible, replaces both <see cref="ScanOperator"/> nodes with
    /// ascending <see cref="IndexScanOperator"/> nodes and returns a
    /// <see cref="MergeJoinOperator"/>. PR13d retired the physically-sorted
    /// column index section, so the path currently returns
    /// <see langword="false"/> unconditionally — the operator infrastructure
    /// stays for future use over a sorted physical layout.
    /// </remarks>
    public static bool TryCreateMergeJoin(
        QueryOperator left,
        QueryOperator right,
        JoinType joinType,
        Expression? onCondition,
        [NotNullWhen(true)] out MergeJoinOperator? mergeJoin)
    {
        mergeJoin = null;

        // Only INNER, LEFT, RIGHT, and FULL OUTER joins benefit from merge.
        // CROSS, SEMI, and ANTI-SEMI joins have different semantics or no equi-key.
        if (joinType is not (JoinType.Inner or JoinType.Left or JoinType.Right or JoinType.FullOuter))
        {
            return false;
        }

        JoinKeyExtractionResult? extraction = JoinKeyExtractor.TryExtract(onCondition);

        if (extraction is null)
        {
            return false;
        }

        // Only single-key merge join for now.
        if (extraction.KeyPairs.Count != 1)
        {
            return false;
        }

        // Both keys must be simple column references to match against sorted index names.
        if (extraction.KeyPairs[0].Left is not ColumnReference leftColumnRef
            || extraction.KeyPairs[0].Right is not ColumnReference rightColumnRef)
        {
            return false;
        }

        // Both sides must be simple scan chains — if the left side contains a join,
        // hash join output is unordered and merge join cannot apply.
        ScanOperator? leftScan = PlanTreeWalker.FindScanOperatorInChain(left);
        ScanOperator? rightScan = PlanTreeWalker.FindScanOperatorInChain(right);

        if (leftScan is null || rightScan is null)
        {
            return false;
        }

        // Both scans must have source indexes with sorted value indexes on the join column.
        if (leftScan.SourceIndex is null || rightScan.SourceIndex is null)
        {
            return false;
        }

        string leftColumnName = leftColumnRef.ColumnName;
        string rightColumnName = rightColumnRef.ColumnName;

        // PR13d: physically-sorted column indexes (the old SortedIndex section) were
        // retired in v8 in favour of per-column B+Tree files whose entries enumerate
        // in key order but point at scattered rows in the datum file. Merge join via
        // existing-sort no longer applies; the operator infrastructure
        // (MergeJoinOperator, IndexScanOperator) stays for future use over a sorted
        // physical layout (e.g. a sorted segment) but the planner can't pick it
        // without that backing.
        return false;
#pragma warning disable CS0162 // Unreachable code
        IColumnIndex leftColumnIndex = null!;
        IColumnIndex rightColumnIndex = null!;

        // Replace both ScanOperators with ascending IndexScanOperators.
        IndexScanOperator leftIndexScan = new(
            leftScan.TableProvider,
            leftScan.RequiredColumns,
            leftColumnIndex,
            leftScan.SourceIndex.Chunks,
            descending: false,
            leftColumnName);

        IndexScanOperator rightIndexScan = new(
            rightScan.TableProvider,
            rightScan.RequiredColumns,
            rightColumnIndex,
            rightScan.SourceIndex.Chunks,
            descending: false,
            rightColumnName);

        QueryOperator leftReplaced = PlanTreeWalker.ReplaceScanOperator(left, leftScan, leftIndexScan);
        QueryOperator rightReplaced = PlanTreeWalker.ReplaceScanOperator(right, rightScan, rightIndexScan);

        mergeJoin = new MergeJoinOperator(
            leftReplaced, rightReplaced, joinType, extraction,
            leftColumnName, rightColumnName);

        return true;
#pragma warning restore CS0162
    }
}
