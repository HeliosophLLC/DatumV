using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using DatumIngest.Functions;
using DatumIngest.Model;

namespace DatumIngest.Execution;

/// <summary>
/// A <see cref="ConcurrentQueue{T}"/> paired with an atomic approximate count.
/// <see cref="ConcurrentQueue{T}.Count"/> traverses internal segments with memory
/// barriers on every call — O(segments), not O(1). At 65 million returns per query
/// this dominated 24% of wall-clock time. The atomic counter eliminates that cost
/// at the expense of a slight over-count race (harmless: we may enqueue a few items
/// past the cap, which is only a soft limit).
/// </summary>
internal sealed class CountedPool<T>
{
    private readonly ConcurrentQueue<T> _queue = new();
    private int _approximateCount;

    /// <summary>Approximate number of items in the pool. May briefly over- or under-count.</summary>
    public int ApproximateCount => Volatile.Read(ref _approximateCount);

    /// <summary>Attempts to dequeue an item. Decrements the counter on success.</summary>
    public bool TryDequeue([MaybeNullWhen(false)] out T result)
    {
        if (_queue.TryDequeue(out result))
        {
            Interlocked.Decrement(ref _approximateCount);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Enqueues an item if the approximate count is below <paramref name="limit"/>.
    /// Uses a racy read — may slightly exceed the limit, which is acceptable for a soft cap.
    /// </summary>
    public void EnqueueIfUnderLimit(T item, int limit)
    {
        if (Volatile.Read(ref _approximateCount) < limit)
        {
            _queue.Enqueue(item);
            Interlocked.Increment(ref _approximateCount);
        }
    }

    /// <summary>Unconditionally enqueues an item.</summary>
    public void Enqueue(T item)
    {
        _queue.Enqueue(item);
        Interlocked.Increment(ref _approximateCount);
    }

    /// <summary>Drains all items from the pool.</summary>
    public void Clear()
    {
        while (_queue.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _approximateCount);
        }
    }
}

/// <summary>
/// Process-wide static pool of <see cref="Row"/> objects and <see cref="DataValue"/> arrays,
/// bucketed by field count. Survives across queries so that warm pools from earlier queries
/// are immediately available to subsequent ones — eliminating repeated allocation/GC pressure.
/// </summary>
/// <remarks>
/// <para>
/// All pools use <see cref="CountedPool{T}"/> (a <see cref="ConcurrentQueue{T}"/> with an
/// atomic counter) for lock-free, thread-safe rent/return across producer and consumer
/// threads (e.g. JOIN produces rows, GROUP BY returns them).
/// </para>
/// <para>
/// Call <see cref="Warmup"/> after planning a query to burst-allocate rows of the expected
/// combined width. Burst allocation creates dense, contiguous blocks in gen2 that avoid the
/// "Swiss cheese" fragmentation caused by interleaved gen0 promotions during organic fill.
/// </para>
/// </remarks>
public static class GlobalBufferPool
{
    private static readonly ConcurrentDictionary<int, CountedPool<DataValue[]>> ArrayPools = new();
    private static readonly ConcurrentDictionary<int, CountedPool<GroupState>> GroupStatePools = new();
    private static readonly ConcurrentDictionary<int, CountedPool<IAggregateAccumulator[]>> AccumulatorArrayPools = new();
    private static readonly ConcurrentQueue<LocalBufferPool> LocalBufferPools = new();

    /// <summary>
    /// Maximum number of items per bucket. Prevents unbounded growth when queries of
    /// varying widths are executed. A lower cap reduces gen2 reference density and
    /// therefore GC card-table scanning cost during ephemeral collections. The pool
    /// only needs enough rows to cover the in-flight batch pipeline
    /// (<c>BatchSize × pipeline_depth</c>); anything beyond that adds gen2 scan
    /// overhead without improving hit rates. Defaults to 8192 per bucket.
    /// </summary>
    private static int _maxItemsPerBucket = 8_192;

#if POOL_DIAGNOSTICS
    /// <summary>
    /// Maps each <see cref="DataValue"/> array that has ever passed through the pool to
    /// its <see cref="PooledBuffer"/> tracker. Uses <see cref="ConditionalWeakTable{TKey,TValue}"/>
    /// so entries are automatically removed when the buffer is garbage-collected — no leaks,
    /// no identity-hash collisions from address reuse.
    /// </summary>
    private static readonly ConditionalWeakTable<DataValue[], PooledBuffer> Trackers = new();

    /// <summary>
    /// Returns the <see cref="PooledBuffer"/> tracker for a buffer, or <see langword="null"/>
    /// if the buffer was never pooled (e.g. test-constructed rows).
    /// </summary>
    internal static PooledBuffer? GetTracker(DataValue[] buffer) =>
        Trackers.TryGetValue(buffer, out PooledBuffer? tracker) ? tracker : null;

