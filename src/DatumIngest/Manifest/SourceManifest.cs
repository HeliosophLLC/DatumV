namespace DatumIngest.Manifest;

/// <summary>
/// Container for per-table manifests within a single source file.
/// A <c>.datum-manifest</c> sidecar always contains a <see cref="SourceManifest"/>,
/// keyed by the logical table name used in the catalog.
/// </summary>
/// <remarks>
/// <para>
/// When a source file exposes multiple logical tables (e.g. a JSON file with several
/// top-level array properties), each table's statistics are stored under its fully
/// qualified catalog name (e.g. <c>"annotations"</c>, <c>"annotations.labels"</c>).
/// </para>
/// <para>
/// Use <see cref="Create"/> to wrap a single <see cref="QueryResultsManifest"/> with
/// its catalog table name.
/// </para>
/// </remarks>
public sealed class SourceManifest
{
    /// <summary>
    /// Gets the on-disk schema version of this manifest file. Defaults to
    /// <c>1</c> for files written before <see cref="ManifestSchemaVersion"/>
    /// existed (the field is absent in those files; <see cref="System.Text.Json"/>
    /// fills the default on deserialize). Bumped each time a new
    /// <see cref="FeatureManifest"/> subtype is registered.
    /// </summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// Gets the per-table manifests, keyed by catalog table name.
    /// Single-table sources have one entry; multi-table sources have one per sub-table.
    /// </summary>
    public required IReadOnlyDictionary<string, QueryResultsManifest> Tables { get; init; }

    /// <summary>
    /// Creates a <see cref="SourceManifest"/> wrapping a single <see cref="QueryResultsManifest"/>,
    /// stamping it with the current <see cref="ManifestSchemaVersion.Current"/>.
    /// </summary>
    /// <param name="tableName">Catalog table name used as the dictionary key.</param>
    /// <param name="manifest">The single-table manifest.</param>
    /// <returns>A new source manifest with one entry keyed by <paramref name="tableName"/>.</returns>
    public static SourceManifest Create(string tableName, QueryResultsManifest manifest)
    {
        return new SourceManifest
        {
            SchemaVersion = ManifestSchemaVersion.Current,
            Tables = new Dictionary<string, QueryResultsManifest> { [tableName] = manifest }
        };
    }
}
