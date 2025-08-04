using DatumIngest.Indexing;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest;

/// <summary>
/// Per-table output of a <see cref="DatumIngester"/> run.
/// Contains the in-memory schema, manifest, index, and binary streams for a single logical table.
/// </summary>
public sealed class DatumIngestionTableResult
{
    /// <summary>The logical table name.</summary>
    public required string TableName { get; init; }

    /// <summary>
    /// The destination file name for this table's <c>.datum</c> file, including the extension.
    /// Must end with <c>.datum</c>.
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>
    /// The destination file name for this table's <c>.datum-index</c> file, including the extension.
    /// Must end with <c>.datum-index</c>.
    /// </summary>
    public required string IndexFileName { get; init; }

    /// <summary>
    /// The destination file name for this table's <c>.datum-manifest</c> file, including the extension.
    /// Must end with <c>.datum-manifest</c>.
    /// </summary>
    public required string ManifestFileName { get; init; }

    /// <summary>The discovered schema for this table.</summary>
    public required Schema Schema { get; init; }

    /// <summary>
    /// The single-table manifest for this table, wrapped in a <see cref="SourceManifest"/>
    /// whose <see cref="SourceManifest.Tables"/> dictionary contains exactly one entry
    /// keyed by <see cref="TableName"/>.
    /// </summary>
    public required SourceManifest Manifest { get; init; }

    /// <summary>The in-memory source index for this table.</summary>
    public required SourceIndex Index { get; init; }

    /// <summary>
    /// Seekable <see cref="MemoryStream"/> containing the <c>.datum</c> file bytes,
    /// positioned at offset 0 and ready to upload.
    /// </summary>
    public required MemoryStream DatumStream { get; init; }

    /// <summary>
    /// Seekable <see cref="MemoryStream"/> containing the <c>.datum-index</c> file bytes,
    /// positioned at offset 0 and ready to upload.
    /// </summary>
    public required MemoryStream IndexStream { get; init; }

    /// <summary>Serialized single-table schema JSON.</summary>
    public required string SchemaJson { get; init; }

    /// <summary>Serialized single-table manifest JSON.</summary>
    public required string ManifestJson { get; init; }

    /// <summary>Total number of rows ingested for this table.</summary>
    public required long RowCount { get; init; }

    /// <summary>Number of feature columns in the manifest for this table.</summary>
    public required int FeatureCount { get; init; }

    /// <summary>Byte length of the <c>.datum</c> stream.</summary>
    public long DatumByteCount => DatumStream.Length;

    /// <summary>Byte length of the <c>.datum-index</c> stream.</summary>
    public long IndexByteCount => IndexStream.Length;

    /// <summary>Byte count of <see cref="SchemaJson"/> when UTF-8 encoded.</summary>
    public int SchemaByteCount => System.Text.Encoding.UTF8.GetByteCount(SchemaJson);

    /// <summary>Byte count of <see cref="ManifestJson"/> when UTF-8 encoded.</summary>
    public int ManifestByteCount => System.Text.Encoding.UTF8.GetByteCount(ManifestJson);
}