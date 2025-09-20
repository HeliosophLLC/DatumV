using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace DatumIngest.Diagnostics;

/// <summary>
/// Lightweight file-based execution tracer for diagnosing join performance.
/// Activated by setting the <c>DATUM_TRACE_FILE</c> environment variable to a file path
/// before the process starts. When the variable is not set the tracer is a no-op with
/// no overhead on hot paths.
/// </summary>
/// <remarks>
/// <para>
/// Usage: set <c>DATUM_TRACE_FILE=C:\temp\datum-trace.txt</c> then run the query.
/// The trace is written to that file, one timestamped line per event.
/// </para>
/// <para>
/// All <c>Write</c> overloads accept interpolated strings via
/// <see cref="TraceInterpolatedStringHandler"/> so that formatting is deferred
/// and zero-cost when tracing is disabled. This makes the trace calls safe to
/// leave in production code permanently.
/// </para>
/// </remarks>
internal static class ExecutionTracer
{
    private static StreamWriter? _writer;
    private static readonly object _writeLock = new();
    private static readonly Stopwatch _watch = Stopwatch.StartNew();

    /// <summary>
    /// Gets a value indicating whether the tracer is active.
    /// Checked by callers to skip label computation when tracing is off.
    /// </summary>
    internal static bool IsEnabled => _writer is not null;

    /// <summary>
    /// Opens the trace file pointed to by <c>DATUM_TRACE_FILE</c>.
    /// Call once at process startup (or at the start of a query if preferred).
    /// Safe to call multiple times — only the first call opens the file.
    /// </summary>
    internal static void Initialize()
    {
        if (_writer is not null) return;

        string? path = Environment.GetEnvironmentVariable("DATUM_TRACE_FILE");
        if (path is not null)
        {
            Initialize(path);
        }
    }

    /// <summary>
    /// Opens the trace file at the specified <paramref name="filePath"/>.
    /// Safe to call multiple times — only the first call opens the file.
    /// </summary>
    /// <param name="filePath">Absolute path to the trace output file.</param>
    public static void Initialize(string filePath)
    {
        if (_writer is not null) return;

        lock (_writeLock)
        {
            if (_writer is not null) return;

            _watch.Restart();
            _writer = new StreamWriter(filePath, append: false, Encoding.UTF8) { AutoFlush = true };
            Write("=== DatumIngest Execution Trace ===");
            Write($"PID {Environment.ProcessId}  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Write(new string('-', 72));
        }
    }

    /// <summary>Writes a single timestamped trace line. No-op when tracing is off.</summary>
    internal static void Write(string message)
    {
        StreamWriter? w = _writer;
        if (w is null) return;

        lock (_writeLock)
        {
            w.WriteLine($"[{_watch.Elapsed.TotalSeconds,8:F3}s] {message}");
        }
    }

    /// <summary>
    /// Writes a single timestamped trace line using a deferred interpolated string.
    /// When tracing is disabled the interpolated string handler short-circuits and
    /// no formatting or allocation occurs.
    /// </summary>
    internal static void Write(ref TraceInterpolatedStringHandler handler)
    {
        if (!handler.IsEnabled) return;

        Write(handler.ToStringAndClear());
    }

    /// <summary>
    /// Writes a separator line. No-op when tracing is off.
    /// </summary>
    internal static void WriteSeparator() => Write(new string('─', 64));

    /// <summary>
    /// Formats a byte count as a human-readable string (B / KB / MB / GB).
    /// </summary>
    internal static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
            >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
            >= 1024L => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes} B",
        };
    }
}

/// <summary>
/// Custom interpolated string handler that defers all formatting until
/// <see cref="ExecutionTracer.Write(ref TraceInterpolatedStringHandler)"/>
/// confirms tracing is enabled. When tracing is off, the constructor sets
/// <see cref="IsEnabled"/> to <c>false</c> and all <c>Append*</c> calls
/// are no-ops — zero allocation, zero formatting.
/// </summary>
[InterpolatedStringHandler]
internal ref struct TraceInterpolatedStringHandler
{
    private DefaultInterpolatedStringHandler _inner;

    internal bool IsEnabled { get; }

    public TraceInterpolatedStringHandler(int literalLength, int formattedCount)
    {
        IsEnabled = ExecutionTracer.IsEnabled;
        if (IsEnabled)
        {
            _inner = new DefaultInterpolatedStringHandler(literalLength, formattedCount);
        }
    }

    public void AppendLiteral(string value)
    {
        if (IsEnabled) _inner.AppendLiteral(value);
    }

    public void AppendFormatted<T>(T value)
    {
        if (IsEnabled) _inner.AppendFormatted(value);
    }

    public void AppendFormatted<T>(T value, string? format)
    {
        if (IsEnabled) _inner.AppendFormatted(value, format);
    }

    public void AppendFormatted<T>(T value, int alignment)
    {
        if (IsEnabled) _inner.AppendFormatted(value, alignment);
    }

    public void AppendFormatted<T>(T value, int alignment, string? format)
    {
        if (IsEnabled) _inner.AppendFormatted(value, alignment, format);
    }

    public void AppendFormatted(ReadOnlySpan<char> value)
    {
        if (IsEnabled) _inner.AppendFormatted(value);
    }

    public void AppendFormatted(string? value)
    {
        if (IsEnabled) _inner.AppendFormatted(value);
    }

    internal string ToStringAndClear() => _inner.ToStringAndClear();
}
