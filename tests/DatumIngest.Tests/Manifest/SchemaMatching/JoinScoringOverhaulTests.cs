namespace DatumIngest.Tests.Manifest.SchemaMatching;

using DatumIngest.Manifest;
using DatumIngest.Manifest.SchemaMatching;
using DatumIngest.Model;

/// <summary>
/// Tests for schema matching infrastructure: role-based candidate filtering
/// and gated evidence scoring.
/// </summary>
public sealed class JoinScoringOverhaulTests
{
    // ── 3.1: Role-Based Candidate Filtering ──

    [Fact]
    public void IsRolePairJoinable_BothMeasure_Rejected()
    {
        NumericFeatureManifest left = MakeNumericFeature("amount", ColumnRole.Measure);
        NumericFeatureManifest right = MakeNumericFeature("price", ColumnRole.Measure);

        Assert.False(ColumnMatcher.IsRolePairJoinable(left, right));
    }

    [Fact]
    public void IsRolePairJoinable_BothStructural_Rejected()
    {
        // Use StringFeatureManifest with Structural role — the role gate only checks
        // the Role property, not the manifest subclass.
        StringFeatureManifest left = MakeStringFeature("embedding_a", ColumnRole.Structural);
        StringFeatureManifest right = MakeStringFeature("embedding_b", ColumnRole.Structural);

        Assert.False(ColumnMatcher.IsRolePairJoinable(left, right));
    }

    [Fact]
    public void IsRolePairJoinable_IdentifierAndForeignKey_Allowed()
    {
        NumericFeatureManifest left = MakeNumericFeature("customer_id", ColumnRole.Identifier);
        NumericFeatureManifest right = MakeNumericFeature("customer_id", ColumnRole.ForeignKey);

        Assert.True(ColumnMatcher.IsRolePairJoinable(left, right));
    }

    [Fact]
    public void IsRolePairJoinable_IdentifierAndMeasure_Allowed()
    {
        NumericFeatureManifest left = MakeNumericFeature("id", ColumnRole.Identifier);
        NumericFeatureManifest right = MakeNumericFeature("amount", ColumnRole.Measure);

        Assert.True(ColumnMatcher.IsRolePairJoinable(left, right));
    }

    [Fact]
    public void IsRolePairJoinable_CategoricalWithSimilarNdv_Allowed()
    {
        StringFeatureManifest left = MakeStringFeature("region", ColumnRole.Categorical, estimatedDistinctCount: 30);
        StringFeatureManifest right = MakeStringFeature("region", ColumnRole.Categorical, estimatedDistinctCount: 25);

        // Both NDV ≥ 20 (minimum floor), ratio = 25/30 = 0.833 ≥ 0.5.
        Assert.True(ColumnMatcher.IsRolePairJoinable(left, right));
    }

    [Fact]
    public void IsRolePairJoinable_CategoricalWithDisparate_Ndv_Rejected()
    {
        StringFeatureManifest left = MakeStringFeature("country", ColumnRole.Categorical, estimatedDistinctCount: 200);
        StringFeatureManifest right = MakeStringFeature("gender", ColumnRole.Categorical, estimatedDistinctCount: 3);

        // NDV ratio = 3/200 = 0.015 < 0.5 (also below minimum floor).
        Assert.False(ColumnMatcher.IsRolePairJoinable(left, right));
    }

    [Fact]
    public void IsRolePairJoinable_CategoricalBelowMinimumDistinctCount_Rejected()
    {
        StringFeatureManifest left = MakeStringFeature("flag_a", ColumnRole.Categorical, estimatedDistinctCount: 5);
        StringFeatureManifest right = MakeStringFeature("flag_b", ColumnRole.Categorical, estimatedDistinctCount: 4);

        // NDV ratio = 4/5 = 0.8 ≥ 0.5 but both below minimum floor of 20.
        Assert.False(ColumnMatcher.IsRolePairJoinable(left, right));
    }

    [Fact]
    public void IsRolePairJoinable_CategoricalOneSideBelowMinimumDistinctCount_Rejected()
    {
        StringFeatureManifest left = MakeStringFeature("state_code", ColumnRole.Categorical, estimatedDistinctCount: 50);
        StringFeatureManifest right = MakeStringFeature("binary_flag", ColumnRole.Categorical, estimatedDistinctCount: 2);

        // Right side below minimum floor of 20 (also NDV ratio 2/50 = 0.04 < 0.5).
        Assert.False(ColumnMatcher.IsRolePairJoinable(left, right));
    }

