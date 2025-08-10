namespace DatumIngest;

/// <summary>
/// Identifies the current phase of index building, reported via
/// <see cref="IndexingProgress"/> during
/// <see cref="DatumIngester.BuildIndexAsync(string, DatumIndexerOptions?, System.Action{IndexingProgress}?, System.Threading.CancellationToken)"/>.
/// </summary>
public enum IndexingPhase
{
    /// <summary>
    /// Row iteration and chunk accumulation: reading source rows, computing
    /// per-chunk statistics, accumulating bloom filters and sorted index entries.
    /// </summary>
    Scanning,

    /// <summary>
    /// Index finalization: K-way merge of spill files, B+Tree bulk loading,
    /// page compression, and writing the <c>.datum-index</c> file sections.
    /// </summary>
    Building,
}
