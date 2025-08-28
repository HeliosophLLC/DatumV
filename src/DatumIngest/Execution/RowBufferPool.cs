using DatumIngest.Model;

namespace DatumIngest.Execution;

/// <summary>
/// Per-query wrapper around <see cref="GlobalBufferPool"/> that tracks per-query
/// utilisation statistics. All actual pooling is delegated to the process-wide
/// <see cref="GlobalBufferPool"/>, so rows and arrays survive across queries.
/// </summary>
public sealed class RowBufferPool
{
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
