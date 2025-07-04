using Axon.QueryEngine.Catalog;
using Axon.QueryEngine.Execution.Operators;
using Axon.QueryEngine.Functions;
using Axon.QueryEngine.Model;
using Axon.QueryEngine.Parsing.Ast;

namespace Axon.QueryEngine.Execution;

/// <summary>
/// Transforms a parsed <see cref="SelectStatement"/> AST into an executable
/// operator tree (<see cref="IQueryOperator"/>). Performs projection pushdown
/// by analyzing column references to determine the minimal column set each
/// scan operator must produce.
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
        // 1. Build the source operator (FROM clause).
        IQueryOperator source = PlanSource(statement.From.Source);

        // 2. Apply JOINs.
        if (statement.Joins is not null)
        {
            foreach (JoinClause join in statement.Joins)
            {
                IQueryOperator rightSide = PlanSource(join.Source);
                source = new JoinOperator(source, rightSide, join.Type, join.OnCondition);
            }
        }

        // 3. Apply WHERE filter.
        if (statement.Where is not null)
        {
            source = new FilterOperator(source, statement.Where);
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

    private IQueryOperator PlanSource(TableSource source)
    {
        return source switch
        {
            TableReference tableRef => PlanTableReference(tableRef),
            SubquerySource subquery => PlanSubquery(subquery),
            FunctionSource functionSource => PlanFunctionSource(functionSource),
            _ => throw new InvalidOperationException(
                $"Unsupported table source type: {source.GetType().Name}."),
        };
    }

    private IQueryOperator PlanTableReference(TableReference tableRef)
    {
        TableDescriptor descriptor = _catalog.Resolve(tableRef.Name);

        // No projection pushdown yet in V1 -- pass null to get all columns.
        IQueryOperator scanOperator = new ScanOperator(descriptor, requiredColumns: null);

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
}
