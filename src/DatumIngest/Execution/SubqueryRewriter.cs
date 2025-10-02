using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

/// <summary>
/// Walks an expression tree and replaces <see cref="SubqueryExpression"/> nodes
/// with their evaluated results. Uncorrelated subqueries are constant-folded
/// at plan time into <see cref="LiteralExpression"/> nodes (zero per-row cost).
/// Correlated subqueries (those referencing outer-scope table aliases) are
/// rewritten to synthetic <see cref="ColumnReference"/> nodes and recorded for
/// injection of <see cref="Operators.ScalarSubqueryOperator"/> into the pipeline.
/// </summary>
internal static class SubqueryRewriter
{
    /// <summary>
    /// Describes a correlated scalar subquery that must be evaluated per outer row.
    /// </summary>
    /// <param name="SyntheticColumnName">
    /// The synthetic column name injected into the row by the <see cref="Operators.ScalarSubqueryOperator"/>.
    /// </param>
    /// <param name="InnerQuery">The parsed subquery to execute per outer row.</param>
    internal sealed record CorrelatedSubquery(string SyntheticColumnName, SelectStatement InnerQuery);

    /// <summary>
    /// Describes a decorrelated scalar subquery that has been rewritten into a
    /// <c>GROUP BY</c> + <c>LEFT JOIN</c>. The inner plan produces grouped aggregate
    /// results keyed by the correlation column(s), and the outer query references
    /// the aggregate result via a synthetic column.
    /// </summary>
    /// <param name="SyntheticColumnName">
    /// The synthetic column name that replaces the original subquery expression.
    /// </param>
    /// <param name="InnerPlan">The planned operator tree for the grouped inner query.</param>
    /// <param name="OnCondition">
    /// The LEFT JOIN predicate connecting outer key(s) to inner GROUP BY key(s).
    /// </param>
    internal sealed record DecorrelatedScalarJoin(
        string SyntheticColumnName,
        IQueryOperator InnerPlan,
        Expression OnCondition);

    /// <summary>
    /// Result of rewriting an expression tree. Contains the rewritten expression
    /// and any correlated subqueries that need per-row execution.
    /// </summary>
    /// <param name="Expression">The rewritten expression with subqueries replaced.</param>
    /// <param name="CorrelatedSubqueries">
    /// Correlated subqueries requiring <see cref="Operators.ScalarSubqueryOperator"/> injection.
    /// Empty when all subqueries were uncorrelated (constant-folded).
    /// </param>
    /// <param name="DecorrelatedScalarJoins">
    /// Decorrelated scalar subqueries rewritten as <c>LEFT JOIN</c> on a grouped inner plan.
    /// Empty when no subqueries qualified for decorrelation.
    /// </param>
    internal sealed record RewriteResult(
        Expression Expression,
        IReadOnlyList<CorrelatedSubquery> CorrelatedSubqueries,
        IReadOnlyList<DecorrelatedScalarJoin> DecorrelatedScalarJoins);

    /// <summary>
    /// Rewrites all <see cref="SubqueryExpression"/> nodes in the given expression.
    /// Uncorrelated subqueries are planned, executed, and replaced with literals.
    /// Correlated subqueries are replaced with synthetic column references and
    /// recorded in the result for downstream operator injection.
    /// </summary>
    /// <param name="expression">The expression tree to rewrite.</param>
    /// <param name="outerAliases">Table aliases available in the outer scope, used to detect correlation.</param>
    /// <param name="planner">The query planner for planning inner subqueries.</param>
    /// <param name="context">Execution context for running uncorrelated subqueries.</param>
    /// <param name="functionRegistry">Function registry for detecting aggregate functions during decorrelation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rewritten expression and any correlated subqueries.</returns>
    internal static async Task<RewriteResult> RewriteAsync(
        Expression expression,
        HashSet<string> outerAliases,
        QueryPlanner planner,
        ExecutionContext context,
        FunctionRegistry functionRegistry,
        CancellationToken cancellationToken)
    {
        List<CorrelatedSubquery> correlatedSubqueries = [];
        List<DecorrelatedScalarJoin> decorrelatedJoins = [];
        int[] subqueryCounter = [0];

        Expression rewritten = await RewriteNodeAsync(
            expression, outerAliases, planner, context, functionRegistry,
            correlatedSubqueries, decorrelatedJoins,
            subqueryCounter, cancellationToken).ConfigureAwait(false);

        return new RewriteResult(rewritten, correlatedSubqueries, decorrelatedJoins);
    }

