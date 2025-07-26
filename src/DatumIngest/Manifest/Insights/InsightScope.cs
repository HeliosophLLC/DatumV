namespace DatumIngest.Manifest.Insights;

/// <summary>
/// Describes the granularity of a <see cref="DatasetInsight"/>.
/// </summary>
public enum InsightScope
{
    /// <summary>Applies to a single feature/column.</summary>
    Feature,

    /// <summary>Applies to a pair of features (e.g., correlation-based redundancy).</summary>
    FeaturePair,

    /// <summary>Applies to a group of related features (e.g., correlated missingness cluster).</summary>
    FeatureGroup,

    /// <summary>Applies to the dataset as a whole.</summary>
    Dataset,

    /// <summary>Applies across multiple manifests (tables) — e.g., join candidates, schema drift.</summary>
    CrossManifest
}
