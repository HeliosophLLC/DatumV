using DatumIngest.Model;

namespace DatumIngest.Ingestion;

/// <summary>
/// The result of ingesting a single source file into a <c>.datum</c> file.
/// </summary>
/// <param name="OutputPath">Absolute path to the written <c>.datum</c> file.</param>
/// <param name="RowCount">Total number of rows written.</param>
/// <param name="BytesWritten">Total bytes written to the output file.</param>
/// <param name="Schema">The inferred schema of the source data.</param>
/// <param name="Statistics">Per-column statistics accumulated during ingestion.</param>
/// <param name="Sample">A preview of sampled rows for UI display, or <c>null</c> if sampling was disabled.</param>
public sealed record IngestionResult(
    string OutputPath,
    long RowCount,
    long BytesWritten,
    Schema Schema,
    IReadOnlyDictionary<string, Statistics.ColumnStatistics> Statistics,
    SamplePreview? Sample);