    private static async Task<Expression> RewriteNodeAsync(
        Expression expression,
        HashSet<string> outerAliases,
        QueryPlanner planner,
        ExecutionContext context,
        FunctionRegistry functionRegistry,
        List<CorrelatedSubquery> correlatedSubqueries,
        List<DecorrelatedScalarJoin> decorrelatedJoins,
        int[] subqueryCounter,
        CancellationToken cancellationToken)
    {
        switch (expression)
        {
            case SubqueryExpression subquery:
                return await RewriteSubqueryAsync(
                    subquery, outerAliases, planner, context, functionRegistry,
                    correlatedSubqueries, decorrelatedJoins,
                    subqueryCounter, cancellationToken).ConfigureAwait(false);

            case BinaryExpression binary:
            {
                Expression left = await RewriteNodeAsync(
                    binary.Left, outerAliases, planner, context, functionRegistry,
                    correlatedSubqueries, decorrelatedJoins,
                    subqueryCounter, cancellationToken).ConfigureAwait(false);
                Expression right = await RewriteNodeAsync(
                    binary.Right, outerAliases, planner, context, functionRegistry,
                    correlatedSubqueries, decorrelatedJoins,
                    subqueryCounter, cancellationToken).ConfigureAwait(false);

                return ReferenceEquals(left, binary.Left) && ReferenceEquals(right, binary.Right)
                    ? binary
                    : new BinaryExpression(left, binary.Operator, right);
            }

            case LikeExpression like:
            {
                Expression expr = await RewriteNodeAsync(
                    like.Expression, outerAliases, planner, context, functionRegistry,
                    correlatedSubqueries, decorrelatedJoins,
                    subqueryCounter, cancellationToken).ConfigureAwait(false);
                Expression pattern = await RewriteNodeAsync(
                    like.Pattern, outerAliases, planner, context, functionRegistry,
                    correlatedSubqueries, decorrelatedJoins,
                    subqueryCounter, cancellationToken).ConfigureAwait(false);
                Expression escapeChar = await RewriteNodeAsync(
                    like.EscapeCharacter, outerAliases, planner, context, functionRegistry,
                    correlatedSubqueries, decorrelatedJoins,
                    subqueryCounter, cancellationToken).ConfigureAwait(false);

                return ReferenceEquals(expr, like.Expression)
                    && ReferenceEquals(pattern, like.Pattern)
                    && ReferenceEquals(escapeChar, like.EscapeCharacter)
                    ? like
                    : new LikeExpression(expr, pattern, escapeChar, like.CaseInsensitive);
            }

            case UnaryExpression unary:
            {
                Expression operand = await RewriteNodeAsync(
                    unary.Operand, outerAliases, planner, context, functionRegistry,
                    correlatedSubqueries, decorrelatedJoins,
                    subqueryCounter, cancellationToken).ConfigureAwait(false);

                return ReferenceEquals(operand, unary.Operand)
                    ? unary
                    : new UnaryExpression(unary.Operator, operand);
            }

            case FunctionCallExpression function:
            {
                List<Expression>? rewrittenArguments = null;

                for (int index = 0; index < function.Arguments.Count; index++)
                {
                    Expression original = function.Arguments[index];
                    Expression rewritten = await RewriteNodeAsync(
                        original, outerAliases, planner, context, functionRegistry,
                        correlatedSubqueries, decorrelatedJoins,
                        subqueryCounter, cancellationToken).ConfigureAwait(false);

                    if (!ReferenceEquals(rewritten, original))
                    {
                        rewrittenArguments ??= new List<Expression>(function.Arguments);
                        rewrittenArguments[index] = rewritten;
                    }
                }

                return rewrittenArguments is null
                    ? function
                    : new FunctionCallExpression(function.FunctionName, rewrittenArguments, function.OrderBy, function.Distinct, function.Span);
            }

            case InExpression inExpression:
            {
                Expression target = await RewriteNodeAsync(
                    inExpression.Expression, outerAliases, planner, context, functionRegistry,
                    correlatedSubqueries, decorrelatedJoins,
                    subqueryCounter, cancellationToken).ConfigureAwait(false);

                List<Expression>? rewrittenValues = null;

                for (int index = 0; index < inExpression.Values.Count; index++)
                {
                    Expression original = inExpression.Values[index];
                    Expression rewritten = await RewriteNodeAsync(
                        original, outerAliases, planner, context, functionRegistry,
                        correlatedSubqueries, decorrelatedJoins,
                        subqueryCounter, cancellationToken).ConfigureAwait(false);

                    if (!ReferenceEquals(rewritten, original))
                    {
                        rewrittenValues ??= new List<Expression>(inExpression.Values);
                        rewrittenValues[index] = rewritten;
                    }
                }

                bool changed = !ReferenceEquals(target, inExpression.Expression) || rewrittenValues is not null;
                return changed
                    ? new InExpression(target, rewrittenValues ?? inExpression.Values, inExpression.Negated)
                    : inExpression;
            }

            case BetweenExpression between:
            {
                Expression target = await RewriteNodeAsync(
                    between.Expression, outerAliases, planner, context, functionRegistry,
                    correlatedSubqueries, decorrelatedJoins,
                    subqueryCounter, cancellationToken).ConfigureAwait(false);
                Expression low = await RewriteNodeAsync(
                    between.Low, outerAliases, planner, context, functionRegistry,
                    correlatedSubqueries, decorrelatedJoins,
                    subqueryCounter, cancellationToken).ConfigureAwait(false);
                Expression high = await RewriteNodeAsync(
                    between.High, outerAliases, planner, context, functionRegistry,
                    correlatedSubqueries, decorrelatedJoins,
                    subqueryCounter, cancellationToken).ConfigureAwait(false);

                bool changed = !ReferenceEquals(target, between.Expression) ||
                    !ReferenceEquals(low, between.Low) ||
                    !ReferenceEquals(high, between.High);

                return changed
                    ? new BetweenExpression(target, low, high, between.Negated)
                    : between;
            }

            case IsNullExpression isNull:
            {
                Expression inner = await RewriteNodeAsync(
                    isNull.Expression, outerAliases, planner, context, functionRegistry,
                    correlatedSubqueries, decorrelatedJoins,
                    subqueryCounter, cancellationToken).ConfigureAwait(false);

                return ReferenceEquals(inner, isNull.Expression)
                    ? isNull
                    : new IsNullExpression(inner, isNull.Negated);
            }

            case CastExpression cast:
            {
                Expression inner = await RewriteNodeAsync(
                    cast.Expression, outerAliases, planner, context, functionRegistry,
                    correlatedSubqueries, decorrelatedJoins,
                    subqueryCounter, cancellationToken).ConfigureAwait(false);

                return ReferenceEquals(inner, cast.Expression)
                    ? cast
                    : new CastExpression(inner, cast.TargetType, cast.Span);
            }

            case CaseExpression caseExpression:
            {
                Expression? operand = caseExpression.Operand;
                if (operand is not null)
                {
                    operand = await RewriteNodeAsync(
                        operand, outerAliases, planner, context, functionRegistry,
                        correlatedSubqueries, decorrelatedJoins,
                        subqueryCounter, cancellationToken).ConfigureAwait(false);
                }

                List<WhenClause>? rewrittenClauses = null;

                for (int index = 0; index < caseExpression.WhenClauses.Count; index++)
                {
                    WhenClause clause = caseExpression.WhenClauses[index];
                    Expression condition = await RewriteNodeAsync(
                        clause.Condition, outerAliases, planner, context, functionRegistry,
                        correlatedSubqueries, decorrelatedJoins,
                        subqueryCounter, cancellationToken).ConfigureAwait(false);
                    Expression result = await RewriteNodeAsync(
                        clause.Result, outerAliases, planner, context, functionRegistry,
                        correlatedSubqueries, decorrelatedJoins,
                        subqueryCounter, cancellationToken).ConfigureAwait(false);

                    if (!ReferenceEquals(condition, clause.Condition) || !ReferenceEquals(result, clause.Result))
                    {
                        rewrittenClauses ??= new List<WhenClause>(caseExpression.WhenClauses);
                        rewrittenClauses[index] = new WhenClause(condition, result);
                    }
                }

                Expression? elseResult = caseExpression.ElseResult;
                if (elseResult is not null)
                {
                    elseResult = await RewriteNodeAsync(
                        elseResult, outerAliases, planner, context, functionRegistry,
                        correlatedSubqueries, decorrelatedJoins,
                        subqueryCounter, cancellationToken).ConfigureAwait(false);
                }

                bool changed = !ReferenceEquals(operand, caseExpression.Operand) ||
                    rewrittenClauses is not null ||
                    !ReferenceEquals(elseResult, caseExpression.ElseResult);

                return changed
                    ? new CaseExpression(
                        operand,
                        rewrittenClauses ?? caseExpression.WhenClauses,
                        elseResult)
                    : caseExpression;
            }

            // Leaf nodes that cannot contain subqueries.
            case LiteralExpression:
            case ColumnReference:
            case ErrorExpression:
            case ParameterExpression:
            case WindowFunctionCallExpression:
            // Semi-join subquery nodes are handled by SemiJoinRewriter before
            // this rewriter runs, so they pass through unchanged.
            case InSubqueryExpression:
            case ExistsExpression:
                return expression;

            default:
                return expression;
        }
    }

