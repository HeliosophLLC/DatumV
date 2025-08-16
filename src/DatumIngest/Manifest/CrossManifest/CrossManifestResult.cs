namespace DatumIngest.Manifest.CrossManifest;

using DatumIngest.Manifest.Insights;

/// <summary>
/// Top-level result of cross-manifest analysis containing join candidates,
/// join graphs (primary + alternates for equivalent table partitions),
/// transitive chains, insights, and recommended SQL.
/// </summary>
public sealed class CrossManifestResult
{
    /// <summary>Gets the names of the tables that were analyzed.</summary>
    public required IReadOnlyList<string> Tables { get; init; }

    /// <summary>Gets all discovered join candidates, sorted by confidence descending.</summary>
    public required IReadOnlyList<JoinCandidate> Candidates { get; init; }

    /// <summary>
    /// Gets the join graphs. The first entry is the primary (recommended) graph.
    /// Additional entries represent alternate graphs created by substituting equivalent
    /// table partitions (e.g., train/test splits). Each graph carries its own edges,
    /// recommended query, and annotations.
    /// </summary>
    public required IReadOnlyList<JoinGraph> JoinGraphs { get; init; }

    /// <summary>
    /// Gets groups of tables that share identical or near-identical schemas and connect
    /// to the same hub tables. Null if no equivalent table groups were detected.
    /// </summary>
    public IReadOnlyList<EquivalentTableGroup>? EquivalentTableGroups { get; init; }

    /// <summary>Gets transitive join chains discovered in the primary graph. Null if none found.</summary>
    public IReadOnlyList<JoinChain>? TransitiveChains { get; init; }

    /// <summary>Gets cross-manifest insights (join quality, schema drift, normalization hints). Null if none.</summary>
    public IReadOnlyList<DatasetInsight>? Insights { get; init; }

    /// <summary>
    /// Gets per-table column insights from single-manifest analysis. Each entry maps a
    /// table name to its column-level insights (nullity, skew, encoding, outliers, etc.).
    /// Present for all tables, including single-table results with no join candidates.
    /// Null if no per-table insights were found.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<DatasetInsight>>? PerTableInsights { get; init; }
}
