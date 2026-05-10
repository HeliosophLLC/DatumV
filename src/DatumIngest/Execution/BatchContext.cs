using DatumIngest.Catalog;
using DatumIngest.Functions;
using DatumIngest.Model;

namespace DatumIngest.Execution;

/// <summary>
/// Long-lived state for a procedural batch: the variable store (an arena
/// holding bound variable payloads) plus the scope chain (block-scoped
/// visibility). One <see cref="BatchContext"/> spans the entire batch run
/// regardless of how many child queries execute inside; each child query
/// constructs its own per-query <see cref="ExecutionContext"/> that
/// borrows references to <see cref="VariableStore"/> and
/// <see cref="VariableScope"/> from this batch context.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Lifetime split.</strong> A child query's
/// <see cref="ExecutionContext.Store"/> is rented at query start and
/// returned to the pool when the query ends — its bytes are gone.
/// <see cref="VariableStore"/> here is a separate arena with a baseline
/// reference held by this batch context; it survives every child
/// query's rent/return cycle and is released only when this context is
/// disposed at batch end.
/// </para>
/// <para>
/// <strong>Stabilise on bind.</strong> Use <see cref="Declare"/> /
/// <see cref="Set"/> to bind a DataValue produced by a child query.
/// They copy the payload from the producing query's store into
/// <see cref="VariableStore"/> via
/// <see cref="DataValueRetention.Stabilize"/>, so the binding remains
/// valid after the producing query's arena is recycled.
/// </para>
/// <para>
/// <strong>Single-frame storage, multi-frame visibility.</strong>
/// <see cref="VariableScope"/> tracks block-scoped visibility (push on
/// <c>BEGIN</c>, pop on <c>END</c>); <see cref="VariableStore"/> is
/// procedure-wide. Bytes from popped frames leak into
/// <see cref="VariableStore"/> until batch end. Acceptable for short
/// procedures (seconds, dozens of variables); a future per-frame arena
/// scheme can replace the single store without touching the public
/// API.
/// </para>
/// </remarks>
public sealed class BatchContext : IDisposable
{
    /// <summary>
    /// Creates a fresh batch context: allocates a new procedure-lifetime
    /// <see cref="VariableStore"/> arena (with a baseline reference), an
    /// empty <see cref="VariableScope"/> with one root frame already
    /// pushed, and a <see cref="MemoryAccountant"/> shared by every child
    /// <see cref="ExecutionContext"/> spawned for queries inside the
    /// batch. The <paramref name="catalog"/> is captured so the body's
    /// scalar evaluations (DECLARE / SET initialisers) can build an
    /// <see cref="ExecutionContext"/> with the catalog's function /
    /// sidecar / pool ambient state — every batch runs against some
    /// catalog, so this is a structural correspondence rather than an
    /// optional dependency.
    /// </summary>
    /// <param name="catalog">The catalog the batch runs against.</param>
    /// <param name="memoryBudgetBytes">Spill-trigger threshold applied to
    /// the residency of <em>everything</em> in the batch: all child query
    /// state plus the bytes <see cref="VariableScope"/> bindings hold onto
    /// across queries. <c>null</c> disables the budget check.</param>
    public BatchContext(TableCatalog catalog, long? memoryBudgetBytes = null)
    {
        Catalog = catalog;
        VariableStore = new Arena();
        // Baseline reference owned by this batch context. Released exactly
        // once on Dispose. Mirrors the SelectPlan._hoistStore pattern.
        VariableStore.AddReference();
        Types = new TypeRegistry();
        Accountant = new MemoryAccountant(
            memoryBudgetBytes: memoryBudgetBytes,
            arenaBytesProbe: () => VariableStore.BytesWritten);
        // VariableScope reports declare/set/pop into the same accountant so
        // long-lived DECLARE'd managed payloads count against the batch
        // budget across query boundaries.
        VariableScope = new VariableScope(Accountant);
    }

    /// <summary>
    /// The catalog under which this batch runs. Threaded into every
    /// <see cref="ExecutionContext"/> that <see cref="Declare"/> /
    /// <see cref="Set"/> spin up for binding scalars.
    /// </summary>
    public TableCatalog Catalog { get; }

    /// <summary>
    /// Gets or sets whether <see cref="Dispose"/> has been called on this context.
    /// </summary>
    public bool Disposed { get; private set; }

    /// <summary>
    /// Procedure-lifetime arena holding bound variable payloads. Borrowed
    /// by each child query's <see cref="ExecutionContext"/> as
    /// <c>ExecutionContext.VariableStore</c>; the per-query rent/return
    /// cycle never touches its refcount.
    /// </summary>
    public Arena VariableStore { get; }

