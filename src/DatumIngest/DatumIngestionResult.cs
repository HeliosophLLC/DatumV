namespace DatumIngest;

/// <summary>
/// The output of a <see cref="DatumIngester"/> run: streams ready to upload,
/// serialized schema and statistics JSON, and summary counts.
/// Dispose when uploads are complete.
/// </summary>
public sealed class DatumIngestionResult : IAsyncDisposable
{
    /// <summary>The logical table name derived from the source file name.</summary>
    public string TableName { get; init; } = string.Empty;

    /// <summary>
    /// Seekable <see cref="MemoryStream"/> containing the <c>.datum</c> file bytes,
    /// positioned at offset 0 and ready to upload.
    /// </summary>
    public MemoryStream DatumStream { get; init; } = new();

    /// <summary>
    /// Seekable <see cref="MemoryStream"/> containing the <c>.datum-index</c> file bytes,
    /// positioned at offset 0 and ready to upload.
    /// </summary>
    public MemoryStream IndexStream { get; init; } = new();

    /// <summary>Serialized <c>SourceSchema</c> JSON for the table.</summary>
    public string SchemaJson { get; init; } = string.Empty;

    /// <summary>Serialized <c>SourceManifest</c> JSON containing column statistics.</summary>
    public string ManifestJson { get; init; } = string.Empty;

    /// <summary>Total number of rows ingested.</summary>
    public long RowCount { get; init; }

    /// <summary>Number of feature columns in the manifest.</summary>
    public int FeatureCount { get; init; }

    /// <summary>Byte length of the <c>.datum</c> stream.</summary>
    public long DatumByteCount => DatumStream.Length;

    /// <summary>Byte length of the <c>.datum-index</c> stream.</summary>
    public long IndexByteCount => IndexStream.Length;

    /// <summary>Byte count of <see cref="SchemaJson"/> when UTF-8 encoded.</summary>
    public int SchemaByteCount => System.Text.Encoding.UTF8.GetByteCount(SchemaJson);

    /// <summary>Byte count of <see cref="ManifestJson"/> when UTF-8 encoded.</summary>
    public int ManifestByteCount => System.Text.Encoding.UTF8.GetByteCount(ManifestJson);

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        DatumStream.Dispose();
        IndexStream.Dispose();
        return ValueTask.CompletedTask;
    }
}
