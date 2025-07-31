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
    /// Result of rewriting an expression tree. Contains the rewritten expression
    /// and any correlated subqueries that need per-row execution.
    /// </summary>
    /// <param name="Expression">The rewritten expression with subqueries replaced.</param>
    /// <param name="CorrelatedSubqueries">
    /// Correlated subqueries requiring <see cref="Operators.ScalarSubqueryOperator"/> injection.
    /// Empty when all subqueries were uncorrelated (constant-folded).
    /// </param>
    internal sealed record RewriteResult(
        Expression Expression,
        IReadOnlyList<CorrelatedSubquery> CorrelatedSubqueries);

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
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rewritten expression and any correlated subqueries.</returns>
    internal static async Task<RewriteResult> RewriteAsync(
        Expression expression,
        HashSet<string> outerAliases,
        QueryPlanner planner,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        List<CorrelatedSubquery> correlatedSubqueries = [];
        int[] subqueryCounter = [0];

        Expression rewritten = await RewriteNodeAsync(
            expression, outerAliases, planner, context, correlatedSubqueries,
            subqueryCounter, cancellationToken).ConfigureAwait(false);

        return new RewriteResult(rewritten, correlatedSubqueries);
    }

    private static async Task<Expression> RewriteNodeAsync(
        Expression expression,
        HashSet<string> outerAliases,
        QueryPlanner planner,
        ExecutionContext context,
        List<CorrelatedSubquery> correlatedSubqueries,
        int[] subqueryCounter,
        CancellationToken cancellationToken)
    {
        switch (expression)
        {
            case SubqueryExpression subquery:
                return await RewriteSubqueryAsync(
                    subquery, outerAliases, planner, context, correlatedSubqueries,
                    subqueryCounter, cancellationToken).ConfigureAwait(false);

            case BinaryExpression binary:
            {
                Expression left = await RewriteNodeAsync(
                    binary.Left, outerAliases, planner, context, correlatedSubqueries,
                    subqueryCounter, cancellationToken).ConfigureAwait(false);
                Expression right = await RewriteNodeAsync(
                    binary.Right, outerAliases, planner, context, correlatedSubqueries,
                    subqueryCounter, cancellationToken).ConfigureAwait(false);

                return ReferenceEquals(left, binary.Left) && ReferenceEquals(right, binary.Right)
                    ? binary
                    : new BinaryExpression(left, binary.Operator, right);
            }

            case UnaryExpression unary:
            {
                Expression operand = await RewriteNodeAsync(
                    unary.Operand, outerAliases, planner, context, correlatedSubqueries,
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
                        original, outerAliases, planner, context, correlatedSubqueries,
                        subqueryCounter, cancellationToken).ConfigureAwait(false);

                    if (!ReferenceEquals(rewritten, original))
                    {
                        rewrittenArguments ??= new List<Expression>(function.Arguments);
                        rewrittenArguments[index] = rewritten;
                    }
                }

                return rewrittenArguments is null
                    ? function
                    : new FunctionCallExpression(function.FunctionName, rewrittenArguments, function.Distinct, function.Span);
            }

            case InExpression inExpression:
            {
                Expression target = await RewriteNodeAsync(
                    inExpression.Expression, outerAliases, planner, context, correlatedSubqueries,
                    subqueryCounter, cancellationToken).ConfigureAwait(false);

                List<Expression>? rewrittenValues = null;

                for (int index = 0; index < inExpression.Values.Count; index++)
                {
                    Expression original = inExpression.Values[index];
                    Expression rewritten = await RewriteNodeAsync(
                        original, outerAliases, planner, context, correlatedSubqueries,
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
                    between.Expression, outerAliases, planner, context, correlatedSubqueries,
                    subqueryCounter, cancellationToken).ConfigureAwait(false);
                Expression low = await RewriteNodeAsync(
                    between.Low, outerAliases, planner, context, correlatedSubqueries,
                    subqueryCounter, cancellationToken).ConfigureAwait(false);
                Expression high = await RewriteNodeAsync(
                    between.High, outerAliases, planner, context, correlatedSubqueries,
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
                    isNull.Expression, outerAliases, planner, context, correlatedSubqueries,
                    subqueryCounter, cancellationToken).ConfigureAwait(false);

                return ReferenceEquals(inner, isNull.Expression)
                    ? isNull
                    : new IsNullExpression(inner, isNull.Negated);
            }

            case CastExpression cast:
            {
                Expression inner = await RewriteNodeAsync(
                    cast.Expression, outerAliases, planner, context, correlatedSubqueries,
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
                        operand, outerAliases, planner, context, correlatedSubqueries,
                        subqueryCounter, cancellationToken).ConfigureAwait(false);
                }

                List<WhenClause>? rewrittenClauses = null;

                for (int index = 0; index < caseExpression.WhenClauses.Count; index++)
                {
                    WhenClause clause = caseExpression.WhenClauses[index];
                    Expression condition = await RewriteNodeAsync(
                        clause.Condition, outerAliases, planner, context, correlatedSubqueries,
                        subqueryCounter, cancellationToken).ConfigureAwait(false);
                    Expression result = await RewriteNodeAsync(
                        clause.Result, outerAliases, planner, context, correlatedSubqueries,
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
                        elseResult, outerAliases, planner, context, correlatedSubqueries,
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
    /// </summary>
    private static async Task<Expression> RewriteSubqueryAsync(
        SubqueryExpression subquery,
        HashSet<string> outerAliases,
        QueryPlanner planner,
        ExecutionContext context,
        List<CorrelatedSubquery> correlatedSubqueries,
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
            // Correlated: replace with synthetic column reference.
            // The ScalarSubqueryOperator will populate this column per outer row.
            // The "__subquery" table qualifier prevents predicate pushdown from
            // pushing this reference below the ScalarSubqueryOperator, while
            // ExpressionEvaluator still resolves it via unqualified fallback.
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

        await foreach (Row row in innerPlan.ExecuteAsync(context).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (firstRow is not null)
            {
                hasMultipleRows = true;
                break;
            }

            firstRow = row;
        }

        if (hasMultipleRows)
        {
            throw new InvalidOperationException("Scalar subquery returned more than one row.");
        }

        if (firstRow is null)
        {
            return new LiteralExpression(null);
        }

        if (firstRow.FieldCount != 1)
        {
            throw new InvalidOperationException(
                $"Scalar subquery must return exactly one column, but returned {firstRow.FieldCount}.");
        }

        DataValue value = firstRow[0];

        if (value.IsNull)
        {
            return new LiteralExpression(null);
        }

        object literal = value.Kind switch
        {
            DataKind.Scalar => value.AsScalar(),
            DataKind.UInt8 => (float)value.AsUInt8(),
            DataKind.String => value.AsString(),
            DataKind.Boolean => value.AsBoolean(),
            _ => value.AsScalar(),
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
}