    [Fact]
    public void IsRolePairJoinable_NullRoleFallsThrough()
    {
        NumericFeatureManifest left = MakeNumericFeature("amount", role: null);
        NumericFeatureManifest right = MakeNumericFeature("price", role: null);

        // Without roles, the pair is allowed through for backward compatibility.
        Assert.True(ColumnMatcher.IsRolePairJoinable(left, right));
    }

    [Fact]
    public void IsRolePairJoinable_OneNullRoleFallsThrough()
    {
        NumericFeatureManifest left = MakeNumericFeature("id", ColumnRole.Identifier);
        NumericFeatureManifest right = MakeNumericFeature("amount", role: null);

        Assert.True(ColumnMatcher.IsRolePairJoinable(left, right));
    }

    [Fact]
    public void IsRolePairJoinable_TemporalAndMeasure_Rejected()
    {
        // Use StringFeatureManifest with Temporal role — the role gate only checks
        // the Role property, not the manifest subclass.
        StringFeatureManifest left = MakeStringFeature("created_at", ColumnRole.Temporal);
        NumericFeatureManifest right = MakeNumericFeature("amount", ColumnRole.Measure);

        // Neither is Identifier/ForeignKey, not both Categorical → rejected.
        Assert.False(ColumnMatcher.IsRolePairJoinable(left, right));
    }

    [Fact]
    public void IsRolePairJoinable_ForeignKeyAndCategorical_Allowed()
    {
        NumericFeatureManifest left = MakeNumericFeature("dept_id", ColumnRole.ForeignKey);
        StringFeatureManifest right = MakeStringFeature("dept_code", ColumnRole.Categorical, estimatedDistinctCount: 10);

        // One side is ForeignKey → allowed.
        Assert.True(ColumnMatcher.IsRolePairJoinable(left, right));
    }

    [Fact]
    public void FindCandidatePairs_MeasureColumns_FilteredByRole()
    {
        ManifestWithName left = MakeManifest("table_a",
            MakeNumericFeature("revenue", ColumnRole.Measure),
            MakeNumericFeature("order_id", ColumnRole.Identifier));
        ManifestWithName right = MakeManifest("table_b",
            MakeNumericFeature("cost", ColumnRole.Measure),
            MakeNumericFeature("order_id", ColumnRole.Identifier));

        IReadOnlyList<ColumnMatchCandidate> candidates =
            ColumnMatcher.FindCandidatePairs(left, right, SchemaMatchingThresholds.Default);

        // order_id↔order_id should be a candidate; revenue↔cost should not.
        Assert.Contains(candidates, c => c.LeftColumn == "order_id" && c.RightColumn == "order_id");
        Assert.DoesNotContain(candidates, c => c.LeftColumn == "revenue" && c.RightColumn == "cost");
    }

    // ── 3.2: Gated Evidence Scoring ──

    [Fact]
    public void ScoreEvidence_WeakNameBelowJoinabilityFloor_HardZero()
    {
        NumericFeatureManifest left = MakeNumericFeature("revenue", ColumnRole.Identifier,
            estimatedDistinctCount: 1000);
        NumericFeatureManifest right = MakeNumericFeature("zip_code", ColumnRole.Identifier,
            estimatedDistinctCount: 1000);

        // Name similarity for "revenue" vs "zip_code" is well below 0.3.
        double nameSimilarity = ColumnMatcher.ComputeNameSimilarity("revenue", "zip_code");
        ColumnMatchCandidate match = new("revenue", "zip_code", nameSimilarity, 1.0);

        JoinEvidence evidence = JoinEvidenceScorer.ScoreEvidence(
            left, 1000, right, 1000, match, SchemaMatchingThresholds.Default);

        Assert.Equal(0.0, evidence.CompositeConfidence);
    }

