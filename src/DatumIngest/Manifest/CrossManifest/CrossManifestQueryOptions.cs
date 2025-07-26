namespace DatumIngest.Manifest.CrossManifest;

/// <summary>
/// Options controlling cross-manifest JOIN SQL generation.
/// </summary>
public sealed class CrossManifestQueryOptions
{
    /// <summary>
    /// Minimum confidence for a candidate to be included in the generated SQL.
    /// Defaults to <see cref="CrossManifestThresholds.GraphEdgeMinConfidence"/>.
    /// </summary>
    public double MinConfidence { get; init; } = 0.5;

    /// <summary>
    /// Whether to include quality annotations as SQL comments.
    /// </summary>
    public bool IncludeAnnotations { get; init; } = true;

    /// <summary>
    /// Whether to use LEFT JOIN instead of INNER JOIN for candidates with high null-key ratios.
    /// </summary>
    public bool UseLeftJoinForNullableKeys { get; init; } = true;

    /// <summary>
    /// Null-key ratio above which LEFT JOIN is preferred over INNER JOIN
    /// (when <see cref="UseLeftJoinForNullableKeys"/> is enabled).
    /// </summary>
    public double LeftJoinNullKeyThreshold { get; init; } = 0.1;

    /// <summary>
    /// Gets the default options.
    /// </summary>
    public static CrossManifestQueryOptions Default { get; } = new();
}
