using System.Text;
using Axon.QueryEngine.Execution.Operators;
using Axon.QueryEngine.Parsing.Ast;

namespace Axon.QueryEngine.Execution;

/// <summary>
/// Produces a static <see cref="ExplainPlanNode"/> tree from an operator tree,
/// describing the execution plan structure and emitting warnings about
/// potential performance issues.
/// </summary>
public static class QueryExplainer
{
    /// <summary>
    /// Builds an explain plan tree from the root operator.
    /// </summary>
    /// <param name="root">The root of the operator tree.</param>
    /// <returns>An explain plan node tree describing the plan.</returns>
    public static ExplainPlanNode Explain(IQueryOperator root)
    {
        return BuildNode(root);
    }

    private static ExplainPlanNode BuildNode(IQueryOperator op)
    {
        return op switch
        {
            InstrumentedOperator instrumented => BuildNode(instrumented.Inner),
            ScanOperator scan => BuildScanNode(scan),
            FilterOperator filter => BuildFilterNode(filter),
            ProjectOperator project => BuildProjectNode(project),
            JoinOperator join => BuildJoinNode(join),
            OrderByOperator orderBy => BuildOrderByNode(orderBy),
            LimitOperator limit => BuildLimitNode(limit),
            AliasOperator alias => BuildAliasNode(alias),
            SubqueryOperator subquery => BuildSubqueryNode(subquery),
            _ => new ExplainPlanNode
            {
                OperatorName = op.GetType().Name,
                Details = "unknown operator",
            },
        };
    }

    private static ExplainPlanNode BuildScanNode(ScanOperator scan)
    {
        string tableName = scan.Descriptor.Name;
        string provider = scan.Descriptor.Provider;
        string columns = scan.RequiredColumns is not null
            ? string.Join(", ", scan.RequiredColumns)
            : "*";

        return new ExplainPlanNode
        {
            OperatorName = "Scan",
            Details = $"table: {tableName}, provider: {provider}, columns: [{columns}]",
        };
    }

    private static ExplainPlanNode BuildFilterNode(FilterOperator filter)
    {
        ExplainPlanNode node = new()
        {
            OperatorName = "Filter",
            Details = $"predicate: {FormatExpression(filter.Predicate)}",
            Children = { BuildNode(filter.Source) },
        };

        // Warn about LIKE predicates (no index, full scan).
        if (ContainsLike(filter.Predicate))
        {
            node.Warnings.Add("LIKE predicate requires full scan of input rows.");
        }

        return node;
    }

    private static ExplainPlanNode BuildProjectNode(ProjectOperator project)
    {
        List<string> columnNames = [];
        foreach (SelectColumn column in project.Columns)
        {
            if (column.Alias is not null)
            {
                columnNames.Add($"{FormatExpression(column.Expression)} AS {column.Alias}");
            }
            else
            {
                columnNames.Add(FormatExpression(column.Expression));
            }
        }

        return new ExplainPlanNode
        {
            OperatorName = "Project",
            Details = string.Join(", ", columnNames),
            Children = { BuildNode(project.Source) },
        };
    }

    private static ExplainPlanNode BuildJoinNode(JoinOperator join)
    {
        string joinType = join.Type switch
        {
            JoinType.Inner => "INNER",
            JoinType.Left => "LEFT",
            JoinType.Right => "RIGHT",
            JoinType.FullOuter => "FULL OUTER",
            JoinType.Cross => "CROSS",
            _ => join.Type.ToString(),
        };

        string condition = join.OnCondition is not null
            ? $", on: {FormatExpression(join.OnCondition)}"
            : "";

        // Determine the actual join strategy using JoinKeyExtractor.
        string strategy;
        if (join.Type == JoinType.Cross)
        {
            strategy = "nested-loop";
        }
        else
        {
            JoinKeyExtractionResult? extraction = JoinKeyExtractor.TryExtract(join.OnCondition);
            if (extraction is not null)
            {
                strategy = extraction.Residual is not null ? "hash+filter" : "hash";
            }
            else
            {
                strategy = "nested-loop";
            }
        }

        ExplainPlanNode leftChild = BuildNode(join.Left);
        leftChild.ChildLabel = "probe";

        ExplainPlanNode rightChild = BuildNode(join.Right);
        rightChild.ChildLabel = "build";

        ExplainPlanNode node = new()
        {
            OperatorName = $"{joinType} Join",
            Details = $"strategy: {strategy}{condition}",
            Children = { leftChild, rightChild },
        };

        // Warn about cross joins (can produce very large output).
        if (join.Type == JoinType.Cross)
        {
            node.Warnings.Add("CROSS JOIN produces a cartesian product; output size = left × right.");
        }

        // Warn about full outer joins (both sides fully materialized).
        if (join.Type == JoinType.FullOuter)
        {
            node.Warnings.Add("FULL OUTER JOIN materializes both sides in memory.");
        }

        // Warn about nested-loop join performance.
        if (strategy == "nested-loop" && join.Type != JoinType.Cross)
        {
            node.Warnings.Add(
                "Nested-loop join has O(n*m) complexity. Consider rewriting the ON condition as an equi-join.");
        }

        return node;
    }

