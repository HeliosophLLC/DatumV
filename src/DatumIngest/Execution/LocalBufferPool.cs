using DatumIngest.Execution.Pooling;
using DatumIngest.Model;

namespace DatumIngest.Execution;

/// <summary>
/// Per-query wrapper around <see cref="PoolBacking"/> that tracks per-query
/// utilisation statistics. All actual pooling is delegated to the shared
/// <see cref="PoolBacking"/> instance, so arrays survive across queries.
/// </summary>
public sealed class LocalBufferPool : IDisposable
{
    private readonly PoolBacking _backing;
    private long _rentCount;
    private long _returnCount;

    /// <summary>
    /// Creates a new <see cref="LocalBufferPool"/> backed by the given
    /// <see cref="PoolBacking"/> instance.
    /// </summary>
    public LocalBufferPool(PoolBacking backing)
    {
        _backing = backing;
    }

    /// <summary>
    /// Creates a new <see cref="LocalBufferPool"/> backed by the
    /// global singleton <see cref="PoolBacking"/>. Used by tests and
    /// legacy code that doesn't inject a backing instance.
    /// </summary>
    public LocalBufferPool() : this(Pooling.GlobalPool.Backing) { }

    /// <summary>
    /// Rents a <see cref="DataValue"/> array of exactly <paramref name="length"/> elements.
    /// Returns a previously returned buffer when one is available; allocates otherwise.
    /// </summary>
    public DataValue[] Rent(int length)
    {
        Interlocked.Increment(ref _rentCount);
        return _backing.RentDataValues(length);
    }

    /// <summary>
    /// Returns a buffer so it can be reused by a future <see cref="Rent"/> call
    /// of the same length.
    /// </summary>
    public void Return(DataValue[] buffer)
    {
        Interlocked.Increment(ref _returnCount);
        _backing.Return(buffer);
    }

    /// <summary>
    /// Rents a <see cref="DataValue"/> array and copies the source values into it.
    /// Used by caching operators (CTE, CrossValidate) that need to hold values
    /// independently of the input batch lifecycle.
    /// </summary>
    /// <param name="source">The values to copy.</param>
    /// <returns>A pool-rented array containing a copy of <paramref name="source"/>.</returns>
    public DataValue[] RentCopy(ReadOnlySpan<DataValue> source)
    {
        DataValue[] buffer = Rent(source.Length);
        source.CopyTo(buffer);
        return buffer;
    }

    /// <summary>
    /// Returns the backing <see cref="DataValue"/> array from a <see cref="Row"/>
    /// to the pool. Convenience overload for callers holding a <see cref="Row"/> struct.
    /// </summary>
    public void ReturnValues(Row row)
    {
        Interlocked.Increment(ref _returnCount);
        _backing.Return(row);
    }

    /// <summary>
    /// Rents a pool-aware <see cref="RowBatch"/>.
    /// When the batch is returned via <see cref="ReturnBatch"/>, all contained
    /// <see cref="DataValue"/> arrays are returned to this pool.
    /// </summary>
    /// <param name="capacity">The maximum number of rows the batch can hold.</param>
    /// <returns>An empty batch ready for <see cref="RowBatch.Add"/> calls.</returns>
    public RowBatch RentBatch(int capacity)
    {
        return _backing.RentRowBatch(capacity);
    }

    /// <summary>
    /// Returns all <see cref="DataValue"/> arrays contained in the batch to this pool,
    /// then returns the backing <see cref="Row"/> array.
    /// The batch must not be used after calling this method.
    /// </summary>
    /// <param name="batch">The batch to return.</param>
    public void ReturnBatch(RowBatch batch)
    {
        for (int i = 0; i < batch.Count; i++)
        {
            ReturnValues(batch[i]);
        }

        batch.ReturnShell();
    }

    /// <summary>Total number of <see cref="Rent"/> calls made against this pool.</summary>
    internal long RentCount => Interlocked.Read(ref _rentCount);

    /// <summary>Total number of <see cref="Return"/>/<see cref="ReturnValues"/> calls.</summary>
    internal long ReturnCount => Interlocked.Read(ref _returnCount);

    /// <summary>
    /// Resets all counters. Called when this instance is rented from the
    /// global pool for reuse by a new query.
    /// </summary>
    internal void Reset()
    {
        _rentCount = 0;
        _returnCount = 0;
    }

    /// <summary>
    /// Returns this <see cref="LocalBufferPool"/> instance to the global pool
    /// for reuse by a subsequent query.
    /// </summary>
    public void Dispose()
    {
#if POOL_DIAGNOSTICS
        long rents = Interlocked.Read(ref _rentCount);
        long returns = Interlocked.Read(ref _returnCount);
        long leaked = rents - returns;

        if (leaked > 0)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[POOL_DIAGNOSTICS] Rent/Return imbalance: rented={rents:N0} returned={returns:N0} leaked={leaked:N0}");
        }
#endif

        Pooling.GlobalPool.ReturnLocalBufferPool(this);
    }
}
