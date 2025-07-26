namespace DatumIngest.Tests.Manifest.CrossManifest;

using DatumIngest.Manifest;
using DatumIngest.Manifest.CrossManifest;
using DatumIngest.Manifest.CrossManifest.Rules;
using DatumIngest.Manifest.Insights;
using DatumIngest.Model;

/// <summary>
/// Tests for all <see cref="ICrossManifestInsightRule"/> implementations — verifies
/// each rule fires on the correct conditions and does not fire on safe inputs.
/// </summary>
public sealed class CrossManifestInsightRuleTests
{
    // ── ManyToManyJoinRule ──

    [Fact]
    public void ManyToManyJoinRule_Fires_OnManyToManyCandidate()
    {
        ManyToManyJoinRule rule = new();

        List<JoinCandidate> candidates =
        [
            MakeCandidate("orders", "products", JoinClassification.ManyToMany, fanout: 5.0),
        ];

        List<RawFinding> findings = rule.Evaluate([], candidates, CrossManifestThresholds.Default).ToList();

        Assert.Single(findings);
        Assert.Equal(InsightKind.ManyToManyJoin, findings[0].Kind);
        Assert.Equal(InsightSeverity.Warning, findings[0].Severity);
    }

    [Fact]
    public void ManyToManyJoinRule_DoesNotFire_OnOneToMany()
    {
        ManyToManyJoinRule rule = new();

        List<JoinCandidate> candidates =
        [
            MakeCandidate("orders", "customers", JoinClassification.OneToMany),
        ];

        List<RawFinding> findings = rule.Evaluate([], candidates, CrossManifestThresholds.Default).ToList();

        Assert.Empty(findings);
    }

    // ── HighNullKeyRule ──

    [Fact]
    public void HighNullKeyRule_Fires_WhenNullKeyRatioExceedsThreshold()
    {
        HighNullKeyRule rule = new();

        List<JoinCandidate> candidates =
        [
            MakeCandidateWithNullKey("orders", "customers", nullKeyRatio: 0.5),
        ];

        List<RawFinding> findings = rule.Evaluate([], candidates, CrossManifestThresholds.Default).ToList();

        Assert.Single(findings);
        Assert.Equal(InsightKind.HighNullKey, findings[0].Kind);
    }

    [Fact]
    public void HighNullKeyRule_DoesNotFire_WhenNullKeyRatioBelowThreshold()
    {
        HighNullKeyRule rule = new();

        List<JoinCandidate> candidates =
        [
            MakeCandidateWithNullKey("orders", "customers", nullKeyRatio: 0.1),
        ];

        List<RawFinding> findings = rule.Evaluate([], candidates, CrossManifestThresholds.Default).ToList();

        Assert.Empty(findings);
    }

    // ── CardinalityMismatchRule ──

    [Fact]
    public void CardinalityMismatchRule_Fires_WhenRatioVeryLow()
    {
        CardinalityMismatchRule rule = new();

        List<JoinCandidate> candidates =
        [
            MakeCandidateWithCardinality("orders", "customers", cardinalityRatio: 0.005),
        ];

        List<RawFinding> findings = rule.Evaluate([], candidates, CrossManifestThresholds.Default).ToList();

        Assert.Single(findings);
        Assert.Equal(InsightKind.CardinalityMismatch, findings[0].Kind);
        Assert.Equal(InsightSeverity.Info, findings[0].Severity);
    }

    [Fact]
    public void CardinalityMismatchRule_DoesNotFire_WhenRatioAboveThreshold()
    {
        CardinalityMismatchRule rule = new();

        List<JoinCandidate> candidates =
        [
            MakeCandidateWithCardinality("orders", "customers", cardinalityRatio: 0.5),
        ];

        List<RawFinding> findings = rule.Evaluate([], candidates, CrossManifestThresholds.Default).ToList();

        Assert.Empty(findings);
    }

    // ── DisjointRangeRule ──

