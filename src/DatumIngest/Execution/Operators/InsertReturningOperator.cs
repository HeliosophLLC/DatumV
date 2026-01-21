using DatumIngest.Catalog;
using DatumIngest.Model;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Adapts an <see cref="InsertReturningPlan"/> into an <see cref="IQueryOperator"/>
/// so a data-modifying CTE body — <c>WITH cte AS (INSERT … RETURNING …)</c> —
/// can act as a row source in the surrounding plan tree.
/// </summary>
/// <remarks>
/// <para>
/// The wrapped INSERT runs at <see cref="TableCatalog.PlanQuery"/> time, exactly
/// once per containing query plan (the <see cref="InsertExecutor.Execute"/> call
/// that produced the plan already committed the side effect). This operator
/// just replays the captured RETURNING batches — it carries no further side
/// effects and can be safely materialised by
/// <see cref="CommonTableExpressionOperator"/> for multi-reference patterns.
/// </para>
/// <para>
/// <b>Caveat (deferred):</b> with the current sync-at-Plan design, even
/// <c>EXPLAIN WITH cte AS (INSERT …) SELECT …</c> would commit the INSERT
/// during planning. That mirrors how top-level INSERT … RETURNING already
/// behaves and is documented as a known divergence from PostgreSQL, where
/// modifying CTEs run at execution time. The fix lands when the executor
/// goes async-first (C1g) and INSERT execution moves out of plan time.
/// </para>
/// </remarks>
internal sealed class InsertReturningOperator : IQueryOperator
{
    private readonly IQueryPlan _innerPlan;
    private readonly string _explainDetails;

    public InsertReturningOperator(IQueryPlan innerPlan, string targetTableName)
    {
        _innerPlan = innerPlan;
        _explainDetails = $"INSERT INTO {targetTableName} … RETURNING …";
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
    {
        // Forward the inner plan's pre-captured RETURNING batches. The
        // INSERT side effect already ran during planning; iterating here
        // is read-only.
        await foreach (RowBatch batch in _innerPlan.ExecuteAsync(context.CancellationToken)
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
            ["timing"] = "side-effect at plan time; rows replayed at execute",
        },
    };
}
