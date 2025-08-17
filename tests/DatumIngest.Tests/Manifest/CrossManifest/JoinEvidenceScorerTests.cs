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

    // ── Multiplicative Penalties ──

    [Fact]
    public void ScoreEvidence_IncompatibleTypes_PenalisesConfidence()
    {
        // String vs Scalar — typeCompatibility = 0.0.
        StringFeatureManifest left = MakeStringFeatureWithTopK("aisle",
            [new FrequencyEntry("Active", 100)]);
        NumericFeatureManifest right = MakeIntegerFeature("aisle_id",
            estimatedDistinctCount: 100,
            topK: [new FrequencyEntry("1", 100)]);

        ColumnMatchCandidate match = new("aisle", "aisle_id", 0.625, 0.0);

        JoinEvidence evidence = JoinEvidenceScorer.ScoreEvidence(
            left, 1000, right, 1000, match, CrossManifestThresholds.Default);

        // Type incompatibility penalty (0.7×) should visibly reduce confidence.
        Assert.True(evidence.CompositeConfidence < 0.50,
            $"Expected < 0.50 but was {evidence.CompositeConfidence:F4}");
    }

    [Fact]
    public void ScoreEvidence_LargeCardinalityMismatch_PenalisesConfidence()
    {
        // 21 distinct vs 3.4M distinct — cardinality ratio ≈ 0.000006.
        NumericFeatureManifest left = MakeIntegerFeature("department_id",
            estimatedDistinctCount: 21, min: 1, max: 21,
            topK: [new FrequencyEntry("1", 10), new FrequencyEntry("2", 10)]);
        NumericFeatureManifest right = MakeIntegerFeature("order_id",
            estimatedDistinctCount: 3400000, min: 1, max: 3500000,
            topK: [new FrequencyEntry("100", 1), new FrequencyEntry("200", 1)]);

        ColumnMatchCandidate match = new("department_id", "order_id", 0.535, 1.0);

        JoinEvidence evidence = JoinEvidenceScorer.ScoreEvidence(
            left, 21, right, 3421083, match, CrossManifestThresholds.Default);

        // Extreme cardinality mismatch should drop confidence well below graph threshold.
        Assert.True(evidence.CompositeConfidence < 0.30,
            $"Expected < 0.30 but was {evidence.CompositeConfidence:F4}");
    }

    [Fact]
    public void ScoreEvidence_ZeroOverlapWeakName_PenalisesConfidence()
    {
        // No TopK overlap, weak name similarity — coincidental numeric match.
        NumericFeatureManifest left = MakeIntegerFeature("order_hour_of_day",
            estimatedDistinctCount: 24, min: 0, max: 23,
            topK: [new FrequencyEntry("10", 200), new FrequencyEntry("11", 190)]);
        NumericFeatureManifest right = MakeIntegerFeature("department_id",
            estimatedDistinctCount: 21, min: 1, max: 21,
            topK: [new FrequencyEntry("4", 300), new FrequencyEntry("7", 200)]);

        ColumnMatchCandidate match = new("order_hour_of_day", "department_id", 0.176, 1.0);

        JoinEvidence evidence = JoinEvidenceScorer.ScoreEvidence(
            left, 3421083, right, 49688, match, CrossManifestThresholds.Default);

        // Weak name + zero overlap + name mismatch + ManyToMany penalties should all apply.
        Assert.True(evidence.CompositeConfidence < 0.25,
            $"Expected < 0.25 but was {evidence.CompositeConfidence:F4}");
    }

    [Fact]
    public void ScoreEvidence_PerfectPrimaryKeyJoin_RetainsHighConfidence()
    {
        // aisle_id → aisle_id: exact name, same type, same cardinality, same range.
        NumericFeatureManifest left = MakeIntegerFeature("aisle_id",
            estimatedDistinctCount: 134, min: 1, max: 134,
            topK: [new FrequencyEntry("1", 1), new FrequencyEntry("2", 1)]);
        NumericFeatureManifest right = MakeIntegerFeature("aisle_id",
            estimatedDistinctCount: 134, min: 1, max: 134,
            topK: [new FrequencyEntry("1", 300), new FrequencyEntry("2", 280)]);

        ColumnMatchCandidate match = new("aisle_id", "aisle_id", 1.0, 1.0);

        JoinEvidence evidence = JoinEvidenceScorer.ScoreEvidence(
            left, 134, right, 49688, match, CrossManifestThresholds.Default);

        // Perfect join — no penalties should fire, confidence should be very high.
        Assert.True(evidence.CompositeConfidence > 0.85,
            $"Expected > 0.85 but was {evidence.CompositeConfidence:F4}");
    }

    [Fact]
    public void ScoreEvidence_UnrelatedStringColumns_StaysLow()
    {
        // aisle (135 values) vs eval_set (3 values) — completely unrelated strings.
        StringFeatureManifest left = MakeStringFeatureWithTopK("aisle",
            [new FrequencyEntry("frozen", 50), new FrequencyEntry("dairy", 40)], estimatedDistinctCount: 135);
        StringFeatureManifest right = MakeStringFeatureWithTopK("eval_set",
            [new FrequencyEntry("prior", 500), new FrequencyEntry("train", 300)], estimatedDistinctCount: 3);

        ColumnMatchCandidate match = new("aisle", "eval_set", 0.25, 1.0);

        JoinEvidence evidence = JoinEvidenceScorer.ScoreEvidence(
            left, 134, right, 3421083, match, CrossManifestThresholds.Default);

        // Low name similarity + zero overlap + cardinality mismatch → should be well below 0.3.
        Assert.True(evidence.CompositeConfidence < 0.25,
            $"Expected < 0.25 but was {evidence.CompositeConfidence:F4}");
    }

    [Fact]
    public void ScoreEvidence_IndependentUuidIdentifiers_PenalisesConfidence()
    {
        // Two UUID columns from different entity domains: customer_id vs order_id.
        // Both are near-unique (distinct ratio ≥ 0.95), disjoint TopK, moderate name
        // similarity (~0.60 after _id suffix bonus). The independent-identifier penalty
        // should suppress the score below the candidate threshold.
        StringFeatureManifest left = MakeStringFeatureWithTopK("customer_id",
            [new FrequencyEntry("uuid-aaa-111", 1), new FrequencyEntry("uuid-aaa-222", 1)],
            estimatedDistinctCount: 96000);
        StringFeatureManifest right = MakeStringFeatureWithTopK("order_id",
            [new FrequencyEntry("uuid-bbb-333", 1), new FrequencyEntry("uuid-bbb-444", 1)],
            estimatedDistinctCount: 99000);

        ColumnMatchCandidate match = new("customer_id", "order_id", 0.605, 1.0);

        JoinEvidence evidence = JoinEvidenceScorer.ScoreEvidence(
            left, 100000, right, 100000, match, CrossManifestThresholds.Default);

        // Combined zero-overlap-weak-name (×0.85) + independent-identifier (×0.75)
        // penalties should push this well below CandidateMinConfidence (0.45).
        Assert.True(evidence.CompositeConfidence < 0.45,
            $"Expected < 0.45 but was {evidence.CompositeConfidence:F4}");
    }

    [Fact]
    public void ScoreEvidence_SameNameUuidColumns_RetainsHighConfidence()
    {
        // True UUID FK join: customer_id ↔ customer_id across two tables.
        // Both sides are near-unique with zero TopK overlap (UUIDs don't collide
        // in small samples), but exact name match protects from penalty.
        StringFeatureManifest left = MakeStringFeatureWithTopK("customer_id",
            [new FrequencyEntry("uuid-aaa-111", 1), new FrequencyEntry("uuid-aaa-222", 1)],
            estimatedDistinctCount: 96000);
        StringFeatureManifest right = MakeStringFeatureWithTopK("customer_id",
            [new FrequencyEntry("uuid-aaa-333", 1), new FrequencyEntry("uuid-aaa-444", 1)],
            estimatedDistinctCount: 99000);

        ColumnMatchCandidate match = new("customer_id", "customer_id", 1.0, 1.0);

        JoinEvidence evidence = JoinEvidenceScorer.ScoreEvidence(
            left, 100000, right, 100000, match, CrossManifestThresholds.Default);

        // Exact name match means neither zero-overlap-weak-name nor
        // independent-identifier penalty fires. Score should stay high.
        Assert.True(evidence.CompositeConfidence > 0.80,
            $"Expected > 0.80 but was {evidence.CompositeConfidence:F4}");
    }

    [Fact]
    public void ScoreEvidence_ManyToManyUnrelatedColumns_PenalisesConfidence()
    {
        // reordered (2 values in 32M rows) vs order_dow (7 values in 3.4M rows).
        // Neither side is a unique key → ManyToMany penalty applies.
        NumericFeatureManifest left = MakeIntegerFeature("reordered",
            estimatedDistinctCount: 2, min: 0, max: 1,
            topK: [new FrequencyEntry("0", 19000000), new FrequencyEntry("1", 13000000)]);
        NumericFeatureManifest right = MakeIntegerFeature("order_dow",
            estimatedDistinctCount: 7, min: 0, max: 6,
            topK: [new FrequencyEntry("0", 600000), new FrequencyEntry("1", 500000)]);

        ColumnMatchCandidate match = new("reordered", "order_dow", 0.444, 1.0);

        JoinEvidence evidence = JoinEvidenceScorer.ScoreEvidence(
            left, 32434489, right, 3421083, match, CrossManifestThresholds.Default);

        // Weak name + ManyToMany penalty should push confidence well below candidate threshold.
        Assert.True(evidence.CompositeConfidence < 0.30,
            $"Expected < 0.30 but was {evidence.CompositeConfidence:F4}");
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
            Kind = DataKind.Float32,
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
            Kind = DataKind.Float32,
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
        IReadOnlyList<FrequencyEntry> topK,
        long estimatedDistinctCount = 100)
    {
        return new StringFeatureManifest
        {
            Name = name,
            Kind = DataKind.String,
            Count = 1000,
            NullCount = 0,
            ValidCount = 1000,
            NullRatio = 0.0,
            EstimatedDistinctCount = estimatedDistinctCount,
            TopKValues = topK,
            MinLength = 1,
            MaxLength = 50,
        };
    }
}