    /// <summary>
    /// Throws if the buffer has been returned to the pool and not yet re-rented.
    /// No-op for buffers that were never pooled. Only active under POOL_DIAGNOSTICS.
    /// </summary>
    internal static void AssertNotReturned(DataValue[] buffer, string context = "")
    {
        if (Trackers.TryGetValue(buffer, out PooledBuffer? tracker))
        {
            tracker.AssertNotReturned(context);
        }
    }
#endif

    /// <summary>
    /// Configures the maximum number of items retained per bucket. Must be called before
    /// any pooling activity. Values above zero are accepted; zero or negative values are ignored.
    /// </summary>
    /// <param name="maxItemsPerBucket">
    /// Upper bound on items per field-count bucket. <see cref="Return"/> and
    /// <see cref="ReturnValues"/> silently discard items once a bucket reaches this limit.
    /// </param>
    public static void Configure(int maxItemsPerBucket)
    {
        if (maxItemsPerBucket > 0)
        {
            _maxItemsPerBucket = maxItemsPerBucket;
        }
    }

    /// <summary>
    /// Burst-allocates <paramref name="count"/> <see cref="DataValue"/> arrays of
    /// <paramref name="fieldCount"/> elements. The arrays are allocated contiguously to
    /// ensure dense packing in the managed heap, then enqueued into the pool for
    /// immediate reuse.
    /// </summary>
    /// <param name="fieldCount">Number of fields (columns) per array.</param>
    /// <param name="count">Number of arrays to pre-allocate.</param>
    public static void Warmup(int fieldCount, int count)
    {
        CountedPool<DataValue[]> pool = ArrayPools.GetOrAdd(fieldCount, static _ => new CountedPool<DataValue[]>());

        for (int i = 0; i < count; i++)
        {
            DataValue[] values = new DataValue[fieldCount];
            pool.Enqueue(values);
        }
    }

    /// <summary>
    /// Rents a <see cref="DataValue"/> array of exactly <paramref name="length"/> elements.
    /// Returns a previously returned buffer when one is available; allocates otherwise.
    /// Callers must overwrite every slot before reading — the buffer may contain stale
    /// values from a previous query. Correctness is enforced at development time by the
    /// <c>PooledBuffer</c> tracker (<c>POOL_DIAGNOSTICS</c>), which prevents any access
    /// to a returned buffer until it is re-rented.
    /// </summary>
    public static DataValue[] Rent(int length)
    {
        if (ArrayPools.TryGetValue(length, out CountedPool<DataValue[]>? pool)
            && pool.TryDequeue(out DataValue[]? buffer))
        {
#if POOL_DIAGNOSTICS
            if (Trackers.TryGetValue(buffer, out PooledBuffer? tracker))
            {
                tracker.MarkRented();
            }
#endif
            return buffer;
        }

        return new DataValue[length];
    }

    /// <summary>
    /// Returns a <see cref="DataValue"/> array so it can be reused by a future
    /// <see cref="Rent"/> call of the same length.
    /// </summary>
    public static void Return(DataValue[] buffer)
    {
#if POOL_DIAGNOSTICS
        PooledBuffer tracker = Trackers.GetOrCreateValue(buffer);
        tracker.AssertNotDoubleReturned();
        tracker.MarkReturned();
#endif

        CountedPool<DataValue[]> pool = ArrayPools.GetOrAdd(buffer.Length, static _ => new CountedPool<DataValue[]>());
        pool.EnqueueIfUnderLimit(buffer, _maxItemsPerBucket);
    }

    /// <summary>
    /// Returns a <see cref="DataValue"/> array extracted from a <see cref="Row"/> to
    /// the pool. Convenience overload for callers that hold a <see cref="Row"/> struct
    /// and need to recycle its backing buffer.
    /// </summary>
    public static void ReturnValues(Row row)
    {
        Return(row.RawValues);
    }

