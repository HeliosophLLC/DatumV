using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Catalog;

/// <summary>
/// Abstract base class for every planned SQL statement — SELECT and CALL
/// (<see cref="Plans.SelectPlan"/>), DDL (<c>DdlPlan</c>), DML
/// (<c>DmlPlan</c>), DML with RETURNING
/// (<see cref="Plans.DmlReturningPlan"/>), and procedural control flow
/// (<c>BlockPlan</c>, <c>IfPlan</c>, <c>WhilePlan</c>, <c>ForPlan</c>,
/// <c>ProceduralLeafPlan</c>).
/// </summary>
/// <remarks>
/// <para>
/// Plans compose at construction time: a parent plan owns its child plans
/// as fields, and <see cref="ExplainTree"/> walks the full tree without
/// executing anything. <c>EXPLAIN INSERT … SELECT …</c> shows the SELECT
/// subtree under the INSERT node; <c>EXPLAIN BEGIN … END</c> shows the
/// control-flow structure with every embedded query pre-planned.
/// </para>
/// <para>
/// <see cref="ExecuteAsync"/> is a thin dispatch into
/// <see cref="ExecuteImplAsync"/>; subclasses override the latter.
/// Centralizing per-plan <c>ExecutionContext</c> creation here is a
/// planned follow-up — keeping the dispatch thin for now avoids forcing
/// every DDL subclass to allocate a context it never uses.
/// </para>
/// <para>
/// Every execute / analyze path requires a non-null
/// <see cref="BatchContext"/>. Top-level "batch of one" callers either
/// construct a fresh <c>new BatchContext(catalog)</c> in a <c>using</c>
/// scope, or call the <c>TableCatalog.ExecuteAsync(plan, ct)</c>
/// convenience that owns the lifetime; procedural batches share the one
/// their <see cref="BatchExecutor"/> owns.
/// </para>
/// </remarks>
public abstract class StatementPlan
{
    /// <summary>
    /// The static EXPLAIN tree — operator structure, cardinality estimates,
    /// pruning annotations, and warnings. Reading does not execute the plan.
    /// </summary>
    public abstract ExplainPlanNode ExplainTree { get; }

    /// <summary>
    /// The <see cref="TableCatalog"/> this plan was built against, or
    /// <see langword="null"/> for stateless plans. Used by host code that
    /// wants a "batch of one" iteration without threading the catalog
    /// separately — see
    /// <see cref="TableCatalog.ExecuteAsync(StatementPlan, CancellationToken)"/>.
    /// </summary>
    public virtual TableCatalog? Catalog => null;

    /// <summary>
    /// Default: returns the static <see cref="ExplainTree"/>. Side-effect
    /// plans (DDL/DML) inherit this; <see cref="Plans.SelectPlan"/>
    /// overrides to run the operator tree under instrumentation. Plans
    /// that wrap a side effect with a row stream (DML RETURNING) follow
    /// SQL Server semantics — the side effect applies and the
    /// instrumented tree is returned.
    /// </summary>
    public virtual Task<ExplainPlanNode> AnalyzeAsync(
        CancellationToken cancellationToken,
        BatchContext batchContext)
    {
        ArgumentNullException.ThrowIfNull(batchContext);
        return Task.FromResult(ExplainTree);
    }

    /// <summary>
    /// Streams the plan's output as a sequence of <see cref="RowBatch"/>.
    /// Each batch is automatically returned to the pool when the iterator
    /// advances past it, so consumers must finish using the current batch
    /// before requesting the next one.
    /// </summary>
    public IAsyncEnumerable<RowBatch> ExecuteAsync(
        CancellationToken cancellationToken,
        BatchContext batchContext)
    {
        ArgumentNullException.ThrowIfNull(batchContext);
        return ExecuteImplAsync(cancellationToken, batchContext);
    }

    /// <summary>
    /// Subclass-supplied execution. Same contract as
    /// <see cref="StatementPlan.ExecuteAsync(CancellationToken, BatchContext)"/>:
    /// streams output as <see cref="RowBatch"/> values; each yielded batch
    /// is owned by the consumer until the next iteration step.
    /// </summary>
    protected abstract IAsyncEnumerable<RowBatch> ExecuteImplAsync(
        CancellationToken cancellationToken,
        BatchContext batchContext);
}
