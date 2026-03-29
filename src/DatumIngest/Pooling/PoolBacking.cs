using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using DatumIngest.Diagnostics;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;

namespace DatumIngest.Pooling;

/// <summary>
/// 
/// </summary>
public sealed class PoolBacking
{   
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

    /// <summary>
    /// Marks a buffer as rented in the diagnostic tracker.
    /// </summary>
    internal static void MarkRentedWithDiagnostics(DataValue[] buffer)
    {
        if (Trackers.TryGetValue(buffer, out PooledBuffer? tracker))
        {
            tracker.MarkRented();
        }
    }

    /// <summary>
    /// Marks a buffer as returned with full diagnostics: asserts not double-returned,
    /// then marks as returned.
    /// </summary>
    internal static void MarkReturnedWithDiagnostics(DataValue[] buffer)
    {
        PooledBuffer tracker = Trackers.GetOrCreateValue(buffer);
        tracker.AssertNotDoubleReturned();
        tracker.MarkReturned();
    }
#endif

    private int _maxItemsPerBucket = 8_192;
    private readonly ConcurrentDictionary<int, CountedPool<DataValue[]>> dataValuePools = new();
    //private readonly ConcurrentDictionary<int, CountedPool<Row[]>> rowPools = new();
    private readonly ConcurrentDictionary<int, CountedPool<RowBatch>> rowBatchPools = new();
    private readonly ConcurrentDictionary<int, CountedPool<GroupState>> groupStatePools = new();
    private readonly ConcurrentDictionary<int, CountedPool<IAggregateAccumulator[]>> accumulatorArrayPools = new();
    private readonly CountedPool<Arena> arenaPools = new();

    // Set of arenas currently rented out (refcount > 0). Used by the
    // streaming memory profile to report a query's total in-flight arena
    // bytes — including operator-local arenas (OrderBy bufferArena, spill
    // consolidated arenas) that aren't otherwise visible to the
    // BatchExecutor sidecar. ConcurrentDictionary acts as a thread-safe
    // set; the byte value is a placeholder. Add on rent paths, remove
    // when the arena's refcount drops to zero (TryReturn).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Arena, byte> _liveArenas = new();

    // Per-instance leak counters — Interlocked-incremented at every public Rent/Return call site.
    // Always-on (Interlocked is cheap; not gated on DATUM_DIAGNOSTICS so leak tests work in any
    // configuration). Per-instance avoids the cross-test race that the global DatumDiagnostics
    // counters would suffer when xUnit parallelises across collections.
    private long _dataValueArrayRentCount;
    private long _dataValueArrayReturnCount;
    private long _rowBatchRentCount;
    private long _rowBatchReturnCount;
    private long _arenaRentCount;
    private long _arenaFullyReleasedCount;

    /// <summary>Total <see cref="DataValue"/>[] rent calls served by this pool.</summary>
    internal long DataValueArrayRentCount => Volatile.Read(ref _dataValueArrayRentCount);

    /// <summary>Total <see cref="DataValue"/>[] return calls received by this pool.</summary>
    internal long DataValueArrayReturnCount => Volatile.Read(ref _dataValueArrayReturnCount);

    /// <summary>Total <see cref="RowBatch"/> rent calls served by this pool.</summary>
    internal long RowBatchRentCount => Volatile.Read(ref _rowBatchRentCount);

    /// <summary>Total <see cref="RowBatch"/> return calls received by this pool.</summary>
    internal long RowBatchReturnCount => Volatile.Read(ref _rowBatchReturnCount);

    /// <summary>Total <see cref="Arena"/> rent calls served by this pool.</summary>
    internal long ArenaRentCount => Volatile.Read(ref _arenaRentCount);

    /// <summary>
    /// Number of <see cref="TryReturn(Arena)"/> calls that fully released the arena
    /// (refcount hit zero, arena went into the pool / was disposed). Matches
    /// <see cref="ArenaRentCount"/> when every rented arena has flowed back through
    /// the pool, regardless of how many intermediate AddReference / Release pairs
    /// happened (e.g. RentRowBatch with an explicit arena adds and TryReturn releases).
    /// </summary>
    internal long ArenaFullyReleasedCount => Volatile.Read(ref _arenaFullyReleasedCount);