    [Fact]
    public void DisjointRangeRule_Fires_Critical_WhenCompletelyDisjoint()
    {
        DisjointRangeRule rule = new();

        List<JoinCandidate> candidates =
        [
            MakeCandidateWithRangeOverlap("orders", "customers", rangeOverlap: 0.0),
        ];

        List<RawFinding> findings = rule.Evaluate([], candidates, CrossManifestThresholds.Default).ToList();

        Assert.Single(findings);
        Assert.Equal(InsightKind.DisjointRange, findings[0].Kind);
        Assert.Equal(InsightSeverity.Critical, findings[0].Severity);
    }

    [Fact]
    public void DisjointRangeRule_Fires_Warning_WhenLowOverlap()
    {
        DisjointRangeRule rule = new();

        List<JoinCandidate> candidates =
        [
            MakeCandidateWithRangeOverlap("orders", "customers", rangeOverlap: 0.03),
        ];

        List<RawFinding> findings = rule.Evaluate([], candidates, CrossManifestThresholds.Default).ToList();

        Assert.Single(findings);
        Assert.Equal(InsightSeverity.Warning, findings[0].Severity);
    }

    [Fact]
    public void DisjointRangeRule_DoesNotFire_WhenGoodOverlap()
    {
        DisjointRangeRule rule = new();

        List<JoinCandidate> candidates =
        [
            MakeCandidateWithRangeOverlap("orders", "customers", rangeOverlap: 0.5),
        ];

        List<RawFinding> findings = rule.Evaluate([], candidates, CrossManifestThresholds.Default).ToList();

        Assert.Empty(findings);
    }

    [Fact]
    public void DisjointRangeRule_DoesNotFire_WhenNoRangeOverlap()
    {
        DisjointRangeRule rule = new();

        List<JoinCandidate> candidates =
        [
            MakeCandidateWithRangeOverlap("orders", "customers", rangeOverlap: null),
        ];

        List<RawFinding> findings = rule.Evaluate([], candidates, CrossManifestThresholds.Default).ToList();

        Assert.Empty(findings);
    }

    // ── SchemaDriftRule ──

    [Fact]
    public void SchemaDriftRule_Fires_WhenSameColumnDifferentTypes()
    {
        SchemaDriftRule rule = new();

        List<ManifestWithName> manifests =
        [
            MakeManifestWithName("orders",
                MakeFeature("customer_id", DataKind.Scalar)),
            MakeManifestWithName("customers",
                MakeFeature("customer_id", DataKind.String)),
        ];

        List<RawFinding> findings = rule.Evaluate(manifests, [], CrossManifestThresholds.Default).ToList();

        Assert.Single(findings);
        Assert.Equal(InsightKind.SchemaDrift, findings[0].Kind);
    }

    [Fact]
    public void SchemaDriftRule_DoesNotFire_WhenSameTypes()
    {
        SchemaDriftRule rule = new();

        List<ManifestWithName> manifests =
        [
            MakeManifestWithName("orders",
                MakeFeature("customer_id", DataKind.String)),
            MakeManifestWithName("customers",
                MakeFeature("customer_id", DataKind.String)),
        ];

        List<RawFinding> findings = rule.Evaluate(manifests, [], CrossManifestThresholds.Default).ToList();

        Assert.Empty(findings);
    }

    // ── DenormalizationHintRule ──

    [Fact]
    public void DenormalizationHintRule_Fires_WhenHighColumnOverlap()
    {
        DenormalizationHintRule rule = new();

        // Two tables with the same two columns, high TopK Jaccard and exact type.
        List<ManifestWithName> manifests =
        [
            MakeManifestWithName("table_a",
                MakeFeature("name", DataKind.String),
                MakeFeature("code", DataKind.String)),
            MakeManifestWithName("table_b",
                MakeFeature("name", DataKind.String),
                MakeFeature("code", DataKind.String)),
        ];

        List<JoinCandidate> candidates =
        [
            MakeCandidateWithTopKJaccard("table_a", "table_b", "name", "name", topKJaccard: 0.8, typeCompatibility: 1.0),
            MakeCandidateWithTopKJaccard("table_a", "table_b", "code", "code", topKJaccard: 0.7, typeCompatibility: 1.0),
        ];

        List<RawFinding> findings = rule.Evaluate(manifests, candidates, CrossManifestThresholds.Default).ToList();

        Assert.Single(findings);
        Assert.Equal(InsightKind.DenormalizationHint, findings[0].Kind);
    }

