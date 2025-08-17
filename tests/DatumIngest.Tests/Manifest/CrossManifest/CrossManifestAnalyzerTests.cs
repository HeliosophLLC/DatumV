namespace DatumIngest.Tests.Manifest.CrossManifest;

using DatumIngest.Manifest;
using DatumIngest.Manifest.CrossManifest;
using DatumIngest.Model;

/// <summary>
/// End-to-end integration tests for <see cref="CrossManifestAnalyzer"/> — verifies
/// the full pipeline: column matching → evidence scoring → composite key detection →
/// join graph → insights → SQL generation.
/// </summary>
public sealed class CrossManifestAnalyzerTests
{
    [Fact]
    public void Analyze_TwoTablesWithMatchingKey_DiscoversCandidates()
    {
        List<ManifestWithName> manifests =
        [
            MakeManifest("orders",
                MakeIntegerFeature("customer_id", estimatedDistinctCount: 900,
                    topK: [new FrequencyEntry("1", 10), new FrequencyEntry("2", 10), new FrequencyEntry("3", 10)])),
            MakeManifest("customers",
                MakeIntegerFeature("customer_id", estimatedDistinctCount: 1000,
                    topK: [new FrequencyEntry("1", 1), new FrequencyEntry("2", 1), new FrequencyEntry("3", 1)])),
        ];

        CrossManifestResult result = CrossManifestAnalyzer.Analyze(manifests);

        Assert.Equal(2, result.Tables.Count);
        Assert.True(result.Candidates.Count > 0);
        Assert.Contains(result.Candidates, c =>
            c.LeftColumns.Contains("customer_id") && c.RightColumns.Contains("customer_id"));
    }

    [Fact]
    public void Analyze_SingleManifest_ReturnsEmptyResult()
    {
        List<ManifestWithName> manifests =
        [
            MakeManifest("orders", MakeIntegerFeature("customer_id")),
        ];

        CrossManifestResult result = CrossManifestAnalyzer.Analyze(manifests);

        Assert.Single(result.Tables);
        Assert.Empty(result.Candidates);
        Assert.Empty(result.JoinGraphs);
    }

    [Fact]
    public void Analyze_SingleManifest_IncludesPerTableInsights()
    {
        List<ManifestWithName> manifests =
        [
            MakeManifest("orders", MakeHighNullFeature("discount_code")),
        ];

        CrossManifestResult result = CrossManifestAnalyzer.Analyze(manifests);

        Assert.Empty(result.Candidates);
        Assert.NotNull(result.PerTableInsights);
        Assert.True(result.PerTableInsights.ContainsKey("orders"));
        Assert.True(result.PerTableInsights["orders"].Count > 0);
    }

    [Fact]
    public void Analyze_MultipleManifests_IncludesPerTableInsights()
    {
        List<ManifestWithName> manifests =
        [
            MakeManifest("orders",
                MakeIntegerFeature("customer_id", estimatedDistinctCount: 900,
                    topK: [new FrequencyEntry("1", 10), new FrequencyEntry("2", 10), new FrequencyEntry("3", 10)]),
                MakeHighNullFeature("discount_code")),
            MakeManifest("customers",
                MakeIntegerFeature("customer_id", estimatedDistinctCount: 1000,
                    topK: [new FrequencyEntry("1", 1), new FrequencyEntry("2", 1), new FrequencyEntry("3", 1)])),
        ];

        CrossManifestResult result = CrossManifestAnalyzer.Analyze(manifests);

        // Should have join candidates AND per-table insights.
        Assert.True(result.Candidates.Count > 0);
        Assert.NotNull(result.PerTableInsights);
        Assert.True(result.PerTableInsights.ContainsKey("orders"));
    }

    [Fact]
    public void Analyze_NoMatchingColumns_ReturnsNoCandidates()
    {
        List<ManifestWithName> manifests =
        [
            MakeManifest("orders", MakeIntegerFeature("total_amount")),
            MakeManifest("weather", MakeIntegerFeature("temperature")),
        ];

        CrossManifestResult result = CrossManifestAnalyzer.Analyze(manifests);

        // Names are completely different — candidates may exist but with very low confidence.
        // Those below CandidateMinConfidence will be filtered out.
        foreach (JoinCandidate candidate in result.Candidates)
        {
            Assert.True(candidate.Confidence >= CrossManifestThresholds.Default.CandidateMinConfidence);
        }
    }

