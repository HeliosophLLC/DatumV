using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Export.Arrow;

/// <summary>
/// Apache Arrow IPC implementation of <see cref="IExportFormat"/>.
/// Single-file output in the Arrow IPC <em>file</em> format (also known as
/// Feather v2 — same on-disk shape, different extension convention). The
/// file format carries a footer with a dictionary of record-batch
/// locations so consumers can random-access any batch without rescanning
/// from the start, which is the typical pandas / Polars / DuckDB read
/// pattern.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Round-trip with <c>open_arrow</c>.</strong> The sink writes
/// only the shapes the existing reader understands (scalars, 1-D arrays
/// via <c>ListArray</c>), so a <c>COPY TO 'foo.arrow'</c> followed by
/// <c>SELECT * FROM open_arrow('foo.arrow')</c> recovers the same shape
/// for every column the format supports. Typed-media columns survive the
/// round trip as opaque byte arrays today; the <c>datumv.*</c> field
/// metadata that lets <c>open_parquet</c> retype them transparently is
/// also written here, ready for a matching enhancement on the read side.
/// </para>
/// <para>
/// <strong>Plan-time rejection set</strong> matches the
/// <c>open_arrow</c> reader's supported set: <see cref="DataKind.Struct"/>
/// and <c>Array&lt;Struct&gt;</c> aren't yet wired on either side, so
/// they reject here rather than producing a file the reader will
/// refuse. <see cref="DataKind.Drawing"/> rejects because the recipe
/// isn't bytes; the runtime-only handles (VideoFrame / AudioSlice /
/// VideoSlice) reject because they're per-query references, not values.
/// </para>
/// </remarks>
public sealed class ArrowExportFormat : IExportFormat
{
    /// <inheritdoc />
    public string Name => "arrow";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [".arrow", ".feather"];

    /// <inheritdoc />
    public bool RequiresDirectorySink => false;

    /// <inheritdoc />
    public MediaDisposition ResolveDisposition(ColumnInfo column, ExportOptions options)
    {
        // Plan-time validation is the only hook we get before file I/O —
        // surface unsupported kinds here with a column-named message so
        // typos and shape mismatches don't manifest as cryptic builder
        // failures mid-batch.
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
        // Deliberately unused: Arrow timestamps are UTC-normalized instants
        // with UTC schema metadata, session-independent by design (schema
        // fingerprints and round-trips must not vary with SET TIME ZONE).
        TimeZoneInfo? sessionTimeZone = null)
    {
        if (target is not ExportTarget.File fileTarget)
        {
            throw new ExportPlanException(
                "COPY TO arrow: target must be a single file path. " +
                "Directory targets are not supported by the Arrow sink.");
        }

        // Re-run the rejection check after schema reconciliation. The
        // first-batch observation in ExportPlan can rewrite a column's
        // runtime kind into something the planner didn't see (a model
        // invocation whose return shape wasn't statically resolvable);
        // catching it here keeps the column-named error message correct.
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            RejectIfUnsupported(schema.Columns[i], columnNameForError: schema.Columns[i].Name);
        }

        return new ArrowExportSink(fileTarget.Path, schema, sidecarRegistry);
    }

    /// <summary>
    /// Throws <see cref="ExportPlanException"/> for kinds the v1 sink
    /// doesn't encode. The list mirrors the <c>open_arrow</c> reader's
    /// supported set so a written file is always readable; broadening
    /// the writer's set without broadening the reader produces files the
    /// engine refuses to re-import, which defeats the round-trip story.
    /// </summary>
    private static void RejectIfUnsupported(ColumnInfo column, string columnNameForError)
    {
        switch (column.Kind)
        {
            case DataKind.Drawing:
                throw new ExportPlanException(
                    $"COPY TO arrow: column '{columnNameForError}' has kind Drawing — " +
                    "Drawing is a procedural recipe, not bytes. Rasterise it first via " +
                    "render(drawing, point2d(w, h)) and export the resulting image.");

            case DataKind.AudioSlice:
            case DataKind.VideoSlice:
            case DataKind.VideoFrame:
                throw new ExportPlanException(
                    $"COPY TO arrow: column '{columnNameForError}' has runtime-only kind " +
                    $"{column.Kind} (a lazy handle, not a serialisable value). " +
                    "Materialise it first — e.g. render the video frame to an image — " +
                    "and export the materialised column.");

            case DataKind.Lambda:
            case DataKind.Type:
            case DataKind.Unknown:
                throw new ExportPlanException(
                    $"COPY TO arrow: column '{columnNameForError}' has kind {column.Kind} " +
                    "which cannot be exported to any on-disk format.");
        }

        // Struct columns now write as Arrow StructType — both top-level
        // and Array<Struct>. The schema's Fields metadata is required so
        // the writer can name each child column; reject upfront when it
        // isn't carried (uncommon in practice, but a schema-reconciled
        // column whose runtime kind diverged into Struct might lack it).
        if (column.Kind == DataKind.Struct && column.Fields is null)
        {
            throw new ExportPlanException(
                $"COPY TO arrow: column '{columnNameForError}' is Struct but the projection " +
                "didn't carry per-field metadata, so the writer can't name the child columns. " +
                "Make sure the source query exposes a struct literal with named fields " +
                "(e.g. `{ a: 1, b: 'x' }`) or wrap the value in a typed constructor.");
        }
        else if (column.Kind == DataKind.Struct && column.Fields is { } structFields)
        {
            for (int i = 0; i < structFields.Count; i++)
            {
                RejectIfUnsupported(structFields[i], $"{columnNameForError}.{structFields[i].Name}");
            }
        }

        // Multi-dim arrays would silently flatten to 1-D Arrow ListArray
        // and lose the shape on disk — the reader can't tell a flattened
        // (2,3) Float32 vector from a 6-element vector. Reject upfront
        // with an actionable hint rather than producing a file whose
        // round-trip via open_arrow comes back as a 1-D array. CSV / JSON
        // make the same call.
        if (column.IsMultiDim || (column.FixedShape is { } shape && shape.Length > 1))
        {
            throw new ExportPlanException(
                $"COPY TO arrow: column '{columnNameForError}' is a multi-dimensional " +
                $"array (kind {column.Kind}, shape preserved on disk requires a per-row " +
                "shape buffer Arrow doesn't carry). Flatten the array via " +
                "`CAST(arr AS Float32[])` (or the matching element kind) before exporting, " +
                "or export to parquet — Parquet's logical-type metadata preserves the shape.");
        }

        // Color / Point2D / Point3D have no native Arrow shape and the
        // reader doesn't surface them either. Reject explicitly rather
        // than silently flattening to a struct that won't round-trip.
        if (column.Kind == DataKind.Color
            || column.Kind == DataKind.Point2D
            || column.Kind == DataKind.Point3D)
        {
            throw new ExportPlanException(
                $"COPY TO arrow: column '{columnNameForError}' has kind {column.Kind} " +
                "which has no Arrow representation today. These are inline value types; " +
                "project the components into separate scalar columns or export to parquet.");
        }
    }
}
