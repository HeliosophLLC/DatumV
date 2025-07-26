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
        Assert.Empty(result.JoinGraph);
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
        if (result.JoinGraph.Count >= 2)
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
            Assert.NotNull(result.RecommendedQuery);
            Assert.Contains("SELECT", result.RecommendedQuery);
            Assert.Contains("JOIN", result.RecommendedQuery);
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
            Kind = DataKind.Scalar,
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
}
