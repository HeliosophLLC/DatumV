namespace DatumIngest;

/// <summary>
/// A snapshot of progress during index building, reported by
/// <see cref="DatumIngester.BuildIndexAsync(string, DatumIndexerOptions?, Action{IndexingProgress}?, CancellationToken)"/>
/// at regular percentage intervals.
/// </summary>
/// <param name="TableName">The logical name of the table being indexed.</param>
/// <param name="RowsProcessed">Number of rows processed so far.</param>
/// <param name="TotalRows">Total number of rows in the table.</param>
/// <param name="PercentComplete">
/// Completion percentage (0–100). Reported at every 5% boundary and always
/// at 100% when the table finishes.
/// </param>
/// <param name="Phase">
/// The current phase of index building. Defaults to <see cref="IndexingPhase.Scanning"/>
/// for backward compatibility with existing consumers.
/// </param>
public sealed record IndexingProgress(
    string TableName,
    long RowsProcessed,
    long TotalRows,
    int PercentComplete,
    IndexingPhase Phase = IndexingPhase.Scanning);
