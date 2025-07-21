namespace DatumIngest.Tests.Manifest.Insights;

using DatumIngest.Manifest;
using DatumIngest.Manifest.Insights;
using DatumIngest.Model;
using DatumIngest.Statistics;

/// <summary>
/// Integration tests verifying that <see cref="ManifestBuilder.Build"/> correctly wires
/// <see cref="InsightAnalyzer"/> and <see cref="QuerySynthesizer"/> when
/// <see cref="InsightThresholds"/> are provided.
/// </summary>
public sealed class ManifestBuilderInsightsIntegrationTests
{
    [Fact]
    public void Build_WithInsightThresholds_PopulatesInsights()
    {
        StatisticsCollector collector = new();

        // All-constant column → should trigger ConstantFeature insight.
        for (int i = 0; i < 100; i++)
        {
            collector.AddRow(new Row(["constant"], [DataValue.FromScalar(42.0f)]));
        }

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["constant"] = DataKind.Scalar };

        QueryResultsManifest manifest = ManifestBuilder.Build(
            stats, kinds, 100, insightThresholds: new InsightThresholds());

        Assert.NotNull(manifest.Insights);
        Assert.NotEmpty(manifest.Insights);
        Assert.Contains(manifest.Insights, i => i.Kind == InsightKind.ConstantFeature);
    }

    [Fact]
    public void Build_WithInsightThresholds_GeneratesRecommendedQuery()
    {
        StatisticsCollector collector = new();

        for (int i = 0; i < 100; i++)
        {
            collector.AddRow(new Row(
                ["constant", "normal"],
                [DataValue.FromScalar(42.0f), DataValue.FromScalar(i * 1.0f)]));
        }

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new()
        {
            ["constant"] = DataKind.Scalar,
            ["normal"] = DataKind.Scalar
        };

        QueryResultsManifest manifest = ManifestBuilder.Build(
            stats, kinds, 100, insightThresholds: new InsightThresholds());

        // Constant column should be auto-dropped → recommended query should exist.
        Assert.NotNull(manifest.RecommendedQuery);
        Assert.Contains("normal", manifest.RecommendedQuery);
    }

    [Fact]
    public void Build_WithoutInsightThresholds_NoInsights()
    {
        StatisticsCollector collector = new();

        for (int i = 0; i < 10; i++)
        {
            collector.AddRow(new Row(["value"], [DataValue.FromScalar(42.0f)]));
        }

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["value"] = DataKind.Scalar };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 10);

        Assert.Null(manifest.Insights);
        Assert.Null(manifest.RecommendedQuery);
        Assert.Null(manifest.FullSuggestedQuery);
        Assert.Null(manifest.QueryAnnotations);
    }

    [Fact]
    public void Build_WithInsightThresholds_PopulatesQueryAnnotations()
    {
        StatisticsCollector collector = new();

        for (int i = 0; i < 100; i++)
        {
            collector.AddRow(new Row(["constant"], [DataValue.FromScalar(42.0f)]));
        }

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["constant"] = DataKind.Scalar };

        QueryResultsManifest manifest = ManifestBuilder.Build(
            stats, kinds, 100, insightThresholds: new InsightThresholds());

        Assert.NotNull(manifest.QueryAnnotations);
        Assert.NotEmpty(manifest.QueryAnnotations);
    }
}
