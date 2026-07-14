using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Export.Json;

/// <summary>
/// JSON implementation of <see cref="IExportFormat"/>. Single-file UTF-8
/// output. Two modes share the same sink:
/// <list type="bullet">
///   <item><description><strong>Array</strong> (default): one top-level JSON
///   array, one object per row. Friendly to <c>JSON.parse</c> and to ad-hoc
///   inspection — at the cost of needing the whole document in memory on the
///   consumer side.</description></item>
///   <item><description><strong>Lines</strong> (<c>LINES true</c>, or any
///   <c>.jsonl</c> / <c>.ndjson</c> extension): newline-delimited JSON. One
///   complete object per line, no outer array. The streaming-friendly shape
///   ML / DuckDB / jq pipelines expect.</description></item>
/// </list>
/// Structure-preserving where CSV is not: <see cref="DataKind.Struct"/>
/// becomes a nested object with real field names sourced from the projected
/// <see cref="ColumnInfo.Fields"/>, <c>Array&lt;T&gt;</c> becomes a real JSON
/// array, and <see cref="DataKind.Json"/> decodes the CBOR payload to a real
/// nested node rather than an opaque escaped string. Typed-media kinds are
/// rejected at plan time with the same Parquet-hint message the CSV sink
/// uses — base64-inlining megabytes per row defeats the format's purpose.
/// </summary>
public sealed class JsonExportFormat : IExportFormat
{
    /// <inheritdoc />
    public string Name => "json";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [".json", ".jsonl", ".ndjson"];

    /// <inheritdoc />
    public bool RequiresDirectorySink => false;

    /// <inheritdoc />
    public MediaDisposition ResolveDisposition(ColumnInfo column, ExportOptions options)
    {
        // Validate global options on every call. Idempotent — safe to invoke
        // per column. Surfacing typos here means the user gets a clear error
        // before any file handle opens.
        _ = ResolveLines(options);
        _ = ResolveIndent(options);
        ValidateOptionCombination(options);

        RejectIfUnsupported(column, columnNameForError: column.Name);
        return MediaDisposition.Inline;
    }

    /// <inheritdoc />
    public IExportSink CreateSink(
        ExportTarget target,
        Schema schema,
        IReadOnlyList<MediaDisposition> columnDispositions,
        ExportOptions options,
        SidecarRegistry? sidecarRegistry,
        TimeZoneInfo? sessionTimeZone = null)
    {
        if (target is not ExportTarget.File fileTarget)
        {
            throw new ExportPlanException(
                "COPY TO json: target must be a single file path. " +
                "Directory targets are not supported by the JSON sink.");
        }

        // The reconciliation pass in ExportPlan may rewrite the schema after
        // observing the first batch's runtime kinds (e.g. a model invocation
        // that returned an Image when the planner expected String). Re-run
        // the unsupported-kind check here so the rejected-kind error message
        // still names the right column even when the planner schema was
        // optimistic.
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            RejectIfUnsupported(schema.Columns[i], columnNameForError: schema.Columns[i].Name);
        }