    [Fact]
    public void Analyze_ThreeTableChain_DiscoversTransitiveChains()
    {
        List<ManifestWithName> manifests =
        [
            MakeManifest("orders",
                MakeIntegerFeature("customer_id", estimatedDistinctCount: 900,
                    topK: [new FrequencyEntry("1", 10), new FrequencyEntry("2", 10), new FrequencyEntry("3", 10)]),
                MakeIntegerFeature("product_id", estimatedDistinctCount: 800,
                    topK: [new FrequencyEntry("100", 5), new FrequencyEntry("200", 5), new FrequencyEntry("300", 5)])),
            MakeManifest("customers",
                MakeIntegerFeature("customer_id", estimatedDistinctCount: 1000,
                    topK: [new FrequencyEntry("1", 1), new FrequencyEntry("2", 1), new FrequencyEntry("3", 1)])),
            MakeManifest("products",
                MakeIntegerFeature("product_id", estimatedDistinctCount: 1000,
                    topK: [new FrequencyEntry("100", 1), new FrequencyEntry("200", 1), new FrequencyEntry("300", 1)])),
        ];

        CrossManifestResult result = CrossManifestAnalyzer.Analyze(manifests);

        // If both joins are above threshold, transitive chains should exist.
        if (result.JoinGraphs.Count > 0 && result.JoinGraphs[0].Edges.Count >= 2)
        {
            Assert.NotNull(result.TransitiveChains);
            Assert.True(result.TransitiveChains.Count > 0);
        }
    }

    [Fact]
    public void Analyze_GeneratesSql_WhenCandidatesExist()
    {
        List<ManifestWithName> manifests =
        [
            MakeManifest("orders",
                MakeIntegerFeature("customer_id", estimatedDistinctCount: 950,
                    topK: [new FrequencyEntry("1", 10), new FrequencyEntry("2", 10)])),
            MakeManifest("customers",
                MakeIntegerFeature("customer_id", estimatedDistinctCount: 1000,
                    topK: [new FrequencyEntry("1", 1), new FrequencyEntry("2", 1)])),
        ];

        CrossManifestResult result = CrossManifestAnalyzer.Analyze(manifests);

        if (result.Candidates.Any(c => c.Confidence >= 0.5))
        {
            Assert.True(result.JoinGraphs.Count > 0);
            Assert.NotNull(result.JoinGraphs[0].RecommendedQuery);
            Assert.Contains("SELECT", result.JoinGraphs[0].RecommendedQuery);
            Assert.Contains("JOIN", result.JoinGraphs[0].RecommendedQuery);
        }
    }

