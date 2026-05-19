using System.Diagnostics.CodeAnalysis;
using Heliosoph.DatumV.Diagnostics;
using Heliosoph.DatumV.Execution.Operators;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution.Planner;

/// <summary>
/// Heuristic-driven join-order optimization for <see cref="QueryPlanner"/>:
/// elides redundant LEFT JOINs whose right side is unreferenced, then greedily
/// reorders inner-join chains so the largest table drives as the outermost
/// probe (or a sort-eligible table when an ORDER BY can be elided).
/// </summary>
/// <remarks>
/// These are heuristics; a cost-based optimizer on the roadmap will replace
/// them. Trace output flows through <see cref="DatumActivity"/> so EXPLAIN-style
/// inspection of the planner's decisions is available end-to-end.
/// </remarks>
internal static class JoinReorderer
{
    /// <summary>
    /// Removes LEFT JOINs whose right-side table is not referenced anywhere in
    /// the query output (SELECT, WHERE, GROUP BY, HAVING, ORDER BY, LET, QUALIFY)
    /// and is not required by any other surviving join's ON condition. Safe
    /// because a LEFT JOIN to an unreferenced table cannot filter rows — it
    /// preserves all left-side rows — and only adds hash-table and I/O cost.
    /// </summary>
    /// <remarks>
    /// Elimination is iterative: removing one join may make another join's
    /// right-side table unreferenced (cascading elimination). Only LEFT JOINs
    /// are candidates; INNER/RIGHT/CROSS joins can filter or multiply rows and
    /// are never removed.
    /// </remarks>
    public static void EliminateUnusedJoins(
        SelectStatement statement,
        List<(JoinClause Join, QueryOperator Operator, HashSet<string> Aliases)> plannedJoins)
    {
        // Collect table aliases referenced in query output clauses (not JOIN ON).
        HashSet<string> outputReferenced = new(StringComparer.OrdinalIgnoreCase);

        foreach (SelectColumn column in statement.Columns)
        {
            if (column is SelectAllColumns)
            {
                return; // SELECT * needs all tables — no elimination possible.
            }

            if (column is SelectTableColumns tableColumns)
            {
                outputReferenced.Add(tableColumns.TableName);
                continue;
            }

            foreach (string alias in ColumnReferenceCollector.CollectTableAliases(column.Expression))
            {
                outputReferenced.Add(alias);
            }
        }

        if (statement.Where is not null)
        {
            foreach (string alias in ColumnReferenceCollector.CollectTableAliases(statement.Where))
            {
                outputReferenced.Add(alias);
            }
        }

        if (statement.GroupBy is not null)
        {
            foreach (Expression expression in statement.GroupBy.Expressions)
            {
                foreach (string alias in ColumnReferenceCollector.CollectTableAliases(expression))
                {
                    outputReferenced.Add(alias);
                }
            }
        }

        if (statement.Having is not null)
        {
            foreach (string alias in ColumnReferenceCollector.CollectTableAliases(statement.Having))
            {
                outputReferenced.Add(alias);
            }
        }

        if (statement.OrderBy is not null)
        {
            foreach (OrderByItem item in statement.OrderBy.Items)
            {
                foreach (string alias in ColumnReferenceCollector.CollectTableAliases(item.Expression))
                {
                    outputReferenced.Add(alias);
                }
            }
        }

        if (statement.LetBindings is not null)
        {
            foreach (LetBinding binding in statement.LetBindings)
            {
                foreach (string alias in ColumnReferenceCollector.CollectTableAliases(binding.Expression))
                {
                    outputReferenced.Add(alias);
                }
            }
        }

        if (statement.Qualify is not null)
        {
            foreach (string alias in ColumnReferenceCollector.CollectTableAliases(statement.Qualify))
            {
                outputReferenced.Add(alias);
            }
        }

        // Iteratively eliminate LEFT JOINs whose right-side alias is unreferenced
        // by both the output clauses and other surviving joins' ON conditions.
        bool changed = true;

        while (changed)
        {
            changed = false;

            for (int index = plannedJoins.Count - 1; index >= 0; index--)
            {
                (JoinClause join, _, HashSet<string> aliases) = plannedJoins[index];

                if (join.Type != JoinType.Left)
                {
                    continue;
                }

                // Check if any of this join's right-side aliases are needed.
                bool needed = false;

                foreach (string alias in aliases)
                {
                    if (outputReferenced.Contains(alias))
                    {
                        needed = true;
                        break;
                    }
                }

                if (needed)
                {
                    continue;
                }

                // Check if any other surviving join's ON condition references this alias.
                bool referencedByOtherJoin = false;

                for (int otherIndex = 0; otherIndex < plannedJoins.Count; otherIndex++)
                {
                    if (otherIndex == index)
                    {
                        continue;
                    }

                    JoinClause otherJoin = plannedJoins[otherIndex].Join;

                    if (otherJoin.OnCondition is null)
                    {
                        continue;
                    }

                    HashSet<string> onAliases =
                        ColumnReferenceCollector.CollectTableAliases(otherJoin.OnCondition);

                    foreach (string alias in aliases)
                    {
                        if (onAliases.Contains(alias))
                        {
                            referencedByOtherJoin = true;
                            break;
                        }
                    }

                    if (referencedByOtherJoin)
                    {
                        break;
                    }
                }

                if (!referencedByOtherJoin)
                {
                    string joinLabel = string.Join(", ", aliases);
                    DatumActivity.Operators.Trace($"JOIN ELIMINATION  removed {joinLabel} (unreferenced LEFT JOIN)");
                    plannedJoins.RemoveAt(index);
                    changed = true;
                }
            }
        }
    }

