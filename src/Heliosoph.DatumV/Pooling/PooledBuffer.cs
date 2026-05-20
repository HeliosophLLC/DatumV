#if POOL_DIAGNOSTICS
using System.Diagnostics;

namespace Heliosoph.DatumV.Pooling;

/// <summary>
/// Diagnostic tracker for a <see cref="Model.DataValue"/> array that has passed through
/// <see cref="Pool"/>. When the buffer is returned to the pool the
/// tracker is marked as pooled, and any subsequent access through
/// <see cref="Model.Row.RawValues"/> or the <see cref="Model.Row"/> indexers throws
/// an <see cref="InvalidOperationException"/> with the return-site stack trace.
/// <para>
/// This class only exists when the <c>POOL_DIAGNOSTICS</c> compiler symbol is defined
/// (Debug builds and test projects). Release builds carry zero overhead.
/// </para>
/// </summary>
internal sealed class PooledBuffer
{
    private volatile bool _pooled;
    private string? _returnStackTrace;

    /// <summary>
    /// Marks this buffer as returned to the pool. Any subsequent access will throw.
    /// </summary>
    internal void MarkReturned()
    {
        _returnStackTrace = new StackTrace(skipFrames: 2, fNeedFileInfo: true).ToString();
        _pooled = true;
    }

    /// <summary>
    /// Marks this buffer as rented (live). Clears the return-site stack trace.
    /// </summary>
    internal void MarkRented()
    {
        _pooled = false;
        _returnStackTrace = null;
    }

    /// <summary>
    /// Returns <see langword="true"/> if the buffer is currently in the pool.
    /// </summary>
    internal bool IsPooled => _pooled;

    /// <summary>
    /// Throws if the buffer has been returned to the pool.
    /// </summary>
    /// <param name="context">Caller context for the error message (e.g. property name).</param>
    internal void AssertNotReturned(string context = "")
    {
        if (_pooled)
        {
            throw new InvalidOperationException(
                $"USE-AFTER-RETURN: Access to a DataValue[] buffer that was returned to the pool. " +
                $"Context: {context}\n" +
                $"Buffer was returned at:\n{_returnStackTrace}");
        }
    }

    /// <summary>
    /// Throws if this buffer has already been marked as returned (double-return detection).
    /// </summary>
    internal void AssertNotDoubleReturned()
    {
        if (_pooled)
        {
            throw new InvalidOperationException(
                $"DOUBLE-RETURN: Buffer returned to pool twice without being rented in between.\n" +
                $"Previous return at:\n{_returnStackTrace}");
        }
    }
}
#endif
