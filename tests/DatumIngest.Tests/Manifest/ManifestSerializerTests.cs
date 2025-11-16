namespace DatumIngest.Tests.Manifest;

using System.Text.Json;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Statistics;
using DatumIngest.Statistics.Accumulators;

public sealed class ManifestSerializerTests : ServiceTestBase
{
    private readonly Arena _arena = new();

    public override void Dispose()
    {
        _arena.Dispose();
        base.Dispose();
    }
    [Fact]
    public void Serialize_NumericManifest_ProducesValidJson()
    {
        QueryResultsManifest manifest = BuildNumericManifest();
        string json = ManifestSerializer.Serialize("test", manifest);

        Assert.Contains("\"type\": \"numeric\"", json);
        Assert.Contains("\"rowCount\": 3", json);
        Assert.Contains("\"min\":", json);
        Assert.Contains("\"max\":", json);
        Assert.Contains("\"mean\":", json);
        Assert.Contains("\"histogram\":", json);
    }

    [Fact]
    public void Serialize_StringManifest_ContainsLengthFields()
    {
        QueryResultsManifest manifest = BuildStringManifest();
        string json = ManifestSerializer.Serialize("test", manifest);

        Assert.Contains("\"type\": \"string\"", json);
        Assert.Contains("\"minLength\":", json);
        Assert.Contains("\"maxLength\":", json);
    }

    [Fact]
    public void Serialize_VectorManifest_ContainsElementStats()
    {
        QueryResultsManifest manifest = BuildVectorManifest();
        string json = ManifestSerializer.Serialize("test", manifest);

        Assert.Contains("\"type\": \"vector\"", json);
        Assert.Contains("\"minLength\":", json);
        Assert.Contains("\"elementStats\":", json);
    }

    [Fact]
    public void Serialize_TemporalManifest_ContainsDates()
    {
        QueryResultsManifest manifest = BuildTemporalManifest();
        string json = ManifestSerializer.Serialize("test", manifest);

        Assert.Contains("\"type\": \"temporal\"", json);
        Assert.Contains("\"earliest\":", json);
        Assert.Contains("\"latest\":", json);
    }

    [Fact]
    public void Serialize_CamelCasePropertyNames()
    {
        QueryResultsManifest manifest = BuildNumericManifest();
        string json = ManifestSerializer.Serialize("test", manifest);

        Assert.Contains("\"rowCount\":", json);
        Assert.Contains("\"generatedAtUtc\":", json);
        Assert.Contains("\"estimatedDistinctCount\":", json);
        Assert.Contains("\"validCount\":", json);
        Assert.Contains("\"isConstant\":", json);
        Assert.Contains("\"isNearConstant\":", json);
        Assert.Contains("\"topKValues\":", json);
        Assert.Contains("\"standardDeviation\":", json);
    }

    [Fact]
    public void Serialize_KindAsString()
    {
        QueryResultsManifest manifest = BuildNumericManifest();
        string json = ManifestSerializer.Serialize("test", manifest);

        Assert.Contains("\"kind\": \"Float32\"", json);
    }

    [Fact]
    public void Serialize_Indented()
    {
        QueryResultsManifest manifest = BuildNumericManifest();
        string json = ManifestSerializer.Serialize("test", manifest);

        Assert.Contains("\n", json);
        Assert.Contains("  ", json);
    }

    [Fact]
    public void Serialize_MultipleFeatureTypes_AllDiscriminated()
    {
        ColumnLookup columnLookup = new (["id", "name"]);
        StatisticsCollector collector = new();
        Row row = MakeRow(
            columnLookup,
            DataValue.FromFloat32(1.0f),
            DataValue.FromString("test", _arena));
        collector.AddRow(row, _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new()
        {
            ["id"] = DataKind.Float32,
            ["name"] = DataKind.String
        };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 1);
        string json = ManifestSerializer.Serialize("test", manifest);

        Assert.Contains("\"type\": \"numeric\"", json);
        Assert.Contains("\"type\": \"string\"", json);
    }

    [Fact]
    public void SerializeToUtf8Bytes_ProducesNonEmptyOutput()
    {
        QueryResultsManifest manifest = BuildNumericManifest();
        byte[] bytes = ManifestSerializer.SerializeToUtf8Bytes("test", manifest);

        Assert.True(bytes.Length > 0);
    }

