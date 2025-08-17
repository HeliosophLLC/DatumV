namespace DatumIngest.Tests.Manifest.CrossManifest;

using DatumIngest.Manifest;
using DatumIngest.Manifest.CrossManifest;
using DatumIngest.Model;

/// <summary>
/// Unit tests for <see cref="EquivalentTableDetector"/> — validates detection of
/// structurally equivalent table groups (e.g., train/test splits) from schema overlap,
/// inter-table edge classification, and shared hub connections.
/// </summary>
public sealed class EquivalentTableDetectorTests
{
    [Fact]
    public void Detect_EquivalentPairWithSharedHub_ReturnsGroup()
    {
        // Simulate order_products__prior and order_products__train sharing the same schema
        // and both connecting to an "orders" hub via order_id.
        List<ManifestWithName> manifests =
        [
            MakeManifest("orders", rowCount: 3_000_000,
                MakeIntegerFeature("order_id", estimatedDistinctCount: 3_000_000),
                MakeIntegerFeature("user_id", estimatedDistinctCount: 200_000)),
            MakeManifest("order_products__prior", rowCount: 32_000_000,
                MakeIntegerFeature("order_id", estimatedDistinctCount: 3_000_000),
                MakeIntegerFeature("product_id", estimatedDistinctCount: 49_000),
                MakeIntegerFeature("add_to_cart_order", estimatedDistinctCount: 50),
                MakeIntegerFeature("reordered", estimatedDistinctCount: 2)),
            MakeManifest("order_products__train", rowCount: 1_400_000,
                MakeIntegerFeature("order_id", estimatedDistinctCount: 130_000),
                MakeIntegerFeature("product_id", estimatedDistinctCount: 39_000),
                MakeIntegerFeature("add_to_cart_order", estimatedDistinctCount: 50),
                MakeIntegerFeature("reordered", estimatedDistinctCount: 2)),
        ];

        List<JoinCandidate> candidates =
        [
            // prior → orders (ManyToOne — valid hub connection)
            MakeCandidate("order_products__prior", "orders", "order_id",
                JoinClassification.ManyToOne, confidence: 0.90, uniqueKeyScore: 0.95),
            // train → orders (ManyToOne — valid hub connection)
            MakeCandidate("order_products__train", "orders", "order_id",
                JoinClassification.ManyToOne, confidence: 0.85, uniqueKeyScore: 0.95),
            // prior ↔ train (ManyToMany — partition edge)
            MakeCandidate("order_products__prior", "order_products__train", "order_id",
                JoinClassification.ManyToMany, confidence: 0.50, uniqueKeyScore: 0.02),
            MakeCandidate("order_products__prior", "order_products__train", "product_id",
                JoinClassification.ManyToMany, confidence: 0.40, uniqueKeyScore: 0.01),
        ];

        IReadOnlyList<EquivalentTableGroup> groups = EquivalentTableDetector.Detect(manifests, candidates);

        Assert.Single(groups);

        EquivalentTableGroup group = groups[0];
        Assert.Equal(2, group.Tables.Count);
        Assert.Contains("order_products__prior", group.Tables);
        Assert.Contains("order_products__train", group.Tables);
        Assert.True(group.SchemaOverlap >= 0.75);
        Assert.Contains("order_id", group.SharedColumns);
        Assert.Contains("product_id", group.SharedColumns);
    }

    [Fact]
    public void Detect_PreferredTableHasHigherHubConfidence()
    {
        List<ManifestWithName> manifests =
        [
            MakeManifest("hub", rowCount: 1000,
                MakeIntegerFeature("id", estimatedDistinctCount: 1000)),
            MakeManifest("table_a", rowCount: 5000,
                MakeIntegerFeature("id", estimatedDistinctCount: 800),
                MakeIntegerFeature("value", estimatedDistinctCount: 50)),
            MakeManifest("table_b", rowCount: 2000,
                MakeIntegerFeature("id", estimatedDistinctCount: 800),
                MakeIntegerFeature("value", estimatedDistinctCount: 50)),
        ];

        List<JoinCandidate> candidates =
        [
            MakeCandidate("table_a", "hub", "id",
                JoinClassification.ManyToOne, confidence: 0.90, uniqueKeyScore: 0.95),
            MakeCandidate("table_b", "hub", "id",
                JoinClassification.ManyToOne, confidence: 0.70, uniqueKeyScore: 0.95),
            MakeCandidate("table_a", "table_b", "id",
                JoinClassification.ManyToMany, confidence: 0.40, uniqueKeyScore: 0.05),
        ];

        IReadOnlyList<EquivalentTableGroup> groups = EquivalentTableDetector.Detect(manifests, candidates);

        Assert.Single(groups);
        // table_a has higher hub confidence (0.90 vs 0.70), so it should be preferred.
        Assert.Equal("table_a", groups[0].PreferredTable);
    }

