using Heliosoph.DatumV.Execution.Operators;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution.Planner;

/// <summary>
/// Builds the dictionary of <see cref="CommonTableExpressionOperator"/>
/// instances for a <c>WITH</c> clause, handling both non-recursive and
/// recursive CTEs. Mirrors the order in which the parsed CTEs appear, so
/// later CTEs can reference earlier ones through the body-plan callback.
/// </summary>
/// <remarks>
/// <para>
/// Non-recursive CTEs delegate to the body-plan callback for their body,
/// passing the dictionary of already-built sibling CTEs so references
/// resolve. The auto-materialise policy mirrors what shipped in PR-CTE:
/// explicit <see cref="MaterializationHint.Materialized"/> /
/// <see cref="MaterializationHint.NotMaterialized"/> wins, otherwise we
/// materialise when the CTE is referenced more than once
/// (per <see cref="CommonTableExpressionReferenceCounter"/>).
/// </para>
/// <para>
/// Recursive CTEs build the anchor up-front via the select-statement
/// callback and stash a factory delegate that, given the working-table
/// operator at runtime, re-plans the recursive member with a self-reference
/// plus all sibling CTEs in scope.
/// </para>
/// </remarks>
internal static class CommonTableExpressionPlanner
{
    /// <summary>
    /// Plans the <c>WITH</c> clause of <paramref name="statement"/>, returning
    /// the CTE name → operator dictionary in declaration order or
    /// <see langword="null"/> when the statement has no CTEs.
    /// </summary>
    /// <param name="statement">The SELECT statement whose CTEs to plan.</param>
    /// <param name="planSelectStatementWithCommonTableExpressions">
    /// Plans a <see cref="SelectStatement"/> (the recursive-CTE anchor or
    /// recursive-member body) against an external CTE dictionary. Equivalent
    /// to <c>QueryPlanner.PlanCore(stmt, externalCommonTableExpressionOperators: ctes)</c>.
    /// </param>
    /// <param name="planBodyWithCommonTableExpressions">
    /// Plans a non-recursive CTE body (which is a
    /// <see cref="QueryExpression"/>) against the set of already-built
    /// sibling CTEs. When the sibling set is empty the caller can defer to
    /// the plain <c>Plan(QueryExpression)</c> path; this delegate hides that
    /// choice.
    /// </param>
    public static Dictionary<string, CommonTableExpressionOperator>? Plan(
        SelectStatement statement,
        Func<SelectStatement, IReadOnlyDictionary<string, CommonTableExpressionOperator>, QueryOperator> planSelectStatementWithCommonTableExpressions,
        Func<QueryExpression, IReadOnlyDictionary<string, CommonTableExpressionOperator>, QueryOperator> planBodyWithCommonTableExpressions)
    {
        if (statement.CommonTableExpressions is null || statement.CommonTableExpressions.Count == 0)
        {
            return null;
        }

        Dictionary<string, int> referenceCounts =
            CommonTableExpressionReferenceCounter.CountCommonTableExpressionReferences(statement);

        Dictionary<string, CommonTableExpressionOperator> operators = new(
            statement.CommonTableExpressions.Count, StringComparer.OrdinalIgnoreCase);

        foreach (CommonTableExpression commonTableExpression in statement.CommonTableExpressions)
        {
            if (commonTableExpression.IsRecursive && commonTableExpression.RecursiveQuery is not null)
            {
                // Recursive CTEs store the anchor as a SelectQueryExpression wrapper.
                SelectStatement anchorStatement = commonTableExpression.Body switch
                {
                    SelectQueryExpression select => select.Statement,
                    _ => throw new QueryPlanException(
                        $"Recursive CTE '{commonTableExpression.Name}' body must be a single SELECT statement (the anchor member)."),
                };

                QueryOperator anchorPlan = planSelectStatementWithCommonTableExpressions(anchorStatement, operators);

                // Capture for the closure. The factory is called at execution time,
                // once per iteration, with a fresh working-table operator.
                CommonTableExpression capturedDefinition = commonTableExpression;

                RecursiveCommonTableExpressionOperator recursiveOperator = new(
                    anchorPlan,
                    workingTableOperator =>
                    {
                        // Build a CTE dictionary that maps the self-reference to the
                        // working table so the recursive member's FROM resolves correctly.
                        CommonTableExpressionOperator selfReference = new(
                            workingTableOperator,
                            capturedDefinition.Name,
                            isMaterialized: false);

                        Dictionary<string, CommonTableExpressionOperator> selfReferenceOperators = new(
                            StringComparer.OrdinalIgnoreCase);

                        // Include all previously-built CTEs so the recursive member
                        // can reference sibling CTEs in addition to itself.
                        foreach (KeyValuePair<string, CommonTableExpressionOperator> existing in operators)
                        {
                            selfReferenceOperators[existing.Key] = existing.Value;
                        }

                        selfReferenceOperators[capturedDefinition.Name] = selfReference;

                        return planSelectStatementWithCommonTableExpressions(
                            capturedDefinition.RecursiveQuery!, selfReferenceOperators);
                    },
                    commonTableExpression.Name,
                    commonTableExpression.ColumnNames);

                // Wrap recursive operator so PlanTableReference can resolve the CTE by name.
                CommonTableExpressionOperator wrappedRecursive = new(
                    recursiveOperator,
                    commonTableExpression.Name,
                    isMaterialized: false);

                operators[commonTableExpression.Name] = wrappedRecursive;
                continue;
            }

            QueryOperator innerPlan = planBodyWithCommonTableExpressions(commonTableExpression.Body, operators);

            bool shouldMaterialize = commonTableExpression.Hint switch
            {
                MaterializationHint.Materialized => true,
                MaterializationHint.NotMaterialized => false,
                // Auto-materialize when referenced more than once to avoid redundant computation.
                _ => referenceCounts.TryGetValue(commonTableExpression.Name, out int count) && count > 1,
            };

            CommonTableExpressionOperator cteOperator = new(
                innerPlan,
                commonTableExpression.Name,
                shouldMaterialize,
                commonTableExpression.ColumnNames);

            operators[commonTableExpression.Name] = cteOperator;
        }

        return operators.Count > 0 ? operators : null;
    }
}
