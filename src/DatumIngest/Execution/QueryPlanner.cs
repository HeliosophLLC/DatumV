using System.Diagnostics.CodeAnalysis;
using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Diagnostics;
using DatumIngest.Execution.Operators;
using DatumIngest.Execution.Planner;
using DatumIngest.Functions;
using DatumIngest.Indexing;
using DatumIngest.Indexing.Fts;
using DatumIngest.Model;
using DatumIngest.Models;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

/// <summary>
/// Transforms a parsed <see cref="SelectStatement"/> AST into an executable
/// operator tree (<see cref="QueryOperator"/>). Applies predicate pushdown
/// to filter rows early and projection pushdown to skip unreferenced columns
/// at the source.
/// </summary>
public sealed class QueryPlanner
{
    private readonly TableCatalog _catalog;
    private readonly FunctionRegistry _functionRegistry;
    private readonly SourcePlanner _sourcePlanner;

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
        _sourcePlanner = new SourcePlanner(catalog, functionRegistry, Plan);
    }

    /// <summary>
    /// Plans the given query expression into an operator tree ready for execution.
    /// Dispatches to the appropriate planning method based on whether the query
    /// is a single SELECT or a compound set operation.
    /// </summary>
    /// <param name="query">The parsed query expression.</param>
    /// <returns>The root operator of the execution plan.</returns>
    public QueryOperator Plan(QueryExpression query)
    {
        // PG-style named arguments — rewrite fn(a := 1, b => 2) into the
        // canonical positional shape. TableCatalog.PlanQuery runs this
        // pass before UdfInliner so macro-UDF substitution sees aligned
        // arguments; the re-application here is idempotent (no-op when
        // every call has already been permuted) and covers test-harness
        // paths that call the planner directly.
        query = NamedArgPermuter.Permute(query, _functionRegistry, _catalog.Udfs, _catalog.SearchPath);

        // Plan-time function gate. The planner is only entered for top-level
        // queries — procedural UDF / model bodies are interpreted by their
        // respective adapters (`ProceduralUdfFunction`, `ProceduralModelFunction`)
        // and never reach here. The gate walks every reachable function call
        // and refuses unknown names (so a typo can't survive long enough to
        // warm an ONNX session in a neighboring projection) plus body-scoped
        // calls out of context.
        PlanTimeFunctionGate.EnforceForQuery(query, _functionRegistry, _catalog.Models);

        QueryOperator op = query switch
        {
            SelectQueryExpression select => Plan(select.Statement),
            CompoundQueryExpression compound => PlanCompound(compound),
            InsertQueryExpression insertQuery => PlanInsertQueryExpression(insertQuery),
            UpdateQueryExpression updateQuery => PlanUpdateQueryExpression(updateQuery),
            DeleteQueryExpression deleteQuery => PlanDeleteQueryExpression(deleteQuery),
            _ => throw new InvalidOperationException($"Unexpected query expression type: {query.GetType().Name}"),
        };
        return Finalize(op);
    }

    /// <summary>
    /// Plans an <see cref="InsertQueryExpression"/> (a data-modifying CTE body)
    /// into a <see cref="Operators.DmlReturningOperator"/>. The INSERT side
    /// effect fires when the surrounding plan executes — matching PostgreSQL's
    /// modifying-CTE semantics. <c>EXPLAIN</c> does not commit it.
    /// </summary>
    private QueryOperator PlanInsertQueryExpression(InsertQueryExpression insertQuery)
    {
        if (insertQuery.Insert.Returning is null)
        {
            throw new InvalidOperationException(
                $"INSERT INTO '{insertQuery.Insert.TableName}' inside a CTE body must include a " +
                "RETURNING clause — without it the CTE has no rows to project.");
        }

        return Operators.DmlReturningOperator.ForInsert(_catalog, insertQuery.Insert);
    }

    /// <summary>
    /// Plans an <see cref="UpdateQueryExpression"/> (a data-modifying CTE body).
    /// Same modifying-CTE semantics as INSERT: side effect fires once per
    /// surrounding execution; RETURNING is required.
    /// </summary>
    private QueryOperator PlanUpdateQueryExpression(UpdateQueryExpression updateQuery)
    {
        if (updateQuery.Update.Returning is null)
        {
            throw new InvalidOperationException(
                $"UPDATE '{updateQuery.Update.TableName}' inside a CTE body must include a " +
                "RETURNING clause — without it the CTE has no rows to project.");
        }

        return Operators.DmlReturningOperator.ForUpdate(_catalog, updateQuery.Update);
    }

    /// <summary>
    /// Plans a <see cref="DeleteQueryExpression"/> (a data-modifying CTE body).
    /// Same modifying-CTE semantics as INSERT: side effect fires once per
    /// surrounding execution; RETURNING is required.
    /// </summary>
    private QueryOperator PlanDeleteQueryExpression(DeleteQueryExpression deleteQuery)
    {
        if (deleteQuery.Delete.Returning is null)
        {
            throw new InvalidOperationException(
                $"DELETE FROM '{deleteQuery.Delete.TableName}' inside a CTE body must include a " +
                "RETURNING clause — without it the CTE has no rows to project.");
        }

        return Operators.DmlReturningOperator.ForDelete(_catalog, deleteQuery.Delete);
    }

    /// <summary>
    /// Plans the given statement into an operator tree ready for execution.
    /// </summary>
    /// <param name="statement">The parsed SELECT statement.</param>
    /// <returns>The root operator of the execution plan.</returns>
    public QueryOperator Plan(SelectStatement statement)
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
    public async Task<QueryOperator> PlanAsync(
        QueryExpression query,
        CancellationToken cancellationToken)
    {
        QueryOperator op = query switch
        {
            SelectQueryExpression select => PlanCore(select.Statement),
            CompoundQueryExpression compound => await PlanCompoundAsync(compound, cancellationToken).ConfigureAwait(false),
            InsertQueryExpression insertQuery => PlanInsertQueryExpression(insertQuery),
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
    public async Task<QueryOperator> PlanWithSubqueriesAsync(
        QueryExpression query,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        // Mirrors the sync Plan() entry point: PG-style named arguments
        // are rewritten into positional form before any operator-building
        // pass. Idempotent — TableCatalog.PlanQuery already permuted user-
        // facing paths; this call covers direct planner invocations from
        // test fixtures and from PlanCompoundWithSubqueriesAsync's
        // recursion.
        query = NamedArgPermuter.Permute(query, _functionRegistry, _catalog.Udfs, _catalog.SearchPath);

        QueryOperator op = query switch
        {
            SelectQueryExpression select =>
                await PlanCoreWithSubqueriesAsync(select.Statement, context, cancellationToken).ConfigureAwait(false),
            CompoundQueryExpression compound =>
                await PlanCompoundWithSubqueriesAsync(compound, context, cancellationToken).ConfigureAwait(false),
            InsertQueryExpression insertQuery => PlanInsertQueryExpression(insertQuery),
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
    /// instance optimisation that lives in <see cref="DatumIngest.Catalog.Plans.QueryPlan"/>
    /// because the hoist arena's lifetime is tied to that instance, not to the planner.
    /// </remarks>
    private QueryOperator Finalize(QueryOperator op)
    {
        QueryOperator afterModelHoist = ModelInvocationHoister.Hoist(op, _catalog.Models);
        // Collapse adjacent single-invocation MIO stacks into one
        // multi-invocation MIO so the column-major dispatch path (one
        // model active at a time per upstream batch, lease released
        // between invocations) replaces the stacked-MIO shape for
        // multi-model queries. Single-MIO plans are left unchanged.
        QueryOperator afterDagCollapse = BatchedModelDagCollapser.Collapse(afterModelHoist);
        // Post-pass entry point. Historically this lowered SQL-defined
        // straight-line bodies into a chain of ProjectOperator + InferOperator
        // nodes for cross-row batching; that path was removed because the
        // multi-operator pipeline paid for arena retention + sidecar
        // re-decode at every boundary (measured ~20× slower per row than
        // the unified MIO + ProceduralModelAdapter path). The pass is kept
        // as a call site so per-model rewrites that DO benefit from plan-
        // shape changes have a hook; currently it's a no-op walk.
        QueryOperator afterBodyLower = ModelBodyLowerer.LowerSqlDefinedBodies(
            afterDagCollapse, _catalog.DeclaredModels);
        // Decompose composite metadata functions (pixel_count, dimensions(literal))
        // into compositions of image_width / image_height / image_channels so the
        // elider below can fold each component into a struct read and CSE can
        // collapse repeated accessor references across clauses.
        QueryOperator afterMetadataLower = ImageMetadataLowerer.Lower(
            afterBodyLower, _functionRegistry, _catalog.SearchPath);
        // Rewrite IInlineMetadataAccessor calls (image_width, video_height, ...)
        // into InlineAccessorExpression so the evaluator skips IScalarFunction
        // dispatch on the common stamped-metadata path. Must run BEFORE CSE so
        // repeated accessor calls in WHERE + SELECT + ORDER BY dedup on the
        // rewritten node's record equality.
        QueryOperator afterAccessorElide = InlineAccessorElider.Elide(
            afterMetadataLower, _functionRegistry, _catalog.SearchPath);
        return CommonSubexpressionEliminator.Eliminate(afterAccessorElide, _functionRegistry);
    }

    /// <summary>
    /// Plans a compound set operation (UNION, INTERSECT, EXCEPT) by recursively
    /// planning both branches and combining them with a <see cref="SetOperationOperator"/>.
    /// ORDER BY, LIMIT, and OFFSET on the compound are applied on top.
    /// </summary>
    private QueryOperator PlanCompound(CompoundQueryExpression compound)
    {
        QueryOperator left = Plan(compound.Left);
        QueryOperator right = Plan(compound.Right);
        QueryOperator result = new SetOperationOperator(left, right, compound.OperationType, compound.All);

        result = CompoundQueryClauses.ApplyCompoundTrailingClauses(result, compound);
        return result;
    }

    /// <summary>
    /// Plans a query expression with access to sibling CTE operators so that
    /// table references inside the expression can resolve earlier CTEs.
    /// Used when planning non-recursive CTE bodies that may reference sibling CTEs.
    /// </summary>
    private QueryOperator PlanWithSiblingCommonTableExpressions(
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
    private QueryOperator PlanCompoundWithSiblingCommonTableExpressions(
        CompoundQueryExpression compound,
        IReadOnlyDictionary<string, CommonTableExpressionOperator> siblingOperators)
    {
        QueryOperator left = PlanWithSiblingCommonTableExpressions(compound.Left, siblingOperators);
        QueryOperator right = PlanWithSiblingCommonTableExpressions(compound.Right, siblingOperators);
        QueryOperator result = new SetOperationOperator(left, right, compound.OperationType, compound.All);

        result = CompoundQueryClauses.ApplyCompoundTrailingClauses(result, compound);
        return result;
    }

    /// <summary>
    /// Async variant of <see cref="PlanCompound"/> with late materialization.
    /// </summary>
    private async Task<QueryOperator> PlanCompoundAsync(
        CompoundQueryExpression compound, CancellationToken cancellationToken)
    {
        QueryOperator left = await PlanAsync(compound.Left, cancellationToken).ConfigureAwait(false);
        QueryOperator right = await PlanAsync(compound.Right, cancellationToken).ConfigureAwait(false);
        QueryOperator result = new SetOperationOperator(left, right, compound.OperationType, compound.All);

        result = CompoundQueryClauses.ApplyCompoundTrailingClauses(result, compound);
        return result;
    }

    /// <summary>
    /// Async variant of <see cref="PlanCompound"/> with subquery rewriting.
    /// </summary>
    private async Task<QueryOperator> PlanCompoundWithSubqueriesAsync(
        CompoundQueryExpression compound, ExecutionContext context, CancellationToken cancellationToken)
    {
        QueryOperator left = await PlanWithSubqueriesAsync(compound.Left, context, cancellationToken).ConfigureAwait(false);
        QueryOperator right = await PlanWithSubqueriesAsync(compound.Right, context, cancellationToken).ConfigureAwait(false);
        QueryOperator result = new SetOperationOperator(left, right, compound.OperationType, compound.All);

        result = CompoundQueryClauses.ApplyCompoundTrailingClauses(result, compound);
        return result;
    }


    /// <summary>
    /// Plan-time constant-fold of LIMIT (and optional OFFSET) into a single
    /// topN row count, used to enable OrderByOperator's bounded-heap path.
    /// Returns <see langword="null"/> when either expression isn't a
    /// foldable literal — the planner then falls back to a full sort.
    /// </summary>
    /// <summary>
    /// Core planning logic shared by <see cref="Plan(SelectStatement)"/> and the
    /// rewriting pipeline in <see cref="PlanWithSubqueriesAsync"/>.
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
    /// <param name="extraReferenceExpressions">
    /// Optional out-of-band expressions whose column references must be added to the
    /// projection-pushdown set even though they don't appear in the rewritten statement
    /// (e.g. ON conditions of decorrelated/semi-join descriptors injected by
    /// <see cref="PlanWithSubqueriesAsync"/>).
    /// </param>
    private QueryOperator PlanCore(
        SelectStatement statement,
        Func<QueryOperator, QueryOperator>? sourceTransform = null,
        IReadOnlyDictionary<string, CommonTableExpressionOperator>? externalCommonTableExpressionOperators = null,
        IReadOnlyList<Expression>? extraReferenceExpressions = null)
    {
        // 0. Plan Common Table Expressions (WITH clause).
        Dictionary<string, CommonTableExpressionOperator>? commonTableExpressionOperators =
            CommonTableExpressionPlanner.Plan(
                statement,
                planSelectStatementWithCommonTableExpressions: (stmt, ctes) =>
                    PlanCore(stmt, externalCommonTableExpressionOperators: ctes),
                planBodyWithCommonTableExpressions: (body, ctes) =>
                    ctes.Count > 0
                        ? PlanWithSiblingCommonTableExpressions(body, ctes)
                        : Plan(body));

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
            ProjectionPushdown.CollectAllReferencedColumns(statement);

        // Merge in references from out-of-band ON conditions (semi-join descriptors,
        // decorrelated scalar subqueries). These expressions reference outer-table
        // columns at execution time but do not appear in the rewritten statement's
        // WHERE/JOIN clauses, so CollectAllReferencedColumns can't discover them on
        // its own — projection pushdown would then trim a column the semi-join's
        // probe-key evaluation needs and fail with "Column not found in row".
        // An empty set is the SELECT * sentinel ("all columns needed"); leaving it
        // empty preserves that semantic (the extras are already covered).
        if (extraReferenceExpressions is not null && allReferencedColumns.Count > 0)
        {
            foreach (Expression expression in extraReferenceExpressions)
            {
                foreach ((string? tableName, string columnName) in
                    ColumnReferenceCollector.Collect(expression))
                {
                    allReferencedColumns.Add((tableName, columnName));
                }
            }
        }

        // 1. Build the source operator (FROM clause) with projection pushdown.
        bool hasJoins = statement.Joins is not null && statement.Joins.Count > 0;
        QueryOperator source = statement.From is not null
            ? _sourcePlanner.PlanSource(statement.From.Source, allReferencedColumns, hasJoins, commonTableExpressionOperators)
            : new SingleEmptyRowOperator();

        // Track which table aliases are available on the current (left) side.
        HashSet<string> leftAliases = new(StringComparer.OrdinalIgnoreCase);
        if (statement.From is not null)
        {
            SourceAliases.CollectSourceAliases(statement.From.Source, leftAliases);
        }

        // 2. Apply JOINs with predicate pushdown.
        List<Expression>? pendingPredicates = null;

        if (statement.Where is not null)
        {
            pendingPredicates = new List<Expression>();
            PredicateUtilities.FlattenAnd(statement.Where, pendingPredicates);
        }

        // Hold back any WHERE predicate that references a LET binding name.
        // PushPredicatesBelow treats unqualified-only predicates as "globally
        // pushable" and wraps them directly above the source — but a LET-
        // referencing predicate can't safely evaluate there because the LET's
        // hidden column hasn't been computed yet. The held-back predicates are
        // processed in step 3 below by LiftLetBindingsForWhere, which inserts
        // the LET rungs first and only then wraps a Filter above them.
        List<Expression>? letReferencingPredicates = null;
        if (statement.LetBindings is { Count: > 0 } && pendingPredicates is { Count: > 0 })
        {
            HashSet<string> letNames = new(StringComparer.OrdinalIgnoreCase);
            foreach (LetBinding b in statement.LetBindings) letNames.Add(b.Name);

            for (int i = pendingPredicates.Count - 1; i >= 0; i--)
            {
                bool referencesLet = false;
                foreach ((string? tableName, string columnName) in
                    ColumnReferenceCollector.Collect(pendingPredicates[i]))
                {
                    if (tableName is null && letNames.Contains(columnName))
                    {
                        referencesLet = true;
                        break;
                    }
                }
                if (referencesLet)
                {
                    letReferencingPredicates ??= new List<Expression>();
                    letReferencingPredicates.Add(pendingPredicates[i]);
                    pendingPredicates.RemoveAt(i);
                }
            }
        }

        if (statement.Joins is not null)
        {
            // Pre-plan all join sources so we can inspect estimated row counts.
            List<(JoinClause Join, QueryOperator Operator, HashSet<string> Aliases)> plannedJoins = new(statement.Joins.Count);

            foreach (JoinClause join in statement.Joins)
            {
                QueryOperator rightSide = _sourcePlanner.PlanSource(join.Source, allReferencedColumns, hasJoins, commonTableExpressionOperators);
                HashSet<string> rightAliases = new(StringComparer.OrdinalIgnoreCase);
                SourceAliases.CollectSourceAliases(join.Source, rightAliases);
                plannedJoins.Add((join, rightSide, rightAliases));
            }

            // When ORDER BY has a single qualified column reference, check whether
            // the referenced table has a sorted column index on that column. If so,
            // pass the alias to TryReorderJoins so it protects that table as the
            // outermost probe, enabling sort elimination via IndexScanOperator.
            string? orderBySortTableAlias = OrderByAnalyzer.GetOrderBySortTableAlias(
                statement.OrderBy, source, leftAliases, plannedJoins);

            // Join elimination: remove LEFT JOINs whose right-side table is not
            // referenced anywhere in the query output and is not required by any
            // other surviving join. Safe because a LEFT JOIN to an unreferenced
            // table cannot filter rows (it preserves all left-side rows).
            JoinReorderer.EliminateUnusedJoins(statement, plannedJoins);

            // Greedy join reordering: place the largest table on the probe
            // (streaming) side so LIMIT can short-circuit earlier, and build
            // the smaller tables into hash tables. Only applied when every
            // join is a non-lateral INNER join and all sources have estimated
            // row counts. This is a heuristic — the roadmap CBO will replace it.
            if (JoinReorderer.TryReorderJoins(source, leftAliases, plannedJoins, orderBySortTableAlias,
                out QueryOperator? reorderedSource, out HashSet<string>? reorderedFromAliases,
                out List<(JoinClause Join, QueryOperator Operator, HashSet<string> Aliases)>? reorderedJoins))
            {
                source = reorderedSource;
                leftAliases = reorderedFromAliases;
                plannedJoins = reorderedJoins;
            }

            foreach ((JoinClause join, QueryOperator rightSide, HashSet<string> rightAliases) in plannedJoins)
            {
                QueryOperator currentRight = rightSide;

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
                        PredicatePushdown.DeriveTransitivePredicates(join.OnCondition, pendingPredicates);
                    }

                    // Predicate pushdown: push single-table WHERE predicates below the join.
                    if (pendingPredicates is not null && join.Type == JoinType.Inner)
                    {
                        currentRight = PredicatePushdown.PushPredicatesBelow(currentRight, rightAliases, pendingPredicates);
                        source = PredicatePushdown.PushPredicatesBelow(source, leftAliases, pendingPredicates);
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
                    if (JoinStrategySelector.ShouldUseMergeJoin(statement, join.OnCondition, source, currentRight)
                        && JoinStrategySelector.TryCreateMergeJoin(source, currentRight, join.Type, join.OnCondition,
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
                            long? leftRowCount = PlanShapeInspector.GetEstimatedRowCount(source);
                            long? rightRowCount = PlanShapeInspector.GetEstimatedRowCount(currentRight);

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
                        bool preferIndexNestedLoop = JoinStrategySelector.ShouldPreferIndexNestedLoop(statement, currentRight, join);

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
            // FTS rewrite: if any predicate is `col @@ <const>` and `col`
            // has a full-text index, replace the scan with a
            // FullTextSearchOperator and strip the matched predicate from
            // pendingPredicates so it doesn't also become a residual filter.
            source = FullTextSearchRewriter.MaybeRewriteForFullTextSearch(source, pendingPredicates);

            // No joins — push applicable predicates directly to the source.
            source = PredicatePushdown.PushPredicatesBelow(source, leftAliases, pendingPredicates);
        }

        // 2b. Inject source transforms (e.g. Float32SubqueryOperator for correlated subqueries)
        // after joins and predicate pushdown, before the remaining WHERE filter.
        if (sourceTransform is not null)
        {
            source = sourceTransform(source);
        }

        // 3. Apply remaining WHERE predicates that could not be pushed down.
        IReadOnlyList<LetBinding>? userLetBindings = statement.LetBindings;
        if (pendingPredicates is not null && pendingPredicates.Count > 0)
        {
            Expression remaining = PredicateUtilities.CombineWithAnd(pendingPredicates);
            source = new FilterOperator(source, remaining);
        }

        // 3a. LET-from-WHERE visibility (Phase 1). Predicates referencing LET
        // bindings stayed out of pushdown. Lift each referenced LET into a
        // RowEnricher / ModelInvocation rung between the source and a fresh
        // Filter, so the synthesised hidden column is on the row by the time
        // the predicate evaluates. Lifted bindings are replaced with pass-
        // through column references in `userLetBindings` — the downstream
        // projection still names them but the "evaluation" is just a column
        // read. Aggregate / window LET bodies are rejected here with a clear
        // diagnostic pointing at HAVING / QUALIFY.
        if (letReferencingPredicates is { Count: > 0 })
        {
            Expression letPredicate = PredicateUtilities.CombineWithAnd(letReferencingPredicates);
            (source, letPredicate, userLetBindings) = LiftLetBindingsForWhere(
                source, letPredicate, userLetBindings);
            source = new FilterOperator(source, letPredicate);
        }

        // 3b. GROUP BY / aggregation.
        // Desugar CROSS VALIDATE into a synthetic LET binding before any rewriting pass.
        // The fold expression is: CAST(FLOOR(hash_split(key, seed) * k) AS Int32)
        GroupByClause? groupBy = statement.GroupBy;
        if (statement.CrossValidate is CrossValidateClause cv)
        {
            LetBinding foldBinding = LetDesugarer.DesugarCrossValidate(cv);
            List<LetBinding> merged = userLetBindings is not null
                ? [foldBinding, .. userLetBindings]
                : [foldBinding];
            userLetBindings = merged;

            // When GROUP BY references the fold alias, we need the fold value to be
            // materialized as a column BEFORE GroupByOperator runs. We'll inject a
            // pre-GROUP BY ProjectOperator that computes the fold and passes all
            // source columns through (SELECT *, fold_expr AS fold). Then GROUP BY
            // references the materialized "fold" column directly.
            if (groupBy is not null && ProjectionPushdown.HasColumnReference(groupBy.Expressions, cv.OutputAlias))
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
        IReadOnlyList<LetBinding>? letBindings = LetDesugarer.DesugarDestructuredLetBindings(userLetBindings);
        bool hasGroupBy = groupBy is not null;
        bool hasAggregates = PredicateUtilities.HasAggregateFunction(statement.Columns, _functionRegistry)
            || LetDesugarer.HasLetAggregateFunction(letBindings, _functionRegistry);
        IReadOnlyList<SelectColumn> projectionColumns = statement.Columns;
        IReadOnlyList<AssertClause>? assertions = statement.Assertions;
        // Aggregate-rewritten ORDER BY clause when the query has GROUP BY /
        // aggregates; bare aggregate calls in ORDER BY are lifted into the
        // GroupBy's aggregate columns and rewritten as column references.
        // null when no rewrite happened — falls back to the original clause.
        OrderByClause? rewrittenOrderByClause = null;
        // Aggregate column names appended to the projection as hidden
        // passthroughs because ORDER BY references them but SELECT doesn't.
        // After the OrderByOperator runs, a final trim ProjectOperator drops
        // these columns so the user-visible output matches their SELECT list.
        // null/empty when no passthroughs were needed.
        List<string>? orderByAggregatePassthroughs = null;

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

                    if (!PredicateUtilities.ExpressionContainsAggregate(column.Expression, _functionRegistry))
                    {
                        inferred.Add(column.Expression);
                    }
                }

                if (letBindings is not null)
                {
                    foreach (LetBinding binding in letBindings)
                    {
                        if (binding.OutputAlias is not null
                            && !PredicateUtilities.ExpressionContainsAggregate(binding.Expression, _functionRegistry))
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

                Expression rewritten = AggregateRewriter.RewriteAggregateExpression(
                    column.Expression, _functionRegistry, aggregateColumns);
                rewrittenColumns.Add(new SelectColumn(rewritten, column.Alias));
            }

            // Rewrite aggregate expressions inside LET bindings.
            if (letBindings is not null)
            {
                List<LetBinding> rewrittenLetBindings = new(letBindings.Count);
                foreach (LetBinding binding in letBindings)
                {
                    Expression rewritten = AggregateRewriter.RewriteAggregateExpression(
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
                    Expression rewrittenPredicate = AggregateRewriter.RewriteAggregateExpression(
                        assertClause.Predicate, _functionRegistry, aggregateColumns);
                    Expression? rewrittenMessage = assertClause.Message is not null
                        ? AggregateRewriter.RewriteAggregateExpression(
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
                    Expression rewritten = AggregateRewriter.RewriteAggregateExpression(
                        item.Expression, _functionRegistry, aggregateColumns);
                    rewrittenOrderBy.Add(ReferenceEquals(rewritten, item.Expression)
                        ? item
                        : item with { Expression = rewritten });
                }
                rewrittenOrderByClause = new OrderByClause(rewrittenOrderBy);

                // ORDER BY may reference aggregate columns the SELECT list
                // doesn't emit (e.g. `SELECT category … ORDER BY COUNT(*)`,
                // or `SELECT … COUNT(*) AS c … ORDER BY COUNT(*)` where the
                // SELECT renamed it). The GroupByOperator emits those aggregate
                // columns by their synthesised output name; without a matching
                // entry in the projection, the column gets dropped before
                // ORDER BY runs and the column ref dangles. Append a synthetic
                // passthrough SelectColumn for each missing aggregate name so
                // it survives the projection. The trim Project after
                // ORDER BY drops them again so the user-visible schema matches
                // their SELECT list.
                AggregateRewriter.AppendOrderByAggregatePassthroughs(
                    rewrittenOrderBy, aggregateColumns, rewrittenColumns,
                    ref orderByAggregatePassthroughs);
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
                DatumActivity.Operators.Trace("GROUP BY without aggregates rewritten to streaming DISTINCT");
            }
            else
            {
                bool streamingSorted = SortAggregateAnalyzer.CanUseStreamingAggregate(source, groupByExpressions);

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
                    DatumActivity.Operators.Trace("GROUP BY uses streaming aggregation (sorted input)");
                }

                // Apply HAVING as a filter on the grouped output.
                if (statement.Having is not null)
                {
                    Expression havingRewritten = AggregateRewriter.RewriteAggregateExpression(
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
        bool hasWindowFunctions = WindowRewriter.HasWindowFunction(projectionColumns, _functionRegistry)
            || LetDesugarer.HasLetWindowFunction(letBindings);
        bool qualifyHasWindowFunctions = statement.Qualify is not null
            && WindowRewriter.ExpressionContainsWindowFunction(statement.Qualify);
        bool assertionsHaveWindowFunctions = WindowRewriter.HasAssertWindowFunction(assertions);
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

                Expression rewritten = WindowRewriter.RewriteWindowExpression(
                    column.Expression, _functionRegistry, windowColumns);
                windowRewrittenColumns.Add(new SelectColumn(rewritten, column.Alias));
            }

            // Rewrite window function calls inside LET binding expressions.
            if (letBindings is not null)
            {
                List<LetBinding> windowRewrittenLetBindings = new(letBindings.Count);
                foreach (LetBinding binding in letBindings)
                {
                    Expression rewritten = WindowRewriter.RewriteWindowExpression(
                        binding.Expression, _functionRegistry, windowColumns);
                    windowRewrittenLetBindings.Add(binding with { Expression = rewritten });
                }
                letBindings = windowRewrittenLetBindings;
            }

            // Rewrite any inline window function calls inside the QUALIFY expression
            // so they become column references to the same WindowOperator output.
            if (qualifyHasWindowFunctions)
            {
                qualifyExpression = WindowRewriter.RewriteWindowExpression(
                    qualifyExpression!, _functionRegistry, windowColumns);
            }

            // Rewrite window function calls inside ASSERT clause predicates and messages.
            if (assertionsHaveWindowFunctions && assertions is not null)
            {
                List<AssertClause> windowRewrittenAssertions = new(assertions.Count);
                foreach (AssertClause assertClause in assertions)
                {
                    Expression rewrittenPredicate = WindowRewriter.RewriteWindowExpression(
                        assertClause.Predicate, _functionRegistry, windowColumns);
                    Expression? rewrittenMessage = assertClause.Message is not null
                        ? WindowRewriter.RewriteWindowExpression(
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
        bool hasScanExpressions = ScanExpressionRewriter.HasScanExpression(projectionColumns)
            || ScanExpressionRewriter.HasLetScanExpression(letBindings);

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
                    ScanExpressionRewriter.RewriteScanExpression(column.Expression, scanColumns);
                    for (int i = 0; i < topScan.OutputAliases.Count; i++)
                    {
                        scanRewrittenColumns.Add(new SelectColumn(
                            new ColumnReference(null, topScan.OutputAliases[i]),
                            topScan.OutputAliases[i]));
                    }
                    continue;
                }

                Expression rewritten = ScanExpressionRewriter.RewriteScanExpression(column.Expression, scanColumns);
                scanRewrittenColumns.Add(new SelectColumn(rewritten, column.Alias));
            }

            // Rewrite SCAN expressions inside LET binding expressions.
            if (letBindings is not null)
            {
                List<LetBinding> scanRewrittenLetBindings = new(letBindings.Count);
                foreach (LetBinding binding in letBindings)
                {
                    Expression rewritten = ScanExpressionRewriter.RewriteScanExpression(binding.Expression, scanColumns);
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
            qualifyExpression = AliasResolver.ResolveSelectAliases(qualifyExpression, projectionColumns);
            qualifyExpression = AliasResolver.ResolveLetBindingReferences(qualifyExpression, letBindings);
            source = new FilterOperator(source, qualifyExpression);
        }

        // 3f. ASSERT — resolve predicate and message expressions against SELECT aliases
        // and LET binding names before they reach the ProjectOperator evaluator.
        if (assertions is not null)
        {
            List<AssertClause> resolvedAssertions = new(assertions.Count);
            foreach (AssertClause assertClause in assertions)
            {
                Expression resolvedPredicate = AliasResolver.ResolveSelectAliases(
                    assertClause.Predicate, projectionColumns);
                resolvedPredicate = AliasResolver.ResolveLetBindingReferences(resolvedPredicate, letBindings);
                Expression? resolvedMessage = assertClause.Message is not null
                    ? AliasResolver.ResolveLetBindingReferences(
                        AliasResolver.ResolveSelectAliases(assertClause.Message, projectionColumns),
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

        source = PivotApplier.ApplyPivotOrUnpivot(source, statement, _functionRegistry);

        // 4. Apply SELECT projection (with LET bindings for memoized evaluation).
        //
        // When SELECT * is used with JOINs, expand the wildcard into per-table
        // wildcards (e.g. "a.*", "b.*") in SQL-text order. This ensures the
        // ProjectOperator emits columns in the original FROM/JOIN declaration
        // order even when greedy join reordering has swapped the physical probe
        // and build sides.
        //
        // For single-source SELECT * where the source carries an explicit alias,
        // expand to `alias.*` with QualifyOutput=false. The AliasOperator wraps
        // the source's columns with qualified physical names (`alias.col`); without
        // this rewrite the wildcard passthrough leaks those qualified names into
        // the output, breaking outer-query references when the result is later
        // re-aliased (e.g. `WITH cte AS (SELECT * FROM t a) SELECT c.col FROM cte c`
        // would fail to resolve `c.col` because the CTE's output column is `a.col`).
        projectionColumns = SelectStarExpander.ExpandSelectStar(statement, projectionColumns);

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

        return SelectTrailingClauses.ApplyTrailingClauses(
            source, statement, rewrittenOrderByClause, orderByAggregatePassthroughs);
    }

    /// <summary>
    /// Plans a statement with scalar subquery rewriting. Uncorrelated subqueries are
    /// constant-folded at plan time. Correlated subqueries are rewritten to synthetic
    /// column references and injected as <see cref="Operators.ScalarSubqueryOperator"/>
    /// wrappers around the source operator.
    /// </summary>
    private async Task<QueryOperator> PlanCoreWithSubqueriesAsync(
        SelectStatement statement,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        // Determine if the statement contains any SubqueryExpressions.
        if (!SubqueryDetection.ContainsSubqueryExpression(statement))
        {
            return PlanCore(statement);
        }

        // Collect outer-scope table aliases for correlation detection.
        HashSet<string> outerAliases = new(StringComparer.OrdinalIgnoreCase);
        if (statement.From is not null)
        {
            SourceAliases.CollectSourceAliases(statement.From.Source, outerAliases);
        }

        if (statement.Joins is not null)
        {
            foreach (JoinClause join in statement.Joins)
            {
                SourceAliases.CollectSourceAliases(join.Source, outerAliases);
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

        // LET binding bodies — same shape as the SELECT-column rewrite. Must
        // run before the statement reconstruction below so the rebuilt
        // statement carries rewritten LET bindings into PlanCore.
        IReadOnlyList<LetBinding>? rewrittenLetBindings = statement.LetBindings;
        if (statement.LetBindings is not null)
        {
            List<LetBinding>? letList = null;
            for (int index = 0; index < statement.LetBindings.Count; index++)
            {
                LetBinding binding = statement.LetBindings[index];
                SubqueryRewriter.RewriteResult result = await SubqueryRewriter.RewriteAsync(
                    binding.Expression, outerAliases, this, context, _functionRegistry,
                    cancellationToken).ConfigureAwait(false);

                if (!ReferenceEquals(result.Expression, binding.Expression))
                {
                    letList ??= new List<LetBinding>(statement.LetBindings);
                    letList[index] = binding with { Expression = result.Expression };
                    allCorrelated.AddRange(result.CorrelatedSubqueries);
                    allDecorrelated.AddRange(result.DecorrelatedScalarJoins);
                }
            }
            if (letList is not null)
            {
                rewrittenLetBindings = letList;
            }
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

        // Reconstruct the statement with rewritten expressions. Use record-with
        // syntax so all fields not explicitly rewritten (LetBindings,
        // CrossValidate, GroupBy, Assertions, Pivot, OrderBy, etc.) are
        // preserved by reference. The previous positional invocation here
        // silently dropped LetBindings and CrossValidate because it stopped
        // at CommonTableExpressions.
        SelectStatement rewrittenStatement = statement with
        {
            Columns = rewrittenColumns is not null ? rewrittenColumns : statement.Columns,
            Joins = rewrittenJoins,
            Where = rewrittenWhere,
            Having = rewrittenHaving,
            LetBindings = rewrittenLetBindings,
        };

        // Build a source transform that injects ScalarSubqueryOperator wrappers,
        // decorrelated LEFT JOINs, and semi-join operators between the source
        // (Scan+Joins) and the rest of the pipeline (Filter/Project/etc.).
        // This ensures synthetic columns and semi-join filtering are applied
        // before any operator that references them.
        Func<QueryOperator, QueryOperator>? sourceTransform = null;
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
                    QueryOperator innerPlan = Plan(correlated.InnerQuery);
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

        // Collect ON-condition expressions from the out-of-band joins so projection
        // pushdown sees the outer-table column references they evaluate at runtime.
        // The rewritten statement's WHERE no longer contains the IN/EXISTS subquery
        // (rewriter consumed it), so without this list e.g. `customers.id` from
        // `WHERE EXISTS (SELECT 1 FROM orders WHERE orders.customer_id = customers.id)`
        // would be trimmed from the customers scan and the semi-join's key
        // evaluation would throw "Column 'customers.id' not found in row".
        //
        // The same trim happens to correlated scalar subqueries: the rewriter
        // replaces `(SELECT ... WHERE inner.x = outer.y)` with a synthetic column
        // reference, leaving the inner statement (with its `outer.y` ref) only
        // reachable via the CorrelatedSubquery descriptor. Wrap each inner
        // statement in a SubqueryExpression so ColumnReferenceCollector descends
        // into it and surfaces the outer-scope refs for projection pushdown.
        List<Expression>? extraReferences = null;
        if (allDecorrelated.Count > 0 || semiJoinResult.SemiJoins.Count > 0 || allCorrelated.Count > 0)
        {
            extraReferences = new List<Expression>(
                allDecorrelated.Count + semiJoinResult.SemiJoins.Count + allCorrelated.Count);
            foreach (SubqueryRewriter.DecorrelatedScalarJoin decorrelated in allDecorrelated)
            {
                if (decorrelated.OnCondition is not null)
                {
                    extraReferences.Add(decorrelated.OnCondition);
                }
            }
            foreach (SemiJoinRewriter.SemiJoinDescriptor semiJoin in semiJoinResult.SemiJoins)
            {
                if (semiJoin.OnCondition is not null)
                {
                    extraReferences.Add(semiJoin.OnCondition);
                }
            }
            foreach (SubqueryRewriter.CorrelatedSubquery correlated in allCorrelated)
            {
                extraReferences.Add(new SubqueryExpression(correlated.InnerQuery));
            }
        }

        // Plan the rewritten statement through the standard pipeline with the source transform.
        return PlanCore(rewrittenStatement, sourceTransform, externalCommonTableExpressionOperators: null, extraReferences);
    }








    /// <summary>
    /// Phase 1 of LET-from-WHERE visibility. When the residual WHERE predicate
    /// references one or more LET binding names, lift those bindings into rungs
    /// below the FilterOperator so the synthesised hidden columns are on the
    /// row when the predicate evaluates. The set of lifted bindings is
    /// transitively closed (a lifted binding that references another LET name
    /// drags that one along too).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pure-scalar LET bodies become <see cref="RowEnricherOperator"/> rungs.
    /// LET bodies that are exactly a <c>models.*</c> call become
    /// <see cref="ModelInvocationOperator"/> rungs. Mixed bodies (scalar
    /// expressions wrapping one or more model calls) extract the inner model
    /// calls into upstream MIO rungs via
    /// <see cref="ModelInvocationHoister.HoistModelCallsFromExpression"/>;
    /// the residual scalar then runs in a <see cref="RowEnricherOperator"/>.
    /// </para>
    /// <para>
    /// Aggregate- or window-derived LET bodies are rejected with a clear
    /// "use HAVING" / "use QUALIFY" diagnostic — those clauses run after
    /// WHERE so they cannot be staged below the Filter.
    /// </para>
    /// <para>
    /// Lifted bindings stay in the returned <see cref="LetBinding"/> list as
    /// pass-through references (their <c>Expression</c> becomes a
    /// <see cref="ColumnReference"/> to the synthetic column). This keeps the
    /// downstream projection-side LET handling intact — the binding is still
    /// "evaluated" inside the projection, but the evaluation is now a no-op
    /// column read because the value already lives on the row.
    /// </para>
    /// </remarks>
    private (QueryOperator Source, Expression Predicate, IReadOnlyList<LetBinding>? LetBindings)
        LiftLetBindingsForWhere(
            QueryOperator source,
            Expression predicate,
            IReadOnlyList<LetBinding>? letBindings)
    {
        if (letBindings is null || letBindings.Count == 0)
        {
            return (source, predicate, letBindings);
        }

        // Step 1: which LET names does WHERE reference directly?
        HashSet<string> allLetNames = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, LetBinding> bindingByName = new(StringComparer.OrdinalIgnoreCase);
        foreach (LetBinding b in letBindings)
        {
            allLetNames.Add(b.Name);
            bindingByName[b.Name] = b;
        }

        HashSet<string> liftedNames = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string? tableName, string columnName) in
            ColumnReferenceCollector.Collect(predicate))
        {
            if (tableName is null && allLetNames.Contains(columnName))
            {
                liftedNames.Add(columnName);
            }
        }

        if (liftedNames.Count == 0)
        {
            return (source, predicate, letBindings);
        }

        // Step 2: transitive closure. A lifted binding that mentions another
        // LET in its body forces that one to lift too.
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (string n in liftedNames.ToArray())
            {
                LetBinding b = bindingByName[n];
                foreach ((string? tableName, string columnName) in
                    ColumnReferenceCollector.Collect(b.Expression))
                {
                    if (tableName is null && allLetNames.Contains(columnName)
                        && liftedNames.Add(columnName))
                    {
                        changed = true;
                    }
                }
            }
        }

        // Step 3: validate. Aggregate / window bodies can't lift below WHERE.
        foreach (string n in liftedNames)
        {
            LetBinding b = bindingByName[n];
            if (PredicateUtilities.ExpressionContainsAggregate(b.Expression, _functionRegistry))
            {
                throw new InvalidOperationException(
                    $"LET binding '{n}' is referenced from WHERE but its body contains an " +
                    $"aggregate function. Aggregates are computed after GROUP BY — use " +
                    $"HAVING instead of WHERE to filter on aggregate results.");
            }
            if (WindowRewriter.ExpressionContainsWindowFunction(b.Expression))
            {
                throw new InvalidOperationException(
                    $"LET binding '{n}' is referenced from WHERE but its body contains a " +
                    $"window function. Window functions are computed after GROUP BY — use " +
                    $"QUALIFY instead of WHERE to filter on window results.");
            }
        }

        // Step 4: topo-order the lifted bindings. Inner deps first.
        Dictionary<string, Expression> hoists = new(StringComparer.OrdinalIgnoreCase);
        foreach (string n in liftedNames)
        {
            hoists[n] = bindingByName[n].Expression;
        }
        List<List<string>> levels = HoistDependencyOrdering.OrderByDependency(hoists);

        // Step 5: assign synthetic column names and build the staircase. One
        // rung per level: scalar bindings group into a single RowEnricher,
        // model bindings each get their own MIO.
        Dictionary<string, string> nameToSynth = new(StringComparer.OrdinalIgnoreCase);
        foreach (string n in liftedNames)
        {
            nameToSynth[n] = $"__let_{n}_pre";
        }

        foreach (List<string> level in levels)
        {
            List<RowEnrichment> enrichments = new();
            foreach (string n in level)
            {
                LetBinding b = bindingByName[n];
                Expression rewrittenBody = LetBindingLifter.ReplaceLetNameRefs(b.Expression, nameToSynth);

                if (rewrittenBody is FunctionCallExpression fn
                    && string.Equals(fn.SchemaName, ModelInvocationHoister.ModelSchema, StringComparison.OrdinalIgnoreCase))
                {
                    if (_catalog.Models is null)
                    {
                        throw new InvalidOperationException(
                            $"LET binding '{n}' calls a model but no ModelCatalog is configured.");
                    }
                    source = LetBindingLifter.BuildSingleMioForLiftedLet(source, fn, nameToSynth[n], _catalog.Models);
                }
                else
                {
                    // Mixed-body case: a scalar expression that contains one
                    // or more models.* calls. Extract them into upstream MIO
                    // rungs first; the residual is a pure-scalar expression
                    // that the Enricher can evaluate. Pure-scalar bodies
                    // (no model calls) pass through unchanged.
                    if (_catalog.Models is not null)
                    {
                        (source, rewrittenBody) =
                            ModelInvocationHoister.HoistModelCallsFromExpression(
                                source, rewrittenBody, _catalog.Models);
                    }
                    enrichments.Add(new RowEnrichment(nameToSynth[n], rewrittenBody));
                }
            }
            if (enrichments.Count > 0)
            {
                source = new RowEnricherOperator(source, enrichments);
            }
        }

        // Step 6: rewrite the WHERE predicate to use synthetic column names.
        Expression rewrittenPredicate = LetBindingLifter.ReplaceLetNameRefs(predicate, nameToSynth);

        // Step 7: lifted LETs become pass-through references in the bindings list
        // so projection-side handling continues to find them by name.
        List<LetBinding> updatedBindings = new(letBindings.Count);
        foreach (LetBinding b in letBindings)
        {
            updatedBindings.Add(liftedNames.Contains(b.Name)
                ? b with { Expression = new ColumnReference(TableName: null, ColumnName: nameToSynth[b.Name]) }
                : b);
        }

        return (source, rewrittenPredicate, updatedBindings);
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
