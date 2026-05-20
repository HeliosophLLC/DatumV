using Heliosoph.DatumV.Execution.Operators;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution;

/// <summary>
/// Rewrites <see cref="InSubqueryExpression"/> and <see cref="ExistsExpression"/>
/// nodes in the WHERE clause into semi-join or anti-semi-join operators. Uncorrelated
/// subqueries are constant-folded at plan time; correlated subqueries are decorrelated
/// into <see cref="JoinType.LeftSemi"/> or <see cref="JoinType.LeftAntiSemi"/> joins.
/// </summary>
internal static class SemiJoinRewriter
{
    /// <summary>
    /// Describes a semi-join to be injected into the query plan by the planner.
    /// </summary>
    /// <param name="JoinType">The semi-join type (LeftSemi or LeftAntiSemi).</param>
    /// <param name="InnerPlan">The planned operator tree for the inner (right) side.</param>
    /// <param name="OnCondition">The join predicate (key equality + correlation).</param>
    /// <param name="NullSensitiveAntiSemi">
    /// True for NOT IN subqueries, enabling SQL-standard null semantics in JoinOperator.
    /// </param>
    internal sealed record SemiJoinDescriptor(
        JoinType JoinType,
        QueryOperator InnerPlan,
        Expression OnCondition,
        bool NullSensitiveAntiSemi);

    /// <summary>
    /// The result of rewriting WHERE-clause subquery predicates.
    /// </summary>
    /// <param name="RemainingWhere">
    /// The WHERE expression with subquery predicates removed, or null if all predicates
    /// were consumed by semi-joins or constant-folded away.
    /// </param>
    /// <param name="SemiJoins">Semi-join descriptors to inject into the plan.</param>
    internal sealed record RewriteResult(
        Expression? RemainingWhere,
        IReadOnlyList<SemiJoinDescriptor> SemiJoins);

