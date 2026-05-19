using System.Diagnostics;

namespace Heliosoph.DatumV.Execution;

/// <summary>
/// Plan-wide memory accounting for a query (or a procedural batch). Owns:
/// <list type="bullet">
///   <item>A residency counter (<see cref="CurrentResidentBytes"/>) updated via
///   <see cref="NotifyMaterialized"/> / <see cref="NotifyReleased"/>. This is
///   the in-RAM byte total that <see cref="WouldExceedBudget"/> checks against
///   the configured <see cref="MemoryBudgetBytes"/>.</item>
///   <item>An optional <see cref="MemoryProfile"/> filled at 1Hz once
///   <see cref="StartProfiling"/> is invoked. The same instance is exposed via
///   <see cref="Profile"/> so a live UI can poll <see cref="MemoryProfile.Latest"/>
///   and a post-mortem inspector can pull the full <see cref="MemoryProfile.Snapshot"/>.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <strong>One accountant per query or per batch.</strong> Standalone queries
/// have their <see cref="ExecutionContext"/> construct and own one. Procedural
/// batches own one at the outer <see cref="ExecutionContext"/> and child
/// derived contexts borrow. This lets every
/// materializing operator in a plan, every <see cref="VariableScope"/>-bound
/// payload, and every DML buffer report into the same counter — so "I'm
/// already over budget at operator entry, spill immediately" works.
/// </para>
/// <para>
/// <strong>Arena bytes don't count.</strong> Both anonymous and file-backed
/// arenas are <see cref="System.IO.MemoryMappedFiles.MemoryMappedFile"/>-backed,
/// so the OS pages their bytes out under pressure — they don't compete with
/// the GC-pinned heap that spilling can relieve. <see cref="Profile"/> records
/// them in <see cref="MemorySample.ArenaBytes"/> for diagnostics; the spill
/// decision considers only <see cref="MemorySample.RowBytes"/>.
/// </para>
/// </remarks>
public sealed class MemoryAccountant : IDisposable
{
    /// <summary>
    /// Default memory budget when a caller doesn't supply one: <c>min(half-RAM, 4 GB)</c>.
    /// Computed once at type initialisation from <c>GC.GetGCMemoryInfo()</c>'s
    /// <c>TotalAvailableMemoryBytes</c> (the process-visible RAM ceiling, which respects
    /// container limits / cgroups).
    /// </summary>
    /// <remarks>
    /// Historically <see cref="MemoryBudgetBytes"/> defaulted to <see langword="null"/>
    /// (= "no budget, never spill"). That had the side effect that any test or in-process
    /// caller that didn't set a budget silently bypassed every spill-aware code path —
    /// hiding real bugs in <see cref="Heliosoph.DatumV.Execution.GraceHashJoinExecutor"/> and
    /// friends until they hit production. We now always have a real budget; callers that
    /// genuinely want unbounded memory must pass <see cref="long.MaxValue"/> explicitly
    /// to make that intent visible at the call site.
    /// </remarks>
    public static readonly long DefaultMemoryBudgetBytes = ComputeDefaultMemoryBudgetBytes();

    private static long ComputeDefaultMemoryBudgetBytes()
    {
        long halfRam = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 2;
        const long fourGB = 4L * 1024 * 1024 * 1024;
        return Math.Min(halfRam, fourGB);
    }

    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly Func<long>? _arenaBytesProbe;
    private long _residentBytes;
    private long _peakResidentBytes;
    private Timer? _samplingTimer;
    private int _disposed;

    /// <summary>
    /// Creates a new accountant.
    /// </summary>
    /// <param name="memoryBudgetBytes">Spill-trigger threshold for
    /// <see cref="WouldExceedBudget"/>. <c>null</c> disables the budget check —
    /// the residency counter still tracks bytes for profiling.</param>
    /// <param name="arenaBytesProbe">Returns the current primary-arena byte
    /// count at sample time. Pass <c>() =&gt; context.Store.BytesWritten</c>
    /// for a standalone query or  procedural batch. <c>null</c> records
    /// zero arena bytes in samples.
    /// </param>
    public MemoryAccountant(long? memoryBudgetBytes = null, Func<long>? arenaBytesProbe = null)
    {
        MemoryBudgetBytes = memoryBudgetBytes ?? DefaultMemoryBudgetBytes;
        _arenaBytesProbe = arenaBytesProbe;
        Profile = new MemoryProfile();
    }

