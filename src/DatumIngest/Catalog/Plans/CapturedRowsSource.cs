using System.Runtime.CompilerServices;
using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Catalog.Plans;

/// <summary>
/// Leaf <see cref="StatementPlan"/> that wraps a sequence of in-memory
/// <see cref="RowBatch"/> values as a row source. The composer pattern
/// for DML RETURNING — a <see cref="DmlReturningPlan"/> owns a child
/// <see cref="DmlPlan"/> (which captures post-mutation rows into a
/// <see cref="CapturedRowsSource"/>) plus the projection over the
/// captured rows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle.</b> Batches are added during the side-effect phase via
/// <see cref="Capture"/>; once iteration begins (<see cref="ExecuteImplAsync"/>),
/// the source yields them in capture order. Iteration is single-shot —
/// re-iterating throws. The source does NOT return captured batches to
/// the pool; the composer (typically <see cref="DmlReturningPlan"/>)
/// owns end-to-end disposal.
/// </para>
/// <para>
/// <b>Thread-safety.</b> Designed for sequential use within a single
/// plan execution: <see cref="Capture"/> runs during the parent
/// <see cref="DmlPlan"/>'s side-effect application; iteration runs after.
/// No interleaving expected; the source does not lock internally.
/// </para>
/// </remarks>
internal sealed class CapturedRowsSource : StatementPlan
{
    private readonly TableCatalog _catalog;
    private readonly List<RowBatch> _captured = new();
    private int _executed;

    public CapturedRowsSource(TableCatalog catalog, string label = "post-mutation image")
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        ExplainTree = new ExplainPlanNode
        {
            OperatorName = "CapturedRows",
            Details = label,
            EstimatedRows = 0,
        };
    }

    /// <summary>
    /// Appends a captured batch to the source. Called by the upstream
    /// DML side-effect application (executor) before the consumer
    /// (typically a <see cref="DmlReturningPlan"/> projection) iterates.
    /// </summary>
    public void Capture(RowBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        _captured.Add(batch);
    }

    /// <summary>
    /// Snapshot of captured batches, in capture order. Exposed so the
    /// composer (<see cref="DmlReturningPlan"/>) can drive its projection
    /// loop and own the end-to-end disposal — iterating
    /// <see cref="ExecuteImplAsync"/> is the contract-pure path; this
    /// accessor is the composer-internal shortcut.
    /// </summary>
    public IReadOnlyList<RowBatch> Batches => _captured;

    /// <inheritdoc />
    public override TableCatalog Catalog => _catalog;

    /// <inheritdoc />
    public override ExplainPlanNode ExplainTree { get; }

    /// <inheritdoc />
#pragma warning disable CS1998 // Async method lacks 'await' operators — yielding from a buffered list.
    protected override async IAsyncEnumerable<RowBatch> ExecuteImplAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        BatchContext batchContext)
    {
        if (Interlocked.Exchange(ref _executed, 1) != 0)
        {
            throw new InvalidOperationException(
                "CapturedRowsSource has already been iterated. " +
                "Statement plans represent a single pending execution; the composer iterates the source once.");
        }
        _ = batchContext;
        cancellationToken.ThrowIfCancellationRequested();
        foreach (RowBatch batch in _captured)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return batch;
        }
    }
#pragma warning restore CS1998
}
