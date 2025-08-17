namespace DatumIngest.Manifest.SchemaMatching;

/// <summary>
/// Configurable thresholds and evidence weights for schema matching. Controls
/// column matching sensitivity, candidate filtering, and composite confidence scoring.
/// </summary>
public sealed class SchemaMatchingThresholds
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

    // ── Candidate Filtering ──

    /// <summary>
    /// Minimum composite confidence for a join candidate to be included in results.
    /// Default: 0.45.
    /// </summary>
    public double CandidateMinConfidence { get; init; } = 0.45;

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

    // ── Gated Scoring Floors ──

    /// <summary>
    /// Minimum joinability prior (role compatibility + name hints) for a candidate
    /// to survive evidence scoring. Candidates below this floor receive a hard-zero
    /// composite confidence.
    /// Default: 0.3.
    /// </summary>
    public double JoinabilityPriorFloor { get; init; } = 0.3;

    /// <summary>
    /// Minimum identity evidence (TopK overlap + unique-key signal + cardinality
    /// agreement) for a candidate to survive scoring. Below floor → hard zero.
    /// Default: 0.1 (set conservatively to accommodate partition pairs with
    /// zero TopK overlap and low unique-key scores).
    /// </summary>
    public double IdentityEvidenceFloor { get; init; } = 0.1;

    /// <summary>
    /// Minimum structural compatibility (type coercion + range agreement) for a
    /// candidate to survive scoring. Below floor → hard zero.
    /// Default: 0.2.
    /// </summary>
    public double StructuralCompatibilityFloor { get; init; } = 0.2;

    /// <summary>Default threshold values.</summary>
    public static SchemaMatchingThresholds Default { get; } = new();
}
