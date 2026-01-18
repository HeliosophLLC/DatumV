using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

/// <summary>
/// Recursively walks an expression tree and collects all
/// <see cref="ColumnReference"/> nodes. Used by the query planner
/// for predicate pushdown (determining which table aliases a predicate
/// references) and projection pushdown (determining which columns are needed).
/// </summary>
public static class ColumnReferenceCollector
{
    /// <summary>
    /// Collects all column references from the given expression.
    /// </summary>
    /// <param name="expression">The expression to walk.</param>
    /// <returns>A set of (TableName, ColumnName) pairs found in the expression.</returns>
    public static HashSet<(string? TableName, string ColumnName)> Collect(Expression expression)
    {
        HashSet<(string? TableName, string ColumnName)> references = new();
        Walk(expression, references);
        return references;
    }

    /// <summary>
    /// Collects all distinct table aliases referenced in the given expression.
    /// Unqualified column references (no table alias) are excluded.
    /// </summary>
    /// <param name="expression">The expression to walk.</param>
    /// <returns>A set of table alias strings.</returns>
    public static HashSet<string> CollectTableAliases(Expression expression)
    {
        HashSet<(string? TableName, string ColumnName)> references = Collect(expression);
        HashSet<string> aliases = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string? tableName, string _) in references)
        {
            if (tableName is not null)
            {
                aliases.Add(tableName);
            }
        }

        return aliases;
    }

    /// <summary>
    /// Collects all column references from a collection of expressions.
    /// </summary>
    /// <param name="expressions">The expressions to walk.</param>
    /// <returns>A set of (TableName, ColumnName) pairs.</returns>
    public static HashSet<(string? TableName, string ColumnName)> CollectFromAll(
        IEnumerable<Expression> expressions)
    {
        HashSet<(string? TableName, string ColumnName)> references = new();
        foreach (Expression expression in expressions)
        {
            Walk(expression, references);
        }

        return references;
    }

    private static void Walk(
        Expression expression,
        HashSet<(string? TableName, string ColumnName)> references)
    {
        switch (expression)
        {
            case ColumnReference column:
                references.Add((column.TableName, column.ColumnName));
                break;

            case BinaryExpression binary:
                Walk(binary.Left, references);
                Walk(binary.Right, references);
                break;

            case LikeExpression like:
                Walk(like.Expression, references);
                Walk(like.Pattern, references);
                Walk(like.EscapeCharacter, references);
                break;

            case UnaryExpression unary:
                Walk(unary.Operand, references);
                break;

            case FunctionCallExpression function:
                foreach (Expression argument in function.Arguments)
                {
                    Walk(argument, references);
                }
                // Aggregates with `ORDER BY x` (inline) or
                // `WITHIN GROUP (ORDER BY x)` reference columns that
                // aren't in the projection but are still needed at
                // accumulation time. Walk both order-by lists so
                // projection pushdown keeps the referenced columns.
                if (function.OrderBy is not null)
                {
                    foreach (OrderByItem item in function.OrderBy)
                    {
                        Walk(item.Expression, references);
                    }
                }
                if (function.WithinGroupOrderBy is not null)
                {
                    foreach (OrderByItem item in function.WithinGroupOrderBy)
                    {
                        Walk(item.Expression, references);
                    }
                }
                break;

            case WindowFunctionCallExpression window:
                foreach (Expression argument in window.Arguments)
                {
                    Walk(argument, references);
                }
                if (window.Window.PartitionBy is not null)
                {
                    foreach (Expression partition in window.Window.PartitionBy)
                    {
                        Walk(partition, references);
                    }
                }
                if (window.Window.OrderBy is not null)
                {
                    foreach (OrderByItem orderByItem in window.Window.OrderBy)
                    {
                        Walk(orderByItem.Expression, references);
                    }
                }
                break;

            case InExpression inExpression:
                Walk(inExpression.Expression, references);
                foreach (Expression value in inExpression.Values)
                {
                    Walk(value, references);
                }
                break;

            case BetweenExpression between:
                Walk(between.Expression, references);
                Walk(between.Low, references);
                Walk(between.High, references);
                break;

            case IsNullExpression isNull:
                Walk(isNull.Expression, references);
                break;

            case CastExpression cast:
                Walk(cast.Expression, references);
                break;

            case CaseExpression caseExpr:
                if (caseExpr.Operand is not null)
                {
                    Walk(caseExpr.Operand, references);
                }

                foreach (WhenClause whenClause in caseExpr.WhenClauses)
                {
                    Walk(whenClause.Condition, references);
                    Walk(whenClause.Result, references);
                }

                if (caseExpr.ElseResult is not null)
                {
                    Walk(caseExpr.ElseResult, references);
                }

                break;

            case SubqueryExpression:
            case InSubqueryExpression:
            case ExistsExpression:
                // Subquery column references are scoped to the inner query;
                // they do not reference the outer query's tables.
                break;

            case StructLiteralExpression structLiteral:
                foreach (StructField field in structLiteral.Fields)
                {
                    Walk(field.Value, references);
                }
                break;

            case IndexAccessExpression indexAccess:
                Walk(indexAccess.Source, references);
                Walk(indexAccess.Index, references);
                break;

            case LiteralExpression:
            case TypeLiteralExpression:
                // No column references in literals or type literals.
                break;
        }
    }
}
