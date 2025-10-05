using System.Collections.Concurrent;

namespace DatumIngest.Execution.Pooling;


/// <summary>
/// Process-wide static pool.
/// </summary>
public static class GlobalPool
{
    private static readonly ConcurrentQueue<Pool> pools = new();
    private static readonly ConcurrentQueue<LocalBufferPool> localBufferPools = new();

    /// <summary>
    /// Gets the process-wide shared <see cref="PoolBacking"/> instance.
    /// </summary>
    internal static PoolBacking Backing { get; } = new PoolBacking();


    /// <summary>
    /// Rents a <see cref="Pool"/>.
    /// </summary>
    public static Pool RentPool()
    {
        if (pools.TryDequeue(out Pool? pool))
        {
            return pool;
        }

        return new Pool(Backing);
    }

    /// <summary>
    /// Returns a <see cref="Pool"/> for future reuse.
    /// </summary>
    public static void ReturnPool(Pool pool)
    {
        if (pool.Backing != Backing)
        {
            throw new InvalidOperationException("Attempted to return a Pool instance that was not created by this GlobalPool.");
        }

        pools.Enqueue(pool);
    }

    /// <summary>
    /// Rents a <see cref="LocalBufferPool"/> for a single query. Returns a previously
    /// returned instance when one is available; allocates otherwise.
    /// </summary>
    public static LocalBufferPool RentLocalBufferPool()
    {
        if (localBufferPools.TryDequeue(out LocalBufferPool? pool))
        {
            pool.Reset();
            return pool;
        }

        return new LocalBufferPool(Backing);
    }

    /// <summary>
    /// Returns a <see cref="LocalBufferPool"/> for reuse by a subsequent query.
    /// </summary>
    public static void ReturnLocalBufferPool(LocalBufferPool pool)
    {
        localBufferPools.Enqueue(pool);
    }
}