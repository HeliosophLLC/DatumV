using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using DatumIngest.Execution.Pooling;
using DatumIngest.Functions;
using DatumIngest.Model;

namespace DatumIngest.Execution.Pooling;


/// <summary>
/// Process-wide static pool.
/// </summary>
public static class GlobalBufferPool
{
    private static readonly ConcurrentQueue<Pool> pools = new();
    
    /// <summary>
    /// Gets a global backing object.
    /// </summary>
    private static PoolBacking Backing { get; } = new PoolBacking();


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
            throw new InvalidOperationException("Attempted to return a Pool instance that was not created by this GlobalBufferPool.");
        }
        
        pools.Enqueue(pool);
    }
}