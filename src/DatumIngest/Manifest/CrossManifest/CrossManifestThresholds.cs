namespace DatumIngest.Manifest.CrossManifest;

/// <summary>
/// Configurable thresholds and evidence weights for cross-manifest join analysis.
/// Controls column matching sensitivity, candidate filtering, and composite scoring.
/// </summary>
public sealed class CrossManifestThresholds
{
    // ── Column Matching ──

    /// <summary>
    /// Minimum normalized name similarity for a column pair to be considered a match candidate.
    /// Default: 0.4 (allows "country" ↔ "country_id" but rejects unrelated names).
    /// </summary>
    public double NameSimilarityMinThreshold { get; init; } = 0.4;

    /// <summary>
    /// Minimum type compatibility score for a column pair to be considered.
    /// 0.0 allows mismatched types if name similarity is high enough.
    /// Default: 0.0 (type mismatch alone does not disqualify).
    /// </summary>
    public double TypeCompatibilityMinThreshold { get; init; } = 0.0;

    // ── Evidence Scoring ──

    /// <summary>
    /// Maximum null-key ratio (either side) for a join to be considered viable.
    /// Default: 0.5 (more than 50% nulls makes the join effectively useless).
    /// </summary>
    public double NullKeyMaxRatio { get; init; } = 0.5;

    // ── Composite Key Detection ──

    /// <summary>
    /// Maximum number of columns in a composite key candidate.
    /// Default: 4.
    /// </summary>
    public int CompositeKeyMaxColumns { get; init; } = 4;

    /// <summary>
    /// Confidence penalty multiplier applied to composite key candidates.
    /// Individual column confidences are multiplied together, then scaled by this factor.
    /// Default: 0.8.
    /// </summary>
    public double CompositeKeyPenalty { get; init; } = 0.8;

    // ── Candidate Filtering ──

    /// <summary>
    /// Minimum composite confidence for a join candidate to be included in results.
    /// Default: 0.45.
    /// </summary>
    public double CandidateMinConfidence { get; init; } = 0.45;

    /// <summary>
    /// Minimum composite confidence for a join candidate to appear in the join graph.
    /// Should be ≥ <see cref="CandidateMinConfidence"/>.
    /// Default: 0.5.
    /// </summary>
    public double GraphEdgeMinConfidence { get; init; } = 0.5;

    // ── Chain Detection ──

    /// <summary>
    /// Maximum depth (number of hops) for transitive join chain detection.
    /// Default: 4 (A → B → C → D → E).
    /// </summary>
    public int ChainMaxDepth { get; init; } = 4;

    // ── Evidence Weights ──
    // Weights for the composite confidence calculation. Must sum to ~1.0.

    /// <summary>Weight for name similarity in composite confidence. Default: 0.40.</summary>
    public double WeightNameSimilarity { get; init; } = 0.40;

    /// <summary>Weight for type compatibility in composite confidence. Default: 0.10.</summary>
    public double WeightTypeCompatibility { get; init; } = 0.10;

    /// <summary>Weight for TopK Jaccard similarity in composite confidence. Default: 0.10.</summary>
    public double WeightTopKJaccard { get; init; } = 0.10;

    /// <summary>Weight for cardinality ratio in composite confidence. Default: 0.20.</summary>
    public double WeightCardinalityRatio { get; init; } = 0.20;

    /// <summary>Weight for numeric range overlap in composite confidence. Default: 0.15.</summary>
    public double WeightRangeOverlap { get; init; } = 0.15;

    /// <summary>Weight for unique key score in composite confidence. Default: 0.05.</summary>
    public double WeightUniqueKeyScore { get; init; } = 0.05;

    // ── Insight Thresholds ──

    /// <summary>
    /// Cardinality ratio below which a CardinalityMismatch insight is emitted.
    /// Default: 0.01 (100:1 ratio or worse).
    /// </summary>
    public double CardinalityMismatchMinRatio { get; init; } = 0.01;

    /// <summary>
    /// Null-key ratio above which a HighNullKey insight is emitted.
    /// Default: 0.3.
    /// </summary>
    public double HighNullKeyMinRatio { get; init; } = 0.3;

    /// <summary>
    /// Minimum number of FK-like relationships from a single table to qualify as a star schema hub.
    /// Default: 3.
    /// </summary>
    public int StarSchemaMinDimensions { get; init; } = 3;

    /// <summary>Default threshold values.</summary>
    public static CrossManifestThresholds Default { get; } = new();
}
