using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Indexing;

namespace DatumIngest;

/// <summary>
/// The output of a <see cref="DatumIngester"/> run: streams ready to upload,
/// in-memory schema and statistics, serialized JSON payloads, and summary counts.
/// Dispose when uploads are complete.
/// </summary>
public sealed class DatumIngestionResult : IAsyncDisposable
{
    /// <summary>
    /// Gets the per-table ingestion results, keyed by logical table name.
    /// Single-table sources have one entry; multi-table sources have one per discovered sub-table.
    /// </summary>
    public required IReadOnlyDictionary<string, DatumIngestionTableResult> Tables { get; init; }

    /// <summary>
    /// Gets the combined source schema covering all ingested tables.
    /// </summary>
    public required SourceSchema SourceSchema { get; init; }

    /// <summary>
    /// Gets the combined source manifest covering all ingested tables.
    /// </summary>
    public required SourceManifest SourceManifest { get; init; }

    /// <summary>
    /// Gets the combined source index set covering all ingested tables.
    /// </summary>
    public required SourceIndexSet IndexSet { get; init; }

    /// <summary>
    /// The logical table name for single-table sources.
    /// Throws when the source expands to multiple logical tables.
    /// </summary>
    public string TableName => GetSingleTableResult().TableName;

    /// <summary>
    /// The discovered schema for single-table sources.
    /// Throws when the source expands to multiple logical tables.
    /// </summary>
    public Schema Schema => GetSingleTableResult().Schema;

    /// <summary>
    /// The collected manifest for single-table sources.
    /// Throws when the source expands to multiple logical tables.
    /// </summary>
    public QueryResultsManifest Manifest => GetSingleTableResult().Manifest;

    /// <summary>
    /// The in-memory index for single-table sources.
    /// Throws when the source expands to multiple logical tables.
    /// </summary>
    public SourceIndex Index => GetSingleTableResult().Index;

    /// <summary>
    /// Seekable <see cref="MemoryStream"/> containing the <c>.datum</c> file bytes,
    /// positioned at offset 0 and ready to upload.
    /// Throws when the source expands to multiple logical tables.
    /// </summary>
    public MemoryStream DatumStream => GetSingleTableResult().DatumStream;

    /// <summary>
    /// Seekable <see cref="MemoryStream"/> containing the <c>.datum-index</c> file bytes,
    /// positioned at offset 0 and ready to upload.
    /// Throws when the source expands to multiple logical tables.
    /// </summary>
    public MemoryStream IndexStream => GetSingleTableResult().IndexStream;

    /// <summary>Serialized <c>SourceSchema</c> JSON for all ingested tables.</summary>
    public string SchemaJson { get; init; } = string.Empty;

    /// <summary>Serialized <c>SourceManifest</c> JSON containing column statistics.</summary>
    public string ManifestJson { get; init; } = string.Empty;

    /// <summary>Total number of rows ingested across all tables.</summary>
    public long RowCount => Tables.Values.Sum(table => table.RowCount);

    /// <summary>Number of feature columns across all manifests.</summary>
    public int FeatureCount => Tables.Values.Sum(table => table.FeatureCount);

    /// <summary>Total byte length of all <c>.datum</c> streams.</summary>
    public long DatumByteCount => Tables.Values.Sum(table => table.DatumByteCount);

    /// <summary>Total byte length of all <c>.datum-index</c> streams.</summary>
    public long IndexByteCount => Tables.Values.Sum(table => table.IndexByteCount);

    /// <summary>Byte count of <see cref="SchemaJson"/> when UTF-8 encoded.</summary>
    public int SchemaByteCount => System.Text.Encoding.UTF8.GetByteCount(SchemaJson);

    /// <summary>Byte count of <see cref="ManifestJson"/> when UTF-8 encoded.</summary>
    public int ManifestByteCount => System.Text.Encoding.UTF8.GetByteCount(ManifestJson);

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        foreach (DatumIngestionTableResult table in Tables.Values)
        {
            table.DatumStream.Dispose();
            table.IndexStream.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private DatumIngestionTableResult GetSingleTableResult()
    {
        if (Tables.Count != 1)
        {
            throw new InvalidOperationException(
                $"This ingestion produced {Tables.Count} tables. Use the Tables collection for multi-table sources.");
        }

        return Tables.Values.First();
    }
}
