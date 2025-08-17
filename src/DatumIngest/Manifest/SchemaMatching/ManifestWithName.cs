namespace DatumIngest.Manifest.SchemaMatching;

/// <summary>
/// Pairs a <see cref="QueryResultsManifest"/> with the table name it was generated from.
/// Used as input to cross-manifest analysis for multi-table join detection.
/// </summary>
/// <param name="Name">The table name (e.g., file stem or catalog name).</param>
/// <param name="Manifest">The statistical manifest for this table.</param>
public sealed record ManifestWithName(
    string Name,
    QueryResultsManifest Manifest);
