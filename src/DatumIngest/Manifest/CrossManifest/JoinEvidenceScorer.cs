namespace DatumIngest.Manifest.CrossManifest;

/// <summary>
/// Computes full <see cref="JoinEvidence"/> for a column pair, combining name similarity,
/// type compatibility, TopK Jaccard, cardinality ratio, range overlap, null-key ratio,
/// and unique key detection into a weighted composite confidence.
/// </summary>
internal static class JoinEvidenceScorer
{
    /// <summary>
    /// Minimum NDV-to-row-count ratio for a column to be considered a unique key.
    /// </summary>
    private const double UniqueKeyThreshold = 0.95;

    /// <summary>
    /// Confidence multiplier when <see cref="JoinEvidence.TypeCompatibility"/> is zero
    /// (incompatible types require a cast).
    /// </summary>
    private const double TypeIncompatibilityPenalty = 0.7;

    /// <summary>
    /// Cardinality ratio below which a scaling penalty is applied. A ratio below 0.1
    /// means one side has at least 10× more distinct values, suggesting different domains.
    /// </summary>
    private const double CardinalityPenaltyThreshold = 0.1;

    /// <summary>
    /// Name similarity at or above which the name-only evidence is considered strong
    /// enough to not penalise zero value overlap.
    /// </summary>
    private const double StrongNameThreshold = 0.8;

    /// <summary>
    /// Confidence multiplier when TopK Jaccard is zero and name similarity is weak.
    /// </summary>
    private const double ZeroOverlapWeakNamePenalty = 0.85;

    /// <summary>
    /// Name similarity below which a scaling penalty is applied. Names too dissimilar
    /// suggest the columns were not designed as a join pair.
    /// </summary>
    private const double WeakNameThreshold = 0.5;

    /// <summary>
    /// Floor multiplier for the weak-name scaling penalty (applied when
    /// <see cref="WeakNameThreshold"/> is not met and name similarity is zero).
    /// </summary>
    private const double WeakNameMinScale = 0.3;

    /// <summary>
    /// Confidence multiplier when neither side of the join has a unique key,
    /// indicating a many-to-many relationship that is rarely intentional.
    /// </summary>
    private const double ManyToManyPenalty = 0.7;

    /// <summary>
    /// Unique key score below which the <see cref="ManyToManyPenalty"/> is applied.
    /// A score below 0.1 means neither column is close to being a primary key.
    /// </summary>
    private const double ManyToManyUniqueKeyThreshold = 0.1;

    /// <summary>
    /// Computes the full join evidence for a candidate column pair.
    /// </summary>
    /// <param name="left">Feature manifest for the left column.</param>
    /// <param name="leftRowCount">Total row count of the left table.</param>
    /// <param name="right">Feature manifest for the right column.</param>
    /// <param name="rightRowCount">Total row count of the right table.</param>
    /// <param name="matchCandidate">Pre-computed name similarity and type compatibility.</param>
    /// <param name="thresholds">Evidence weights for composite scoring.</param>
    /// <returns>The computed join evidence with composite confidence.</returns>
    internal static JoinEvidence ScoreEvidence(
        FeatureManifest left,
        long leftRowCount,
        FeatureManifest right,
        long rightRowCount,
        ColumnMatchCandidate matchCandidate,
        CrossManifestThresholds thresholds)
    {
        double topKJaccard = ComputeTopKJaccard(left, right);
        double cardinalityRatio = ComputeCardinalityRatio(left, right);
        double? rangeOverlap = ComputeRangeOverlap(left, right);
        double nullKeyRatio = ComputeNullKeyRatio(left, right);
        double uniqueKeyScore = ComputeUniqueKeyScore(left, leftRowCount, right, rightRowCount);

        double compositeConfidence = ComputeCompositeConfidence(
            matchCandidate.NameSimilarity,
            matchCandidate.TypeCompatibility,
            topKJaccard,
            cardinalityRatio,
            rangeOverlap,
            uniqueKeyScore,
            thresholds);

        return new JoinEvidence
        {
            NameSimilarity = matchCandidate.NameSimilarity,
            TypeCompatibility = matchCandidate.TypeCompatibility,
            TopKJaccard = topKJaccard,
            CardinalityRatio = cardinalityRatio,
            RangeOverlap = rangeOverlap,
            NullKeyRatio = nullKeyRatio,
            UniqueKeyScore = uniqueKeyScore,
            CompositeConfidence = compositeConfidence,
        };
    }

    /// <summary>
    /// Classifies the expected join cardinality based on uniqueness of each side.
    /// </summary>
    internal static JoinClassification ClassifyJoin(
        FeatureManifest left,
        long leftRowCount,
        FeatureManifest right,
        long rightRowCount)
    {
        bool leftUnique = IsUniqueKey(left, leftRowCount);
        bool rightUnique = IsUniqueKey(right, rightRowCount);

        return (leftUnique, rightUnique) switch
        {
            (true, true) => JoinClassification.OneToOne,
            (true, false) => JoinClassification.OneToMany,
            (false, true) => JoinClassification.ManyToOne,
            (false, false) => JoinClassification.ManyToMany,
        };
    }

