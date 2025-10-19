using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Ingestion;

/// <summary>
/// The result of ingesting a single source file into a <c>.datum</c> file.
/// </summary>
/// <param name="OutputPath">Absolute path to the written <c>.datum</c> file.</param>
/// <param name="RowCount">Total number of rows written.</param>
/// <param name="BytesWritten">Total bytes written to the output file.</param>
/// <param name="Schema">The inferred schema of the source data.</param>
/// <param name="Manifest">
/// The feature-level manifest built from per-column statistics: distribution shape,
/// top-K values, temporal ranges, image stats, etc. Replaces the raw <c>ColumnStatistics</c>
/// map with the analysis-ready view downstream consumers (dashboards, insight engine,
/// query planner) actually use.
/// </param>
/// <param name="Sample">A preview of sampled rows for UI display, or <c>null</c> if sampling was disabled.</param>
/// <param name="ScanPass">
/// Metrics for the optional first-pass type scan. Null when the ingester ran in
/// single-pass (sample-inference) mode or when the source format does not support scanning.
/// </param>
/// <param name="IngestPass">Metrics for the main ingestion pass (the write pass).</param>
public sealed record IngestionResult(
    string OutputPath,
    long RowCount,
    long BytesWritten,
    Schema Schema,
    QueryResultsManifest Manifest,
    SamplePreview? Sample,
    PassMetrics? ScanPass,
    PassMetrics IngestPass);
