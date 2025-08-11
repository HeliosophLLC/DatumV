namespace DatumIngest.Diagnostics;

/// <summary>
/// A diagnostic event emitted during index building, providing visibility into
/// chunk flushing, scanning completion, and index file writing. Subscribe via
/// <see cref="DatumIndexerOptions.Diagnostics"/>.
/// </summary>
public sealed record IndexingDiagnosticEvent
{
    /// <summary>The kind of diagnostic event.</summary>
    public required IndexingDiagnosticEventKind Kind { get; init; }

    /// <summary>The logical name of the table being indexed.</summary>
    public required string TableName { get; init; }

    /// <summary>
    /// Zero-based chunk index. Populated for
    /// <see cref="IndexingDiagnosticEventKind.ChunkFlushed"/>.
    /// </summary>
    public int ChunkIndex { get; init; }

    /// <summary>Total rows processed so far.</summary>
    public long RowsProcessed { get; init; }

    /// <summary>
    /// Number of chunks finalized. Populated for
    /// <see cref="IndexingDiagnosticEventKind.ScanningCompleted"/>.
    /// </summary>
    public int TotalChunks { get; init; }

    /// <summary>
    /// Byte length of the written index stream. Populated for
    /// <see cref="IndexingDiagnosticEventKind.IndexWriteCompleted"/>.
    /// </summary>
    public long BytesWritten { get; init; }
}
