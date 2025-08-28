using System.Collections.Concurrent;
using DatumIngest.Model;

namespace DatumIngest.Execution;

/// <summary>
/// Process-wide static pool of <see cref="Row"/> objects and <see cref="DataValue"/> arrays,
/// bucketed by field count. Survives across queries so that warm pools from earlier queries
/// are immediately available to subsequent ones — eliminating repeated allocation/GC pressure.
/// </summary>
/// <remarks>
/// <para>
/// All pools use <see cref="ConcurrentQueue{T}"/> for lock-free, thread-safe rent/return
/// across producer and consumer threads (e.g. JOIN produces rows, GROUP BY returns them).
/// </para>
/// <para>
/// Call <see cref="Warmup"/> after planning a query to burst-allocate rows of the expected
/// combined width. Burst allocation creates dense, contiguous blocks in gen2 that avoid the
/// "Swiss cheese" fragmentation caused by interleaved gen0 promotions during organic fill.
/// </para>
/// </remarks>
public static class GlobalBufferPool
{
    private static readonly ConcurrentDictionary<int, ConcurrentQueue<DataValue[]>> ArrayPools = new();
    private static readonly ConcurrentDictionary<int, ConcurrentQueue<Row>> RowPools = new();

    /// <summary>
    /// Maximum number of items per bucket. Prevents unbounded growth when queries of
    /// varying widths are executed. Defaults to 2 million per bucket.
    /// </summary>
    private static int _maxItemsPerBucket = 2_000_000;

    /// <summary>
    /// Configures the maximum number of items retained per bucket. Must be called before
    /// any pooling activity. Values above zero are accepted; zero or negative values are ignored.
    /// </summary>
    /// <param name="maxItemsPerBucket">
    /// Upper bound on items per field-count bucket. <see cref="Return"/> and
    /// <see cref="ReturnRow"/> silently discard items once a bucket reaches this limit.
    /// </param>
    public static void Configure(int maxItemsPerBucket)
    {
        if (maxItemsPerBucket > 0)
        {
            _maxItemsPerBucket = maxItemsPerBucket;
        }
    }

    /// <summary>
    /// Burst-allocates <paramref name="count"/> <see cref="Row"/> objects with backing
    /// <see cref="DataValue"/> arrays of <paramref name="fieldCount"/> elements. The rows
    /// are allocated contiguously to ensure dense packing in the managed heap, then
    /// enqueued into the pool for immediate reuse.
    /// </summary>
    /// <param name="fieldCount">Number of fields (columns) per row.</param>
    /// <param name="count">Number of rows to pre-allocate.</param>
    public static void Warmup(int fieldCount, int count)
    {
        ConcurrentQueue<Row> rowPool = RowPools.GetOrAdd(fieldCount, static _ => new ConcurrentQueue<Row>());
        Dictionary<string, int> sharedEmptyIndex = new(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < count; i++)
        {
            string[] names = new string[fieldCount];
            DataValue[] values = new DataValue[fieldCount];
            Row row = new(names, values, sharedEmptyIndex);
            rowPool.Enqueue(row);
        }
    }

    /// <summary>
    /// Rents a <see cref="DataValue"/> array of exactly <paramref name="length"/> elements.
    /// Returns a previously returned buffer when one is available; allocates otherwise.
    /// </summary>
    public static DataValue[] Rent(int length)
    {
        if (ArrayPools.TryGetValue(length, out ConcurrentQueue<DataValue[]>? pool)
            && pool.TryDequeue(out DataValue[]? buffer))
        {
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
        ConcurrentQueue<DataValue[]> pool = ArrayPools.GetOrAdd(buffer.Length, static _ => new ConcurrentQueue<DataValue[]>());

        if (pool.Count < _maxItemsPerBucket)
        {
            pool.Enqueue(buffer);
        }
    }

    /// <summary>
    /// Rents a <see cref="Row"/> with a backing <see cref="DataValue"/> array of
    /// <paramref name="fieldCount"/> elements. The caller must call
    /// <see cref="Row.UpdateSchema"/> to set the correct column names before use.
    /// </summary>
    public static Row RentRow(int fieldCount)
    {
        if (RowPools.TryGetValue(fieldCount, out ConcurrentQueue<Row>? pool)
            && pool.TryDequeue(out Row? row))
        {
            return row;
        }

        return new Row(new string[fieldCount], new DataValue[fieldCount],
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns a <see cref="Row"/> to the pool so it can be reused by a future
    /// <see cref="RentRow"/> call with the same field count.
    /// </summary>
    public static void ReturnRow(Row row)
    {
        ConcurrentQueue<Row> pool = RowPools.GetOrAdd(row.FieldCount, static _ => new ConcurrentQueue<Row>());

        if (pool.Count < _maxItemsPerBucket)
        {
            pool.Enqueue(row);
        }
    }

    /// <summary>
    /// Removes all pooled objects from all buckets, allowing the GC to reclaim memory.
    /// Intended for testing and diagnostic scenarios only.
    /// </summary>
    public static void Clear()
    {
        ArrayPools.Clear();
        RowPools.Clear();
    }
}
