using System.Collections.Concurrent;
using DatumIngest.Model;

namespace DatumIngest.Execution;

/// <summary>
/// Thread-safe pool of <see cref="DataValue"/> arrays bucketed by exact length.
/// Used by join operators to avoid per-row heap allocation of combined-row
/// backing arrays. Downstream consumers (e.g. GROUP BY) return buffers after
/// they have extracted all needed values from each row.
/// </summary>
/// <remarks>
/// Uses <see cref="ConcurrentQueue{T}"/> instead of <see cref="ConcurrentBag{T}"/>
/// because the pool operates in a producer-consumer pattern across threads (JOIN
/// produces rows on one thread, GROUP BY returns them on another). ConcurrentBag
/// uses thread-local lists with <c>Monitor.Enter</c> for cross-thread stealing,
/// which causes severe lock contention at high throughput. ConcurrentQueue is
/// lock-free (Interlocked.CompareExchange), eliminating the contention.
/// </remarks>
public sealed class RowBufferPool
{
    private readonly ConcurrentDictionary<int, ConcurrentQueue<DataValue[]>> _pools = new();
    private readonly ConcurrentDictionary<int, ConcurrentQueue<Row>> _rowPools = new();
    private long _rentCount;
    private long _hitCount;
    private long _returnCount;
    private long _rowRentCount;
    private long _rowHitCount;
    private long _rowReturnCount;

    /// <summary>
    /// Rents a <see cref="DataValue"/> array of exactly <paramref name="length"/> elements.
    /// Returns a previously returned buffer when one is available; allocates otherwise.
    /// </summary>
    public DataValue[] Rent(int length)
    {
        Interlocked.Increment(ref _rentCount);
        if (_pools.TryGetValue(length, out ConcurrentQueue<DataValue[]>? pool)
            && pool.TryDequeue(out DataValue[]? buffer))
        {
            Interlocked.Increment(ref _hitCount);
            return buffer;
        }

        return new DataValue[length];
    }

    /// <summary>
    /// Returns a buffer so it can be reused by a future <see cref="Rent"/> call
    /// of the same length.
    /// </summary>
    public void Return(DataValue[] buffer)
    {
        Interlocked.Increment(ref _returnCount);
        ConcurrentQueue<DataValue[]> pool = _pools.GetOrAdd(buffer.Length, static _ => new ConcurrentQueue<DataValue[]>());
        pool.Enqueue(buffer);
    }

    /// <summary>
    /// Rents a <see cref="Row"/> with a backing <see cref="DataValue"/> array of
    /// <paramref name="fieldCount"/> elements. The caller must call
    /// <see cref="Row.UpdateSchema"/> to set the correct column names before use.
    /// </summary>
    public Row RentRow(int fieldCount)
    {
        Interlocked.Increment(ref _rowRentCount);
        if (_rowPools.TryGetValue(fieldCount, out ConcurrentQueue<Row>? pool)
            && pool.TryDequeue(out Row? row))
        {
            Interlocked.Increment(ref _rowHitCount);
            return row;
        }

        return new Row(new string[fieldCount], new DataValue[fieldCount], new Dictionary<string, int>());
    }

    /// <summary>
    /// Returns a <see cref="Row"/> to the pool so it can be reused by a future
    /// <see cref="RentRow"/> call with the same field count.
    /// </summary>
    public void ReturnRow(Row row)
    {
        Interlocked.Increment(ref _rowReturnCount);
        ConcurrentQueue<Row> pool = _rowPools.GetOrAdd(row.FieldCount, static _ => new ConcurrentQueue<Row>());
        pool.Enqueue(row);
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
        Console.Error.WriteLine($"[RowBufferPool] arrays: rents={rents:N0}  hits={hits:N0}  returns={returns:N0}  hitRate={((double)hits / Math.Max(1, rents)):P1}");
        Console.Error.WriteLine($"[RowBufferPool] rows:   rents={rowRents:N0}  hits={rowHits:N0}  returns={rowReturns:N0}  hitRate={((double)rowHits / Math.Max(1, rowRents)):P1}");
    }
}
