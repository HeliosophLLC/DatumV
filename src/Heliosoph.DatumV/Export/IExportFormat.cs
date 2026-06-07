using System.Collections.Generic;
using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Export;

/// <summary>
/// Static, per-format capability surface. The format registry resolves a
/// COPY statement's FORMAT option (or the inferred extension) to a single
/// <see cref="IExportFormat"/> implementation; the planner then asks it
/// — once per column — for the <see cref="MediaDisposition"/> it will use,
/// and constructs an <see cref="IExportSink"/> for the run.
/// </summary>
public interface IExportFormat
{
    /// <summary>Canonical lower-case name (<c>parquet</c>, <c>csv</c>, …).</summary>
    string Name { get; }

    /// <summary>
    /// File extensions (including leading <c>.</c>) that infer this format
    /// when no explicit FORMAT option is supplied.
    /// </summary>
    IReadOnlyList<string> Extensions { get; }

    /// <summary>
    /// True when the sink writes more than one file (sidecars, partitioned
    /// shards). The planner enforces a matching <see cref="ExportTarget.Directory"/>
    /// before constructing the sink. False for single-file formats.
    /// </summary>
    bool RequiresDirectorySink { get; }

    /// <summary>
    /// Plan-time per-column policy resolution. Called once per column of the
    /// source query's projected schema. Throws
    /// <see cref="ExportPlanException"/> with a column-specific message when
    /// the kind cannot be exported (e.g. a typed-media kind in a format that
    /// has no representation for it).
    /// </summary>
    MediaDisposition ResolveDisposition(ColumnInfo column, ExportOptions options);

    /// <summary>
    /// Builds the per-run sink. Called once after planner-time validation;
    /// the sink owns the target file/directory until
    /// <see cref="IExportSink.FinishAsync"/> + <see cref="System.IAsyncDisposable.DisposeAsync"/>.
    /// The <paramref name="sidecarRegistry"/> parameter carries the
    /// execution-scoped <see cref="SidecarRegistry"/> (typically
    /// <c>ExecutionContext.SidecarRegistry</c>) so the sink can resolve
    /// sidecar-backed typed-media values — Image / Audio / Video / Mesh /
    /// PointCloud / Json whose bytes live in a <c>.datum-blob</c> sidecar
    /// rather than the row arena. <see langword="null"/> is only safe when
    /// the caller guarantees no sidecar-backed values reach the sink
    /// (planner-time validation calls, single-statement smoke tests).
    /// </summary>
    IExportSink CreateSink(
        ExportTarget target,
        Schema schema,
        IReadOnlyList<MediaDisposition> columnDispositions,
        ExportOptions options,
        SidecarRegistry? sidecarRegistry);
}
