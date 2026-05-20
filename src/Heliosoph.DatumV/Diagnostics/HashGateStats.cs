using System.Threading;

namespace Heliosoph.DatumV.Diagnostics;

/// <summary>
/// Diagnostic counters for the per-DataValue hash optimization. Off by default;
/// callers (benchmarks, probes) flip <see cref="Enabled"/> to <c>true</c> before
/// the measured region and read counters after.
/// </summary>
/// <remarks>
/// The counters distinguish "did the optimization fire" from "is the wall-clock
/// timing noisy". Expected pattern: before the redesign,
/// <see cref="HashShortCircuitHits"/> is always zero and
/// <see cref="FullByteCompareCount"/> / <see cref="SidecarFetchForCompare"/>
/// account for every set-membership comparison; after the redesign, the
/// short-circuit count rises and the sidecar-fetch count drops toward zero
/// for the scenarios the optimization targets.
/// </remarks>
public static class HashGateStats
{
    private static long _hashShortCircuitHits;
    private static long _fullByteCompareCount;
    private static long _sidecarFetchForCompare;

    /// <summary>
    /// When <c>false</c> (the default) all <c>Record*</c> methods short-circuit
    /// without touching the counter fields, so this diagnostic adds no overhead
    /// to non-benchmark runs.
    /// </summary>
    public static bool Enabled { get; set; }

    /// <summary>Number of comparisons resolved by a hash mismatch alone (no byte compare).</summary>
    public static long HashShortCircuitHits => Volatile.Read(ref _hashShortCircuitHits);

    /// <summary>Number of full byte-level equality checks performed.</summary>
    public static long FullByteCompareCount => Volatile.Read(ref _fullByteCompareCount);

    /// <summary>Number of sidecar reads issued for a comparison or rehydration.</summary>
    public static long SidecarFetchForCompare => Volatile.Read(ref _sidecarFetchForCompare);

    /// <summary>Records that a comparison was resolved entirely by a hash mismatch.</summary>
    public static void RecordHashShortCircuit()
    {
        if (Enabled) Interlocked.Increment(ref _hashShortCircuitHits);
    }

    /// <summary>Records that a comparison fell through to a full byte-equal check.</summary>
    public static void RecordFullByteCompare()
    {
        if (Enabled) Interlocked.Increment(ref _fullByteCompareCount);
    }

    /// <summary>Records one sidecar fetch attributable to a comparison or rehydration.</summary>
    public static void RecordSidecarFetch()
    {
        if (Enabled) Interlocked.Increment(ref _sidecarFetchForCompare);
    }

    /// <summary>
    /// Resets all counters to zero and disables further recording. Benchmarks call
    /// this in <c>[IterationSetup]</c>, then set <see cref="Enabled"/> for the
    /// measured region only.
    /// </summary>
    public static void Reset()
    {
        Volatile.Write(ref _hashShortCircuitHits, 0);
        Volatile.Write(ref _fullByteCompareCount, 0);
        Volatile.Write(ref _sidecarFetchForCompare, 0);
        Enabled = false;
    }
}
