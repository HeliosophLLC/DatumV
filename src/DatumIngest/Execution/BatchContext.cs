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
    private bool _disposed;

    /// <summary>
    /// Creates a fresh batch context: allocates a new procedure-lifetime
    /// <see cref="VariableStore"/> arena (with a baseline reference), and
    /// an empty <see cref="VariableScope"/> with one root frame already
    /// pushed.
    /// </summary>
    public BatchContext()
    {
        VariableStore = new Arena();
        // Baseline reference owned by this batch context. Released exactly
        // once on Dispose. Mirrors the QueryPlan._hoistStore pattern.
        VariableStore.AddReference();
        VariableScope = new VariableScope();
    }

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
    /// <c>VariableExpression</c> at evaluation time.
    /// </summary>
    public VariableScope VariableScope { get; }

    /// <summary>
    /// Number of procedure-call frames currently above this context's
    /// invocation. The top-level batch is depth 0; the body of an
    /// <c>EXEC proc.X(...)</c> runs in a fresh <see cref="BatchContext"/>
    /// at depth 1; any <c>EXEC proc.Y(...)</c> inside that body opens a
    /// further context at depth 2; and so on. The procedure executor
    /// rejects new calls once the depth would exceed
    /// <see cref="BatchExecutor.MaxProcedureCallDepth"/> so a self- or
    /// mutually recursive procedure fails fast with a clear message
    /// instead of running until the call stack overflows.
    /// </summary>
    public int ProcedureCallDepth { get; init; }

    /// <summary>
    /// Stabilises <paramref name="value"/> from <paramref name="sourceStore"/>
    /// into <see cref="VariableStore"/> and binds it to <paramref name="name"/>
    /// in the topmost frame of <see cref="VariableScope"/>. Throws if the
    /// name is already declared in the topmost frame. When
    /// <paramref name="structFieldNames"/> is non-<see langword="null"/>,
    /// the names attach to this binding so downstream
    /// <c>@var['field']</c> access can resolve a position.
    /// </summary>
    public void Declare(
        string name,
        DataValue value,
        IValueStore sourceStore,
        IReadOnlyList<string>? structFieldNames = null)
    {
        DataValue stable = DataValueRetention.Stabilize(value, sourceStore, VariableStore);
        VariableScope.Declare(name, stable, structFieldNames);
    }

    /// <summary>
    /// Stabilises <paramref name="value"/> into <see cref="VariableStore"/>
    /// and updates the existing binding. Walks the scope chain outward to
    /// find the frame holding the name. Throws if the name is unbound.
    /// </summary>
    public void Set(string name, DataValue value, IValueStore sourceStore)
    {
        DataValue stable = DataValueRetention.Stabilize(value, sourceStore, VariableStore);
        VariableScope.Set(name, stable);
    }

    /// <summary>
    /// Releases the baseline reference on <see cref="VariableStore"/>.
    /// Idempotent. Idiomatic use is <c>using BatchContext ctx = new();</c>
    /// at the procedural-executor's outermost scope.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        VariableStore.ReleaseReference();
    }
}
