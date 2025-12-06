using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using Spectre.Console;

namespace DatumIngest.Shell;

/// <summary>
/// <see cref="IModelInvocationTracer"/> implementation that prints a
/// per-dispatch log to the console. Wired up by <see cref="InteractiveShell"/>
/// when the user toggles <c>.trace on</c>; detached on <c>.trace off</c>.
/// </summary>
/// <remarks>
/// <para>
/// Output format per dispatch (start + done lines):
/// <code>
/// [trace] models.kokoro_82m  rows=4  inputs=["hello there...", ...]  overrides=[]
/// [trace] models.kokoro_82m  rows=4  done in 1.8s
/// </code>
/// String inputs are truncated to <see cref="MaxStringPreviewChars"/> chars
/// each so a 30-row caption batch doesn't bury the terminal in line wrap.
/// Byte payloads (Image / Audio / Video) print as <c>&lt;Audio 38400 bytes&gt;</c>
/// — useful diagnostic information without dumping base64.
/// </para>
/// </remarks>
internal sealed class ShellModelTracer : IModelInvocationTracer
{
    /// <summary>Truncation length for string previews per row.</summary>
    private const int MaxStringPreviewChars = 60;

    /// <summary>Max rows to preview before collapsing to a count.</summary>
    private const int MaxRowsPreviewed = 3;

    /// <inheritdoc />
    public void OnDispatchStarted(
        string modelName,
        int rowCount,
        IReadOnlyList<IReadOnlyList<ValueRef>> inputs,
        IReadOnlyList<IReadOnlyList<ValueRef>> overrides)
    {
        string inputsPreview = FormatBatchPreview(inputs);
        string overridesPreview = FormatBatchPreview(overrides);

        AnsiConsole.MarkupLine(
            $"[grey][[trace]][/] [cyan]models.{Markup.Escape(modelName)}[/] rows={rowCount}  " +
            $"inputs={Markup.Escape(inputsPreview)}  overrides={Markup.Escape(overridesPreview)}");
    }

    /// <inheritdoc />
    public void OnDispatchCompleted(string modelName, int rowCount, TimeSpan elapsed)
    {
        AnsiConsole.MarkupLine(
            $"[grey][[trace]][/] [cyan]models.{Markup.Escape(modelName)}[/] rows={rowCount}  " +
            $"[green]done in {FormatElapsed(elapsed)}[/]");
    }

    /// <inheritdoc />
    public void OnDispatchFailed(
        string modelName, int rowCount, TimeSpan elapsed, Exception exception)
    {
        AnsiConsole.MarkupLine(
            $"[grey][[trace]][/] [cyan]models.{Markup.Escape(modelName)}[/] rows={rowCount}  " +
            $"[red]failed after {FormatElapsed(elapsed)}: {Markup.Escape(exception.Message)}[/]");
    }

    /// <summary>
    /// Formats a row-major batch as <c>[[col0, col1], [col0, col1], ...]</c>,
    /// truncating after <see cref="MaxRowsPreviewed"/> rows. Returns
    /// <c>"[]"</c> for empty batches.
    /// </summary>
    private static string FormatBatchPreview(IReadOnlyList<IReadOnlyList<ValueRef>> batch)
    {
        if (batch.Count == 0) return "[]";

        int previewCount = Math.Min(batch.Count, MaxRowsPreviewed);
        List<string> rowStrings = new(previewCount);
        for (int r = 0; r < previewCount; r++)
        {
            rowStrings.Add(FormatRowPreview(batch[r]));
        }

        string body = string.Join(", ", rowStrings);
        if (batch.Count > previewCount)
        {
            body += $", ... (+{batch.Count - previewCount} more)";
        }
        return "[" + body + "]";
    }

    /// <summary>
    /// Formats a single row's columns as <c>(col0, col1, ...)</c>. Single-
    /// column rows skip the parens for readability — caption inputs are
    /// usually one big string per row.
    /// </summary>
    private static string FormatRowPreview(IReadOnlyList<ValueRef> row)
    {
        if (row.Count == 0) return "()";
        if (row.Count == 1) return FormatValuePreview(row[0]);

        return "(" + string.Join(", ", row.Select(FormatValuePreview)) + ")";
    }

    /// <summary>
    /// Formats one <see cref="ValueRef"/> as a short preview. Strings get
    /// truncated; byte payloads collapse to <c>&lt;kind N bytes&gt;</c>;
    /// nulls print as <c>NULL</c>; numerics print as their string form.
    /// </summary>
    private static string FormatValuePreview(ValueRef value)
    {
        if (value.IsNull) return "NULL";

        DataKind kind = value.Kind;
        if (kind == DataKind.String)
        {
            string s = value.AsString();
            if (s.Length > MaxStringPreviewChars)
            {
                return $"\"{s[..MaxStringPreviewChars].Replace("\n", " ")}…\"";
            }
            return $"\"{s.Replace("\n", " ")}\"";
        }

        if (kind is DataKind.Image or DataKind.Audio or DataKind.Video
            || (kind == DataKind.UInt8 && value.IsArray))
        {
            byte[] bytes = value.AsBytes();
            return $"<{kind} {bytes.Length:N0} bytes>";
        }

        if (kind == DataKind.Boolean) return value.AsBoolean() ? "true" : "false";

        return kind switch
        {
            DataKind.UInt8 => value.AsUInt8().ToString(),
            DataKind.Int8 => value.AsInt8().ToString(),
            DataKind.Int16 => value.AsInt16().ToString(),
            DataKind.UInt16 => value.AsUInt16().ToString(),
            DataKind.Int32 => value.AsInt32().ToString(),
            DataKind.UInt32 => value.AsUInt32().ToString(),
            DataKind.Int64 => value.AsInt64().ToString(),
            DataKind.UInt64 => value.AsUInt64().ToString(),
            DataKind.Float32 => value.AsFloat32().ToString("0.###"),
            DataKind.Float64 => value.AsFloat64().ToString("0.###"),
            _ => $"<{kind}>",
        };
    }

    /// <summary>
    /// Formats a <see cref="TimeSpan"/> as a compact human-readable string —
    /// "12.3ms", "1.4s", "2m 5.6s" — picking the unit by the magnitude of
    /// elapsed time so log lines stay short.
    /// </summary>
    private static string FormatElapsed(TimeSpan elapsed)
    {
        double totalSec = elapsed.TotalSeconds;
        if (totalSec < 1.0) return $"{elapsed.TotalMilliseconds:0.#}ms";
        if (totalSec < 60.0) return $"{totalSec:0.0}s";
        int minutes = (int)elapsed.TotalMinutes;
        double remainderSec = totalSec - minutes * 60;
        return $"{minutes}m {remainderSec:0.0}s";
    }
}
