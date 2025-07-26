namespace DatumIngest.Manifest.CrossManifest;

using System.Text.Json.Serialization;

/// <summary>
/// Per-signal evidence scores for a join candidate column pair.
/// Each signal captures a different aspect of join quality; the composite
/// confidence is a weighted combination of all signals.
/// </summary>
public sealed class JoinEvidence
{
    /// <summary>Gets the normalized name similarity between the two columns (0 = unrelated, 1 = exact match).</summary>
    public required double NameSimilarity { get; init; }

    /// <summary>Gets the type compatibility score (1.0 = exact match, 0.5–0.8 = coercible, 0.0 = incompatible).</summary>
    public required double TypeCompatibility { get; init; }

    /// <summary>Gets the Jaccard similarity of the TopK value sets (0 = disjoint, 1 = identical).</summary>
    public required double TopKJaccard { get; init; }

    /// <summary>Gets the cardinality ratio: min(leftNDV, rightNDV) / max(leftNDV, rightNDV). Close to 1.0 suggests same domain.</summary>
    public required double CardinalityRatio { get; init; }

    /// <summary>Gets the numeric range overlap: intersection / union of [min, max]. Null for non-numeric columns.</summary>
    public double? RangeOverlap { get; init; }

    /// <summary>Gets the maximum null ratio across both columns. High values indicate risky join keys.</summary>
    public required double NullKeyRatio { get; init; }

    /// <summary>Gets the unique key score (1.0 if NDV ≈ RowCount on at least one side, 0.0 otherwise).</summary>
    public required double UniqueKeyScore { get; init; }

    /// <summary>Gets the weighted composite confidence combining all signals.</summary>
    public required double CompositeConfidence { get; init; }
}
