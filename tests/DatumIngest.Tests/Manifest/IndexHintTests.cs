namespace Heliosoph.DatumV.Tests.Manifest;

using Heliosoph.DatumV.Indexing;
using Heliosoph.DatumV.Indexing.Bitmap;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Manifest.Insights;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Statistics;

/// <summary>
/// Tests for <see cref="ColumnIndexHint"/> generation in <see cref="ManifestBuilder"/>
/// and consumption in <see cref="SourceIndexBuilder"/>.
/// </summary>
public sealed class IndexHintTests : ServiceTestBase
{
    private readonly Arena _arena;

    public IndexHintTests()
    {
        _arena = CreateArena();
    }

    public override void Dispose()
    {
        _arena.Dispose();
        base.Dispose();
    }
    // ───────────────── ManifestBuilder hint generation ─────────────────

    [Fact]
    public void Build_BooleanColumn_GeneratesBitmapHint()
    {
        StatisticsCollector collector = new();

        ColumnLookup columnLookup = new (["flag"]);

        collector.AddRow(MakeRow(columnLookup, DataValue.FromBoolean(true)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromBoolean(false)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromBoolean(true)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> statistics = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["flag"] = DataKind.Boolean };

        QueryResultsManifest manifest = ManifestBuilder.Build(statistics, kinds, 3);

        Assert.NotNull(manifest.IndexHints);
        ColumnIndexHint hint = Assert.Single(manifest.IndexHints);
        Assert.Equal("flag", hint.ColumnName);
        Assert.Equal(IndexHintType.Bitmap, hint.PreferredType);
    }

    [Fact]
    public void Build_LowCardinalityString_GeneratesBitmapHint()
    {
        ColumnLookup columnLookup = new (["color"]);
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow(columnLookup, DataValue.FromString("red", _arena)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromString("blue", _arena)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromString("red", _arena)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> statistics = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["color"] = DataKind.String };

        QueryResultsManifest manifest = ManifestBuilder.Build(statistics, kinds, 3);

        Assert.NotNull(manifest.IndexHints);
        ColumnIndexHint hint = Assert.Single(manifest.IndexHints);
        Assert.Equal("color", hint.ColumnName);
        Assert.Equal(IndexHintType.Bitmap, hint.PreferredType);
    }

    [Fact]
    public void Build_MediumCardinalityNumeric_GeneratesSortedHint()
    {
        // Create enough distinct values to exceed bitmap threshold but stay below B+Tree.
        StatisticsCollector collector = new();
        int distinctCount = IndexConstants.BitmapAutoThreshold + 100;

        ColumnLookup columnLookup = new (["id"]);
        for (int i = 0; i < distinctCount; i++)
        {
            collector.AddRow(MakeRow(columnLookup, DataValue.FromFloat32(i)), _arena);
        }

        IReadOnlyDictionary<string, ColumnStatistics> statistics = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["id"] = DataKind.Float32 };

        QueryResultsManifest manifest = ManifestBuilder.Build(statistics, kinds, distinctCount);

        Assert.NotNull(manifest.IndexHints);
        ColumnIndexHint hint = Assert.Single(manifest.IndexHints);
        Assert.Equal("id", hint.ColumnName);
        Assert.Equal(IndexHintType.Sorted, hint.PreferredType);
    }

    [Fact]
    public void Build_VectorColumn_GeneratesNoHint()
    {
        ColumnLookup columnLookup = new (["embedding"]);
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow(columnLookup, DataValue.FromArenaArray<float>([1.0f, 2.0f], DataKind.Float32, _arena)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromArenaArray<float>([3.0f, 4.0f], DataKind.Float32, _arena)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> statistics = collector.GetStatistics();
        Dictionary<string, ColumnInfo> kinds = new()
        {
            ["embedding"] = new ColumnInfo("embedding", DataKind.Float32, nullable: true) { IsArray = true },
        };

        QueryResultsManifest manifest = ManifestBuilder.Build(statistics, kinds, 2);

        Assert.Null(manifest.IndexHints);
    }

    [Fact]
    public void Build_MixedColumns_GeneratesCorrectHintsPerColumn()
    {
        ColumnLookup columnLookup = new (["status", "count"]);
        StatisticsCollector collector = new();

        // Low-cardinality string → Bitmap.
        collector.AddRow(
            MakeRow(
                columnLookup,
                DataValue.FromString("active", _arena),
                DataValue.FromFloat32(1.0f)
            ), _arena);
        collector.AddRow(
            MakeRow(
                columnLookup,
                DataValue.FromString("inactive", _arena),
                DataValue.FromFloat32(2.0f)
            ), _arena);
        collector.AddRow(
            MakeRow(
                columnLookup,
                DataValue.FromString("active", _arena),
                DataValue.FromFloat32(3.0f)
            ), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> statistics = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new()
        {
            ["status"] = DataKind.String,
            ["count"] = DataKind.Float32
        };

        QueryResultsManifest manifest = ManifestBuilder.Build(statistics, kinds, 3);

        Assert.NotNull(manifest.IndexHints);
        Assert.Equal(2, manifest.IndexHints.Count);

        ColumnIndexHint statusHint = manifest.IndexHints.First(h => h.ColumnName == "status");
        ColumnIndexHint countHint = manifest.IndexHints.First(h => h.ColumnName == "count");

        Assert.Equal(IndexHintType.Bitmap, statusHint.PreferredType);
        Assert.Equal(IndexHintType.Bitmap, countHint.PreferredType);
    }

    [Fact]
    public void Build_WithInsights_PreservesIndexHints()
    {
        ColumnLookup columnLookup = new (["flag"]);
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow(columnLookup, DataValue.FromBoolean(true)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromBoolean(false)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> statistics = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["flag"] = DataKind.Boolean };

        // Pass insight thresholds to trigger the insight code path.
        QueryResultsManifest manifest = ManifestBuilder.Build(
            statistics, kinds, 2, insightThresholds: new InsightThresholds());

        Assert.NotNull(manifest.IndexHints);
        ColumnIndexHint hint = Assert.Single(manifest.IndexHints);
        Assert.Equal(IndexHintType.Bitmap, hint.PreferredType);
    }

    // ───────────────── SourceIndexBuilder consumption ─────────────────

    [Fact]
    public void CreateBitmapAccumulators_SelectsAutoIndexableColumns()
    {
        Schema schema = new([
            new ColumnInfo("value", DataKind.Float32, false),
            new ColumnInfo("embedding", DataKind.Float32, false) { IsArray = true },
        ]);

        Dictionary<string, BitmapChunkAccumulator>? accumulators =
            SourceIndexBuilder.CreateBitmapAccumulators(schema);

        Assert.NotNull(accumulators);
        Assert.True(accumulators.ContainsKey("value"));
        Assert.False(accumulators.ContainsKey("embedding"));
    }
}
