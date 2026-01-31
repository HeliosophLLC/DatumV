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

    /// <summary>
    /// Walks every expression-bearing clause of a <see cref="SelectStatement"/>,
    /// collecting column references. Used to surface correlated (outer-scoped)
    /// refs when descending into subquery expressions.
    /// </summary>
    private static void WalkStatement(
        SelectStatement statement,
        HashSet<(string? TableName, string ColumnName)> references)
    {
        foreach (SelectColumn column in statement.Columns)
        {
            if (column is SelectAllColumns or SelectTableColumns)
            {
                continue;
            }
            Walk(column.Expression, references);
        }

        if (statement.From?.Source is SubquerySource fromSubquery)
        {
            WalkStatement(fromSubquery.Query, references);
        }

        if (statement.Joins is not null)
        {
            foreach (JoinClause join in statement.Joins)
            {
                if (join.OnCondition is not null)
                {
                    Walk(join.OnCondition, references);
                }
                if (join.Source is SubquerySource joinSubquery)
                {
                    WalkStatement(joinSubquery.Query, references);
                }
            }
        }

        if (statement.Where is not null)
        {
            Walk(statement.Where, references);
        }

        if (statement.GroupBy is not null)
        {
            foreach (Expression expression in statement.GroupBy.Expressions)
            {
                Walk(expression, references);
            }
        }

        if (statement.Having is not null)
        {
            Walk(statement.Having, references);
        }

        if (statement.Qualify is not null)
        {
            Walk(statement.Qualify, references);
        }

        if (statement.OrderBy is not null)
        {
            foreach (OrderByItem item in statement.OrderBy.Items)
            {
                Walk(item.Expression, references);
            }
        }

        if (statement.LetBindings is not null)
        {
            foreach (LetBinding binding in statement.LetBindings)
            {
                Walk(binding.Expression, references);
            }
        }

        if (statement.Assertions is not null)
        {
            foreach (AssertClause assertion in statement.Assertions)
            {
                Walk(assertion.Predicate, references);
                if (assertion.Message is not null)
                {
                    Walk(assertion.Message, references);
                }
            }
        }

        if (statement.Limit is not null)
        {
            Walk(statement.Limit, references);
        }

        if (statement.Offset is not null)
        {
            Walk(statement.Offset, references);
        }
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

            case ScanExpression scan:
                // INIT, PARTITION BY, ORDER BY are evaluated against the source
                // row only — they cannot see accumulators, so collect refs from
                // them directly.
                foreach (Expression init in scan.InitExpressions)
                {
                    Walk(init, references);
                }
                if (scan.Window.PartitionBy is not null)
                {
                    foreach (Expression partition in scan.Window.PartitionBy)
                    {
                        Walk(partition, references);
                    }
                }
                if (scan.Window.OrderBy is not null)
                {
                    foreach (OrderByItem orderByItem in scan.Window.OrderBy)
                    {
                        Walk(orderByItem.Expression, references);
                    }
                }
                // BODY expressions reference both source columns and accumulator
                // names. The accumulator names are SCAN-scoped (not real columns)
                // and must not leak into projection-pushdown's required-columns set.
                HashSet<(string?, string)> bodyRefs = new();
                foreach (Expression body in scan.BodyExpressions)
                {
                    Walk(body, bodyRefs);
                }
                foreach ((string? tableName, string columnName) bodyRef in bodyRefs)
                {
                    if (bodyRef.tableName is null
                        && scan.AccumulatorNames.Contains(bodyRef.columnName))
                    {
                        continue;
                    }
                    references.Add(bodyRef);
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

            case SubqueryExpression subquery:
                // Walk the inner statement so correlated (outer-scoped) refs
                // surface for projection pushdown. Refs qualified to the
                // subquery's own tables are still collected but get filtered
                // out by ComputeRequiredColumns' per-alias filter.
                WalkStatement(subquery.Query, references);
                break;

            case InSubqueryExpression inSubquery:
                Walk(inSubquery.Expression, references);
                WalkStatement(inSubquery.Query, references);
                break;

            case ExistsExpression exists:
                WalkStatement(exists.Query, references);
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
