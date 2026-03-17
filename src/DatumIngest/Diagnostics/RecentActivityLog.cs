using System.Collections.Concurrent;
using System.Diagnostics;

namespace DatumIngest.Diagnostics;

/// <summary>
/// Bounded ring buffer of recently-completed <see cref="Activity"/> spans
/// from the engine's <see cref="DatumActivity"/> sources. Construct one
/// to start listening; <see cref="Snapshot"/> returns the last
/// <see cref="Capacity"/> events newest-first. Dispose to stop listening.
/// </summary>
/// <remarks>
/// <para>
/// Listening has a non-zero cost: every <c>StartActivity</c> on a matched
/// source allocates an <see cref="Activity"/> and ferries it through this
/// listener's <c>ActivityStopped</c> callback at the end of its scope.
/// For operator-level spans this is negligible (one allocation per batch).
/// If you also wrap scalar dispatches with <see cref="DatumActivity.Scalars"/>,
/// expect a per-row allocation — fine for debugging, not for sustained
/// production loads.
/// </para>
/// <para>
/// Constructing more than one instance is safe — each attaches its own
/// <see cref="ActivityListener"/>. Spans flow through all of them.
/// </para>
/// </remarks>
public sealed class RecentActivityLog : IDisposable
{
    private readonly Queue<RecentActivityEntry> _ring;
    private readonly object _lock = new();
    private readonly ActivityListener _listener;
    // Tracks every Activity currently open (started but not yet stopped).
    // ActivityListener's Started/Stopped callbacks fire on the thread that
    // created/closed the Activity — those threads have the AsyncLocal
    // context set. We mirror that into this ConcurrentDictionary so any
    // OTHER thread (e.g. a Ctrl+C handler running on the console control
    // thread, an admin endpoint thread, a diagnostic timer) can ask
    // "what's open right now" without needing to be in the async flow.
    // The bool value is unused — ConcurrentDictionary is the only built-in
    // concurrent set type, hence the byte-payload idiom.
    private readonly ConcurrentDictionary<Activity, byte> _live = new();
    private bool _disposed;

    /// <summary>
    /// Maximum number of events retained. The oldest event is evicted when
    /// a new one would push past the cap.
    /// </summary>
    public int Capacity { get; }

    /// <summary>
    /// Creates a listener that records the <paramref name="capacity"/> most
    /// recently completed engine activities. Subscribes to every
    /// <see cref="ActivitySource"/> whose name starts with
    /// <c>"DatumIngest."</c>.
    /// </summary>
    public RecentActivityLog(int capacity = 100)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
        _ring = new Queue<RecentActivityEntry>(capacity);

        _listener = new ActivityListener
        {
            // Match every engine source — Operators, Scalars, and any future
            // sub-source under the DatumIngest namespace. The string prefix
            // gate is the standard idiom for scoping a listener to your own
            // sources without intercepting third-party traffic.
            ShouldListenTo = src =>
                src.Name.StartsWith("DatumIngest.", StringComparison.Ordinal),

            // AllData makes every StartActivity return a non-null Activity
            // (otherwise the sampler suppresses creation and we record
            // nothing). The cost is one Activity allocation per StartActivity
            // call, traded for visibility.
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,

            ActivityStarted = a => _live.TryAdd(a, 0),
            ActivityStopped = OnStopped,
        };

        ActivitySource.AddActivityListener(_listener);
    }

    private void OnStopped(Activity a)
    {
        if (_disposed) return;
        _live.TryRemove(a, out _);
        RecentActivityEntry ev = new(a.DisplayName, a.StartTimeUtc, a.Duration);
        lock (_lock)
        {
            _ring.Enqueue(ev);
            while (_ring.Count > Capacity) _ring.Dequeue();
        }
    }

    /// <summary>
    /// Returns a snapshot of the ring buffer, oldest-first. The returned
    /// array is a copy — safe to enumerate without holding the lock.
    /// </summary>
    public RecentActivityEntry[] Snapshot()
    {
        lock (_lock) return _ring.ToArray();
    }

    /// <summary>
    /// Returns every <see cref="Activity"/> currently open (started, not
    /// yet stopped) on this listener's sources. Safe to call from any
    /// thread — uses the cross-thread live registry, not the AsyncLocal-
    /// scoped <see cref="Activity.Current"/>. Frames are ordered by
    /// start time, oldest-first (the root operator first, the deepest
    /// in-flight operator last).
    /// </summary>
    public ActivityFrame[] LiveSnapshot()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        // Materialise into an array, then sort. We sort by start time so
        // the output reads like a stack (oldest = root = top of the chain;
        // newest = leaf = where execution is parked right now).
        Activity[] keys = _live.Keys.ToArray();
        ActivityFrame[] frames = new ActivityFrame[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            Activity a = keys[i];
            frames[i] = new ActivityFrame(a.DisplayName, a.StartTimeUtc, now - a.StartTimeUtc);
        }
        Array.Sort(frames, static (x, y) => x.StartedAt.CompareTo(y.StartedAt));
        return frames;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _listener.Dispose();
    }
}

/// <summary>
/// One completed-span entry from <see cref="RecentActivityLog"/>. Captures
/// the operator name, when it started, and how long it ultimately ran.
/// </summary>
public readonly record struct RecentActivityEntry(string Name, DateTimeOffset StartedAt, TimeSpan Duration);
