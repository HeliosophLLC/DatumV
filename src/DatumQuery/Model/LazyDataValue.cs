namespace Axon.QueryEngine.Model;

/// <summary>
/// Wraps a deferred computation that produces a <see cref="DataValue"/>.
/// The thunk is evaluated at most once and the result is cached for subsequent accesses.
/// Thread-safe: concurrent callers will observe the same cached value.
/// </summary>
public sealed class LazyDataValue
{
    private readonly Lazy<DataValue> _lazy;
    private readonly DataKind _kind;

    /// <summary>
    /// Creates a deferred value whose <see cref="Kind"/> is available immediately
    /// but whose payload is not computed until <see cref="Value"/> is first accessed.
    /// </summary>
    /// <param name="thunk">The factory that produces the value. Invoked at most once.</param>
    /// <param name="kind">The expected kind, available without forcing the thunk.</param>
    public LazyDataValue(Func<DataValue> thunk, DataKind kind)
    {
        // LazyThreadSafetyMode.ExecutionAndPublication ensures the thunk runs at most
        // once even under concurrent access.
        _lazy = new Lazy<DataValue>(thunk, LazyThreadSafetyMode.ExecutionAndPublication);
        _kind = kind;
    }

    /// <summary>The type discriminator, available without forcing the thunk.</summary>
    public DataKind Kind => _kind;

    /// <summary>
    /// Forces evaluation of the thunk (if not already evaluated) and returns the cached result.
    /// Exceptions thrown by the thunk are propagated to the caller.
    /// </summary>
    public DataValue Value => _lazy.Value;
}
