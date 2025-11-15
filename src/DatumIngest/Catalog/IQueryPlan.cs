using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Catalog;

/// <summary>
/// A planned SQL query, ready to execute or explain. Returned from
/// <see cref="TableCatalog.Plan(string)"/>. The plan owns a long-lived
/// hoist store for pre-materialized literals, so the same plan may be
/// inspected and executed multiple times.
/// </summary>
public interface IQueryPlan
{
    /// <summary>
    /// The static EXPLAIN tree — operator structure, cardinality estimates,
    /// pruning annotations, and warnings. Does not execute the query.
    /// </summary>
    ExplainPlanNode ExplainTree { get; }

    /// <summary>
    /// Runs the plan to completion, discarding output rows, and returns the
    /// EXPLAIN tree populated with runtime metrics (rows produced/consumed,
    /// self time, total time). Equivalent to <c>EXPLAIN ANALYZE</c>.
    /// </summary>
    Task<ExplainPlanNode> AnalyzeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Streams the plan's output as a sequence of <see cref="RowBatch"/>.
    /// Each batch is automatically returned to the pool when the iterator
    /// advances past it (i.e. on <c>MoveNextAsync</c> or when the loop ends),
    /// so consumers must finish using the current batch before requesting
    /// the next one.
    /// </summary>
    IAsyncEnumerable<RowBatch> ExecuteAsync(CancellationToken cancellationToken);
}