    /// <summary>
    /// Estimates the average number of right-side rows matched per left-side row.
    /// Returns null if estimation is not possible.
    /// </summary>
    internal static double? EstimateFanout(
        FeatureManifest left,
        long leftRowCount,
        FeatureManifest right,
        long rightRowCount)
    {
        long rightNdv = right.EstimatedDistinctCount;

        if (rightNdv <= 0)
        {
            return null;
        }

        // Average fanout ≈ rightRowCount / rightNDV for the matched key.
        return (double)rightRowCount / rightNdv;
    }

    /// <summary>
    /// Computes Jaccard similarity of the TopK value sets.
    /// Case-insensitive for string comparisons. For numeric columns with non-integer
    /// values, TopK overlap is unreliable and returns 0.
    /// </summary>
    internal static double ComputeTopKJaccard(FeatureManifest left, FeatureManifest right)
    {
        IReadOnlyList<FrequencyEntry>? leftTopK = left.TopKValues;
        IReadOnlyList<FrequencyEntry>? rightTopK = right.TopKValues;

        if (leftTopK is null or { Count: 0 } || rightTopK is null or { Count: 0 })
        {
            return 0.0;
        }

        // For continuous numerics (non-integer), TopK Jaccard is unreliable.
        if (left is NumericFeatureManifest leftNumeric &&
            right is NumericFeatureManifest rightNumeric &&
            !leftNumeric.IntegerValued && !rightNumeric.IntegerValued)
        {
            return 0.0;
        }

        HashSet<string> leftValues = new(StringComparer.OrdinalIgnoreCase);

        foreach (FrequencyEntry entry in leftTopK)
        {
            leftValues.Add(entry.Value);
        }

        HashSet<string> rightValues = new(StringComparer.OrdinalIgnoreCase);

        foreach (FrequencyEntry entry in rightTopK)
        {
            rightValues.Add(entry.Value);
        }

        int intersection = 0;

        foreach (string value in leftValues)
        {
            if (rightValues.Contains(value))
            {
                intersection++;
            }
        }

        int union = leftValues.Count + rightValues.Count - intersection;

        return union == 0 ? 0.0 : (double)intersection / union;
    }

    /// <summary>
    /// Computes min(leftNDV, rightNDV) / max(leftNDV, rightNDV).
    /// Close to 1.0 suggests both columns draw from the same domain.
    /// </summary>
    private static double ComputeCardinalityRatio(FeatureManifest left, FeatureManifest right)
    {
        long leftNdv = left.EstimatedDistinctCount;
        long rightNdv = right.EstimatedDistinctCount;

        if (leftNdv <= 0 || rightNdv <= 0)
        {
            return 0.0;
        }

        long minNdv = Math.Min(leftNdv, rightNdv);
        long maxNdv = Math.Max(leftNdv, rightNdv);

        return (double)minNdv / maxNdv;
    }

    /// <summary>
    /// Computes the overlap ratio of numeric ranges: intersection / union of [min, max].
    /// Returns null for non-numeric columns.
    /// </summary>
    private static double? ComputeRangeOverlap(FeatureManifest left, FeatureManifest right)
    {
        if (left is not NumericFeatureManifest leftNumeric ||
            right is not NumericFeatureManifest rightNumeric)
        {
            return null;
        }

        double intersectionStart = Math.Max(leftNumeric.Min, rightNumeric.Min);
        double intersectionEnd = Math.Min(leftNumeric.Max, rightNumeric.Max);

        if (intersectionStart > intersectionEnd)
        {
            return 0.0; // Disjoint ranges.
        }

        double unionStart = Math.Min(leftNumeric.Min, rightNumeric.Min);
        double unionEnd = Math.Max(leftNumeric.Max, rightNumeric.Max);
        double unionLength = unionEnd - unionStart;

        if (unionLength <= 0.0)
        {
            // Both columns are constant with the same value.
            return 1.0;
        }

        double intersectionLength = intersectionEnd - intersectionStart;

        return intersectionLength / unionLength;
    }

    /// <summary>
    /// Returns max(left.NullRatio, right.NullRatio).
    /// </summary>
    private static double ComputeNullKeyRatio(FeatureManifest left, FeatureManifest right)
    {
        double leftNull = left.NullRatio ?? 0.0;
        double rightNull = right.NullRatio ?? 0.0;

        return Math.Max(leftNull, rightNull);
    }

    /// <summary>
    /// Returns 1.0 if at least one side has NDV / RowCount ≥ 0.95, indicating a primary key.
    /// </summary>
    private static double ComputeUniqueKeyScore(
        FeatureManifest left,
        long leftRowCount,
        FeatureManifest right,
        long rightRowCount)
    {
        if (IsUniqueKey(left, leftRowCount) || IsUniqueKey(right, rightRowCount))
        {
            return 1.0;
        }

        // Partial credit for near-unique columns.
        double leftRatio = leftRowCount > 0
            ? (double)left.EstimatedDistinctCount / leftRowCount
            : 0.0;
        double rightRatio = rightRowCount > 0
            ? (double)right.EstimatedDistinctCount / rightRowCount
            : 0.0;

        return Math.Max(leftRatio, rightRatio);
    }

