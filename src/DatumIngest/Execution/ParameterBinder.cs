using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution;

/// <summary>
/// Replaces <see cref="ParameterExpression"/> nodes in a parsed AST with
/// concrete <see cref="LiteralExpression"/> values from a caller-supplied
/// binding dictionary. This substitution happens before query planning so
/// the optimizer sees only literal values and all existing optimizations
/// (predicate pushdown, index seek, statistics pruning) work unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Load-bearing invariant: bind before plan.</strong> Several
/// pieces of infrastructure assume that by the time the planner / resolver
/// runs, every <c>$parameter</c> has already been rewritten into a
/// <see cref="LiteralExpression"/>. Most visibly, the constant-aware
/// <c>ITableValuedFunction.ValidateArguments</c> overload (used by TVFs
/// whose output schema is determined by argument values — FITS bintables,
/// HDF5 datasets, etc.) treats <c>$archive</c> and <c>'path/to/foo.h5'</c>
/// uniformly because both are LiteralExpressions after this pass runs. If
/// a future change introduces plan caching across multiple bind-and-execute
/// calls (so the same plan is reused with different parameter values), the
/// TVF constant-args path must either opt out of caching or key its
/// per-call peek on the bound values.
/// </para>
/// </remarks>
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
        IReadOnlyDictionary<string, ParameterValue> parameters)
    {
        HashSet<string> referenced = CollectParameterNames(query);

        if (referenced.Count == 0 && parameters.Count == 0)
        {
            return query;
        }

        ValidateParameterUsage(referenced, parameters.Keys);
        return BindQueryExpression(query, parameters);
    }

    /// <summary>
    /// Convenience overload that wraps every <see cref="DataValue"/> in
    /// an <see cref="InlineParameter"/>. Preserves the original
    /// <c>Bind(QueryExpression, dict&lt;string, DataValue&gt;)</c> contract for
    /// callers that don't need binary parameters.
    /// </summary>
    public static QueryExpression Bind(
        QueryExpression query,
        IReadOnlyDictionary<string, DataValue> parameters)
        => Bind(query, AsParameterValues(parameters));

    /// <summary>
    /// Binds named parameters in a top-level <see cref="Statement"/> to
    /// concrete values. Dispatches across every statement subtype that
    /// can carry an <see cref="Expression"/> or nested
    /// <see cref="Statement"/> / <see cref="QueryExpression"/>; identity
    /// for statements that hold no parameter-bearing nodes (DDL,
    /// BREAK/CONTINUE, etc.).
    /// </summary>
    /// <param name="statement">The parsed top-level statement.</param>
    /// <param name="parameters">A dictionary mapping parameter names to values.</param>
    /// <returns>A new statement with all parameters substituted.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when a parameter referenced by the statement is not present
    /// in <paramref name="parameters"/>, or when <paramref name="parameters"/>
    /// contains names not referenced by the statement.
    /// </exception>
    public static Statement Bind(
        Statement statement,
        IReadOnlyDictionary<string, ParameterValue> parameters)
    {
        HashSet<string> referenced = CollectParameterNames(statement);
        ValidateParameterUsage(referenced, parameters.Keys);
        return BindAnyStatement(statement, parameters);
    }

    /// <summary>
    /// Convenience overload that wraps every <see cref="DataValue"/> in
    /// an <see cref="InlineParameter"/>.
    /// </summary>
    public static Statement Bind(
        Statement statement,
        IReadOnlyDictionary<string, DataValue> parameters)
        => Bind(statement, AsParameterValues(parameters));

    /// <summary>
    /// Binds named parameters across a batch of top-level statements. The
    /// union of referenced names across the batch is validated against
    /// <paramref name="parameters"/> exactly once — individual statements
    /// in the batch may legitimately not reference every supplied
    /// parameter, so per-statement strict validation would be wrong.
    /// </summary>
    /// <summary>
    /// Convenience overload that wraps every <see cref="DataValue"/> in
    /// an <see cref="InlineParameter"/>.
    /// </summary>
    public static IReadOnlyList<Statement> Bind(
        IReadOnlyList<Statement> statements,
        IReadOnlyDictionary<string, DataValue> parameters)
        => Bind(statements, AsParameterValues(parameters));

    /// <summary>
    /// Binds named parameters across a batch of top-level statements
    /// using the <see cref="ParameterValue"/> shape that supports both
    /// inline scalars and binary references for multipart payloads.
    /// </summary>
    public static IReadOnlyList<Statement> Bind(
        IReadOnlyList<Statement> statements,
        IReadOnlyDictionary<string, ParameterValue> parameters)
    {
        HashSet<string> referenced = new(StringComparer.OrdinalIgnoreCase);
        foreach (Statement s in statements)
        {
            CollectFromAnyStatement(s, referenced);
        }
        ValidateParameterUsage(referenced, parameters.Keys);
        if (parameters.Count == 0) return statements;

        Statement[] bound = new Statement[statements.Count];
        for (int i = 0; i < statements.Count; i++)
        {
            bound[i] = BindAnyStatement(statements[i], parameters);
        }
        return bound;
    }

    /// <summary>
    /// Collects all distinct parameter names referenced anywhere in a
    /// top-level statement, recursing into nested statements / queries /
    /// expressions.
    /// </summary>
    public static HashSet<string> CollectParameterNames(Statement statement)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        CollectFromAnyStatement(statement, names);
        return names;
    }

    private static void ValidateParameterUsage(
        HashSet<string> referenced,
        IEnumerable<string> suppliedNames)
    {
        HashSet<string> supplied = new(suppliedNames, StringComparer.OrdinalIgnoreCase);
        foreach (string name in referenced)
        {
            if (!supplied.Contains(name))
            {
                throw new ArgumentException(
                    $"Query references parameter '${name}' but no value was supplied.");
            }
        }
        foreach (string name in supplied)
        {
            if (!referenced.Contains(name))
            {
                throw new ArgumentException(
                    $"Parameter '${name}' was supplied but is not referenced in the query.");
            }
        }
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
        IReadOnlyDictionary<string, ParameterValue> parameters)
    {
        HashSet<string> referenced = CollectParameterNames(statement);

        if (referenced.Count == 0 && parameters.Count == 0)
        {
            return statement;
        }

        ValidateParameterUsage(referenced, parameters.Keys);
        return BindStatement(statement, parameters);
    }

    /// <summary>
    /// Convenience overload that wraps every <see cref="DataValue"/> in
    /// an <see cref="InlineParameter"/>. Preserves the original
    /// <c>Bind(SelectStatement, dict&lt;string, DataValue&gt;)</c> contract.
    /// </summary>
    public static SelectStatement Bind(
        SelectStatement statement,
        IReadOnlyDictionary<string, DataValue> parameters)
        => Bind(statement, AsParameterValues(parameters));

    private static IReadOnlyDictionary<string, ParameterValue> AsParameterValues(
        IReadOnlyDictionary<string, DataValue> raw)
    {
        Dictionary<string, ParameterValue> result =
            new(raw.Count, StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, DataValue> kvp in raw)
        {
            result[kvp.Key] = new InlineParameter(kvp.Value);
        }
        return result;
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
        IReadOnlyDictionary<string, ParameterValue> parameters)
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
                boundJoins[i] = new JoinClause(join.Type, source, onCondition, join.IsLateral);
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

        // Recurse into CTE bodies and LET binding expressions so $parameters
        // inside them substitute too. Without this, `WITH foo AS (… $archive …)`
        // would leave the parameter unbound and trip the planner.
        IReadOnlyList<CommonTableExpression>? commonTableExpressions = null;
        if (statement.CommonTableExpressions is not null)
        {
            CommonTableExpression[] bound = new CommonTableExpression[statement.CommonTableExpressions.Count];
            for (int i = 0; i < statement.CommonTableExpressions.Count; i++)
            {
                CommonTableExpression cte = statement.CommonTableExpressions[i];
                bound[i] = cte with
                {
                    Body = BindQueryExpression(cte.Body, parameters),
                    RecursiveQuery = cte.RecursiveQuery is not null
                        ? BindStatement(cte.RecursiveQuery, parameters)
                        : null,
                };
            }
            commonTableExpressions = bound;
        }

        IReadOnlyList<LetBinding>? letBindings = null;
        if (statement.LetBindings is not null)
        {
            LetBinding[] bound = new LetBinding[statement.LetBindings.Count];
            for (int i = 0; i < statement.LetBindings.Count; i++)
            {
                LetBinding binding = statement.LetBindings[i];
                bound[i] = binding with { Expression = BindExpression(binding.Expression, parameters) };
            }
            letBindings = bound;
        }

        // `with` preserves fields ParameterBinder doesn't touch (Distinct,
        // CrossValidate, …). A positional ctor here would silently drop
        // trailing record fields whenever the AST grows — historical instance:
        // WITH-clause CTEs vanished post-bind because the positional call
        // passed only the leading 13 args.
        return statement with
        {
            Columns = columns,
            From = from,
            Joins = joins,
            Where = where,
            GroupBy = groupBy,
            Having = having,
            Qualify = qualify,
            OrderBy = orderBy,
            CommonTableExpressions = commonTableExpressions,
            LetBindings = letBindings,
        };
    }

    private static IReadOnlyList<SelectColumn> BindSelectColumns(
        IReadOnlyList<SelectColumn> columns,
        IReadOnlyDictionary<string, ParameterValue> parameters)
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
        IReadOnlyDictionary<string, ParameterValue> parameters)
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
        IReadOnlyDictionary<string, ParameterValue> parameters)
    {
        return expression switch
        {
            ParameterExpression parameter => ToLiteral(parameter, parameters),
            BinaryExpression binary => new BinaryExpression(
                BindExpression(binary.Left, parameters),
                binary.Operator,
                BindExpression(binary.Right, parameters)),
            LikeExpression like => new LikeExpression(
                BindExpression(like.Expression, parameters),
                BindExpression(like.Pattern, parameters),
                BindExpression(like.EscapeCharacter, parameters),
                like.CaseInsensitive),
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
        IReadOnlyDictionary<string, ParameterValue> parameters)
    {
        ParameterValue raw = parameters[parameter.Name];
        return raw switch
        {
            InlineParameter inline => InlineToLiteral(inline.Value),
            BinaryParameter binary => new LiteralExpression(binary),
            // Raw managed string lands on the existing string-case in
            // ExpressionEvaluator.EvaluateLiteral, which materialises via
            // DataValue.FromString(s, frame.Target).
            StringParameter str => new LiteralExpression(str.Value),
            _ => throw new InvalidOperationException(
                $"Unrecognised ParameterValue subtype: {raw.GetType().Name}."),
        };
    }

    private static LiteralExpression InlineToLiteral(DataValue value)
    {
        if (value.IsNull)
        {
            return new LiteralExpression(null);
        }

        return value.Kind switch
        {
            DataKind.Int8 => new LiteralExpression((sbyte)value.AsInt8()),
            DataKind.Int16 => new LiteralExpression((short)value.AsInt16()),
            DataKind.Int32 => new LiteralExpression(value.AsInt32()),
            DataKind.Int64 => new LiteralExpression(value.AsInt64()),
            DataKind.UInt8 => new LiteralExpression((sbyte)value.AsUInt8()),
            DataKind.Float32 => new LiteralExpression((float)value.AsFloat32()),
            DataKind.Float64 => new LiteralExpression(value.AsFloat64()),
            DataKind.String => new LiteralExpression(value.AsString()),
            DataKind.Boolean => new LiteralExpression(value.AsBoolean()),
            _ => new LiteralExpression(value.AsString()),
        };
    }

    private static FunctionCallExpression BindFunctionCall(
        FunctionCallExpression function,
        IReadOnlyDictionary<string, ParameterValue> parameters)
    {
        Expression[] arguments = new Expression[function.Arguments.Count];
        for (int i = 0; i < function.Arguments.Count; i++)
        {
            arguments[i] = BindExpression(function.Arguments[i], parameters);
        }

        // OrderBy / WithinGroupOrderBy items reference column refs and
        // literals only — parameter substitution flows through them so
        // an aggregate like `string_agg(x, $sep ORDER BY $key)` gets
        // both pieces bound. The lists are usually short (1-2 items),
        // so we walk them eagerly.
        IReadOnlyList<OrderByItem>? boundOrderBy =
            function.OrderBy is null ? null : BindOrderByItems(function.OrderBy, parameters);
        IReadOnlyList<OrderByItem>? boundWithinGroup =
            function.WithinGroupOrderBy is null ? null : BindOrderByItems(function.WithinGroupOrderBy, parameters);

        return new FunctionCallExpression(
            function.FunctionName,
            arguments,
            boundOrderBy,
            function.Distinct,
            function.Span,
            boundWithinGroup,
            function.SchemaName);
    }

    private static IReadOnlyList<OrderByItem> BindOrderByItems(
        IReadOnlyList<OrderByItem> items,
        IReadOnlyDictionary<string, ParameterValue> parameters)
    {
        OrderByItem[] result = new OrderByItem[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            result[i] = new OrderByItem(BindExpression(items[i].Expression, parameters), items[i].Direction);
        }
        return result;
    }

    private static InExpression BindInExpression(
        InExpression inExpr,
        IReadOnlyDictionary<string, ParameterValue> parameters)
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
        IReadOnlyDictionary<string, ParameterValue> parameters)
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

    private static void CollectFromSelectColumns(IReadOnlyList<SelectColumn> columns, HashSet<string> names)
    {
        foreach (SelectColumn column in columns)
        {
            if (column is not (SelectAllColumns or SelectTableColumns))
            {
                CollectFromExpression(column.Expression, names);
            }
        }
    }

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
            case LikeExpression like:
                CollectFromExpression(like.Expression, names);
                CollectFromExpression(like.Pattern, names);
                CollectFromExpression(like.EscapeCharacter, names);
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
            case InsertQueryExpression insertQuery:
                CollectFromAnyStatement(insertQuery.Insert, names);
                break;
            case UpdateQueryExpression updateQuery:
                CollectFromAnyStatement(updateQuery.Update, names);
                break;
            case DeleteQueryExpression deleteQuery:
                CollectFromAnyStatement(deleteQuery.Delete, names);
                break;
        }
    }

    private static QueryExpression BindQueryExpression(
        QueryExpression query,
        IReadOnlyDictionary<string, ParameterValue> parameters)
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
            // Data-modifying CTE bodies: bind the wrapped statement
            // (Source, Where, SET, Returning all carry expressions) and
            // rewrap. Without this, $-parameters inside a CTE's INSERT/
            // UPDATE/DELETE body wouldn't substitute.
            InsertQueryExpression insertQuery =>
                new InsertQueryExpression((InsertStatement)BindAnyStatement(insertQuery.Insert, parameters)),
            UpdateQueryExpression updateQuery =>
                new UpdateQueryExpression((UpdateStatement)BindAnyStatement(updateQuery.Update, parameters)),
            DeleteQueryExpression deleteQuery =>
                new DeleteQueryExpression((DeleteStatement)BindAnyStatement(deleteQuery.Delete, parameters)),
            _ => query,
        };
    }

    private static OrderByClause BindOrderByClause(
        OrderByClause orderBy,
        IReadOnlyDictionary<string, ParameterValue> parameters)
    {
        OrderByItem[] orderItems = new OrderByItem[orderBy.Items.Count];
        for (int i = 0; i < orderBy.Items.Count; i++)
        {
            OrderByItem item = orderBy.Items[i];
            orderItems[i] = new OrderByItem(BindExpression(item.Expression, parameters), item.Direction);
        }

        return new OrderByClause(orderItems);
    }

    // ───────────────────── Top-level Statement binding ─────────────────────

    /// <summary>
    /// Recursively binds parameters in a top-level statement. Walks every
    /// subtype that can carry an <see cref="Expression"/> /
    /// <see cref="QueryExpression"/> / nested <see cref="Statement"/>;
    /// returns DDL and control-flow leaves unchanged.
    /// </summary>
    /// <remarks>
    /// CREATE FUNCTION / CREATE PROCEDURE bodies are <em>not</em> bound:
    /// their stored body is the function/procedure definition, not a
    /// call site. Parameter substitution happens at the call site, where
    /// the inliner sees both the body and the per-call argument values.
    /// </remarks>
    private static Statement BindAnyStatement(
        Statement statement,
        IReadOnlyDictionary<string, ParameterValue> parameters)
    {
        switch (statement)
        {
            case QueryStatement q:
                return new QueryStatement(BindQueryExpression(q.Query, parameters));

            case InsertStatement ins:
                return new InsertStatement(
                    ins.TableName,
                    ins.ColumnNames,
                    BindInsertSource(ins.Source, parameters),
                    Returning: ins.Returning is not null
                        ? BindSelectColumns(ins.Returning, parameters)
                        : null,
                    SchemaName: ins.SchemaName);

            case UpdateStatement upd:
                return BindUpdate(upd, parameters);

            case DeleteStatement del:
                return new DeleteStatement(
                    del.TableName,
                    del.Where is not null ? BindExpression(del.Where, parameters) : null,
                    del.SchemaName,
                    Returning: del.Returning is not null
                        ? BindSelectColumns(del.Returning, parameters)
                        : null);

            case CallStatement call:
                return new CallStatement(BindExpression(call.Call, parameters), call.Span);

            case CreateTableAsSelectStatement ctas:
                return new CreateTableAsSelectStatement(
                    ctas.TableName,
                    BindQueryExpression(ctas.Query, parameters),
                    ctas.IsTemp,
                    ctas.IfNotExists,
                    ctas.StoragePath,
                    ctas.SchemaName);

            case AlterTableAddColumnStatement alter:
                return new AlterTableAddColumnStatement(
                    alter.TableName,
                    alter.ColumnName,
                    alter.TypeName,
                    alter.DefaultValue is not null ? BindExpression(alter.DefaultValue, parameters) : null,
                    alter.Nullable,
                    alter.ComputedExpression is not null ? BindExpression(alter.ComputedExpression, parameters) : null);

            case BlockStatement block:
            {
                Statement[] inner = new Statement[block.Statements.Count];
                for (int i = 0; i < block.Statements.Count; i++)
                {
                    inner[i] = BindAnyStatement(block.Statements[i], parameters);
                }
                return new BlockStatement(inner, block.Span);
            }

            case IfStatement ifs:
                return new IfStatement(
                    BindExpression(ifs.Predicate, parameters),
                    BindAnyStatement(ifs.Then, parameters),
                    ifs.Else is not null ? BindAnyStatement(ifs.Else, parameters) : null,
                    ifs.Span);

            case WhileStatement whileStmt:
                return new WhileStatement(
                    BindExpression(whileStmt.Predicate, parameters),
                    BindAnyStatement(whileStmt.Body, parameters),
                    whileStmt.Span);

            case ForCounterStatement forCounter:
                return new ForCounterStatement(
                    forCounter.VariableName,
                    BindExpression(forCounter.Start, parameters),
                    BindExpression(forCounter.End, parameters),
                    forCounter.Step is not null ? BindExpression(forCounter.Step, parameters) : null,
                    BindAnyStatement(forCounter.Body, parameters),
                    forCounter.Span);

            case ForInStatement forIn:
                return new ForInStatement(
                    forIn.VariableName,
                    BindQueryExpression(forIn.Source, parameters),
                    BindAnyStatement(forIn.Body, parameters),
                    forIn.Span);

            case DeclareStatement decl:
                return new DeclareStatement(
                    decl.VariableName,
                    decl.TypeName,
                    decl.Initializer is not null ? BindExpression(decl.Initializer, parameters) : null,
                    decl.Span);

            case SetStatement set:
                return new SetStatement(
                    set.VariableName,
                    BindExpression(set.Value, parameters),
                    set.Span);

            case ReturnStatement ret:
                return new ReturnStatement(BindExpression(ret.Value, parameters), ret.Span);

            case PrintStatement print:
                return new PrintStatement(BindExpression(print.Value, parameters), print.Span);

            case AssertStatement assertStmt:
                return new AssertStatement(
                    BindExpression(assertStmt.Predicate, parameters),
                    assertStmt.Message is not null ? BindExpression(assertStmt.Message, parameters) : null,
                    assertStmt.Span);

            case RaiseStatement raise:
                return new RaiseStatement(BindExpression(raise.Message, parameters), raise.Span);

            case TryStatement tryStmt:
                return new TryStatement(
                    BindAnyStatement(tryStmt.TryBody, parameters),
                    tryStmt.ErrorVariableName,
                    BindAnyStatement(tryStmt.CatchBody, parameters),
                    tryStmt.FinallyBody is not null ? BindAnyStatement(tryStmt.FinallyBody, parameters) : null,
                    tryStmt.Span);

            // DDL leaves and procedural control-flow signals: no
            // expressions to bind. CREATE FUNCTION / CREATE PROCEDURE
            // bodies are stored verbatim — call-site param substitution
            // happens through inlining, not at definition time.
            case CreateTableStatement:
            case DropTableStatement:
            case AlterTableDropColumnStatement:
            case AnalyzeTableStatement:
            case ReindexTableStatement:
            case CreateFunctionStatement:
            case DropFunctionStatement:
            case CreateProcedureStatement:
            case DropProcedureStatement:
            case BreakStatement:
            case ContinueStatement:
                return statement;

            default:
                return statement;
        }
    }

    private static InsertSource BindInsertSource(
        InsertSource source,
        IReadOnlyDictionary<string, ParameterValue> parameters)
    {
        switch (source)
        {
            case InsertQuerySource q:
                return new InsertQuerySource(BindQueryExpression(q.Query, parameters));
            case InsertValuesSource v:
            {
                IReadOnlyList<Expression>[] rows = new IReadOnlyList<Expression>[v.Rows.Count];
                for (int r = 0; r < v.Rows.Count; r++)
                {
                    IReadOnlyList<Expression> row = v.Rows[r];
                    Expression[] bound = new Expression[row.Count];
                    for (int c = 0; c < row.Count; c++)
                    {
                        bound[c] = BindExpression(row[c], parameters);
                    }
                    rows[r] = bound;
                }
                return new InsertValuesSource(rows);
            }
            default:
                return source;
        }
    }

    private static UpdateStatement BindUpdate(
        UpdateStatement upd,
        IReadOnlyDictionary<string, ParameterValue> parameters)
    {
        ColumnAssignment[] assigns = new ColumnAssignment[upd.Assignments.Count];
        for (int i = 0; i < upd.Assignments.Count; i++)
        {
            ColumnAssignment a = upd.Assignments[i];
            assigns[i] = new ColumnAssignment(a.ColumnName, BindExpression(a.Value, parameters));
        }

        FromClause? from = upd.From is not null
            ? new FromClause(BindTableSource(upd.From.Source, parameters))
            : null;

        IReadOnlyList<JoinClause>? joins = null;
        if (upd.Joins is not null)
        {
            JoinClause[] boundJoins = new JoinClause[upd.Joins.Count];
            for (int i = 0; i < upd.Joins.Count; i++)
            {
                JoinClause j = upd.Joins[i];
                Expression? on = j.OnCondition is not null
                    ? BindExpression(j.OnCondition, parameters)
                    : null;
                boundJoins[i] = new JoinClause(j.Type, BindTableSource(j.Source, parameters), on, j.IsLateral);
            }
            joins = boundJoins;
        }

        Expression? where = upd.Where is not null ? BindExpression(upd.Where, parameters) : null;

        IReadOnlyList<SelectColumn>? returning = upd.Returning is not null
            ? BindSelectColumns(upd.Returning, parameters)
            : null;

        return new UpdateStatement(
            upd.TableName, upd.Alias, assigns, from, joins, where,
            upd.SchemaName, Returning: returning);
    }

    // ───────────────────── Top-level Statement collection ─────────────────────

    private static void CollectFromAnyStatement(Statement statement, HashSet<string> names)
    {
        switch (statement)
        {
            case QueryStatement q:
                CollectFromQueryExpression(q.Query, names);
                break;

            case InsertStatement ins:
                CollectFromInsertSource(ins.Source, names);
                if (ins.Returning is not null) CollectFromSelectColumns(ins.Returning, names);
                break;

            case UpdateStatement upd:
                foreach (ColumnAssignment a in upd.Assignments) CollectFromExpression(a.Value, names);
                if (upd.From is not null) CollectFromTableSource(upd.From.Source, names);
                if (upd.Joins is not null)
                {
                    foreach (JoinClause j in upd.Joins)
                    {
                        CollectFromTableSource(j.Source, names);
                        if (j.OnCondition is not null) CollectFromExpression(j.OnCondition, names);
                    }
                }
                if (upd.Where is not null) CollectFromExpression(upd.Where, names);
                if (upd.Returning is not null) CollectFromSelectColumns(upd.Returning, names);
                break;

            case DeleteStatement del:
                if (del.Where is not null) CollectFromExpression(del.Where, names);
                if (del.Returning is not null) CollectFromSelectColumns(del.Returning, names);
                break;

            case CallStatement call:
                CollectFromExpression(call.Call, names);
                break;

            case CreateTableAsSelectStatement ctas:
                CollectFromQueryExpression(ctas.Query, names);
                break;

            case AlterTableAddColumnStatement alter:
                if (alter.DefaultValue is not null) CollectFromExpression(alter.DefaultValue, names);
                if (alter.ComputedExpression is not null) CollectFromExpression(alter.ComputedExpression, names);
                break;

            case BlockStatement block:
                foreach (Statement s in block.Statements) CollectFromAnyStatement(s, names);
                break;

            case IfStatement ifs:
                CollectFromExpression(ifs.Predicate, names);
                CollectFromAnyStatement(ifs.Then, names);
                if (ifs.Else is not null) CollectFromAnyStatement(ifs.Else, names);
                break;

            case WhileStatement whileStmt:
                CollectFromExpression(whileStmt.Predicate, names);
                CollectFromAnyStatement(whileStmt.Body, names);
                break;

            case ForCounterStatement forCounter:
                CollectFromExpression(forCounter.Start, names);
                CollectFromExpression(forCounter.End, names);
                if (forCounter.Step is not null) CollectFromExpression(forCounter.Step, names);
                CollectFromAnyStatement(forCounter.Body, names);
                break;

            case ForInStatement forIn:
                CollectFromQueryExpression(forIn.Source, names);
                CollectFromAnyStatement(forIn.Body, names);
                break;

            case DeclareStatement decl:
                if (decl.Initializer is not null) CollectFromExpression(decl.Initializer, names);
                break;

            case SetStatement set:
                CollectFromExpression(set.Value, names);
                break;

            case ReturnStatement ret:
                CollectFromExpression(ret.Value, names);
                break;

            case PrintStatement print:
                CollectFromExpression(print.Value, names);
                break;

            case AssertStatement assertStmt:
                CollectFromExpression(assertStmt.Predicate, names);
                if (assertStmt.Message is not null) CollectFromExpression(assertStmt.Message, names);
                break;

            case RaiseStatement raise:
                CollectFromExpression(raise.Message, names);
                break;

            case TryStatement tryStmt:
                CollectFromAnyStatement(tryStmt.TryBody, names);
                CollectFromAnyStatement(tryStmt.CatchBody, names);
                if (tryStmt.FinallyBody is not null) CollectFromAnyStatement(tryStmt.FinallyBody, names);
                break;
        }
    }

    private static void CollectFromInsertSource(InsertSource source, HashSet<string> names)
    {
        switch (source)
        {
            case InsertQuerySource q:
                CollectFromQueryExpression(q.Query, names);
                break;
            case InsertValuesSource v:
                foreach (IReadOnlyList<Expression> row in v.Rows)
                {
                    foreach (Expression e in row) CollectFromExpression(e, names);
                }
                break;
        }
    }
}
