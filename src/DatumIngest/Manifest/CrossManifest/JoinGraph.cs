namespace DatumIngest.Manifest.CrossManifest;

using DatumIngest.Manifest.Insights;

/// <summary>
/// A complete join graph representing one way to join the analyzed tables.
/// The primary graph (first in the <see cref="CrossManifestResult.JoinGraphs"/> list)
/// uses the preferred table from each <see cref="EquivalentTableGroup"/>. Alternate
/// graphs substitute non-preferred tables and carry their own recommended SQL.
/// </summary>
public sealed class JoinGraph
{
    /// <summary>
    /// Gets a short label identifying this graph variant. Null for the primary graph
    /// when no equivalent tables exist.
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// Gets a human-readable explanation of why this graph exists or which table
    /// substitution it represents. Null for the primary graph when no equivalent tables exist.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>Gets the edges in this join graph.</summary>
    public required IReadOnlyList<JoinGraphEdge> Edges { get; init; }

    /// <summary>
    /// Gets the table names excluded from this graph (the non-preferred equivalents).
    /// Null when no tables were excluded.
    /// </summary>
    public IReadOnlyList<string>? ExcludedTables { get; init; }

    /// <summary>
    /// Gets the recommended JOIN SQL query for this graph.
    /// Null if no candidates exceed the confidence threshold.
    /// </summary>
    public string? RecommendedQuery { get; init; }

    /// <summary>Gets annotations mapping joined columns to their originating candidates.</summary>
    public IReadOnlyList<QueryAnnotation>? QueryAnnotations { get; init; }

    /// <summary>
    /// Gets the estimated row count for the bridge/fact table in this graph.
    /// Useful for inferring train/test split sizes. Null when not applicable.
    /// </summary>
    public long? EstimatedRowCount { get; init; }
}