    [Fact]
    public void Serialize_TopKValues_IncludesFrequencies()
    {
        ColumnLookup columnLookup = new (["cat"]);
        StatisticsCollector collector = new();

        for (int i = 0; i < 10; i++)
        {
            collector.AddRow(MakeRow(columnLookup, DataValue.FromString("A", _arena)), _arena);
        }

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["cat"] = DataKind.String };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 10);
        string json = ManifestSerializer.Serialize("test", manifest);

        Assert.Contains("\"frequency\": 10", json);
    }

    private QueryResultsManifest BuildNumericManifest()
    {
        ColumnLookup columnLookup = new (["value"]);
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow(columnLookup, DataValue.FromFloat32(1.0f)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromFloat32(2.0f)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromFloat32(3.0f)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["value"] = DataKind.Float32 };

        return ManifestBuilder.Build(stats, kinds, 3);
    }

    private QueryResultsManifest BuildStringManifest()
    {
        ColumnLookup columnLookup = new (["name"]);
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow(columnLookup, DataValue.FromString("Alice", _arena)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["name"] = DataKind.String };

        return ManifestBuilder.Build(stats, kinds, 1);
    }

    private QueryResultsManifest BuildVectorManifest()
    {
        ColumnLookup columnLookup = new (["embedding"]);
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow(columnLookup, DataValue.FromVector([1.0f, 2.0f, 3.0f], _arena)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["embedding"] = DataKind.Vector };

        return ManifestBuilder.Build(stats, kinds, 1);
    }

    private QueryResultsManifest BuildTemporalManifest()
    {
        ColumnLookup columnLookup = new (["date"]);
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow(columnLookup, DataValue.FromDate(new DateOnly(2024, 6, 15))), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["date"] = DataKind.Date };

        return ManifestBuilder.Build(stats, kinds, 1);
    }

    [Fact]
    public void Serialize_NullRatio_PresentInJson()
    {
        ColumnLookup columnLookup = new (["x"]);
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow(columnLookup, DataValue.FromFloat32(1.0f)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.Null(DataKind.Float32)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["x"] = DataKind.Float32 };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 2);
        string json = ManifestSerializer.Serialize("test", manifest);

        Assert.Contains("\"nullRatio\": 0.5", json);
    }

    [Fact]
    public void Serialize_MissingRuns_PresentInJson()
    {
        ColumnLookup columnLookup = new (["x"]);
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow(columnLookup, DataValue.Null(DataKind.Float32)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromFloat32(1.0f)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.Null(DataKind.Float32)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["x"] = DataKind.Float32 };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 3);
        string json = ManifestSerializer.Serialize("test", manifest);

        Assert.Contains("\"missingRuns\": 2", json);
    }

    [Fact]
    public void Serialize_DominantValueRatio_PresentInJson()
    {
        ColumnLookup columnLookup = new (["x"]);
        StatisticsCollector collector = new();

        for (int i = 0; i < 9; i++)
        {
            collector.AddRow(MakeRow(columnLookup, DataValue.FromString("same", _arena)), _arena);
        }

        collector.AddRow(MakeRow(columnLookup, DataValue.FromString("other", _arena)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["x"] = DataKind.String };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 10);
        string json = ManifestSerializer.Serialize("test", manifest);

        Assert.Contains("\"dominantValueRatio\": 0.9", json);
    }

    [Fact]
    public void Serialize_NumericManifest_ContainsIqrOutlierFields()
    {
        QueryResultsManifest manifest = BuildNumericManifest();
        string json = ManifestSerializer.Serialize("test", manifest);

        Assert.Contains("\"iqr\":", json);
        Assert.Contains("\"lowerFence\":", json);
        Assert.Contains("\"upperFence\":", json);
        Assert.Contains("\"outlierCount\":", json);
        Assert.Contains("\"outlierRatio\":", json);
    }

    [Fact]
    public void Serialize_BooleanManifest_ContainsTrueRatio()
    {
        ColumnLookup columnLookup = new (["flag"]);
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow(columnLookup, DataValue.FromBoolean(true)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromBoolean(false)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromBoolean(true)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["flag"] = DataKind.Boolean };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 3);
        string json = ManifestSerializer.Serialize("test", manifest);

        Assert.Contains("\"type\": \"boolean\"", json);
        Assert.Contains("\"trueRatio\":", json);
    }

    [Fact]
    public void Deserialize_BooleanManifest_RoundTrips()
    {
        ColumnLookup columnLookup = new (["flag"]);
        StatisticsCollector collector = new();
        collector.AddRow(MakeRow(columnLookup, DataValue.FromBoolean(true)), _arena);
        collector.AddRow(MakeRow(columnLookup, DataValue.FromBoolean(false)), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["flag"] = DataKind.Boolean };

        QueryResultsManifest original = ManifestBuilder.Build(stats, kinds, 2);
        string json = ManifestSerializer.Serialize("test", original);
        SourceManifest? deserialized = ManifestSerializer.Deserialize(json);

        Assert.NotNull(deserialized);
        QueryResultsManifest roundTripped = deserialized.Tables["test"];
        BooleanFeatureManifest booleanFeature = Assert.IsType<BooleanFeatureManifest>(roundTripped.Features[0]);
        Assert.Equal("flag", booleanFeature.Name);
        Assert.Equal(DataKind.Boolean, booleanFeature.Kind);
        Assert.Equal(0.5, booleanFeature.TrueRatio);
    }

}