    /// <summary>
    /// Block-scoped visibility for procedural variables. Mutated by the
    /// procedural executor on <c>DECLARE</c> / <c>SET</c> /
    /// <c>BEGIN</c> / <c>END</c>; read by query evaluators to resolve
    /// unqualified <see cref="DatumIngest.Parsing.Ast.ColumnReference"/> nodes at evaluation time (variable-first precedence).
    /// </summary>
    public VariableScope VariableScope { get; }

    /// <summary>
    /// Procedure-lifetime type registry for self-describing struct/array values.
    /// Shared across every <see cref="DatumIngest.Execution.ExecutionContext"/> spun up
    /// for queries inside this batch (FOR-loop sources, body queries, CALL bodies, …)
    /// so a TypeId stamped on a row struct in one query stays resolvable when the same
    /// struct appears in a downstream query within the same procedural batch.
    /// </summary>
    public TypeRegistry Types { get; }

    /// <summary>
    /// Procedure-lifetime memory accountant. The single budget / residency
    /// counter for everything in the batch — child query operators,
    /// <see cref="VariableScope"/>-bound managed payloads, DML buffers — so a
    /// runaway DECLARE in iteration N forces query N+1 to spill earlier. Each
    /// child <see cref="DatumIngest.Execution.ExecutionContext"/> borrows
    /// this reference rather than constructing its own. Disposed at batch end.
    /// </summary>
    public MemoryAccountant Accountant { get; }

    /// <summary>
    /// Number of procedure-call frames currently above this context's
    /// invocation. The top-level batch is depth 0; the body of an
    /// <c>CALL proc.X(...)</c> runs in a fresh <see cref="BatchContext"/>
    /// at depth 1; any <c>CALL proc.Y(...)</c> inside that body opens a
    /// further context at depth 2; and so on. The procedure executor
    /// rejects new calls once the depth would exceed
    /// <see cref="BatchExecutor.MaxProcedureCallDepth"/> so a self- or
    /// mutually recursive procedure fails fast with a clear message
    /// instead of running until the call stack overflows.
    /// </summary>
    public int ProcedureCallDepth { get; init; }

    /// <summary>
    /// Optional sink for <c>PRINT</c> diagnostic output. When set, plan
    /// classes that execute a <c>PRINT</c> statement (currently
    /// <c>Plans.ProceduralLeafPlan</c>) invoke this delegate with the
    /// rendered string; <see langword="null"/> when the PRINT is silently
    /// dropped. <c>BatchExecutor.RunWithEventsAsync</c> wires its own
    /// <c>CellPrintBatchEvent</c> emitter through here so PRINTs
    /// surface to procedural-batch consumers; standalone
    /// <c>catalog.ExecuteAsync(plan, ct)</c> callers can subscribe by
    /// setting this before iteration.
    /// </summary>
    public Action<string?>? PrintSink { get; set; }

    /// <summary>
    /// Lifts <paramref name="value"/> from <paramref name="sourceStore"/>
    /// into a managed-payload <see cref="ValueRef"/> and binds it to
    /// <paramref name="name"/> in the topmost frame of
    /// <see cref="VariableScope"/>. Throws if the name is already declared
    /// in the topmost frame. When <paramref name="structFieldNames"/> is
    /// non-<see langword="null"/>, the names attach to this binding so
    /// downstream <c>@var['field']</c> access can resolve a position.
    /// </summary>
    public void Declare(
        string name,
        DataValue value,
        IValueStore sourceStore,
        IReadOnlyList<string>? structFieldNames = null)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        using ExecutionContext context = Catalog.CreateExecutionContext(store: VariableStore,
            types: Types,
            accountant: Accountant);
        EvaluationFrame frame = context.CreateFrame(Row.Empty, sourceStore, VariableStore);
        ValueRef bound = ExpressionEvaluator.ToValueRef(value, frame);
        VariableScope.Declare(name, bound, structFieldNames);
    }

    /// <summary>
    /// Lifts <paramref name="value"/> from <paramref name="sourceStore"/>
    /// into a managed-payload <see cref="ValueRef"/> and updates the
    /// existing binding. Walks the scope chain outward to find the frame
    /// holding the name. Throws if the name is unbound.
    /// </summary>
    public void Set(string name, DataValue value, IValueStore sourceStore)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        using ExecutionContext context = Catalog.CreateExecutionContext(store: VariableStore,
            types: Types,
            accountant: Accountant);
        EvaluationFrame frame = context.CreateFrame(Row.Empty, sourceStore, VariableStore);
        ValueRef bound = ExpressionEvaluator.ToValueRef(value, frame);
        VariableScope.Set(name, bound);
    }

    /// <summary>
    /// Releases the baseline reference on <see cref="VariableStore"/> and
    /// disposes the batch's <see cref="Accountant"/> (stopping its sampling
    /// timer). Idempotent. Idiomatic use is <c>using BatchContext ctx = new();</c>
    /// at the procedural-executor's outermost scope.
    /// </summary>
    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        Accountant.Dispose();
        VariableStore.ReleaseReference();
    }
}
