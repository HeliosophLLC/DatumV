namespace DatumIngest.Manifest;

using DatumIngest.Manifest.Insights;

/// <summary>
/// Top-level manifest describing a query result set, containing one <see cref="FeatureManifest"/>
/// per column with kind-specific statistics.
/// </summary>
public sealed class QueryResultsManifest
{
    /// <summary>Gets the total number of rows in the result set.</summary>
    public required long RowCount { get; init; }

    /// <summary>Gets the UTC timestamp when this manifest was generated.</summary>
    public required DateTime GeneratedAtUtc { get; init; }

    /// <summary>Gets the per-column feature manifests.</summary>
    public required IReadOnlyList<FeatureManifest> Features { get; init; }

    /// <summary>Gets the pairwise column interaction statistics, or null if not computed.</summary>
    public IReadOnlyList<ColumnInteraction>? Interactions { get; init; }

    /// <summary>Gets the dataset insights derived from manifest analysis, or null if not computed.</summary>
    public IReadOnlyList<DatasetInsight>? Insights { get; init; }

    /// <summary>
    /// Gets the recommended DatumIngest SQL query containing only actions from insights
    /// with <see cref="ApplyMode.AutoSafe"/> or <see cref="ApplyMode.Suggest"/> apply modes.
    /// Null when no actionable insights exist.
    /// </summary>
    public string? RecommendedQuery { get; init; }

    /// <summary>
    /// Gets the full suggested query containing all actions (including <see cref="ApplyMode.ManualOnly"/>
    /// proposed actions). Intended for human review — not auto-applicable.
    /// Null when no actionable insights exist.
    /// </summary>
    public string? FullSuggestedQuery { get; init; }

    /// <summary>
    /// Gets annotations mapping each transformed column in the synthesized queries back to
    /// the insight that produced it. Null when no queries are synthesized.
    /// </summary>
    public IReadOnlyList<QueryAnnotation>? QueryAnnotations { get; init; }

    /// <summary>
    /// Gets per-column index type hints derived from manifest statistics.
    /// Consumed by <see cref="Indexing.SourceIndexBuilder"/> to guide index-type selection
    /// on subsequent ingestion runs. Null when no hints are available.
    /// </summary>
    public IReadOnlyList<ColumnIndexHint>? IndexHints { get; init; }
}
