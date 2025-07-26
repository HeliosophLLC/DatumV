namespace DatumIngest.Tests.Manifest.CrossManifest;

using DatumIngest.Manifest;
using DatumIngest.Manifest.CrossManifest;
using DatumIngest.Model;

/// <summary>
/// Tests for <see cref="JoinEvidenceScorer"/> — TopK Jaccard, cardinality ratio,
/// range overlap, null-key ratio, unique key detection, composite confidence,
/// join classification, and fanout estimation.
/// </summary>
public sealed class JoinEvidenceScorerTests
{
    // ── TopK Jaccard ──

    [Fact]
    public void ComputeTopKJaccard_IdenticalSets_ReturnsOne()
    {
        NumericFeatureManifest left = MakeIntegerFeature("id",
            topK: [new FrequencyEntry("1", 100), new FrequencyEntry("2", 90), new FrequencyEntry("3", 80)]);
        NumericFeatureManifest right = MakeIntegerFeature("id",
            topK: [new FrequencyEntry("1", 100), new FrequencyEntry("2", 90), new FrequencyEntry("3", 80)]);

        double jaccard = JoinEvidenceScorer.ComputeTopKJaccard(left, right);

        Assert.Equal(1.0, jaccard);
    }

    [Fact]
    public void ComputeTopKJaccard_DisjointSets_ReturnsZero()
    {
        NumericFeatureManifest left = MakeIntegerFeature("id",
            topK: [new FrequencyEntry("1", 100), new FrequencyEntry("2", 90)]);
        NumericFeatureManifest right = MakeIntegerFeature("id",
            topK: [new FrequencyEntry("3", 100), new FrequencyEntry("4", 90)]);

        double jaccard = JoinEvidenceScorer.ComputeTopKJaccard(left, right);

        Assert.Equal(0.0, jaccard);
    }

    [Fact]
    public void ComputeTopKJaccard_PartialOverlap_ReturnsCorrectRatio()
    {
        NumericFeatureManifest left = MakeIntegerFeature("id",
            topK: [new FrequencyEntry("1", 100), new FrequencyEntry("2", 90)]);
        NumericFeatureManifest right = MakeIntegerFeature("id",
            topK: [new FrequencyEntry("2", 100), new FrequencyEntry("3", 90)]);

        double jaccard = JoinEvidenceScorer.ComputeTopKJaccard(left, right);

        // Intersection = {2}, Union = {1, 2, 3} → 1/3 ≈ 0.333.
        Assert.Equal(1.0 / 3.0, jaccard, precision: 5);
    }

    [Fact]
    public void ComputeTopKJaccard_EmptyTopK_ReturnsZero()
    {
        NumericFeatureManifest left = MakeIntegerFeature("id", topK: []);
        NumericFeatureManifest right = MakeIntegerFeature("id",
            topK: [new FrequencyEntry("1", 100)]);

        double jaccard = JoinEvidenceScorer.ComputeTopKJaccard(left, right);

        Assert.Equal(0.0, jaccard);
    }

    [Fact]
    public void ComputeTopKJaccard_ContinuousNumerics_ReturnsZero()
    {
        NumericFeatureManifest left = MakeContinuousFeature("price",
            topK: [new FrequencyEntry("1.234", 10)]);
        NumericFeatureManifest right = MakeContinuousFeature("price",
            topK: [new FrequencyEntry("1.234", 10)]);

        double jaccard = JoinEvidenceScorer.ComputeTopKJaccard(left, right);

        // Continuous numerics are skipped because TopK is unreliable.
        Assert.Equal(0.0, jaccard);
    }

    [Fact]
    public void ComputeTopKJaccard_CaseInsensitive()
    {
        StringFeatureManifest left = MakeStringFeatureWithTopK("status",
            [new FrequencyEntry("Active", 100), new FrequencyEntry("Inactive", 50)]);
        StringFeatureManifest right = MakeStringFeatureWithTopK("status",
            [new FrequencyEntry("ACTIVE", 100), new FrequencyEntry("INACTIVE", 50)]);

        double jaccard = JoinEvidenceScorer.ComputeTopKJaccard(left, right);

        Assert.Equal(1.0, jaccard);
    }

    // ── Join Classification ──

