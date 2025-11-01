using System.Diagnostics;

namespace DatumIngest.Diagnostics;

/// <summary>
/// Process-wide diagnostic counters. Every recording method is decorated with
/// <see cref="ConditionalAttribute"/> keyed to the <c>DATUM_DIAGNOSTICS</c>
/// compile-time symbol — when the symbol is not defined, the C# compiler
/// physically strips every call site (including its argument expressions) from
/// the IL. Zero-cost when off, instrumented when on.
///
/// <para>Enable by building with the <c>DATUM_DIAGNOSTICS</c> constant defined.
/// The default Debug configuration sets it alongside <c>POOL_DIAGNOSTICS</c>.</para>
///
/// <para>Counters are static and process-global. For per-query reporting, call
/// <see cref="Reset"/> before the query and <see cref="Report"/> afterwards.</para>
/// </summary>
public static class DatumDiagnostics
{
    // ───────────────────────── Arena counters ─────────────────────────

    private static long _arenaGrowCount;
    private static long _arenaResetCount;
    private static long _arenaDisposeCount;
    private static long _arenaMaxCapacity;
    private static long _arenaBytesCopiedOnGrow;
    private static long _arenaMmfCreateCount;
    private static long _arenaMmfReleaseCount;

    /// <summary>
    /// Records one grow event on an arena: new backing mapping created, existing
    /// bytes copied, old mapping released. Captures the peak capacity ever seen
    /// so the final report shows the high-water mark even after the arena shrinks
    /// or is recycled.
    /// </summary>
    [Conditional("DATUM_DIAGNOSTICS")]
    public static void RecordArenaGrow(int oldCapacity, int newCapacity, int bytesCopied)
    {
        Interlocked.Increment(ref _arenaGrowCount);
        Interlocked.Add(ref _arenaBytesCopiedOnGrow, bytesCopied);
        Interlocked.Increment(ref _arenaMmfCreateCount);
        Interlocked.Increment(ref _arenaMmfReleaseCount);
        UpdateMax(ref _arenaMaxCapacity, newCapacity);
    }

    /// <summary>Records a call to <c>Arena.Reset</c> (position rewind, mapping retained).</summary>
    [Conditional("DATUM_DIAGNOSTICS")]
    public static void RecordArenaReset() => Interlocked.Increment(ref _arenaResetCount);

    /// <summary>
    /// Records the initial allocation of an arena's backing mapping (first-use
    /// deferred allocation in <c>EnsureCapacity</c>).
    /// </summary>
    [Conditional("DATUM_DIAGNOSTICS")]
    public static void RecordArenaInitialMapping(int capacity)
    {
        Interlocked.Increment(ref _arenaMmfCreateCount);
        UpdateMax(ref _arenaMaxCapacity, capacity);
    }

    /// <summary>
    /// Records a full <c>Arena.Dispose</c>: backing mapping released, arena
    /// removed from play. Often means the arena exceeded the pool's capacity cap
    /// and was discarded instead of pooled.
    /// </summary>
    [Conditional("DATUM_DIAGNOSTICS")]
    public static void RecordArenaDispose(int capacityAtDispose)
    {
        Interlocked.Increment(ref _arenaDisposeCount);
        Interlocked.Increment(ref _arenaMmfReleaseCount);
        UpdateMax(ref _arenaMaxCapacity, capacityAtDispose);
    }

    // ───────────────────────── Pool arena counters ─────────────────────────

    private static long _poolArenaRented;
    private static long _poolArenaRentedFromPool;
    private static long _poolArenaReturned;
    private static long _poolArenaPooled;
    private static long _poolArenaOverCapDisposed;

    /// <summary>
    /// Records a rent of an <c>Arena</c> from the pool.
    /// <paramref name="fromPool"/> is <c>true</c> when an existing arena was
    /// dequeued for reuse; <c>false</c> when a fresh one was allocated.
    /// </summary>
    [Conditional("DATUM_DIAGNOSTICS")]
    public static void RecordPoolArenaRent(bool fromPool)
    {
        Interlocked.Increment(ref _poolArenaRented);
        if (fromPool) Interlocked.Increment(ref _poolArenaRentedFromPool);
    }

