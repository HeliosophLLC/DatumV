using System.Diagnostics.CodeAnalysis;
using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Diagnostics;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Indexing;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

/// <summary>
/// Transforms a parsed <see cref="SelectStatement"/> AST into an executable
/// operator tree (<see cref="IQueryOperator"/>). Applies predicate pushdown
/// to filter rows early and projection pushdown to skip unreferenced columns
/// at the source.
/// </summary>
public sealed class QueryPlanner
{
    private readonly TableCatalog _catalog;
    private readonly FunctionRegistry _functionRegistry;

    /// <summary>
    /// Creates a query planner for the given table catalog, function registry,
    /// and optional virtual schema registry.
    /// </summary>
    /// <param name="catalog">The catalog used to resolve table names.</param>
    /// <param name="functionRegistry">The registry used to resolve table-valued functions.</param>
    public QueryPlanner(TableCatalog catalog, FunctionRegistry functionRegistry)
    {
        _catalog = catalog;
        _functionRegistry = functionRegistry;
    }

    /// <summary>
    /// Plans the given query expression into an operator tree ready for execution.
    /// Dispatches to the appropriate planning method based on whether the query
    /// is a single SELECT or a compound set operation.
    /// </summary>
    /// <param name="query">The parsed query expression.</param>
    /// <returns>The root operator of the execution plan.</returns>
    public IQueryOperator Plan(QueryExpression query)
    {
        IQueryOperator op = query switch
        {
            SelectQueryExpression select => Plan(select.Statement),
            CompoundQueryExpression compound => PlanCompound(compound),
            _ => throw new InvalidOperationException($"Unexpected query expression type: {query.GetType().Name}"),
        };
        return Finalize(op);
    }

    /// <summary>
    /// Plans the given statement into an operator tree ready for execution.
    /// </summary>
    /// <param name="statement">The parsed SELECT statement.</param>
    /// <returns>The root operator of the execution plan.</returns>
    public IQueryOperator Plan(SelectStatement statement)
    {
        return Finalize(PlanCore(statement));
    }