    [Fact]
    public void ClassifyJoin_BothUnique_ReturnsOneToOne()
    {
        NumericFeatureManifest left = MakeIntegerFeature("id", estimatedDistinctCount: 1000);
        NumericFeatureManifest right = MakeIntegerFeature("id", estimatedDistinctCount: 1000);

        JoinClassification classification = JoinEvidenceScorer.ClassifyJoin(left, 1000, right, 1000);

        Assert.Equal(JoinClassification.OneToOne, classification);
    }

    [Fact]
    public void ClassifyJoin_LeftUniqueRightNot_ReturnsOneToMany()
    {
        NumericFeatureManifest left = MakeIntegerFeature("id", estimatedDistinctCount: 1000);
        NumericFeatureManifest right = MakeIntegerFeature("id", estimatedDistinctCount: 100);

        JoinClassification classification = JoinEvidenceScorer.ClassifyJoin(left, 1000, right, 1000);

        Assert.Equal(JoinClassification.OneToMany, classification);
    }

    [Fact]
    public void ClassifyJoin_NeitherUnique_ReturnsManyToMany()
    {
        NumericFeatureManifest left = MakeIntegerFeature("id", estimatedDistinctCount: 100);
        NumericFeatureManifest right = MakeIntegerFeature("id", estimatedDistinctCount: 100);

        JoinClassification classification = JoinEvidenceScorer.ClassifyJoin(left, 1000, right, 1000);

        Assert.Equal(JoinClassification.ManyToMany, classification);
    }

    // ── Fanout Estimation ──

    [Fact]
    public void EstimateFanout_ReturnsRowCountOverNDV()
    {
        NumericFeatureManifest left = MakeIntegerFeature("id", estimatedDistinctCount: 1000);
        NumericFeatureManifest right = MakeIntegerFeature("id", estimatedDistinctCount: 100);

        double? fanout = JoinEvidenceScorer.EstimateFanout(left, 1000, right, 1000);

        // 1000 / 100 = 10.0.
        Assert.NotNull(fanout);
        Assert.Equal(10.0, fanout.Value);
    }

    [Fact]
    public void EstimateFanout_ZeroNDV_ReturnsNull()
    {
        NumericFeatureManifest left = MakeIntegerFeature("id", estimatedDistinctCount: 1000);
        NumericFeatureManifest right = MakeIntegerFeature("id", estimatedDistinctCount: 0);

        double? fanout = JoinEvidenceScorer.EstimateFanout(left, 1000, right, 1000);

        Assert.Null(fanout);
    }

    // ── ScoreEvidence (integration) ──

    [Fact]
    public void ScoreEvidence_IdenticalColumns_ReturnsHighConfidence()
    {
        NumericFeatureManifest left = MakeIntegerFeature("customer_id",
            estimatedDistinctCount: 950,
            topK: [new FrequencyEntry("1", 10), new FrequencyEntry("2", 10)]);
        NumericFeatureManifest right = MakeIntegerFeature("customer_id",
            estimatedDistinctCount: 950,
            topK: [new FrequencyEntry("1", 10), new FrequencyEntry("2", 10)]);

        ColumnMatchCandidate match = new("customer_id", "customer_id", 1.0, 1.0);

        JoinEvidence evidence = JoinEvidenceScorer.ScoreEvidence(
            left, 1000, right, 1000, match, CrossManifestThresholds.Default);

        Assert.True(evidence.CompositeConfidence > 0.8);
        Assert.Equal(1.0, evidence.NameSimilarity);
        Assert.Equal(1.0, evidence.TypeCompatibility);
    }

    [Fact]
    public void ScoreEvidence_HighNullRatio_ReflectsInNullKeyRatio()
    {
        NumericFeatureManifest left = MakeIntegerFeature("id", nullRatio: 0.5, nullCount: 500);
        NumericFeatureManifest right = MakeIntegerFeature("id");

        ColumnMatchCandidate match = new("id", "id", 1.0, 1.0);

        JoinEvidence evidence = JoinEvidenceScorer.ScoreEvidence(
            left, 1000, right, 1000, match, CrossManifestThresholds.Default);

        Assert.Equal(0.5, evidence.NullKeyRatio);
    }

