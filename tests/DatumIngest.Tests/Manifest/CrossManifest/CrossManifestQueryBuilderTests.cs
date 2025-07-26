namespace DatumIngest.Tests.Manifest.CrossManifest;

using DatumIngest.Manifest.CrossManifest;

/// <summary>
/// Tests for <see cref="CrossManifestQueryBuilder"/> — SQL generation from join candidates
/// with annotations, LEFT JOIN for nullable keys, and greedy table accumulation.
/// </summary>
public sealed class CrossManifestQueryBuilderTests
{
    [Fact]
    public void BuildQuery_SingleCandidate_ProducesJoinSql()
    {
        List<JoinCandidate> candidates =
        [
            MakeCandidate("orders", "customers", "customer_id", "customer_id", 0.8),
        ];

        string? sql = CrossManifestQueryBuilder.BuildQuery(candidates, CrossManifestQueryOptions.Default);

        Assert.NotNull(sql);
        Assert.Contains("\"orders\"", sql);
        Assert.Contains("\"customers\"", sql);
        Assert.Contains("\"customer_id\"", sql);
        Assert.Contains("JOIN", sql);
    }

    [Fact]
    public void BuildQuery_NoCandidatesAboveThreshold_ReturnsNull()
    {
        List<JoinCandidate> candidates =
        [
            MakeCandidate("orders", "customers", "customer_id", "customer_id", 0.1),
        ];

        string? sql = CrossManifestQueryBuilder.BuildQuery(candidates, CrossManifestQueryOptions.Default);

        Assert.Null(sql);
    }

    [Fact]
    public void BuildQuery_HighNullKey_UsesLeftJoin()
    {
        List<JoinCandidate> candidates =
        [
            MakeCandidateWithNullKey("orders", "customers", "customer_id", "customer_id", 0.8, nullKeyRatio: 0.3),
        ];

        string? sql = CrossManifestQueryBuilder.BuildQuery(candidates, CrossManifestQueryOptions.Default);

        Assert.NotNull(sql);
        Assert.Contains("LEFT JOIN", sql);
    }

    [Fact]
    public void BuildQuery_LowNullKey_UsesInnerJoin()
    {
        List<JoinCandidate> candidates =
        [
            MakeCandidate("orders", "customers", "customer_id", "customer_id", 0.8),
        ];

        string? sql = CrossManifestQueryBuilder.BuildQuery(candidates, CrossManifestQueryOptions.Default);

        Assert.NotNull(sql);
        Assert.Contains("INNER JOIN", sql);
    }

    [Fact]
    public void BuildQuery_WithAnnotations_IncludesComments()
    {
        List<JoinCandidate> candidates =
        [
            MakeCandidate("orders", "customers", "customer_id", "customer_id", 0.8),
        ];

        CrossManifestQueryOptions options = new() { IncludeAnnotations = true };

        string? sql = CrossManifestQueryBuilder.BuildQuery(candidates, options);

        Assert.NotNull(sql);
        Assert.Contains("-- Auto-generated cross-manifest JOIN query", sql);
        Assert.Contains("confidence=", sql);
    }

    [Fact]
    public void BuildQuery_WithoutAnnotations_NoComments()
    {
        List<JoinCandidate> candidates =
        [
            MakeCandidate("orders", "customers", "customer_id", "customer_id", 0.8),
        ];

        CrossManifestQueryOptions options = new() { IncludeAnnotations = false };

        string? sql = CrossManifestQueryBuilder.BuildQuery(candidates, options);

        Assert.NotNull(sql);
        Assert.DoesNotContain("-- Auto-generated", sql);
    }

    [Fact]
    public void BuildQuery_MultipleJoins_ChainsCorrectly()
    {
        List<JoinCandidate> candidates =
        [
            MakeCandidate("orders", "customers", "customer_id", "customer_id", 0.9),
            MakeCandidate("orders", "products", "product_id", "product_id", 0.8),
        ];

        string? sql = CrossManifestQueryBuilder.BuildQuery(candidates, CrossManifestQueryOptions.Default);

        Assert.NotNull(sql);
        Assert.Contains("\"orders\"", sql);
        Assert.Contains("\"customers\"", sql);
        Assert.Contains("\"products\"", sql);
    }

    [Fact]
    public void BuildQuery_CompositeKey_MultipleOnConditions()
    {
        JoinCandidate composite = new()
        {
            LeftTable = "orders",
            RightTable = "customers",
            LeftColumns = ["region_id", "year"],
            RightColumns = ["region_id", "year"],
            Evidence = MakeEvidence(0.7),
            Confidence = 0.7,
            EstimatedJoinType = JoinClassification.ManyToMany,
            EstimatedFanout = null,
            QualityWarnings = null,
        };

        string? sql = CrossManifestQueryBuilder.BuildQuery([composite], CrossManifestQueryOptions.Default);

        Assert.NotNull(sql);
        Assert.Contains("AND", sql);
        Assert.Contains("\"region_id\"", sql);
        Assert.Contains("\"year\"", sql);
    }

    [Fact]
    public void BuildQuery_EndsWithSemicolon()
    {
        List<JoinCandidate> candidates =
        [
            MakeCandidate("orders", "customers", "id", "id", 0.8),
        ];

        string? sql = CrossManifestQueryBuilder.BuildQuery(candidates, CrossManifestQueryOptions.Default);

        Assert.NotNull(sql);
        Assert.EndsWith(";", sql);
    }

    // ── Annotations ──

    [Fact]
    public void GenerateAnnotations_ReturnsAnnotationsForQualifyingCandidates()
    {
        List<JoinCandidate> candidates =
        [
            MakeCandidate("orders", "customers", "id", "id", 0.8),
            MakeCandidate("orders", "products", "pid", "pid", 0.3), // Below threshold.
        ];

        IReadOnlyList<DatumIngest.Manifest.Insights.QueryAnnotation> annotations =
            CrossManifestQueryBuilder.GenerateAnnotations(candidates, CrossManifestQueryOptions.Default);

        Assert.Single(annotations);
    }

    // ── Helpers ──

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
            Evidence = MakeEvidence(confidence),
            Confidence = confidence,
            EstimatedJoinType = JoinClassification.OneToMany,
            EstimatedFanout = null,
            QualityWarnings = null,
        };
    }

    private static JoinCandidate MakeCandidateWithNullKey(
        string leftTable,
        string rightTable,
        string leftColumn,
        string rightColumn,
        double confidence,
        double nullKeyRatio)
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
                NullKeyRatio = nullKeyRatio,
                UniqueKeyScore = 0.9,
                CompositeConfidence = confidence,
            },
            Confidence = confidence,
            EstimatedJoinType = JoinClassification.OneToMany,
            EstimatedFanout = null,
            QualityWarnings = null,
        };
    }

    private static JoinEvidence MakeEvidence(double compositeConfidence)
    {
        return new JoinEvidence
        {
            NameSimilarity = 1.0,
            TypeCompatibility = 1.0,
            TopKJaccard = 0.5,
            CardinalityRatio = 0.8,
            RangeOverlap = null,
            NullKeyRatio = 0.0,
            UniqueKeyScore = 0.9,
            CompositeConfidence = compositeConfidence,
        };
    }
}
