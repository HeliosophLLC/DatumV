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
public sealed class ManifestBuilderInsightsIntegrationTests : ServiceTestBase
{
    private readonly Arena _arena;

    public ManifestBuilderInsightsIntegrationTests()
    {
        _arena = CreateArena();
    }

    public override void Dispose()
    {
        _arena.Dispose();
        base.Dispose();
    }
    [Fact]
    public void Build_WithInsightThresholds_PopulatesInsights()
    {
        ColumnLookup columnLookup = new(["constant"]);
        StatisticsCollector collector = new();

        // All-constant column → should trigger ConstantFeature insight.
        for (int i = 0; i < 100; i++)
        {
            collector.AddRow(MakeRow(columnLookup, DataValue.FromFloat32(42.0f)), _arena);
        }

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["constant"] = DataKind.Float32 };

        QueryResultsManifest manifest = ManifestBuilder.Build(
            stats, kinds, 100, insightThresholds: new InsightThresholds());

        Assert.NotNull(manifest.Insights);
        Assert.NotEmpty(manifest.Insights);
        Assert.Contains(manifest.Insights, i => i.Kind == InsightKind.ConstantFeature);
    }

    [Fact]
    public void Build_WithInsightThresholds_GeneratesRecommendedQuery()
    {
        ColumnLookup columnLookup = new(["constant", "normal"]);
        StatisticsCollector collector = new();

        for (int i = 0; i < 100; i++)
        {
            collector.AddRow(MakeRow(columnLookup, DataValue.FromFloat32(42.0f), DataValue.FromFloat32(i * 1.0f)), _arena);
        }

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new()
        {
            ["constant"] = DataKind.Float32,
            ["normal"] = DataKind.Float32
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
        ColumnLookup columnLookup = new(["value"]);
        StatisticsCollector collector = new();

        for (int i = 0; i < 10; i++)
        {
            collector.AddRow(MakeRow(columnLookup, DataValue.FromFloat32(42.0f)), _arena);
        }

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["value"] = DataKind.Float32 };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 10);

        Assert.Null(manifest.Insights);
        Assert.Null(manifest.RecommendedQuery);
        Assert.Null(manifest.FullSuggestedQuery);
        Assert.Null(manifest.QueryAnnotations);
    }

    [Fact]
    public void Build_WithInsightThresholds_PopulatesQueryAnnotations()
    {
        ColumnLookup columnLookup = new(["constant"]);
        StatisticsCollector collector = new();

        for (int i = 0; i < 100; i++)
        {
            collector.AddRow(MakeRow(columnLookup, DataValue.FromFloat32(42.0f)), _arena);
        }

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["constant"] = DataKind.Float32 };

        QueryResultsManifest manifest = ManifestBuilder.Build(
            stats, kinds, 100, insightThresholds: new InsightThresholds());

        Assert.NotNull(manifest.QueryAnnotations);
        Assert.NotEmpty(manifest.QueryAnnotations);
    }
}