    [Fact]
    public void Detect_RowCountsCaptured()
    {
        List<ManifestWithName> manifests =
        [
            MakeManifest("hub", rowCount: 500,
                MakeIntegerFeature("id", estimatedDistinctCount: 500)),
            MakeManifest("split_a", rowCount: 32_000_000,
                MakeIntegerFeature("id", estimatedDistinctCount: 400),
                MakeIntegerFeature("col1", estimatedDistinctCount: 10)),
            MakeManifest("split_b", rowCount: 1_400_000,
                MakeIntegerFeature("id", estimatedDistinctCount: 400),
                MakeIntegerFeature("col1", estimatedDistinctCount: 10)),
        ];

        List<JoinCandidate> candidates =
        [
            MakeCandidate("split_a", "hub", "id",
                JoinClassification.ManyToOne, confidence: 0.80, uniqueKeyScore: 0.90),
            MakeCandidate("split_b", "hub", "id",
                JoinClassification.ManyToOne, confidence: 0.75, uniqueKeyScore: 0.90),
            MakeCandidate("split_a", "split_b", "id",
                JoinClassification.ManyToMany, confidence: 0.30, uniqueKeyScore: 0.05),
        ];

        IReadOnlyList<EquivalentTableGroup> groups = EquivalentTableDetector.Detect(manifests, candidates);

        Assert.Single(groups);
        Assert.Equal(32_000_000, groups[0].RowCounts["split_a"]);
        Assert.Equal(1_400_000, groups[0].RowCounts["split_b"]);
    }

    [Fact]
    public void Detect_DifferentSchemas_NoGroup()
    {
        // Tables with completely different columns should not be grouped.
        List<ManifestWithName> manifests =
        [
            MakeManifest("hub", rowCount: 100,
                MakeIntegerFeature("id", estimatedDistinctCount: 100)),
            MakeManifest("orders", rowCount: 5000,
                MakeIntegerFeature("id", estimatedDistinctCount: 5000),
                MakeIntegerFeature("customer_id", estimatedDistinctCount: 1000),
                MakeIntegerFeature("total", estimatedDistinctCount: 500)),
            MakeManifest("products", rowCount: 200,
                MakeIntegerFeature("id", estimatedDistinctCount: 200),
                MakeIntegerFeature("name_hash", estimatedDistinctCount: 200),
                MakeIntegerFeature("price", estimatedDistinctCount: 50)),
        ];

        List<JoinCandidate> candidates =
        [
            MakeCandidate("orders", "hub", "id",
                JoinClassification.ManyToOne, confidence: 0.80, uniqueKeyScore: 0.90),
            MakeCandidate("products", "hub", "id",
                JoinClassification.ManyToOne, confidence: 0.80, uniqueKeyScore: 0.90),
            MakeCandidate("orders", "products", "id",
                JoinClassification.ManyToMany, confidence: 0.30, uniqueKeyScore: 0.05),
        ];

        IReadOnlyList<EquivalentTableGroup> groups = EquivalentTableDetector.Detect(manifests, candidates);

        // Schema overlap is only 1/3 ≈ 0.33 — well below 0.75 threshold.
        Assert.Empty(groups);
    }

    [Fact]
    public void Detect_OneToManyEdgeBetweenTables_NoGroup()
    {
        // If the inter-table edge is OneToMany (genuine FK), not equivalent.
        List<ManifestWithName> manifests =
        [
            MakeManifest("hub", rowCount: 100,
                MakeIntegerFeature("id", estimatedDistinctCount: 100)),
            MakeManifest("table_a", rowCount: 5000,
                MakeIntegerFeature("id", estimatedDistinctCount: 1000),
                MakeIntegerFeature("value", estimatedDistinctCount: 50)),
            MakeManifest("table_b", rowCount: 2000,
                MakeIntegerFeature("id", estimatedDistinctCount: 1000),
                MakeIntegerFeature("value", estimatedDistinctCount: 50)),
        ];

        List<JoinCandidate> candidates =
        [
            MakeCandidate("table_a", "hub", "id",
                JoinClassification.ManyToOne, confidence: 0.85, uniqueKeyScore: 0.90),
            MakeCandidate("table_b", "hub", "id",
                JoinClassification.ManyToOne, confidence: 0.80, uniqueKeyScore: 0.90),
            // Genuine FK between the two — NOT partition duplication.
            MakeCandidate("table_a", "table_b", "id",
                JoinClassification.OneToMany, confidence: 0.75, uniqueKeyScore: 0.85),
        ];

        IReadOnlyList<EquivalentTableGroup> groups = EquivalentTableDetector.Detect(manifests, candidates);

        Assert.Empty(groups);
    }