    /// <summary>
    /// Scans the WHERE clause for top-level AND-conjunct
    /// <see cref="InSubqueryExpression"/> and <see cref="ExistsExpression"/> nodes.
    /// Uncorrelated subqueries are constant-folded; correlated subqueries are
    /// decorrelated into <see cref="SemiJoinDescriptor"/> entries.
    /// </summary>
    /// <param name="where">The WHERE expression (may be null).</param>
    /// <param name="outerAliases">Table aliases visible in the outer query scope.</param>
    /// <param name="planner">The query planner used to plan inner queries.</param>
    /// <param name="context">The execution context for evaluating uncorrelated subqueries.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Remaining WHERE predicates and semi-join descriptors.</returns>
    internal static async Task<RewriteResult> RewriteAsync(
        Expression? where,
        HashSet<string> outerAliases,
        QueryPlanner planner,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (where is null)
        {
            return new RewriteResult(null, []);
        }

        List<Expression> conjuncts = new();
        FlattenAnd(where, conjuncts);

        List<SemiJoinDescriptor> semiJoins = new();
        List<Expression> remaining = new();

        foreach (Expression conjunct in conjuncts)
        {
            switch (conjunct)
            {
                case InSubqueryExpression inSubquery:
                    await RewriteInSubqueryAsync(
                        inSubquery, outerAliases, planner, context, semiJoins, remaining,
                        cancellationToken).ConfigureAwait(false);
                    break;

                case ExistsExpression exists:
                    await RewriteExistsAsync(
                        exists, outerAliases, planner, context, semiJoins, remaining,
                        cancellationToken).ConfigureAwait(false);
                    break;

                // NOT EXISTS is parsed as UnaryExpression(NOT, ExistsExpression)
                // because the NOT prefix layer consumes NOT before PrimaryExpression.
                case UnaryExpression { Operator: UnaryOperator.Not, Operand: ExistsExpression inner }:
                    ExistsExpression negated = inner with { Negated = !inner.Negated };
                    await RewriteExistsAsync(
                        negated, outerAliases, planner, context, semiJoins, remaining,
                        cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    remaining.Add(conjunct);
                    break;
            }
        }

        Expression? remainingWhere = remaining.Count > 0
            ? CombineWithAnd(remaining)
            : null;

        return new RewriteResult(remainingWhere, semiJoins);
    }

    /// <summary>
    /// Rewrites an IN/NOT IN subquery predicate. Uncorrelated subqueries are
    /// constant-folded into <see cref="InExpression"/> with literal values.
    /// Correlated subqueries are decorrelated into semi-join descriptors.
    /// </summary>
    private static async Task RewriteInSubqueryAsync(
        InSubqueryExpression inSubquery,
        HashSet<string> outerAliases,
        QueryPlanner planner,
        ExecutionContext context,
        List<SemiJoinDescriptor> semiJoins,
        List<Expression> remaining,
        CancellationToken cancellationToken)
    {
        SelectStatement innerQuery = inSubquery.Query;

        // Validate: inner SELECT must have exactly one column.
        if (innerQuery.Columns.Count != 1 || innerQuery.Columns[0] is SelectAllColumns or SelectTableColumns)
        {
            throw new InvalidOperationException(
                "IN (SELECT ...) subquery must select exactly one column.");
        }

        bool isCorrelated = IsCorrelated(innerQuery, outerAliases);

        if (!isCorrelated)
        {
            // Uncorrelated: execute at plan time, collect values, rewrite to InExpression.
            List<Expression> literalValues = await CollectInnerValuesAsync(
                innerQuery, planner, context, cancellationToken).ConfigureAwait(false);

            remaining.Add(new InExpression(inSubquery.Expression, literalValues, inSubquery.Negated));
        }
        else
        {
            // Correlated: decorrelate into a semi-join.
            Expression innerSelectExpression = innerQuery.Columns[0].Expression;

            // Separate inner WHERE into correlated and non-correlated predicates.
            SeparatedPredicates separated = SeparatePredicates(innerQuery.Where, outerAliases);

            // Build inner plan: inner FROM with non-correlated WHERE only (SELECT * to preserve all columns).
            SelectStatement innerPlanStatement = new(
                [new SelectAllColumns()],
                innerQuery.From,
                Joins: innerQuery.Joins,
                Where: separated.NonCorrelated,
                GroupBy: innerQuery.GroupBy,
                Having: innerQuery.Having);

            QueryOperator innerPlan = planner.Plan(innerPlanStatement);

            // Build ON condition: key equality + correlation predicates, normalized for JoinKeyExtractor.
            Expression keyEquality = new BinaryExpression(
                inSubquery.Expression, BinaryOperator.Equal, innerSelectExpression);

            Expression rawOnCondition = separated.Correlated is not null
                ? new BinaryExpression(keyEquality, BinaryOperator.And, separated.Correlated)
                : keyEquality;

            Expression onCondition = NormalizeJoinKeyOrder(rawOnCondition, outerAliases);

            JoinType joinType = inSubquery.Negated ? JoinType.LeftAntiSemi : JoinType.LeftSemi;
            bool nullSensitive = inSubquery.Negated;

            semiJoins.Add(new SemiJoinDescriptor(joinType, innerPlan, onCondition, nullSensitive));
        }
    }

    /// <summary>
    /// Rewrites an EXISTS/NOT EXISTS predicate. Uncorrelated subqueries are
    /// evaluated at plan time and resolved to a boolean gate. Correlated subqueries
    /// are decorrelated into semi-join descriptors.
    /// </summary>
    private static async Task RewriteExistsAsync(
        ExistsExpression exists,
        HashSet<string> outerAliases,
        QueryPlanner planner,
        ExecutionContext context,
        List<SemiJoinDescriptor> semiJoins,
        List<Expression> remaining,
        CancellationToken cancellationToken)
    {
        SelectStatement innerQuery = exists.Query;
        bool isCorrelated = IsCorrelated(innerQuery, outerAliases);

        if (!isCorrelated)
        {
            // Uncorrelated: execute at plan time, check row existence.
            bool hasRows = await InnerQueryHasRowsAsync(
                innerQuery, planner, context, cancellationToken).ConfigureAwait(false);

            bool predicateIsTrue = exists.Negated ? !hasRows : hasRows;

            if (!predicateIsTrue)
            {
                // Predicate is false → entire WHERE is false. Add a FALSE literal
                // so the filter eliminates all rows.
                remaining.Add(new LiteralExpression(DataValue.FromBoolean(false)));
            }
            // If predicateIsTrue, the predicate is always satisfied — omit it from remaining.
        }
        else
        {
            // Correlated: decorrelate into a semi-join.
            SeparatedPredicates separated = SeparatePredicates(innerQuery.Where, outerAliases);

            if (separated.Correlated is null)
            {
                // No correlation predicates found despite being flagged as correlated.
                // This can happen when correlation is in SELECT list, HAVING, or
                // nested subqueries. Fall back to treating as uncorrelated.
                bool hasRows = await InnerQueryHasRowsAsync(
                    innerQuery, planner, context, cancellationToken).ConfigureAwait(false);
                bool predicateIsTrue = exists.Negated ? !hasRows : hasRows;
                if (!predicateIsTrue)
                {
                    remaining.Add(new LiteralExpression(DataValue.FromBoolean(false)));
                }
                return;
            }

            // Build inner plan with non-correlated WHERE only (SELECT * to preserve all columns).
            SelectStatement innerPlanStatement = new(
                [new SelectAllColumns()],
                innerQuery.From,
                Joins: innerQuery.Joins,
                Where: separated.NonCorrelated,
                GroupBy: innerQuery.GroupBy,
                Having: innerQuery.Having);

            QueryOperator innerPlan = planner.Plan(innerPlanStatement);

            // ON condition = correlation predicates, normalized for JoinKeyExtractor.
            Expression onCondition = NormalizeJoinKeyOrder(separated.Correlated, outerAliases);

            JoinType joinType = exists.Negated ? JoinType.LeftAntiSemi : JoinType.LeftSemi;

            semiJoins.Add(new SemiJoinDescriptor(joinType, innerPlan, onCondition, NullSensitiveAntiSemi: false));
        }
    }

    /// <summary>
    /// Determines whether the inner query is correlated by checking if any expression
    /// in the inner WHERE, SELECT, or HAVING references an outer table alias.
    /// </summary>
    private static bool IsCorrelated(SelectStatement innerQuery, HashSet<string> outerAliases)
    {
        if (innerQuery.Where is not null && ReferencesOuterAlias(innerQuery.Where, outerAliases))
        {
            return true;
        }

        foreach (SelectColumn column in innerQuery.Columns)
        {
            if (column is not (SelectAllColumns or SelectTableColumns)
                && ReferencesOuterAlias(column.Expression, outerAliases))
            {
                return true;
            }
        }

        if (innerQuery.Having is not null && ReferencesOuterAlias(innerQuery.Having, outerAliases))
        {
            return true;
        }

        return false;
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
    /// Executes the inner query at plan time and collects all values from its single
    /// SELECT column into literal expressions for constant-folding an IN predicate.
    /// </summary>
    private static async Task<List<Expression>> CollectInnerValuesAsync(
        SelectStatement innerQuery,
        QueryPlanner planner,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        QueryOperator innerPlan = planner.Plan(innerQuery);
        List<Expression> values = new();

        await foreach (RowBatch inputBatch in innerPlan.ExecuteAsync(context).ConfigureAwait(false))
        {
            for (int i = 0; i < inputBatch.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Row row = inputBatch[i];
                DataValue value = row[0];
                values.Add(new LiteralExpression(value));
            }
        }

        return values;
    }

    /// <summary>
    /// Executes the inner query at plan time and returns whether it produces any rows.
    /// </summary>
    private static async Task<bool> InnerQueryHasRowsAsync(
        SelectStatement innerQuery,
        QueryPlanner planner,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        QueryOperator innerPlan = planner.Plan(innerQuery);

        await foreach (RowBatch inputBatch in innerPlan.ExecuteAsync(context).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (inputBatch.Count > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Holds the result of separating an inner WHERE into correlated and non-correlated parts.
    /// </summary>
    private sealed record SeparatedPredicates(
        Expression? Correlated,
        Expression? NonCorrelated);

    /// <summary>
    /// Separates an inner WHERE expression into correlated predicates (those referencing
    /// outer aliases) and non-correlated predicates (those referencing only inner aliases).
    /// </summary>
    private static SeparatedPredicates SeparatePredicates(
        Expression? where,
        HashSet<string> outerAliases)
    {
        if (where is null)
        {
            return new SeparatedPredicates(null, null);
        }

        List<Expression> conjuncts = new();
        FlattenAnd(where, conjuncts);

        List<Expression> correlated = new();
        List<Expression> nonCorrelated = new();

        foreach (Expression conjunct in conjuncts)
        {
            if (ReferencesOuterAlias(conjunct, outerAliases))
            {
                correlated.Add(conjunct);
            }
            else
            {
                nonCorrelated.Add(conjunct);
            }
        }

        return new SeparatedPredicates(
            correlated.Count > 0 ? CombineWithAnd(correlated) : null,
            nonCorrelated.Count > 0 ? CombineWithAnd(nonCorrelated) : null);
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

    /// <summary>
    /// Normalizes equality expressions in the ON condition so that outer-table references
    /// appear on the left and inner-table references on the right. This matches the
    /// <see cref="JoinKeyExtractor"/> convention where Left key is extracted from the
    /// probe (left/outer) side and Right key from the build (right/inner) side.
    /// </summary>
    private static Expression NormalizeJoinKeyOrder(Expression onCondition, HashSet<string> outerAliases)
    {
        if (onCondition is BinaryExpression binary && binary.Operator == BinaryOperator.And)
        {
            Expression normalizedLeft = NormalizeJoinKeyOrder(binary.Left, outerAliases);
            Expression normalizedRight = NormalizeJoinKeyOrder(binary.Right, outerAliases);
            return new BinaryExpression(normalizedLeft, BinaryOperator.And, normalizedRight);
        }

        if (onCondition is BinaryExpression equality && equality.Operator == BinaryOperator.Equal)
        {
            bool leftRefsOuter = ReferencesOuterAlias(equality.Left, outerAliases);
            bool rightRefsOuter = ReferencesOuterAlias(equality.Right, outerAliases);

            // If the right side references the outer table and the left doesn't, swap.
            if (rightRefsOuter && !leftRefsOuter)
            {
                return new BinaryExpression(equality.Right, BinaryOperator.Equal, equality.Left);
            }
        }

        return onCondition;
    }
}