        return new JsonExportSink(
            fileTarget.Path,
            schema,
            sidecarRegistry,
            lines: ResolveEffectiveLines(options, fileTarget.Path),
            indent: ResolveIndent(options),
            sessionTimeZone: sessionTimeZone);
    }

    /// <summary>
    /// Walks <paramref name="column"/> (and any struct fields recursively)
    /// and throws <see cref="ExportPlanException"/> for kinds JSON cannot
    /// represent meaningfully in flat text. Same set as the CSV sink, same
    /// reasons — JSON could in principle carry base64 bytes inline, but
    /// silently turning a million-row image column into a multi-GB JSON
    /// document defeats the format.
    /// </summary>
    private static void RejectIfUnsupported(ColumnInfo column, string columnNameForError)
    {
        if (column.IsByteArrayColumn)
        {
            throw new ExportPlanException(
                $"COPY TO json: column '{columnNameForError}' is a byte array " +
                "(Array<UInt8>) which JSON cannot represent without base64-inlining " +
                "megabytes per row. Export this column to parquet instead, or " +
                "project it out of the SELECT.");
        }

        switch (column.Kind)
        {
            case DataKind.Image:
            case DataKind.Audio:
            case DataKind.Video:
            case DataKind.Mesh:
            case DataKind.PointCloud:
                throw new ExportPlanException(
                    $"COPY TO json: column '{columnNameForError}' has typed-media kind " +
                    $"{column.Kind} which JSON cannot represent. Export this column to " +
                    "parquet instead — Parquet preserves the bytes losslessly and " +
                    "round-trips through open_parquet.");

            case DataKind.Drawing:
                throw new ExportPlanException(
                    $"COPY TO json: column '{columnNameForError}' has kind Drawing — " +
                    "Drawing is a procedural recipe, not bytes. Rasterise it first via " +
                    "render(drawing, point2d(w, h)) and export the resulting image to parquet.");

            case DataKind.AudioSlice:
            case DataKind.VideoSlice:
            case DataKind.VideoFrame:
                throw new ExportPlanException(
                    $"COPY TO json: column '{columnNameForError}' has runtime-only kind " +
                    $"{column.Kind} (a lazy handle, not a serialisable value). " +
                    "Materialise it first — e.g. render the video frame to an image — " +
                    "and export the materialised column to parquet.");

            case DataKind.Lambda:
            case DataKind.Type:
            case DataKind.Unknown:
                throw new ExportPlanException(
                    $"COPY TO json: column '{columnNameForError}' has kind {column.Kind} " +
                    "which cannot be exported to any on-disk format.");
        }

        if (column.Kind == DataKind.Struct && column.Fields is { } fields)
        {
            for (int i = 0; i < fields.Count; i++)
            {
                RejectIfUnsupported(fields[i], $"{columnNameForError}.{fields[i].Name}");
            }
        }
    }

    /// <summary>
    /// Resolves the user-supplied <c>LINES</c> boolean. Returns
    /// <see langword="null"/> when unspecified so <see cref="ResolveEffectiveLines"/>
    /// can fall back to the path-extension default.
    /// </summary>
    private static bool? ResolveLines(ExportOptions options)
    {
        if (!options.TryGetBool("LINES", out bool value)) return null;
        return value;
    }

    /// <summary>
    /// True when the sink should emit one object per line with no outer
    /// array. Honours the explicit <c>LINES</c> option when set; otherwise
    /// infers from the extension (<c>.jsonl</c> / <c>.ndjson</c> → JSONL,
    /// everything else → array).
    /// </summary>
    private static bool ResolveEffectiveLines(ExportOptions options, string path)
    {
        bool? explicitLines = ResolveLines(options);
        if (explicitLines is { } v) return v;

        string ext = System.IO.Path.GetExtension(path);
        return string.Equals(ext, ".jsonl", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".ndjson", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the <c>INDENT</c> option. Defaults to <see langword="false"/>
    /// — pretty-printing inflates large exports for no readability win past
    /// the first few rows; users who want it ask for it explicitly.
    /// </summary>
    private static bool ResolveIndent(ExportOptions options)
    {
        if (!options.TryGetBool("INDENT", out bool value)) return false;
        return value;
    }

    /// <summary>
    /// Rejects the JSONL + indent combination at plan time. JSONL's
    /// definition is "one complete object per line"; indented output puts
    /// each object on multiple lines and the resulting file is no longer
    /// valid newline-delimited JSON.
    /// </summary>
    private static void ValidateOptionCombination(ExportOptions options)
    {
        bool linesRequested = options.TryGetBool("LINES", out bool linesValue) && linesValue;
        bool indentRequested = options.TryGetBool("INDENT", out bool indentValue) && indentValue;
        if (linesRequested && indentRequested)
        {
            throw new ExportPlanException(
                "COPY TO json: INDENT true cannot be combined with LINES true — JSONL " +
                "is defined as one object per line, and indentation splits each object " +
                "across multiple lines. Pick one.");
        }
    }
}