    /// <summary>
    /// Attempts greedy join reordering: the source with the largest estimated
    /// row count becomes the new FROM (probe / streaming side) so LIMIT can
    /// short-circuit early, and smaller tables move to the build side. When a
    /// preferred probe alias is supplied (the ORDER BY target with a sorted
    /// column index), that table is promoted instead so the sort can later be
    /// eliminated via index-scan rewrite.
    /// </summary>
    /// <remarks>
    /// Only applied when every join is a non-lateral <see cref="JoinType.Inner"/>
    /// join and all sources have estimated row counts. Cross joins are excluded
    /// because they lack an ON condition that can be checked for alias
    /// connectivity. This is a heuristic; the roadmap cost-based optimizer will
    /// replace it.
    /// </remarks>
    public static bool TryReorderJoins(
        QueryOperator fromOperator,
        HashSet<string> fromAliases,
        List<(JoinClause Join, QueryOperator Operator, HashSet<string> Aliases)> plannedJoins,
        string? preferredProbeTableAlias,
        [NotNullWhen(true)] out QueryOperator? reorderedSource,
        [NotNullWhen(true)] out HashSet<string>? reorderedFromAliases,
        [NotNullWhen(true)] out List<(JoinClause Join, QueryOperator Operator, HashSet<string> Aliases)>? reorderedJoins)
    {
        reorderedSource = null;
        reorderedFromAliases = null;
        reorderedJoins = null;

        // Gate: all joins must be non-lateral INNER with an ON condition.
        foreach ((JoinClause join, _, _) in plannedJoins)
        {
            if (join.IsLateral || join.Type != JoinType.Inner || join.OnCondition is null)
            {
                DatumActivity.Operators.Trace($"JOIN REORDER  skipped: non-INNER or lateral join present");
                return false;
            }
        }

        // Collect all sources into a pool with their estimated row counts. Index 0 is
        // the FROM source (no JoinClause); rest are join sources.
        int totalSources = 1 + plannedJoins.Count;
        long?[] rowCounts = new long?[totalSources];

        rowCounts[0] = PlanShapeInspector.GetEstimatedRowCount(fromOperator);
        if (rowCounts[0] is null)
        {
            DatumActivity.Operators.Trace($"JOIN REORDER  skipped: no row count for FROM={PlanShapeInspector.GetOperatorName(fromOperator)}");
            return false;
        }

        for (int index = 0; index < plannedJoins.Count; index++)
        {
            rowCounts[index + 1] = PlanShapeInspector.GetEstimatedRowCount(plannedJoins[index].Operator);
            if (rowCounts[index + 1] is null)
            {
                DatumActivity.Operators.Trace($"JOIN REORDER  skipped: no row count for JOIN[{index}]={PlanShapeInspector.GetOperatorName(plannedJoins[index].Operator)}");
                return false;
            }
        }

        if (DatumActivity.Operators.HasListeners())
        {
            DatumActivity.Operators.Trace($"JOIN REORDER  evaluating {totalSources} sources");
            DatumActivity.Operators.Trace($"  [0] FROM  {PlanShapeInspector.GetOperatorName(fromOperator)}  rows={rowCounts[0]:N0}");
            for (int index = 0; index < plannedJoins.Count; index++)
            {
                DatumActivity.Operators.Trace($"  [{index + 1}] JOIN  {PlanShapeInspector.GetOperatorName(plannedJoins[index].Operator)}  rows={rowCounts[index + 1]:N0}");
            }
        }

        // Find the source with the largest estimated row count — it becomes the probe
        // when no ORDER BY sort-table preference is in effect.
        int largestIndex = 0;
        long largestCount = rowCounts[0]!.Value;

        for (int index = 1; index < totalSources; index++)
        {
            if (rowCounts[index]!.Value > largestCount)
            {
                largestCount = rowCounts[index]!.Value;
                largestIndex = index;
            }
        }

        // Determine the chosen outermost probe. When a preferred probe table exists
        // (ORDER BY table with a sorted column index), use it so that the sort can
        // later be eliminated by replacing its scan with an IndexScanOperator. The
        // existing row-count heuristic is the fallback.
        int chosenIndex = largestIndex;

        if (preferredProbeTableAlias is not null)
        {
            if (fromAliases.Contains(preferredProbeTableAlias))
            {
                // The preferred table is already the outermost FROM — no reordering is
                // needed and we must not displace it with the largest-table heuristic.
                DatumActivity.Operators.Trace($"JOIN REORDER  skipped: ORDER BY table '{preferredProbeTableAlias}' is already outermost FROM");
                return false;
            }

            for (int index = 0; index < plannedJoins.Count; index++)
            {
                if (plannedJoins[index].Aliases.Contains(preferredProbeTableAlias))
                {
                    chosenIndex = index + 1;
                    DatumActivity.Operators.Trace($"JOIN REORDER  promoting ORDER BY table '{preferredProbeTableAlias}' as outermost probe for sort elimination");
                    break;
                }
            }
        }

        // If the chosen source is already the FROM, no reordering is needed.
        if (chosenIndex == 0)
        {
            DatumActivity.Operators.Trace($"JOIN REORDER  skipped: FROM is already largest ({PlanShapeInspector.GetOperatorName(fromOperator)} rows={largestCount:N0})");
            return false;
        }

        DatumActivity.Operators.Trace($"JOIN REORDER  new probe (FROM) = {PlanShapeInspector.GetOperatorName(plannedJoins[chosenIndex - 1].Operator)}  rows={rowCounts[chosenIndex]:N0}");

        // Build the pool of remaining sources to schedule. Each entry: (Operator,
        // Aliases, RowCount, JoinClause or null for the original FROM). The original
        // FROM becomes a joinable source — it keeps the ON condition from the join
        // that previously connected the new probe to the tree. We'll assign ON
        // conditions during the greedy scheduling below.
        List<(QueryOperator Operator, HashSet<string> Aliases, long RowCount, JoinClause? Join)> remaining = new(totalSources - 1);
        remaining.Add((fromOperator, fromAliases, rowCounts[0]!.Value, null));

        for (int index = 0; index < plannedJoins.Count; index++)
        {
            if (index + 1 == chosenIndex)
            {
                continue; // Skip the one we chose as the new FROM.
            }

            remaining.Add((plannedJoins[index].Operator, plannedJoins[index].Aliases,
                rowCounts[index + 1]!.Value, plannedJoins[index].Join));
        }

        // Collect all ON conditions from the original join list. These form the pool
        // of predicates that must be distributed to the reordered joins. Each ON
        // condition connects two sets of aliases.
        List<Expression> onConditionPool = new(plannedJoins.Count);
        foreach ((JoinClause join, _, _) in plannedJoins)
        {
            onConditionPool.Add(join.OnCondition!);
        }

        // Set up the new FROM from the chosen source.
        QueryOperator newFrom;
        HashSet<string> joinedAliases;

        int chosenJoinIndex = chosenIndex - 1;
        newFrom = plannedJoins[chosenJoinIndex].Operator;
        joinedAliases = new HashSet<string>(plannedJoins[chosenJoinIndex].Aliases, StringComparer.OrdinalIgnoreCase);

        // Greedy scheduling: at each step pick the smallest remaining source whose ON
        // condition is satisfiable (all referenced aliases are in the joined set).
        List<(JoinClause Join, QueryOperator Operator, HashSet<string> Aliases)> result = new(remaining.Count);

        while (remaining.Count > 0)
        {
            int bestIndex = -1;
            long bestCount = long.MaxValue;

            for (int index = 0; index < remaining.Count; index++)
            {
                // Check that at least one ON condition is satisfiable when we add this source.
                HashSet<string> candidateAliases = remaining[index].Aliases;

                bool hasSatisfiableCondition = false;
                foreach (Expression onCondition in onConditionPool)
                {
                    HashSet<string> conditionAliases = ColumnReferenceCollector.CollectTableAliases(onCondition);

                    // The condition is satisfiable if every alias it references is
                    // either in the already-joined set or in this candidate's aliases.
                    HashSet<string> combined = new(joinedAliases, StringComparer.OrdinalIgnoreCase);
                    foreach (string alias in candidateAliases)
                    {
                        combined.Add(alias);
                    }

                    if (conditionAliases.IsSubsetOf(combined))
                    {
                        hasSatisfiableCondition = true;
                        break;
                    }
                }

                if (!hasSatisfiableCondition)
                {
                    continue;
                }

                if (remaining[index].RowCount < bestCount)
                {
                    bestCount = remaining[index].RowCount;
                    bestIndex = index;
                }
            }

            if (bestIndex == -1)
            {
                // No remaining source has a satisfiable ON condition — cannot reorder.
                return false;
            }

            // Consume the chosen source and assign its applicable ON conditions.
            (QueryOperator chosenOperator, HashSet<string> chosenAliases, _, _) = remaining[bestIndex];
            remaining.RemoveAt(bestIndex);

            // Collect all ON conditions that are now satisfiable with the joined set + chosen source.
            HashSet<string> newJoined = new(joinedAliases, StringComparer.OrdinalIgnoreCase);
            foreach (string alias in chosenAliases)
            {
                newJoined.Add(alias);
            }

            List<Expression> applicableConditions = new();
            for (int index = onConditionPool.Count - 1; index >= 0; index--)
            {
                HashSet<string> conditionAliases = ColumnReferenceCollector.CollectTableAliases(onConditionPool[index]);

                if (conditionAliases.IsSubsetOf(newJoined))
                {
                    applicableConditions.Add(onConditionPool[index]);
                    onConditionPool.RemoveAt(index);
                }
            }

            // Combine applicable conditions into a single ON expression.
            Expression onExpression = applicableConditions.Count == 1
                ? applicableConditions[0]
                : PredicateUtilities.CombineWithAnd(applicableConditions);

            // The TableSource is not used after planning — supply a placeholder reference.
            JoinClause reorderedJoinClause = new(JoinType.Inner, new TableReference("_reordered_"), onExpression, false);
            result.Add((reorderedJoinClause, chosenOperator, chosenAliases));

            // Expand the joined alias set.
            joinedAliases = newJoined;
        }

        reorderedSource = newFrom;
        reorderedFromAliases = new HashSet<string>(
            plannedJoins[chosenIndex - 1].Aliases,
            StringComparer.OrdinalIgnoreCase);
        reorderedJoins = result;

        if (DatumActivity.Operators.HasListeners())
        {
            DatumActivity.Operators.Trace($"JOIN REORDER  final build order (smallest first):");
            for (int index = 0; index < result.Count; index++)
            {
                DatumActivity.Operators.Trace($"  build[{index}]  {PlanShapeInspector.GetOperatorName(result[index].Operator)}");
            }
        }

        return true;
    }
}
