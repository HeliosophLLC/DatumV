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
public sealed class IndexHintTests
{
    // ───────────────── ManifestBuilder hint generation ─────────────────

    [Fact]
    public void Build_BooleanColumn_GeneratesBitmapHint()
    {
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow("flag", DataValue.FromBoolean(true)));
        collector.AddRow(MakeRow("flag", DataValue.FromBoolean(false)));
        collector.AddRow(MakeRow("flag", DataValue.FromBoolean(true)));

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
        collector.AddRow(MakeRow("color", DataValue.FromString("red")));
        collector.AddRow(MakeRow("color", DataValue.FromString("blue")));
        collector.AddRow(MakeRow("color", DataValue.FromString("red")));

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
            collector.AddRow(MakeRow("id", DataValue.FromFloat32(i)));
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
        collector.AddRow(MakeRow("embedding", DataValue.FromVector([1.0f, 2.0f])));
        collector.AddRow(MakeRow("embedding", DataValue.FromVector([3.0f, 4.0f])));

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
        collector.AddRow(MakeRow("status", DataValue.FromString("active"),
                                 "count", DataValue.FromFloat32(1.0f)));
        collector.AddRow(MakeRow("status", DataValue.FromString("inactive"),
                                 "count", DataValue.FromFloat32(2.0f)));
        collector.AddRow(MakeRow("status", DataValue.FromString("active"),
                                 "count", DataValue.FromFloat32(3.0f)));

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
        collector.AddRow(MakeRow("flag", DataValue.FromBoolean(true)));
        collector.AddRow(MakeRow("flag", DataValue.FromBoolean(false)));

        IReadOnlyDictionary<string, ColumnStatistics> statistics = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["flag"] = DataKind.Boolean };

        // Pass insight thresholds to trigger the insight code path.
        QueryResultsManifest manifest = ManifestBuilder.Build(
            statistics, kinds, 2, insightThresholds: new InsightThresholds());

        Assert.NotNull(manifest.IndexHints);
        ColumnIndexHint hint = Assert.Single(manifest.IndexHints);
        Assert.Equal(IndexHintType.Bitmap, hint.PreferredType);
    }

    // ───────────────── SourceIndexBuilder hint consumption ─────────────────

    [Fact]
    public void CreateBitmapAccumulators_BitmapHint_IncludesColumn()
    {
        Schema schema = new([
            new ColumnInfo("value", DataKind.Float32, false),
            new ColumnInfo("label", DataKind.String, false),
        ]);

        // Only hint "value" as bitmap — "label" would be auto-included anyway.
        List<ColumnIndexHint> hints =
        [
            new ColumnIndexHint("value", IndexHintType.Bitmap),
            new ColumnIndexHint("label", IndexHintType.None),
        ];

        Dictionary<string, BitmapChunkAccumulator>? accumulators =
            SourceIndexBuilder.CreateBitmapAccumulators(schema, hints);

        Assert.NotNull(accumulators);
        Assert.True(accumulators.ContainsKey("value"));
        Assert.False(accumulators.ContainsKey("label"));
    }

    [Fact]
    public void CreateBitmapAccumulators_SortedHint_ExcludesColumn()
    {
        Schema schema = new([
            new ColumnInfo("id", DataKind.Float32, false),
        ]);

        List<ColumnIndexHint> hints =
        [
            new ColumnIndexHint("id", IndexHintType.Sorted),
        ];

        Dictionary<string, BitmapChunkAccumulator>? accumulators =
            SourceIndexBuilder.CreateBitmapAccumulators(schema, hints);

        // "id" is auto-indexable by kind but the Sorted hint excludes it from bitmaps.
        Assert.Null(accumulators);
    }

    [Fact]
    public void CreateBitmapAccumulators_NoHints_FallsBackToAutoDetection()
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

    [Fact]
    public void CreateBitmapAccumulators_BitmapHintOnNonAutoKind_IncludesColumn()
    {
        // A Vector column isn't normally auto-indexable, but a Bitmap hint forces inclusion.
        Schema schema = new([
            new ColumnInfo("embedding", DataKind.Vector, false),
        ]);

        List<ColumnIndexHint> hints =
        [
            new ColumnIndexHint("embedding", IndexHintType.Bitmap),
        ];

        Dictionary<string, BitmapChunkAccumulator>? accumulators =
            SourceIndexBuilder.CreateBitmapAccumulators(schema, hints);

        Assert.NotNull(accumulators);
        Assert.True(accumulators.ContainsKey("embedding"));
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
