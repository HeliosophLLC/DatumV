using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

/// <summary>
/// Replaces <see cref="ParameterExpression"/> nodes in a parsed AST with
/// concrete <see cref="LiteralExpression"/> values from a caller-supplied
/// binding dictionary. This substitution happens before query planning so
/// the optimizer sees only literal values and all existing optimizations
/// (predicate pushdown, index seek, statistics pruning) work unchanged.
/// </summary>
public static class ParameterBinder
{
    /// <summary>
    /// Binds named parameters in a <see cref="QueryExpression"/> to concrete values.
    /// For a <see cref="SelectQueryExpression"/>, binds the inner statement.
    /// For a <see cref="CompoundQueryExpression"/>, recursively binds both branches.
    /// </summary>
    /// <param name="query">The parsed query expression containing parameter references.</param>
    /// <param name="parameters">A dictionary mapping parameter names to values.</param>
    /// <returns>A new query expression with all parameters substituted.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when a parameter referenced in the query is not present in <paramref name="parameters"/>,
    /// or when <paramref name="parameters"/> contains names not referenced by the query.
    /// </exception>
    public static QueryExpression Bind(
        QueryExpression query,
        IReadOnlyDictionary<string, DataValue> parameters)
    {
        HashSet<string> referenced = CollectParameterNames(query);

        if (referenced.Count == 0 && parameters.Count == 0)
        {
            return query;
        }

        // Validate: all referenced parameters must be supplied.
        foreach (string name in referenced)
        {
            if (!parameters.ContainsKey(name))
            {
                throw new ArgumentException(
                    $"Query references parameter '${name}' but no value was supplied.");
            }
        }

        // Validate: all supplied parameters must be referenced.
        foreach (string name in parameters.Keys)
        {
            if (!referenced.Contains(name))
            {
                throw new ArgumentException(
                    $"Parameter '${name}' was supplied but is not referenced in the query.");
            }
        }

        return BindQueryExpression(query, parameters);
    }

    /// <summary>
    /// Binds named parameters in a <see cref="SelectStatement"/> to concrete values.
    /// Returns a new <see cref="SelectStatement"/> with all <see cref="ParameterExpression"/>
    /// nodes replaced by <see cref="LiteralExpression"/> nodes.
    /// </summary>
    /// <param name="statement">The parsed AST containing parameter references.</param>
    /// <param name="parameters">A dictionary mapping parameter names to values.</param>
    /// <returns>A new statement with all parameters substituted.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when a parameter referenced in the query is not present in <paramref name="parameters"/>,
    /// or when <paramref name="parameters"/> contains names not referenced by the query.
    /// </exception>
    public static SelectStatement Bind(
        SelectStatement statement,
        IReadOnlyDictionary<string, DataValue> parameters)
    {
        HashSet<string> referenced = CollectParameterNames(statement);

        if (referenced.Count == 0 && parameters.Count == 0)
        {
            return statement;
        }

        // Validate: all referenced parameters must be supplied.
        foreach (string name in referenced)
        {
            if (!parameters.ContainsKey(name))
            {
                throw new ArgumentException(
                    $"Query references parameter '${name}' but no value was supplied.");
            }
        }

        // Validate: all supplied parameters must be referenced.
        foreach (string name in parameters.Keys)
        {
            if (!referenced.Contains(name))
            {
                throw new ArgumentException(
                    $"Parameter '${name}' was supplied but is not referenced in the query.");
            }
        }

        return BindStatement(statement, parameters);
    }

