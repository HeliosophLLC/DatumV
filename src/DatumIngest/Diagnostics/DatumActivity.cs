using System.Diagnostics;

namespace DatumIngest.Diagnostics;

/// <summary>
/// Engine-wide <see cref="ActivitySource"/> registry plus helpers for
/// inspecting the live execution state. Built on
/// <see cref="System.Diagnostics.Activity"/> (an <see cref="System.Threading.AsyncLocal{T}"/>-backed
/// scope) so any code path can ask "where am I right now" without
/// threading a context through call signatures, and so external tools
/// (<c>dotnet-trace</c>, PerfView, OpenTelemetry) can subscribe to the
/// same spans without any engine-side glue.
/// </summary>
/// <remarks>
/// Zero overhead when no listener is attached: <see cref="ActivitySource.StartActivity(string, ActivityKind)"/>
/// returns <see langword="null"/> in that case, and the <c>using</c>
/// in the call sites is a no-op on null. Attaching a listener (e.g.
/// constructing a <see cref="RecentActivityLog"/> or running
/// <c>dotnet-trace</c>) flips every <c>StartActivity</c> to allocate a
/// real <see cref="Activity"/>.
/// </remarks>
public static class DatumActivity
{
    /// <summary>
    /// Spans for query-plan operators (one per <c>ExecuteAsync</c> call).
    /// Subscribers receive nested operator-level spans whose
    /// <see cref="Activity.Duration"/> is wall-clock time from
    /// <c>ExecuteAsync</c> entry until the iterator is disposed (which
    /// includes time the operator spends suspended waiting for a pull).
    /// </summary>
    public static readonly ActivitySource Operators = new("DatumIngest.Operators");

    /// <summary>
    /// Spans for scalar-function / expression-evaluation work. Separated
    /// from <see cref="Operators"/> so callers can subscribe to one without
    /// paying the per-call allocation cost of the other — scalar dispatch
    /// fires at row granularity, operator dispatch fires at batch granularity.
    /// </summary>
    public static readonly ActivitySource Scalars = new("DatumIngest.Scalars");

    /// <summary>
    /// Walks <see cref="Activity.Current"/> up through <see cref="Activity.Parent"/>
    /// and returns the live operator stack, leaf first. Useful from
    /// debugger watch windows, exception handlers, or admin endpoints
    /// to capture a "where was the engine when this happened" snapshot.
    /// Returns an empty list when no listener is attached (and therefore
    /// no <see cref="Activity"/> objects have been created).
    /// </summary>
    public static IReadOnlyList<ActivityFrame> CurrentStack()
    {
        List<ActivityFrame> frames = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (Activity? a = Activity.Current; a is not null; a = a.Parent)
        {
            frames.Add(new ActivityFrame(a.DisplayName, a.StartTimeUtc, now - a.StartTimeUtc));
        }
        return frames;
    }
}

/// <summary>
/// A single frame from <see cref="DatumActivity.CurrentStack"/>. Captures
/// the operator name, when it started, and how long it has been running
/// at the moment the snapshot was taken (i.e., open-span duration, not
/// final duration).
/// </summary>
public readonly record struct ActivityFrame(string Name, DateTimeOffset StartedAt, TimeSpan Elapsed);
