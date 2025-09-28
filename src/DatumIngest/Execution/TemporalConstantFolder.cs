using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

/// <summary>
/// Replaces transaction-stable temporal expressions (<c>CURRENT_DATE</c>, <c>CURRENT_TIMESTAMP</c>,
/// <c>CURRENT_TIME</c>, <c>LOCALTIME</c>, <c>LOCALTIMESTAMP</c>) and the <c>now()</c> /
/// <c>current_time()</c> / <c>transaction_timestamp()</c> / <c>statement_timestamp()</c>
/// functions with constant <see cref="CastExpression"/> nodes. This ensures all references
/// within a batch/statement share the same timestamp, matching PostgreSQL semantics.
/// </summary>
/// <remarks>
/// <para>
/// Two clock values are used:
/// <list type="bullet">
///   <item><c>batchClock</c> — captured once per batch; used by <c>CURRENT_TIMESTAMP</c>,
///   <c>now()</c>, <c>transaction_timestamp()</c>, and friends.</item>
///   <item><c>statementClock</c> — captured once per statement; used by <c>statement_timestamp()</c>.
///   For the first statement in a batch these are identical.</item>
/// </list>
/// </para>
/// <para>
/// <c>clock_timestamp()</c> and <c>timeofday()</c> are NOT folded — they remain as runtime
/// functions that call <see cref="DateTimeOffset.UtcNow"/> on every row.
/// </para>
/// <para>This pass runs after parameter binding and before query planning.</para>
/// </remarks>
public static class TemporalConstantFolder
{
    /// <summary>
    /// Folds all temporal constants in a <see cref="QueryExpression"/> to literals.
    /// </summary>
    /// <param name="query">The parsed query expression.</param>
    /// <param name="batchClock">Batch (transaction) start time — shared across all statements in a batch.</param>
    /// <param name="statementClock">Statement start time — unique per statement. Defaults to <paramref name="batchClock"/> if null.</param>
    public static QueryExpression Fold(QueryExpression query, DateTimeOffset batchClock, DateTimeOffset? statementClock = null)
    {
        DateTimeOffset stmtClock = statementClock ?? batchClock;
        return query switch
        {
            SelectQueryExpression select =>
                new SelectQueryExpression(FoldStatement(select.Statement, batchClock, stmtClock)),
            CompoundQueryExpression compound =>
                compound with
                {
                    Left = Fold(compound.Left, batchClock, stmtClock),
                    Right = Fold(compound.Right, batchClock, stmtClock),
                    OrderBy = compound.OrderBy is not null
                        ? FoldOrderBy(compound.OrderBy, batchClock, stmtClock)
                        : null,
                },
            _ => query,
        };
    }

    /// <summary>
    /// Folds all temporal constants in a <see cref="SelectStatement"/> to literals.
    /// </summary>
    public static SelectStatement FoldStatement(SelectStatement statement, DateTimeOffset batchClock, DateTimeOffset? statementClock = null)
    {
        DateTimeOffset stmtClock = statementClock ?? batchClock;
        bool hasAny = ContainsTemporalConstant(statement);
        if (!hasAny)
        {
            return statement;
        }

        IReadOnlyList<SelectColumn> columns = FoldSelectColumns(statement.Columns, batchClock, stmtClock);
        Expression? where = statement.Where is not null ? FoldExpression(statement.Where, batchClock, stmtClock) : null;
        Expression? having = statement.Having is not null ? FoldExpression(statement.Having, batchClock, stmtClock) : null;
        Expression? qualify = statement.Qualify is not null ? FoldExpression(statement.Qualify, batchClock, stmtClock) : null;

        IReadOnlyList<JoinClause>? joins = null;
        if (statement.Joins is not null)
        {
            JoinClause[] foldedJoins = new JoinClause[statement.Joins.Count];
            for (int i = 0; i < statement.Joins.Count; i++)
            {
                JoinClause join = statement.Joins[i];
                Expression? onCondition = join.OnCondition is not null
                    ? FoldExpression(join.OnCondition, batchClock, stmtClock)
                    : null;
                TableSource source = FoldTableSource(join.Source, batchClock, stmtClock);
                foldedJoins[i] = new JoinClause(join.Type, source, onCondition);
            }

            joins = foldedJoins;
        }

        FromClause? from = statement.From is not null
            ? new FromClause(FoldTableSource(statement.From.Source, batchClock, stmtClock))
            : null;

        GroupByClause? groupBy = null;
        if (statement.GroupBy is not null)
        {
            Expression[] groupExpressions = new Expression[statement.GroupBy.Expressions.Count];
            for (int i = 0; i < statement.GroupBy.Expressions.Count; i++)
            {
                groupExpressions[i] = FoldExpression(statement.GroupBy.Expressions[i], batchClock, stmtClock);
            }

            groupBy = new GroupByClause(groupExpressions);
        }

        OrderByClause? orderBy = statement.OrderBy is not null
            ? FoldOrderBy(statement.OrderBy, batchClock, stmtClock)
            : null;

        return new SelectStatement(
            columns,
            from,
            statement.Into,
            joins,
            where,
            groupBy,
            having,
            qualify,
            statement.Assertions,
            statement.Pivot,
            statement.Unpivot,
            orderBy,
            statement.Limit,
            statement.Offset);
    }