    /// <summary>Spill-trigger threshold. <c>null</c> disables the budget check.</summary>
    public long? MemoryBudgetBytes { get; }

    /// <summary>Sample series. Always available; populated on
    /// <see cref="Sample"/> or by the 1Hz timer once <see cref="StartProfiling"/>
    /// runs.</summary>
    public MemoryProfile Profile { get; }

    /// <summary>Current in-RAM residency in bytes.</summary>
    public long CurrentResidentBytes => Interlocked.Read(ref _residentBytes);

    /// <summary>
    /// Highest <see cref="CurrentResidentBytes"/> reached since this accountant
    /// was constructed. Updated lazily on every <see cref="NotifyMaterialized"/> —
    /// transient peaks between matched materialize/release pairs are captured
    /// even when the 1Hz sampler missed them. Used by tests and post-mortem
    /// inspectors that want a single "how high did this query go" number.
    /// </summary>
    public long PeakResidentBytes => Interlocked.Read(ref _peakResidentBytes);

    /// <summary>
    /// Returns <c>true</c> if applying <paramref name="pendingDelta"/> would
    /// push residency past <see cref="MemoryBudgetBytes"/>. Callers consult
    /// this to decide whether to spill before adding the next row to a
    /// materialised structure.
    /// </summary>
    public bool WouldExceedBudget(long pendingDelta = 0)
        => MemoryBudgetBytes is long b && CurrentResidentBytes + pendingDelta > b;

    /// <summary>
    /// Reports a held-state increase. Operators call this on hash-table
    /// insert, sort-buffer append, materialised-CTE cache add, etc.
    /// Updates <see cref="PeakResidentBytes"/> when the new total exceeds the
    /// prior peak.
    /// </summary>
    public void NotifyMaterialized(long deltaBytes)
    {
        long current = Interlocked.Add(ref _residentBytes, deltaBytes);
        // Lock-free max-update: read the prior peak, attempt CAS while the
        // new total is higher. Loops at most as many times as concurrent
        // bumps observed in the window.
        long oldPeak = Volatile.Read(ref _peakResidentBytes);
        while (current > oldPeak)
        {
            long witnessed = Interlocked.CompareExchange(
                ref _peakResidentBytes, current, oldPeak);
            if (witnessed == oldPeak) break;
            oldPeak = witnessed;
        }
    }

    /// <summary>
    /// Reports a held-state release. Pair with <see cref="NotifyMaterialized"/>
    /// when a hash table partition flushes, an operator drains its buffer, a
    /// <see cref="VariableScope"/> frame pops.
    /// </summary>
    public void NotifyReleased(long deltaBytes)
        => Interlocked.Add(ref _residentBytes, -deltaBytes);

    /// <summary>
    /// Starts the 1Hz sampling timer. Idempotent — second call is a no-op.
    /// Production query paths call this immediately after constructing or
    /// borrowing the accountant; tests typically omit it and invoke
    /// <see cref="Sample"/> directly when they want a sample.
    /// </summary>
    public void StartProfiling()
    {
        if (_samplingTimer is not null) return;
        _samplingTimer = new Timer(_ => Sample(), state: null, dueTime: 1000, period: 1000);
    }

    /// <summary>
    /// Appends one sample to <see cref="Profile"/> using the current
    /// residency counter and the constructor-supplied arena probe. Safe to
    /// call from any thread; safe to call after <see cref="Dispose"/>.
    /// </summary>
    public void Sample()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        long arena = _arenaBytesProbe?.Invoke() ?? 0;
        Profile.Append(new MemorySample(
            _stopwatch.ElapsedMilliseconds,
            CurrentResidentBytes,
            arena));
    }

    /// <summary>Stops the sampling timer. Idempotent.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _samplingTimer?.Dispose();
        _samplingTimer = null;
    }
}
