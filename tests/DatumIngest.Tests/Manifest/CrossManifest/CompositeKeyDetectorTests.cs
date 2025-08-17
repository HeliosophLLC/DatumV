namespace DatumIngest.Tests.Manifest.CrossManifest;

using DatumIngest.Manifest;
using DatumIngest.Manifest.CrossManifest;
using DatumIngest.Model;

/// <summary>
/// Tests for <see cref="CompositeKeyDetector"/> — multi-column composite key detection
/// from single-column join candidates.
/// </summary>
public sealed class CompositeKeyDetectorTests
{
    [Fact]
    public void DetectCompositeKeys_TwoColumns_ProducesComposite()
    {
        List<JoinCandidate> singles =
        [
            MakeSingleCandidate("orders", "customers", "region_id", "region_id", 0.9, uniqueKeyScore: 0.5),
            MakeSingleCandidate("orders", "customers", "year", "year", 0.85, uniqueKeyScore: 0.5),
        ];

        IReadOnlyList<JoinCandidate> composites =
            CompositeKeyDetector.DetectCompositeKeys(singles, CrossManifestThresholds.Default);

        Assert.Single(composites);
        JoinCandidate composite = composites[0];
        Assert.Equal(2, composite.LeftColumns.Count);
        Assert.Equal(2, composite.RightColumns.Count);
        Assert.Contains("region_id", composite.LeftColumns);
        Assert.Contains("year", composite.LeftColumns);
    }

    [Fact]
    public void DetectCompositeKeys_SingleColumn_ReturnsEmpty()
    {
        List<JoinCandidate> singles =
        [
            MakeSingleCandidate("orders", "customers", "customer_id", "customer_id", 0.8),
        ];

        IReadOnlyList<JoinCandidate> composites =
            CompositeKeyDetector.DetectCompositeKeys(singles, CrossManifestThresholds.Default);

        Assert.Empty(composites);
    }

    [Fact]
    public void DetectCompositeKeys_IndependentlyUnique_SkipsComposite()
    {
        // If one column is already a unique key, no composite is needed.
        List<JoinCandidate> singles =
        [
            MakeSingleCandidate("orders", "customers", "customer_id", "customer_id", 0.9, uniqueKeyScore: 0.98),
            MakeSingleCandidate("orders", "customers", "region_id", "region_id", 0.5, uniqueKeyScore: 0.3),
        ];

        IReadOnlyList<JoinCandidate> composites =
            CompositeKeyDetector.DetectCompositeKeys(singles, CrossManifestThresholds.Default);

        Assert.Empty(composites);
    }

    [Fact]
    public void DetectCompositeKeys_DifferentTablePairs_IndependentComposites()
    {
        List<JoinCandidate> singles =
        [
            MakeSingleCandidate("orders", "customers", "region_id", "region_id", 0.9, uniqueKeyScore: 0.5),
            MakeSingleCandidate("orders", "customers", "year", "year", 0.85, uniqueKeyScore: 0.5),
            MakeSingleCandidate("products", "categories", "dept_id", "dept_id", 0.9, uniqueKeyScore: 0.4),
            MakeSingleCandidate("products", "categories", "brand_id", "brand_id", 0.85, uniqueKeyScore: 0.4),
        ];

        IReadOnlyList<JoinCandidate> composites =
            CompositeKeyDetector.DetectCompositeKeys(singles, CrossManifestThresholds.Default);

        Assert.Equal(2, composites.Count);
    }

    [Fact]
    public void DetectCompositeKeys_AppliesPenalty()
    {
        List<JoinCandidate> singles =
        [
            MakeSingleCandidate("orders", "customers", "region_id", "region_id", 0.8, uniqueKeyScore: 0.5),
            MakeSingleCandidate("orders", "customers", "year", "year", 0.8, uniqueKeyScore: 0.5),
        ];

        IReadOnlyList<JoinCandidate> composites =
            CompositeKeyDetector.DetectCompositeKeys(singles, CrossManifestThresholds.Default);

        Assert.Single(composites);
        // Composite confidence = product of individual confidences × 0.8 penalty.
        // 0.8 × 0.8 × 0.8 = 0.512.
        Assert.True(composites[0].Confidence < 0.8);
    }