    // ───────────────────── Expression folding ─────────────────────

    internal static Expression FoldExpression(Expression expression, DateTimeOffset batchClock, DateTimeOffset stmtClock)
    {
        return expression switch
        {
            CurrentTimestampExpression ct => ResolveTemporalConstant(ct, batchClock),
            FunctionCallExpression { FunctionName: "now", Arguments.Count: 0 } =>
                MakeTimestampLiteral(batchClock, precision: null),
            FunctionCallExpression { FunctionName: "current_time", Arguments.Count: 0 } =>
                MakeTimeLiteral(batchClock, precision: null),
            FunctionCallExpression { FunctionName: "transaction_timestamp", Arguments.Count: 0 } =>
                MakeTimestampLiteral(batchClock, precision: null),
            FunctionCallExpression { FunctionName: "statement_timestamp", Arguments.Count: 0 } =>
                MakeTimestampLiteral(stmtClock, precision: null),
            BinaryExpression binary => new BinaryExpression(
                FoldExpression(binary.Left, batchClock, stmtClock),
                binary.Operator,
                FoldExpression(binary.Right, batchClock, stmtClock)),
            LikeExpression like => new LikeExpression(
                FoldExpression(like.Expression, batchClock, stmtClock),
                FoldExpression(like.Pattern, batchClock, stmtClock),
                FoldExpression(like.EscapeCharacter, batchClock, stmtClock),
                like.CaseInsensitive),
            UnaryExpression unary => new UnaryExpression(
                unary.Operator,
                FoldExpression(unary.Operand, batchClock, stmtClock)),
            FunctionCallExpression function => FoldFunctionCall(function, batchClock, stmtClock),
            InExpression inExpr => FoldInExpression(inExpr, batchClock, stmtClock),
            BetweenExpression between => new BetweenExpression(
                FoldExpression(between.Expression, batchClock, stmtClock),
                FoldExpression(between.Low, batchClock, stmtClock),
                FoldExpression(between.High, batchClock, stmtClock),
                between.Negated),
            IsNullExpression isNull => new IsNullExpression(
                FoldExpression(isNull.Expression, batchClock, stmtClock),
                isNull.Negated),
            CastExpression cast => new CastExpression(
                FoldExpression(cast.Expression, batchClock, stmtClock),
                cast.TargetType,
                cast.Span),
            CaseExpression caseExpr => FoldCaseExpression(caseExpr, batchClock, stmtClock),
            SubqueryExpression subquery => new SubqueryExpression(
                FoldStatement(subquery.Query, batchClock, stmtClock)),
            _ => expression,
        };
    }

    // ───────────────────── Temporal constant resolution ─────────────────────

    private static Expression ResolveTemporalConstant(CurrentTimestampExpression ct, DateTimeOffset batchClock)
    {
        return ct.Kind switch
        {
            CurrentTimestampKind.CurrentDate => MakeDateLiteral(batchClock),
            CurrentTimestampKind.CurrentTime => MakeTimeLiteral(batchClock, ct.Precision),
            CurrentTimestampKind.CurrentTimestamp => MakeTimestampLiteral(batchClock, ct.Precision),
            _ => throw new ArgumentException($"Unknown CurrentTimestampKind: {ct.Kind}"),
        };
    }

