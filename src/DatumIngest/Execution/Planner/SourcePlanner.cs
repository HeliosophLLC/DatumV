using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Planner;

/// <summary>
/// Builds the leaf source operators for a <see cref="SelectStatement"/>'s FROM
/// clause and every JOIN's right side: scans (with TABLESAMPLE wrapping), subquery
/// operators, table-valued function operators, and CTE references.
/// </summary>
/// <remarks>
/// <para>
/// Held as a per-<see cref="QueryPlanner"/> instance because it needs the same
/// catalog + function-registry dependencies as the planner itself, plus a
/// recursive <c>planStatement</c> delegate used by <see cref="PlanSubquery"/> to
/// plan the inner SELECT of a subquery source. The delegate breaks the otherwise
/// circular planner ↔ source-planner reference at construction time.
/// </para>
/// <para>
/// CTE resolution: when a <see cref="TableReference"/>'s name matches an entry in
/// the optional <c>commonTableExpressionOperators</c> dictionary the planner
/// supplies, the call returns the shared CTE operator (wrapped in an
/// <see cref="AliasOperator"/> when joins are present or an explicit alias was
/// written), skipping the table-catalog lookup entirely.
/// </para>
/// </remarks>
internal sealed class SourcePlanner
{
    private readonly TableCatalog _catalog;
    private readonly FunctionRegistry _functionRegistry;
    private readonly Func<SelectStatement, QueryOperator> _planStatement;

    public SourcePlanner(
        TableCatalog catalog,
        FunctionRegistry functionRegistry,
        Func<SelectStatement, QueryOperator> planStatement)
    {
        _catalog = catalog;
        _functionRegistry = functionRegistry;
        _planStatement = planStatement;
    }

    /// <summary>
    /// Dispatches to the per-source-shape planner: <see cref="TableReference"/>,
    /// <see cref="SubquerySource"/>, or <see cref="FunctionSource"/>. Throws on
    /// any unrecognised shape.
    /// </summary>
    public QueryOperator PlanSource(
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

    /// <summary>
    /// Plans a <see cref="TableReference"/>: resolves CTE references first, otherwise
    /// builds a <see cref="ScanOperator"/> with projection pushdown applied and
    /// statistics threaded from the provider's manifest. Wraps in a TABLESAMPLE
    /// operator (Bernoulli / System / Stratified / Balanced) when the reference
    /// declares one, and finally in an <see cref="AliasOperator"/> when an alias was
    /// written or the parent query has joins.
    /// </summary>
    public QueryOperator PlanTableReference(
        TableReference tableRef,
        HashSet<(string? TableName, string ColumnName)> allReferencedColumns,
        bool hasJoins,
        IReadOnlyDictionary<string, CommonTableExpressionOperator>? commonTableExpressionOperators = null)
    {
        // CTE reference: return the shared CTE operator wrapped with an alias.
        if (commonTableExpressionOperators is not null &&
            commonTableExpressionOperators.TryGetValue(tableRef.Name, out CommonTableExpressionOperator? commonTableExpressionOperator))
        {
            QueryOperator cteSource = commonTableExpressionOperator;
            if (tableRef.Alias is not null || hasJoins)
            {
                cteSource = new AliasOperator(cteSource, tableRef.Alias ?? tableRef.Name);
            }

            return cteSource;
        }

        // Resolve the table reference: explicit schema lands in exactly that schema;
        // unqualified walks the session search_path and throws SchemaResolutionException
        // with a helpful message when no schema on the path contains the table.
        SchemaResolver resolver = new(_catalog, _catalog.SearchPath);
        QualifiedName qn = resolver.Resolve(tableRef.SchemaName, tableRef.Name);
        ITableProvider provider = _catalog[qn.ToString()];

        // Projection pushdown: compute required columns for this table's alias.
        string effectiveAlias = tableRef.Alias ?? tableRef.Name;
        IReadOnlySet<string>? requiredColumns =
            ProjectionPushdown.ComputeRequiredColumns(effectiveAlias, allReferencedColumns);

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

        QueryOperator outOperator = scanOperator;

        // Apply TABLESAMPLE row/chunk sampling if the table reference includes a sampling clause.
        if (tableRef.Tablesample is TablesampleClause tablesampleClause)
        {
            double argument = LiteralFolding.EvaluateConstantDouble(tablesampleClause.Percentage);
            int? seed = tablesampleClause.Seed is not null
                ? (int)LiteralFolding.EvaluateConstantDouble(tablesampleClause.Seed)
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

    /// <summary>
    /// Plans a subquery source by recursively invoking the parent planner via the
    /// <c>planStatement</c> delegate, then wraps the result in a
    /// <see cref="SubqueryOperator"/> carrying the alias.
    /// </summary>
    public QueryOperator PlanSubquery(SubquerySource subquery)
    {
        QueryOperator innerPlan = _planStatement(subquery.Query);
        return new SubqueryOperator(innerPlan, subquery.Alias);
    }

    /// <summary>
    /// Plans a table-valued function call as a <see cref="FunctionSourceOperator"/>,
    /// optionally wrapped in an <see cref="AliasOperator"/>. Throws if the function
    /// is not registered.
    /// </summary>
    public QueryOperator PlanFunctionSource(FunctionSource functionSource, bool hasJoins)
    {
        ITableValuedFunction? function = _functionRegistry.TryGetTableValued(functionSource.CallName);

        if (function is null)
        {
            throw new InvalidOperationException(
                $"Unknown table-valued function: '{functionSource.CallName}'.");
        }

        QueryOperator sourceOperator = new FunctionSourceOperator(function, functionSource.Arguments);

        if (functionSource.Alias is not null || hasJoins)
        {
            sourceOperator = new AliasOperator(
                sourceOperator, functionSource.Alias ?? functionSource.FunctionName);
        }

        return sourceOperator;
    }
}
