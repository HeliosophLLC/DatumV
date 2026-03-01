using DatumIngest.Catalog;
using DatumIngest.Catalog.Executors;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Adapts an INSERT / UPDATE / DELETE … RETURNING into an
/// <see cref="IQueryOperator"/> so a data-modifying CTE body —
/// <c>WITH cte AS (INSERT/UPDATE/DELETE … RETURNING …)</c> — can act as a
/// row source in the surrounding plan tree.
/// </summary>
/// <remarks>
/// The DML side effect fires on first <see cref="ExecuteAsync"/>, exactly
/// once per surrounding query execution — matching PostgreSQL's
/// modifying-CTE semantics. <c>EXPLAIN WITH cte AS (UPDATE …) SELECT …</c>
/// does not commit the mutation at plan time. Multi-reference CTEs are
/// memoised by <see cref="CommonTableExpressionOperator"/>, so the
/// mutation still runs only once even when the CTE is referenced
/// multiple times.
/// </remarks>
internal sealed class DmlReturningOperator : IQueryOperator
{
    private readonly TableCatalog _catalog;
    private readonly Func<TableCatalog, Task<IQueryPlan>> _executeAsync;
    private readonly string _operatorName;
    private readonly string _explainDetails;

    private DmlReturningOperator(
        TableCatalog catalog,
        Func<TableCatalog, Task<IQueryPlan>> executeAsync,
        string operatorName,
        string explainDetails)
    {
        _catalog = catalog;
        _executeAsync = executeAsync;
        _operatorName = operatorName;
        _explainDetails = explainDetails;
    }

    public static DmlReturningOperator ForInsert(TableCatalog catalog, InsertStatement insert) =>
        new(
            catalog,
            cat => InsertExecutor.ExecuteAsync(cat, insert),
            operatorName: "InsertReturning",
            explainDetails: $"INSERT INTO {insert.TableName} … RETURNING …");

    public static DmlReturningOperator ForUpdate(TableCatalog catalog, UpdateStatement update) =>
        new(
            catalog,
            cat => UpdateExecutor.ExecuteAsync(cat, update),
            operatorName: "UpdateReturning",
            explainDetails: $"UPDATE {update.TableName} … RETURNING …");

    public static DmlReturningOperator ForDelete(TableCatalog catalog, DeleteStatement delete) =>
        new(
            catalog,
            cat => DeleteExecutor.ExecuteAsync(cat, delete),
            operatorName: "DeleteReturning",
            explainDetails: $"DELETE FROM {delete.TableName} … RETURNING …");

    /// <inheritdoc />
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
    {
        IQueryPlan innerPlan = await _executeAsync(_catalog).ConfigureAwait(false);
        await foreach (RowBatch batch in innerPlan.ExecuteAsync(context.CancellationToken)
            .ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    /// <inheritdoc />
    public OperatorPlanDescription DescribeForExplain() => new(_operatorName)
    {
        Properties = new Dictionary<string, string>
        {
            ["statement"] = _explainDetails,
            ["timing"] = "side effect on first execute (modifying-CTE semantics)",
        },
    };
}