    /// <summary>
    /// Classifies a subquery as correlated or uncorrelated and rewrites accordingly.
    /// Correlated subqueries with a single aggregate and equality correlation are
    /// decorrelated into a <c>GROUP BY</c> + <c>LEFT JOIN</c> for O(N+M) execution.
    /// </summary>
    private static async Task<Expression> RewriteSubqueryAsync(
        SubqueryExpression subquery,
        HashSet<string> outerAliases,
        QueryPlanner planner,
        ExecutionContext context,
        FunctionRegistry functionRegistry,
        List<CorrelatedSubquery> correlatedSubqueries,
        List<DecorrelatedScalarJoin> decorrelatedJoins,
        int[] subqueryCounter,
        CancellationToken cancellationToken)
    {
        // Collect all column references within the subquery's expressions
        // (WHERE, SELECT, HAVING, JOIN ON) to determine if any reference outer-scope aliases.
        HashSet<string> innerReferencedAliases = CollectSubqueryColumnAliases(subquery.Query);

        bool isCorrelated = false;
        foreach (string alias in innerReferencedAliases)
        {
            if (outerAliases.Contains(alias))
            {
                isCorrelated = true;
                break;
            }
        }

        if (isCorrelated)
        {
            // Try decorrelation: rewrite into GROUP BY + LEFT JOIN when the pattern matches.
            DecorrelatedScalarJoin? decorrelated = TryDecorrelateScalarSubquery(
                subquery.Query, outerAliases, planner, functionRegistry, subqueryCounter);

            if (decorrelated is not null)
            {
                decorrelatedJoins.Add(decorrelated);

                Expression resultReference = new ColumnReference("__subquery", decorrelated.SyntheticColumnName);

                // COUNT produces 0 for empty groups, but LEFT JOIN yields NULL for
                // non-matching rows. Wrap with CASE WHEN IS NULL THEN 0 ELSE result END.
                if (subquery.Query.Columns[0].Expression is FunctionCallExpression func
                    && string.Equals(func.FunctionName, "COUNT", StringComparison.OrdinalIgnoreCase))
                {
                    resultReference = new CaseExpression(
                        Operand: null,
                        WhenClauses: [new WhenClause(
                            new IsNullExpression(resultReference, Negated: false),
                            new LiteralExpression(0f))],
                        ElseResult: resultReference);
                }

                return resultReference;
            }

            // Fallback: per-row execution via ScalarSubqueryOperator.
            string syntheticName = $"__scalar_subquery_{subqueryCounter[0]++}";
            correlatedSubqueries.Add(new CorrelatedSubquery(syntheticName, subquery.Query));
            return new ColumnReference("__subquery", syntheticName);
        }

        // Uncorrelated: execute at plan time and replace with constant.
        return await ExecuteAndFoldAsync(subquery.Query, planner, context, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Executes an uncorrelated scalar subquery and returns a <see cref="LiteralExpression"/>
    /// with the result. Enforces SQL standard semantics: zero rows → NULL, one row with one
    /// column → the value, multiple rows → error, multiple columns → error.
    /// </summary>
    private static async Task<LiteralExpression> ExecuteAndFoldAsync(
        SelectStatement innerQuery,
        QueryPlanner planner,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        IQueryOperator innerPlan = planner.Plan(innerQuery);

        Row? firstRow = null;
        bool hasMultipleRows = false;

        await foreach (RowBatch inputBatch in innerPlan.ExecuteAsync(context).ConfigureAwait(false))
        {
            for (int i = 0; i < inputBatch.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Row row = inputBatch[i];

                if (firstRow is not null)
                {
                    hasMultipleRows = true;
                    break;
                }

                firstRow = row;
            }

            if (hasMultipleRows)
            {
                break;
            }
        }

        if (hasMultipleRows)
        {
            throw new InvalidOperationException("Scalar subquery returned more than one row.");
        }

        if (firstRow is null)
        {
            return new LiteralExpression(null);
        }

        if (firstRow.Value.FieldCount != 1)
        {
            throw new InvalidOperationException(
                $"Scalar subquery must return exactly one column, but returned {firstRow.Value.FieldCount}.");
        }

        DataValue value = firstRow.Value[0];

        if (value.IsNull)
        {
            return new LiteralExpression(null);
        }

        object literal = value.Kind switch
        {
            DataKind.Int8 => (object)(sbyte)value.AsInt8(),
            DataKind.Int16 => (short)value.AsInt16(),
            DataKind.Int32 => value.AsInt32(),
            DataKind.Int64 => value.AsInt64(),
            DataKind.UInt8 => (sbyte)value.AsUInt8(),
            DataKind.Float32 => value.AsFloat32(),
            DataKind.Float64 => value.AsFloat64(),
            DataKind.String => value.AsString(),
            DataKind.Boolean => value.AsBoolean(),
            _ => value.ToFloat(),
        };

        return new LiteralExpression(literal);
    }

    /// <summary>
    /// Collects all table aliases referenced in a subquery's expression clauses (WHERE,
    /// SELECT columns, HAVING, JOIN ON). This includes aliases that may refer to
    /// outer-scope tables (correlated references).
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="ColumnReferenceCollector"/> which stops at subquery boundaries,
    /// this method only collects immediate references — it does not recurse into nested
    /// subqueries (those would be rewritten in a separate pass).
    /// </remarks>
    private static HashSet<string> CollectSubqueryColumnAliases(SelectStatement query)
    {
        HashSet<string> aliases = new(StringComparer.OrdinalIgnoreCase);

        // Collect from WHERE.
        if (query.Where is not null)
        {
            CollectAliasesFromExpression(query.Where, aliases);
        }

        // Collect from SELECT columns.
        foreach (SelectColumn column in query.Columns)
        {
            if (column is SelectAllColumns or SelectTableColumns)
            {
                continue;
            }

            CollectAliasesFromExpression(column.Expression, aliases);
        }

        // Collect from HAVING.
        if (query.Having is not null)
        {
            CollectAliasesFromExpression(query.Having, aliases);
        }

        // Collect from JOIN ON conditions.
        if (query.Joins is not null)
        {
            foreach (JoinClause join in query.Joins)
            {
                if (join.OnCondition is not null)
                {
                    CollectAliasesFromExpression(join.OnCondition, aliases);
                }
            }
        }

        return aliases;
    }

    /// <summary>
    /// Collects table aliases from column references in an expression.
    /// Does not recurse into nested <see cref="SubqueryExpression"/> nodes.
    /// </summary>
    private static void CollectAliasesFromExpression(Expression expression, HashSet<string> aliases)
    {
        switch (expression)
        {
            case ColumnReference column when column.TableName is not null:
                aliases.Add(column.TableName);
                break;

            case BinaryExpression binary:
                CollectAliasesFromExpression(binary.Left, aliases);
                CollectAliasesFromExpression(binary.Right, aliases);
                break;

            case LikeExpression like:
                CollectAliasesFromExpression(like.Expression, aliases);
                CollectAliasesFromExpression(like.Pattern, aliases);
                CollectAliasesFromExpression(like.EscapeCharacter, aliases);
                break;

            case UnaryExpression unary:
                CollectAliasesFromExpression(unary.Operand, aliases);
                break;

            case FunctionCallExpression function:
                foreach (Expression argument in function.Arguments)
                {
                    CollectAliasesFromExpression(argument, aliases);
                }
                break;

            case InExpression inExpression:
                CollectAliasesFromExpression(inExpression.Expression, aliases);
                foreach (Expression value in inExpression.Values)
                {
                    CollectAliasesFromExpression(value, aliases);
                }
                break;

            case BetweenExpression between:
                CollectAliasesFromExpression(between.Expression, aliases);
                CollectAliasesFromExpression(between.Low, aliases);
                CollectAliasesFromExpression(between.High, aliases);
                break;

            case IsNullExpression isNull:
                CollectAliasesFromExpression(isNull.Expression, aliases);
                break;

            case CastExpression cast:
                CollectAliasesFromExpression(cast.Expression, aliases);
                break;

            case CaseExpression caseExpression:
                if (caseExpression.Operand is not null)
                {
                    CollectAliasesFromExpression(caseExpression.Operand, aliases);
                }

                foreach (WhenClause clause in caseExpression.WhenClauses)
                {
                    CollectAliasesFromExpression(clause.Condition, aliases);
                    CollectAliasesFromExpression(clause.Result, aliases);
                }

                if (caseExpression.ElseResult is not null)
                {
                    CollectAliasesFromExpression(caseExpression.ElseResult, aliases);
                }
                break;

            case SubqueryExpression:
            case InSubqueryExpression:
            case ExistsExpression:
                // Do not recurse into nested subqueries — they are separate scopes.
                break;
        }
    }

    // ────────────────────────────────── Decorrelation ──────────────────────────────────

    /// <summary>
    /// Attempts to decorrelate a correlated scalar subquery into a <c>GROUP BY</c> +
    /// <c>LEFT JOIN</c>. Returns the decorrelated join descriptor, or <see langword="null"/>
    /// if the subquery does not match the decorrelatable pattern.
    /// </summary>
    /// <remarks>
    /// <para>A subquery is decorrelatable when all of these hold:</para>
    /// <list type="number">
    /// <item>Inner SELECT has exactly one column that is a single aggregate function call.</item>
    /// <item>Inner WHERE contains at least one correlated equality conjunct where both sides
    /// are simple <see cref="ColumnReference"/> nodes.</item>
    /// <item>No existing GROUP BY, HAVING, ORDER BY, or LIMIT on the inner query.</item>
    /// </list>
    /// <para>
    /// The rewrite produces:
    /// <c>SELECT inner_key AS __group_key_N, AGG(args) AS __agg_result_N FROM inner WHERE non_correlated GROUP BY inner_key</c>
    /// and a LEFT JOIN ON <c>outer_key = __derived.__group_key_N</c>.
    /// For COUNT aggregates, the replacement expression is wrapped with
    /// <c>CASE WHEN result IS NULL THEN 0 ELSE result END</c> to preserve
    /// SQL semantics (empty group → 0, not NULL).
    /// </para>
    /// </remarks>
    private static DecorrelatedScalarJoin? TryDecorrelateScalarSubquery(
        SelectStatement innerQuery,
        HashSet<string> outerAliases,
        QueryPlanner planner,
        FunctionRegistry functionRegistry,
        int[] subqueryCounter)
    {
        // Guard: no GROUP BY, HAVING, ORDER BY, LIMIT, or OFFSET already present.
        if (innerQuery.GroupBy is not null || innerQuery.Having is not null
            || innerQuery.OrderBy is not null || innerQuery.Limit is not null
            || innerQuery.Offset is not null)
        {
            return null;
        }

        // Guard: exactly one SELECT column that is a single aggregate function call.
        if (innerQuery.Columns.Count != 1 || innerQuery.Columns[0] is SelectAllColumns or SelectTableColumns)
        {
            return null;
        }

        Expression selectExpression = innerQuery.Columns[0].Expression;
        if (selectExpression is not FunctionCallExpression aggregateCall
            || functionRegistry.TryGetAggregate(aggregateCall.FunctionName) is null)
        {
            return null;
        }

        // Separate WHERE into correlated and non-correlated predicates.
        if (innerQuery.Where is null)
        {
            return null;
        }

        List<Expression> conjuncts = [];
        FlattenAnd(innerQuery.Where, conjuncts);

        List<(ColumnReference OuterKey, ColumnReference InnerKey)> correlationKeys = [];
        List<Expression> nonCorrelated = [];

        foreach (Expression conjunct in conjuncts)
        {
            if (TryExtractCorrelationEquality(conjunct, outerAliases, out ColumnReference? outerKey, out ColumnReference? innerKey))
            {
                correlationKeys.Add((outerKey, innerKey));
            }
            else if (ReferencesOuterAlias(conjunct, outerAliases))
            {
                // Non-equality correlation (e.g., a.id > b.id) — cannot decorrelate.
                return null;
            }
            else
            {
                nonCorrelated.Add(conjunct);
            }
        }

        if (correlationKeys.Count == 0)
        {
            return null;
        }

        // Build the derived table: SELECT inner_keys, AGG(args) FROM inner WHERE non_correlated GROUP BY inner_keys
        int sequenceNumber = subqueryCounter[0]++;

        List<SelectColumn> derivedColumns = new(correlationKeys.Count + 1);
        List<Expression> groupByExpressions = new(correlationKeys.Count);
        Expression? onCondition = null;

        for (int index = 0; index < correlationKeys.Count; index++)
        {
            (ColumnReference outerKey, ColumnReference innerKey) = correlationKeys[index];
            string groupKeyAlias = $"__group_key_{sequenceNumber}_{index}";

            derivedColumns.Add(new SelectColumn(innerKey, groupKeyAlias));
            groupByExpressions.Add(innerKey);

            // ON condition: outer_key = __group_key_N_M (unqualified inner key).
            Expression equality = new BinaryExpression(
                outerKey,
                BinaryOperator.Equal,
                new ColumnReference(null, groupKeyAlias));

            onCondition = onCondition is null
                ? equality
                : new BinaryExpression(onCondition, BinaryOperator.And, equality);
        }

        string aggregateResultAlias = $"__agg_result_{sequenceNumber}";
        derivedColumns.Add(new SelectColumn(aggregateCall, aggregateResultAlias));

        Expression? nonCorrelatedWhere = nonCorrelated.Count > 0
            ? CombineWithAnd(nonCorrelated)
            : null;

        SelectStatement derivedQuery = new(
            derivedColumns,
            innerQuery.From,
            Joins: innerQuery.Joins,
            Where: nonCorrelatedWhere,
            GroupBy: new GroupByClause(groupByExpressions));

        IQueryOperator innerPlan = planner.Plan(derivedQuery);

        // The synthetic column name matches the aggregate output alias so
        // the ColumnReference resolves via unqualified fallback on the joined row.
        DecorrelatedScalarJoin join = new(aggregateResultAlias, innerPlan, onCondition!);
        return join;
    }

    /// <summary>
    /// Tries to extract a correlation equality of the form <c>outer.col = inner.col</c>
    /// from a binary expression. Both sides must be simple <see cref="ColumnReference"/>
    /// nodes, and exactly one must reference an outer alias.
    /// </summary>
    private static bool TryExtractCorrelationEquality(
        Expression expression,
        HashSet<string> outerAliases,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ColumnReference? outerKey,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ColumnReference? innerKey)
    {
        outerKey = null;
        innerKey = null;

        if (expression is not BinaryExpression { Operator: BinaryOperator.Equal } equality)
        {
            return false;
        }

        if (equality.Left is not ColumnReference leftColumn || equality.Right is not ColumnReference rightColumn)
        {
            return false;
        }

        bool leftIsOuter = leftColumn.TableName is not null
            && outerAliases.Contains(leftColumn.TableName);
        bool rightIsOuter = rightColumn.TableName is not null
            && outerAliases.Contains(rightColumn.TableName);

        // Exactly one side must reference outer scope.
        if (leftIsOuter == rightIsOuter)
        {
            return false;
        }

        if (leftIsOuter)
        {
            outerKey = leftColumn;
            innerKey = rightColumn;
        }
        else
        {
            outerKey = rightColumn;
            innerKey = leftColumn;
        }

        return true;
    }

    /// <summary>
    /// Returns true if the expression contains any column reference whose table qualifier
    /// matches one of the outer aliases.
    /// </summary>
    private static bool ReferencesOuterAlias(Expression expression, HashSet<string> outerAliases)
    {
        HashSet<string> aliases = ColumnReferenceCollector.CollectTableAliases(expression);

        foreach (string alias in aliases)
        {
            if (outerAliases.Contains(alias))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Recursively flattens AND-connected expressions into a list of conjuncts.
    /// </summary>
    private static void FlattenAnd(Expression expression, List<Expression> conjuncts)
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
    /// Combines a list of expressions with AND into a single expression.
    /// </summary>
    private static Expression CombineWithAnd(List<Expression> expressions)
    {
        Expression result = expressions[0];
        for (int index = 1; index < expressions.Count; index++)
        {
            result = new BinaryExpression(result, BinaryOperator.And, expressions[index]);
        }

        return result;
    }
}
