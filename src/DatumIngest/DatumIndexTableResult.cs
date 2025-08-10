using DatumIngest.Indexing;

namespace DatumIngest;

/// <summary>
/// Per-table output of a <see cref="DatumIngester.BuildIndexAsync(string, DatumIndexerOptions?, CancellationToken)"/>
/// call. Contains the in-memory index and the serialized <c>.datum-index</c> stream for a single logical table.
/// </summary>
public sealed class DatumIndexTableResult
{
    /// <summary>The logical table name.</summary>
    public required string TableName { get; init; }

    /// <summary>
    /// The destination file name for this table's <c>.datum-index</c> file, including the extension.
    /// Must end with <c>.datum-index</c>.
    /// </summary>
    public required string IndexFileName { get; init; }

    /// <summary>The in-memory source index for this table.</summary>
    public required SourceIndex Index { get; init; }

    /// <summary>
    /// Seekable stream containing the <c>.datum-index</c> file bytes,
    /// positioned at offset 0 and ready to upload. May be backed by a temporary file
    /// for datasets whose serialized index exceeds the <see cref="MemoryStream"/> capacity.
    /// </summary>
    public required Stream IndexStream { get; init; }

    /// <summary>Byte length of the <c>.datum-index</c> stream.</summary>
    public long IndexByteCount => IndexStream.Length;
}
