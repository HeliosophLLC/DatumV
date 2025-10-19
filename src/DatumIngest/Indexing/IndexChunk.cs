namespace DatumIngest.Indexing;

/// <summary>
/// Metadata for a single chunk within an index. Each chunk represents a contiguous
/// range of rows and carries per-column statistics for partition pruning.
/// </summary>
/// <param name="RowOffset">Zero-based index of the first row in this chunk.</param>
/// <param name="RowCount">Number of rows in this chunk.</param>
/// <param name="ColumnStatistics">Per-column statistics keyed by column name (case-insensitive).</param>
public sealed record IndexChunk(
    long RowOffset,
    long RowCount,
    IReadOnlyDictionary<string, ChunkColumnStatistics> ColumnStatistics);