    /// <summary>
    /// Initializes a new <see cref="PoolBacking"/>.
    /// </summary>
    public PoolBacking()
    {
        // if (maxItemsPerBucket > 0)
        // {
        //     _maxItemsPerBucket = maxItemsPerBucket;
        // }
    }


    /// <summary>
    /// Burst-allocates <paramref name="count"/> <see cref="DataValue"/> arrays of
    /// <paramref name="fieldCount"/> elements. The arrays are allocated contiguously to
    /// ensure dense packing in the managed heap, then enqueued into the pool for
    /// immediate reuse.
    /// </summary>
    /// <param name="fieldCount">Number of fields (columns) per array.</param>
    /// <param name="count">Number of arrays to pre-allocate.</param>
    public void Warmup(int fieldCount, int count)
    {
        CountedPool<DataValue[]> pool = dataValuePools.GetOrAdd(fieldCount, static _ => new CountedPool<DataValue[]>());

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
    public DataValue[] RentDataValues(int length)
    {
        // Length-zero arrays are global singletons in .NET (`new DataValue[0]`
        // returns the same instance as `Array.Empty<DataValue>()`). Pooling
        // a singleton across queries is meaningless — there's nothing to
        // reuse — and breaks the POOL_DIAGNOSTICS tracker, which can't
        // distinguish "this query rented the singleton" from "the previous
        // query returned the singleton and we never reset it". Short-circuit
        // here so empty arrays never enter the pool or the tracker.
        if (length == 0)
        {
            return Array.Empty<DataValue>();
        }

        Interlocked.Increment(ref _dataValueArrayRentCount);

        if (dataValuePools.TryGetValue(length, out CountedPool<DataValue[]>? pool)
            && pool.TryDequeue(out DataValue[]? buffer))
        {
#if POOL_DIAGNOSTICS
            if (Trackers.TryGetValue(buffer, out PooledBuffer? tracker))
            {
                tracker.MarkRented();
            }
#endif
            DatumDiagnostics.RecordPoolDataValueArrayRent(fromPool: true);
            return buffer;
        }

        DatumDiagnostics.RecordPoolDataValueArrayRent(fromPool: false);
        return new DataValue[length];
    }

    // /// <summary>
    // /// Rents a <see cref="Row"/> array of exactly <paramref name="length"/> elements.
    // /// </summary>
    // public Row[] RentRows(int length)
    // {
    //     if (rowPools.TryGetValue(length, out CountedPool<Row[]>? pool)
    //         && pool.TryDequeue(out Row[]? buffer))
    //     {
    //         return buffer;
    //     }

    //     return new Row[length];
    // }

    /// <summary>
    /// Rents a <see cref="RowBatch"/> with a backing <see cref="Row"/> array of exactly.
    /// </summary>
    /// <param name="columnLookup">The column lookup for the batch.</param>
    /// <param name="capacity">The capacity of the column batch.</param>
    /// <param name="arena">An optional <see cref="Arena"/> if an arena should not be rented from the pool; if null, a new arena will be rented for the batch.</param> 
    public RowBatch RentRowBatch(ColumnLookup columnLookup, int capacity, Arena? arena = null)
    {
        Interlocked.Increment(ref _rowBatchRentCount);

        if (arena != null)
        {
            arena.AddReference();
        }
        else
        {
            arena = RentArena();
        }

        Row[] rows = ArrayPool<Row>.Shared.Rent(capacity);
        RowBatch rowBatch = new(columnLookup, rows, capacity, arena);

        DatumDiagnostics.RecordPoolRowBatchRent();
        return rowBatch;
    }

    /// <summary>
    /// Rents a <see cref="ColumnBatch"/> with the specified column lookup and row capacity.
    /// </summary>
    /// <param name="columnLookup">The column lookup for the batch.</param>
    /// <param name="rowCapacity">The row capacity for the batch.</param>
    /// <param name="arena">An optional <see cref="Arena"/> if an arena should not be rented from the pool; if null, a new arena will be rented for the batch.</param>
    /// <returns>A rented <see cref="ColumnBatch"/>.</returns>
    public ColumnBatch RentColumnBatch(ColumnLookup columnLookup, int rowCapacity, Arena? arena = null)
    {
        DataValue[][] columns = ArrayPool<DataValue[]>.Shared.Rent(columnLookup.Count);
        for (int i = 0; i < columnLookup.Count; i++)
        {
            columns[i] = RentDataValues(rowCapacity);
        }

        if (arena != null)
        {
            arena.AddReference();
        }
        else
        {
            arena = RentArena();
        }

        DatumDiagnostics.RecordPoolColumnBatchRent();
        return new ColumnBatch(columnLookup, columns, arena);
    }

    /// <summary>
    /// Rents an <see cref="Arena"/>. When <paramref name="initialCapacity"/> is supplied,
    /// freshly-allocated arenas use that as their initial backing-region size — useful when
    /// the caller knows roughly how many bytes will be appended and wants to avoid the
    /// resize-and-copy thrash that happens on every doubling. Pooled arenas keep whatever
    /// capacity they already had; the hint only affects newly-constructed arenas.
    /// </summary>
    /// <param name="initialCapacity">
    /// Hint for the initial mmap region size of a freshly-allocated arena, in bytes.
    /// Zero (default) uses Arena's built-in default. The Arena constructor floors this
    /// at its own minimum.
    /// </param>
    public Arena RentArena(long initialCapacity = 0)
    {
        Interlocked.Increment(ref _arenaRentCount);

        bool fromPool;
        if (arenaPools.TryDequeue(out Arena? arena))
        {
            arena.Unpool();
            fromPool = true;
        }
        else
        {
            arena = initialCapacity > 0 ? new Arena(initialCapacity) : new Arena();
            fromPool = false;
        }

        arena.AddReference();
        _liveArenas.TryAdd(arena, 0);
        DatumDiagnostics.RecordPoolArenaRent(fromPool);

        return arena;
    }

    /// <summary>
    /// Rents a fresh file-backed <see cref="Arena"/>. File-backed arenas can't be reused
    /// across queries (file identity is tied to a specific spill operation), so this always
    /// constructs a new arena and never hits a pool — but it still flows through the pool's
    /// rent-counter accounting so the leak invariant (<see cref="ArenaRentCount"/> matches
    /// <see cref="ArenaFullyReleasedCount"/>) covers both arena kinds uniformly.
    /// </summary>
    /// <param name="filePath">Path to the backing file. The file must not exist; the OS
    /// creates it. Terminal release (refcount → 0) deletes the file via
    /// <see cref="Arena.Dispose"/>.</param>
    /// <param name="initialCapacity">Pre-size the file to this many bytes. See
    /// <see cref="Arena.CreateFileBacked"/> for sizing guidance.</param>
    public Arena RentFileBackedArena(string filePath, long initialCapacity)
    {
        Interlocked.Increment(ref _arenaRentCount);

        Arena arena = Arena.CreateFileBacked(filePath, initialCapacity);
        arena.AddReference();
        _liveArenas.TryAdd(arena, 0);
        DatumDiagnostics.RecordPoolArenaRent(fromPool: false);

        return arena;
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
    public GroupState RentGroupState(int accumulatorCount)
    {
        GroupState state;

        if (groupStatePools.TryGetValue(accumulatorCount, out CountedPool<GroupState>? pool)
            && pool.TryDequeue(out GroupState? pooled))
        {
            state = pooled;
            DatumDiagnostics.RecordPoolGroupStateRent(fromPool: true);
        }
        else
        {
            state = new GroupState();
            DatumDiagnostics.RecordPoolGroupStateRent(fromPool: false);
        }

        if (accumulatorArrayPools.TryGetValue(accumulatorCount, out CountedPool<IAggregateAccumulator[]>? arrayPool)
            && arrayPool.TryDequeue(out IAggregateAccumulator[]? array))
        {
            state.Accumulators = array;
            DatumDiagnostics.RecordPoolAccumulatorArrayRent(fromPool: true);
        }
        else
        {
            state.Accumulators = new IAggregateAccumulator[accumulatorCount];
            DatumDiagnostics.RecordPoolAccumulatorArrayRent(fromPool: false);
        }

        state.AccumulatorCount = accumulatorCount;
        state.OrderedBuffers = null;
        state.KeyValues = null;

        return state;
    }
    
    /// <summary>
    /// Returns a <see cref="DataValue"/> array so it can be reused by a future
    /// <see cref="RentDataValues"/> call of the same length.
    /// </summary>
    public void Return(DataValue[] buffer)
    {
        // Empty-array singletons (see RentDataValues) bypass the pool entirely.
        // Skipping here keeps the POOL_DIAGNOSTICS tracker from ever flagging
        // the singleton as "returned" — a state it could never escape because
        // RentDataValues short-circuits length==0 and never marks it rented.
        if (buffer.Length == 0)
        {
            return;
        }

#if POOL_DIAGNOSTICS
        MarkReturnedWithDiagnostics(buffer);
#endif

        Interlocked.Increment(ref _dataValueArrayReturnCount);

        CountedPool<DataValue[]> pool = dataValuePools.GetOrAdd(buffer.Length, static _ => new CountedPool<DataValue[]>());
        bool pooled = pool.EnqueueIfUnderLimit(buffer, _maxItemsPerBucket);
        DatumDiagnostics.RecordPoolDataValueArrayReturn(pooled);
    }

    /// <summary>
    /// Returns a <see cref="DataValue"/> array extracted from a <see cref="Row"/> to
    /// the pool. Convenience overload for callers that hold a <see cref="Row"/> struct
    /// and need to recycle its backing buffer.
    /// </summary>
    public void Return(Row row)
    {
        Return(row.RawValues);
    }

    /// <summary>
    /// Returns a <see cref="RowBatch"/> to the pool.
    /// </summary>
    public void Return(RowBatch batch)
    {
        Interlocked.Increment(ref _rowBatchReturnCount);

        for (int i = 0; i < batch.Count; i++)
        {
            Return(batch[i]);
        }

        TryReturn(batch.Arena);

        batch.Dispose();
        DatumDiagnostics.RecordPoolRowBatchReturn();
    }

    /// <summary>
    /// Returns a <see cref="ColumnBatch"/> to the pool, including all its backing buffers and arena.
    /// The batch is disposed after return; callers must not access any properties or methods
    /// of the batch after calling this method.
    /// </summary>
    /// <param name="batch"></param>
    public void Return(ColumnBatch batch)
    {
        for (int column = 0; column < batch.ColumnCount; column++)
        {
            Return(batch.GetColumnBuffer(column));
        }

        TryReturn(batch.Arena);

        batch.Dispose();
        DatumDiagnostics.RecordPoolColumnBatchReturn();
    }

    /// <summary>
    /// Returns the <paramref name="arena"/> to the pool and resets it for reuse.
    /// Arenas above a certain capacity are not pooled to prevent unbounded memory usage from errant returns.
    /// </summary>
    /// <param name="arena">The arena to return.</param>
    /// <returns><see langword="true"/> if the arena was accepted into the pool; <see langword="false"/> if the arena was not pooled (e.g. still in use by another owner, or above the capacity threshold).</returns>
    public bool TryReturn(Arena arena)
    {
        /* Logic:
         *  - row batches are typically 1024
         *  - single DataValue arrays are DataValue.SizeBytes
         *  - 256 columns * 1024 rows * DataValue.SizeBytes = ~5MB at current width
        */
        const int maxArenaCapacity = 5 * 1024 * 1024;

        if (arena.ReleaseReference() > 0)
        {
            // Arena still has outstanding references — not a full return.
            DatumDiagnostics.RecordPoolArenaReturn(pooled: false, disposedOverCap: false);
            return false;
        }

        // Refcount hit zero. Either disposed or pooled — in all three branches
        // below, the arena is no longer "live" from the profile's perspective
        // (disposed = gone; pooled = position reset to 0). Remove from the
        // live set unconditionally here so we don't repeat it in each branch.
        _liveArenas.TryRemove(arena, out _);

        if (arena.IsFileBacked)
        {
            // File-backed arenas don't go into the anonymous pool — they have file identity
            // tied to a specific spill operation. Terminal release deletes the file via
            // Arena.Dispose. The shared refcount path keeps RowBatch.Return → TryReturn
            // working uniformly for both arena kinds, and RentFileBackedArena bumps the
            // rent counter so the fully-released bump here keeps the leak invariant balanced.
            arena.Dispose();
            Interlocked.Increment(ref _arenaFullyReleasedCount);
            DatumDiagnostics.RecordPoolArenaReturn(pooled: false, disposedOverCap: false);
            return true;
        }
        else if (arena.Capacity >= maxArenaCapacity)
        {
            arena.Dispose();
            Interlocked.Increment(ref _arenaFullyReleasedCount);
            DatumDiagnostics.RecordPoolArenaReturn(pooled: false, disposedOverCap: true);
            return true;
        }

        arena.Pool();

        arenaPools.Enqueue(arena);
        Interlocked.Increment(ref _arenaFullyReleasedCount);
        DatumDiagnostics.RecordPoolArenaReturn(pooled: true, disposedOverCap: false);

        return true;
    }

    /// <summary>
    /// Sums <see cref="Arena.BytesWritten"/> across every arena currently
    /// rented from this pool (refcount &gt; 0). Used by the streaming memory
    /// profile's Arena sparkline to report the total in-flight arena bytes
    /// for a running query — including operator-local arenas that the
    /// BatchExecutor sidecar can't otherwise observe (OrderBy's bufferArena
    /// during accumulation, hash-join build arenas, SpillReaderWriter
    /// consolidated arenas).
    /// </summary>
    /// <remarks>
    /// Iteration over the concurrent dictionary is approximate under
    /// concurrent rent/return — that's fine for sampling at 1 Hz. The
    /// alternative (per-write counter) would add overhead to every arena
    /// write; this approach pays only at sample time.
    /// </remarks>
    public long TotalLiveArenaBytes()
    {
        long total = 0;
        foreach (KeyValuePair<Arena, byte> kvp in _liveArenas)
        {
            total += kvp.Key.BytesWritten;
        }
        return total;
    }

    /// <summary>
    /// Returns multiple <see cref="GroupState"/> objects to the pool in bulk.
    /// </summary>
    public void Return(IEnumerable<GroupState> groups, int accumulatorCount)
    {
        CountedPool<GroupState> pool = groupStatePools.GetOrAdd(
            accumulatorCount, static _ => new CountedPool<GroupState>());
        CountedPool<IAggregateAccumulator[]> arrayPool = accumulatorArrayPools.GetOrAdd(
            accumulatorCount, static _ => new CountedPool<IAggregateAccumulator[]>());

        foreach (GroupState state in groups)
        {
            bool arrayPooled = arrayPool.EnqueueIfUnderLimit(state.Accumulators, _maxItemsPerBucket);
            DatumDiagnostics.RecordPoolAccumulatorArrayReturn(arrayPooled);

            state.Accumulators = [];
            state.AccumulatorCount = 0;
            state.KeyValues = null;
            state.OrderedBuffers = null;

            bool statePooled = pool.EnqueueIfUnderLimit(state, _maxItemsPerBucket);
            DatumDiagnostics.RecordPoolGroupStateReturn(statePooled);
        }
    }

    /// <summary>
    /// Returns a <see cref="GroupState"/> to the pool. The backing
    /// <see cref="IAggregateAccumulator"/> array is returned to the exact-length
    /// pool <em>without</em> clearing accumulator references — the next renter can
    /// type-check and <see cref="IAggregateAccumulator.Reset">Reset</see> them
    /// rather than allocating fresh accumulators.
    /// </summary>
    public void Return(GroupState state)
    {
        IAggregateAccumulator[] array = state.Accumulators;
        int accumulatorCount = state.AccumulatorCount;

        CountedPool<IAggregateAccumulator[]> arrayPool = accumulatorArrayPools.GetOrAdd(
            accumulatorCount, static _ => new CountedPool<IAggregateAccumulator[]>());
        bool arrayPooled = arrayPool.EnqueueIfUnderLimit(array, _maxItemsPerBucket);
        DatumDiagnostics.RecordPoolAccumulatorArrayReturn(arrayPooled);

        state.Accumulators = [];
        state.AccumulatorCount = 0;
        state.KeyValues = null;
        state.OrderedBuffers = null;

        CountedPool<GroupState> pool = groupStatePools.GetOrAdd(
            accumulatorCount, static _ => new CountedPool<GroupState>());
        bool statePooled = pool.EnqueueIfUnderLimit(state, _maxItemsPerBucket);
        DatumDiagnostics.RecordPoolGroupStateReturn(statePooled);
    }

}

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
    /// Returns <see langword="true"/> when the item was enqueued; <see langword="false"/>
    /// when the limit gate rejected it. Callers can use this to record diagnostic
    /// "pooled vs over-limit-discarded" counters.
    /// </summary>
    public bool EnqueueIfUnderLimit(T item, int limit)
    {
        if (Volatile.Read(ref _approximateCount) < limit)
        {
            _queue.Enqueue(item);
            Interlocked.Increment(ref _approximateCount);
            return true;
        }
        return false;
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
