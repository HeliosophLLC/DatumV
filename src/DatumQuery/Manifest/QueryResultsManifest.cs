namespace Axon.QueryEngine.Manifest;

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
}