    [Fact]
    public void ScoreEvidence_NonNumericColumns_RangeOverlapIsNull()
    {
        StringFeatureManifest left = MakeStringFeatureWithTopK("status", []);
        StringFeatureManifest right = MakeStringFeatureWithTopK("status", []);

        ColumnMatchCandidate match = new("status", "status", 1.0, 1.0);

        JoinEvidence evidence = JoinEvidenceScorer.ScoreEvidence(
            left, 1000, right, 1000, match, CrossManifestThresholds.Default);

        Assert.Null(evidence.RangeOverlap);
    }

    [Fact]
    public void ScoreEvidence_NumericColumns_RangeOverlapComputed()
    {
        NumericFeatureManifest left = MakeIntegerFeature("amount", min: 0, max: 100);
        NumericFeatureManifest right = MakeIntegerFeature("amount", min: 50, max: 150);

        ColumnMatchCandidate match = new("amount", "amount", 1.0, 1.0);

        JoinEvidence evidence = JoinEvidenceScorer.ScoreEvidence(
            left, 1000, right, 1000, match, CrossManifestThresholds.Default);

        Assert.NotNull(evidence.RangeOverlap);
        // Intersection [50, 100] = 50, Union [0, 150] = 150 → 50/150 ≈ 0.333.
        Assert.Equal(50.0 / 150.0, evidence.RangeOverlap.Value, precision: 5);
    }

    [Fact]
    public void ScoreEvidence_DisjointRanges_RangeOverlapZero()
    {
        NumericFeatureManifest left = MakeIntegerFeature("amount", min: 0, max: 50);
        NumericFeatureManifest right = MakeIntegerFeature("amount", min: 100, max: 200);

        ColumnMatchCandidate match = new("amount", "amount", 1.0, 1.0);

        JoinEvidence evidence = JoinEvidenceScorer.ScoreEvidence(
            left, 1000, right, 1000, match, CrossManifestThresholds.Default);

        Assert.NotNull(evidence.RangeOverlap);
        Assert.Equal(0.0, evidence.RangeOverlap.Value);
    }

    // ── Helpers ──

    private static NumericFeatureManifest MakeIntegerFeature(
        string name,
        long estimatedDistinctCount = 100,
        double min = 0.0,
        double max = 100.0,
        double nullRatio = 0.0,
        long nullCount = 0,
        IReadOnlyList<FrequencyEntry>? topK = null)
    {
        return new NumericFeatureManifest
        {
            Name = name,
            Kind = DataKind.Scalar,
            Count = 1000 - nullCount,
            NullCount = nullCount,
            ValidCount = 1000 - nullCount,
            NullRatio = nullRatio,
            EstimatedDistinctCount = estimatedDistinctCount,
            TopKValues = topK ?? [],
            Min = min,
            Max = max,
            Mean = (min + max) / 2.0,
            Variance = 25.0,
            StandardDeviation = 5.0,
            Skewness = 0.0,
            Kurtosis = 3.0,
            Histogram = new HistogramData([], []),
            ZeroCount = 0,
            ZeroRatio = 0.0,
            OutlierCount = 0,
            OutlierRatio = 0.0,
            IntegerValued = true,
        };
    }

    private static NumericFeatureManifest MakeContinuousFeature(
        string name,
        IReadOnlyList<FrequencyEntry>? topK = null)
    {
        return new NumericFeatureManifest
        {
            Name = name,
            Kind = DataKind.Scalar,
            Count = 1000,
            NullCount = 0,
            ValidCount = 1000,
            NullRatio = 0.0,
            EstimatedDistinctCount = 100,
            TopKValues = topK ?? [],
            Min = 0.0,
            Max = 100.0,
            Mean = 50.0,
            Variance = 25.0,
            StandardDeviation = 5.0,
            Skewness = 0.0,
            Kurtosis = 3.0,
            Histogram = new HistogramData([], []),
            ZeroCount = 0,
            ZeroRatio = 0.0,
            OutlierCount = 0,
            OutlierRatio = 0.0,
            IntegerValued = false,
        };
    }

    private static StringFeatureManifest MakeStringFeatureWithTopK(
        string name,
        IReadOnlyList<FrequencyEntry> topK)
    {
        return new StringFeatureManifest
        {
            Name = name,
            Kind = DataKind.String,
            Count = 1000,
            NullCount = 0,
            ValidCount = 1000,
            NullRatio = 0.0,
            EstimatedDistinctCount = 100,
            TopKValues = topK,
            MinLength = 1,
            MaxLength = 50,
        };
    }
}