    [Fact]
    public void DenormalizationHintRule_DoesNotFire_WhenLowOverlap()
    {
        DenormalizationHintRule rule = new();

        List<ManifestWithName> manifests =
        [
            MakeManifestWithName("table_a",
                MakeFeature("name", DataKind.String),
                MakeFeature("code", DataKind.String),
                MakeFeature("extra1", DataKind.String),
                MakeFeature("extra2", DataKind.String),
                MakeFeature("extra3", DataKind.String)),
            MakeManifestWithName("table_b",
                MakeFeature("name", DataKind.String),
                MakeFeature("other", DataKind.String)),
        ];

        List<JoinCandidate> candidates =
        [
            MakeCandidateWithTopKJaccard("table_a", "table_b", "name", "name", topKJaccard: 0.8, typeCompatibility: 1.0),
        ];

        List<RawFinding> findings = rule.Evaluate(manifests, candidates, CrossManifestThresholds.Default).ToList();

        // Only 1 overlap out of 2 columns on smaller table — not enough.
        Assert.Empty(findings);
    }

    // ── StarSchemaRule ──

    [Fact]
    public void StarSchemaRule_Fires_WhenFactTableHasEnoughDimensions()
    {
        StarSchemaRule rule = new();

        List<JoinCandidate> candidates =
        [
            MakeCandidate("dim_customer", "fact_sales", JoinClassification.OneToMany, confidence: 0.8),
            MakeCandidate("dim_product", "fact_sales", JoinClassification.OneToMany, confidence: 0.7),
            MakeCandidate("dim_date", "fact_sales", JoinClassification.OneToMany, confidence: 0.6),
        ];

        List<RawFinding> findings = rule.Evaluate([], candidates, CrossManifestThresholds.Default).ToList();

        Assert.Single(findings);
        Assert.Equal(InsightKind.StarSchema, findings[0].Kind);
        Assert.Contains("fact_sales", findings[0].AffectedFeatures);
    }

    [Fact]
    public void StarSchemaRule_DoesNotFire_WithTooFewDimensions()
    {
        StarSchemaRule rule = new();

        List<JoinCandidate> candidates =
        [
            MakeCandidate("dim_customer", "fact_sales", JoinClassification.OneToMany, confidence: 0.8),
            MakeCandidate("dim_product", "fact_sales", JoinClassification.OneToMany, confidence: 0.7),
        ];

        List<RawFinding> findings = rule.Evaluate([], candidates, CrossManifestThresholds.Default).ToList();

        Assert.Empty(findings);
    }

    [Fact]
    public void StarSchemaRule_DoesNotFire_OnManyToMany()
    {
        StarSchemaRule rule = new();

        List<JoinCandidate> candidates =
        [
            MakeCandidate("dim_customer", "fact_sales", JoinClassification.ManyToMany, confidence: 0.8),
            MakeCandidate("dim_product", "fact_sales", JoinClassification.ManyToMany, confidence: 0.7),
            MakeCandidate("dim_date", "fact_sales", JoinClassification.ManyToMany, confidence: 0.6),
        ];

        List<RawFinding> findings = rule.Evaluate([], candidates, CrossManifestThresholds.Default).ToList();

        Assert.Empty(findings);
    }

    // ── Helpers ──

    private static ManifestWithName MakeManifestWithName(string name, params FeatureManifest[] features)
    {
        return new ManifestWithName(name, new QueryResultsManifest
        {
            RowCount = 1000,
            GeneratedAtUtc = DateTime.UtcNow,
            Features = features,
        });
    }

