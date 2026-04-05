using System.Diagnostics;
using System.Runtime.CompilerServices;

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
    /// Spans for model-calibration lifecycle work — store load/save,
    /// weight-cost measurements, calibration ramp passes, validation
    /// drift events. Distinct source from <see cref="Operators"/> so the
    /// (relatively rare) calibration trail can be filtered to a dedicated
    /// listener without pulling in operator-level chatter.
    /// </summary>
    public static readonly ActivitySource Calibration = new("DatumIngest.Calibration");

    /// <summary>
    /// Formats a byte count as a human-readable string (B / KB / MB / GB).
    /// Convenience helper for trace messages that report memory budgets.
    /// </summary>
    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
        >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        >= 1024L => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes} B",
    };

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
            frames.Add(new ActivityFrame(a.DisplayName, a.Source.Name, a.Parent?.DisplayName, a.StartTimeUtc, now - a.StartTimeUtc));
        }
        return frames;
    }
}

/// <summary>
/// A single frame from <see cref="DatumActivity.CurrentStack"/> or
/// <see cref="RecentActivityLog.LiveSnapshot"/>. Captures the operator
/// name, the <see cref="ActivitySource"/> it came from
/// (<c>"DatumIngest.Operators"</c> vs <c>"DatumIngest.Scalars"</c>), the
/// display name of the parent activity (so callers can reconstruct the
/// pull chain across threads), and the moment the activity started plus
/// elapsed time at snapshot.
/// </summary>
public readonly record struct ActivityFrame(
    string Name,
    string SourceName,
    string? ParentName,
    DateTimeOffset StartedAt,
    TimeSpan Elapsed);

/// <summary>
/// Extensions on <see cref="ActivitySource"/> for emitting one-shot trace
/// messages as zero-duration spans. Replaces ad-hoc tracer write calls:
/// the formatted message becomes the span's display name so it appears
/// in <see cref="RecentActivityLog.Snapshot()"/> alongside the surrounding
/// operator and scalar spans, and standard trace consumers
/// (<c>dotnet-trace</c>, OpenTelemetry exporters) pick it up via the
/// usual <see cref="ActivityListener"/> mechanism.
/// </summary>
/// <remarks>
/// The interpolated-string handler short-circuits when no listener is
/// attached, so the formatting cost is paid only when something is
/// actually subscribed. Sites that need to capture state for the message
/// (e.g. compute <see cref="GC.GetTotalMemory"/> first) should still guard
/// with <see cref="ActivitySource.HasListeners"/> explicitly because
/// argument evaluation precedes the handler.
/// </remarks>
public static class ActivitySourceTraceExtensions
{
    /// <summary>
    /// Emits a one-shot trace message as a zero-duration span. No-op when
    /// the source has no listeners.
    /// </summary>
    public static void Trace(
        this ActivitySource source,
        [InterpolatedStringHandlerArgument(nameof(source))] ref TraceMessageInterpolatedHandler message)
    {
        if (!message.IsEnabled) return;
        Activity? span = source.StartActivity(message.ToStringAndClear());
        span?.Dispose();
    }

    /// <summary>
    /// Non-interpolated overload: emits the supplied static message as a
    /// zero-duration span. The interpolated handler isn't used for plain
    /// string literals because the C# compiler binds to the most specific
    /// overload, so this overload exists explicitly to keep
    /// <c>source.Trace("static text")</c> call sites compilable.
    /// </summary>
    public static void Trace(this ActivitySource source, string message)
    {
        if (!source.HasListeners()) return;
        Activity? span = source.StartActivity(message);
        span?.Dispose();
    }
}

/// <summary>
/// Interpolated-string handler used by
/// <see cref="ActivitySourceTraceExtensions.Trace(ActivitySource, ref TraceMessageInterpolatedHandler)"/>
/// to defer message formatting until after the listener check has confirmed
/// at least one consumer is attached. When the source has no listeners,
/// <see cref="IsEnabled"/> is <see langword="false"/> and every
/// <c>Append*</c> call is a no-op — zero allocation, zero formatting.
/// </summary>
[InterpolatedStringHandler]
public ref struct TraceMessageInterpolatedHandler
{
    private DefaultInterpolatedStringHandler _inner;

    /// <summary>
    /// <see langword="true"/> when the source had at least one listener at
    /// construction time. Append calls short-circuit when this is false.
    /// </summary>
    public bool IsEnabled { get; }

    /// <summary>Compiler-emitted constructor: caller passes literal-length,
    /// formatted-count, and the trace source whose listener state gates
    /// formatting work.</summary>
    public TraceMessageInterpolatedHandler(int literalLength, int formattedCount, ActivitySource source)
    {
        IsEnabled = source.HasListeners();
        if (IsEnabled)
        {
            _inner = new DefaultInterpolatedStringHandler(literalLength, formattedCount);
        }
    }

    /// <summary>Appends a literal string segment from the interpolated template.</summary>
    public void AppendLiteral(string value)
    {
        if (IsEnabled) _inner.AppendLiteral(value);
    }

    /// <summary>Appends a formatted interpolation hole.</summary>
    public void AppendFormatted<T>(T value)
    {
        if (IsEnabled) _inner.AppendFormatted(value);
    }

    /// <summary>Appends a formatted interpolation hole with the given format string.</summary>
    public void AppendFormatted<T>(T value, string? format)
    {
        if (IsEnabled) _inner.AppendFormatted(value, format);
    }

    /// <summary>Appends a formatted interpolation hole with the given alignment.</summary>
    public void AppendFormatted<T>(T value, int alignment)
    {
        if (IsEnabled) _inner.AppendFormatted(value, alignment);
    }

    /// <summary>Appends a formatted interpolation hole with alignment and format string.</summary>
    public void AppendFormatted<T>(T value, int alignment, string? format)
    {
        if (IsEnabled) _inner.AppendFormatted(value, alignment, format);
    }

    /// <summary>Appends a character-span interpolation hole.</summary>
    public void AppendFormatted(ReadOnlySpan<char> value)
    {
        if (IsEnabled) _inner.AppendFormatted(value);
    }

    /// <summary>Appends a nullable-string interpolation hole.</summary>
    public void AppendFormatted(string? value)
    {
        if (IsEnabled) _inner.AppendFormatted(value);
    }

    internal string ToStringAndClear() => _inner.ToStringAndClear();
}