    /// <summary>
    /// Collects all distinct parameter names referenced in a query expression,
    /// recursively traversing compound set operation branches.
    /// </summary>
    /// <param name="query">The parsed query expression to scan.</param>
    /// <returns>A set of parameter names (without the <c>$</c> prefix).</returns>
    public static HashSet<string> CollectParameterNames(QueryExpression query)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        CollectFromQueryExpression(query, names);
        return names;
    }

    /// <summary>
    /// Collects all distinct parameter names referenced in a statement.
    /// </summary>
    /// <param name="statement">The parsed AST to scan.</param>
    /// <returns>A set of parameter names (without the <c>$</c> prefix).</returns>
    public static HashSet<string> CollectParameterNames(SelectStatement statement)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        CollectFromStatement(statement, names);
        return names;
    }

    // ───────────────────── Statement binding ─────────────────────

    private static SelectStatement BindStatement(
        SelectStatement statement,
        IReadOnlyDictionary<string, DataValue> parameters)
    {
        IReadOnlyList<SelectColumn> columns = BindSelectColumns(statement.Columns, parameters);
        Expression? where = statement.Where is not null ? BindExpression(statement.Where, parameters) : null;
        Expression? having = statement.Having is not null ? BindExpression(statement.Having, parameters) : null;
        Expression? qualify = statement.Qualify is not null ? BindExpression(statement.Qualify, parameters) : null;

        IReadOnlyList<JoinClause>? joins = null;
        if (statement.Joins is not null)
        {
            JoinClause[] boundJoins = new JoinClause[statement.Joins.Count];
            for (int i = 0; i < statement.Joins.Count; i++)
            {
                JoinClause join = statement.Joins[i];
                Expression? onCondition = join.OnCondition is not null
                    ? BindExpression(join.OnCondition, parameters)
                    : null;
                TableSource source = BindTableSource(join.Source, parameters);
                boundJoins[i] = new JoinClause(join.Type, source, onCondition);
            }

            joins = boundJoins;
        }

        FromClause? from = statement.From is not null
            ? new FromClause(BindTableSource(statement.From.Source, parameters))
            : null;

        GroupByClause? groupBy = null;
        if (statement.GroupBy is not null)
        {
            Expression[] groupExpressions = new Expression[statement.GroupBy.Expressions.Count];
            for (int i = 0; i < statement.GroupBy.Expressions.Count; i++)
            {
                groupExpressions[i] = BindExpression(statement.GroupBy.Expressions[i], parameters);
            }

            groupBy = new GroupByClause(groupExpressions);
        }

        OrderByClause? orderBy = null;
        if (statement.OrderBy is not null)
        {
            OrderByItem[] orderItems = new OrderByItem[statement.OrderBy.Items.Count];
            for (int i = 0; i < statement.OrderBy.Items.Count; i++)
            {
                OrderByItem item = statement.OrderBy.Items[i];
                orderItems[i] = new OrderByItem(BindExpression(item.Expression, parameters), item.Direction);
            }

            orderBy = new OrderByClause(orderItems);
        }

        return new SelectStatement(
            columns,
            from,
            statement.Into,
            joins,
            where,
            groupBy,
            having,
            qualify,
            orderBy,
            statement.Limit,
            statement.Offset);
    }

    private static IReadOnlyList<SelectColumn> BindSelectColumns(
        IReadOnlyList<SelectColumn> columns,
        IReadOnlyDictionary<string, DataValue> parameters)
    {
        SelectColumn[] result = new SelectColumn[columns.Count];
        for (int i = 0; i < columns.Count; i++)
        {
            SelectColumn column = columns[i];
            if (column is SelectAllColumns or SelectTableColumns)
            {
                result[i] = column;
            }
            else
            {
                result[i] = new SelectColumn(BindExpression(column.Expression, parameters), column.Alias);
            }
        }

        return result;
    }

    private static TableSource BindTableSource(
        TableSource source,
        IReadOnlyDictionary<string, DataValue> parameters)
    {
        if (source is FunctionSource functionSource)
        {
            Expression[] arguments = new Expression[functionSource.Arguments.Count];
            for (int i = 0; i < functionSource.Arguments.Count; i++)
            {
                arguments[i] = BindExpression(functionSource.Arguments[i], parameters);
            }

            return new FunctionSource(functionSource.FunctionName, arguments, functionSource.Alias, functionSource.Span);
        }

        if (source is SubquerySource subquerySource)
        {
            return new SubquerySource(BindStatement(subquerySource.Query, parameters), subquerySource.Alias);
        }

        return source;
    }

    // ───────────────────── Expression binding ─────────────────────

    private static Expression BindExpression(
        Expression expression,
        IReadOnlyDictionary<string, DataValue> parameters)
    {
        return expression switch
        {
            ParameterExpression parameter => ToLiteral(parameter, parameters),
            BinaryExpression binary => new BinaryExpression(
                BindExpression(binary.Left, parameters),
                binary.Operator,
                BindExpression(binary.Right, parameters)),
            UnaryExpression unary => new UnaryExpression(
                unary.Operator,
                BindExpression(unary.Operand, parameters)),
            FunctionCallExpression function => BindFunctionCall(function, parameters),
            InExpression inExpr => BindInExpression(inExpr, parameters),
            BetweenExpression between => new BetweenExpression(
                BindExpression(between.Expression, parameters),
                BindExpression(between.Low, parameters),
                BindExpression(between.High, parameters),
                between.Negated),
            IsNullExpression isNull => new IsNullExpression(
                BindExpression(isNull.Expression, parameters),
                isNull.Negated),
            CastExpression cast => new CastExpression(
                BindExpression(cast.Expression, parameters),
                cast.TargetType,
                cast.Span),
            CaseExpression caseExpr => BindCaseExpression(caseExpr, parameters),
            SubqueryExpression subquery => new SubqueryExpression(
                BindStatement(subquery.Query, parameters)),
            // Leaf nodes with no children: LiteralExpression, ColumnReference, ErrorExpression
            _ => expression,
        };
    }

    private static LiteralExpression ToLiteral(
        ParameterExpression parameter,
        IReadOnlyDictionary<string, DataValue> parameters)
    {
        DataValue value = parameters[parameter.Name];

        if (value.IsNull)
        {
            return new LiteralExpression(null);
        }

        return value.Kind switch
        {
            DataKind.Scalar => new LiteralExpression((double)value.AsScalar()),
            DataKind.String => new LiteralExpression(value.AsString()),
            DataKind.Boolean => new LiteralExpression(value.AsBoolean()),
            _ => new LiteralExpression(value.AsString()),
        };
    }

    private static FunctionCallExpression BindFunctionCall(
        FunctionCallExpression function,
        IReadOnlyDictionary<string, DataValue> parameters)
    {
        Expression[] arguments = new Expression[function.Arguments.Count];
        for (int i = 0; i < function.Arguments.Count; i++)
        {
            arguments[i] = BindExpression(function.Arguments[i], parameters);
        }

        return new FunctionCallExpression(function.FunctionName, arguments, function.OrderBy, function.Distinct, function.Span);
    }

    private static InExpression BindInExpression(
        InExpression inExpr,
        IReadOnlyDictionary<string, DataValue> parameters)
    {
        Expression[] values = new Expression[inExpr.Values.Count];
        for (int i = 0; i < inExpr.Values.Count; i++)
        {
            values[i] = BindExpression(inExpr.Values[i], parameters);
        }

        return new InExpression(
            BindExpression(inExpr.Expression, parameters),
            values,
            inExpr.Negated);
    }

    private static CaseExpression BindCaseExpression(
        CaseExpression caseExpr,
        IReadOnlyDictionary<string, DataValue> parameters)
    {
        Expression? operand = caseExpr.Operand is not null
            ? BindExpression(caseExpr.Operand, parameters)
            : null;

        WhenClause[] whenClauses = new WhenClause[caseExpr.WhenClauses.Count];
        for (int i = 0; i < caseExpr.WhenClauses.Count; i++)
        {
            WhenClause clause = caseExpr.WhenClauses[i];
            whenClauses[i] = new WhenClause(
                BindExpression(clause.Condition, parameters),
                BindExpression(clause.Result, parameters));
        }

        Expression? elseResult = caseExpr.ElseResult is not null
            ? BindExpression(caseExpr.ElseResult, parameters)
            : null;

        return new CaseExpression(operand, whenClauses, elseResult, caseExpr.Span);
    }

    // ───────────────────── Parameter name collection ─────────────────────

    private static void CollectFromStatement(SelectStatement statement, HashSet<string> names)
    {
        foreach (SelectColumn column in statement.Columns)
        {
            if (column is not (SelectAllColumns or SelectTableColumns))
            {
                CollectFromExpression(column.Expression, names);
            }
        }

        if (statement.From is not null)
        {
            CollectFromTableSource(statement.From.Source, names);
        }

        if (statement.Where is not null)
        {
            CollectFromExpression(statement.Where, names);
        }

        if (statement.Having is not null)
        {
            CollectFromExpression(statement.Having, names);
        }

        if (statement.Qualify is not null)
        {
            CollectFromExpression(statement.Qualify, names);
        }

        if (statement.Joins is not null)
        {
            foreach (JoinClause join in statement.Joins)
            {
                CollectFromTableSource(join.Source, names);
                if (join.OnCondition is not null)
                {
                    CollectFromExpression(join.OnCondition, names);
                }
            }
        }

        if (statement.GroupBy is not null)
        {
            foreach (Expression expression in statement.GroupBy.Expressions)
            {
                CollectFromExpression(expression, names);
            }
        }

        if (statement.OrderBy is not null)
        {
            foreach (OrderByItem item in statement.OrderBy.Items)
            {
                CollectFromExpression(item.Expression, names);
            }
        }
    }

    private static void CollectFromTableSource(TableSource source, HashSet<string> names)
    {
        if (source is FunctionSource functionSource)
        {
            foreach (Expression argument in functionSource.Arguments)
            {
                CollectFromExpression(argument, names);
            }
        }
        else if (source is SubquerySource subquerySource)
        {
            CollectFromStatement(subquerySource.Query, names);
        }
    }

    private static void CollectFromExpression(Expression expression, HashSet<string> names)
    {
        switch (expression)
        {
            case ParameterExpression parameter:
                names.Add(parameter.Name);
                break;
            case BinaryExpression binary:
                CollectFromExpression(binary.Left, names);
                CollectFromExpression(binary.Right, names);
                break;
            case UnaryExpression unary:
                CollectFromExpression(unary.Operand, names);
                break;
            case FunctionCallExpression function:
                foreach (Expression argument in function.Arguments)
                {
                    CollectFromExpression(argument, names);
                }

                break;
            case InExpression inExpr:
                CollectFromExpression(inExpr.Expression, names);
                foreach (Expression value in inExpr.Values)
                {
                    CollectFromExpression(value, names);
                }

                break;
            case BetweenExpression between:
                CollectFromExpression(between.Expression, names);
                CollectFromExpression(between.Low, names);
                CollectFromExpression(between.High, names);
                break;
            case IsNullExpression isNull:
                CollectFromExpression(isNull.Expression, names);
                break;
            case CastExpression cast:
                CollectFromExpression(cast.Expression, names);
                break;
            case CaseExpression caseExpr:
                if (caseExpr.Operand is not null)
                {
                    CollectFromExpression(caseExpr.Operand, names);
                }

                foreach (WhenClause clause in caseExpr.WhenClauses)
                {
                    CollectFromExpression(clause.Condition, names);
                    CollectFromExpression(clause.Result, names);
                }

                if (caseExpr.ElseResult is not null)
                {
                    CollectFromExpression(caseExpr.ElseResult, names);
                }

                break;
            case SubqueryExpression subquery:
                CollectFromStatement(subquery.Query, names);
                break;
            case InSubqueryExpression inSubquery:
                CollectFromExpression(inSubquery.Expression, names);
                CollectFromStatement(inSubquery.Query, names);
                break;
            case ExistsExpression exists:
                CollectFromStatement(exists.Query, names);
                break;
        }
    }

    private static void CollectFromQueryExpression(QueryExpression query, HashSet<string> names)
    {
        switch (query)
        {
            case SelectQueryExpression select:
                CollectFromStatement(select.Statement, names);
                break;
            case CompoundQueryExpression compound:
                CollectFromQueryExpression(compound.Left, names);
                CollectFromQueryExpression(compound.Right, names);
                if (compound.OrderBy is not null)
                {
                    foreach (OrderByItem item in compound.OrderBy.Items)
                    {
                        CollectFromExpression(item.Expression, names);
                    }
                }

                break;
        }
    }

    private static QueryExpression BindQueryExpression(
        QueryExpression query,
        IReadOnlyDictionary<string, DataValue> parameters)
    {
        return query switch
        {
            SelectQueryExpression select =>
                new SelectQueryExpression(BindStatement(select.Statement, parameters)),
            CompoundQueryExpression compound =>
                compound with
                {
                    Left = BindQueryExpression(compound.Left, parameters),
                    Right = BindQueryExpression(compound.Right, parameters),
                    OrderBy = compound.OrderBy is not null
                        ? BindOrderByClause(compound.OrderBy, parameters)
                        : null,
                },
            _ => query,
        };
    }

    private static OrderByClause BindOrderByClause(
        OrderByClause orderBy,
        IReadOnlyDictionary<string, DataValue> parameters)
    {
        OrderByItem[] orderItems = new OrderByItem[orderBy.Items.Count];
        for (int i = 0; i < orderBy.Items.Count; i++)
        {
            OrderByItem item = orderBy.Items[i];
            orderItems[i] = new OrderByItem(BindExpression(item.Expression, parameters), item.Direction);
        }

        return new OrderByClause(orderItems);
    }
}