    [Fact]
    public void DetectCompositeKeys_RespectsMaxColumns()
    {
        CrossManifestThresholds thresholds = new() { CompositeKeyMaxColumns = 2 };

        List<JoinCandidate> singles =
        [
            MakeSingleCandidate("orders", "customers", "a", "a", 0.9, uniqueKeyScore: 0.3),
            MakeSingleCandidate("orders", "customers", "b", "b", 0.85, uniqueKeyScore: 0.3),
            MakeSingleCandidate("orders", "customers", "c", "c", 0.8, uniqueKeyScore: 0.3),
        ];

        IReadOnlyList<JoinCandidate> composites =
            CompositeKeyDetector.DetectCompositeKeys(singles, thresholds);

        Assert.Single(composites);
        // Should take at most 2 columns.
        Assert.True(composites[0].LeftColumns.Count <= 2);
    }

    [Fact]
    public void DetectCompositeKeys_ClassifiedAsManyToMany()
    {
        List<JoinCandidate> singles =
        [
            MakeSingleCandidate("orders", "customers", "region", "region", 0.9, uniqueKeyScore: 0.3),
            MakeSingleCandidate("orders", "customers", "year", "year", 0.85, uniqueKeyScore: 0.3),
        ];

        IReadOnlyList<JoinCandidate> composites =
            CompositeKeyDetector.DetectCompositeKeys(singles, CrossManifestThresholds.Default);

        Assert.Single(composites);
        Assert.Equal(JoinClassification.ManyToMany, composites[0].EstimatedJoinType);
    }

    // ── Containment-Based Composite Detection ──

    [Fact]
    public void DetectCompositeKeys_WithContainment_PopulatesCompositeEvidence()
    {
        List<JoinCandidate> singles =
        [
            MakeSingleCandidateWithContainment("orders", "customers", "region_id", "region_id", 0.9,
                containmentLeftInRight: 1.0, containmentRightInLeft: 0.6, exactJaccard: 0.5),
            MakeSingleCandidateWithContainment("orders", "customers", "year", "year", 0.85,
                containmentLeftInRight: 0.95, containmentRightInLeft: 0.7, exactJaccard: 0.6),
        ];

        IReadOnlyList<JoinCandidate> composites =
            CompositeKeyDetector.DetectCompositeKeys(singles, CrossManifestThresholds.Default);

        Assert.Single(composites);
        JoinCandidate composite = composites[0];

        // Minimum containment across components (conservative).
        Assert.NotNull(composite.Evidence.ContainmentLeftInRight);
        Assert.Equal(0.95, composite.Evidence.ContainmentLeftInRight!.Value, precision: 5);
        Assert.NotNull(composite.Evidence.ContainmentRightInLeft);
        Assert.Equal(0.6, composite.Evidence.ContainmentRightInLeft!.Value, precision: 5);
        // Minimum exact Jaccard across components.
        Assert.NotNull(composite.Evidence.ExactJaccard);
        Assert.Equal(0.5, composite.Evidence.ExactJaccard!.Value, precision: 5);
    }

    [Fact]
    public void DetectCompositeKeys_HighBidirectionalContainment_BoostsConfidence()
    {
        // Both components have containment ≥ 0.8 in both directions → boost.
        List<JoinCandidate> withContainment =
        [
            MakeSingleCandidateWithContainment("orders", "customers", "region_id", "region_id", 0.8,
                containmentLeftInRight: 0.95, containmentRightInLeft: 0.90, exactJaccard: 0.8),
            MakeSingleCandidateWithContainment("orders", "customers", "year", "year", 0.8,
                containmentLeftInRight: 0.90, containmentRightInLeft: 0.85, exactJaccard: 0.7),
        ];

        List<JoinCandidate> withoutContainment =
        [
            MakeSingleCandidate("orders", "customers", "region_id", "region_id", 0.8, uniqueKeyScore: 0.5),
            MakeSingleCandidate("orders", "customers", "year", "year", 0.8, uniqueKeyScore: 0.5),
        ];

        IReadOnlyList<JoinCandidate> boosted =
            CompositeKeyDetector.DetectCompositeKeys(withContainment, CrossManifestThresholds.Default);
        IReadOnlyList<JoinCandidate> baseline =
            CompositeKeyDetector.DetectCompositeKeys(withoutContainment, CrossManifestThresholds.Default);

        Assert.Single(boosted);
        Assert.Single(baseline);
        Assert.True(boosted[0].Confidence > baseline[0].Confidence,
            $"Expected boosted confidence ({boosted[0].Confidence}) > baseline ({baseline[0].Confidence})");
    }

