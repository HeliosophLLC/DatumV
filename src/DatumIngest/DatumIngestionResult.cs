using DatumIngest.Indexing;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest;

/// <summary>
/// The output of a <see cref="DatumIngester.IngestAsync(string, Action{IngestionProgress}?, CancellationToken)"/> call: <c>.datum</c> streams,
/// per-table schemas and statistics, serialized JSON payloads, and summary counts.
/// Dispose when uploads are complete.
/// </summary>
/// <remarks>
/// This result does not contain indexes. Call
/// <see cref="DatumIngester.BuildIndexAsync(string, DatumIndexerOptions?, Action{IndexingProgress}?, CancellationToken)"/>
/// on the produced <c>.datum</c> file to build indexes separately.
/// </remarks>
public sealed class DatumIngestionResult : IAsyncDisposable
{
    /// <summary>
    /// Gets the source fingerprint shared by all ingested tables.
    /// </summary>
    public required SourceFingerprint Fingerprint { get; init; }

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

    /// <summary>Serialized <c>SourceSchema</c> JSON for all ingested tables.</summary>
    public string SchemaJson { get; init; } = string.Empty;

    /// <summary>Serialized <c>SourceManifest</c> JSON containing column statistics.</summary>
    public string ManifestJson { get; init; } = string.Empty;

    /// <summary>
    /// Gets the combined source vocabulary set covering all ingested tables,
    /// or <c>null</c> when no columns have attached vocabularies.
    /// </summary>
    public SourceVocabularySet? SourceVocabularySet { get; init; }

    /// <summary>
    /// Serialized <c>SourceVocabularySet</c> JSON, or <c>null</c> when no columns
    /// have attached vocabularies.
    /// </summary>
    public string? VocabularyJson { get; init; }

    /// <summary>Total number of rows ingested across all tables.</summary>
    public long RowCount => Tables.Values.Sum(table => table.RowCount);

    /// <summary>Number of feature columns across all manifests.</summary>
    public int FeatureCount => Tables.Values.Sum(table => table.FeatureCount);

    /// <summary>Total byte length of all <c>.datum</c> streams.</summary>
    public long DatumByteCount => Tables.Values.Sum(table => table.DatumByteCount);

    /// <summary>Byte count of <see cref="SchemaJson"/> when UTF-8 encoded.</summary>
    public int SchemaByteCount => System.Text.Encoding.UTF8.GetByteCount(SchemaJson);

    /// <summary>Byte count of <see cref="ManifestJson"/> when UTF-8 encoded.</summary>
    public int ManifestByteCount => System.Text.Encoding.UTF8.GetByteCount(ManifestJson);

    /// <summary>
    /// Gets the per-table sample previews, keyed by logical table name.
    /// Each preview contains a representative subset of rows collected via reservoir
    /// sampling during ingestion.
    /// </summary>
    public IReadOnlyDictionary<string, SamplePreview> Samples { get; init; } =
        new Dictionary<string, SamplePreview>(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        foreach (DatumIngestionTableResult table in Tables.Values)
        {
            table.DatumStream.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
