using System.Collections.Concurrent;
using DatumIngest.Execution;

namespace DatumIngest.Pooling;


/// <summary>
/// Process-wide static pool.
/// </summary>
public static class GlobalPool
{
    private static readonly ConcurrentQueue<LocalBufferPool> localBufferPools = new();
    /// <summary>
    /// Gets the global <see cref="PoolBacking"/> instance shared by all pools. Contains the shared resources (e.g. buffers) that survive across queries.
    /// </summary>
    public static PoolBacking Backing => new();

    /// <summary>
    /// Returns a <see cref="LocalBufferPool"/> for reuse by a subsequent query.
    /// </summary>
    public static void ReturnLocalBufferPool(LocalBufferPool pool)
    {
        localBufferPools.Enqueue(pool);
    }
}