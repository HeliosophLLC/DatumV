using DatumIngest.Functions;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Planner;

/// <summary>
/// Stateless expression-shape utilities used by <see cref="QueryPlanner"/>: AND-conjunct
/// flattening / re-combining (for predicate pushdown) and aggregate-function detection
/// (for projection-vs-aggregate dispatch in PlanCore and LET binding validation).
/// </summary>
internal static class PredicateUtilities
{
    /// <summary>
    /// Recursively flattens AND-connected expressions into a list of conjuncts so each
    /// can be considered individually for pushdown.
    /// </summary>
    public static void FlattenAnd(Expression expression, List<Expression> conjuncts)
    {
        if (expression is BinaryExpression binary && binary.Operator == BinaryOperator.And)
        {
            FlattenAnd(binary.Left, conjuncts);
            FlattenAnd(binary.Right, conjuncts);
        }
        else
        {
            conjuncts.Add(expression);
        }
    }

    /// <summary>
    /// Combines a list of expressions with AND into a single expression — inverse of
    /// <see cref="FlattenAnd"/>. The list must be non-empty.
    /// </summary>
    public static Expression CombineWithAnd(List<Expression> expressions)
    {
        Expression result = expressions[0];
        for (int index = 1; index < expressions.Count; index++)
        {
            result = new BinaryExpression(result, BinaryOperator.And, expressions[index]);
        }
        return result;
    }

    /// <summary>
    /// Returns <see langword="true"/> if any SELECT column expression contains an
    /// aggregate function call. Used by PlanCore to decide whether to emit a
    /// <see cref="Operators.GroupByOperator"/>. Wildcard columns are skipped — their
    /// presence forces a separate path.
    /// </summary>
    public static bool HasAggregateFunction(
        IReadOnlyList<SelectColumn> columns,
        FunctionRegistry functionRegistry)
    {
        foreach (SelectColumn column in columns)
        {
            if (column is SelectAllColumns or SelectTableColumns)
            {
                continue;
            }

            if (ExpressionContainsAggregate(column.Expression, functionRegistry))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Recursively checks whether an expression tree contains an aggregate function call.
    /// Descends through binary / unary / cast / IN / BETWEEN / IS NULL / CASE shapes so an
    /// aggregate buried in a sub-expression still surfaces.
    /// </summary>
    public static bool ExpressionContainsAggregate(Expression expression, FunctionRegistry functionRegistry)
    {
        return expression switch
        {
            FunctionCallExpression func => functionRegistry.TryGetAggregate(func.CallName) is not null
                || func.Arguments.Any(argument => ExpressionContainsAggregate(argument, functionRegistry)),
            BinaryExpression bin => ExpressionContainsAggregate(bin.Left, functionRegistry)
                || ExpressionContainsAggregate(bin.Right, functionRegistry),
            UnaryExpression unary => ExpressionContainsAggregate(unary.Operand, functionRegistry),
            CastExpression cast => ExpressionContainsAggregate(cast.Expression, functionRegistry),
            InExpression inExpr => ExpressionContainsAggregate(inExpr.Expression, functionRegistry),
            BetweenExpression between => ExpressionContainsAggregate(between.Expression, functionRegistry)
                || ExpressionContainsAggregate(between.Low, functionRegistry)
                || ExpressionContainsAggregate(between.High, functionRegistry),
            IsNullExpression isNull => ExpressionContainsAggregate(isNull.Expression, functionRegistry),
            CaseExpression caseExpr => CaseExpressionContainsAggregate(caseExpr, functionRegistry),
            _ => false,
        };
    }

    /// <summary>
    /// Checks whether a CASE expression contains any aggregate function calls in its
    /// operand, WHEN conditions, THEN results, or ELSE result.
    /// </summary>
    public static bool CaseExpressionContainsAggregate(CaseExpression caseExpression, FunctionRegistry functionRegistry)
    {
        if (caseExpression.Operand is not null && ExpressionContainsAggregate(caseExpression.Operand, functionRegistry))
        {
            return true;
        }

        foreach (WhenClause whenClause in caseExpression.WhenClauses)
        {
            if (ExpressionContainsAggregate(whenClause.Condition, functionRegistry)
                || ExpressionContainsAggregate(whenClause.Result, functionRegistry))
            {
                return true;
            }
        }

        return caseExpression.ElseResult is not null
            && ExpressionContainsAggregate(caseExpression.ElseResult, functionRegistry);
    }
}
