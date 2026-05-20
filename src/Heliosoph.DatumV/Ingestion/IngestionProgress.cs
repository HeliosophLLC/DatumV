namespace Heliosoph.DatumV.Ingestion;

/// <summary>
/// A snapshot of progress during ingestion, reported at regular intervals.
/// </summary>
/// <param name="RowsProcessed">Number of rows processed so far.</param>
/// <param name="TotalRows">
/// Estimated total number of rows, or <c>null</c> when the source format cannot
/// estimate the row count. When <c>null</c>, only the final 100% report is issued.
/// </param>
/// <param name="PercentComplete">
/// Completion percentage (0–100). Reported at every 5% boundary when
/// <paramref name="TotalRows"/> is available, and always at 100% when ingestion finishes.
/// </param>
public sealed record IngestionProgress(
    long RowsProcessed,
    long? TotalRows,
    int PercentComplete);
