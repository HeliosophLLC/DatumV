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
    IAsyncEnumerable<RowBatch> ExecuteAsync(CancellationToken cancellationToken)
        => ExecuteAsync(cancellationToken, streamingSink: null);

    /// <summary>
    /// Same as <see cref="ExecuteAsync(CancellationToken)"/>, but attaches a
    /// streaming sink to the per-query <c>ExecutionContext</c>. When non-
    /// <see langword="null"/>, model invocations switch from their batched
    /// <c>InferBatchAsync</c> path to the per-row <c>InferStreamingAsync</c>
    /// path and forward each yielded chunk to <paramref name="streamingSink"/>
    /// as it arrives.
    /// </summary>
    /// <remarks>
    /// Used by <c>EXEC &lt;model-call&gt;</c> in the interactive shell to
    /// render LLM tokens live. Plain <c>SELECT</c>/<c>WHERE</c>/<c>GROUP BY</c>
    /// callers leave <paramref name="streamingSink"/> at <see langword="null"/>
    /// — they need the full collected value, not chunks.
    /// </remarks>
    IAsyncEnumerable<RowBatch> ExecuteAsync(
        CancellationToken cancellationToken,
        IModelStreamingSink? streamingSink);
}