    private static bool IsUniqueKey(FeatureManifest feature, long rowCount)
    {
        if (rowCount <= 0)
        {
            return false;
        }

        return (double)feature.EstimatedDistinctCount / rowCount >= UniqueKeyThreshold;
    }

    /// <summary>
    /// Computes the weighted composite confidence from all evidence signals.
    /// When range overlap is null (non-numeric), its weight is redistributed
    /// proportionally across other signals. After the weighted average, multiplicative
    /// penalties are applied for type incompatibility, cardinality mismatch,
    /// zero value overlap, and weak name similarity.
    /// </summary>
    private static double ComputeCompositeConfidence(
        double nameSimilarity,
        double typeCompatibility,
        double topKJaccard,
        double cardinalityRatio,
        double? rangeOverlap,
        double uniqueKeyScore,
        CrossManifestThresholds thresholds)
    {
        double totalWeight;
        double weightedSum;

        if (rangeOverlap.HasValue)
        {
            totalWeight = thresholds.WeightNameSimilarity
                + thresholds.WeightTypeCompatibility
                + thresholds.WeightTopKJaccard
                + thresholds.WeightCardinalityRatio
                + thresholds.WeightRangeOverlap
                + thresholds.WeightUniqueKeyScore;

            weightedSum = (nameSimilarity * thresholds.WeightNameSimilarity)
                + (typeCompatibility * thresholds.WeightTypeCompatibility)
                + (topKJaccard * thresholds.WeightTopKJaccard)
                + (cardinalityRatio * thresholds.WeightCardinalityRatio)
                + (rangeOverlap.Value * thresholds.WeightRangeOverlap)
                + (uniqueKeyScore * thresholds.WeightUniqueKeyScore);
        }
        else
        {
            // Non-numeric: redistribute range overlap weight.
            totalWeight = thresholds.WeightNameSimilarity
                + thresholds.WeightTypeCompatibility
                + thresholds.WeightTopKJaccard
                + thresholds.WeightCardinalityRatio
                + thresholds.WeightUniqueKeyScore;

            weightedSum = (nameSimilarity * thresholds.WeightNameSimilarity)
                + (typeCompatibility * thresholds.WeightTypeCompatibility)
                + (topKJaccard * thresholds.WeightTopKJaccard)
                + (cardinalityRatio * thresholds.WeightCardinalityRatio)
                + (uniqueKeyScore * thresholds.WeightUniqueKeyScore);
        }

        double confidence = totalWeight > 0.0 ? weightedSum / totalWeight : 0.0;

        confidence = ApplyMultiplicativePenalties(
            confidence, nameSimilarity, typeCompatibility, topKJaccard, cardinalityRatio, uniqueKeyScore);

        return confidence;
    }

    /// <summary>
    /// Applies multiplicative penalty factors that gate confidence for structurally
    /// implausible joins. These catch cases where a few coincidentally high signals
    /// (e.g. same type + one unique side) inflate the weighted average above the
    /// candidate threshold.
    /// </summary>
    private static double ApplyMultiplicativePenalties(
        double confidence,
        double nameSimilarity,
        double typeCompatibility,
        double topKJaccard,
        double cardinalityRatio,
        double uniqueKeyScore)
    {
        // Incompatible types require a cast — penalise regardless of name match.
        if (typeCompatibility == 0.0)
        {
            confidence *= TypeIncompatibilityPenalty;
        }

        // Large cardinality mismatch (< 10:1 NDV ratio) suggests different domains.
        // Scales from 0.5× at ratio=0 to 1.0× at ratio=0.1.
        if (cardinalityRatio < CardinalityPenaltyThreshold)
        {
            double scale = 0.5 + (cardinalityRatio / CardinalityPenaltyThreshold * 0.5);
            confidence *= scale;
        }

        // No overlapping values AND weak name evidence — likely coincidental match.
        if (topKJaccard == 0.0 && nameSimilarity < StrongNameThreshold)
        {
            confidence *= ZeroOverlapWeakNamePenalty;
        }

        // Very different column names — names are the strongest structural signal.
        // Scales from 0.3× at name=0 to 1.0× at name=0.5.
        if (nameSimilarity < WeakNameThreshold)
        {
            double scale = WeakNameMinScale + ((1.0 - WeakNameMinScale) * nameSimilarity / WeakNameThreshold);
            confidence *= scale;
        }

        // Neither side is a unique key — likely many-to-many, rarely a real join.
        if (uniqueKeyScore < ManyToManyUniqueKeyThreshold)
        {
            confidence *= ManyToManyPenalty;
        }

        return confidence;
    }
}
