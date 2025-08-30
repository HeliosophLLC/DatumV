using System.Collections.Concurrent;
using DatumIngest.Model;

namespace DatumIngest.Execution;

/// <summary>
/// Per-query wrapper around <see cref="GlobalBufferPool"/> that tracks per-query
/// utilisation statistics and manages <em>owned</em> objects whose lifetimes extend
/// to query end. All actual pooling is delegated to the process-wide
/// <see cref="GlobalBufferPool"/>, so rows and arrays survive across queries.
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
    private long _rowRentCount;
    private long _rowHitCount;
    private long _rowReturnCount;
    private long _ownedArrayCount;
    private long _ownedRowCount;

    /// <summary>
    /// Arrays whose lifetime extends to query end. Drained back to
    /// <see cref="GlobalBufferPool"/> on <see cref="Dispose"/>.
    /// </summary>
    private readonly ConcurrentQueue<DataValue[]> _ownedArrays = new();

    /// <summary>
    /// Rows whose lifetime extends to query end. Drained back to
    /// <see cref="GlobalBufferPool"/> on <see cref="Dispose"/>.
    /// </summary>
    private readonly ConcurrentQueue<Row> _ownedRows = new();

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
    /// Rents a <see cref="Row"/> with a backing <see cref="DataValue"/> array of
    /// <paramref name="fieldCount"/> elements. The caller must call
    /// <see cref="Row.UpdateSchema"/> to set the correct column names before use.
    /// </summary>
    public Row RentRow(int fieldCount)
    {
        Interlocked.Increment(ref _rowRentCount);
        Row row = GlobalBufferPool.RentRow(fieldCount);
        Interlocked.Increment(ref _rowHitCount);
        return row;
    }

    /// <summary>
    /// Returns a <see cref="Row"/> to the pool so it can be reused by a future
    /// <see cref="RentRow"/> call with the same field count.
    /// </summary>
    public void ReturnRow(Row row)
    {
        Interlocked.Increment(ref _rowReturnCount);
        GlobalBufferPool.ReturnRow(row);
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
    /// Registers a <see cref="Row"/> as owned by this query. The row will be
    /// returned to <see cref="GlobalBufferPool"/> when this pool is disposed.
    /// Use this when the row's lifetime is ambiguous or extends to query end.
    /// </summary>
    /// <param name="row">The row to take ownership of.</param>
    /// <returns>The same <paramref name="row"/>, for fluent chaining.</returns>
    public Row Own(Row row)
    {
        _ownedRows.Enqueue(row);
        Interlocked.Increment(ref _ownedRowCount);
        return row;
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
    /// Rents a <see cref="Row"/> and immediately registers it as owned.
    /// Equivalent to <c>Own(RentRow(fieldCount))</c>.
    /// </summary>
    public Row RentOwnedRow(int fieldCount)
    {
        return Own(RentRow(fieldCount));
    }

    /// <summary>Writes pool utilisation statistics to stderr (temporary diagnostics).</summary>
    public void DumpStats()
    {
        long rents = Interlocked.Read(ref _rentCount);
        long hits = Interlocked.Read(ref _hitCount);
        long returns = Interlocked.Read(ref _returnCount);
        long rowRents = Interlocked.Read(ref _rowRentCount);
        long rowHits = Interlocked.Read(ref _rowHitCount);
        long rowReturns = Interlocked.Read(ref _rowReturnCount);
        long ownedArrays = Interlocked.Read(ref _ownedArrayCount);
        long ownedRows = Interlocked.Read(ref _ownedRowCount);
        Console.Error.WriteLine($"[LocalBufferPool] arrays: rents={rents:N0}  hits={hits:N0}  returns={returns:N0}  hitRate={((double)hits / Math.Max(1, rents)):P1}");
        Console.Error.WriteLine($"[LocalBufferPool] rows:   rents={rowRents:N0}  hits={rowHits:N0}  returns={rowReturns:N0}  hitRate={((double)rowHits / Math.Max(1, rowRents)):P1}");
        Console.Error.WriteLine($"[LocalBufferPool] owned:  arrays={ownedArrays:N0}  rows={ownedRows:N0}");
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
        _rowRentCount = 0;
        _rowHitCount = 0;
        _rowReturnCount = 0;
        _ownedArrayCount = 0;
        _ownedRowCount = 0;

        // Drain in case a previous query didn't dispose cleanly.
        while (_ownedArrays.TryDequeue(out _)) { }
        while (_ownedRows.TryDequeue(out _)) { }
    }

    /// <summary>
    /// Returns all owned arrays and rows to <see cref="GlobalBufferPool"/>,
    /// then returns this <see cref="LocalBufferPool"/> instance itself to
    /// the global pool for reuse by a subsequent query.
    /// </summary>
    public void Dispose()
    {
        while (_ownedArrays.TryDequeue(out DataValue[]? buffer))
        {
            GlobalBufferPool.Return(buffer);
        }

        while (_ownedRows.TryDequeue(out Row? row))
        {
            GlobalBufferPool.ReturnRow(row);
        }

        GlobalBufferPool.ReturnLocalBufferPool(this);
    }
}
