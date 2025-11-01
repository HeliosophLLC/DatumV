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

    // ───────────────────────── Pool DataValue[] counters ─────────────────────────

    private static long _poolDvArrayRented;
    private static long _poolDvArrayRentedFromPool;
    private static long _poolDvArrayReturned;
    private static long _poolDvArrayPooled;
    private static long _poolDvArrayOverLimitDiscarded;

    /// <summary>Records a <c>DataValue[]</c> rent from the exact-length pool.</summary>
    [Conditional("DATUM_DIAGNOSTICS")]
    public static void RecordPoolDataValueArrayRent(bool fromPool)
    {
        Interlocked.Increment(ref _poolDvArrayRented);
        if (fromPool) Interlocked.Increment(ref _poolDvArrayRentedFromPool);
    }

    /// <summary>Records a <c>DataValue[]</c> return. <paramref name="pooled"/> is true when the
    /// buffer was enqueued; false when the per-bucket cap rejected it and the buffer will be GC'd.</summary>
    [Conditional("DATUM_DIAGNOSTICS")]
    public static void RecordPoolDataValueArrayReturn(bool pooled)
    {
        Interlocked.Increment(ref _poolDvArrayReturned);
        if (pooled) Interlocked.Increment(ref _poolDvArrayPooled);
        else Interlocked.Increment(ref _poolDvArrayOverLimitDiscarded);
    }

    // ───────────────────────── Pool RowBatch / ColumnBatch counters ─────────────────────────

    private static long _poolRowBatchRented;
    private static long _poolRowBatchReturned;
    private static long _poolColumnBatchRented;
    private static long _poolColumnBatchReturned;

    /// <summary>Records a <c>RowBatch</c> rent (always newly constructed today; counter
    /// tracks construction rate rather than pool-hit rate).</summary>
    [Conditional("DATUM_DIAGNOSTICS")]
    public static void RecordPoolRowBatchRent() => Interlocked.Increment(ref _poolRowBatchRented);

    /// <summary>Records a <c>RowBatch</c> return.</summary>
    [Conditional("DATUM_DIAGNOSTICS")]
    public static void RecordPoolRowBatchReturn() => Interlocked.Increment(ref _poolRowBatchReturned);

    /// <summary>Records a <c>ColumnBatch</c> rent.</summary>
    [Conditional("DATUM_DIAGNOSTICS")]
    public static void RecordPoolColumnBatchRent() => Interlocked.Increment(ref _poolColumnBatchRented);

    /// <summary>Records a <c>ColumnBatch</c> return.</summary>
    [Conditional("DATUM_DIAGNOSTICS")]
    public static void RecordPoolColumnBatchReturn() => Interlocked.Increment(ref _poolColumnBatchReturned);

    // ───────────────────────── Pool GroupState counters ─────────────────────────

    private static long _poolGroupStateRented;
    private static long _poolGroupStateRentedFromPool;
    private static long _poolGroupStateReturned;
    private static long _poolGroupStatePooled;
    private static long _poolAccumulatorArrayRented;
    private static long _poolAccumulatorArrayRentedFromPool;
    private static long _poolAccumulatorArrayReturned;
    private static long _poolAccumulatorArrayPooled;

    /// <summary>Records a <c>GroupState</c> rent from the per-accumulator-count bucket.</summary>
    [Conditional("DATUM_DIAGNOSTICS")]
    public static void RecordPoolGroupStateRent(bool fromPool)
    {
        Interlocked.Increment(ref _poolGroupStateRented);
        if (fromPool) Interlocked.Increment(ref _poolGroupStateRentedFromPool);
    }

    /// <summary>Records a <c>GroupState</c> return; <paramref name="pooled"/> is true when the
    /// per-bucket cap admitted the instance.</summary>
    [Conditional("DATUM_DIAGNOSTICS")]
    public static void RecordPoolGroupStateReturn(bool pooled)
    {
        Interlocked.Increment(ref _poolGroupStateReturned);
        if (pooled) Interlocked.Increment(ref _poolGroupStatePooled);
    }

    /// <summary>Records an <c>IAggregateAccumulator[]</c> rent.</summary>
    [Conditional("DATUM_DIAGNOSTICS")]
    public static void RecordPoolAccumulatorArrayRent(bool fromPool)
    {
        Interlocked.Increment(ref _poolAccumulatorArrayRented);
        if (fromPool) Interlocked.Increment(ref _poolAccumulatorArrayRentedFromPool);
    }

    /// <summary>Records an <c>IAggregateAccumulator[]</c> return.</summary>
    [Conditional("DATUM_DIAGNOSTICS")]
    public static void RecordPoolAccumulatorArrayReturn(bool pooled)
    {
        Interlocked.Increment(ref _poolAccumulatorArrayReturned);
        if (pooled) Interlocked.Increment(ref _poolAccumulatorArrayPooled);
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

        Interlocked.Exchange(ref _poolDvArrayRented, 0);
        Interlocked.Exchange(ref _poolDvArrayRentedFromPool, 0);
        Interlocked.Exchange(ref _poolDvArrayReturned, 0);
        Interlocked.Exchange(ref _poolDvArrayPooled, 0);
        Interlocked.Exchange(ref _poolDvArrayOverLimitDiscarded, 0);

        Interlocked.Exchange(ref _poolRowBatchRented, 0);
        Interlocked.Exchange(ref _poolRowBatchReturned, 0);
        Interlocked.Exchange(ref _poolColumnBatchRented, 0);
        Interlocked.Exchange(ref _poolColumnBatchReturned, 0);

        Interlocked.Exchange(ref _poolGroupStateRented, 0);
        Interlocked.Exchange(ref _poolGroupStateRentedFromPool, 0);
        Interlocked.Exchange(ref _poolGroupStateReturned, 0);
        Interlocked.Exchange(ref _poolGroupStatePooled, 0);
        Interlocked.Exchange(ref _poolAccumulatorArrayRented, 0);
        Interlocked.Exchange(ref _poolAccumulatorArrayRentedFromPool, 0);
        Interlocked.Exchange(ref _poolAccumulatorArrayReturned, 0);
        Interlocked.Exchange(ref _poolAccumulatorArrayPooled, 0);
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
        writer.WriteLine($"    rent  (total / from-pool): {_poolArenaRented:N0} / {_poolArenaRentedFromPool:N0} ({HitRate(_poolArenaRentedFromPool, _poolArenaRented)})");
        writer.WriteLine($"    return (total / pooled / over-cap disposed): {_poolArenaReturned:N0} / {_poolArenaPooled:N0} / {_poolArenaOverCapDisposed:N0}");
        writer.WriteLine("  Pool (DataValue[]):");
        writer.WriteLine($"    rent  (total / from-pool): {_poolDvArrayRented:N0} / {_poolDvArrayRentedFromPool:N0} ({HitRate(_poolDvArrayRentedFromPool, _poolDvArrayRented)})");
        writer.WriteLine($"    return (total / pooled / over-limit discarded): {_poolDvArrayReturned:N0} / {_poolDvArrayPooled:N0} / {_poolDvArrayOverLimitDiscarded:N0}");
        writer.WriteLine("  Pool (RowBatch):");
        writer.WriteLine($"    rent / return: {_poolRowBatchRented:N0} / {_poolRowBatchReturned:N0}");
        writer.WriteLine("  Pool (ColumnBatch):");
        writer.WriteLine($"    rent / return: {_poolColumnBatchRented:N0} / {_poolColumnBatchReturned:N0}");
        writer.WriteLine("  Pool (GroupState):");
        writer.WriteLine($"    rent  (total / from-pool): {_poolGroupStateRented:N0} / {_poolGroupStateRentedFromPool:N0} ({HitRate(_poolGroupStateRentedFromPool, _poolGroupStateRented)})");
        writer.WriteLine($"    return (total / pooled): {_poolGroupStateReturned:N0} / {_poolGroupStatePooled:N0}");
        writer.WriteLine("  Pool (Accumulator[]):");
        writer.WriteLine($"    rent  (total / from-pool): {_poolAccumulatorArrayRented:N0} / {_poolAccumulatorArrayRentedFromPool:N0} ({HitRate(_poolAccumulatorArrayRentedFromPool, _poolAccumulatorArrayRented)})");
        writer.WriteLine($"    return (total / pooled): {_poolAccumulatorArrayReturned:N0} / {_poolAccumulatorArrayPooled:N0}");
    }

    private static string HitRate(long hits, long total)
    {
        if (total == 0) return "n/a";
        double pct = 100.0 * hits / total;
        return $"{pct:F1}% hit";
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
