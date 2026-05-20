using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Heliosoph.DatumV.Diagnostics;

/// <summary>
/// Bounded ring buffer of recently-completed <see cref="Activity"/> spans
/// from the engine's <see cref="DatumActivity"/> sources. Construct one
/// to start listening; <see cref="Snapshot()"/> returns the last
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
    // Monotonic per-entry sequence number assigned at enqueue time and
    // stored alongside each entry. Drives <see cref="DrainSince"/>: callers
    // hand back the cursor they last received and get only newer entries.
    // Never resets across the listener's lifetime so a wraparound takes
    // ~292 years at 1 billion entries/sec.
    private long _nextSequence;
    // Sequence of the most-recently evicted entry (highest seq dropped to
    // make room for a new one). DrainSince uses this to compute how many
    // entries the caller missed between their cursor and the oldest still-
    // present entry.
    private long _lastEvictedSequence = -1;
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
    /// <c>"Heliosoph.DatumV."</c>.
    /// </summary>
    public RecentActivityLog(int capacity = 100)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
        _ring = new Queue<RecentActivityEntry>(capacity);

        _listener = new ActivityListener
        {
            // Match every engine source — Operators, Scalars, and any future
            // sub-source under the Heliosoph.DatumV namespace. The string prefix
            // gate is the standard idiom for scoping a listener to your own
            // sources without intercepting third-party traffic.
            ShouldListenTo = src =>
                src.Name.StartsWith("Heliosoph.DatumV.", StringComparison.Ordinal),

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
        lock (_lock)
        {
            long seq = _nextSequence++;
            RecentActivityEntry ev = new(seq, a.DisplayName, a.Source.Name, a.Parent?.DisplayName, a.StartTimeUtc, a.Duration);
            _ring.Enqueue(ev);
            while (_ring.Count > Capacity)
            {
                // Track the highest evicted sequence so DrainSince can
                // report how many entries the caller missed between their
                // cursor and the oldest entry still in the ring.
                RecentActivityEntry evicted = _ring.Dequeue();
                if (evicted.Sequence > _lastEvictedSequence)
                    _lastEvictedSequence = evicted.Sequence;
            }
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
    /// Same as <see cref="Snapshot()"/> but filters to entries whose
    /// <see cref="RecentActivityEntry.SourceName"/> matches one of the
    /// supplied source names. Useful when scalar spans are wired and the
    /// operator-pull pattern would otherwise be drowned in per-row
    /// function spans.
    /// </summary>
    public RecentActivityEntry[] Snapshot(params string[] sourceNames)
    {
        if (sourceNames is null || sourceNames.Length == 0) return Snapshot();
        HashSet<string> filter = new(sourceNames, StringComparer.Ordinal);
        lock (_lock)
        {
            return _ring.Where(e => filter.Contains(e.SourceName)).ToArray();
        }
    }

    /// <summary>
    /// Incremental drain: returns every entry whose sequence is strictly
    /// greater than <paramref name="cursor"/>, plus the new cursor value
    /// the caller should pass on the next drain, plus the number of
    /// entries evicted from the ring between the caller's cursor and the
    /// oldest entry still present. Pass <c>-1</c> on the first call to
    /// receive every entry currently in the ring.
    /// </summary>
    /// <param name="cursor">The sequence number from the caller's previous drain (or -1 to start fresh).</param>
    /// <param name="sourceNames">
    /// Optional source-name filter. When non-empty, only entries whose
    /// <see cref="RecentActivityEntry.SourceName"/> matches one of the
    /// supplied names appear in the result — but the returned cursor and
    /// dropped count still reflect ALL entries (including filtered-out
    /// ones), so a downgrade from "all sources" to "operators only"
    /// doesn't leave the caller stuck re-receiving filtered entries.
    /// </param>
    public TraceDrainResult DrainSince(long cursor, params string[] sourceNames)
    {
        HashSet<string>? filter = sourceNames is { Length: > 0 }
            ? new HashSet<string>(sourceNames, StringComparer.Ordinal)
            : null;
        lock (_lock)
        {
            long nextCursor = cursor;
            int dropped = 0;
            // Count entries the caller missed: any sequence in (cursor,
            // oldestSeqInRing) is gone. We approximate by comparing the
            // caller's cursor to _lastEvictedSequence: every evicted
            // entry with seq > cursor and seq <= _lastEvictedSequence
            // is a drop the caller can never see.
            if (cursor < _lastEvictedSequence)
            {
                // Conservative lower bound — exact count would require
                // walking eviction history; the ring's high-watermark
                // delta is sufficient to render the "N entries dropped"
                // badge in the UI.
                dropped = (int)Math.Min(int.MaxValue, _lastEvictedSequence - cursor);
            }

            List<RecentActivityEntry> result = new();
            foreach (RecentActivityEntry e in _ring)
            {
                if (e.Sequence <= cursor) continue;
                if (e.Sequence > nextCursor) nextCursor = e.Sequence;
                if (filter is not null && !filter.Contains(e.SourceName)) continue;
                result.Add(e);
            }
            return new TraceDrainResult(result.ToArray(), nextCursor, dropped);
        }
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
            frames[i] = new ActivityFrame(a.DisplayName, a.Source.Name, a.Parent?.DisplayName, a.StartTimeUtc, now - a.StartTimeUtc);
        }
        Array.Sort(frames, static (x, y) => x.StartedAt.CompareTo(y.StartedAt));
        return frames;
    }

    /// <summary>
    /// Renders the completed-span ring as a multi-line text trace, oldest
    /// first. Each line reports the relative timestamp from the first
    /// recorded span, the source-prefixed display name, the parent's
    /// display name (when known), and the duration in milliseconds. Used
    /// by hosts (web app, probe) that need a human-readable trace blob
    /// to attach to a query response.
    /// </summary>
    public string FormatTrace()
    {
        RecentActivityEntry[] entries = Snapshot();
        if (entries.Length == 0) return string.Empty;

        StringBuilder sb = new();
        DateTimeOffset start = entries[0].StartedAt;
        foreach (RecentActivityEntry e in entries)
        {
            double offsetMs = (e.StartedAt - start).TotalMilliseconds;
            string sourceTag = e.SourceName switch
            {
                "Heliosoph.DatumV.Operators" => "op",
                "Heliosoph.DatumV.Scalars" => "fn",
                _ => e.SourceName,
            };
            sb.Append('[').Append(offsetMs.ToString("F2")).Append("ms ").Append(sourceTag).Append("] ");
            sb.Append(e.Name);
            if (e.ParentName is not null) sb.Append(" ⇐ ").Append(e.ParentName);
            sb.Append("  ").Append(e.Duration.TotalMilliseconds.ToString("F2")).AppendLine(" ms");
        }
        return sb.ToString();
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
/// a monotonic sequence number assigned at enqueue time (used by
/// <see cref="RecentActivityLog.DrainSince"/> as a cursor), the operator
/// name, the <see cref="ActivitySource"/> it came from
/// (<c>"Heliosoph.DatumV.Operators"</c> vs <c>"Heliosoph.DatumV.Scalars"</c>), the
/// display name of its parent activity at start (so callers can render the
/// pull chain), when it started, and how long it ultimately ran.
/// </summary>
public readonly record struct RecentActivityEntry(
    long Sequence,
    string Name,
    string SourceName,
    string? ParentName,
    DateTimeOffset StartedAt,
    TimeSpan Duration);

/// <summary>
/// Result of a <see cref="RecentActivityLog.DrainSince"/> call.
/// </summary>
/// <param name="Entries">New entries (oldest first) in the requested source set.</param>
/// <param name="Cursor">Pass this back on the next drain to receive only newer entries.</param>
/// <param name="Dropped">
/// Approximate count of entries the caller's cursor was behind by — i.e.
/// entries that aged out of the ring before this drain could see them.
/// Lets the host surface a "trace overflow, N events lost" badge.
/// </param>
public readonly record struct TraceDrainResult(
    RecentActivityEntry[] Entries,
    long Cursor,
    int Dropped);
