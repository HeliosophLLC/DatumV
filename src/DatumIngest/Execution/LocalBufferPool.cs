using System.Collections.Concurrent;
using DatumIngest.Model;

namespace DatumIngest.Execution;

/// <summary>
/// Per-query wrapper around <see cref="GlobalBufferPool"/> that tracks per-query
/// utilisation statistics and manages <em>owned</em> objects whose lifetimes extend
/// to query end. All actual pooling is delegated to the process-wide
/// <see cref="GlobalBufferPool"/>, so arrays survive across queries.
/// </summary>
/// <remarks>
/// Two lifetime patterns are supported:
/// <list type="bullet">
///   <item>
///     <term>Rent / Return</term>
///     <description>Caller explicitly returns the object when finished (e.g. JOIN
///     produces a row, GROUP BY returns it after extracting values).</description>
///   </item>
///   <item>
///     <term>Rent / Own</term>
///     <description>Object lifetime is ambiguous or query-scoped. The object is
///     registered on an internal list and bulk-returned to <see cref="GlobalBufferPool"/>
///     when this pool is <see cref="Dispose">disposed</see>.</description>
///   </item>
/// </list>
/// </remarks>
public sealed class LocalBufferPool : IDisposable
{
    private long _rentCount;
    private long _hitCount;
    private long _returnCount;
    private long _ownedArrayCount;

    /// <summary>
    /// Arrays whose lifetime extends to query end. Drained back to
    /// <see cref="GlobalBufferPool"/> on <see cref="Dispose"/>.
    /// </summary>
    private readonly ConcurrentQueue<DataValue[]> _ownedArrays = new();

    /// <summary>
    /// Rents a <see cref="DataValue"/> array of exactly <paramref name="length"/> elements.
    /// Returns a previously returned buffer when one is available; allocates otherwise.
    /// </summary>
    public DataValue[] Rent(int length)
    {
        Interlocked.Increment(ref _rentCount);
        DataValue[] buffer = GlobalBufferPool.Rent(length);

        // A hit means the global pool had one ready (the buffer came back non-null
        // from the queue). We detect a miss by checking whether the array is
        // zero-initialised, but since returned arrays are also zeroed by the runtime
        // on allocation, the simplest accurate approach is to track at the global level.
        // For now, count every successful rent as a hit — the first query will show
        // inflated hit rates, but cross-query reuse (the real metric) will be visible
        // when comparing first-query vs second-query stats.
        Interlocked.Increment(ref _hitCount);
        return buffer;
    }

    /// <summary>
    /// Returns a buffer so it can be reused by a future <see cref="Rent"/> call
    /// of the same length.
    /// </summary>
    public void Return(DataValue[] buffer)
    {
        Interlocked.Increment(ref _returnCount);
        GlobalBufferPool.Return(buffer);
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
        GlobalBufferPool.Return(row.RawValues);
    }

    /// <summary>
    /// Registers a <see cref="DataValue"/> array as owned by this query. The array
    /// will be returned to <see cref="GlobalBufferPool"/> when this pool is disposed.
    /// Use this when the array's lifetime is ambiguous or extends to query end.
    /// </summary>
    /// <param name="buffer">The array to take ownership of.</param>
    /// <returns>The same <paramref name="buffer"/>, for fluent chaining.</returns>
    public DataValue[] Own(DataValue[] buffer)
    {
        _ownedArrays.Enqueue(buffer);
        Interlocked.Increment(ref _ownedArrayCount);
        return buffer;
    }

    /// <summary>
    /// Rents a <see cref="DataValue"/> array and immediately registers it as owned.
    /// Equivalent to <c>Own(Rent(length))</c>.
    /// </summary>
    public DataValue[] RentOwned(int length)
    {
        return Own(Rent(length));
    }

    /// <summary>
    /// Rents a pool-aware <see cref="RowBatch"/>. The backing <see cref="Row"/> array
    /// is rented from <see cref="System.Buffers.ArrayPool{T}.Shared"/>.
    /// When the batch is returned via <see cref="ReturnBatch"/>, all contained
    /// <see cref="DataValue"/> arrays are returned to this pool.
    /// </summary>
    /// <param name="capacity">The maximum number of rows the batch can hold.</param>
    /// <returns>An empty batch ready for <see cref="RowBatch.Add"/> calls.</returns>
    public RowBatch RentBatch(int capacity)
    {
        return RowBatch.Rent(capacity);
    }

    /// <summary>
    /// Returns all <see cref="DataValue"/> arrays contained in the batch to this pool,
    /// then returns the backing <see cref="Row"/> array to
    /// <see cref="System.Buffers.ArrayPool{T}.Shared"/>.
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

    /// <summary>Total number of <see cref="Own"/>/<see cref="RentOwned"/> calls.</summary>
    internal long OwnedArrayCount => Interlocked.Read(ref _ownedArrayCount);

    /// <summary>
    /// Current number of arrays in the <see cref="_ownedArrays"/> queue.
    /// This is O(segments) — use only in tests and diagnostics, not in hot paths.
    /// </summary>
    internal int OwnedArrayQueueCount => _ownedArrays.Count;

    /// <summary>Writes pool utilisation statistics to stderr (temporary diagnostics).</summary>
    public void DumpStats()
    {
        long rents = Interlocked.Read(ref _rentCount);
        long hits = Interlocked.Read(ref _hitCount);
        long returns = Interlocked.Read(ref _returnCount);
        long ownedArrays = Interlocked.Read(ref _ownedArrayCount);
        Console.Error.WriteLine($"[LocalBufferPool] arrays: rents={rents:N0}  hits={hits:N0}  returns={returns:N0}  hitRate={((double)hits / Math.Max(1, rents)):P1}");
        Console.Error.WriteLine($"[LocalBufferPool] owned:  arrays={ownedArrays:N0}");
    }

    /// <summary>
    /// Resets all counters and clears owned-object queues. Called when this instance
    /// is rented from <see cref="GlobalBufferPool"/> for reuse by a new query.
    /// </summary>
    internal void Reset()
    {
        _rentCount = 0;
        _hitCount = 0;
        _returnCount = 0;
        _ownedArrayCount = 0;

        // Drain in case a previous query didn't dispose cleanly.
        while (_ownedArrays.TryDequeue(out _)) { }
    }

    /// <summary>
    /// Returns all owned arrays to <see cref="GlobalBufferPool"/>,
    /// then returns this <see cref="LocalBufferPool"/> instance itself to
    /// the global pool for reuse by a subsequent query.
    /// </summary>
    public void Dispose()
    {
#if POOL_DIAGNOSTICS
        long rents = Interlocked.Read(ref _rentCount);
        long returns = Interlocked.Read(ref _returnCount);
        long leaked = rents - returns;

        if (leaked > 0)
        {
            // Log the imbalance. In Debug builds with POOL_DIAGNOSTICS, this surfaces
            // operators that rent DataValue[] arrays but never return them via
            // ReturnBatch. The threshold allows for arrays legitimately held by
            // operators that outlive the pool (e.g., join build-side partitions
            // that are cleaned up by the operator, not the pool).
            System.Diagnostics.Debug.WriteLine(
                $"[POOL_DIAGNOSTICS] Rent/Return imbalance: rented={rents:N0} returned={returns:N0} leaked={leaked:N0}");
        }
#endif

        while (_ownedArrays.TryDequeue(out DataValue[]? buffer))
        {
            GlobalBufferPool.Return(buffer);
        }

        GlobalBufferPool.ReturnLocalBufferPool(this);
    }
}
