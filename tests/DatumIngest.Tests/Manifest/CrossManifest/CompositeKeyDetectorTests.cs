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
}