    /// <summary>
    /// Rents a <see cref="GroupState"/> shell with an <see cref="IAggregateAccumulator"/>
    /// array of exactly <paramref name="accumulatorCount"/> elements. The array may
    /// still contain accumulators from the previous owner — the caller should
    /// type-check each slot and <see cref="IAggregateAccumulator.Reset">Reset</see>
    /// matching accumulators rather than creating fresh ones.
    /// </summary>
    /// <param name="accumulatorCount">Number of aggregate columns.</param>
    /// <returns>
    /// A <see cref="GroupState"/> with an <see cref="GroupState.Accumulators"/>
    /// array ready to be populated by the caller.
    /// </returns>
    public static GroupState RentGroupState(int accumulatorCount)
    {
        GroupState state;

        if (GroupStatePools.TryGetValue(accumulatorCount, out CountedPool<GroupState>? pool)
            && pool.TryDequeue(out GroupState? pooled))
        {
            state = pooled;
        }
        else
        {
            state = new GroupState();
        }

        if (AccumulatorArrayPools.TryGetValue(accumulatorCount, out CountedPool<IAggregateAccumulator[]>? arrayPool)
            && arrayPool.TryDequeue(out IAggregateAccumulator[]? array))
        {
            state.Accumulators = array;
        }
        else
        {
            state.Accumulators = new IAggregateAccumulator[accumulatorCount];
        }

        state.AccumulatorCount = accumulatorCount;
        state.OrderedBuffers = null;
        state.KeyValues = null;

        return state;
    }

    /// <summary>
    /// Returns a <see cref="GroupState"/> to the pool. The backing
    /// <see cref="IAggregateAccumulator"/> array is returned to the exact-length
    /// pool <em>without</em> clearing accumulator references — the next renter can
    /// type-check and <see cref="IAggregateAccumulator.Reset">Reset</see> them
    /// rather than allocating fresh accumulators.
    /// </summary>
    public static void ReturnGroupState(GroupState state)
    {
        IAggregateAccumulator[] array = state.Accumulators;
        int accumulatorCount = state.AccumulatorCount;

        CountedPool<IAggregateAccumulator[]> arrayPool = AccumulatorArrayPools.GetOrAdd(
            accumulatorCount, static _ => new CountedPool<IAggregateAccumulator[]>());
        arrayPool.EnqueueIfUnderLimit(array, _maxItemsPerBucket);

        state.Accumulators = [];
        state.AccumulatorCount = 0;
        state.KeyValues = null;
        state.OrderedBuffers = null;

        CountedPool<GroupState> pool = GroupStatePools.GetOrAdd(
            accumulatorCount, static _ => new CountedPool<GroupState>());
        pool.EnqueueIfUnderLimit(state, _maxItemsPerBucket);
    }

    /// <summary>
    /// Returns multiple <see cref="GroupState"/> objects to the pool in bulk.
    /// </summary>
    public static void ReturnGroupStates(IEnumerable<GroupState> groups, int accumulatorCount)
    {
        CountedPool<GroupState> pool = GroupStatePools.GetOrAdd(
            accumulatorCount, static _ => new CountedPool<GroupState>());
        CountedPool<IAggregateAccumulator[]> arrayPool = AccumulatorArrayPools.GetOrAdd(
            accumulatorCount, static _ => new CountedPool<IAggregateAccumulator[]>());

        foreach (GroupState state in groups)
        {
            arrayPool.EnqueueIfUnderLimit(state.Accumulators, _maxItemsPerBucket);

            state.Accumulators = [];
            state.AccumulatorCount = 0;
            state.KeyValues = null;
            state.OrderedBuffers = null;

            pool.EnqueueIfUnderLimit(state, _maxItemsPerBucket);
        }
    }

    /// <summary>
    /// Rents a <see cref="LocalBufferPool"/> for a single query. Returns a previously
    /// returned instance when one is available; allocates otherwise. The caller must
    /// <see cref="LocalBufferPool.Dispose">dispose</see> the pool after the query
    /// completes to return owned objects and the pool itself.
    /// </summary>
    public static LocalBufferPool RentLocalBufferPool()
    {
        if (LocalBufferPools.TryDequeue(out LocalBufferPool? pool))
        {
            pool.Reset();
            return pool;
        }

        return new LocalBufferPool();
    }

    /// <summary>
    /// Returns a <see cref="LocalBufferPool"/> to the process-wide pool for reuse
    /// by a subsequent query. Called by <see cref="LocalBufferPool.Dispose"/>.
    /// </summary>
    public static void ReturnLocalBufferPool(LocalBufferPool pool)
    {
        LocalBufferPools.Enqueue(pool);
    }

    /// <summary>
    /// Removes all pooled objects from all buckets, allowing the GC to reclaim memory.
    /// Intended for testing and diagnostic scenarios only.
    /// </summary>
    public static void Clear()
    {
        foreach (CountedPool<DataValue[]> pool in ArrayPools.Values) pool.Clear();
        ArrayPools.Clear();
        foreach (CountedPool<GroupState> pool in GroupStatePools.Values) pool.Clear();
        GroupStatePools.Clear();
        foreach (CountedPool<IAggregateAccumulator[]> pool in AccumulatorArrayPools.Values) pool.Clear();
        AccumulatorArrayPools.Clear();
        while (LocalBufferPools.TryDequeue(out _)) { }
    }
}
