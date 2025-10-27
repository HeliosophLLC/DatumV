namespace DatumIngest.Tests.Manifest;

using DatumIngest.Indexing;
using DatumIngest.Indexing.Bitmap;
using DatumIngest.Manifest;
using DatumIngest.Manifest.Insights;
using DatumIngest.Model;
using DatumIngest.Statistics;
using DatumIngest.Statistics.Accumulators;

/// <summary>
/// Tests for <see cref="ColumnIndexHint"/> generation in <see cref="ManifestBuilder"/>
/// and consumption in <see cref="SourceIndexBuilder"/>.
/// </summary>
public sealed class IndexHintTests : ServiceTestBase
{
    private readonly Arena _arena = new();

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
        collector.AddRow(MakeRow("flag", DataValue.FromBoolean(true)), _arena);
        collector.AddRow(MakeRow("flag", DataValue.FromBoolean(false)), _arena);
        collector.AddRow(MakeRow("flag", DataValue.FromBoolean(true)), _arena);

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
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow("color", DataValue.FromString("red", _arena)), _arena);
        collector.AddRow(MakeRow("color", DataValue.FromString("blue", _arena)), _arena);
        collector.AddRow(MakeRow("color", DataValue.FromString("red", _arena)), _arena);

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

        for (int i = 0; i < distinctCount; i++)
        {
            collector.AddRow(MakeRow("id", DataValue.FromFloat32(i)), _arena);
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
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow("embedding", DataValue.FromVector([1.0f, 2.0f], _arena)), _arena);
        collector.AddRow(MakeRow("embedding", DataValue.FromVector([3.0f, 4.0f], _arena)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> statistics = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["embedding"] = DataKind.Vector };

        QueryResultsManifest manifest = ManifestBuilder.Build(statistics, kinds, 2);

        Assert.Null(manifest.IndexHints);
    }

    [Fact]
    public void Build_MixedColumns_GeneratesCorrectHintsPerColumn()
    {
        StatisticsCollector collector = new();

        // Low-cardinality string → Bitmap.
        collector.AddRow(MakeRow("status", DataValue.FromString("active", _arena),
                                 "count", DataValue.FromFloat32(1.0f)), _arena);
        collector.AddRow(MakeRow("status", DataValue.FromString("inactive", _arena),
                                 "count", DataValue.FromFloat32(2.0f)), _arena);
        collector.AddRow(MakeRow("status", DataValue.FromString("active", _arena),
                                 "count", DataValue.FromFloat32(3.0f)), _arena);

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
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow("flag", DataValue.FromBoolean(true)), _arena);
        collector.AddRow(MakeRow("flag", DataValue.FromBoolean(false)), _arena);

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
            new ColumnInfo("embedding", DataKind.Vector, false),
        ]);

        Dictionary<string, BitmapChunkAccumulator>? accumulators =
            SourceIndexBuilder.CreateBitmapAccumulators(schema);

        Assert.NotNull(accumulators);
        Assert.True(accumulators.ContainsKey("value"));
        Assert.False(accumulators.ContainsKey("embedding"));
    }

    // ───────────────── Helpers ─────────────────

    private static Row MakeRow(string columnName, DataValue value)
    {
        return new Row([columnName], [value]);
    }

    private static Row MakeRow(string column1, DataValue value1, string column2, DataValue value2)
    {
        return new Row([column1, column2], [value1, value2]);
    }
}
