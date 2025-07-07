using Axon.QueryEngine.Catalog;
using Axon.QueryEngine.Execution.Operators;
using Axon.QueryEngine.Functions;
using Axon.QueryEngine.Model;
using Axon.QueryEngine.Parsing.Ast;

namespace Axon.QueryEngine.Execution;

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
    /// Plans the given statement into an operator tree ready for execution.
    /// </summary>
    /// <param name="statement">The parsed SELECT statement.</param>
    /// <returns>The root operator of the execution plan.</returns>
    public IQueryOperator Plan(SelectStatement statement)
    {
        return PlanCore(statement, deferredColumns: null);
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
    /// Core planning logic shared by <see cref="Plan"/> and <see cref="PlanAsync"/>.
    /// When <paramref name="deferredColumns"/> is provided, expensive columns are excluded
    /// from scans and a <see cref="LateMaterializationOperator"/> is injected before projection.
    /// </summary>
    private IQueryOperator PlanCore(
        SelectStatement statement,
        IReadOnlyDictionary<string, DeferredTableColumns>? deferredColumns)
    {
        // Compute the set of all referenced columns for projection pushdown.
        HashSet<(string? TableName, string ColumnName)> allReferencedColumns =
            CollectAllReferencedColumns(statement);

        // 1. Build the source operator (FROM clause) with projection pushdown.
        IQueryOperator source = PlanSource(statement.From.Source, allReferencedColumns, deferredColumns);

        // Track which table aliases are available on the current (left) side.
        HashSet<string> leftAliases = new(StringComparer.OrdinalIgnoreCase);
        CollectSourceAliases(statement.From.Source, leftAliases);

        // 2. Apply JOINs with predicate pushdown.
        List<Expression>? pendingPredicates = null;

        if (statement.Where is not null)
        {
            pendingPredicates = new List<Expression>();
            FlattenAnd(statement.Where, pendingPredicates);
        }

        if (statement.Joins is not null)
        {
            foreach (JoinClause join in statement.Joins)
            {
                IQueryOperator rightSide = PlanSource(join.Source, allReferencedColumns, deferredColumns);

                HashSet<string> rightAliases = new(StringComparer.OrdinalIgnoreCase);
                CollectSourceAliases(join.Source, rightAliases);

                // Predicate pushdown: push single-table WHERE predicates below the join.
                if (pendingPredicates is not null && join.Type == JoinType.Inner)
                {
                    rightSide = PushPredicatesBelow(rightSide, rightAliases, pendingPredicates);
                    source = PushPredicatesBelow(source, leftAliases, pendingPredicates);
                }

                source = new JoinOperator(source, rightSide, join.Type, join.OnCondition);

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

        // 3. Apply remaining WHERE predicates that could not be pushed down.
        if (pendingPredicates is not null && pendingPredicates.Count > 0)
        {
            Expression remaining = CombineWithAnd(pendingPredicates);
            source = new FilterOperator(source, remaining);
        }

        // 3b. Late materialization: fetch expensive output-only columns for surviving rows.
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

        // 4. Apply SELECT projection.
        bool hasStarOnly = statement.Columns.Count == 1 && statement.Columns[0] is SelectAllColumns;
        if (!hasStarOnly)
        {
            source = new ProjectOperator(source, statement.Columns);
        }

        // 5. Apply ORDER BY.
        if (statement.OrderBy is not null)
        {
            source = new OrderByOperator(source, statement.OrderBy.Items);
        }

        // 6. Apply LIMIT/OFFSET.
        if (statement.Limit is not null)
        {
            source = new LimitOperator(source, statement.Limit.Value, statement.Offset ?? 0);
        }

        return source;
    }

    /// <summary>
    /// Pushes predicates that reference only the given set of aliases below the
    /// current operator as filter nodes. Pushed predicates are removed from the list.
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
            }
        }

        return result;
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

    private IQueryOperator PlanSource(
        TableSource source,
        HashSet<(string? TableName, string ColumnName)> allReferencedColumns,
        IReadOnlyDictionary<string, DeferredTableColumns>? deferredColumns)
    {
        return source switch
        {
            TableReference tableRef => PlanTableReference(tableRef, allReferencedColumns, deferredColumns),
            SubquerySource subquery => PlanSubquery(subquery),
            FunctionSource functionSource => PlanFunctionSource(functionSource),
            _ => throw new InvalidOperationException(
                $"Unsupported table source type: {source.GetType().Name}."),
        };
    }

    private IQueryOperator PlanTableReference(
        TableReference tableRef,
        HashSet<(string? TableName, string ColumnName)> allReferencedColumns,
        IReadOnlyDictionary<string, DeferredTableColumns>? deferredColumns)
    {
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

        // If the table has an alias, wrap column names.
        if (tableRef.Alias is not null)
        {
            scanOperator = new AliasOperator(scanOperator, tableRef.Alias);
        }

        return scanOperator;
    }

    private IQueryOperator PlanSubquery(SubquerySource subquery)
    {
        IQueryOperator innerPlan = Plan(subquery.Query);
        return new SubqueryOperator(innerPlan, subquery.Alias);
    }

    private IQueryOperator PlanFunctionSource(FunctionSource functionSource)
    {
        ITableValuedFunction? function = _functionRegistry.TryGetTableValued(functionSource.FunctionName);

        if (function is null)
        {
            throw new InvalidOperationException(
                $"Unknown table-valued function: '{functionSource.FunctionName}'.");
        }

        IQueryOperator sourceOperator = new FunctionSourceOperator(function, functionSource.Arguments);

        if (functionSource.Alias is not null)
        {
            sourceOperator = new AliasOperator(sourceOperator, functionSource.Alias);
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
        AnalyzeTableSource(
            statement.From.Source, allReferencedColumns, pipelineColumns,
            cancellationToken, ref result);

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
