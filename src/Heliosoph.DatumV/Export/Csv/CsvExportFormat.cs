using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Export.Csv;

/// <summary>
/// CSV implementation of <see cref="IExportFormat"/>. Single-file output,
/// UTF-8 encoded, RFC 4180 quoting. Scalar kinds round-trip back through
/// <c>open_csv_typed</c> because the on-disk text matches the scanner's
/// expected forms (ISO 8601 dates / timestamps, lowercase booleans, plain
/// invariant-culture numerics, empty field for NULL). Composite kinds
/// (<see cref="DataKind.Struct"/> and <c>Array&lt;T&gt;</c>) are written as
/// JSON text in the cell — pandas / Excel / DuckDB all read those as strings,
/// which is the honest answer for CSV. Typed-media kinds (Image / Audio /
/// Video / Mesh / PointCloud / Drawing) are rejected at plan time with a
/// pointer at Parquet — base64-inlining megabytes per row defeats the point
/// of CSV.
/// </summary>
public sealed class CsvExportFormat : IExportFormat
{
    /// <inheritdoc />
    public string Name => "csv";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [".csv"];

    /// <inheritdoc />
    public bool RequiresDirectorySink => false;

    /// <inheritdoc />
    public MediaDisposition ResolveDisposition(ColumnInfo column, ExportOptions options)
    {
        // Validate the global options on every per-column call. ResolveDisposition
        // is the only plan-time hook the format gets; running these here surfaces
        // bad option values (unknown DELIMITER, malformed LINE_ENDING) before any
        // file handle is opened. Idempotent — safe to call per column.
        _ = ResolveDelimiter(options);
        _ = ResolveQuote(options);
        _ = ResolveLineEnding(options);
        _ = ResolveNullString(options);
        _ = ResolveHeader(options);

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
                "COPY TO csv: target must be a single file path. " +
                "Directory targets are not supported by the CSV sink.");
        }

        // Re-validate at sink-construction time. The reconciliation pass in
        // ExportPlan can hand us a fresh column whose runtime kind differs from
        // the planner kind (notably a model invocation that returned an Image
        // value at execution time); rejecting here keeps the partial-file
        // cleanup contract intact for that case.
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            RejectIfUnsupported(schema.Columns[i], columnNameForError: schema.Columns[i].Name);
        }

        return new CsvExportSink(
            fileTarget.Path,
            schema,
            sidecarRegistry,
            delimiter: ResolveDelimiter(options),
            quote: ResolveQuote(options),
            lineEnding: ResolveLineEnding(options),
            nullString: ResolveNullString(options),
            writeHeader: ResolveHeader(options),
            sessionTimeZone: sessionTimeZone);
    }

    /// <summary>
    /// Walks <paramref name="column"/> (and any struct fields recursively)
    /// and throws <see cref="ExportPlanException"/> for kinds the CSV sink
    /// cannot represent. Image / Audio / Video / Mesh / PointCloud / Drawing
    /// route the caller at Parquet; the runtime-only lazy handles
    /// (VideoFrame / AudioSlice / VideoSlice) and the meta kinds (Lambda /
    /// Type / Unknown) are rejected as nonsensical for a flat-text export.
    /// </summary>
    private static void RejectIfUnsupported(ColumnInfo column, string columnNameForError)
    {
        // Array<UInt8> is conventionally encoded-media bytes (PNG / JPEG /
        // WAV / etc.) in disguise. Inlining base64 megabytes per row in a
        // CSV is the worst of both worlds — reject and route to Parquet.
        if (column.IsByteArrayColumn)
        {
            throw new ExportPlanException(
                $"COPY TO csv: column '{columnNameForError}' is a byte array " +
                "(Array<UInt8>) which CSV cannot represent without base64-inlining " +
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
                    $"COPY TO csv: column '{columnNameForError}' has typed-media kind " +
                    $"{column.Kind} which CSV cannot represent. Export this column to " +
                    "parquet instead — Parquet preserves the bytes losslessly and " +
                    "round-trips through open_parquet.");

            case DataKind.Drawing:
                throw new ExportPlanException(
                    $"COPY TO csv: column '{columnNameForError}' has kind Drawing — " +
                    "Drawing is a procedural recipe, not bytes. Rasterise it first via " +
                    "render(drawing, point2d(w, h)) and export the resulting image to parquet.");

            case DataKind.AudioSlice:
            case DataKind.VideoSlice:
            case DataKind.VideoFrame:
                throw new ExportPlanException(
                    $"COPY TO csv: column '{columnNameForError}' has runtime-only kind " +
                    $"{column.Kind} (a lazy handle, not a serialisable value). " +
                    "Materialise it first — e.g. render the video frame to an image — " +
                    "and export the materialised column to parquet.");

            case DataKind.Lambda:
            case DataKind.Type:
            case DataKind.Unknown:
                throw new ExportPlanException(
                    $"COPY TO csv: column '{columnNameForError}' has kind {column.Kind} " +
                    "which cannot be exported to any on-disk format.");
        }

        if (column.Kind == DataKind.Struct && column.Fields is { } fields)
        {
            // Walk struct fields so a typed-media field inside an otherwise-
            // representable struct fails at plan time rather than mid-stream.
            // The error names the outermost column then the dotted path so
            // users can find the offender in a deeply-nested schema.
            for (int i = 0; i < fields.Count; i++)
            {
                RejectIfUnsupported(fields[i], $"{columnNameForError}.{fields[i].Name}");
            }
        }
    }

    /// <summary>
    /// Resolves the <c>DELIMITER</c> option. Defaults to comma. Must be a
    /// single non-control character; explicit support for the common
    /// alternatives (semicolon, tab, pipe) without listing every printable
    /// character.
    /// </summary>
    private static char ResolveDelimiter(ExportOptions options)
    {
        string? raw = options.GetString("DELIMITER");
        if (raw is null) return ',';

        if (raw.Length == 0)
        {
            throw new ExportPlanException(
                "COPY TO csv: DELIMITER must be a single character (got empty string).");
        }
        // Accept the common multi-char escape spellings 'tab' and '\\t' as
        // shorthand for a literal tab character — typing a literal tab inside
        // a SQL string literal is awkward enough that this is worth handling.
        if (string.Equals(raw, "tab", StringComparison.OrdinalIgnoreCase)
            || raw == "\\t")
        {
            return '\t';
        }
        if (raw.Length != 1)
        {
            throw new ExportPlanException(
                $"COPY TO csv: DELIMITER must be a single character (got '{raw}'). " +
                "Use 'tab' or '\\t' for a tab delimiter.");
        }
        char c = raw[0];
        if (c == '"' || c == '\r' || c == '\n')
        {
            throw new ExportPlanException(
                $"COPY TO csv: DELIMITER '{c}' would collide with CSV quoting/line " +
                "structure; pick a different character.");
        }
        return c;
    }

    /// <summary>
    /// Resolves the <c>QUOTE</c> option. Defaults to double-quote (RFC 4180).
    /// Restricted to a single non-newline character so RFC 4180 escape
    /// doubling stays well-defined.
    /// </summary>
    private static char ResolveQuote(ExportOptions options)
    {
        string? raw = options.GetString("QUOTE");
        if (raw is null) return '"';

        if (raw.Length != 1)
        {
            throw new ExportPlanException(
                $"COPY TO csv: QUOTE must be a single character (got '{raw}').");
        }
        char c = raw[0];
        if (c == '\r' || c == '\n')
        {
            throw new ExportPlanException(
                "COPY TO csv: QUOTE cannot be a newline character.");
        }
        return c;
    }

    /// <summary>
    /// Resolves the <c>LINE_ENDING</c> option to <c>"\n"</c> (default) or
    /// <c>"\r\n"</c>. The default matches Unix tooling and DuckDB; users
    /// targeting strict-Windows tools can opt in to CRLF.
    /// </summary>
    private static string ResolveLineEnding(ExportOptions options)
    {
        string? raw = options.GetString("LINE_ENDING");
        if (raw is null) return "\n";

        return raw.Trim().ToLowerInvariant() switch
        {
            "lf" or "unix" or "\\n" => "\n",
            "crlf" or "windows" or "\\r\\n" => "\r\n",
            _ => throw new ExportPlanException(
                $"COPY TO csv: LINE_ENDING value '{raw}' is not recognised. " +
                "Supported values: lf (default), crlf."),
        };
    }

    /// <summary>
    /// Resolves the <c>NULL_STRING</c> option. Defaults to empty string —
    /// matches both PostgreSQL's <c>COPY</c> default and the
    /// <see cref="Serialization.Csv.CsvTypeScanner"/>'s null-inference
    /// behaviour, so an export-then-<c>open_csv_typed</c> round trip
    /// preserves NULL without an explicit option on read.
    /// </summary>
    private static string ResolveNullString(ExportOptions options)
        => options.GetString("NULL_STRING") ?? string.Empty;

    /// <summary>
    /// Resolves the <c>HEADER</c> option. Defaults to <see langword="true"/>
    /// — modern tooling (Excel, pandas, DuckDB) all expect a header row.
    /// Suppress with <c>HEADER false</c> for pipelines that already know
    /// the schema.
    /// </summary>
    private static bool ResolveHeader(ExportOptions options)
    {
        if (!options.TryGetBool("HEADER", out bool value)) return true;
        return value;
    }
}
