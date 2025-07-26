namespace DatumIngest.Manifest.CrossManifest;

using DatumIngest.Manifest.Insights;

/// <summary>
/// Top-level result of cross-manifest analysis containing join candidates,
/// the join graph, transitive chains, insights, and recommended SQL.
/// </summary>
public sealed class CrossManifestResult
{
    /// <summary>Gets the names of the tables that were analyzed.</summary>
    public required IReadOnlyList<string> Tables { get; init; }

    /// <summary>Gets all discovered join candidates, sorted by confidence descending.</summary>
    public required IReadOnlyList<JoinCandidate> Candidates { get; init; }

    /// <summary>Gets the join graph edges (candidates above threshold).</summary>
    public required IReadOnlyList<JoinGraphEdge> JoinGraph { get; init; }

    /// <summary>Gets transitive join chains discovered in the graph. Null if none found.</summary>
    public IReadOnlyList<JoinChain>? TransitiveChains { get; init; }

    /// <summary>Gets cross-manifest insights (join quality, schema drift, normalization hints). Null if none.</summary>
    public IReadOnlyList<DatasetInsight>? Insights { get; init; }

    /// <summary>Gets the recommended JOIN SQL query. Null if no candidates exceed the confidence threshold.</summary>
    public string? RecommendedQuery { get; init; }

    /// <summary>Gets annotations mapping joined columns to their originating candidates.</summary>
    public IReadOnlyList<QueryAnnotation>? QueryAnnotations { get; init; }
}
