using DatumIngest.Execution.Operators;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Planner;

/// <summary>
/// Predicate pushdown helpers used by <see cref="QueryPlanner"/>'s join planning.
/// Walks a pending-predicate list and pushes each conjunct as far down the
/// operator tree as its alias-availability allows, then adds it to the surviving
/// scan(s) as an advisory filter hint for statistics-based partition pruning.
/// </summary>
/// <remarks>
/// <see cref="PushPredicatesBelow"/> is the rung-by-rung pushdown driver.
/// <see cref="DeriveTransitivePredicates"/> mines equi-join conditions for
/// transitive equalities (so <c>A.x = lit AND A.x = B.x</c> derives the extra
/// <c>B.x = lit</c> predicate for B's scan).
/// <see cref="ExtractEquiJoinPairs"/> is a leaf helper that pulls AND-connected
/// column-to-column equalities out of an ON condition.
/// </remarks>
internal static class PredicatePushdown
{
    /// <summary>
    /// Pushes predicates that reference only aliases in <paramref name="availableAliases"/>
    /// below <paramref name="operatorNode"/> as <see cref="FilterOperator"/> wrappers.
    /// Pushed predicates are removed from <paramref name="predicates"/>. When the
    /// underlying source is a <see cref="ScanOperator"/>, the predicate is also added as
    /// an advisory filter hint for statistics-based partition pruning.
    /// </summary>
    public static QueryOperator PushPredicatesBelow(
        QueryOperator operatorNode,
        HashSet<string> availableAliases,
        List<Expression> predicates)
    {
        QueryOperator result = operatorNode;

        for (int index = predicates.Count - 1; index >= 0; index--)
        {
            Expression predicate = predicates[index];
            HashSet<string> predicateAliases = ColumnReferenceCollector.CollectTableAliases(predicate);

            // Push if all referenced aliases are available on this side, or if the
            // predicate has no table-qualified references (global).
            if (predicateAliases.Count == 0 || predicateAliases.IsSubsetOf(availableAliases))
            {
                result = new FilterOperator(result, predicate);
                predicates.RemoveAt(index);

                // Pass the predicate as a filter hint to the underlying scan so
                // filterable providers can use statistics to skip partitions.
                PlanTreeWalker.AddFilterHintToScan(operatorNode, predicate);
            }
        }

        return result;
    }

    /// <summary>
    /// Derives transitive equality predicates from equi-join conditions and existing
    /// pending predicates. When a pending predicate says <c>A.x = &lt;literal&gt;</c>
    /// and the join ON condition contains <c>A.x = B.x</c>, this derives
    /// <c>B.x = &lt;literal&gt;</c> and appends it to the pending list so it can be
    /// pushed to B's scan for index / statistics pruning.
    /// </summary>
    public static void DeriveTransitivePredicates(
        Expression onCondition,
        List<Expression> pendingPredicates)
    {
        // Extract equi-join pairs: (A.col, B.col) from the ON condition.
        List<(ColumnReference Left, ColumnReference Right)> equiPairs = new();
        ExtractEquiJoinPairs(onCondition, equiPairs);

        if (equiPairs.Count == 0)
        {
            return;
        }

        // Collect existing literal equality predicates: alias.col = literal.
        // We scan the current pendingPredicates snapshot (not the derived ones) to
        // avoid infinite chaining in a single pass.
        int originalCount = pendingPredicates.Count;
        Dictionary<(string Alias, string Column), LiteralExpression> literalEqualities = new();

        for (int i = 0; i < originalCount; i++)
        {
            if (pendingPredicates[i] is not BinaryExpression binary
                || binary.Operator != BinaryOperator.Equal)
            {
                continue;
            }

            if (binary.Left is ColumnReference colRef && binary.Right is LiteralExpression lit
                && colRef.TableName is not null)
            {
                literalEqualities[(colRef.TableName, colRef.ColumnName)] = lit;
            }
            else if (binary.Right is ColumnReference colRef2 && binary.Left is LiteralExpression lit2
                && colRef2.TableName is not null)
            {
                literalEqualities[(colRef2.TableName, colRef2.ColumnName)] = lit2;
            }
        }

        if (literalEqualities.Count == 0)
        {
            return;
        }

        // For each equi-join pair, check if one side has a literal equality and
        // derive a predicate for the other side.
        foreach ((ColumnReference leftCol, ColumnReference rightCol) in equiPairs)
        {
            if (leftCol.TableName is not null
                && literalEqualities.TryGetValue(
                    (leftCol.TableName, leftCol.ColumnName), out LiteralExpression? leftLiteral))
            {
                // A.x = literal AND A.x = B.x → derive B.x = literal
                BinaryExpression derived = new(rightCol, BinaryOperator.Equal, leftLiteral);
                pendingPredicates.Add(derived);
            }

            if (rightCol.TableName is not null
                && literalEqualities.TryGetValue(
                    (rightCol.TableName, rightCol.ColumnName), out LiteralExpression? rightLiteral))
            {
                // B.x = literal AND A.x = B.x → derive A.x = literal
                BinaryExpression derived = new(leftCol, BinaryOperator.Equal, rightLiteral);
                pendingPredicates.Add(derived);
            }
        }
    }

    /// <summary>
    /// Extracts column-to-column equality pairs from a join ON condition. Only
    /// top-level AND-connected equalities between two qualified column references
    /// are extracted.
    /// </summary>
    private static void ExtractEquiJoinPairs(
        Expression expression,
        List<(ColumnReference Left, ColumnReference Right)> pairs)
    {
        if (expression is BinaryExpression binary)
        {
            if (binary.Operator == BinaryOperator.And)
            {
                ExtractEquiJoinPairs(binary.Left, pairs);
                ExtractEquiJoinPairs(binary.Right, pairs);
                return;
            }

            if (binary.Operator == BinaryOperator.Equal
                && binary.Left is ColumnReference leftCol
                && binary.Right is ColumnReference rightCol)
            {
                pairs.Add((leftCol, rightCol));
            }
        }
    }
}
