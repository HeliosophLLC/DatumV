using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Planner;

/// <summary>
/// Detects whether a <see cref="SelectStatement"/> or expression tree contains any
/// kind of subquery (<see cref="SubqueryExpression"/>, <see cref="InSubqueryExpression"/>,
/// <see cref="ExistsExpression"/>). <see cref="QueryPlanner"/> uses these checks to
/// branch between the cheap no-subquery PlanCore path and the heavier
/// PlanCoreWithSubqueriesAsync path that runs <c>SubqueryRewriter</c>.
/// </summary>
/// <remarks>
/// All clauses that may carry expressions are inspected: WHERE, HAVING, QUALIFY,
/// SELECT columns, LET binding bodies, and JOIN ON conditions. Missing any one
/// surfaces as a runtime "Subquery expression was not rewritten" exception, so the
/// LET-binding traversal is documented in the wrapper here as a non-obvious case.
/// </remarks>
internal static class SubqueryDetection
{
    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="statement"/> contains a
    /// subquery in any of its expression clauses.
    /// </summary>
    public static bool ContainsSubqueryExpression(SelectStatement statement)
    {
        if (statement.Where is not null && ContainsSubquery(statement.Where))
        {
            return true;
        }

        foreach (SelectColumn column in statement.Columns)
        {
            if (column is SelectAllColumns or SelectTableColumns)
            {
                continue;
            }

            if (ContainsSubquery(column.Expression))
            {
                return true;
            }
        }

        // LET binding bodies can also carry subquery expressions
        // (`LET avg_price = (SELECT AVG(price) FROM ref)`). Without this check, a
        // statement whose only subquery sits inside a LET body would take the
        // no-subquery shortcut, skip SubqueryRewriter entirely, and crash at
        // evaluation time with "Subquery expression was not rewritten."
        if (statement.LetBindings is not null)
        {
            foreach (LetBinding binding in statement.LetBindings)
            {
                if (ContainsSubquery(binding.Expression))
                {
                    return true;
                }
            }
        }

        if (statement.Having is not null && ContainsSubquery(statement.Having))
        {
            return true;
        }

        if (statement.Qualify is not null && ContainsSubquery(statement.Qualify))
        {
            return true;
        }

        if (statement.Joins is not null)
        {
            foreach (JoinClause join in statement.Joins)
            {
                if (join.OnCondition is not null && ContainsSubquery(join.OnCondition))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Recursively checks if an expression tree contains a <see cref="SubqueryExpression"/>,
    /// <see cref="InSubqueryExpression"/>, or <see cref="ExistsExpression"/>. Descends
    /// through the standard composite shapes (binary / unary / function-call / IN /
    /// BETWEEN / IS NULL / CAST / CASE) so a subquery buried inside one still surfaces.
    /// </summary>
    public static bool ContainsSubquery(Expression expression)
    {
        return expression switch
        {
            SubqueryExpression => true,
            InSubqueryExpression => true,
            ExistsExpression => true,
            BinaryExpression binary => ContainsSubquery(binary.Left) || ContainsSubquery(binary.Right),
            UnaryExpression unary => ContainsSubquery(unary.Operand),
            FunctionCallExpression function => function.Arguments.Any(ContainsSubquery),
            InExpression inExpr => ContainsSubquery(inExpr.Expression) || inExpr.Values.Any(ContainsSubquery),
            BetweenExpression between => ContainsSubquery(between.Expression) ||
                ContainsSubquery(between.Low) || ContainsSubquery(between.High),
            IsNullExpression isNull => ContainsSubquery(isNull.Expression),
            CastExpression cast => ContainsSubquery(cast.Expression),
            CaseExpression caseExpr => (caseExpr.Operand is not null && ContainsSubquery(caseExpr.Operand)) ||
                caseExpr.WhenClauses.Any(clause => ContainsSubquery(clause.Condition) || ContainsSubquery(clause.Result)) ||
                (caseExpr.ElseResult is not null && ContainsSubquery(caseExpr.ElseResult)),
            _ => false,
        };
    }
}
