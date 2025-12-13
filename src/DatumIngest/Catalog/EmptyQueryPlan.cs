using System.Runtime.CompilerServices;
using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Catalog;

/// <summary>
/// A no-op <see cref="IQueryPlan"/> used by DDL execution paths (e.g. <c>CREATE
/// FUNCTION</c>, <c>DROP FUNCTION</c>) where the side effect of planning has
/// already been applied to the catalog. Streams zero rows; the EXPLAIN tree
/// reports the no-op nature so a host that runs <c>EXPLAIN CREATE FUNCTION ...</c>
/// gets a sensible answer rather than a crash.
/// </summary>
internal sealed class EmptyQueryPlan : IQueryPlan
{
    /// <summary>Shared singleton — the plan is stateless.</summary>
    public static readonly EmptyQueryPlan Instance = new();

    private EmptyQueryPlan() { }

    /// <inheritdoc />
    public ExplainPlanNode ExplainTree { get; } = new()
    {
        OperatorName = "DDL",
        Details = "applied at plan time; no rows produced",
        EstimatedRows = 0,
    };

    /// <inheritdoc />
    public Task<ExplainPlanNode> AnalyzeAsync(CancellationToken cancellationToken)
        => Task.FromResult(ExplainTree);

    /// <inheritdoc />
#pragma warning disable CS1998 // Async method lacks 'await' operators — empty enumerable, nothing to await.
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        IModelStreamingSink? streamingSink,
        BatchContext? batchContext)
    {
        _ = streamingSink;
        _ = batchContext;
        cancellationToken.ThrowIfCancellationRequested();
        yield break;
    }
#pragma warning restore CS1998
}