    private static Expression MakeDateLiteral(DateTimeOffset clock)
    {
        string dateString = clock.ToString("yyyy-MM-dd");
        return new CastExpression(new LiteralExpression(dateString), "Date");
    }

    private static Expression MakeTimeLiteral(DateTimeOffset clock, int? precision)
    {
        DateTimeOffset truncated = TruncatePrecision(clock, precision);
        string timeString = truncated.ToString("HH:mm:ss.FFFFFFF");
        return new CastExpression(new LiteralExpression(timeString), "Time");
    }

    private static Expression MakeTimestampLiteral(DateTimeOffset clock, int? precision)
    {
        DateTimeOffset truncated = TruncatePrecision(clock, precision);
        string timestampString = truncated.ToString("O");
        return new CastExpression(new LiteralExpression(timestampString), "DateTime");
    }

    /// <summary>
    /// Truncates the fractional seconds of a <see cref="DateTimeOffset"/> to <paramref name="precision"/> digits.
    /// Null precision means no truncation.
    /// </summary>
    private static DateTimeOffset TruncatePrecision(DateTimeOffset value, int? precision)
    {
        if (precision is null)
        {
            return value;
        }

        int p = Math.Clamp(precision.Value, 0, 6);

        // Compute ticks per unit at the desired precision.
        // p=0: second (10_000_000 ticks), p=3: millisecond (10_000 ticks), p=6: microsecond (10 ticks)
        long ticksPerUnit = (long)Math.Pow(10, 7 - p);
        long truncatedTicks = value.Ticks / ticksPerUnit * ticksPerUnit;

        return new DateTimeOffset(truncatedTicks, value.Offset);
    }

    // ───────────────────── Detection (short-circuit for no-op) ─────────────────────

    private static bool ContainsTemporalConstant(SelectStatement statement)
    {
        foreach (SelectColumn column in statement.Columns)
        {
            if (column is not (SelectAllColumns or SelectTableColumns) && ContainsTemporalExpr(column.Expression))
            {
                return true;
            }
        }

        if (statement.Where is not null && ContainsTemporalExpr(statement.Where)) return true;
        if (statement.Having is not null && ContainsTemporalExpr(statement.Having)) return true;
        if (statement.Qualify is not null && ContainsTemporalExpr(statement.Qualify)) return true;

        if (statement.Joins is not null)
        {
            foreach (JoinClause join in statement.Joins)
            {
                if (join.OnCondition is not null && ContainsTemporalExpr(join.OnCondition)) return true;
            }
        }

        if (statement.OrderBy is not null)
        {
            foreach (OrderByItem item in statement.OrderBy.Items)
            {
                if (ContainsTemporalExpr(item.Expression)) return true;
            }
        }

        if (statement.GroupBy is not null)
        {
            foreach (Expression expr in statement.GroupBy.Expressions)
            {
                if (ContainsTemporalExpr(expr)) return true;
            }
        }

        return false;
    }

    private static bool ContainsTemporalExpr(Expression expression)
    {
        return expression switch
        {
            CurrentTimestampExpression => true,
            FunctionCallExpression { FunctionName: "now", Arguments.Count: 0 } => true,
            FunctionCallExpression { FunctionName: "current_time", Arguments.Count: 0 } => true,
            FunctionCallExpression { FunctionName: "transaction_timestamp", Arguments.Count: 0 } => true,
            FunctionCallExpression { FunctionName: "statement_timestamp", Arguments.Count: 0 } => true,
            BinaryExpression binary => ContainsTemporalExpr(binary.Left) || ContainsTemporalExpr(binary.Right),
            UnaryExpression unary => ContainsTemporalExpr(unary.Operand),
            FunctionCallExpression function => function.Arguments.Any(ContainsTemporalExpr),
            CastExpression cast => ContainsTemporalExpr(cast.Expression),
            CaseExpression caseExpr =>
                (caseExpr.Operand is not null && ContainsTemporalExpr(caseExpr.Operand)) ||
                caseExpr.WhenClauses.Any(w => ContainsTemporalExpr(w.Condition) || ContainsTemporalExpr(w.Result)) ||
                (caseExpr.ElseResult is not null && ContainsTemporalExpr(caseExpr.ElseResult)),
            InExpression inExpr => ContainsTemporalExpr(inExpr.Expression) || inExpr.Values.Any(ContainsTemporalExpr),
            BetweenExpression between =>
                ContainsTemporalExpr(between.Expression) || ContainsTemporalExpr(between.Low) || ContainsTemporalExpr(between.High),
            IsNullExpression isNull => ContainsTemporalExpr(isNull.Expression),
            SubqueryExpression subquery => ContainsTemporalConstant(subquery.Query),
            _ => false,
        };
    }