    /// <summary>
    /// Records a return of an <c>Arena</c> to the pool. <paramref name="pooled"/>
    /// indicates the arena was enqueued for reuse; <paramref name="disposedOverCap"/>
    /// indicates it was disposed because its capacity exceeded the pool cap.
    /// When both are <c>false</c>, the arena still had outstanding references
    /// and the return was a no-op.
    /// </summary>
    [Conditional("DATUM_DIAGNOSTICS")]
    public static void RecordPoolArenaReturn(bool pooled, bool disposedOverCap)
    {
        Interlocked.Increment(ref _poolArenaReturned);
        if (pooled) Interlocked.Increment(ref _poolArenaPooled);
        if (disposedOverCap) Interlocked.Increment(ref _poolArenaOverCapDisposed);
    }

    // ───────────────────────── Report / Reset ─────────────────────────

    /// <summary>
    /// Clears all counters to zero. Typically called once at the start of a
    /// measured operation (e.g. a query) so the final <see cref="Report"/>
    /// reflects only work done during that operation.
    /// </summary>
    [Conditional("DATUM_DIAGNOSTICS")]
    public static void Reset()
    {
        Interlocked.Exchange(ref _arenaGrowCount, 0);
        Interlocked.Exchange(ref _arenaResetCount, 0);
        Interlocked.Exchange(ref _arenaDisposeCount, 0);
        Interlocked.Exchange(ref _arenaMaxCapacity, 0);
        Interlocked.Exchange(ref _arenaBytesCopiedOnGrow, 0);
        Interlocked.Exchange(ref _arenaMmfCreateCount, 0);
        Interlocked.Exchange(ref _arenaMmfReleaseCount, 0);
        Interlocked.Exchange(ref _poolArenaRented, 0);
        Interlocked.Exchange(ref _poolArenaRentedFromPool, 0);
        Interlocked.Exchange(ref _poolArenaReturned, 0);
        Interlocked.Exchange(ref _poolArenaPooled, 0);
        Interlocked.Exchange(ref _poolArenaOverCapDisposed, 0);
    }

    /// <summary>
    /// Writes a human-readable snapshot of all counters to
    /// <paramref name="writer"/> (defaulting to <see cref="Console.Out"/>).
    /// Only emits output when <c>DATUM_DIAGNOSTICS</c> is defined; callers need
    /// no guard.
    /// </summary>
    [Conditional("DATUM_DIAGNOSTICS")]
    public static void Report(TextWriter? writer = null)
    {
        writer ??= Console.Out;
        writer.WriteLine("Diagnostics:");
        writer.WriteLine("  Arena lifecycle:");
        writer.WriteLine($"    grow events:           {_arenaGrowCount:N0}");
        writer.WriteLine($"    reset events:          {_arenaResetCount:N0}");
        writer.WriteLine($"    dispose events:        {_arenaDisposeCount:N0}");
        writer.WriteLine($"    max capacity observed: {FormatBytes(_arenaMaxCapacity)}");
        writer.WriteLine($"    bytes copied on grow:  {FormatBytes(_arenaBytesCopiedOnGrow)}");
        writer.WriteLine($"    mmf create / release:  {_arenaMmfCreateCount:N0} / {_arenaMmfReleaseCount:N0}");
        writer.WriteLine("  Pool (arena):");
        writer.WriteLine($"    rent  (total / from-pool): {_poolArenaRented:N0} / {_poolArenaRentedFromPool:N0}");
        writer.WriteLine($"    return (total / pooled / over-cap disposed): {_poolArenaReturned:N0} / {_poolArenaPooled:N0} / {_poolArenaOverCapDisposed:N0}");
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static void UpdateMax(ref long max, long candidate)
    {
        long current;
        do
        {
            current = Interlocked.Read(ref max);
            if (candidate <= current) return;
        } while (Interlocked.CompareExchange(ref max, candidate, current) != current);
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };
}
