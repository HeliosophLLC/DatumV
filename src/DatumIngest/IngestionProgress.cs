namespace DatumIngest;

/// <summary>
/// A snapshot of progress during ingestion, reported by
/// <see cref="DatumIngester.IngestAsync(string, Action{IngestionProgress}?, CancellationToken)"/>
/// at regular percentage intervals.
/// </summary>
/// <param name="TableName">The logical name of the table being ingested.</param>
/// <param name="RowsProcessed">Number of rows processed so far.</param>
/// <param name="TotalRows">
/// Estimated total number of rows, or <c>null</c> when the provider cannot
/// estimate the row count (for example JSON files). When <c>null</c>, only the
/// final 100% report is issued.
/// </param>
/// <param name="PercentComplete">
/// Completion percentage (0–100). Reported at every 5% boundary when
/// <paramref name="TotalRows"/> is available, and always at 100% when the
/// table finishes.
/// </param>
public sealed record IngestionProgress(
    string TableName,
    long RowsProcessed,
    long? TotalRows,
    int PercentComplete);