    [Fact]
    public void ScoreEvidence_NoIdentityEvidence_HardZero()
    {
        // Both non-unique, disjoint TopK, extreme cardinality mismatch.
        NumericFeatureManifest left = MakeNumericFeature("counter", ColumnRole.Identifier,
            estimatedDistinctCount: 2);
        NumericFeatureManifest right = MakeNumericFeature("counter", ColumnRole.Identifier,
            estimatedDistinctCount: 2,
            topK: [new FrequencyEntry("99", 500)]);

        SchemaMatchingThresholds strict = new() { IdentityEvidenceFloor = 0.5 };
        ColumnMatchCandidate match = new("counter", "counter", 1.0, 1.0);

        JoinEvidence evidence = JoinEvidenceScorer.ScoreEvidence(
            left, 1000, right, 1000, match, strict);

        // With strict floor and low identity evidence, should be zero.
        Assert.Equal(0.0, evidence.CompositeConfidence);
    }

    [Fact]
    public void ScoreEvidence_IncompatibleTypes_StructuralFloorKills()
    {
        // Image vs Float32 → typeCompatibility = 0.0.
        StringFeatureManifest left = new()
        {
            Name = "data",
            Kind = DataKind.Image,
            Count = 1000,
            NullCount = 0,
            ValidCount = 1000,
            NullRatio = 0.0,
            EstimatedDistinctCount = 100,
            TopKValues = [],
            MinLength = 1,
            MaxLength = 50,
        };
        NumericFeatureManifest right = MakeNumericFeature("data", ColumnRole.Identifier,
            estimatedDistinctCount: 100);

        // Structural floor of 0.2 should kill this because type=0.0 → structural=0.0.
        ColumnMatchCandidate match = new("data", "data", 1.0, 0.0);

        JoinEvidence evidence = JoinEvidenceScorer.ScoreEvidence(
            left, 1000, right, 1000, match, SchemaMatchingThresholds.Default);

        Assert.Equal(0.0, evidence.CompositeConfidence);
    }

    // ── Helpers ──

    private static ManifestWithName MakeManifest(string name, params FeatureManifest[] features)
    {
        return new ManifestWithName(name, new QueryResultsManifest
        {
            RowCount = 1000,
            GeneratedAtUtc = DateTime.UtcNow,
            Features = features,
        });
    }

    private static NumericFeatureManifest MakeNumericFeature(
        string name,
        ColumnRole? role = null,
        long estimatedDistinctCount = 100,
        IReadOnlyList<FrequencyEntry>? topK = null)
    {
        NumericFeatureManifest manifest = new()
        {
            Name = name,
            Kind = DataKind.Float32,
            Count = 1000,
            NullCount = 0,
            ValidCount = 1000,
            NullRatio = 0.0,
            EstimatedDistinctCount = estimatedDistinctCount,
            TopKValues = topK ?? [],
            Min = 0.0,
            Max = 1000.0,
            Mean = 500.0,
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
        manifest.Role = role;
        return manifest;
    }

    private static StringFeatureManifest MakeStringFeature(
        string name,
        ColumnRole? role = null,
        long estimatedDistinctCount = 100)
    {
        StringFeatureManifest manifest = new()
        {
            Name = name,
            Kind = DataKind.String,
            Count = 1000,
            NullCount = 0,
            ValidCount = 1000,
            NullRatio = 0.0,
            EstimatedDistinctCount = estimatedDistinctCount,
            TopKValues = [],
            MinLength = 1,
            MaxLength = 50,
        };
        manifest.Role = role;
        return manifest;
    }

    private static JoinCandidate MakeCandidate(
        string leftTable,
        string rightTable,
        string leftColumn,
        string rightColumn,
        double confidence)
    {
        return new JoinCandidate
        {
            LeftTable = leftTable,
            RightTable = rightTable,
            LeftColumns = [leftColumn],
            RightColumns = [rightColumn],
            Evidence = new JoinEvidence
            {
                NameSimilarity = 1.0,
                TypeCompatibility = 1.0,
                TopKJaccard = 0.5,
                CardinalityRatio = 0.8,
                RangeOverlap = null,
                NullKeyRatio = 0.0,
                UniqueKeyScore = 0.9,
                CompositeConfidence = confidence,
            },
            Confidence = confidence,
            EstimatedJoinType = JoinClassification.OneToMany,
            EstimatedFanout = null,
            QualityWarnings = null,
        };
    }
}
