namespace DatumIngest.Manifest;

/// <summary>
/// Container for per-sub-table manifests within a single source file.
/// A <c>.datum-manifest</c> sidecar always contains a <see cref="SourceManifest"/>,
/// even for single-table sources — the sole table uses an empty-string key.
/// </summary>
/// <remarks>
/// <para>
/// When a source file exposes multiple logical tables (e.g. a JSON file with several
/// top-level array properties), each table's statistics are stored under its sub-table
/// qualifier key (the leaf property name). Single-table sources use <c>""</c> as the key.
/// </para>
/// <para>
/// Use <see cref="Create"/> to wrap a single <see cref="QueryResultsManifest"/> with
/// the default empty key.
/// </para>
/// </remarks>
public sealed class SourceManifest
{
    /// <summary>
    /// Gets the per-sub-table manifests, keyed by sub-table qualifier.
    /// An empty string key represents the sole or default table in the source file.
    /// Non-empty keys represent sub-table qualifiers (e.g. JSON property names).
    /// </summary>
    public required IReadOnlyDictionary<string, QueryResultsManifest> Tables { get; init; }

    /// <summary>
    /// Creates a <see cref="SourceManifest"/> wrapping a single <see cref="QueryResultsManifest"/>
    /// with an empty-string key.
    /// </summary>
    /// <param name="manifest">The single-table manifest.</param>
    /// <returns>A new source manifest with one entry keyed by <c>""</c>.</returns>
    public static SourceManifest Create(QueryResultsManifest manifest)
    {
        return new SourceManifest
        {
            Tables = new Dictionary<string, QueryResultsManifest> { [""] = manifest }
        };
    }
}