    private static ExplainPlanNode BuildOrderByNode(OrderByOperator orderBy)
    {
        List<string> items = [];
        foreach (OrderByItem item in orderBy.OrderByItems)
        {
            string direction = item.Direction == SortDirection.Ascending ? "ASC" : "DESC";
            items.Add($"{FormatExpression(item.Expression)} {direction}");
        }

        ExplainPlanNode node = new()
        {
            OperatorName = "Sort",
            Details = string.Join(", ", items),
            Children = { BuildNode(orderBy.Source) },
        };

        node.Warnings.Add("ORDER BY materializes all input rows for sorting.");

        return node;
    }

    private static ExplainPlanNode BuildLimitNode(LimitOperator limit)
    {
        string details = limit.Offset > 0
            ? $"limit: {limit.Limit}, offset: {limit.Offset}"
            : $"limit: {limit.Limit}";

        return new ExplainPlanNode
        {
            OperatorName = "Limit",
            Details = details,
            Children = { BuildNode(limit.Source) },
        };
    }

    private static ExplainPlanNode BuildAliasNode(AliasOperator alias)
    {
        return new ExplainPlanNode
        {
            OperatorName = "Alias",
            Details = $"as: {alias.Alias}",
            Children = { BuildNode(alias.Source) },
        };
    }

    private static ExplainPlanNode BuildSubqueryNode(SubqueryOperator subquery)
    {
        return new ExplainPlanNode
        {
            OperatorName = "Subquery",
            Details = $"alias: {subquery.Alias}",
            Children = { BuildNode(subquery.InnerOperator) },
        };
    }

    // ──────────────── Expression formatting ────────────────

    /// <summary>
    /// Formats an expression as a human-readable SQL-like string.
    /// </summary>
    /// <param name="expression">The expression to format.</param>
    /// <returns>A formatted string representation.</returns>
    public static string FormatExpression(Expression expression)
    {
        return expression switch
        {
            ColumnReference col => col.TableName is not null
                ? $"{col.TableName}.{col.ColumnName}"
                : col.ColumnName,
            LiteralExpression lit => FormatLiteral(lit.Value),
            BinaryExpression bin => $"{FormatExpression(bin.Left)} {FormatBinaryOp(bin.Operator)} {FormatExpression(bin.Right)}",
            UnaryExpression unary => FormatUnary(unary),
            FunctionCallExpression func => $"{func.FunctionName}({string.Join(", ", func.Arguments.Select(FormatExpression))})",
            InExpression inExpr => $"{FormatExpression(inExpr.Expression)} {(inExpr.Negated ? "NOT IN" : "IN")} ({string.Join(", ", inExpr.Values.Select(FormatExpression))})",
            BetweenExpression between => $"{FormatExpression(between.Expression)} {(between.Negated ? "NOT BETWEEN" : "BETWEEN")} {FormatExpression(between.Low)} AND {FormatExpression(between.High)}",
            IsNullExpression isNull => $"{FormatExpression(isNull.Expression)} {(isNull.Negated ? "IS NOT NULL" : "IS NULL")}",
            CastExpression cast => $"CAST({FormatExpression(cast.Expression)} AS {cast.TargetType})",
            _ => expression.ToString() ?? "?",
        };
    }

    private static string FormatLiteral(object? value)
    {
        return value switch
        {
            null => "NULL",
            string s => $"'{s}'",
            bool b => b ? "TRUE" : "FALSE",
            _ => value.ToString() ?? "NULL",
        };
    }

    private static string FormatBinaryOp(BinaryOperator op)
    {
        return op switch
        {
            BinaryOperator.Add => "+",
            BinaryOperator.Subtract => "-",
            BinaryOperator.Multiply => "*",
            BinaryOperator.Divide => "/",
            BinaryOperator.Equal => "=",
            BinaryOperator.NotEqual => "!=",
            BinaryOperator.LessThan => "<",
            BinaryOperator.GreaterThan => ">",
            BinaryOperator.LessThanOrEqual => "<=",
            BinaryOperator.GreaterThanOrEqual => ">=",
            BinaryOperator.And => "AND",
            BinaryOperator.Or => "OR",
            BinaryOperator.Like => "LIKE",
            _ => op.ToString(),
        };
    }

    private static string FormatUnary(UnaryExpression unary)
    {
        return unary.Operator switch
        {
            UnaryOperator.Not => $"NOT {FormatExpression(unary.Operand)}",
            UnaryOperator.Negate => $"-{FormatExpression(unary.Operand)}",
            _ => $"{unary.Operator} {FormatExpression(unary.Operand)}",
        };
    }

    private static bool ContainsLike(Expression expression)
    {
        return expression switch
        {
            BinaryExpression bin => bin.Operator == BinaryOperator.Like
                || ContainsLike(bin.Left)
                || ContainsLike(bin.Right),
            UnaryExpression unary => ContainsLike(unary.Operand),
            _ => false,
        };
    }
}