    /// <summary>
    /// Plans the given query expression with cost-based late materialization of expensive columns.
    /// Dispatches to the appropriate planning method based on whether the query
    /// is a single SELECT or a compound set operation.
    /// </summary>
    /// <param name="query">The parsed query expression.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The root operator of the execution plan.</returns>
    public async Task<IQueryOperator> PlanAsync(
        QueryExpression query,
        CancellationToken cancellationToken)
    {
        IQueryOperator op = query switch
        {
            SelectQueryExpression select => PlanCore(select.Statement),
            CompoundQueryExpression compound => await PlanCompoundAsync(compound, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unexpected query expression type: {query.GetType().Name}"),
        };
        return Finalize(op);
    }

    /// <summary>
    /// Plans the given query expression with cost-based late materialization and scalar subquery
    /// rewriting. Dispatches to the appropriate planning method based on whether the query
    /// is a single SELECT or a compound set operation.
    /// </summary>
    /// <param name="query">The parsed query expression.</param>
    /// <param name="context">Execution context for running uncorrelated scalar subqueries at plan time.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The root operator of the execution plan.</returns>
    public async Task<IQueryOperator> PlanWithSubqueriesAsync(
        QueryExpression query,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        IQueryOperator op = query switch
        {
            SelectQueryExpression select =>
                await PlanCoreWithSubqueriesAsync(select.Statement, context, cancellationToken).ConfigureAwait(false),
            CompoundQueryExpression compound =>
                await PlanCompoundWithSubqueriesAsync(compound, context, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unexpected query expression type: {query.GetType().Name}"),
        };
        return Finalize(op);
    }

    /// <summary>
    /// Required-for-correctness rewrites applied to every plan, regardless of entry point.
    /// </summary>
    /// <list type="number">
    ///   <item><description>
    ///     <see cref="ModelInvocationHoister"/> — hoists <c>models.*</c> calls out of
    ///     expressions into <see cref="Operators.ModelInvocationOperator"/> nodes so
    ///     model dispatch happens once per batch instead of once per row.
    ///   </description></item>
    /// </list>
    /// <remarks>
    /// <see cref="LiteralHoister"/> is intentionally <em>not</em> here — it's a per-plan-
    /// instance optimisation that lives in <see cref="DatumIngest.Catalog.QueryPlan"/>
    /// because the hoist arena's lifetime is tied to that instance, not to the planner.
    /// </remarks>
    private IQueryOperator Finalize(IQueryOperator op)
    {
        IQueryOperator afterModelHoist = ModelInvocationHoister.Hoist(op, _catalog.Models);
        return CommonSubexpressionEliminator.Eliminate(afterModelHoist, _functionRegistry);
    }

    /// <summary>
    /// Plans a compound set operation (UNION, INTERSECT, EXCEPT) by recursively
    /// planning both branches and combining them with a <see cref="SetOperationOperator"/>.
    /// ORDER BY, LIMIT, and OFFSET on the compound are applied on top.
    /// </summary>
    private IQueryOperator PlanCompound(CompoundQueryExpression compound)
    {
        IQueryOperator left = Plan(compound.Left);
        IQueryOperator right = Plan(compound.Right);
        IQueryOperator result = new SetOperationOperator(left, right, compound.OperationType, compound.All);

        result = ApplyCompoundTrailingClauses(result, compound);
        return result;
    }

    /// <summary>
    /// Plans a query expression with access to sibling CTE operators so that
    /// table references inside the expression can resolve earlier CTEs.
    /// Used when planning non-recursive CTE bodies that may reference sibling CTEs.
    /// </summary>
    private IQueryOperator PlanWithSiblingCommonTableExpressions(
        QueryExpression query,
        IReadOnlyDictionary<string, CommonTableExpressionOperator> siblingOperators)
    {
        return query switch
        {
            SelectQueryExpression select => PlanCore(
                select.Statement,
                externalCommonTableExpressionOperators: siblingOperators),
            CompoundQueryExpression compound => PlanCompoundWithSiblingCommonTableExpressions(compound, siblingOperators),
            _ => throw new InvalidOperationException($"Unexpected query expression type: {query.GetType().Name}"),
        };
    }

    /// <summary>
    /// Plans a compound set operation with sibling CTE operators threaded to both branches.
    /// </summary>
    private IQueryOperator PlanCompoundWithSiblingCommonTableExpressions(
        CompoundQueryExpression compound,
        IReadOnlyDictionary<string, CommonTableExpressionOperator> siblingOperators)
    {
        IQueryOperator left = PlanWithSiblingCommonTableExpressions(compound.Left, siblingOperators);
        IQueryOperator right = PlanWithSiblingCommonTableExpressions(compound.Right, siblingOperators);
        IQueryOperator result = new SetOperationOperator(left, right, compound.OperationType, compound.All);

        result = ApplyCompoundTrailingClauses(result, compound);
        return result;
    }

    /// <summary>
    /// Async variant of <see cref="PlanCompound"/> with late materialization.
    /// </summary>
    private async Task<IQueryOperator> PlanCompoundAsync(
        CompoundQueryExpression compound, CancellationToken cancellationToken)
    {
        IQueryOperator left = await PlanAsync(compound.Left, cancellationToken).ConfigureAwait(false);
        IQueryOperator right = await PlanAsync(compound.Right, cancellationToken).ConfigureAwait(false);
        IQueryOperator result = new SetOperationOperator(left, right, compound.OperationType, compound.All);

        result = ApplyCompoundTrailingClauses(result, compound);
        return result;
    }

    /// <summary>
    /// Async variant of <see cref="PlanCompound"/> with subquery rewriting.
    /// </summary>
    private async Task<IQueryOperator> PlanCompoundWithSubqueriesAsync(
        CompoundQueryExpression compound, ExecutionContext context, CancellationToken cancellationToken)
    {
        IQueryOperator left = await PlanWithSubqueriesAsync(compound.Left, context, cancellationToken).ConfigureAwait(false);
        IQueryOperator right = await PlanWithSubqueriesAsync(compound.Right, context, cancellationToken).ConfigureAwait(false);
        IQueryOperator result = new SetOperationOperator(left, right, compound.OperationType, compound.All);

        result = ApplyCompoundTrailingClauses(result, compound);
        return result;
    }

    /// <summary>
    /// Applies ORDER BY, LIMIT/OFFSET from a compound query expression to the
    /// combined operator. Mirrors the trailing-clause logic in <see cref="PlanCore"/>.
    /// </summary>
    private static IQueryOperator ApplyCompoundTrailingClauses(
        IQueryOperator source, CompoundQueryExpression compound)
    {
        if (compound.OrderBy is not null)
        {
            int? topNRows = compound.Limit is not null
                ? compound.Limit.Value + (compound.Offset ?? 0)
                : null;

            source = new OrderByOperator(source, compound.OrderBy.Items, topNRows);
        }

        if (compound.Limit is not null)
        {
            source = new LimitOperator(source, compound.Limit.Value, compound.Offset ?? 0);
        }

        return source;
    }

    /// <summary>
    /// Core planning logic shared by <see cref="Plan(SelectStatement)"/> and <see cref="PlanCore(SelectStatement, Func{IQueryOperator, IQueryOperator}?, IReadOnlyDictionary{string, CommonTableExpressionOperator}?)"/>.
    /// </summary>
    /// <param name="statement">The parsed SELECT statement.</param>
    /// <param name="sourceTransform">
    /// Optional transform applied to the source operator after joins and predicate pushdown
    /// but before the remaining WHERE filter. Used to inject <see cref="Operators.ScalarSubqueryOperator"/>
    /// wrappers for correlated subqueries.
    /// </param>
    /// <param name="externalCommonTableExpressionOperators">
    /// Pre-built CTE operators injected by the recursive CTE planner. These are merged
    /// into the CTE dictionary so the recursive member's FROM clause resolves the self-reference
    /// to the working table operator.
    /// </param>
    private IQueryOperator PlanCore(
        SelectStatement statement,
        Func<IQueryOperator, IQueryOperator>? sourceTransform = null,
        IReadOnlyDictionary<string, CommonTableExpressionOperator>? externalCommonTableExpressionOperators = null)
    {
        // 0. Plan Common Table Expressions (WITH clause).
        Dictionary<string, CommonTableExpressionOperator>? commonTableExpressionOperators =
            PlanCommonTableExpressions(statement);

        // Merge externally provided CTE operators (e.g. recursive working table).
        if (externalCommonTableExpressionOperators is not null)
        {
            commonTableExpressionOperators ??= new(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, CommonTableExpressionOperator> entry in externalCommonTableExpressionOperators)
            {
                commonTableExpressionOperators[entry.Key] = entry.Value;
            }
        }

        // Compute the set of all referenced columns for projection pushdown.
        HashSet<(string? TableName, string ColumnName)> allReferencedColumns =
            CollectAllReferencedColumns(statement);

        // 1. Build the source operator (FROM clause) with projection pushdown.
        bool hasJoins = statement.Joins is not null && statement.Joins.Count > 0;
        IQueryOperator source = statement.From is not null
            ? PlanSource(statement.From.Source, allReferencedColumns, hasJoins, commonTableExpressionOperators)
            : new SingleEmptyRowOperator();

        // Track which table aliases are available on the current (left) side.
        HashSet<string> leftAliases = new(StringComparer.OrdinalIgnoreCase);
        if (statement.From is not null)
        {
            CollectSourceAliases(statement.From.Source, leftAliases);
        }

        // 2. Apply JOINs with predicate pushdown.
        List<Expression>? pendingPredicates = null;

        if (statement.Where is not null)
        {
            pendingPredicates = new List<Expression>();
            FlattenAnd(statement.Where, pendingPredicates);
        }

        if (statement.Joins is not null)
        {
            // Pre-plan all join sources so we can inspect estimated row counts.
            List<(JoinClause Join, IQueryOperator Operator, HashSet<string> Aliases)> plannedJoins = new(statement.Joins.Count);

            foreach (JoinClause join in statement.Joins)
            {
                IQueryOperator rightSide = PlanSource(join.Source, allReferencedColumns, hasJoins, commonTableExpressionOperators);
                HashSet<string> rightAliases = new(StringComparer.OrdinalIgnoreCase);
                CollectSourceAliases(join.Source, rightAliases);
                plannedJoins.Add((join, rightSide, rightAliases));
            }

            // When ORDER BY has a single qualified column reference, check whether
            // the referenced table has a sorted column index on that column. If so,
            // pass the alias to TryReorderJoins so it protects that table as the
            // outermost probe, enabling sort elimination via IndexScanOperator.
            string? orderBySortTableAlias = GetOrderBySortTableAlias(
                statement.OrderBy, source, leftAliases, plannedJoins);

            // Join elimination: remove LEFT JOINs whose right-side table is not
            // referenced anywhere in the query output and is not required by any
            // other surviving join. Safe because a LEFT JOIN to an unreferenced
            // table cannot filter rows (it preserves all left-side rows).
            EliminateUnusedJoins(statement, plannedJoins);

            // Greedy join reordering: place the largest table on the probe
            // (streaming) side so LIMIT can short-circuit earlier, and build
            // the smaller tables into hash tables. Only applied when every
            // join is a non-lateral INNER join and all sources have estimated
            // row counts. This is a heuristic — the roadmap CBO will replace it.
            if (TryReorderJoins(source, leftAliases, plannedJoins, orderBySortTableAlias,
                out IQueryOperator? reorderedSource, out HashSet<string>? reorderedFromAliases,
                out List<(JoinClause Join, IQueryOperator Operator, HashSet<string> Aliases)>? reorderedJoins))
            {
                source = reorderedSource;
                leftAliases = reorderedFromAliases;
                plannedJoins = reorderedJoins;
            }

            foreach ((JoinClause join, IQueryOperator rightSide, HashSet<string> rightAliases) in plannedJoins)
            {
                IQueryOperator currentRight = rightSide;

                if (join.IsLateral)
                {
                    // Lateral joins re-execute the right side per outer row;
                    // predicate pushdown across the lateral boundary is not safe.
                    source = new LateralJoinOperator(source, currentRight, join.Type, join.OnCondition);
                }
                else
                {
                    // Transitive predicate inference: when the ON condition has
                    // equi-join pairs (A.x = B.x) and a pending predicate says
                    // A.x = <literal>, derive B.x = <literal> (and vice versa)
                    // so it can be pushed to B's scan for index pruning.
                    if (pendingPredicates is not null && join.Type == JoinType.Inner
                        && join.OnCondition is not null)
                    {
                        DeriveTransitivePredicates(join.OnCondition, pendingPredicates);
                    }

                    // Predicate pushdown: push single-table WHERE predicates below the join.
                    if (pendingPredicates is not null && join.Type == JoinType.Inner)
                    {
                        currentRight = PushPredicatesBelow(currentRight, rightAliases, pendingPredicates);
                        source = PushPredicatesBelow(source, leftAliases, pendingPredicates);
                    }

                    // Merge join: when both sides have sorted indexes on the equi-join
                    // key column, prefer a streaming two-pointer merge over building a
                    // hash table when either:
                    //   (a) a downstream operator consumes sorted output (ORDER BY / GROUP BY
                    //       leading column matches the join key), or
                    //   (b) the build side is large enough that the index-ordered random-access
                    //       cost of merge join is cheaper than allocating a multi-million-row
                    //       hash table.  The threshold mirrors IndexConstants.BPlusTreeAutoThreshold
                    //       — columns at that cardinality already have B+Tree indexes, so sorted
                    //       traversal is specifically optimised for them.
                    if (ShouldUseMergeJoin(statement, join.OnCondition, source, currentRight)
                        && TryCreateMergeJoin(source, currentRight, join.Type, join.OnCondition,
                            out MergeJoinOperator? mergeJoin))
                    {
                        source = mergeJoin;
                    }
                    else
                    {
                        // Build-side flip: when the right (build) side has 2x+ more estimated
                        // rows than the left (probe) side, flip them so the smaller side is
                        // materialized into the hash table. Applied to INNER, LEFT, and RIGHT
                        // joins. For INNER joins this acts as a fallback when TryReorderJoins
                        // did not fire (e.g. missing row-count estimates on one source).
                        // Semi-joins have asymmetric semantics and are not flipped.
                        bool flipped = false;

                        if (join.Type is JoinType.Inner or JoinType.Left or JoinType.Right)
                        {
                            long? leftRowCount = GetEstimatedRowCount(source);
                            long? rightRowCount = GetEstimatedRowCount(currentRight);

                            if (leftRowCount is not null && rightRowCount is not null
                                && leftRowCount > 0 && rightRowCount > 0)
                            {
                                // Flip when the build side (right) is at least 2x larger than
                                // the probe side (left), reducing hash table memory pressure.
                                if (rightRowCount >= leftRowCount * 2)
                                {
                                    flipped = true;
                                }
                            }
                        }

                        // Prefer index nested-loop join when the planner detects a
                        // LIMIT clause and an indexed build side. This allows NLJ to
                        // activate without waiting for LimitOperator to propagate
                        // context.RowLimit at runtime.
                        bool preferIndexNestedLoop = ShouldPreferIndexNestedLoop(statement, currentRight, join);

                        // Normalize the ON condition so that probe-side (left) column
                        // references appear on the Left of each equality. After join
                        // reordering, the AST-order Left/Right may no longer correspond
                        // to the actual probe/build sides.
                        Expression? normalizedOnCondition = join.OnCondition is not null
                            ? JoinKeyExtractor.NormalizeKeyOrder(join.OnCondition, leftAliases)
                            : null;

                        source = new JoinOperator(source, currentRight, join.Type, normalizedOnCondition,
                            flipped: flipped, preferIndexNestedLoop: preferIndexNestedLoop);
                    }
                }

                // After the join, both sides' aliases are available on the left.
                foreach (string alias in rightAliases)
                {
                    leftAliases.Add(alias);
                }
            }
        }
        else if (pendingPredicates is not null)
        {
            // No joins — push applicable predicates directly to the source.
            source = PushPredicatesBelow(source, leftAliases, pendingPredicates);
        }

        // 2b. Inject source transforms (e.g. Float32SubqueryOperator for correlated subqueries)
        // after joins and predicate pushdown, before the remaining WHERE filter.
        if (sourceTransform is not null)
        {
            source = sourceTransform(source);
        }

        // 3. Apply remaining WHERE predicates that could not be pushed down.
        if (pendingPredicates is not null && pendingPredicates.Count > 0)
        {
            Expression remaining = CombineWithAnd(pendingPredicates);
            source = new FilterOperator(source, remaining);
        }

        // 3b. GROUP BY / aggregation.
        // Desugar CROSS VALIDATE into a synthetic LET binding before any rewriting pass.
        // The fold expression is: CAST(FLOOR(hash_split(key, seed) * k) AS Int32)
        IReadOnlyList<LetBinding>? userLetBindings = statement.LetBindings;
        GroupByClause? groupBy = statement.GroupBy;
        if (statement.CrossValidate is CrossValidateClause cv)
        {
            LetBinding foldBinding = DesugarCrossValidate(cv);
            List<LetBinding> merged = userLetBindings is not null
                ? [foldBinding, .. userLetBindings]
                : [foldBinding];
            userLetBindings = merged;

            // When GROUP BY references the fold alias, we need the fold value to be
            // materialized as a column BEFORE GroupByOperator runs. We'll inject a
            // pre-GROUP BY ProjectOperator that computes the fold and passes all
            // source columns through (SELECT *, fold_expr AS fold). Then GROUP BY
            // references the materialized "fold" column directly.
            if (groupBy is not null && HasColumnReference(groupBy.Expressions, cv.OutputAlias))
            {
                // Wrap the source with a pre-GROUP BY projection that computes the fold
                // column via a LET binding (SELECT *, fold_expr AS fold). This
                // materializes the fold value BEFORE GroupByOperator runs, so GROUP BY
                // can reference "fold" as a plain column without needing to evaluate
                // hash_split against aliased rows.
                LetBinding preFoldBinding = new(cv.OutputAlias, foldBinding.Expression, OutputAlias: cv.OutputAlias);
                source = new ProjectOperator(
                    source,
                    [new SelectAllColumns()],
                    letBindings: [preFoldBinding]);

                // Remove the fold from the final LET bindings — it's already materialized
                // as a source column. The final SELECT column "fold" will resolve against
                // the GroupByOperator's output (which carries the fold as a GROUP BY key).
                merged.RemoveAt(0);
                userLetBindings = merged.Count > 0 ? merged : null;
            }
        }

        // Desugar destructured LET bindings before any rewriting pass so that all
        // downstream code (aggregate rewriting, window rewriting, ProjectOperator) only
        // sees plain LetBinding nodes. statement.LetBindings is left untouched so that
        // separate utility passes (column-reference collection, pushdown analysis) that
        // read it directly continue to work against the original AST expressions.
        IReadOnlyList<LetBinding>? letBindings = DesugarDestructuredLetBindings(userLetBindings);
        bool hasGroupBy = groupBy is not null;
        bool hasAggregates = HasAggregateFunction(statement.Columns, _functionRegistry)
            || HasLetAggregateFunction(letBindings, _functionRegistry);
        IReadOnlyList<SelectColumn> projectionColumns = statement.Columns;
        IReadOnlyList<AssertClause>? assertions = statement.Assertions;
        // Aggregate-rewritten ORDER BY clause when the query has GROUP BY /
        // aggregates; bare aggregate calls in ORDER BY are lifted into the
        // GroupBy's aggregate columns and rewritten as column references.
        // null when no rewrite happened — falls back to the original clause.
        OrderByClause? rewrittenOrderByClause = null;

        if (hasGroupBy || hasAggregates)
        {
            IReadOnlyList<Expression> groupByExpressions =
                groupBy?.Expressions ?? Array.Empty<Expression>();

            // GROUP BY ALL: derive grouping keys from non-aggregate SELECT columns.
            if (groupBy is { IsAll: true })
            {
                List<Expression> inferred = new();

                foreach (SelectColumn column in statement.Columns)
                {
                    if (column is SelectAllColumns or SelectTableColumns)
                    {
                        continue;
                    }

                    if (!ExpressionContainsAggregate(column.Expression, _functionRegistry))
                    {
                        inferred.Add(column.Expression);
                    }
                }

                if (letBindings is not null)
                {
                    foreach (LetBinding binding in letBindings)
                    {
                        if (binding.OutputAlias is not null
                            && !ExpressionContainsAggregate(binding.Expression, _functionRegistry))
                        {
                            inferred.Add(binding.Expression);
                        }
                    }
                }

                groupByExpressions = inferred;
            }

            List<AggregateColumn> aggregateColumns = new();
            List<SelectColumn> rewrittenColumns = new();

            foreach (SelectColumn column in statement.Columns)
            {
                if (column is SelectAllColumns or SelectTableColumns)
                {
                    rewrittenColumns.Add(column);
                    continue;
                }

                Expression rewritten = RewriteAggregateExpression(
                    column.Expression, _functionRegistry, aggregateColumns);
                rewrittenColumns.Add(new SelectColumn(rewritten, column.Alias));
            }

            // Rewrite aggregate expressions inside LET bindings.
            if (letBindings is not null)
            {
                List<LetBinding> rewrittenLetBindings = new(letBindings.Count);
                foreach (LetBinding binding in letBindings)
                {
                    Expression rewritten = RewriteAggregateExpression(
                        binding.Expression, _functionRegistry, aggregateColumns);
                    rewrittenLetBindings.Add(binding with { Expression = rewritten });
                }
                letBindings = rewrittenLetBindings;
            }

            // Rewrite aggregate expressions inside ASSERT clause predicates and messages.
            if (assertions is not null)
            {
                List<AssertClause> rewrittenAssertions = new(assertions.Count);
                foreach (AssertClause assertClause in assertions)
                {
                    Expression rewrittenPredicate = RewriteAggregateExpression(
                        assertClause.Predicate, _functionRegistry, aggregateColumns);
                    Expression? rewrittenMessage = assertClause.Message is not null
                        ? RewriteAggregateExpression(
                            assertClause.Message, _functionRegistry, aggregateColumns)
                        : null;
                    rewrittenAssertions.Add(assertClause with
                    {
                        Predicate = rewrittenPredicate,
                        Message = rewrittenMessage,
                    });
                }
                assertions = rewrittenAssertions;
            }

            // Rewrite ORDER BY items so bare aggregate calls (e.g.
            // `ORDER BY COUNT(*)`) become column references to the GroupBy's
            // synthesised aggregate output. The rewrite de-duplicates by output
            // name, so an aggregate that already appears in SELECT shares its
            // column with the ORDER BY ref instead of producing a second
            // AggregateColumn. Done here — before the DISTINCT-vs-GroupBy
            // decision below — so an ORDER BY-only aggregate (no SELECT/LET
            // counterpart) still populates aggregateColumns and routes the
            // query through the real GroupByOperator path.
            if (statement.OrderBy is not null)
            {
                List<OrderByItem> rewrittenOrderBy = new(statement.OrderBy.Items.Count);
                foreach (OrderByItem item in statement.OrderBy.Items)
                {
                    Expression rewritten = RewriteAggregateExpression(
                        item.Expression, _functionRegistry, aggregateColumns);
                    rewrittenOrderBy.Add(ReferenceEquals(rewritten, item.Expression)
                        ? item
                        : item with { Expression = rewritten });
                }
                rewrittenOrderByClause = new OrderByClause(rewrittenOrderBy);
            }

            if (hasGroupBy && aggregateColumns.Count == 0 && statement.Having is null)
            {
                // GROUP BY without aggregates or HAVING is equivalent to DISTINCT
                // on the grouped columns. Replace the blocking GroupByOperator with
                // a streaming ProjectOperator + DistinctOperator so that a
                // downstream LIMIT can short-circuit without materialising all rows.
                List<SelectColumn> groupByProjection = new(groupByExpressions.Count);

                foreach (Expression expression in groupByExpressions)
                {
                    string name = QueryExplainer.FormatExpression(expression);
                    groupByProjection.Add(new SelectColumn(expression, name));
                }

                source = new ProjectOperator(source, groupByProjection, null);
                source = new DistinctOperator(source);
                ExecutionTracer.Write("GROUP BY without aggregates rewritten to streaming DISTINCT");
            }
            else
            {
                bool streamingSorted = CanUseStreamingAggregate(source, groupByExpressions);

                // Sort injection was previously used here to convert a hash aggregate into
                // a sort + streaming aggregate pair, which bounded memory at the cost of
                // sorting all input rows.  Now that GroupByOperator supports spill-to-disk
                // (both sequential and parallel paths), sort injection is unnecessary and
                // counterproductive: sorting N input rows before streaming is always more
                // expensive than hash-aggregating N rows into G groups and sorting only G.
                // Sort injection is therefore disabled.  The natural-ordering path
                // (CanUseStreamingAggregate returning true) remains for index-scan sources.

                source = new GroupByOperator(source, groupByExpressions, aggregateColumns, streamingSorted);

                if (streamingSorted)
                {
                    ExecutionTracer.Write("GROUP BY uses streaming aggregation (sorted input)");
                }

                // Apply HAVING as a filter on the grouped output.
                if (statement.Having is not null)
                {
                    Expression havingRewritten = RewriteAggregateExpression(
                        statement.Having, _functionRegistry, aggregateColumns);
                    source = new FilterOperator(source, havingRewritten);
                }
            }

            projectionColumns = rewrittenColumns;
        }

        // 3c. Window functions — insert WindowOperator after GROUP BY
        // (which may reference aggregate output columns) but before projection.
        // QUALIFY may also contain inline window function calls that must be
        // lifted into the same WindowOperator.
        bool hasWindowFunctions = HasWindowFunction(projectionColumns, _functionRegistry)
            || HasLetWindowFunction(letBindings);
        bool qualifyHasWindowFunctions = statement.Qualify is not null
            && ExpressionContainsWindowFunction(statement.Qualify);
        bool assertionsHaveWindowFunctions = HasAssertWindowFunction(assertions);
        Expression? qualifyExpression = statement.Qualify;

        if (hasWindowFunctions || qualifyHasWindowFunctions || assertionsHaveWindowFunctions)
        {
            List<WindowColumn> windowColumns = new();
            List<SelectColumn> windowRewrittenColumns = new();

            foreach (SelectColumn column in projectionColumns)
            {
                if (column is SelectAllColumns or SelectTableColumns)
                {
                    windowRewrittenColumns.Add(column);
                    continue;
                }

                Expression rewritten = RewriteWindowExpression(
                    column.Expression, _functionRegistry, windowColumns);
                windowRewrittenColumns.Add(new SelectColumn(rewritten, column.Alias));
            }

            // Rewrite window function calls inside LET binding expressions.
            if (letBindings is not null)
            {
                List<LetBinding> windowRewrittenLetBindings = new(letBindings.Count);
                foreach (LetBinding binding in letBindings)
                {
                    Expression rewritten = RewriteWindowExpression(
                        binding.Expression, _functionRegistry, windowColumns);
                    windowRewrittenLetBindings.Add(binding with { Expression = rewritten });
                }
                letBindings = windowRewrittenLetBindings;
            }

            // Rewrite any inline window function calls inside the QUALIFY expression
            // so they become column references to the same WindowOperator output.
            if (qualifyHasWindowFunctions)
            {
                qualifyExpression = RewriteWindowExpression(
                    qualifyExpression!, _functionRegistry, windowColumns);
            }

            // Rewrite window function calls inside ASSERT clause predicates and messages.
            if (assertionsHaveWindowFunctions && assertions is not null)
            {
                List<AssertClause> windowRewrittenAssertions = new(assertions.Count);
                foreach (AssertClause assertClause in assertions)
                {
                    Expression rewrittenPredicate = RewriteWindowExpression(
                        assertClause.Predicate, _functionRegistry, windowColumns);
                    Expression? rewrittenMessage = assertClause.Message is not null
                        ? RewriteWindowExpression(
                            assertClause.Message, _functionRegistry, windowColumns)
                        : null;
                    windowRewrittenAssertions.Add(assertClause with
                    {
                        Predicate = rewrittenPredicate,
                        Message = rewrittenMessage,
                    });
                }
                assertions = windowRewrittenAssertions;
            }

            source = new WindowOperator(source, windowColumns);
            projectionColumns = windowRewrittenColumns;
        }

        // 3d. SCAN fold expressions — insert FoldScanOperator after WindowOperator
        // but before projection. SCAN expressions produce running accumulators
        // where output[i] = f(output[i-1], input[i]).
        bool hasScanExpressions = HasScanExpression(projectionColumns)
            || HasLetScanExpression(letBindings);

        if (hasScanExpressions)
        {
            List<FoldScanColumn> scanColumns = new();
            List<SelectColumn> scanRewrittenColumns = new();

            foreach (SelectColumn column in projectionColumns)
            {
                if (column is SelectAllColumns or SelectTableColumns)
                {
                    scanRewrittenColumns.Add(column);
                    continue;
                }

                // For tuple SCAN expressions at the top level of a select column,
                // expand into multiple select columns (one per output alias).
                if (column.Expression is ScanExpression topScan && topScan.OutputAliases.Count > 1)
                {
                    RewriteScanExpression(column.Expression, scanColumns);
                    for (int i = 0; i < topScan.OutputAliases.Count; i++)
                    {
                        scanRewrittenColumns.Add(new SelectColumn(
                            new ColumnReference(null, topScan.OutputAliases[i]),
                            topScan.OutputAliases[i]));
                    }
                    continue;
                }

                Expression rewritten = RewriteScanExpression(column.Expression, scanColumns);
                scanRewrittenColumns.Add(new SelectColumn(rewritten, column.Alias));
            }

            // Rewrite SCAN expressions inside LET binding expressions.
            if (letBindings is not null)
            {
                List<LetBinding> scanRewrittenLetBindings = new(letBindings.Count);
                foreach (LetBinding binding in letBindings)
                {
                    Expression rewritten = RewriteScanExpression(binding.Expression, scanColumns);
                    scanRewrittenLetBindings.Add(binding with { Expression = rewritten });
                }
                letBindings = scanRewrittenLetBindings;
            }

            source = new Operators.FoldScanOperator(source, scanColumns);
            projectionColumns = scanRewrittenColumns;
        }

        // 3e. QUALIFY — post-window-function filter, analogous to HAVING for GROUP BY.
        // QUALIFY runs before projection, so resolve any SELECT aliases
        // and LET binding names (e.g. QUALIFY rn <= 2 where rn is a window alias).
        if (qualifyExpression is not null)
        {
            qualifyExpression = ResolveSelectAliases(qualifyExpression, projectionColumns);
            qualifyExpression = ResolveLetBindingReferences(qualifyExpression, letBindings);
            source = new FilterOperator(source, qualifyExpression);
        }

        // 3f. ASSERT — resolve predicate and message expressions against SELECT aliases
        // and LET binding names before they reach the ProjectOperator evaluator.
        if (assertions is not null)
        {
            List<AssertClause> resolvedAssertions = new(assertions.Count);
            foreach (AssertClause assertClause in assertions)
            {
                Expression resolvedPredicate = ResolveSelectAliases(
                    assertClause.Predicate, projectionColumns);
                resolvedPredicate = ResolveLetBindingReferences(resolvedPredicate, letBindings);
                Expression? resolvedMessage = assertClause.Message is not null
                    ? ResolveLetBindingReferences(
                        ResolveSelectAliases(assertClause.Message, projectionColumns),
                        letBindings)
                    : null;
                resolvedAssertions.Add(assertClause with
                {
                    Predicate = resolvedPredicate,
                    Message = resolvedMessage,
                });
            }
            assertions = resolvedAssertions;
        }

        // PIVOT / UNPIVOT support deleted alongside RowSerializer (the only remaining
        // legacy spill consumer). Re-add when a demo demands it; the parser still
        // recognises the tokens but the planner rejects them here.
        if (statement.Pivot is not null || statement.Unpivot is not null)
        {
            throw new NotSupportedException(
                "PIVOT / UNPIVOT are not currently supported. The operators were removed "
                + "during the spill-format consolidation; re-add when a query needs them.");
        }

        // 4. Apply SELECT projection (with LET bindings for memoized evaluation).
        //
        // When SELECT * is used with JOINs, expand the wildcard into per-table
        // wildcards (e.g. "a.*", "b.*") in SQL-text order. This ensures the
        // ProjectOperator emits columns in the original FROM/JOIN declaration
        // order even when greedy join reordering has swapped the physical probe
        // and build sides.
        if (statement.Joins is not null
            && projectionColumns.Count == 1
            && projectionColumns[0] is SelectAllColumns selectAll)
        {
            List<SelectColumn> expanded = new();

            if (statement.From is not null)
            {
                string? fromAlias = GetSourceAlias(statement.From.Source);
                if (fromAlias is not null)
                {
                    expanded.Add(new SelectTableColumns(fromAlias, ExcludedColumns: selectAll.ExcludedColumns, ReplacedColumns: selectAll.ReplacedColumns, QualifyOutput: true));
                }
            }

            foreach (JoinClause join in statement.Joins)
            {
                string? joinAlias = GetSourceAlias(join.Source);
                if (joinAlias is not null)
                {
                    expanded.Add(new SelectTableColumns(joinAlias, ExcludedColumns: selectAll.ExcludedColumns, ReplacedColumns: selectAll.ReplacedColumns, QualifyOutput: true));
                }
            }

            if (expanded.Count > 0)
            {
                projectionColumns = expanded;
            }
        }

        {
        bool hasStarOnly = projectionColumns.Count == 1
            && projectionColumns[0] is SelectAllColumns { ExcludedColumns: null, ReplacedColumns: null }
            && letBindings is null
            && assertions is null;
        if (!hasStarOnly)
        {
            source = new ProjectOperator(source, projectionColumns, letBindings, assertions);
        }
        }

        // 5. Apply DISTINCT — streaming deduplication on projected output.
        if (statement.Distinct)
        {
            // When SELECT DISTINCT is combined with ORDER BY, every ORDER BY expression
            // must appear in the SELECT list, otherwise the result is ambiguous.
            if (statement.OrderBy is not null)
            {
                ValidateDistinctOrderBy(statement.Columns, statement.OrderBy);
            }

            source = new DistinctOperator(source);
        }

        // 6. Apply ORDER BY — use index scan when a sorted index covers the sort column,
        //    or elide entirely when a streaming GROUP BY already produces sorted output.
        if (statement.OrderBy is not null)
        {
            // When the query had GROUP BY / aggregates, ORDER BY items have
            // been rewritten so bare aggregate calls reference the GroupBy's
            // output columns. Otherwise fall back to the original parsed clause.
            OrderByClause effectiveOrderBy = rewrittenOrderByClause ?? statement.OrderBy;

            if (OutputOrderingSatisfiesOrderBy(source, effectiveOrderBy))
            {
                ExecutionTracer.Write("ORDER BY elided — output already sorted by streaming GROUP BY");
            }
            else if (!TryReplaceWithIndexScan(ref source, effectiveOrderBy))
            {
                int? topNRows = statement.Limit is not null
                    ? statement.Limit.Value + (statement.Offset ?? 0)
                    : null;

                source = new OrderByOperator(source, effectiveOrderBy.Items, topNRows);
            }
        }

        // 7. Apply LIMIT/OFFSET.
        if (statement.Limit is not null)
        {
            source = new LimitOperator(source, statement.Limit.Value, statement.Offset ?? 0);
        }

        return source;
    }

    /// <summary>
    /// Plans a statement with scalar subquery rewriting. Uncorrelated subqueries are
    /// constant-folded at plan time. Correlated subqueries are rewritten to synthetic
    /// column references and injected as <see cref="Operators.ScalarSubqueryOperator"/>
    /// wrappers around the source operator.
    /// </summary>
    private async Task<IQueryOperator> PlanCoreWithSubqueriesAsync(
        SelectStatement statement,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        // Determine if the statement contains any SubqueryExpressions.
        if (!ContainsSubqueryExpression(statement))
        {
            return PlanCore(statement);
        }

        // Collect outer-scope table aliases for correlation detection.
        HashSet<string> outerAliases = new(StringComparer.OrdinalIgnoreCase);
        if (statement.From is not null)
        {
            CollectSourceAliases(statement.From.Source, outerAliases);
        }

        if (statement.Joins is not null)
        {
            foreach (JoinClause join in statement.Joins)
            {
                CollectSourceAliases(join.Source, outerAliases);
            }
        }

        // Rewrite semi-join subqueries (IN/NOT IN/EXISTS/NOT EXISTS) in WHERE
        // before the scalar SubqueryRewriter pass, so these nodes are consumed first.
        SemiJoinRewriter.RewriteResult semiJoinResult = await SemiJoinRewriter.RewriteAsync(
            statement.Where, outerAliases, this, context, cancellationToken).ConfigureAwait(false);

        // Rewrite all expression clauses that may contain SubqueryExpressions.
        List<SubqueryRewriter.CorrelatedSubquery> allCorrelated = [];
        List<SubqueryRewriter.DecorrelatedScalarJoin> allDecorrelated = [];

        Expression? rewrittenWhere = semiJoinResult.RemainingWhere;
        if (rewrittenWhere is not null)
        {
            SubqueryRewriter.RewriteResult result = await SubqueryRewriter.RewriteAsync(
                rewrittenWhere, outerAliases, this, context, _functionRegistry,
                cancellationToken).ConfigureAwait(false);
            rewrittenWhere = result.Expression;
            allCorrelated.AddRange(result.CorrelatedSubqueries);
            allDecorrelated.AddRange(result.DecorrelatedScalarJoins);
        }

        List<SelectColumn>? rewrittenColumns = null;
        for (int index = 0; index < statement.Columns.Count; index++)
        {
            SelectColumn column = statement.Columns[index];
            if (column is SelectAllColumns or SelectTableColumns)
            {
                continue;
            }

            SubqueryRewriter.RewriteResult result = await SubqueryRewriter.RewriteAsync(
                column.Expression, outerAliases, this, context, _functionRegistry,
                cancellationToken).ConfigureAwait(false);

            if (!ReferenceEquals(result.Expression, column.Expression))
            {
                rewrittenColumns ??= new List<SelectColumn>(statement.Columns);
                rewrittenColumns[index] = new SelectColumn(result.Expression, column.Alias);
                allCorrelated.AddRange(result.CorrelatedSubqueries);
                allDecorrelated.AddRange(result.DecorrelatedScalarJoins);
            }
        }

        Expression? rewrittenHaving = statement.Having;
        if (rewrittenHaving is not null)
        {
            SubqueryRewriter.RewriteResult result = await SubqueryRewriter.RewriteAsync(
                rewrittenHaving, outerAliases, this, context, _functionRegistry,
                cancellationToken).ConfigureAwait(false);
            rewrittenHaving = result.Expression;
            allCorrelated.AddRange(result.CorrelatedSubqueries);
            allDecorrelated.AddRange(result.DecorrelatedScalarJoins);
        }

        IReadOnlyList<JoinClause>? rewrittenJoins = statement.Joins;
        if (statement.Joins is not null)
        {
            List<JoinClause>? joinList = null;
            for (int index = 0; index < statement.Joins.Count; index++)
            {
                JoinClause join = statement.Joins[index];
                if (join.OnCondition is null)
                {
                    continue;
                }

                SubqueryRewriter.RewriteResult result = await SubqueryRewriter.RewriteAsync(
                    join.OnCondition, outerAliases, this, context, _functionRegistry,
                    cancellationToken).ConfigureAwait(false);

                if (!ReferenceEquals(result.Expression, join.OnCondition))
                {
                    joinList ??= new List<JoinClause>(statement.Joins);
                    joinList[index] = new JoinClause(join.Type, join.Source, result.Expression);
                    allCorrelated.AddRange(result.CorrelatedSubqueries);
                    allDecorrelated.AddRange(result.DecorrelatedScalarJoins);
                }
            }

            if (joinList is not null)
            {
                rewrittenJoins = joinList;
            }
        }

        // Reconstruct the statement with rewritten expressions.
        SelectStatement rewrittenStatement = new(
            rewrittenColumns is not null ? rewrittenColumns : statement.Columns,
            statement.From,
            statement.Into,
            rewrittenJoins,
            rewrittenWhere,
            statement.GroupBy,
            rewrittenHaving,
            statement.Qualify,
            statement.Assertions,
            statement.Pivot,
            statement.Unpivot,
            statement.OrderBy,
            statement.Limit,
            statement.Offset,
            statement.Distinct,
            statement.CommonTableExpressions);

        // Build a source transform that injects ScalarSubqueryOperator wrappers,
        // decorrelated LEFT JOINs, and semi-join operators between the source
        // (Scan+Joins) and the rest of the pipeline (Filter/Project/etc.).
        // This ensures synthetic columns and semi-join filtering are applied
        // before any operator that references them.
        Func<IQueryOperator, IQueryOperator>? sourceTransform = null;
        if (allCorrelated.Count > 0 || allDecorrelated.Count > 0 || semiJoinResult.SemiJoins.Count > 0)
        {
            sourceTransform = source =>
            {
                // Decorrelated scalar subqueries: inject as LEFT JOINs on grouped inner plans.
                // These must be injected before correlated ScalarSubqueryOperators so that
                // any remaining correlated subqueries see the decorrelated columns.
                foreach (SubqueryRewriter.DecorrelatedScalarJoin decorrelated in allDecorrelated)
                {
                    source = new JoinOperator(
                        source, decorrelated.InnerPlan, JoinType.Left,
                        decorrelated.OnCondition);
                }

                foreach (SubqueryRewriter.CorrelatedSubquery correlated in allCorrelated)
                {
                    IQueryOperator innerPlan = Plan(correlated.InnerQuery);
                    source = new Operators.ScalarSubqueryOperator(source, innerPlan, correlated.SyntheticColumnName);
                }

                foreach (SemiJoinRewriter.SemiJoinDescriptor semiJoin in semiJoinResult.SemiJoins)
                {
                    source = new JoinOperator(
                        source, semiJoin.InnerPlan, semiJoin.JoinType,
                        semiJoin.OnCondition, semiJoin.NullSensitiveAntiSemi);
                }

                return source;
            };
        }

        // Plan the rewritten statement through the standard pipeline with the source transform.
        return PlanCore(rewrittenStatement, sourceTransform);
    }

    /// <summary>
    /// Checks whether a <see cref="SelectStatement"/> contains any
    /// <see cref="SubqueryExpression"/> nodes in its expression clauses.
    /// </summary>
    private static bool ContainsSubqueryExpression(SelectStatement statement)
    {
        if (statement.Where is not null && ContainsSubquery(statement.Where))
        {
            return true;
        }

        foreach (SelectColumn column in statement.Columns)
        {
            if (column is SelectAllColumns or SelectTableColumns)
            {
                continue;
            }

            if (ContainsSubquery(column.Expression))
            {
                return true;
            }
        }

        if (statement.Having is not null && ContainsSubquery(statement.Having))
        {
            return true;
        }

        if (statement.Qualify is not null && ContainsSubquery(statement.Qualify))
        {
            return true;
        }

        if (statement.Joins is not null)
        {
            foreach (JoinClause join in statement.Joins)
            {
                if (join.OnCondition is not null && ContainsSubquery(join.OnCondition))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Recursively checks if an expression tree contains a <see cref="SubqueryExpression"/>.
    /// </summary>
    private static bool ContainsSubquery(Expression expression)
    {
        return expression switch
        {
            SubqueryExpression => true,
            InSubqueryExpression => true,
            ExistsExpression => true,
            BinaryExpression binary => ContainsSubquery(binary.Left) || ContainsSubquery(binary.Right),
            UnaryExpression unary => ContainsSubquery(unary.Operand),
            FunctionCallExpression function => function.Arguments.Any(ContainsSubquery),
            InExpression inExpr => ContainsSubquery(inExpr.Expression) || inExpr.Values.Any(ContainsSubquery),
            BetweenExpression between => ContainsSubquery(between.Expression) ||
                ContainsSubquery(between.Low) || ContainsSubquery(between.High),
            IsNullExpression isNull => ContainsSubquery(isNull.Expression),
            CastExpression cast => ContainsSubquery(cast.Expression),
            CaseExpression caseExpr => (caseExpr.Operand is not null && ContainsSubquery(caseExpr.Operand)) ||
                caseExpr.WhenClauses.Any(clause => ContainsSubquery(clause.Condition) || ContainsSubquery(clause.Result)) ||
                (caseExpr.ElseResult is not null && ContainsSubquery(caseExpr.ElseResult)),
            _ => false,
        };
    }

    /// <summary>
    /// Pushes predicates that reference only the given set of aliases below the
    /// current operator as filter nodes. Pushed predicates are removed from the list.
    /// When the underlying source is a <see cref="ScanOperator"/>, the predicate
    /// is also added as an advisory filter hint for statistics-based partition pruning.
    /// </summary>
    private static IQueryOperator PushPredicatesBelow(
        IQueryOperator operatorNode,
        HashSet<string> availableAliases,
        List<Expression> predicates)
    {
        IQueryOperator result = operatorNode;

        for (int index = predicates.Count - 1; index >= 0; index--)
        {
            Expression predicate = predicates[index];
            HashSet<string> predicateAliases = ColumnReferenceCollector.CollectTableAliases(predicate);

            // Push if all referenced aliases are available on this side,
            // or if the predicate has no table-qualified references (global).
            if (predicateAliases.Count == 0 || predicateAliases.IsSubsetOf(availableAliases))
            {
                result = new FilterOperator(result, predicate);
                predicates.RemoveAt(index);

                // Pass the predicate as a filter hint to the underlying scan
                // so filterable providers can use statistics to skip partitions.
                AddFilterHintToScan(operatorNode, predicate);
            }
        }

        return result;
    }

    /// <summary>
    /// Walks down the operator tree to find the <see cref="ScanOperator"/>
    /// and adds the predicate as an advisory filter hint.
    /// </summary>
    private static void AddFilterHintToScan(IQueryOperator operatorNode, Expression predicate)
    {
        IQueryOperator current = operatorNode;
        while (true)
        {
            switch (current)
            {
                case ScanOperator scan:
                    scan.AddFilterHint(predicate);
                    return;
                case AliasOperator alias:
                    current = alias.Source;
                    break;
                case FilterOperator filter:
                    current = filter.Source;
                    break;
                default:
                    return; // Not a simple scan chain — cannot push hints.
            }
        }
    }

    /// <summary>
    /// Derives transitive equality predicates from equi-join conditions and existing
    /// pending predicates. When a pending predicate says <c>A.x = &lt;literal&gt;</c>
    /// and the join ON condition contains <c>A.x = B.x</c>, this method derives
    /// <c>B.x = &lt;literal&gt;</c> and appends it to the pending list so it can be
    /// pushed to B's scan for index/statistics pruning.
    /// </summary>
    /// <param name="onCondition">The join ON condition to extract equi-join pairs from.</param>
    /// <param name="pendingPredicates">
    /// The list of pending WHERE predicates. Newly derived predicates are appended.
    /// </param>
    private static void DeriveTransitivePredicates(
        Expression onCondition,
        List<Expression> pendingPredicates)
    {
        // Extract equi-join pairs: (A.col, B.col) from the ON condition.
        List<(ColumnReference Left, ColumnReference Right)> equiPairs = new();
        ExtractEquiJoinPairs(onCondition, equiPairs);

        if (equiPairs.Count == 0)
        {
            return;
        }

        // Collect existing literal equality predicates: alias.col = literal.
        // We scan the current pendingPredicates snapshot (not the derived ones)
        // to avoid infinite chaining in a single pass.
        int originalCount = pendingPredicates.Count;
        Dictionary<(string Alias, string Column), LiteralExpression> literalEqualities = new();

        for (int i = 0; i < originalCount; i++)
        {
            if (pendingPredicates[i] is not BinaryExpression binary
                || binary.Operator != BinaryOperator.Equal)
            {
                continue;
            }

            if (binary.Left is ColumnReference colRef && binary.Right is LiteralExpression lit
                && colRef.TableName is not null)
            {
                literalEqualities[(colRef.TableName, colRef.ColumnName)] = lit;
            }
            else if (binary.Right is ColumnReference colRef2 && binary.Left is LiteralExpression lit2
                && colRef2.TableName is not null)
            {
                literalEqualities[(colRef2.TableName, colRef2.ColumnName)] = lit2;
            }
        }

        if (literalEqualities.Count == 0)
        {
            return;
        }

        // For each equi-join pair, check if one side has a literal equality
        // and derive a predicate for the other side.
        foreach ((ColumnReference leftCol, ColumnReference rightCol) in equiPairs)
        {
            if (leftCol.TableName is not null
                && literalEqualities.TryGetValue(
                    (leftCol.TableName, leftCol.ColumnName), out LiteralExpression? leftLiteral))
            {
                // A.x = literal AND A.x = B.x → derive B.x = literal
                BinaryExpression derived = new(rightCol, BinaryOperator.Equal, leftLiteral);
                pendingPredicates.Add(derived);
            }

            if (rightCol.TableName is not null
                && literalEqualities.TryGetValue(
                    (rightCol.TableName, rightCol.ColumnName), out LiteralExpression? rightLiteral))
            {
                // B.x = literal AND A.x = B.x → derive A.x = literal
                BinaryExpression derived = new(leftCol, BinaryOperator.Equal, rightLiteral);
                pendingPredicates.Add(derived);
            }
        }
    }

    /// <summary>
    /// Extracts column-to-column equality pairs from a join ON condition.
    /// Only top-level AND-connected equalities between two qualified column references
    /// are extracted.
    /// </summary>
    private static void ExtractEquiJoinPairs(
        Expression expression,
        List<(ColumnReference Left, ColumnReference Right)> pairs)
    {
        if (expression is BinaryExpression binary)
        {
            if (binary.Operator == BinaryOperator.And)
            {
                ExtractEquiJoinPairs(binary.Left, pairs);
                ExtractEquiJoinPairs(binary.Right, pairs);
                return;
            }

            if (binary.Operator == BinaryOperator.Equal
                && binary.Left is ColumnReference leftCol
                && binary.Right is ColumnReference rightCol)
            {
                pairs.Add((leftCol, rightCol));
            }
        }
    }

    /// <summary>
    /// Removes LEFT JOINs whose right-side table is not referenced anywhere in
    /// the query output (SELECT, WHERE, GROUP BY, HAVING, ORDER BY, LET, QUALIFY)
    /// and is not required by any other surviving join's ON condition. This is
    /// safe because a LEFT JOIN to an unreferenced table cannot filter rows — it
    /// preserves all left-side rows — and only adds hash-table and I/O cost.
    /// </summary>
    /// <remarks>
    /// Elimination is iterative: removing one join may make another join's
    /// right-side table unreferenced (cascading elimination). Only LEFT JOINs
    /// are candidates; INNER/RIGHT/CROSS joins can filter or multiply rows and
    /// are never removed.
    /// </remarks>
    private static void EliminateUnusedJoins(
        SelectStatement statement,
        List<(JoinClause Join, IQueryOperator Operator, HashSet<string> Aliases)> plannedJoins)
    {
        // Collect table aliases referenced in query output clauses (not JOIN ON).
        HashSet<string> outputReferenced = new(StringComparer.OrdinalIgnoreCase);

        foreach (SelectColumn column in statement.Columns)
        {
            if (column is SelectAllColumns)
            {
                return; // SELECT * needs all tables — no elimination possible.
            }

            if (column is SelectTableColumns tableColumns)
            {
                outputReferenced.Add(tableColumns.TableName);
                continue;
            }

            foreach (string alias in ColumnReferenceCollector.CollectTableAliases(column.Expression))
            {
                outputReferenced.Add(alias);
            }
        }

        if (statement.Where is not null)
        {
            foreach (string alias in ColumnReferenceCollector.CollectTableAliases(statement.Where))
            {
                outputReferenced.Add(alias);
            }
        }

        if (statement.GroupBy is not null)
        {
            foreach (Expression expression in statement.GroupBy.Expressions)
            {
                foreach (string alias in ColumnReferenceCollector.CollectTableAliases(expression))
                {
                    outputReferenced.Add(alias);
                }
            }
        }

        if (statement.Having is not null)
        {
            foreach (string alias in ColumnReferenceCollector.CollectTableAliases(statement.Having))
            {
                outputReferenced.Add(alias);
            }
        }

        if (statement.OrderBy is not null)
        {
            foreach (OrderByItem item in statement.OrderBy.Items)
            {
                foreach (string alias in ColumnReferenceCollector.CollectTableAliases(item.Expression))
                {
                    outputReferenced.Add(alias);
                }
            }
        }

        if (statement.LetBindings is not null)
        {
            foreach (LetBinding binding in statement.LetBindings)
            {
                foreach (string alias in ColumnReferenceCollector.CollectTableAliases(binding.Expression))
                {
                    outputReferenced.Add(alias);
                }
            }
        }

        if (statement.Qualify is not null)
        {
            foreach (string alias in ColumnReferenceCollector.CollectTableAliases(statement.Qualify))
            {
                outputReferenced.Add(alias);
            }
        }

        // Iteratively eliminate LEFT JOINs whose right-side alias is unreferenced
        // by both the output clauses and other surviving joins' ON conditions.
        bool changed = true;

        while (changed)
        {
            changed = false;

            for (int index = plannedJoins.Count - 1; index >= 0; index--)
            {
                (JoinClause join, _, HashSet<string> aliases) = plannedJoins[index];

                if (join.Type != JoinType.Left)
                {
                    continue;
                }

                // Check if any of this join's right-side aliases are needed.
                bool needed = false;

                foreach (string alias in aliases)
                {
                    if (outputReferenced.Contains(alias))
                    {
                        needed = true;
                        break;
                    }
                }

                if (needed)
                {
                    continue;
                }

                // Check if any other surviving join's ON condition references this alias.
                bool referencedByOtherJoin = false;

                for (int otherIndex = 0; otherIndex < plannedJoins.Count; otherIndex++)
                {
                    if (otherIndex == index)
                    {
                        continue;
                    }

                    JoinClause otherJoin = plannedJoins[otherIndex].Join;

                    if (otherJoin.OnCondition is null)
                    {
                        continue;
                    }

                    HashSet<string> onAliases =
                        ColumnReferenceCollector.CollectTableAliases(otherJoin.OnCondition);

                    foreach (string alias in aliases)
                    {
                        if (onAliases.Contains(alias))
                        {
                            referencedByOtherJoin = true;
                            break;
                        }
                    }

                    if (referencedByOtherJoin)
                    {
                        break;
                    }
                }

                if (!referencedByOtherJoin)
                {
                    string joinLabel = string.Join(", ", aliases);
                    ExecutionTracer.Write($"JOIN ELIMINATION  removed {joinLabel} (unreferenced LEFT JOIN)");
                    plannedJoins.RemoveAt(index);
                    changed = true;
                }
            }
        }
    }

    /// <summary>
    /// Attempts greedy join reordering: the source with the largest estimated row
    /// count becomes the new FROM (probe/streaming side) so that LIMIT can short-circuit
    /// early, and smaller tables are placed on the build side.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only applied when every join is a non-lateral <see cref="JoinType.Inner"/> join
    /// and all sources have estimated row counts. Cross joins are excluded because
    /// they lack an ON condition that can be checked for alias connectivity.
    /// </para>
    /// <para>
    /// This is a heuristic — the roadmap cost-based optimizer will replace it.
    /// </para>
    /// </remarks>
    /// <returns><c>true</c> if a reordering was produced and the out parameters are populated.</returns>
    private static bool TryReorderJoins(
        IQueryOperator fromOperator,
        HashSet<string> fromAliases,
        List<(JoinClause Join, IQueryOperator Operator, HashSet<string> Aliases)> plannedJoins,
        string? preferredProbeTableAlias,
        [NotNullWhen(true)] out IQueryOperator? reorderedSource,
        [NotNullWhen(true)] out HashSet<string>? reorderedFromAliases,
        [NotNullWhen(true)] out List<(JoinClause Join, IQueryOperator Operator, HashSet<string> Aliases)>? reorderedJoins)
    {
        reorderedSource = null;
        reorderedFromAliases = null;
        reorderedJoins = null;

        // Gate: all joins must be non-lateral INNER with an ON condition.
        foreach ((JoinClause join, _, _) in plannedJoins)
        {
            if (join.IsLateral || join.Type != JoinType.Inner || join.OnCondition is null)
            {
                ExecutionTracer.Initialize();
                ExecutionTracer.Write("JOIN REORDER  skipped: non-INNER or lateral join present");
                return false;
            }
        }

        // Collect all sources into a pool with their estimated row counts.
        // Index 0 is the FROM source (no JoinClause); rest are join sources.
        int totalSources = 1 + plannedJoins.Count;
        long?[] rowCounts = new long?[totalSources];

        rowCounts[0] = GetEstimatedRowCount(fromOperator);
        if (rowCounts[0] is null)
        {
            ExecutionTracer.Initialize();
            ExecutionTracer.Write($"JOIN REORDER  skipped: no row count for FROM={GetOperatorName(fromOperator)}");
            return false;
        }

        for (int index = 0; index < plannedJoins.Count; index++)
        {
            rowCounts[index + 1] = GetEstimatedRowCount(plannedJoins[index].Operator);
            if (rowCounts[index + 1] is null)
            {
                ExecutionTracer.Initialize();
                ExecutionTracer.Write($"JOIN REORDER  skipped: no row count for JOIN[{index}]={GetOperatorName(plannedJoins[index].Operator)}");
                return false;
            }
        }

        if (ExecutionTracer.IsEnabled)
        {
            ExecutionTracer.WriteSeparator();
            ExecutionTracer.Write($"JOIN REORDER  evaluating {totalSources} sources");
            ExecutionTracer.Write($"  [0] FROM  {GetOperatorName(fromOperator)}  rows={rowCounts[0]:N0}");
            for (int index = 0; index < plannedJoins.Count; index++)
            {
                ExecutionTracer.Write($"  [{index + 1}] JOIN  {GetOperatorName(plannedJoins[index].Operator)}  rows={rowCounts[index + 1]:N0}");
            }
        }

        // Find the source with the largest estimated row count — it becomes the probe
        // when no ORDER BY sort-table preference is in effect.
        int largestIndex = 0;
        long largestCount = rowCounts[0]!.Value;

        for (int index = 1; index < totalSources; index++)
        {
            if (rowCounts[index]!.Value > largestCount)
            {
                largestCount = rowCounts[index]!.Value;
                largestIndex = index;
            }
        }

        // Determine the chosen outermost probe.
        // When a preferred probe table exists (ORDER BY table with a sorted column index),
        // use it so that the sort can later be eliminated by replacing its scan with an
        // IndexScanOperator. The existing row-count heuristic is the fallback.
        int chosenIndex = largestIndex;

        if (preferredProbeTableAlias is not null)
        {
            if (fromAliases.Contains(preferredProbeTableAlias))
            {
                // The preferred table is already the outermost FROM — no reordering
                // is needed and we must not displace it with the largest-table heuristic.
                ExecutionTracer.Write($"JOIN REORDER  skipped: ORDER BY table '{preferredProbeTableAlias}' is already outermost FROM");
                return false;
            }

            for (int index = 0; index < plannedJoins.Count; index++)
            {
                if (plannedJoins[index].Aliases.Contains(preferredProbeTableAlias))
                {
                    chosenIndex = index + 1;
                    ExecutionTracer.Write($"JOIN REORDER  promoting ORDER BY table '{preferredProbeTableAlias}' as outermost probe for sort elimination");
                    break;
                }
            }
        }

        // If the chosen source is already the FROM, no reordering is needed.
        if (chosenIndex == 0)
        {
            ExecutionTracer.Write($"JOIN REORDER  skipped: FROM is already largest ({GetOperatorName(fromOperator)} rows={largestCount:N0})");
            return false;
        }

        ExecutionTracer.Write($"JOIN REORDER  new probe (FROM) = {GetOperatorName(plannedJoins[chosenIndex - 1].Operator)}  rows={rowCounts[chosenIndex]:N0}");

        // Build the pool of remaining sources to schedule.
        // Each entry: (Operator, Aliases, RowCount, JoinClause or null for the original FROM).
        List<(IQueryOperator Operator, HashSet<string> Aliases, long RowCount, JoinClause? Join)> remaining = new(totalSources - 1);

        // The original FROM becomes a joinable source — it keeps the ON condition
        // from the join that previously connected the new probe to the tree.
        // We'll assign ON conditions during the greedy scheduling below.
        remaining.Add((fromOperator, fromAliases, rowCounts[0]!.Value, null));

        for (int index = 0; index < plannedJoins.Count; index++)
        {
            if (index + 1 == chosenIndex)
            {
                continue; // Skip the one we chose as the new FROM.
            }

            remaining.Add((plannedJoins[index].Operator, plannedJoins[index].Aliases,
                rowCounts[index + 1]!.Value, plannedJoins[index].Join));
        }

        // Collect all ON conditions from the original join list. These form the
        // pool of predicates that must be distributed to the reordered joins.
        // Each ON condition connects two sets of aliases.
        List<Expression> onConditionPool = new(plannedJoins.Count);
        foreach ((JoinClause join, _, _) in plannedJoins)
        {
            onConditionPool.Add(join.OnCondition!);
        }

        // Set up the new FROM from the chosen source.
        IQueryOperator newFrom;
        HashSet<string> joinedAliases;

        int chosenJoinIndex = chosenIndex - 1;
        newFrom = plannedJoins[chosenJoinIndex].Operator;
        joinedAliases = new HashSet<string>(plannedJoins[chosenJoinIndex].Aliases, StringComparer.OrdinalIgnoreCase);

        // Greedy scheduling: at each step pick the smallest remaining source whose
        // ON condition is satisfiable (all referenced aliases are in the joined set).
        List<(JoinClause Join, IQueryOperator Operator, HashSet<string> Aliases)> result = new(remaining.Count);

        while (remaining.Count > 0)
        {
            int bestIndex = -1;
            long bestCount = long.MaxValue;

            for (int index = 0; index < remaining.Count; index++)
            {
                // Check that at least one ON condition is satisfiable when we add this source.
                HashSet<string> candidateAliases = remaining[index].Aliases;

                bool hasSatisfiableCondition = false;
                foreach (Expression onCondition in onConditionPool)
                {
                    HashSet<string> conditionAliases = ColumnReferenceCollector.CollectTableAliases(onCondition);

                    // The condition is satisfiable if every alias it references is
                    // either in the already-joined set or in this candidate's aliases.
                    HashSet<string> combined = new(joinedAliases, StringComparer.OrdinalIgnoreCase);
                    foreach (string alias in candidateAliases)
                    {
                        combined.Add(alias);
                    }

                    if (conditionAliases.IsSubsetOf(combined))
                    {
                        hasSatisfiableCondition = true;
                        break;
                    }
                }

                if (!hasSatisfiableCondition)
                {
                    continue;
                }

                if (remaining[index].RowCount < bestCount)
                {
                    bestCount = remaining[index].RowCount;
                    bestIndex = index;
                }
            }

            if (bestIndex == -1)
            {
                // No remaining source has a satisfiable ON condition — cannot reorder.
                return false;
            }

            // Consume the chosen source and assign its applicable ON conditions.
            (IQueryOperator chosenOperator, HashSet<string> chosenAliases, _, _) = remaining[bestIndex];
            remaining.RemoveAt(bestIndex);

            // Collect all ON conditions that are now satisfiable with the joined set + chosen source.
            HashSet<string> newJoined = new(joinedAliases, StringComparer.OrdinalIgnoreCase);
            foreach (string alias in chosenAliases)
            {
                newJoined.Add(alias);
            }

            List<Expression> applicableConditions = new();
            for (int index = onConditionPool.Count - 1; index >= 0; index--)
            {
                HashSet<string> conditionAliases = ColumnReferenceCollector.CollectTableAliases(onConditionPool[index]);

                if (conditionAliases.IsSubsetOf(newJoined))
                {
                    applicableConditions.Add(onConditionPool[index]);
                    onConditionPool.RemoveAt(index);
                }
            }

            // Combine applicable conditions into a single ON expression.
            Expression onExpression = applicableConditions.Count == 1
                ? applicableConditions[0]
                : CombineWithAnd(applicableConditions);

            // The TableSource is not used after planning — supply a placeholder reference.
            JoinClause reorderedJoinClause = new(JoinType.Inner, new TableReference("_reordered_"), onExpression, false);
            result.Add((reorderedJoinClause, chosenOperator, chosenAliases));

            // Expand the joined alias set.
            joinedAliases = newJoined;
        }

        reorderedSource = newFrom;
        reorderedFromAliases = new HashSet<string>(
            plannedJoins[chosenIndex - 1].Aliases,
            StringComparer.OrdinalIgnoreCase);
        reorderedJoins = result;

        if (ExecutionTracer.IsEnabled)
        {
            ExecutionTracer.Write("JOIN REORDER  final build order (smallest first):");
            for (int index = 0; index < result.Count; index++)
            {
                ExecutionTracer.Write($"  build[{index}]  {GetOperatorName(result[index].Operator)}");
            }
        }

        return true;
    }

    /// <summary>
    /// Walks through wrapping operators (<see cref="AliasOperator"/>, <see cref="FilterOperator"/>)
    /// to find the underlying <see cref="ScanOperator"/> and returns its table name,
    /// used in execution trace output.
    /// </summary>
    private static string GetOperatorName(IQueryOperator op)
    {
        IQueryOperator current = op;
        while (true)
        {
            if (current is ScanOperator scan)
                return scan.TableProvider.Name;
            if (current is AliasOperator alias)
                current = alias.Source;
            else if (current is FilterOperator filter)
                current = filter.Source;
            else
                return current.GetType().Name;
        }
    }

    /// <summary>
    /// Walks through wrapping operators (<see cref="AliasOperator"/>, <see cref="FilterOperator"/>)
    /// to find the underlying <see cref="ScanOperator"/> and returns its estimated row count.
    /// </summary>
    /// <returns>The estimated row count, or <c>null</c> if no <see cref="ScanOperator"/> is found
    /// or it lacks an estimate.</returns>
    private static long? GetEstimatedRowCount(IQueryOperator operatorNode)
    {
        IQueryOperator current = operatorNode;
        while (true)
        {
            switch (current)
            {
                case ScanOperator scan:
                    return scan.TableRowCount;
                case AliasOperator alias:
                    current = alias.Source;
                    break;
                case FilterOperator filter:
                    current = filter.Source;
                    break;
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// Inspects the operator tree to determine the output sort ordering, if any.
    /// Walks through transparent wrappers (<see cref="AliasOperator"/>,
    /// <see cref="FilterOperator"/>, <see cref="ProjectOperator"/>) that preserve
    /// row order and extracts ordering from operators that produce sorted output
    /// (<see cref="IndexScanOperator"/>, <see cref="MergeJoinOperator"/>).
    /// </summary>
    /// <returns>
    /// A list of <c>(ColumnName, Descending)</c> tuples describing the sort order,
    /// or <c>null</c> if the ordering is unknown or destroyed by a blocking operator.
    /// </returns>
    private static IReadOnlyList<(string ColumnName, bool Descending)>? GetOutputOrdering(
        IQueryOperator operatorNode)
    {
        IQueryOperator current = operatorNode;
        while (true)
        {
            switch (current)
            {
                case IndexScanOperator indexScan:
                    return [(indexScan.ColumnName, indexScan.Descending)];
                case MergeJoinOperator mergeJoin:
                    // Merge join preserves left-side ordering.
                    current = mergeJoin.Left;
                    break;
                case JoinOperator { PreferIndexNestedLoop: true } indexNlj:
                    // Index nested-loop join streams probe rows in left-side order,
                    // so the output ordering is inherited from the probe (left) side.
                    current = indexNlj.Left;
                    break;
                case GroupByOperator { StreamingSorted: true } groupBy:
                {
                    // Streaming GROUP BY emits groups in key order, inheriting the
                    // sort direction from the source. Build the output ordering from
                    // the GROUP BY key expressions matched against the source ordering.
                    IReadOnlyList<(string ColumnName, bool Descending)>? sourceOrdering =
                        GetOutputOrdering(groupBy.Source);

                    if (sourceOrdering is null)
                    {
                        return null;
                    }

                    List<(string, bool)> result = new(groupBy.GroupByExpressions.Count);

                    for (int index = 0; index < groupBy.GroupByExpressions.Count; index++)
                    {
                        if (groupBy.GroupByExpressions[index] is not ColumnReference column)
                        {
                            return null;
                        }

                        // Find the direction from the source ordering for this column.
                        bool found = false;
                        for (int orderIndex = 0; orderIndex < sourceOrdering.Count; orderIndex++)
                        {
                            if (string.Equals(column.ColumnName, sourceOrdering[orderIndex].ColumnName,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                result.Add((column.ColumnName, sourceOrdering[orderIndex].Descending));
                                found = true;
                                break;
                            }
                        }

                        if (!found)
                        {
                            return null;
                        }
                    }

                    return result;
                }
                case AliasOperator alias:
                    current = alias.Source;
                    break;
                case FilterOperator filter:
                    current = filter.Source;
                    break;
                case ProjectOperator project:
                    current = project.Source;
                    break;
                case OrderByOperator orderBy:
                {
                    // An ORDER BY operator establishes a known output ordering from its
                    // sort criteria, provided every item is a simple ColumnReference.
                    List<(string, bool)> ordering = new(orderBy.OrderByItems.Count);
                    foreach (OrderByItem item in orderBy.OrderByItems)
                    {
                        if (item.Expression is not ColumnReference col)
                        {
                            return null;
                        }

                        ordering.Add((col.ColumnName, item.Direction == SortDirection.Descending));
                    }

                    return ordering;
                }
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// Returns <c>true</c> when the source operator's output ordering already
    /// satisfies every item in the <paramref name="orderBy"/> clause, making a
    /// separate <see cref="OrderByOperator"/> unnecessary.
    /// </summary>
    private static bool OutputOrderingSatisfiesOrderBy(
        IQueryOperator source,
        OrderByClause orderBy)
    {
        IReadOnlyList<(string ColumnName, bool Descending)>? ordering = GetOutputOrdering(source);

        if (ordering is null || ordering.Count < orderBy.Items.Count)
        {
            return false;
        }

        for (int index = 0; index < orderBy.Items.Count; index++)
        {
            OrderByItem item = orderBy.Items[index];

            if (item.Expression is not ColumnReference columnReference)
            {
                return false;
            }

            if (!string.Equals(columnReference.ColumnName, ordering[index].ColumnName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            bool descending = item.Direction == SortDirection.Descending;

            if (descending != ordering[index].Descending)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks whether the GROUP BY key expressions match the output ordering of the
    /// source operator, enabling streaming aggregation. All GROUP BY expressions must
    /// be simple <see cref="ColumnReference"/> nodes whose column names match the
    /// ordering columns in the same sequence.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the source is sorted on the GROUP BY keys and streaming
    /// aggregation can replace hash aggregation.
    /// </returns>
    private static bool CanUseStreamingAggregate(
        IQueryOperator source,
        IReadOnlyList<Expression> groupByExpressions)
    {
        if (groupByExpressions.Count == 0)
        {
            // Global aggregation (no GROUP BY) always produces one group — streaming not applicable.
            return false;
        }

        IReadOnlyList<(string ColumnName, bool Descending)>? ordering = GetOutputOrdering(source);

        if (ordering is null || ordering.Count == 0)
        {
            return false;
        }

        // The ordering must cover at least the first GROUP BY key. For full streaming,
        // all GROUP BY keys must match the ordering prefix.
        if (ordering.Count < groupByExpressions.Count)
        {
            // Source produces fewer sorted columns than GROUP BY keys — the remaining
            // keys could interleave within a single sort-key partition. Only safe when
            // all GROUP BY keys are covered.
            return false;
        }

        for (int index = 0; index < groupByExpressions.Count; index++)
        {
            if (groupByExpressions[index] is not ColumnReference columnReference)
            {
                return false;
            }

            if (!string.Equals(columnReference.ColumnName, ordering[index].ColumnName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks whether each GROUP BY key expression is a simple <see cref="ColumnReference"/>
    /// whose unqualified column name matches the corresponding ORDER BY item at the same
    /// position. The ORDER BY clause may contain more items than the GROUP BY list; the
    /// check is a prefix match on the first <c>groupByExpressions.Count</c> ORDER BY items.
    /// </summary>
    /// <remarks>
    /// This predicate gates sort injection: if it returns <c>true</c>, an
    /// <see cref="OrderByOperator"/> keyed on the GROUP BY columns (in ORDER BY
    /// directions) can be placed before the <see cref="GroupByOperator"/>, enabling
    /// streaming aggregation and allowing the downstream ORDER BY to be elided.
    /// </remarks>
    private static bool GroupByKeysMatchOrderByPrefix(
        IReadOnlyList<Expression> groupByExpressions,
        OrderByClause orderBy)
    {
        if (groupByExpressions.Count == 0)
        {
            return false;
        }

        if (orderBy.Items.Count < groupByExpressions.Count)
        {
            return false;
        }

        for (int index = 0; index < groupByExpressions.Count; index++)
        {
            if (groupByExpressions[index] is not ColumnReference groupColumn)
            {
                return false;
            }

            if (orderBy.Items[index].Expression is not ColumnReference orderColumn)
            {
                return false;
            }

            if (!string.Equals(groupColumn.ColumnName, orderColumn.ColumnName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Builds the <see cref="OrderByItem"/> list for the sort injected before a
    /// <see cref="GroupByOperator"/>. One item is emitted per GROUP BY expression,
    /// using the sort direction from the corresponding ORDER BY item at the same index.
    /// </summary>
    private static IReadOnlyList<OrderByItem> BuildGroupBySortItems(
        IReadOnlyList<Expression> groupByExpressions,
        OrderByClause orderBy)
    {
        List<OrderByItem> items = new(groupByExpressions.Count);

        for (int index = 0; index < groupByExpressions.Count; index++)
        {
            SortDirection direction = index < orderBy.Items.Count
                ? orderBy.Items[index].Direction
                : SortDirection.Ascending;

            items.Add(new OrderByItem(groupByExpressions[index], direction));
        }

        return items;
    }

    /// <summary>
    /// Attempts to replace the <see cref="ScanOperator"/> in the operator tree with an
    /// <see cref="IndexScanOperator"/> when the ORDER BY clause is a single column reference
    /// covered by a sorted value index and the provider supports seeking.
    /// Mutates <paramref name="source"/> in place when the substitution succeeds.
    /// </summary>
    /// <returns><c>true</c> if the index scan was substituted and the ORDER BY can be elided.</returns>
    private static bool TryReplaceWithIndexScan(ref IQueryOperator source, OrderByClause orderBy)
    {
        // Only single-column, simple column reference ORDER BY is eligible.
        if (orderBy.Items.Count != 1)
        {
            return false;
        }

        OrderByItem item = orderBy.Items[0];

        if (item.Expression is not ColumnReference columnRef)
        {
            return false;
        }

        string sortColumn = columnRef.ColumnName;

        // Walk the operator tree to find the ScanOperator.
        ScanOperator? scan = FindScanOperator(source);

        if (scan is null)
        {
            return false;
        }

        // The scan must have a source index with a column index for the sort column.
        if (scan.SourceIndex is null)
        {
            return false;
        }

        if (!scan.SourceIndex.TryGetColumnIndex(sortColumn, out IColumnIndex? columnIndex))
        {
            return false;
        }

        bool descending = item.Direction == SortDirection.Descending;

        IndexScanOperator indexScan = new(
            scan.TableProvider,
            scan.RequiredColumns,
            columnIndex,
            scan.SourceIndex.Chunks,
            descending,
            sortColumn);

        // Replace the ScanOperator in the tree with the IndexScanOperator.
        source = ReplaceScanOperator(source, scan, indexScan);
        return true;
    }

    /// <summary>
    /// Returns <c>true</c> when a merge join on the given condition would produce
    /// output whose sort order benefits a downstream operator. Merge join replaces
    /// sequential table scans with index-ordered random-access reads — without a
    /// downstream consumer that exploits the sorted output, hash join's sequential
    /// scan is faster for full table scans.
    /// </summary>
    /// <remarks>
    /// Checks two cases:
    /// <list type="number">
    /// <item>ORDER BY: when the leading ORDER BY column matches the merge join key,
    /// the sorted merge output can eliminate a separate sort pass.</item>
    /// <item>GROUP BY: when the leading GROUP BY column matches the merge join key,
    /// streaming aggregation can emit groups one at a time, enabling LIMIT
    /// short-circuit.</item>
    /// </list>
    /// </remarks>
    /// <summary>
    /// Returns <c>true</c> when a merge join is preferable to a hash join for the
    /// current join node.  Two independent conditions each independently justify merge join:
    /// <list type="bullet">
    /// <item>The leading ORDER BY or GROUP BY column matches the join key so that the merge
    /// join's sorted output eliminates a downstream <see cref="OrderByOperator"/> or enables
    /// streaming aggregation.</item>
    /// <item>Either side of the join has more than
    /// <see cref="IndexConstants.BPlusTreeAutoThreshold"/> estimated rows, meaning a hash
    /// table would need to materialise millions of rows — more expensive than the
    /// index-ordered random-access reads used by <see cref="MergeJoinOperator"/>.</item>
    /// </list>
    /// When neither condition holds the hash join's sequential scan pattern is cheaper.
    /// </summary>
    private static bool ShouldUseMergeJoin(
        SelectStatement statement,
        Expression? onCondition,
        IQueryOperator leftOperator,
        IQueryOperator rightOperator)
    {
        if (onCondition is null)
        {
            return false;
        }

        JoinKeyExtractionResult? extraction = JoinKeyExtractor.TryExtract(onCondition);

        if (extraction is null
            || extraction.KeyPairs.Count != 1
            || extraction.KeyPairs[0].Left is not ColumnReference leftKey
            || extraction.KeyPairs[0].Right is not ColumnReference rightKey)
        {
            return false;
        }

        // Check whether the leading ORDER BY column matches the join key.
        if (statement.OrderBy is not null
            && statement.OrderBy.Items.Count > 0
            && statement.OrderBy.Items[0].Expression is ColumnReference orderColumn)
        {
            string orderColumnName = orderColumn.ColumnName;

            if (string.Equals(orderColumnName, leftKey.ColumnName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(orderColumnName, rightKey.ColumnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Check whether the leading GROUP BY column matches the join key,
        // enabling streaming aggregation with LIMIT short-circuit.
        if (statement.GroupBy is not null
            && statement.GroupBy.Expressions.Count > 0
            && statement.GroupBy.Expressions[0] is ColumnReference groupColumn)
        {
            string groupColumnName = groupColumn.ColumnName;

            if (string.Equals(groupColumnName, leftKey.ColumnName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(groupColumnName, rightKey.ColumnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Large-build override: when either side exceeds the B+Tree auto-threshold the
        // cost of allocating and probing a hash table outweighs the random-access penalty
        // of index-ordered merge join reads.  Merge join is only attempted if TryCreateMergeJoin
        // subsequently confirms that sorted indexes actually exist on both sides.
        long? leftRows = GetEstimatedRowCount(leftOperator);
        long? rightRows = GetEstimatedRowCount(rightOperator);

        if ((leftRows > IndexConstants.BPlusTreeAutoThreshold)
            || (rightRows > IndexConstants.BPlusTreeAutoThreshold))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns <c>true</c> when the given join should be executed as an index
    /// nested-loop join at plan time. Three conditions must all hold:
    /// <list type="number">
    /// <item>The statement has a LIMIT clause (so only a small top-N result is needed).</item>
    /// <item>The join has a single equi-join key expressed as two simple
    /// <see cref="ColumnReference"/> nodes.</item>
    /// <item>The build side (right operator) is a simple scan chain whose
    /// <see cref="ScanOperator.SourceIndex"/> contains a sorted index on the
    /// build key column — confirming that index point-seeks are available.</item>
    /// </list>
    /// Join types are restricted to <see cref="JoinType.Inner"/> and
    /// <see cref="JoinType.LeftSemi"/>; all others fall back to hash join.
    /// </summary>
    /// <remarks>
    /// The seekable-provider check is
    /// intentionally deferred to runtime inside
    /// <see cref="JoinOperator.TryCreateIndexNestedLoopExecutor"/>.
    /// Using <see cref="ScanOperator.SourceIndex"/> presence as the plan-time
    /// proxy is safe because <c>.datum</c> files with an index always expose a
    /// seekable provider.
    /// </remarks>
    private static bool ShouldPreferIndexNestedLoop(
        SelectStatement statement,
        IQueryOperator buildSide,
        JoinClause join)
    {
        // NLJ only pays off when LIMIT restricts the result to a small top-N.
        if (statement.Limit is null)
        {
            return false;
        }

        // Blocking operators (GROUP BY, DISTINCT, HAVING) must consume all
        // join output before LIMIT can take effect, so the LIMIT cannot
        // short-circuit the join. In that situation index NLJ degrades to
        // per-row seeks across the entire probe side — catastrophically
        // worse than a hash join.
        if (statement.GroupBy is not null || statement.Distinct || statement.Having is not null)
        {
            return false;
        }

        // Only INNER and LeftSemi joins — must match what IndexNestedLoopJoinExecutor supports.
        if (join.Type is not (JoinType.Inner or JoinType.LeftSemi))
        {
            return false;
        }

        // Single equi-join key with simple column references on both sides.
        JoinKeyExtractionResult? extraction = JoinKeyExtractor.TryExtract(join.OnCondition);

        if (extraction is null
            || extraction.KeyPairs.Count != 1
            || extraction.KeyPairs[0].Right is not ColumnReference buildKeyRef)
        {
            return false;
        }

        // Build side must be a simple scan chain with a source index.
        ScanOperator? buildScan = FindScanOperatorInChain(buildSide);

        if (buildScan?.SourceIndex is null)
        {
            return false;
        }

        // The build key column must exist in the index.
        string buildKeyColumn = buildKeyRef.QualifiedName ?? buildKeyRef.ColumnName;

        return buildScan.SourceIndex.TryGetColumnIndex(buildKeyColumn, out _)
            || buildScan.SourceIndex.TryGetColumnIndex(buildKeyRef.ColumnName, out _);
    }

    /// <summary>
    /// Attempts to create a <see cref="MergeJoinOperator"/> for the given join when both
    /// sides have sorted value indexes on their respective equi-join key columns.
    /// Only single-key equi-joins with simple <see cref="ColumnReference"/> keys are eligible.
    /// Both sides must be simple scan chains (Scan → Alias → Filter) — if the left side is
    /// already a join tree, the hash join output is unordered so merge join cannot apply.
    /// <para>
    /// When eligible, replaces both <see cref="ScanOperator"/> nodes with
    /// <see cref="IndexScanOperator"/> nodes (ascending) and returns a <see cref="MergeJoinOperator"/>.
    /// </para>
    /// </summary>
    private static bool TryCreateMergeJoin(
        IQueryOperator left,
        IQueryOperator right,
        JoinType joinType,
        Expression? onCondition,
        [NotNullWhen(true)] out MergeJoinOperator? mergeJoin)
    {
        mergeJoin = null;

        // Only INNER, LEFT, RIGHT, and FULL OUTER joins benefit from merge.
        // CROSS, SEMI, and ANTI-SEMI joins have different semantics or no equi-key.
        if (joinType is not (JoinType.Inner or JoinType.Left or JoinType.Right or JoinType.FullOuter))
        {
            return false;
        }

        JoinKeyExtractionResult? extraction = JoinKeyExtractor.TryExtract(onCondition);

        if (extraction is null)
        {
            return false;
        }

        // Only single-key merge join for now.
        if (extraction.KeyPairs.Count != 1)
        {
            return false;
        }

        // Both keys must be simple column references to match against sorted index names.
        if (extraction.KeyPairs[0].Left is not ColumnReference leftColumnRef
            || extraction.KeyPairs[0].Right is not ColumnReference rightColumnRef)
        {
            return false;
        }

        // Both sides must be simple scan chains — if the left side contains a join,
        // hash join output is unordered and merge join cannot apply.
        ScanOperator? leftScan = FindScanOperatorInChain(left);
        ScanOperator? rightScan = FindScanOperatorInChain(right);

        if (leftScan is null || rightScan is null)
        {
            return false;
        }

        // Both scans must have source indexes with sorted value indexes on the join column.
        if (leftScan.SourceIndex is null || rightScan.SourceIndex is null)
        {
            return false;
        }

        string leftColumnName = leftColumnRef.ColumnName;
        string rightColumnName = rightColumnRef.ColumnName;

        // Both sides must have a physically-sorted column index on the join key.
        // B+Tree indexes are excluded: they enumerate entries in key order but the
        // underlying rows are scattered in the datum file, making a full-table
        // merge-join scan prohibitively expensive.
        if (!leftScan.SourceIndex.TryGetSortedColumnIndex(leftColumnName, out IColumnIndex? leftColumnIndex)
            || !rightScan.SourceIndex.TryGetSortedColumnIndex(rightColumnName, out IColumnIndex? rightColumnIndex))
        {
            return false;
        }

        // Replace both ScanOperators with ascending IndexScanOperators.
        IndexScanOperator leftIndexScan = new(
            leftScan.TableProvider,
            leftScan.RequiredColumns,
            leftColumnIndex,
            leftScan.SourceIndex.Chunks,
            descending: false,
            leftColumnName);

        IndexScanOperator rightIndexScan = new(
            rightScan.TableProvider,
            rightScan.RequiredColumns,
            rightColumnIndex,
            rightScan.SourceIndex.Chunks,
            descending: false,
            rightColumnName);

        IQueryOperator leftReplaced = ReplaceScanOperator(left, leftScan, leftIndexScan);
        IQueryOperator rightReplaced = ReplaceScanOperator(right, rightScan, rightIndexScan);

        mergeJoin = new MergeJoinOperator(
            leftReplaced, rightReplaced, joinType, extraction,
            leftColumnName, rightColumnName);

        return true;
    }

    /// <summary>
    /// Finds the <see cref="ScanOperator"/> at the outermost probe position in the operator
    /// tree. Walks through <see cref="AliasOperator"/>, <see cref="FilterOperator"/>,
    /// <see cref="ProjectOperator"/>, <see cref="DistinctOperator"/>, and
    /// the <em>left (probe) side</em> of <see cref="JoinOperator"/> nodes, following the
    /// probe chain down to the leaf scan.
    /// Returns <c>null</c> if no scan is reachable via this path.
    /// </summary>
    private static ScanOperator? FindScanOperator(IQueryOperator operatorNode)
    {
        IQueryOperator current = operatorNode;

        while (true)
        {
            switch (current)
            {
                case ScanOperator scan:
                    return scan;
                case AliasOperator alias:
                    current = alias.Source;
                    break;
                case FilterOperator filter:
                    current = filter.Source;
                    break;
                case ProjectOperator project:
                    current = project.Source;
                    break;
                case DistinctOperator distinct:
                    current = distinct.Source;
                    break;
                case JoinOperator join:
                    // Follow the probe (left) side of the join to find the outermost
                    // driving scan. This allows sort elimination when the ORDER BY table
                    // has been placed as the outermost probe by join reordering.
                    current = join.Left;
                    break;
                case MergeJoinOperator merge:
                    // Follow the left side of the merge join — the left input preserves
                    // its sorted order, so the outermost scan is reachable.
                    current = merge.Left;
                    break;
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// Replaces the target <see cref="ScanOperator"/> in the operator tree with the given
    /// <see cref="IndexScanOperator"/>, preserving any wrapping operators (alias, filter,
    /// project, distinct, join). For <see cref="JoinOperator"/> nodes the left (probe) chain
    /// is searched recursively; the right (build) side is never modified.
    /// </summary>
    private static IQueryOperator ReplaceScanOperator(
        IQueryOperator root, ScanOperator target, IndexScanOperator replacement)
    {
        if (ReferenceEquals(root, target))
        {
            return replacement;
        }

        // Rebuild the wrapper chain with the replacement at the leaf.
        return root switch
        {
            AliasOperator alias => new AliasOperator(
                ReplaceScanOperator(alias.Source, target, replacement), alias.Alias),
            FilterOperator filter => new FilterOperator(
                ReplaceScanOperator(filter.Source, target, replacement), filter.Predicate),
            ProjectOperator project => new ProjectOperator(
                ReplaceScanOperator(project.Source, target, replacement),
                project.Columns, project.LetBindings),
            DistinctOperator distinct => new DistinctOperator(
                ReplaceScanOperator(distinct.Source, target, replacement)),
            // Rebuild join with the replaced probe side; the build side is untouched.
            JoinOperator join => new JoinOperator(
                ReplaceScanOperator(join.Left, target, replacement),
                join.Right,
                join.Type,
                join.OnCondition,
                join.NullSensitiveAntiSemi,
                join.Flipped),
            // Rebuild merge join with the replaced side(s).
            MergeJoinOperator merge => new MergeJoinOperator(
                ReplaceScanOperator(merge.Left, target, replacement),
                ReplaceScanOperator(merge.Right, target, replacement),
                merge.Type,
                merge.Extraction,
                merge.LeftSortColumn,
                merge.RightSortColumn),
            _ => root,
        };
    }

    /// <summary>
    /// Returns the qualified table alias from a single-column ORDER BY if the referenced
    /// table has a sorted column index on that sort column. The result is passed to
    /// <see cref="TryReorderJoins"/> so the relevant table is protected as the outermost
    /// probe, enabling sort elimination via <see cref="TryReplaceWithIndexScan"/>.
    /// Returns <c>null</c> when the ORDER BY is multi-column, unqualified, or the table
    /// lacks the required sorted column index.
    /// </summary>
    private static string? GetOrderBySortTableAlias(
        OrderByClause? orderBy,
        IQueryOperator fromOperator,
        HashSet<string> fromAliases,
        List<(JoinClause Join, IQueryOperator Operator, HashSet<string> Aliases)> plannedJoins)
    {
        if (orderBy is null || orderBy.Items.Count != 1)
        {
            return null;
        }

        if (orderBy.Items[0].Expression is not ColumnReference { TableName: string tableName, ColumnName: string columnName })
        {
            return null;
        }

        // Locate the operator for the ORDER BY table alias.
        IQueryOperator? tableOperator = null;

        if (fromAliases.Contains(tableName))
        {
            tableOperator = fromOperator;
        }
        else
        {
            foreach ((_, IQueryOperator op, HashSet<string> aliases) in plannedJoins)
            {
                if (aliases.Contains(tableName))
                {
                    tableOperator = op;
                    break;
                }
            }
        }

        if (tableOperator is null)
        {
            return null;
        }

        // Verify the table has a sorted column index on the ORDER BY column.
        // Use the simple chain-only scan finder (no join traversal) since each
        // individual table operator is a scan chain, not a join tree.
        ScanOperator? scan = FindScanOperatorInChain(tableOperator);

        if (scan?.SourceIndex is null)
        {
            return null;
        }

        return scan.SourceIndex.TryGetColumnIndex(columnName, out _) ? tableName : null;
    }

    /// <summary>
    /// Finds the <see cref="ScanOperator"/> in a simple operator chain without crossing
    /// into join subtrees. This variant is used when inspecting a single planned table
    /// operator (which is always a chain, never a join).
    /// </summary>
    private static ScanOperator? FindScanOperatorInChain(IQueryOperator operatorNode)
    {
        IQueryOperator current = operatorNode;

        while (true)
        {
            switch (current)
            {
                case ScanOperator scan:
                    return scan;
                case AliasOperator alias:
                    current = alias.Source;
                    break;
                case FilterOperator filter:
                    current = filter.Source;
                    break;
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// Collects all column references from every clause of the statement
    /// for projection pushdown.
    /// </summary>
    private static HashSet<(string? TableName, string ColumnName)> CollectAllReferencedColumns(
        SelectStatement statement)
    {
        HashSet<(string? TableName, string ColumnName)> references = new();

        // If SELECT * or SELECT table.*, we need all columns — return empty
        // to signal "no restriction" downstream.
        foreach (SelectColumn column in statement.Columns)
        {
            if (column is SelectAllColumns or SelectTableColumns)
            {
                return references; // Empty set means "all columns needed".
            }
        }

        // SELECT columns.
        foreach (SelectColumn column in statement.Columns)
        {
            foreach ((string? tableName, string columnName) in
                ColumnReferenceCollector.Collect(column.Expression))
            {
                references.Add((tableName, columnName));
            }
        }

        // LET binding expressions.
        if (statement.LetBindings is not null)
        {
            foreach (LetBinding binding in statement.LetBindings)
            {
                foreach ((string? tableName, string columnName) in
                    ColumnReferenceCollector.Collect(binding.Expression))
                {
                    references.Add((tableName, columnName));
                }
            }
        }

        // WHERE predicate.
        if (statement.Where is not null)
        {
            foreach ((string? tableName, string columnName) in
                ColumnReferenceCollector.Collect(statement.Where))
            {
                references.Add((tableName, columnName));
            }
        }

        // JOIN ON conditions.
        if (statement.Joins is not null)
        {
            foreach (JoinClause join in statement.Joins)
            {
                if (join.OnCondition is not null)
                {
                    foreach ((string? tableName, string columnName) in
                        ColumnReferenceCollector.Collect(join.OnCondition))
                    {
                        references.Add((tableName, columnName));
                    }
                }
            }
        }

        // ORDER BY expressions.
        if (statement.OrderBy is not null)
        {
            foreach (OrderByItem item in statement.OrderBy.Items)
            {
                foreach ((string? tableName, string columnName) in
                    ColumnReferenceCollector.Collect(item.Expression))
                {
                    references.Add((tableName, columnName));
                }
            }
        }

        // GROUP BY expressions.
        if (statement.GroupBy is not null)
        {
            foreach (Expression groupExpression in statement.GroupBy.Expressions)
            {
                foreach ((string? tableName, string columnName) in
                    ColumnReferenceCollector.Collect(groupExpression))
                {
                    references.Add((tableName, columnName));
                }
            }
        }

        // HAVING predicate.
        if (statement.Having is not null)
        {
            foreach ((string? tableName, string columnName) in
                ColumnReferenceCollector.Collect(statement.Having))
            {
                references.Add((tableName, columnName));
            }
        }

        // CROSS VALIDATE key columns (ON, STRATIFY BY, GROUP BY).
        if (statement.CrossValidate is not null)
        {
            foreach (Expression keyExpr in statement.CrossValidate.KeyColumns)
            {
                foreach ((string? tableName, string columnName) in
                    ColumnReferenceCollector.Collect(keyExpr))
                {
                    references.Add((tableName, columnName));
                }
            }

            if (statement.CrossValidate.StratifyColumns is not null)
            {
                foreach (Expression stratifyExpr in statement.CrossValidate.StratifyColumns)
                {
                    foreach ((string? tableName, string columnName) in
                        ColumnReferenceCollector.Collect(stratifyExpr))
                    {
                        references.Add((tableName, columnName));
                    }
                }
            }

            if (statement.CrossValidate.GroupColumns is not null)
            {
                foreach (Expression groupExpr in statement.CrossValidate.GroupColumns)
                {
                    foreach ((string? tableName, string columnName) in
                        ColumnReferenceCollector.Collect(groupExpr))
                    {
                        references.Add((tableName, columnName));
                    }
                }
            }
        }

        return references;
    }

    /// <summary>
    /// Computes the set of required column names for a specific table alias
    /// from the globally referenced columns. Returns null when all columns
    /// are needed (SELECT * or no column analysis available).
    /// </summary>
    private static IReadOnlySet<string>? ComputeRequiredColumns(
        string? alias,
        HashSet<(string? TableName, string ColumnName)> allReferencedColumns)
    {
        // Empty set means SELECT * — all columns needed.
        if (allReferencedColumns.Count == 0)
        {
            return null;
        }

        HashSet<string> required = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string? tableName, string columnName) in allReferencedColumns)
        {
            if (tableName is null)
            {
                // Unqualified reference — could be from any table; include it.
                required.Add(columnName);
            }
            else if (alias is not null
                && string.Equals(tableName, alias, StringComparison.OrdinalIgnoreCase))
            {
                required.Add(columnName);
            }
        }

        // If no columns matched this alias, it's possible the query references
        // columns without qualification. Return null (all columns) to be safe.
        return required.Count > 0 ? required : null;
    }

    /// <summary>
    /// Collects the table aliases introduced by a table source into the given set.
    /// </summary>
    private static void CollectSourceAliases(TableSource source, HashSet<string> aliases)
    {
        switch (source)
        {
            case TableReference tableRef:
                aliases.Add(tableRef.Alias ?? tableRef.Name);
                break;
            case SubquerySource subquery:
                aliases.Add(subquery.Alias);
                break;
            case FunctionSource functionSource:
                if (functionSource.Alias is not null)
                {
                    aliases.Add(functionSource.Alias);
                }
                break;
        }
    }

    /// <summary>
    /// Returns the alias (or fallback name) introduced by a table source,
    /// used when expanding <c>SELECT *</c> into per-table wildcards.
    /// </summary>
    private static string? GetSourceAlias(TableSource source)
    {
        return source switch
        {
            TableReference tableRef => tableRef.Alias ?? tableRef.Name,
            SubquerySource subquery => subquery.Alias,
            FunctionSource functionSource => functionSource.Alias,
            _ => null,
        };
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
    /// Returns <c>true</c> if any SELECT column contains an aggregate function call.
    /// </summary>
    private static bool HasAggregateFunction(
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
    /// </summary>
    private static bool ExpressionContainsAggregate(Expression expression, FunctionRegistry functionRegistry)
    {
        return expression switch
        {
            FunctionCallExpression func => functionRegistry.TryGetAggregate(func.FunctionName) is not null
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
    /// Rewrites an expression by replacing aggregate <see cref="FunctionCallExpression"/>
    /// nodes with <see cref="ColumnReference"/> nodes that reference the output columns
    /// of the <see cref="GroupByOperator"/>. Each unique aggregate is added to
    /// <paramref name="aggregateColumns"/> only once.
    /// </summary>
    private static Expression RewriteAggregateExpression(
        Expression expression,
        FunctionRegistry functionRegistry,
        List<AggregateColumn> aggregateColumns)
    {
        if (expression is FunctionCallExpression func)
        {
            IAggregateFunction? aggregateFunction =
                functionRegistry.TryGetAggregate(func.FunctionName);

            if (aggregateFunction is not null)
            {
                bool isCountStar = IsCountStarCall(func);

                if (func.Distinct && isCountStar)
                {
                    throw new InvalidOperationException(
                        "COUNT(DISTINCT *) is not supported. Use COUNT(DISTINCT column) instead.");
                }
                string outputName = QueryExplainer.FormatExpression(func);

                // Deduplicate: reuse existing AggregateColumn if the same aggregate
                // expression already appears (e.g. SELECT COUNT(*), COUNT(*) FROM t).
                bool alreadyRegistered = false;
                foreach (AggregateColumn existing in aggregateColumns)
                {
                    if (string.Equals(existing.OutputName, outputName, StringComparison.OrdinalIgnoreCase))
                    {
                        alreadyRegistered = true;
                        break;
                    }
                }

                if (!alreadyRegistered)
                {
                    IReadOnlyList<Expression> arguments = isCountStar
                        ? Array.Empty<Expression>()
                        : func.Arguments;

                    aggregateColumns.Add(new AggregateColumn(
                        aggregateFunction, arguments, outputName, isCountStar, func.Distinct, func.OrderBy));
                }

                return new ColumnReference(null, outputName);
            }
        }

        // Recurse into sub-expressions.
        return expression switch
        {
            FunctionCallExpression scalarFunc => RewriteScalarFunctionArguments(
                scalarFunc, functionRegistry, aggregateColumns),
            BinaryExpression bin => new BinaryExpression(
                RewriteAggregateExpression(bin.Left, functionRegistry, aggregateColumns),
                bin.Operator,
                RewriteAggregateExpression(bin.Right, functionRegistry, aggregateColumns)),
            UnaryExpression unary => new UnaryExpression(
                unary.Operator,
                RewriteAggregateExpression(unary.Operand, functionRegistry, aggregateColumns)),
            CastExpression cast => new CastExpression(
                RewriteAggregateExpression(cast.Expression, functionRegistry, aggregateColumns),
                cast.TargetType),
            CaseExpression caseExpr => RewriteCaseAggregateExpression(caseExpr, functionRegistry, aggregateColumns),
            _ => expression,
        };
    }

    /// <summary>
    /// Rewrites the arguments of a non-aggregate <see cref="FunctionCallExpression"/>
    /// so that nested aggregate calls (e.g. <c>DATE_DIFF('day', MIN(x), MAX(x))</c>)
    /// are replaced with column references to the <see cref="GroupByOperator"/> output.
    /// </summary>
    private static Expression RewriteScalarFunctionArguments(
        FunctionCallExpression func,
        FunctionRegistry functionRegistry,
        List<AggregateColumn> aggregateColumns)
    {
        bool changed = false;
        Expression[] rewrittenArgs = new Expression[func.Arguments.Count];
        for (int i = 0; i < func.Arguments.Count; i++)
        {
            rewrittenArgs[i] = RewriteAggregateExpression(
                func.Arguments[i], functionRegistry, aggregateColumns);
            if (!ReferenceEquals(rewrittenArgs[i], func.Arguments[i]))
            {
                changed = true;
            }
        }

        return changed
            ? new FunctionCallExpression(func.FunctionName, rewrittenArgs, func.OrderBy, func.Distinct)
            : func;
    }

    /// <summary>
    /// Checks whether a CASE expression contains any aggregate function calls
    /// in its operand, WHEN conditions, THEN results, or ELSE result.
    /// </summary>
    private static bool CaseExpressionContainsAggregate(CaseExpression caseExpression, FunctionRegistry functionRegistry)
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

    /// <summary>
    /// Rewrites aggregate references inside a CASE expression by descending
    /// into operand, WHEN conditions, THEN results, and the ELSE branch.
    /// </summary>
    private static CaseExpression RewriteCaseAggregateExpression(
        CaseExpression caseExpression,
        FunctionRegistry functionRegistry,
        List<AggregateColumn> aggregateColumns)
    {
        Expression? rewrittenOperand = caseExpression.Operand is not null
            ? RewriteAggregateExpression(caseExpression.Operand, functionRegistry, aggregateColumns)
            : null;

        List<WhenClause> rewrittenClauses = new(caseExpression.WhenClauses.Count);
        foreach (WhenClause whenClause in caseExpression.WhenClauses)
        {
            rewrittenClauses.Add(new WhenClause(
                RewriteAggregateExpression(whenClause.Condition, functionRegistry, aggregateColumns),
                RewriteAggregateExpression(whenClause.Result, functionRegistry, aggregateColumns)));
        }

        Expression? rewrittenElse = caseExpression.ElseResult is not null
            ? RewriteAggregateExpression(caseExpression.ElseResult, functionRegistry, aggregateColumns)
            : null;

        return new CaseExpression(rewrittenOperand, rewrittenClauses, rewrittenElse, caseExpression.Span);
    }

    // ────────────────────────────────── Window Functions ──────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> if any SELECT column contains a window function call
    /// (a <see cref="WindowFunctionCallExpression"/> node).
    /// </summary>
    private static bool HasWindowFunction(
        IReadOnlyList<SelectColumn> columns,
        FunctionRegistry functionRegistry)
    {
        foreach (SelectColumn column in columns)
        {
            if (column is SelectAllColumns or SelectTableColumns)
            {
                continue;
            }

            if (ExpressionContainsWindowFunction(column.Expression))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Recursively checks whether an expression tree contains a <see cref="WindowFunctionCallExpression"/>.
    /// </summary>
    private static bool ExpressionContainsWindowFunction(Expression expression)
    {
        return expression switch
        {
            WindowFunctionCallExpression => true,
            BinaryExpression bin => ExpressionContainsWindowFunction(bin.Left)
                || ExpressionContainsWindowFunction(bin.Right),
            UnaryExpression unary => ExpressionContainsWindowFunction(unary.Operand),
            CastExpression cast => ExpressionContainsWindowFunction(cast.Expression),
            CaseExpression caseExpr => CaseExpressionContainsWindowFunction(caseExpr),
            FunctionCallExpression func => func.Arguments.Any(ExpressionContainsWindowFunction),
            _ => false,
        };
    }

    /// <summary>
    /// Checks whether a CASE expression contains any window function calls.
    /// </summary>
    private static bool CaseExpressionContainsWindowFunction(CaseExpression caseExpression)
    {
        if (caseExpression.Operand is not null && ExpressionContainsWindowFunction(caseExpression.Operand))
        {
            return true;
        }

        foreach (WhenClause whenClause in caseExpression.WhenClauses)
        {
            if (ExpressionContainsWindowFunction(whenClause.Condition)
                || ExpressionContainsWindowFunction(whenClause.Result))
            {
                return true;
            }
        }

        return caseExpression.ElseResult is not null
            && ExpressionContainsWindowFunction(caseExpression.ElseResult);
    }

    /// <summary>
    /// Resolves column references in <paramref name="expression"/> that match
    /// SELECT-list aliases by substituting them with the underlying (rewritten)
    /// expression. This allows QUALIFY to reference aliases like <c>rn</c> even
    /// though projection has not yet been applied at that pipeline stage.
    /// </summary>
    private static Expression ResolveSelectAliases(
        Expression expression, IReadOnlyList<SelectColumn> projectionColumns)
    {
        if (expression is ColumnReference column && column.TableName is null)
        {
            foreach (SelectColumn selectColumn in projectionColumns)
            {
                if (selectColumn.Alias is not null &&
                    string.Equals(selectColumn.Alias, column.ColumnName, StringComparison.OrdinalIgnoreCase))
                {
                    return selectColumn.Expression;
                }
            }
        }

        if (expression is BinaryExpression binary)
        {
            Expression left = ResolveSelectAliases(binary.Left, projectionColumns);
            Expression right = ResolveSelectAliases(binary.Right, projectionColumns);
            if (ReferenceEquals(left, binary.Left) && ReferenceEquals(right, binary.Right))
            {
                return expression;
            }

            return new BinaryExpression(left, binary.Operator, right);
        }

        if (expression is UnaryExpression unary)
        {
            Expression operand = ResolveSelectAliases(unary.Operand, projectionColumns);
            return ReferenceEquals(operand, unary.Operand) ? expression : new UnaryExpression(unary.Operator, operand);
        }

        return expression;
    }

    /// <summary>
    /// Resolves column references in <paramref name="expression"/> that match
    /// LET binding names by substituting them with the binding's expression.
    /// This allows QUALIFY to reference LET-bound names as expression substitution
    /// (not memoized, since QUALIFY runs before projection).
    /// </summary>
    private static Expression ResolveLetBindingReferences(
        Expression expression, IReadOnlyList<LetBinding>? letBindings)
    {
        if (letBindings is null || letBindings.Count == 0)
        {
            return expression;
        }

        if (expression is ColumnReference column && column.TableName is null)
        {
            foreach (LetBinding binding in letBindings)
            {
                if (string.Equals(binding.Name, column.ColumnName, StringComparison.OrdinalIgnoreCase))
                {
                    return binding.Expression;
                }
            }
        }

        if (expression is BinaryExpression binary)
        {
            Expression left = ResolveLetBindingReferences(binary.Left, letBindings);
            Expression right = ResolveLetBindingReferences(binary.Right, letBindings);
            if (ReferenceEquals(left, binary.Left) && ReferenceEquals(right, binary.Right))
            {
                return expression;
            }

            return new BinaryExpression(left, binary.Operator, right);
        }

        if (expression is UnaryExpression unary)
        {
            Expression operand = ResolveLetBindingReferences(unary.Operand, letBindings);
            return ReferenceEquals(operand, unary.Operand) ? expression : new UnaryExpression(unary.Operator, operand);
        }

        return expression;
    }

    /// <summary>
    /// Returns <see langword="true"/> if any LET binding expression contains an
    /// aggregate function call, requiring the GROUP BY rewriting path.
    /// </summary>
    /// <summary>
    /// Checks if any expression in the list is a column reference matching the given name.
    /// </summary>
    private static bool HasColumnReference(IReadOnlyList<Expression> expressions, string columnName)
    {
        foreach (Expression expr in expressions)
        {
            if (expr is ColumnReference { TableName: null } col
                && col.ColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Rewrites SELECT column expressions that reference the CROSS VALIDATE fold alias
    /// with the GroupByOperator's formatted output column name. This ensures the projection
    /// resolves against the grouped row rather than through the LET binding.
    /// </summary>
    private static IReadOnlyList<SelectColumn> RewriteFoldAliasInColumns(
        IReadOnlyList<SelectColumn> columns, string foldAlias, string groupByColumnName)
    {
        List<SelectColumn> result = new(columns.Count);
        foreach (SelectColumn column in columns)
        {
            if (column.Expression is ColumnReference { TableName: null } col
                && col.ColumnName.Equals(foldAlias, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new SelectColumn(
                    new ColumnReference(null, groupByColumnName), column.Alias ?? foldAlias));
            }
            else
            {
                result.Add(column);
            }
        }

        return result;
    }

    /// <summary>
    /// Rewrites GROUP BY expressions that reference the CROSS VALIDATE fold alias
    /// with the fold expression. This is necessary because GROUP BY runs before LET
    /// evaluation, so the fold alias doesn't exist as a row column yet.
    /// </summary>
    private static GroupByClause RewriteFoldAliasInGroupBy(
        GroupByClause groupBy, string foldAlias, Expression foldExpression)
    {
        bool anyRewritten = false;
        List<Expression> rewritten = new(groupBy.Expressions.Count);

        foreach (Expression expr in groupBy.Expressions)
        {
            if (expr is ColumnReference { TableName: null } col
                && col.ColumnName.Equals(foldAlias, StringComparison.OrdinalIgnoreCase))
            {
                rewritten.Add(foldExpression);
                anyRewritten = true;
            }
            else
            {
                rewritten.Add(expr);
            }
        }

        return anyRewritten ? new GroupByClause(rewritten, groupBy.IsAll) : groupBy;
    }

    /// <summary>
    /// Desugars a <see cref="CrossValidateClause"/> into a synthetic <see cref="LetBinding"/>
    /// that computes the fold index: <c>CAST(FLOOR(hash_split(key, seed) * k) AS Int32)</c>.
    /// For composite keys, the key is <c>concat_ws('|', CAST(k1 AS String), ...)</c>.
    /// For GROUP BY keys, the group key replaces the ON key.
    /// </summary>
    private static LetBinding DesugarCrossValidate(CrossValidateClause cv)
    {
        double k = EvaluateConstantDouble(cv.FoldCount);
        if (k < 2 || k != Math.Floor(k))
        {
            throw new InvalidOperationException(
                $"CROSS VALIDATE k must be an integer >= 2, got {k}.");
        }

        double seed = cv.Seed is not null ? EvaluateConstantDouble(cv.Seed) : 0;

        // Determine the hash key expression — GROUP BY key overrides ON key.
        IReadOnlyList<Expression> keyColumns = cv.GroupColumns ?? cv.KeyColumns;

        // Build the hash key expression: single column or composite via concat_ws.
        Expression hashKeyExpr;
        if (keyColumns.Count == 1)
        {
            hashKeyExpr = keyColumns[0];
        }
        else
        {
            // concat_ws('|', CAST(k1 AS String), CAST(k2 AS String), ...)
            List<Expression> concatArgs = [new LiteralExpression("|")];
            foreach (Expression col in keyColumns)
            {
                concatArgs.Add(new CastExpression(col, "String"));
            }

            hashKeyExpr = new FunctionCallExpression("concat_ws", concatArgs);
        }

        // hash_split(key, seed)
        Expression hashSplitCall = new FunctionCallExpression("hash_split",
            [hashKeyExpr, new LiteralExpression(seed)]);

        // hash_split(...) * k
        Expression multiply = new BinaryExpression(
            hashSplitCall, BinaryOperator.Multiply, new LiteralExpression(k));

        // FLOOR(hash_split(...) * k)
        Expression floor = new FunctionCallExpression("floor", [multiply]);

        // CAST(FLOOR(...) AS Int32)
        Expression cast = new CastExpression(floor, "Int32");

        return new LetBinding(cv.OutputAlias, cast, OutputAlias: cv.OutputAlias);
    }

    /// <summary>
    /// Expands destructured LET bindings (<c>LET (a, b) = expr</c>, <c>LET {x, y} = expr</c>) into plain
    /// nodes before any rewriting passes run. Each destructured binding becomes one hidden memoizing
    /// binding (named <c>__destructure_N</c>) plus one plain binding per extracted name.
    /// Plain bindings are passed through unchanged.
    /// </summary>
    private static IReadOnlyList<LetBinding>? DesugarDestructuredLetBindings(
        IReadOnlyList<LetBinding>? letBindings)
    {
        if (letBindings is null)
        {
            return null;
        }

        // Fast path: skip allocation when no destructuring is present.
        bool hasDestructure = false;
        foreach (LetBinding binding in letBindings)
        {
            if (binding.Destructure is not null)
            {
                hasDestructure = true;
                break;
            }
        }

        if (!hasDestructure)
        {
            return letBindings;
        }

        List<LetBinding> expanded = new(letBindings.Count + 4);
        int counter = 0;

        foreach (LetBinding binding in letBindings)
        {
            if (binding.Destructure is null)
            {
                expanded.Add(binding);
                continue;
            }

            DestructurePattern pattern = binding.Destructure;
            string hiddenName = $"__destructure_{counter++}";

            // Hidden binding memoizes the RHS expression once per row.
            expanded.Add(new LetBinding(hiddenName, binding.Expression, OutputAlias: null, Span: binding.Span));

            Expression hiddenRef = new ColumnReference(null, hiddenName);

            if (pattern.Mode == DestructureMode.Positional)
            {
                for (int i = 0; i < pattern.Names.Count; i++)
                {
                    Expression index = new LiteralExpression((float)i);
                    expanded.Add(new LetBinding(
                        pattern.Names[i],
                        new IndexAccessExpression(hiddenRef, index),
                        OutputAlias: null,
                        Span: binding.Span));
                }
            }
            else
            {
                // Named: extract each field by its string key.
                foreach (string fieldName in pattern.Names)
                {
                    Expression index = new LiteralExpression(fieldName);
                    expanded.Add(new LetBinding(
                        fieldName,
                        new IndexAccessExpression(hiddenRef, index),
                        OutputAlias: null,
                        Span: binding.Span));
                }
            }
        }

        return expanded;
    }

    private static bool HasLetAggregateFunction(
        IReadOnlyList<LetBinding>? letBindings, FunctionRegistry functionRegistry)
    {
        if (letBindings is null)
        {
            return false;
        }

        foreach (LetBinding binding in letBindings)
        {
            if (ExpressionContainsAggregate(binding.Expression, functionRegistry))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> if any LET binding expression contains a
    /// window function call, requiring the window function rewriting path.
    /// </summary>
    private static bool HasLetWindowFunction(IReadOnlyList<LetBinding>? letBindings)
    {
        if (letBindings is null)
        {
            return false;
        }

        foreach (LetBinding binding in letBindings)
        {
            if (ExpressionContainsWindowFunction(binding.Expression))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> if any <see cref="AssertClause"/> predicate or
    /// message expression contains a window function call, requiring the window function
    /// rewriting path to be active so assertion columns are available.
    /// </summary>
    private static bool HasAssertWindowFunction(IReadOnlyList<AssertClause>? assertions)
    {
        if (assertions is null)
        {
            return false;
        }

        foreach (AssertClause assertClause in assertions)
        {
            if (ExpressionContainsWindowFunction(assertClause.Predicate))
            {
                return true;
            }

            if (assertClause.Message is not null
                && ExpressionContainsWindowFunction(assertClause.Message))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Rewrites an expression by replacing <see cref="WindowFunctionCallExpression"/>
    /// nodes with <see cref="ColumnReference"/> nodes that reference the output columns
    /// of the <see cref="WindowOperator"/>. Each unique window function call is added
    /// to <paramref name="windowColumns"/> only once.
    /// </summary>
    private static Expression RewriteWindowExpression(
        Expression expression,
        FunctionRegistry functionRegistry,
        List<WindowColumn> windowColumns)
    {
        if (expression is WindowFunctionCallExpression windowCall)
        {
            if (windowCall.Distinct)
            {
                throw new InvalidOperationException(
                    $"DISTINCT is not supported in window functions: " +
                    $"'{QueryExplainer.FormatExpression(windowCall)}'.");
            }

            string outputName = QueryExplainer.FormatExpression(windowCall);

            // Deduplicate: reuse existing WindowColumn if the same expression already appears.
            bool alreadyRegistered = false;
            foreach (WindowColumn existing in windowColumns)
            {
                if (string.Equals(existing.OutputName, outputName, StringComparison.OrdinalIgnoreCase))
                {
                    alreadyRegistered = true;
                    break;
                }
            }

            if (!alreadyRegistered)
            {
                IWindowFunction? windowFunction =
                    functionRegistry.TryGetWindowOrAggregate(windowCall.FunctionName);

                if (windowFunction is null)
                {
                    throw new InvalidOperationException(
                        $"Unknown window function: '{windowCall.FunctionName}'.");
                }

                windowColumns.Add(new WindowColumn(
                    windowFunction,
                    windowCall.Arguments,
                    windowCall.Window,
                    outputName,
                    windowCall.NullHandling,
                    windowCall.FromLast));
            }

            return new ColumnReference(null, outputName);
        }

        // Recurse into sub-expressions.
        return expression switch
        {
            BinaryExpression bin => new BinaryExpression(
                RewriteWindowExpression(bin.Left, functionRegistry, windowColumns),
                bin.Operator,
                RewriteWindowExpression(bin.Right, functionRegistry, windowColumns)),
            UnaryExpression unary => new UnaryExpression(
                unary.Operator,
                RewriteWindowExpression(unary.Operand, functionRegistry, windowColumns)),
            CastExpression cast => new CastExpression(
                RewriteWindowExpression(cast.Expression, functionRegistry, windowColumns),
                cast.TargetType),
            CaseExpression caseExpr => RewriteCaseWindowExpression(caseExpr, functionRegistry, windowColumns),
            _ => expression,
        };
    }

    /// <summary>
    /// Rewrites window function references inside a CASE expression.
    /// </summary>
    private static CaseExpression RewriteCaseWindowExpression(
        CaseExpression caseExpression,
        FunctionRegistry functionRegistry,
        List<WindowColumn> windowColumns)
    {
        Expression? rewrittenOperand = caseExpression.Operand is not null
            ? RewriteWindowExpression(caseExpression.Operand, functionRegistry, windowColumns)
            : null;

        List<WhenClause> rewrittenClauses = new(caseExpression.WhenClauses.Count);
        foreach (WhenClause whenClause in caseExpression.WhenClauses)
        {
            rewrittenClauses.Add(new WhenClause(
                RewriteWindowExpression(whenClause.Condition, functionRegistry, windowColumns),
                RewriteWindowExpression(whenClause.Result, functionRegistry, windowColumns)));
        }

        Expression? rewrittenElse = caseExpression.ElseResult is not null
            ? RewriteWindowExpression(caseExpression.ElseResult, functionRegistry, windowColumns)
            : null;

        return new CaseExpression(rewrittenOperand, rewrittenClauses, rewrittenElse, caseExpression.Span);
    }

    // ───────────────────── SCAN expression detection and rewriting ─────────────────────

    /// <summary>
    /// Returns <see langword="true"/> if any <see cref="SelectColumn"/> expression
    /// contains a <see cref="ScanExpression"/>.
    /// </summary>
    private static bool HasScanExpression(IReadOnlyList<SelectColumn> columns)
    {
        foreach (SelectColumn column in columns)
        {
            if (column is SelectAllColumns or SelectTableColumns)
            {
                continue;
            }

            if (ExpressionContainsScanExpression(column.Expression))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> if any <see cref="LetBinding"/> expression
    /// contains a <see cref="ScanExpression"/>.
    /// </summary>
    private static bool HasLetScanExpression(IReadOnlyList<LetBinding>? letBindings)
    {
        if (letBindings is null)
        {
            return false;
        }

        foreach (LetBinding binding in letBindings)
        {
            if (ExpressionContainsScanExpression(binding.Expression))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Recursively checks whether an expression tree contains a <see cref="ScanExpression"/>.
    /// </summary>
    private static bool ExpressionContainsScanExpression(Expression expression)
    {
        return expression switch
        {
            ScanExpression => true,
            BinaryExpression bin => ExpressionContainsScanExpression(bin.Left)
                || ExpressionContainsScanExpression(bin.Right),
            UnaryExpression unary => ExpressionContainsScanExpression(unary.Operand),
            CastExpression cast => ExpressionContainsScanExpression(cast.Expression),
            CaseExpression caseExpr =>
                (caseExpr.Operand is not null && ExpressionContainsScanExpression(caseExpr.Operand))
                || caseExpr.WhenClauses.Any(w =>
                    ExpressionContainsScanExpression(w.Condition) || ExpressionContainsScanExpression(w.Result))
                || (caseExpr.ElseResult is not null && ExpressionContainsScanExpression(caseExpr.ElseResult)),
            FunctionCallExpression func => func.Arguments.Any(ExpressionContainsScanExpression),
            _ => false,
        };
    }

    /// <summary>
    /// Rewrites an expression by replacing <see cref="ScanExpression"/> nodes with
    /// <see cref="ColumnReference"/> nodes that reference the output columns of the
    /// <see cref="Operators.FoldScanOperator"/>. Each SCAN expression is converted to a
    /// <see cref="FoldScanColumn"/> descriptor. PREV() calls inside body expressions
    /// are rewritten to <c>__prev_</c>-prefixed column references.
    /// </summary>
    private static Expression RewriteScanExpression(
        Expression expression,
        List<FoldScanColumn> scanColumns)
    {
        if (expression is ScanExpression scan)
        {
            // Validate counts match.
            if (scan.AccumulatorNames.Count != scan.BodyExpressions.Count
                || scan.AccumulatorNames.Count != scan.InitExpressions.Count
                || scan.AccumulatorNames.Count != scan.OutputAliases.Count)
            {
                throw new InvalidOperationException(
                    "SCAN expression has mismatched accumulator, body, init, and alias counts.");
            }

            if (scan.Window.OrderBy is null or { Count: 0 })
            {
                throw new InvalidOperationException(
                    "SCAN expression requires ORDER BY in the OVER clause.");
            }

            // Collect PREV() column references from body expressions.
            HashSet<string> prevColumnNames = new(StringComparer.OrdinalIgnoreCase);

            // Rewrite PREV(col) calls in body expressions to __prev_col column references.
            List<Expression> rewrittenBodies = new(scan.BodyExpressions.Count);
            foreach (Expression body in scan.BodyExpressions)
            {
                rewrittenBodies.Add(RewritePrevCalls(body, prevColumnNames));
            }

            scanColumns.Add(new FoldScanColumn(
                scan.AccumulatorNames,
                rewrittenBodies,
                scan.InitExpressions,
                scan.Window,
                scan.OutputAliases,
                prevColumnNames.ToList()));

            // Replace the SCAN expression with a column reference to the first output alias.
            // For tuple form, the other aliases are also available as columns from the operator.
            return new ColumnReference(null, scan.OutputAliases[0]);
        }

        // Recurse into sub-expressions.
        return expression switch
        {
            BinaryExpression bin => new BinaryExpression(
                RewriteScanExpression(bin.Left, scanColumns),
                bin.Operator,
                RewriteScanExpression(bin.Right, scanColumns)),
            UnaryExpression unary => new UnaryExpression(
                unary.Operator,
                RewriteScanExpression(unary.Operand, scanColumns)),
            CastExpression cast => new CastExpression(
                RewriteScanExpression(cast.Expression, scanColumns),
                cast.TargetType),
            CaseExpression caseExpr => RewriteCaseScanExpression(caseExpr, scanColumns),
            _ => expression,
        };
    }

    /// <summary>
    /// Rewrites SCAN expression references inside a CASE expression.
    /// </summary>
    private static CaseExpression RewriteCaseScanExpression(
        CaseExpression caseExpression,
        List<FoldScanColumn> scanColumns)
    {
        Expression? rewrittenOperand = caseExpression.Operand is not null
            ? RewriteScanExpression(caseExpression.Operand, scanColumns)
            : null;

        List<WhenClause> rewrittenClauses = new(caseExpression.WhenClauses.Count);
        foreach (WhenClause whenClause in caseExpression.WhenClauses)
        {
            rewrittenClauses.Add(new WhenClause(
                RewriteScanExpression(whenClause.Condition, scanColumns),
                RewriteScanExpression(whenClause.Result, scanColumns)));
        }

        Expression? rewrittenElse = caseExpression.ElseResult is not null
            ? RewriteScanExpression(caseExpression.ElseResult, scanColumns)
            : null;

        return new CaseExpression(rewrittenOperand, rewrittenClauses, rewrittenElse, caseExpression.Span);
    }

    /// <summary>
    /// Rewrites <c>PREV(col)</c> function calls into <c>__prev_col</c> column references.
    /// Collects the <c>__prev_</c>-prefixed column names into <paramref name="prevColumnNames"/>.
    /// </summary>
    private static Expression RewritePrevCalls(
        Expression expression,
        HashSet<string> prevColumnNames)
    {
        if (expression is FunctionCallExpression func
            && string.Equals(func.FunctionName, "PREV", StringComparison.OrdinalIgnoreCase))
        {
            if (func.Arguments.Count != 1 || func.Arguments[0] is not ColumnReference colRef)
            {
                throw new InvalidOperationException(
                    "PREV() requires exactly one column reference argument.");
            }

            string prevName = "__prev_" + colRef.ColumnName;
            prevColumnNames.Add(prevName);
            return new ColumnReference(null, prevName);
        }

        return expression switch
        {
            BinaryExpression bin => new BinaryExpression(
                RewritePrevCalls(bin.Left, prevColumnNames),
                bin.Operator,
                RewritePrevCalls(bin.Right, prevColumnNames)),
            UnaryExpression unary => new UnaryExpression(
                unary.Operator,
                RewritePrevCalls(unary.Operand, prevColumnNames)),
            CastExpression cast => new CastExpression(
                RewritePrevCalls(cast.Expression, prevColumnNames),
                cast.TargetType),
            FunctionCallExpression funcExpr => new FunctionCallExpression(
                funcExpr.FunctionName,
                funcExpr.Arguments.Select(a => RewritePrevCalls(a, prevColumnNames)).ToList(),
                funcExpr.OrderBy,
                funcExpr.Distinct,
                funcExpr.Span),
            IsNullExpression isNull => new IsNullExpression(
                RewritePrevCalls(isNull.Expression, prevColumnNames),
                isNull.Negated),
            BetweenExpression between => new BetweenExpression(
                RewritePrevCalls(between.Expression, prevColumnNames),
                RewritePrevCalls(between.Low, prevColumnNames),
                RewritePrevCalls(between.High, prevColumnNames),
                between.Negated),
            InExpression inExpr => new InExpression(
                RewritePrevCalls(inExpr.Expression, prevColumnNames),
                inExpr.Values.Select(v => RewritePrevCalls(v, prevColumnNames)).ToList(),
                inExpr.Negated),
            CaseExpression caseExpr => new CaseExpression(
                caseExpr.Operand is not null ? RewritePrevCalls(caseExpr.Operand, prevColumnNames) : null,
                caseExpr.WhenClauses.Select(w => new WhenClause(
                    RewritePrevCalls(w.Condition, prevColumnNames),
                    RewritePrevCalls(w.Result, prevColumnNames))).ToList(),
                caseExpr.ElseResult is not null ? RewritePrevCalls(caseExpr.ElseResult, prevColumnNames) : null,
                caseExpr.Span),
            IndexAccessExpression idx => new IndexAccessExpression(
                RewritePrevCalls(idx.Source, prevColumnNames),
                RewritePrevCalls(idx.Index, prevColumnNames),
                idx.Span),
            _ => expression,
        };
    }

    /// <summary>
    /// Validates that every ORDER BY expression appears in the SELECT list
    /// when SELECT DISTINCT is active. If an ORDER BY column is not projected,
    /// the result would be ambiguous because DISTINCT collapses rows before sorting.
    /// </summary>
    private static void ValidateDistinctOrderBy(
        IReadOnlyList<SelectColumn> selectColumns,
        OrderByClause orderBy)
    {
        // Collect the effective output names from the SELECT list.
        HashSet<string> selectedNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (SelectColumn column in selectColumns)
        {
            if (column is SelectAllColumns or SelectTableColumns)
            {
                // SELECT * projects everything — any ORDER BY is valid.
                return;
            }

            if (column.Alias is not null)
            {
                selectedNames.Add(column.Alias);
            }

            // Also accept the raw expression form (e.g. "t.name") so that
            // ORDER BY t.name matches SELECT t.name even without an alias.
            selectedNames.Add(QueryExplainer.FormatExpression(column.Expression));
        }

        foreach (OrderByItem item in orderBy.Items)
        {
            string orderExpression = QueryExplainer.FormatExpression(item.Expression);
            if (!selectedNames.Contains(orderExpression))
            {
                throw new InvalidOperationException(
                    $"ORDER BY expression '{orderExpression}' must appear in the SELECT list " +
                    $"when SELECT DISTINCT is specified.");
            }
        }
    }

    /// <summary>
    /// Detects the <c>COUNT(*)</c> sentinel pattern: a function call to COUNT with a
    /// single <see cref="LiteralExpression"/> argument whose value is <c>"*"</c>.
    /// </summary>
    private static bool IsCountStarCall(FunctionCallExpression function)
    {
        return string.Equals(function.FunctionName, "COUNT", StringComparison.OrdinalIgnoreCase)
            && function.Arguments.Count == 1
            && function.Arguments[0] is LiteralExpression literal
            && literal.Value is string value
            && value == "*";
    }

    private IQueryOperator PlanSource(
        TableSource source,
        HashSet<(string? TableName, string ColumnName)> allReferencedColumns,
        bool hasJoins,
        IReadOnlyDictionary<string, CommonTableExpressionOperator>? commonTableExpressionOperators = null)
    {
        return source switch
        {
            TableReference tableRef => PlanTableReference(tableRef, allReferencedColumns, hasJoins, commonTableExpressionOperators),
            SubquerySource subquery => PlanSubquery(subquery),
            FunctionSource functionSource => PlanFunctionSource(functionSource, hasJoins),
            _ => throw new InvalidOperationException(
                $"Unsupported table source type: {source.GetType().Name}."),
        };
    }

    private IQueryOperator PlanTableReference(
        TableReference tableRef,
        HashSet<(string? TableName, string ColumnName)> allReferencedColumns,
        bool hasJoins,
        IReadOnlyDictionary<string, CommonTableExpressionOperator>? commonTableExpressionOperators = null)
    {

        // CTE reference: return the shared CTE operator wrapped with an alias.
        if (commonTableExpressionOperators is not null &&
            commonTableExpressionOperators.TryGetValue(tableRef.Name, out CommonTableExpressionOperator? commonTableExpressionOperator))
        {
            IQueryOperator cteSource = commonTableExpressionOperator;
            if (tableRef.Alias is not null || hasJoins)
            {
                cteSource = new AliasOperator(cteSource, tableRef.Alias ?? tableRef.Name);
            }

            return cteSource;
        }

        ITableProvider provider = _catalog[tableRef.Name];

        // Projection pushdown: compute required columns for this table's alias.
        string effectiveAlias = tableRef.Alias ?? tableRef.Name;
        IReadOnlySet<string>? requiredColumns =
            ComputeRequiredColumns(effectiveAlias, allReferencedColumns);

        long rowCount = provider.GetRowCount();
        ScanOperator scanOperator = new(provider, requiredColumns, rowCount);

        // Attach per-column statistics from manifest if available.
        if (provider.GetManifest() is Manifest.QueryResultsManifest manifest)
        {
            // Build column-name → FeatureManifest lookup for selectivity estimation.
            Dictionary<string, Manifest.FeatureManifest> columnStatistics = new(StringComparer.OrdinalIgnoreCase);
            foreach (Manifest.FeatureManifest feature in manifest.Features)
            {
                columnStatistics[feature.Name] = feature;
            }

            scanOperator.ColumnStatistics = columnStatistics;
        }

        IQueryOperator outOperator = scanOperator;

        // Apply TABLESAMPLE row/chunk sampling if the table reference includes a sampling clause.
        if (tableRef.Tablesample is TablesampleClause tablesampleClause)
        {
            double argument = EvaluateConstantDouble(tablesampleClause.Percentage);
            int? seed = tablesampleClause.Seed is not null
                ? (int)EvaluateConstantDouble(tablesampleClause.Seed)
                : null;

            outOperator = tablesampleClause.Method switch
            {
                TablesampleMethod.Bernoulli or TablesampleMethod.System =>
                    new SampleScanOperator(outOperator, tablesampleClause.Method, argument, seed),
                TablesampleMethod.Stratified =>
                    new StratifiedSampleOperator(
                        outOperator, argument,
                        tablesampleClause.StratifyColumns!.Select(c => c.ColumnName).ToArray(), seed),
                TablesampleMethod.Balanced =>
                    new BalancedSampleOperator(
                        outOperator, (int)argument,
                        tablesampleClause.StratifyColumns!.Select(c => c.ColumnName).ToArray(), seed),
                _ => throw new InvalidOperationException(
                    $"Unknown TABLESAMPLE method: {tablesampleClause.Method}"),
            };
        }

        // Wrap column names with the alias prefix. When the query involves JOINs,
        // unaliased tables are implicitly aliased with their table name to prevent
        // column name collisions in the combined row schema.
        if (tableRef.Alias is not null || hasJoins)
        {
            outOperator = new AliasOperator(outOperator, tableRef.Alias ?? tableRef.Name);
        }

        return outOperator;
    }

    private IQueryOperator PlanSubquery(SubquerySource subquery)
    {
        IQueryOperator innerPlan = Plan(subquery.Query);
        return new SubqueryOperator(innerPlan, subquery.Alias);
    }

    private IQueryOperator PlanFunctionSource(FunctionSource functionSource, bool hasJoins)
    {
        ITableValuedFunction? function = _functionRegistry.TryGetTableValued(functionSource.FunctionName);

        if (function is null)
        {
            throw new InvalidOperationException(
                $"Unknown table-valued function: '{functionSource.FunctionName}'.");
        }

        IQueryOperator sourceOperator = new FunctionSourceOperator(function, functionSource.Arguments);

        if (functionSource.Alias is not null || hasJoins)
        {
            sourceOperator = new AliasOperator(
                sourceOperator, functionSource.Alias ?? functionSource.FunctionName);
        }

        return sourceOperator;
    }

    /// <summary>
    /// Collects column references from WHERE, JOIN ON, and ORDER BY — the places
    /// where column values are needed by intermediate pipeline operators (filter,
    /// join, sort). Columns referenced only in SELECT are output-only.
    /// </summary>
    private static HashSet<(string? TableName, string ColumnName)> CollectPipelineColumns(
        SelectStatement statement)
    {
        HashSet<(string? TableName, string ColumnName)> references = new();

        if (statement.Where is not null)
        {
            foreach ((string? tableName, string columnName) in
                ColumnReferenceCollector.Collect(statement.Where))
            {
                references.Add((tableName, columnName));
            }
        }

        if (statement.Joins is not null)
        {
            foreach (JoinClause join in statement.Joins)
            {
                if (join.OnCondition is not null)
                {
                    foreach ((string? tableName, string columnName) in
                        ColumnReferenceCollector.Collect(join.OnCondition))
                    {
                        references.Add((tableName, columnName));
                    }
                }
            }
        }

        if (statement.OrderBy is not null)
        {
            foreach (OrderByItem item in statement.OrderBy.Items)
            {
                foreach ((string? tableName, string columnName) in
                    ColumnReferenceCollector.Collect(item.Expression))
                {
                    references.Add((tableName, columnName));
                }
            }
        }

        // GROUP BY expressions reference source columns needed before aggregation.
        if (statement.GroupBy is not null)
        {
            foreach (Expression groupExpression in statement.GroupBy.Expressions)
            {
                foreach ((string? tableName, string columnName) in
                    ColumnReferenceCollector.Collect(groupExpression))
                {
                    references.Add((tableName, columnName));
                }
            }
        }

        // HAVING can reference source columns via aggregate argument expressions.
        if (statement.Having is not null)
        {
            foreach ((string? tableName, string columnName) in
                ColumnReferenceCollector.Collect(statement.Having))
            {
                references.Add((tableName, columnName));
            }
        }

        // QUALIFY can reference source columns and window function output columns.
        if (statement.Qualify is not null)
        {
            foreach ((string? tableName, string columnName) in
                ColumnReferenceCollector.Collect(statement.Qualify))
            {
                references.Add((tableName, columnName));
            }
        }

        return references;
    }

    /// <summary>
    /// Plans all Common Table Expressions from the WITH clause.
    /// Each CTE body is planned into an operator tree and wrapped in a
    /// <see cref="CommonTableExpressionOperator"/>. Materialization is determined
    /// by the explicit hint or by reference counting (auto-materialize when referenced
    /// more than once).
    /// </summary>
    /// <returns>
    /// A dictionary mapping CTE names to their operators, or <see langword="null"/>
    /// if the statement has no CTEs.
    /// </returns>
    private Dictionary<string, CommonTableExpressionOperator>? PlanCommonTableExpressions(
        SelectStatement statement)
    {
        if (statement.CommonTableExpressions is null || statement.CommonTableExpressions.Count == 0)
        {
            return null;
        }

        Dictionary<string, int> referenceCounts =
            CountCommonTableExpressionReferences(statement);

        Dictionary<string, CommonTableExpressionOperator> operators = new(
            statement.CommonTableExpressions.Count, StringComparer.OrdinalIgnoreCase);

        foreach (CommonTableExpression commonTableExpression in statement.CommonTableExpressions)
        {
            if (commonTableExpression.IsRecursive && commonTableExpression.RecursiveQuery is not null)
            {
                // Recursive CTEs store the anchor as a SelectQueryExpression wrapper.
                SelectStatement anchorStatement = commonTableExpression.Body switch
                {
                    SelectQueryExpression select => select.Statement,
                    _ => throw new QueryPlanException(
                        $"Recursive CTE '{commonTableExpression.Name}' body must be a single SELECT statement (the anchor member)."),
                };

                IQueryOperator anchorPlan = PlanCore(
                    anchorStatement,
                    externalCommonTableExpressionOperators: operators);

                // Capture for the closure. The factory is called at execution time,
                // once per iteration, with a fresh working-table operator.
                CommonTableExpression capturedDefinition = commonTableExpression;
                QueryPlanner capturedPlanner = this;

                RecursiveCommonTableExpressionOperator recursiveOperator = new(
                    anchorPlan,
                    workingTableOperator =>
                    {
                        // Build a CTE dictionary that maps the self-reference to the
                        // working table so the recursive member's FROM resolves correctly.
                        CommonTableExpressionOperator selfReference = new(
                            workingTableOperator,
                            capturedDefinition.Name,
                            isMaterialized: false);

                        Dictionary<string, CommonTableExpressionOperator> selfReferenceOperators = new(
                            StringComparer.OrdinalIgnoreCase);

                        // Include all previously-built CTEs so the recursive member
                        // can reference sibling CTEs in addition to itself.
                        foreach (KeyValuePair<string, CommonTableExpressionOperator> existing in operators)
                        {
                            selfReferenceOperators[existing.Key] = existing.Value;
                        }

                        selfReferenceOperators[capturedDefinition.Name] = selfReference;

                        return capturedPlanner.PlanCore(
                            capturedDefinition.RecursiveQuery,
                            externalCommonTableExpressionOperators: selfReferenceOperators);
                    },
                    commonTableExpression.Name,
                    commonTableExpression.ColumnNames);

                // Wrap recursive operator so PlanTableReference can resolve the CTE by name.
                CommonTableExpressionOperator wrappedRecursive = new(
                    recursiveOperator,
                    commonTableExpression.Name,
                    isMaterialized: false);

                operators[commonTableExpression.Name] = wrappedRecursive;
                continue;
            }

            IQueryOperator innerPlan = operators.Count > 0
                ? PlanWithSiblingCommonTableExpressions(commonTableExpression.Body, operators)
                : Plan(commonTableExpression.Body);

            bool shouldMaterialize = commonTableExpression.Hint switch
            {
                MaterializationHint.Materialized => true,
                MaterializationHint.NotMaterialized => false,
                // Auto-materialize when referenced more than once to avoid redundant computation.
                _ => referenceCounts.TryGetValue(commonTableExpression.Name, out int count) && count > 1,
            };

            CommonTableExpressionOperator cteOperator = new(
                innerPlan,
                commonTableExpression.Name,
                shouldMaterialize,
                commonTableExpression.ColumnNames);

            operators[commonTableExpression.Name] = cteOperator;
        }

        return operators.Count > 0 ? operators : null;
    }

    /// <summary>
    /// Counts how many times each CTE name appears as a table reference in the
    /// FROM clause and JOIN sources of the statement.
    /// </summary>
    private static Dictionary<string, int> CountCommonTableExpressionReferences(
        SelectStatement statement)
    {
        Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);

        if (statement.CommonTableExpressions is null)
        {
            return counts;
        }

        // Build the set of known CTE names for fast lookup.
        HashSet<string> cteNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (CommonTableExpression commonTableExpression in statement.CommonTableExpressions)
        {
            cteNames.Add(commonTableExpression.Name);
            counts[commonTableExpression.Name] = 0;
        }

        // Count references in FROM source.
        if (statement.From is not null)
        {
            CountCommonTableExpressionReferencesInSource(statement.From.Source, cteNames, counts);
        }

        // Count references in JOIN sources.
        if (statement.Joins is not null)
        {
            foreach (JoinClause join in statement.Joins)
            {
                CountCommonTableExpressionReferencesInSource(join.Source, cteNames, counts);
            }
        }

        return counts;
    }

    /// <summary>
    /// Recursively counts CTE name references within a single table source.
    /// </summary>
    private static void CountCommonTableExpressionReferencesInSource(
        TableSource source,
        IReadOnlySet<string> commonTableExpressionNames,
        Dictionary<string, int> counts)
    {
        switch (source)
        {
            case TableReference tableReference:
                if (commonTableExpressionNames.Contains(tableReference.Name))
                {
                    counts[tableReference.Name]++;
                }

                break;

            case SubquerySource:
            case FunctionSource:
                // Subqueries and functions do not reference outer CTEs.
                break;
        }
    }

    /// <summary>
    /// Evaluates a constant expression to a <see cref="double"/> at plan time.
    /// Used for TABLESAMPLE percentage and REPEATABLE seed values.
    /// </summary>
    private static double EvaluateConstantDouble(Expression expression)
    {
        return expression switch
        {
            LiteralExpression { Value: sbyte int8Value } => int8Value,
            LiteralExpression { Value: short int16Value } => int16Value,
            LiteralExpression { Value: int intValue } => intValue,
            LiteralExpression { Value: long longValue } => longValue,
            LiteralExpression { Value: float floatValue } => floatValue,
            LiteralExpression { Value: double doubleValue } => doubleValue,
            _ => throw new InvalidOperationException(
                "TABLESAMPLE percentage and REPEATABLE seed must be constant numeric values."),
        };
    }
}

/// <summary>
/// Describes the expensive columns deferred for late materialization on a specific table.
/// </summary>
/// <param name="Descriptor">Table descriptor for creating the keyed provider.</param>
/// <param name="KeyColumn">Unqualified column name used for keyed lookup.</param>
/// <param name="ColumnNames">Unqualified names of the expensive columns to fetch later.</param>
internal sealed record DeferredTableColumns(
    TableDescriptor Descriptor,
    string KeyColumn,
    IReadOnlySet<string> ColumnNames);