    [Fact]
    public void Detect_HighUniqueKeyScoreBetweenTables_NoGroup()
    {
        // ManyToMany classification but high unique key score → genuine relationship, not partition.
        List<ManifestWithName> manifests =
        [
            MakeManifest("hub", rowCount: 100,
                MakeIntegerFeature("id", estimatedDistinctCount: 100)),
            MakeManifest("table_a", rowCount: 5000,
                MakeIntegerFeature("id", estimatedDistinctCount: 1000),
                MakeIntegerFeature("value", estimatedDistinctCount: 50)),
            MakeManifest("table_b", rowCount: 2000,
                MakeIntegerFeature("id", estimatedDistinctCount: 1000),
                MakeIntegerFeature("value", estimatedDistinctCount: 50)),
        ];

        List<JoinCandidate> candidates =
        [
            MakeCandidate("table_a", "hub", "id",
                JoinClassification.ManyToOne, confidence: 0.85, uniqueKeyScore: 0.90),
            MakeCandidate("table_b", "hub", "id",
                JoinClassification.ManyToOne, confidence: 0.80, uniqueKeyScore: 0.90),
            // ManyToMany but high unique key score — indicates a key-based relationship.
            MakeCandidate("table_a", "table_b", "id",
                JoinClassification.ManyToMany, confidence: 0.60, uniqueKeyScore: 0.80),
        ];

        IReadOnlyList<EquivalentTableGroup> groups = EquivalentTableDetector.Detect(manifests, candidates);

        Assert.Empty(groups);
    }

    [Fact]
    public void Detect_NoSharedHubConnection_NoGroup()
    {
        // Same schema, ManyToMany inter-table edge, but no shared hub → no group.
        List<ManifestWithName> manifests =
        [
            MakeManifest("table_a", rowCount: 5000,
                MakeIntegerFeature("id", estimatedDistinctCount: 1000),
                MakeIntegerFeature("value", estimatedDistinctCount: 50)),
            MakeManifest("table_b", rowCount: 2000,
                MakeIntegerFeature("id", estimatedDistinctCount: 1000),
                MakeIntegerFeature("value", estimatedDistinctCount: 50)),
        ];

        List<JoinCandidate> candidates =
        [
            // ManyToMany between them, but no hub connections exist.
            MakeCandidate("table_a", "table_b", "id",
                JoinClassification.ManyToMany, confidence: 0.40, uniqueKeyScore: 0.05),
        ];

        IReadOnlyList<EquivalentTableGroup> groups = EquivalentTableDetector.Detect(manifests, candidates);

        Assert.Empty(groups);
    }

    [Fact]
    public void Detect_TwoTablesNoInterTableCandidate_NoGroup()
    {
        // Two tables with identical schema but no inter-table candidates at all.
        List<ManifestWithName> manifests =
        [
            MakeManifest("hub", rowCount: 100,
                MakeIntegerFeature("hub_key", estimatedDistinctCount: 100)),
            MakeManifest("table_a", rowCount: 5000,
                MakeIntegerFeature("hub_key", estimatedDistinctCount: 80),
                MakeIntegerFeature("measure", estimatedDistinctCount: 50)),
            MakeManifest("table_b", rowCount: 2000,
                MakeIntegerFeature("hub_key", estimatedDistinctCount: 80),
                MakeIntegerFeature("measure", estimatedDistinctCount: 50)),
        ];

        List<JoinCandidate> candidates =
        [
            MakeCandidate("table_a", "hub", "hub_key",
                JoinClassification.ManyToOne, confidence: 0.85, uniqueKeyScore: 0.90),
            MakeCandidate("table_b", "hub", "hub_key",
                JoinClassification.ManyToOne, confidence: 0.80, uniqueKeyScore: 0.90),
            // No candidate between table_a and table_b.
        ];

        IReadOnlyList<EquivalentTableGroup> groups = EquivalentTableDetector.Detect(manifests, candidates);

        // AllInterTableEdgesAreManyToMany returns false when no inter-table candidates exist.
        Assert.Empty(groups);
    }

    // ── Helpers ──

    private static ManifestWithName MakeManifest(string name, long rowCount, params FeatureManifest[] features)
    {
        return new ManifestWithName(name, new QueryResultsManifest
        {
            RowCount = rowCount,
            GeneratedAtUtc = DateTime.UtcNow,
            Features = features,
        });
    }

    private static NumericFeatureManifest MakeIntegerFeature(
        string name,
        long estimatedDistinctCount = 100)
    {
        return new NumericFeatureManifest
        {
            Name = name,
            Kind = DataKind.Float32,
            Count = 1000,
            NullCount = 0,
            ValidCount = 1000,
            NullRatio = 0.0,
            EstimatedDistinctCount = estimatedDistinctCount,
            TopKValues = [],
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
    }

    private static JoinCandidate MakeCandidate(
        string leftTable,
        string rightTable,
        string joinColumn,
        JoinClassification joinType,
        double confidence,
        double uniqueKeyScore)
    {
        return new JoinCandidate
        {
            LeftTable = leftTable,
            RightTable = rightTable,
            LeftColumns = [joinColumn],
            RightColumns = [joinColumn],
            Evidence = new JoinEvidence
            {
                NameSimilarity = 1.0,
                TypeCompatibility = 1.0,
                TopKJaccard = 0.5,
                CardinalityRatio = 0.8,
                RangeOverlap = 0.9,
                NullKeyRatio = 0.0,
                UniqueKeyScore = uniqueKeyScore,
                CompositeConfidence = confidence,
            },
            Confidence = confidence,
            EstimatedJoinType = joinType,
            EstimatedFanout = null,
            QualityWarnings = null,
        };
    }
}
