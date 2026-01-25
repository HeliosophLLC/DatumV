using DatumIngest.Catalog;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Adapts an <see cref="InsertStatement"/> into an <see cref="IQueryOperator"/>
/// so a data-modifying CTE body — <c>WITH cte AS (INSERT … RETURNING …)</c> —
/// can act as a row source in the surrounding plan tree.
/// </summary>
/// <remarks>
/// The INSERT side effect fires on first <see cref="ExecuteAsync"/>, exactly
/// once per surrounding query execution — matching PostgreSQL's
/// modifying-CTE semantics. <c>EXPLAIN WITH cte AS (INSERT …) SELECT …</c>
/// no longer commits the INSERT at plan time. Multi-reference CTEs are
/// memoised by <see cref="CommonTableExpressionOperator"/>, so the INSERT
/// still runs only once even when the CTE is referenced multiple times.
/// </remarks>
internal sealed class InsertReturningOperator : IQueryOperator
{
    private readonly TableCatalog _catalog;
    private readonly InsertStatement _insert;
    private readonly string _explainDetails;

    public InsertReturningOperator(TableCatalog catalog, InsertStatement insert)
    {
        _catalog = catalog;
        _insert = insert;
        _explainDetails = $"INSERT INTO {insert.TableName} … RETURNING …";
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
    {
        IQueryPlan innerPlan = await InsertExecutor.ExecuteAsync(_catalog, _insert).ConfigureAwait(false);
        await foreach (RowBatch batch in innerPlan.ExecuteAsync(context.CancellationToken)
            .ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    /// <inheritdoc />
    public OperatorPlanDescription DescribeForExplain() => new("InsertReturning")
    {
        Properties = new Dictionary<string, string>
        {
            ["statement"] = _explainDetails,
            ["timing"] = "side effect on first execute (modifying-CTE semantics)",
        },
    };
}