    [Fact]
    public void DetectCompositeKeys_LowContainment_NoBoost()
    {
        // Components have containment < 0.8 → no boost applied.
        List<JoinCandidate> lowContainment =
        [
            MakeSingleCandidateWithContainment("orders", "customers", "region_id", "region_id", 0.8,
                containmentLeftInRight: 0.5, containmentRightInLeft: 0.4, exactJaccard: 0.3),
            MakeSingleCandidateWithContainment("orders", "customers", "year", "year", 0.8,
                containmentLeftInRight: 0.6, containmentRightInLeft: 0.3, exactJaccard: 0.2),
        ];

        List<JoinCandidate> withoutContainment =
        [
            MakeSingleCandidate("orders", "customers", "region_id", "region_id", 0.8, uniqueKeyScore: 0.5),
            MakeSingleCandidate("orders", "customers", "year", "year", 0.8, uniqueKeyScore: 0.5),
        ];

        IReadOnlyList<JoinCandidate> withLow =
            CompositeKeyDetector.DetectCompositeKeys(lowContainment, CrossManifestThresholds.Default);
        IReadOnlyList<JoinCandidate> baseline =
            CompositeKeyDetector.DetectCompositeKeys(withoutContainment, CrossManifestThresholds.Default);

        Assert.Single(withLow);
        Assert.Single(baseline);
        // No boost → same confidence (containment doesn't penalize, just doesn't boost).
        Assert.Equal(baseline[0].Confidence, withLow[0].Confidence, precision: 5);
    }

    [Fact]
    public void DetectCompositeKeys_PartialContainment_NoContainmentFieldsOnEvidence()
    {
        // Only one of two components has containment → composite evidence containment is null.
        List<JoinCandidate> partial =
        [
            MakeSingleCandidateWithContainment("orders", "customers", "region_id", "region_id", 0.9,
                containmentLeftInRight: 1.0, containmentRightInLeft: 0.8, exactJaccard: 0.7),
            MakeSingleCandidate("orders", "customers", "year", "year", 0.85, uniqueKeyScore: 0.5),
        ];

        IReadOnlyList<JoinCandidate> composites =
            CompositeKeyDetector.DetectCompositeKeys(partial, CrossManifestThresholds.Default);

        Assert.Single(composites);
        // Only 1 of 2 components has containment → allComponentsHaveContainment = false → null.
        Assert.Null(composites[0].Evidence.ContainmentLeftInRight);
        Assert.Null(composites[0].Evidence.ContainmentRightInLeft);
    }

    // ── Containment-Based Join Classification ──

    [Fact]
    public void DetectCompositeKeys_LeftContainedInRight_ClassifiedAsManyToOne()
    {
        // FK fully contained in PK, PK not fully contained in FK → ManyToOne.
        List<JoinCandidate> singles =
        [
            MakeSingleCandidateWithContainment("orders", "customers", "region_id", "region_id", 0.9,
                containmentLeftInRight: 0.95, containmentRightInLeft: 0.5, exactJaccard: 0.4),
            MakeSingleCandidateWithContainment("orders", "customers", "year", "year", 0.85,
                containmentLeftInRight: 0.90, containmentRightInLeft: 0.6, exactJaccard: 0.5),
        ];

        IReadOnlyList<JoinCandidate> composites =
            CompositeKeyDetector.DetectCompositeKeys(singles, CrossManifestThresholds.Default);

        Assert.Single(composites);
        Assert.Equal(JoinClassification.ManyToOne, composites[0].EstimatedJoinType);
    }

    [Fact]
    public void DetectCompositeKeys_RightContainedInLeft_ClassifiedAsOneToMany()
    {
        List<JoinCandidate> singles =
        [
            MakeSingleCandidateWithContainment("customers", "orders", "region_id", "region_id", 0.9,
                containmentLeftInRight: 0.5, containmentRightInLeft: 0.95, exactJaccard: 0.4),
            MakeSingleCandidateWithContainment("customers", "orders", "year", "year", 0.85,
                containmentLeftInRight: 0.6, containmentRightInLeft: 0.90, exactJaccard: 0.5),
        ];

        IReadOnlyList<JoinCandidate> composites =
            CompositeKeyDetector.DetectCompositeKeys(singles, CrossManifestThresholds.Default);

        Assert.Single(composites);
        Assert.Equal(JoinClassification.OneToMany, composites[0].EstimatedJoinType);
    }

