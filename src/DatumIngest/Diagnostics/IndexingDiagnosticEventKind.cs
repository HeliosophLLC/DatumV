namespace DatumIngest.Diagnostics;

/// <summary>
/// Identifies the kind of diagnostic event emitted during index building via
/// <see cref="DatumIndexerOptions.Diagnostics"/>.
/// </summary>
public enum IndexingDiagnosticEventKind
{
    /// <summary>
    /// A chunk was finalized and its sorted index entries flushed to disk.
    /// Emitted once per chunk during the scanning phase.
    /// </summary>
    ChunkFlushed,

    /// <summary>
    /// Row scanning completed. All source rows have been observed and chunked.
    /// </summary>
    ScanningCompleted,

    /// <summary>
    /// The <c>.datum-index</c> file has been written (k-way merge, compression,
    /// and serialization complete).
    /// </summary>
    IndexWriteCompleted,
}