    private static StringFeatureManifest MakeFeature(string name, DataKind kind)
    {
        return new StringFeatureManifest
        {
            Name = name,
            Kind = kind,
            Count = 1000,
            NullCount = 0,
            ValidCount = 1000,
            NullRatio = 0.0,
            EstimatedDistinctCount = 100,
            TopKValues = [],
            MinLength = 1,
            MaxLength = 50,
        };
    }

    private static JoinCandidate MakeCandidate(
        string leftTable,
        string rightTable,
        JoinClassification joinType,
        double? fanout = null,
        double confidence = 0.7)
    {
        return new JoinCandidate
        {
            LeftTable = leftTable,
            RightTable = rightTable,
            LeftColumns = ["id"],
            RightColumns = ["id"],
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
            EstimatedJoinType = joinType,
            EstimatedFanout = fanout,
            QualityWarnings = null,
        };
    }

    private static JoinCandidate MakeCandidateWithNullKey(
        string leftTable,
        string rightTable,
        double nullKeyRatio)
    {
        return new JoinCandidate
        {
            LeftTable = leftTable,
            RightTable = rightTable,
            LeftColumns = ["key_col"],
            RightColumns = ["key_col"],
            Evidence = new JoinEvidence
            {
                NameSimilarity = 1.0,
                TypeCompatibility = 1.0,
                TopKJaccard = 0.5,
                CardinalityRatio = 0.8,
                RangeOverlap = null,
                NullKeyRatio = nullKeyRatio,
                UniqueKeyScore = 0.9,
                CompositeConfidence = 0.7,
            },
            Confidence = 0.7,
            EstimatedJoinType = JoinClassification.OneToMany,
            EstimatedFanout = null,
            QualityWarnings = null,
        };
    }

    private static JoinCandidate MakeCandidateWithCardinality(
        string leftTable,
        string rightTable,
        double cardinalityRatio)
    {
        return new JoinCandidate
        {
            LeftTable = leftTable,
            RightTable = rightTable,
            LeftColumns = ["key_col"],
            RightColumns = ["key_col"],
            Evidence = new JoinEvidence
            {
                NameSimilarity = 1.0,
                TypeCompatibility = 1.0,
                TopKJaccard = 0.5,
                CardinalityRatio = cardinalityRatio,
                RangeOverlap = null,
                NullKeyRatio = 0.0,
                UniqueKeyScore = 0.9,
                CompositeConfidence = 0.7,
            },
            Confidence = 0.7,
            EstimatedJoinType = JoinClassification.OneToMany,
            EstimatedFanout = null,
            QualityWarnings = null,
        };
    }

    private static JoinCandidate MakeCandidateWithRangeOverlap(
        string leftTable,
        string rightTable,
        double? rangeOverlap)
    {
        return new JoinCandidate
        {
            LeftTable = leftTable,
            RightTable = rightTable,
            LeftColumns = ["amount"],
            RightColumns = ["amount"],
            Evidence = new JoinEvidence
            {
                NameSimilarity = 1.0,
                TypeCompatibility = 1.0,
                TopKJaccard = 0.5,
                CardinalityRatio = 0.8,
                RangeOverlap = rangeOverlap,
                NullKeyRatio = 0.0,
                UniqueKeyScore = 0.9,
                CompositeConfidence = 0.7,
            },
            Confidence = 0.7,
            EstimatedJoinType = JoinClassification.OneToMany,
            EstimatedFanout = null,
            QualityWarnings = null,
        };
    }

    private static JoinCandidate MakeCandidateWithTopKJaccard(
        string leftTable,
        string rightTable,
        string leftColumn,
        string rightColumn,
        double topKJaccard,
        double typeCompatibility)
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
                TypeCompatibility = typeCompatibility,
                TopKJaccard = topKJaccard,
                CardinalityRatio = 0.8,
                RangeOverlap = null,
                NullKeyRatio = 0.0,
                UniqueKeyScore = 0.5,
                CompositeConfidence = 0.7,
            },
            Confidence = 0.7,
            EstimatedJoinType = JoinClassification.OneToMany,
            EstimatedFanout = null,
            QualityWarnings = null,
        };
    }
}
