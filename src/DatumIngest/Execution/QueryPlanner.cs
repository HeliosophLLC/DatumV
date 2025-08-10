using System.Diagnostics.CodeAnalysis;
using DatumIngest.Catalog;
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
    /// Creates a query planner for the given table catalog and function registry.
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
        return query switch
        {
            SelectQueryExpression select => Plan(select.Statement),
            CompoundQueryExpression compound => PlanCompound(compound),
            _ => throw new InvalidOperationException($"Unexpected query expression type: {query.GetType().Name}"),
        };
    }

    /// <summary>
    /// Plans the given statement into an operator tree ready for execution.
    /// </summary>
    /// <param name="statement">The parsed SELECT statement.</param>
    /// <returns>The root operator of the execution plan.</returns>
    public IQueryOperator Plan(SelectStatement statement)
    {
        return PlanCore(statement, deferredColumns: null);
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
        return query switch
        {
            SelectQueryExpression select => await PlanAsync(select.Statement, cancellationToken).ConfigureAwait(false),
            CompoundQueryExpression compound => await PlanCompoundAsync(compound, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unexpected query expression type: {query.GetType().Name}"),
        };
    }

    /// <summary>
    /// Plans the given statement with cost-based late materialization of expensive columns.
    /// When a source has expensive columns (e.g. <c>file_bytes</c> in ZIP) that are only
    /// referenced in SELECT (not in JOIN ON or WHERE), those columns are excluded from the
    /// scan and fetched only for surviving rows via <see cref="IKeyedTableProvider"/>.
    /// </summary>
    /// <param name="statement">The parsed SELECT statement.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The root operator of the execution plan.</returns>
    public async Task<IQueryOperator> PlanAsync(
        SelectStatement statement,
        CancellationToken cancellationToken)
    {
        Dictionary<string, DeferredTableColumns>? deferredColumns =
            await AnalyzeDeferredColumnsAsync(statement, cancellationToken)
                .ConfigureAwait(false);

        IQueryOperator plan = PlanCore(statement, deferredColumns);

        return plan;
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
        return query switch
        {
            SelectQueryExpression select =>
                await PlanWithSubqueriesAsync(select.Statement, context, cancellationToken).ConfigureAwait(false),
            CompoundQueryExpression compound =>
                await PlanCompoundWithSubqueriesAsync(compound, context, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unexpected query expression type: {query.GetType().Name}"),
        };
    }

    /// <summary>
    /// Plans the given statement with cost-based late materialization and scalar subquery
    /// rewriting. Requires an <see cref="ExecutionContext"/> to execute uncorrelated subqueries
    /// at plan time (constant folding).
    /// </summary>
    /// <param name="statement">The parsed SELECT statement.</param>
    /// <param name="context">Execution context for running uncorrelated scalar subqueries at plan time.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The root operator of the execution plan.</returns>
    public async Task<IQueryOperator> PlanWithSubqueriesAsync(
        SelectStatement statement,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        Dictionary<string, DeferredTableColumns>? deferredColumns =
            await AnalyzeDeferredColumnsAsync(statement, cancellationToken)
                .ConfigureAwait(false);

        IQueryOperator plan = await PlanCoreWithSubqueriesAsync(
            statement, deferredColumns, context, cancellationToken)
            .ConfigureAwait(false);

        return plan;
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
    /// Core planning logic shared by <see cref="Plan(SelectStatement)"/> and <see cref="PlanAsync(SelectStatement, CancellationToken)"/>.
    /// When <paramref name="deferredColumns"/> is provided, expensive columns are excluded
    /// from scans and a <see cref="LateMaterializationOperator"/> is injected before projection.
    /// </summary>
    /// <param name="statement">The parsed SELECT statement.</param>
    /// <param name="deferredColumns">Columns to defer for late materialization, or <see langword="null"/>.</param>
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
        IReadOnlyDictionary<string, DeferredTableColumns>? deferredColumns,
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
            ? PlanSource(statement.From.Source, allReferencedColumns, deferredColumns, hasJoins, commonTableExpressionOperators)
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
                IQueryOperator rightSide = PlanSource(join.Source, allReferencedColumns, deferredColumns, hasJoins, commonTableExpressionOperators);
                HashSet<string> rightAliases = new(StringComparer.OrdinalIgnoreCase);
                CollectSourceAliases(join.Source, rightAliases);
                plannedJoins.Add((join, rightSide, rightAliases));
            }

            // Greedy join reordering: place the largest table on the probe
            // (streaming) side so LIMIT can short-circuit earlier, and build
            // the smaller tables into hash tables. Only applied when every
            // join is a non-lateral INNER join and all sources have estimated
            // row counts. This is a heuristic — the roadmap CBO will replace it.
            if (TryReorderJoins(source, leftAliases, plannedJoins,
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

                    source = new JoinOperator(source, currentRight, join.Type, join.OnCondition);
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

        // 2b. Inject source transforms (e.g. ScalarSubqueryOperator for correlated subqueries)
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

        // 3b. Late materialization: fetch expensive output-only columns for surviving rows.
        // When GROUP BY is present, late materialization must happen before aggregation
        // so the aggregate functions see fully materialized column values.
        if (deferredColumns is not null)
        {
            foreach (KeyValuePair<string, DeferredTableColumns> entry in deferredColumns)
            {
                source = new LateMaterializationOperator(
                    source,
                    entry.Value.Descriptor,
                    entry.Value.KeyColumn,
                    entry.Value.ColumnNames,
                    entry.Key);
            }
        }

        // 3c. GROUP BY / aggregation.
        bool hasGroupBy = statement.GroupBy is not null;
        bool hasAggregates = HasAggregateFunction(statement.Columns, _functionRegistry)
            || HasLetAggregateFunction(statement.LetBindings, _functionRegistry);
        IReadOnlyList<SelectColumn> projectionColumns = statement.Columns;
        IReadOnlyList<LetBinding>? letBindings = statement.LetBindings;

        if (hasGroupBy || hasAggregates)
        {
            IReadOnlyList<Expression> groupByExpressions =
                statement.GroupBy?.Expressions ?? Array.Empty<Expression>();

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

            source = new GroupByOperator(source, groupByExpressions, aggregateColumns);

            // Apply HAVING as a filter on the grouped output.
            if (statement.Having is not null)
            {
                Expression havingRewritten = RewriteAggregateExpression(
                    statement.Having, _functionRegistry, aggregateColumns);
                source = new FilterOperator(source, havingRewritten);
            }

            projectionColumns = rewrittenColumns;
        }

        // 3d. Window functions — insert WindowOperator after GROUP BY
        // (which may reference aggregate output columns) but before projection.
        // QUALIFY may also contain inline window function calls that must be
        // lifted into the same WindowOperator.
        bool hasWindowFunctions = HasWindowFunction(projectionColumns, _functionRegistry)
            || HasLetWindowFunction(letBindings);
        bool qualifyHasWindowFunctions = statement.Qualify is not null
            && ExpressionContainsWindowFunction(statement.Qualify);
        Expression? qualifyExpression = statement.Qualify;

        if (hasWindowFunctions || qualifyHasWindowFunctions)
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

            source = new WindowOperator(source, windowColumns);
            projectionColumns = windowRewrittenColumns;
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

        // 3f. PIVOT — reshape wide data by rotating a column's distinct values into columns.
        // PIVOT replaces the SELECT projection entirely; the projectionColumns are discarded.
        if (statement.Pivot is not null)
        {
            PivotClause pivot = statement.Pivot;

            if (pivot.Aggregates.Count == 0)
            {
                throw new InvalidOperationException(
                    "PIVOT requires at least one aggregate function.");
            }

            // Resolve aggregate functions from the registry.
            List<AggregateColumn> pivotAggregates = new(pivot.Aggregates.Count);

            foreach (FunctionCallExpression aggregateCall in pivot.Aggregates)
            {
                IAggregateFunction? function =
                    _functionRegistry.TryGetAggregate(aggregateCall.FunctionName)
                    ?? throw new InvalidOperationException(
                        $"PIVOT aggregate '{aggregateCall.FunctionName}' is not a known aggregate function.");

                bool isCountStar = IsCountStarCall(aggregateCall);
                IReadOnlyList<Expression> arguments = isCountStar
                    ? Array.Empty<Expression>()
                    : aggregateCall.Arguments;

                pivotAggregates.Add(new AggregateColumn(
                    function, arguments, QueryExplainer.FormatExpression(aggregateCall), isCountStar));
            }

            // Pre-evaluate the explicit value list (literals only) into DataValues at plan time.
            IReadOnlyList<DataValue>? explicitValues = null;

            if (pivot.ValueList is not null)
            {
                DataValue[] values = new DataValue[pivot.ValueList.Count];

                for (int valueIndex = 0; valueIndex < pivot.ValueList.Count; valueIndex++)
                {
                    values[valueIndex] = EvaluateLiteralForPivot(pivot.ValueList[valueIndex]);
                }

                explicitValues = values;
            }

            source = new PivotOperator(source, pivotAggregates, pivot.PivotColumn, explicitValues);

            // Skip the regular SELECT projection — PIVOT defines the output schema.
            goto afterProjection;
        }

        // 3g. UNPIVOT — reshape wide rows into narrow rows by rotating columns into (name, value) pairs.
        // UNPIVOT also replaces the SELECT projection entirely.
        if (statement.Unpivot is not null)
        {
            UnpivotClause unpivot = statement.Unpivot;

            if (unpivot.SourceColumns.Count == 0)
            {
                throw new InvalidOperationException(
                    "UNPIVOT requires at least one source column in the IN (...) list.");
            }

            string[] sourceColumnNames = new string[unpivot.SourceColumns.Count];

            for (int colIndex = 0; colIndex < unpivot.SourceColumns.Count; colIndex++)
            {
                sourceColumnNames[colIndex] = unpivot.SourceColumns[colIndex].ColumnName;
            }

            source = new UnpivotOperator(
                source, unpivot.ValueColumnName, unpivot.NameColumnName, sourceColumnNames, unpivot.IncludeNulls);

            // Skip the regular SELECT projection — UNPIVOT defines the output schema.
            goto afterProjection;
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
            && projectionColumns[0] is SelectAllColumns)
        {
            List<SelectColumn> expanded = new();

            if (statement.From is not null)
            {
                string? fromAlias = GetSourceAlias(statement.From.Source);
                if (fromAlias is not null)
                {
                    expanded.Add(new SelectTableColumns(fromAlias));
                }
            }

            foreach (JoinClause join in statement.Joins)
            {
                string? joinAlias = GetSourceAlias(join.Source);
                if (joinAlias is not null)
                {
                    expanded.Add(new SelectTableColumns(joinAlias));
                }
            }

            if (expanded.Count > 0)
            {
                projectionColumns = expanded;
            }
        }

        {
        bool hasStarOnly = projectionColumns.Count == 1
            && projectionColumns[0] is SelectAllColumns
            && letBindings is null;
        if (!hasStarOnly)
        {
            source = new ProjectOperator(source, projectionColumns, letBindings);
        }
        }

        afterProjection:

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

        // 6. Apply ORDER BY — use index scan when a sorted index covers the sort column.
        if (statement.OrderBy is not null)
        {
            if (!TryReplaceWithIndexScan(ref source, statement.OrderBy))
            {
                int? topNRows = statement.Limit is not null
                    ? statement.Limit.Value + (statement.Offset ?? 0)
                    : null;

                source = new OrderByOperator(source, statement.OrderBy.Items, topNRows);
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
        IReadOnlyDictionary<string, DeferredTableColumns>? deferredColumns,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        // Determine if the statement contains any SubqueryExpressions.
        if (!ContainsSubqueryExpression(statement))
        {
            return PlanCore(statement, deferredColumns);
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
        return PlanCore(rewrittenStatement, deferredColumns, sourceTransform);
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

        // Find the source with the largest estimated row count — it becomes the probe.
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

        // If the largest source is already the FROM, no reordering needed.
        if (largestIndex == 0)
        {
            ExecutionTracer.Write($"JOIN REORDER  skipped: FROM is already largest ({GetOperatorName(fromOperator)} rows={largestCount:N0})");
            return false;
        }

        ExecutionTracer.Write($"JOIN REORDER  new probe (FROM) = {GetOperatorName(largestIndex == 0 ? fromOperator : plannedJoins[largestIndex - 1].Operator)}  rows={largestCount:N0}");

        // Build the pool of remaining sources to schedule.
        // Each entry: (Operator, Aliases, RowCount, JoinClause or null for the original FROM).
        List<(IQueryOperator Operator, HashSet<string> Aliases, long RowCount, JoinClause? Join)> remaining = new(totalSources - 1);

        // The original FROM becomes a joinable source — it keeps the ON condition
        // from the join that previously connected the new probe to the tree.
        // We'll assign ON conditions during the greedy scheduling below.
        remaining.Add((fromOperator, fromAliases, rowCounts[0]!.Value, null));

        for (int index = 0; index < plannedJoins.Count; index++)
        {
            if (index + 1 == largestIndex)
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

        // Set up the new FROM from the largest source.
        IQueryOperator newFrom;
        HashSet<string> joinedAliases;

        if (largestIndex == 0)
        {
            newFrom = fromOperator;
            joinedAliases = new HashSet<string>(fromAliases, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            int joinIndex = largestIndex - 1;
            newFrom = plannedJoins[joinIndex].Operator;
            joinedAliases = new HashSet<string>(plannedJoins[joinIndex].Aliases, StringComparer.OrdinalIgnoreCase);
        }

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
            largestIndex == 0 ? fromAliases : plannedJoins[largestIndex - 1].Aliases,
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
                return scan.Descriptor.Name;
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
                    return scan.EstimatedRowCount;
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
            scan.Descriptor,
            scan.RequiredColumns,
            columnIndex,
            scan.SourceIndex.Chunks,
            descending);

        // Replace the ScanOperator in the tree with the IndexScanOperator.
        source = ReplaceScanOperator(source, scan, indexScan);
        return true;
    }

    /// <summary>
    /// Finds the <see cref="ScanOperator"/> in a simple operator chain
    /// (ScanOperator, possibly wrapped in AliasOperator and/or FilterOperator).
    /// Returns <c>null</c> if the tree shape is too complex for index scan substitution.
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
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// Replaces the target <see cref="ScanOperator"/> in the operator tree with the given
    /// <see cref="IndexScanOperator"/>, preserving any wrapping operators (alias, filter).
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
            _ => root,
        };
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

    /// <summary>
    /// Evaluates a PIVOT <c>IN</c>-list expression to a <see cref="DataValue"/> at plan time.
    /// Only literal expressions are supported; non-literal expressions cause
    /// <see cref="InvalidOperationException"/> because the value list must be a closed
    /// set of constants known before execution begins.
    /// </summary>
    private static DataValue EvaluateLiteralForPivot(Expression expression)
    {
        if (expression is not LiteralExpression literal)
        {
            throw new InvalidOperationException(
                "PIVOT IN (…) values must be literal constants (strings or numbers). " +
                $"Expression '{QueryExplainer.FormatExpression(expression)}' is not a literal.");
        }

        if (literal.Value is null)
        {
            return DataValue.Null(DataKind.Scalar);
        }

        return literal.Value switch
        {
            DataValue dataValue => dataValue,
            int intValue => DataValue.FromScalar(intValue),
            long longValue => DataValue.FromScalar(longValue),
            float floatValue => DataValue.FromScalar(floatValue),
            double doubleValue => DataValue.FromScalar((float)doubleValue),
            string stringValue => DataValue.FromString(stringValue),
            bool boolValue => DataValue.FromBoolean(boolValue),
            _ => throw new InvalidOperationException(
                $"Unsupported PIVOT value literal type: {literal.Value.GetType().Name}."),
        };
    }

    private IQueryOperator PlanSource(
        TableSource source,
        HashSet<(string? TableName, string ColumnName)> allReferencedColumns,
        IReadOnlyDictionary<string, DeferredTableColumns>? deferredColumns,
        bool hasJoins,
        IReadOnlyDictionary<string, CommonTableExpressionOperator>? commonTableExpressionOperators = null)
    {
        return source switch
        {
            TableReference tableRef => PlanTableReference(tableRef, allReferencedColumns, deferredColumns, hasJoins, commonTableExpressionOperators),
            SubquerySource subquery => PlanSubquery(subquery),
            FunctionSource functionSource => PlanFunctionSource(functionSource, hasJoins),
            _ => throw new InvalidOperationException(
                $"Unsupported table source type: {source.GetType().Name}."),
        };
    }

    private IQueryOperator PlanTableReference(
        TableReference tableRef,
        HashSet<(string? TableName, string ColumnName)> allReferencedColumns,
        IReadOnlyDictionary<string, DeferredTableColumns>? deferredColumns,
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

        TableDescriptor descriptor = _catalog.Resolve(tableRef.Name);

        // Projection pushdown: compute required columns for this table's alias.
        string effectiveAlias = tableRef.Alias ?? tableRef.Name;
        IReadOnlySet<string>? requiredColumns =
            ComputeRequiredColumns(effectiveAlias, allReferencedColumns);

        // Late materialization: exclude deferred columns from the scan so the
        // provider does not materialize expensive data for every row.
        if (deferredColumns is not null &&
            deferredColumns.TryGetValue(effectiveAlias, out DeferredTableColumns? deferred))
        {
            if (requiredColumns is not null)
            {
                HashSet<string> filtered = new(requiredColumns, StringComparer.OrdinalIgnoreCase);
                foreach (string column in deferred.ColumnNames)
                {
                    filtered.Remove(column);
                }

                requiredColumns = filtered;
            }
        }

        IQueryOperator scanOperator = new ScanOperator(descriptor, requiredColumns);

        // Populate estimated row count from provider capabilities for cost annotations.
        // GetCapabilitiesAsync returns Task.FromResult for most providers.
        ITableProvider provider = _catalog.CreateProvider(descriptor);
        ProviderCapabilities capabilities = provider
            .GetCapabilitiesAsync(descriptor, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        ((ScanOperator)scanOperator).EstimatedRowCount = capabilities.EstimatedRowCount;

        // Override row count and attach per-column statistics from manifest if available.
        if (_catalog.TryGetManifest(descriptor.Name, out Manifest.QueryResultsManifest? manifest) && manifest is not null)
        {
            // Manifest row count is authoritative — it comes from a full-data scan
            // and is available even for providers that cannot report row counts.
            ((ScanOperator)scanOperator).EstimatedRowCount = manifest.RowCount;

            // Build column-name → FeatureManifest lookup for selectivity estimation.
            Dictionary<string, Manifest.FeatureManifest> columnStatistics = new(StringComparer.OrdinalIgnoreCase);
            foreach (Manifest.FeatureManifest feature in manifest.Features)
            {
                columnStatistics[feature.Name] = feature;
            }

            ((ScanOperator)scanOperator).ColumnStatistics = columnStatistics;
        }

        // Attach source index for chunk-based pruning if one is registered.
        if (_catalog.TryGetIndex(descriptor.Name, out Indexing.SourceIndex? sourceIndex))
        {
            ((ScanOperator)scanOperator).SetSourceIndex(sourceIndex!);
        }

        // Apply TABLESAMPLE row/chunk sampling if the table reference includes a sampling clause.
        if (tableRef.Tablesample is TablesampleClause tablesampleClause)
        {
            double percentage = EvaluateConstantDouble(tablesampleClause.Percentage);
            int? seed = tablesampleClause.Seed is not null
                ? (int)EvaluateConstantDouble(tablesampleClause.Seed)
                : null;

            scanOperator = new SampleScanOperator(scanOperator, tablesampleClause.Method, percentage, seed);
        }

        // Wrap column names with the alias prefix. When the query involves JOINs,
        // unaliased tables are implicitly aliased with their table name to prevent
        // column name collisions in the combined row schema.
        if (tableRef.Alias is not null || hasJoins)
        {
            scanOperator = new AliasOperator(scanOperator, tableRef.Alias ?? tableRef.Name);
        }

        return scanOperator;
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
    /// Analyzes all table sources to find expensive columns that are referenced only
    /// in the SELECT projection (not in WHERE, JOIN ON, or ORDER BY). These columns
    /// can be deferred and fetched via <see cref="IKeyedTableProvider"/> after joins and
    /// filters have eliminated non-matching rows.
    /// </summary>
    private async Task<Dictionary<string, DeferredTableColumns>?> AnalyzeDeferredColumnsAsync(
        SelectStatement statement,
        CancellationToken cancellationToken)
    {
        HashSet<(string? TableName, string ColumnName)> allReferencedColumns =
            CollectAllReferencedColumns(statement);

        // SELECT * prevents determining which columns are output-only.
        if (allReferencedColumns.Count == 0)
        {
            return null;
        }

        HashSet<(string? TableName, string ColumnName)> pipelineColumns =
            CollectPipelineColumns(statement);

        Dictionary<string, DeferredTableColumns>? result = null;

        // Analyze FROM source.
        if (statement.From is not null)
        {
            AnalyzeTableSource(
                statement.From.Source, allReferencedColumns, pipelineColumns,
                cancellationToken, ref result);
        }

        // Analyze JOIN sources.
        if (statement.Joins is not null)
        {
            foreach (JoinClause join in statement.Joins)
            {
                AnalyzeTableSource(
                    join.Source, allReferencedColumns, pipelineColumns,
                    cancellationToken, ref result);
            }
        }

        return result;
    }

    /// <summary>
    /// Checks a single table source for deferrable expensive columns and adds
    /// entries to the result dictionary if applicable.
    /// </summary>
    private void AnalyzeTableSource(
        TableSource source,
        HashSet<(string? TableName, string ColumnName)> allReferencedColumns,
        HashSet<(string? TableName, string ColumnName)> pipelineColumns,
        CancellationToken cancellationToken,
        ref Dictionary<string, DeferredTableColumns>? result)
    {
        if (source is not TableReference tableRef)
        {
            return;
        }

        if (!_catalog.TryResolve(tableRef.Name, out TableDescriptor? descriptor) || descriptor is null)
        {
            return;
        }

        ITableProvider provider = _catalog.CreateProvider(descriptor);
        if (provider is not IKeyedTableProvider)
        {
            return;
        }

        // GetCapabilitiesAsync returns Task.FromResult for most providers
        // (ZIP, JSON, CSV, HDF5). Parquet opens a file but is fast.
        ProviderCapabilities capabilities =
            provider.GetCapabilitiesAsync(descriptor, cancellationToken)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();

        if (capabilities.KeyColumn is null || capabilities.ColumnCosts.Count == 0)
        {
            return;
        }

        string effectiveAlias = tableRef.Alias ?? tableRef.Name;

        // Find expensive columns referenced only in SELECT/output (not in pipeline).
        IReadOnlySet<string>? allRequired =
            ComputeRequiredColumns(effectiveAlias, allReferencedColumns);
        IReadOnlySet<string>? pipelineRequired =
            ComputeRequiredColumns(effectiveAlias, pipelineColumns);

        if (allRequired is null)
        {
            return;
        }

        HashSet<string> deferrable = new(StringComparer.OrdinalIgnoreCase);

        foreach (string columnName in allRequired)
        {
            if (capabilities.ColumnCosts.TryGetValue(columnName, out ColumnCost cost) &&
                cost == ColumnCost.Expensive)
            {
                // Column is expensive. Check it's not needed by pipeline operators.
                bool neededInPipeline = pipelineRequired is not null &&
                    pipelineRequired.Contains(columnName);

                if (!neededInPipeline)
                {
                    deferrable.Add(columnName);
                }
            }
        }

        if (deferrable.Count == 0)
        {
            return;
        }

        // Verify the key column is available in pipeline rows (needed for lookup).
        bool keyInPipeline = pipelineRequired is not null &&
            pipelineRequired.Contains(capabilities.KeyColumn);
        bool keyInAll = allRequired.Contains(capabilities.KeyColumn);

        if (!keyInPipeline && !keyInAll)
        {
            return;
        }

        result ??= new Dictionary<string, DeferredTableColumns>(StringComparer.OrdinalIgnoreCase);
        result[effectiveAlias] = new DeferredTableColumns(
            descriptor, capabilities.KeyColumn, deferrable);
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
                IQueryOperator anchorPlan = PlanCore(
                    commonTableExpression.Query,
                    deferredColumns: null,
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
                            deferredColumns: null,
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

            IQueryOperator innerPlan = PlanCore(
                commonTableExpression.Query,
                deferredColumns: null,
                externalCommonTableExpressionOperators: operators);

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
