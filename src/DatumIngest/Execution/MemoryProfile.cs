namespace Heliosoph.DatumV.Execution;

/// <summary>
/// A single point in a query's memory-residency timeline. Captured by
/// <see cref="MemoryAccountant.Sample"/> at 1Hz (or on demand) and stored in
/// the owning <see cref="MemoryProfile"/>.
/// </summary>
/// <param name="ElapsedMs">Milliseconds since the accountant was constructed.</param>
/// <param name="RowBytes">In-RAM residency reported via
/// <see cref="MemoryAccountant.NotifyMaterialized"/> /
/// <see cref="MemoryAccountant.NotifyReleased"/>: DataValue arrays, dictionary
/// buckets, managed payloads held by VariableScope, hash-table state in
/// materializing operators. The number the spill budget compares against.</param>
/// <param name="ArenaBytes">Bytes written into the primary <see cref="Model.Arena"/>
/// associated with this accountant. Anonymous and file-backed arenas alike are
/// <c>MemoryMappedFile</c>-backed, so these bytes are OS-paged and do NOT
/// count against the spill budget — recorded only for diagnostics.</param>
public readonly record struct MemorySample(long ElapsedMs, long RowBytes, long ArenaBytes);

/// <summary>
/// Time-ordered series of <see cref="MemorySample"/>s produced by a
/// <see cref="MemoryAccountant"/>. Two read paths:
/// <list type="bullet">
///   <item><see cref="Latest"/> — most recent sample, intended for live UI
///   polling (memory-pressure sparkline next to a streaming result pane).</item>
///   <item><see cref="Snapshot"/> — full series, intended for post-mortem
///   inspection (the EXPLAIN-style memory graph of a completed query).</item>
/// </list>
/// </summary>
/// <remarks>
/// Sampling cadence is 1Hz so the sample list is small (≈24 bytes × 3600 / hour ≈
/// 85 KB / hour) and unbounded growth is acceptable for v1. Continuous queries
/// will need a ring-buffer cap; deferred.
/// </remarks>
public sealed class MemoryProfile
{
    private readonly List<MemorySample> _samples = [];
    private readonly Lock _lock = new();
    private MemorySample _latest;

    /// <summary>Most recent sample, or <c>default</c> when no samples have been recorded.</summary>
    public MemorySample Latest
    {
        get { lock (_lock) return _latest; }
    }

    /// <summary>Appends a sample. Thread-safe.</summary>
    public void Append(MemorySample sample)
    {
        lock (_lock)
        {
            _samples.Add(sample);
            _latest = sample;
        }
    }

    /// <summary>Returns a stable copy of every sample recorded so far, in append order.</summary>
    public IReadOnlyList<MemorySample> Snapshot()
    {
        lock (_lock)
        {
            return _samples.ToArray();
        }
    }
}