    [Fact]
    public void DetectCompositeKeys_BothFullyContained_ClassifiedAsOneToOne()
    {
        List<JoinCandidate> singles =
        [
            MakeSingleCandidateWithContainment("left", "right", "a", "a", 0.9,
                containmentLeftInRight: 0.95, containmentRightInLeft: 0.95, exactJaccard: 0.9),
            MakeSingleCandidateWithContainment("left", "right", "b", "b", 0.85,
                containmentLeftInRight: 0.90, containmentRightInLeft: 0.90, exactJaccard: 0.85),
        ];

        IReadOnlyList<JoinCandidate> composites =
            CompositeKeyDetector.DetectCompositeKeys(singles, CrossManifestThresholds.Default);

        Assert.Single(composites);
        Assert.Equal(JoinClassification.OneToOne, composites[0].EstimatedJoinType);
    }

    [Fact]
    public void DetectCompositeKeys_NeitherContained_ClassifiedAsManyToMany()
    {
        List<JoinCandidate> singles =
        [
            MakeSingleCandidateWithContainment("left", "right", "a", "a", 0.9,
                containmentLeftInRight: 0.5, containmentRightInLeft: 0.5, exactJaccard: 0.3),
            MakeSingleCandidateWithContainment("left", "right", "b", "b", 0.85,
                containmentLeftInRight: 0.4, containmentRightInLeft: 0.6, exactJaccard: 0.3),
        ];

        IReadOnlyList<JoinCandidate> composites =
            CompositeKeyDetector.DetectCompositeKeys(singles, CrossManifestThresholds.Default);

        Assert.Single(composites);
        Assert.Equal(JoinClassification.ManyToMany, composites[0].EstimatedJoinType);
    }

    [Fact]
    public void DetectCompositeKeys_NoContainment_DefaultsManyToMany()
    {
        // Without containment data, classification defaults to ManyToMany.
        List<JoinCandidate> singles =
        [
            MakeSingleCandidate("orders", "customers", "region", "region", 0.9, uniqueKeyScore: 0.3),
            MakeSingleCandidate("orders", "customers", "year", "year", 0.85, uniqueKeyScore: 0.3),
        ];

        IReadOnlyList<JoinCandidate> composites =
            CompositeKeyDetector.DetectCompositeKeys(singles, CrossManifestThresholds.Default);

        Assert.Single(composites);
        Assert.Equal(JoinClassification.ManyToMany, composites[0].EstimatedJoinType);
    }

    // ── Helpers ──

    private static JoinCandidate MakeSingleCandidate(
        string leftTable,
        string rightTable,
        string leftColumn,
        string rightColumn,
        double confidence,
        double uniqueKeyScore = 0.5)
    {
        return new JoinCandidate
        {
            LeftTable = leftTable,
            RightTable = rightTable,
            LeftColumns = [leftColumn],
            RightColumns = [rightColumn],
            Evidence = new JoinEvidence
            {
                NameSimilarity = 0.8,
                TypeCompatibility = 1.0,
                TopKJaccard = 0.5,
                CardinalityRatio = 0.8,
                RangeOverlap = null,
                NullKeyRatio = 0.0,
                UniqueKeyScore = uniqueKeyScore,
                CompositeConfidence = confidence,
            },
            Confidence = confidence,
            EstimatedJoinType = JoinClassification.ManyToMany,
            EstimatedFanout = null,
            QualityWarnings = null,
        };
    }

    /// <summary>
    /// Creates a single-column join candidate with containment and exact Jaccard data
    /// from Phase 4 vocabulary analysis.
    /// </summary>
    private static JoinCandidate MakeSingleCandidateWithContainment(
        string leftTable,
        string rightTable,
        string leftColumn,
        string rightColumn,
        double confidence,
        double containmentLeftInRight,
        double containmentRightInLeft,
        double exactJaccard)
    {
        return new JoinCandidate
        {
            LeftTable = leftTable,
            RightTable = rightTable,
            LeftColumns = [leftColumn],
            RightColumns = [rightColumn],
            Evidence = new JoinEvidence
            {
                NameSimilarity = 0.8,
                TypeCompatibility = 1.0,
                TopKJaccard = 0.5,
                CardinalityRatio = 0.8,
                RangeOverlap = null,
                NullKeyRatio = 0.0,
                UniqueKeyScore = 0.5,
                CompositeConfidence = confidence,
                ExactJaccard = exactJaccard,
                ContainmentLeftInRight = containmentLeftInRight,
                ContainmentRightInLeft = containmentRightInLeft,
            },
            Confidence = confidence,
            EstimatedJoinType = JoinClassification.ManyToMany,
            EstimatedFanout = null,
            QualityWarnings = null,
        };
    }
}