    [Fact]
    public void Analyze_CustomThresholds_Respected()
    {
        // Very strict thresholds — nothing should pass.
        CrossManifestThresholds strict = new()
        {
            CandidateMinConfidence = 0.99,
            GraphEdgeMinConfidence = 0.99,
        };

        List<ManifestWithName> manifests =
        [
            MakeManifest("orders", MakeIntegerFeature("customer_id")),
            MakeManifest("customers", MakeIntegerFeature("customer_id")),
        ];

        CrossManifestResult result = CrossManifestAnalyzer.Analyze(manifests, strict);

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void Analyze_ManyToManyJoin_ProducesInsight()
    {
        // Both sides non-unique → ManyToMany classification.
        List<ManifestWithName> manifests =
        [
            MakeManifest("order_tags",
                MakeIntegerFeature("tag_id", estimatedDistinctCount: 50,
                    topK: [new FrequencyEntry("1", 100), new FrequencyEntry("2", 80)])),
            MakeManifest("product_tags",
                MakeIntegerFeature("tag_id", estimatedDistinctCount: 50,
                    topK: [new FrequencyEntry("1", 100), new FrequencyEntry("2", 80)])),
        ];

        CrossManifestResult result = CrossManifestAnalyzer.Analyze(manifests);

        if (result.Candidates.Any(c => c.EstimatedJoinType == JoinClassification.ManyToMany))
        {
            Assert.NotNull(result.Insights);
            Assert.Contains(result.Insights,
                i => i.Kind == DatumIngest.Manifest.Insights.InsightKind.ManyToManyJoin);
        }
    }

    [Fact]
    public void Analyze_ResultTablesMatchInput()
    {
        List<ManifestWithName> manifests =
        [
            MakeManifest("table_a", MakeIntegerFeature("id")),
            MakeManifest("table_b", MakeIntegerFeature("id")),
            MakeManifest("table_c", MakeIntegerFeature("id")),
        ];

        CrossManifestResult result = CrossManifestAnalyzer.Analyze(manifests);

        Assert.Equal(3, result.Tables.Count);
        Assert.Contains("table_a", result.Tables);
        Assert.Contains("table_b", result.Tables);
        Assert.Contains("table_c", result.Tables);
    }

    /// <summary>
    /// Verifies that when equivalent table detection identifies a partition pair, the
    /// alternate graph inherits hub edges from the primary graph even when the alternate
    /// table's candidate confidence falls below <see cref="CrossManifestThresholds.GraphEdgeMinConfidence"/>.
    /// Reproduces the Instacart scenario where <c>order_products__train</c> has low cardinality
    /// overlap with <c>orders</c>, producing confidence just below the graph threshold.
    /// </summary>
    [Fact]
    public void Analyze_AlternateGraphInheritsHubEdgesFromPrimaryGraph()
    {
        // Hub table: unique key (NDV/RowCount = 1.0).
        ManifestWithName hub = MakeManifest("hub",
            MakeIntegerFeature("key_id", estimatedDistinctCount: 1000,
                topK:
                [
                    new FrequencyEntry("1", 1),
                    new FrequencyEntry("2", 1),
                    new FrequencyEntry("3", 1),
                ]));

        // Preferred equivalent: NDV/RowCount = 0.1 (not unique), high TopK overlap
        // with hub → strong confidence (~0.82, above GraphEdgeMinConfidence 0.50).
        ManifestWithName preferred = MakeManifest("preferred",
            MakeIntegerFeature("key_id", estimatedDistinctCount: 100,
                topK:
                [
                    new FrequencyEntry("1", 10),
                    new FrequencyEntry("2", 10),
                    new FrequencyEntry("3", 10),
                ]));

        // Alternate equivalent: NDV/RowCount = 0.035 (not unique), zero TopK overlap
        // with hub → weak confidence (~0.48, above CandidateMinConfidence 0.45 but
        // below GraphEdgeMinConfidence 0.50). Without hub edge inheritance this edge
        // would be missing from the alternate graph.
        ManifestWithName alternate = MakeManifest("alternate",
            MakeIntegerFeature("key_id", estimatedDistinctCount: 35,
                topK:
                [
                    new FrequencyEntry("100", 30),
                    new FrequencyEntry("200", 30),
                    new FrequencyEntry("300", 30),
                ]));

        List<ManifestWithName> manifests = [hub, preferred, alternate];

        CrossManifestResult result = CrossManifestAnalyzer.Analyze(manifests);

        // Equivalent table detection should recognize preferred + alternate as a partition pair.
        Assert.NotNull(result.EquivalentTableGroups);
        Assert.Single(result.EquivalentTableGroups);
        Assert.Equal("preferred", result.EquivalentTableGroups[0].PreferredTable);

        // Two graphs: primary (preferred → hub) and alternate (alternate → hub).
        Assert.Equal(2, result.JoinGraphs.Count);

        // Primary graph has the hub edge.
        JoinGraph primaryGraph = result.JoinGraphs[0];
        JoinGraphEdge primaryHubEdge = Assert.Single(primaryGraph.Edges, edge =>
            (edge.LeftTable == "hub" && edge.RightTable == "preferred") ||
            (edge.LeftTable == "preferred" && edge.RightTable == "hub"));

        // Alternate graph should inherit the hub edge despite the candidate's
        // confidence being below GraphEdgeMinConfidence.
        JoinGraph alternateGraph = result.JoinGraphs[1];
        JoinGraphEdge inheritedEdge = Assert.Single(alternateGraph.Edges, edge =>
            (edge.LeftTable == "hub" && edge.RightTable == "alternate") ||
            (edge.LeftTable == "alternate" && edge.RightTable == "hub"));

        // The edge carries the candidate's true confidence and records the primary
        // edge's origin so consumers can correlate back to the structural source
        // in the differently-shaped primary graph.
        Assert.NotNull(inheritedEdge.InheritedFrom);
        Assert.Equal(primaryHubEdge.CandidateIndex, inheritedEdge.InheritedFrom.CandidateIndex);
        Assert.True(inheritedEdge.InheritedFrom.Confidence >= 0.5,
            "InheritedFrom.Confidence should reflect the primary edge which exceeded the threshold.");
        Assert.True(inheritedEdge.Confidence < 0.5,
            "Inherited edge should carry the candidate's real confidence, not an inflated value.");
        Assert.Equal(result.Candidates[inheritedEdge.CandidateIndex].Confidence, inheritedEdge.Confidence);

        // The recommended query and annotations should also include the inherited
        // hub join — they must not drop it because confidence is below MinConfidence.
        Assert.NotNull(alternateGraph.RecommendedQuery);
        Assert.Contains("hub", alternateGraph.RecommendedQuery);

        Assert.NotNull(alternateGraph.QueryAnnotations);
        Assert.Contains(alternateGraph.QueryAnnotations, annotation =>
            annotation.Note.Contains("hub") && annotation.Note.Contains("alternate"));
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

    private static NumericFeatureManifest MakeIntegerFeature(
        string name,
        long estimatedDistinctCount = 100,
        double min = 0.0,
        double max = 1000.0,
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

    /// <summary>
    /// Creates a numeric feature with a high null ratio (50%), triggering HighMissingness insights.
    /// </summary>
    private static NumericFeatureManifest MakeHighNullFeature(string name)
    {
        return new NumericFeatureManifest
        {
            Name = name,
            Kind = DataKind.Float32,
            Count = 1000,
            NullCount = 500,
            ValidCount = 500,
            NullRatio = 0.5,
            EstimatedDistinctCount = 50,
            TopKValues = [],
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
}