    // ───────────────────── Structural helpers ─────────────────────

    private static IReadOnlyList<SelectColumn> FoldSelectColumns(
        IReadOnlyList<SelectColumn> columns, DateTimeOffset batchClock, DateTimeOffset stmtClock)
    {
        SelectColumn[] result = new SelectColumn[columns.Count];
        for (int i = 0; i < columns.Count; i++)
        {
            SelectColumn column = columns[i];
            result[i] = column is SelectAllColumns or SelectTableColumns
                ? column
                : new SelectColumn(FoldExpression(column.Expression, batchClock, stmtClock), column.Alias);
        }

        return result;
    }

    private static TableSource FoldTableSource(TableSource source, DateTimeOffset batchClock, DateTimeOffset stmtClock)
    {
        if (source is FunctionSource functionSource)
        {
            Expression[] arguments = new Expression[functionSource.Arguments.Count];
            for (int i = 0; i < functionSource.Arguments.Count; i++)
            {
                arguments[i] = FoldExpression(functionSource.Arguments[i], batchClock, stmtClock);
            }

            return new FunctionSource(functionSource.FunctionName, arguments, functionSource.Alias, functionSource.Span);
        }

        if (source is SubquerySource subquerySource)
        {
            return new SubquerySource(FoldStatement(subquerySource.Query, batchClock, stmtClock), subquerySource.Alias);
        }

        return source;
    }

    private static FunctionCallExpression FoldFunctionCall(
        FunctionCallExpression function, DateTimeOffset batchClock, DateTimeOffset stmtClock)
    {
        Expression[] arguments = new Expression[function.Arguments.Count];
        for (int i = 0; i < function.Arguments.Count; i++)
        {
            arguments[i] = FoldExpression(function.Arguments[i], batchClock, stmtClock);
        }

        return new FunctionCallExpression(function.FunctionName, arguments, function.OrderBy, function.Distinct, function.Span);
    }

    private static InExpression FoldInExpression(InExpression inExpr, DateTimeOffset batchClock, DateTimeOffset stmtClock)
    {
        Expression[] values = new Expression[inExpr.Values.Count];
        for (int i = 0; i < inExpr.Values.Count; i++)
        {
            values[i] = FoldExpression(inExpr.Values[i], batchClock, stmtClock);
        }

        return new InExpression(
            FoldExpression(inExpr.Expression, batchClock, stmtClock),
            values,
            inExpr.Negated);
    }

    private static CaseExpression FoldCaseExpression(CaseExpression caseExpr, DateTimeOffset batchClock, DateTimeOffset stmtClock)
    {
        Expression? operand = caseExpr.Operand is not null
            ? FoldExpression(caseExpr.Operand, batchClock, stmtClock)
            : null;

        WhenClause[] whenClauses = new WhenClause[caseExpr.WhenClauses.Count];
        for (int i = 0; i < caseExpr.WhenClauses.Count; i++)
        {
            WhenClause clause = caseExpr.WhenClauses[i];
            whenClauses[i] = new WhenClause(
                FoldExpression(clause.Condition, batchClock, stmtClock),
                FoldExpression(clause.Result, batchClock, stmtClock));
        }

        Expression? elseResult = caseExpr.ElseResult is not null
            ? FoldExpression(caseExpr.ElseResult, batchClock, stmtClock)
            : null;

        return new CaseExpression(operand, whenClauses, elseResult, caseExpr.Span);
    }

    private static OrderByClause FoldOrderBy(OrderByClause orderBy, DateTimeOffset batchClock, DateTimeOffset stmtClock)
    {
        OrderByItem[] items = new OrderByItem[orderBy.Items.Count];
        for (int i = 0; i < orderBy.Items.Count; i++)
        {
            OrderByItem item = orderBy.Items[i];
            items[i] = new OrderByItem(FoldExpression(item.Expression, batchClock, stmtClock), item.Direction);
        }

        return new OrderByClause(items);
    }
}
