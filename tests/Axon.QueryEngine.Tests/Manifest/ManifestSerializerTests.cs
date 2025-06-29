namespace Axon.QueryEngine.Tests.Manifest;

using System.Text.Json;
using Axon.QueryEngine.Manifest;
using Axon.QueryEngine.Model;
using Axon.QueryEngine.Statistics;
using Axon.QueryEngine.Statistics.Accumulators;

public sealed class ManifestSerializerTests
{
    [Fact]
    public void Serialize_NumericManifest_ProducesValidJson()
    {
        QueryResultsManifest manifest = BuildNumericManifest();
        string json = ManifestSerializer.Serialize(manifest);

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
        string json = ManifestSerializer.Serialize(manifest);

        Assert.Contains("\"type\": \"string\"", json);
        Assert.Contains("\"minLength\":", json);
        Assert.Contains("\"maxLength\":", json);
    }

    [Fact]
    public void Serialize_ImageManifest_ContainsDimensionFields()
    {
        QueryResultsManifest manifest = BuildImageManifest();
        string json = ManifestSerializer.Serialize(manifest);

        Assert.Contains("\"type\": \"image\"", json);
        Assert.Contains("\"minWidth\":", json);
        Assert.Contains("\"maxWidth\":", json);
        Assert.Contains("\"channelCounts\":", json);
        Assert.Contains("\"fileSizeStats\":", json);
    }

    [Fact]
    public void Serialize_VectorManifest_ContainsElementStats()
    {
        QueryResultsManifest manifest = BuildVectorManifest();
        string json = ManifestSerializer.Serialize(manifest);

        Assert.Contains("\"type\": \"vector\"", json);
        Assert.Contains("\"minLength\":", json);
        Assert.Contains("\"elementStats\":", json);
    }

    [Fact]
    public void Serialize_TemporalManifest_ContainsDates()
    {
        QueryResultsManifest manifest = BuildTemporalManifest();
        string json = ManifestSerializer.Serialize(manifest);

        Assert.Contains("\"type\": \"temporal\"", json);
        Assert.Contains("\"earliest\":", json);
        Assert.Contains("\"latest\":", json);
    }

    [Fact]
    public void Serialize_CamelCasePropertyNames()
    {
        QueryResultsManifest manifest = BuildNumericManifest();
        string json = ManifestSerializer.Serialize(manifest);

        Assert.Contains("\"rowCount\":", json);
        Assert.Contains("\"generatedAtUtc\":", json);
        Assert.Contains("\"estimatedDistinctCount\":", json);
        Assert.Contains("\"isConstant\":", json);
        Assert.Contains("\"topKValues\":", json);
        Assert.Contains("\"standardDeviation\":", json);
    }

    [Fact]
    public void Serialize_KindAsString()
    {
        QueryResultsManifest manifest = BuildNumericManifest();
        string json = ManifestSerializer.Serialize(manifest);

        Assert.Contains("\"kind\": \"Scalar\"", json);
    }

    [Fact]
    public void Serialize_Indented()
    {
        QueryResultsManifest manifest = BuildNumericManifest();
        string json = ManifestSerializer.Serialize(manifest);

        Assert.Contains("\n", json);
        Assert.Contains("  ", json);
    }

    [Fact]
    public void Serialize_MultipleFeatureTypes_AllDiscriminated()
    {
        StatisticsCollector collector = new();
        Row row = new(
            ["id", "name"],
            [DataValue.FromScalar(1.0f), DataValue.FromString("test")]);
        collector.AddRow(row);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new()
        {
            ["id"] = DataKind.Scalar,
            ["name"] = DataKind.String
        };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 1);
        string json = ManifestSerializer.Serialize(manifest);

        Assert.Contains("\"type\": \"numeric\"", json);
        Assert.Contains("\"type\": \"string\"", json);
    }

    [Fact]
    public void SerializeToUtf8Bytes_ProducesNonEmptyOutput()
    {
        QueryResultsManifest manifest = BuildNumericManifest();
        byte[] bytes = ManifestSerializer.SerializeToUtf8Bytes(manifest);

        Assert.True(bytes.Length > 0);
    }

    [Fact]
    public void Serialize_TopKValues_IncludesFrequencies()
    {
        StatisticsCollector collector = new();

        for (int i = 0; i < 10; i++)
        {
            collector.AddRow(new Row(["cat"], [DataValue.FromString("A")]));
        }

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["cat"] = DataKind.String };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 10);
        string json = ManifestSerializer.Serialize(manifest);

        Assert.Contains("\"frequency\": 10", json);
    }

    private static QueryResultsManifest BuildNumericManifest()
    {
        StatisticsCollector collector = new();
        collector.AddRow(new Row(["value"], [DataValue.FromScalar(1.0f)]));
        collector.AddRow(new Row(["value"], [DataValue.FromScalar(2.0f)]));
        collector.AddRow(new Row(["value"], [DataValue.FromScalar(3.0f)]));

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["value"] = DataKind.Scalar };

        return ManifestBuilder.Build(stats, kinds, 3);
    }

    private static QueryResultsManifest BuildStringManifest()
    {
        StatisticsCollector collector = new();
        collector.AddRow(new Row(["name"], [DataValue.FromString("Alice")]));

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["name"] = DataKind.String };

        return ManifestBuilder.Build(stats, kinds, 1);
    }

    private static QueryResultsManifest BuildImageManifest()
    {
        StatisticsCollector collector = new();
        byte[] jpeg = new byte[20];
        jpeg[0] = 0xFF;
        jpeg[1] = 0xD8;
        jpeg[2] = 0xFF;
        jpeg[3] = 0xC0;
        jpeg[4] = 0x00;
        jpeg[5] = 0x0B;
        jpeg[6] = 0x08;
        jpeg[7] = 0x01; jpeg[8] = 0xE0; // height = 480
        jpeg[9] = 0x02; jpeg[10] = 0x80; // width = 640
        jpeg[11] = 0x03; // 3 channels

        collector.AddRow(new Row(["photo"], [DataValue.FromImage(jpeg)]));

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["photo"] = DataKind.Image };

        return ManifestBuilder.Build(stats, kinds, 1);
    }

    private static QueryResultsManifest BuildVectorManifest()
    {
        StatisticsCollector collector = new();
        collector.AddRow(new Row(["embedding"], [DataValue.FromVector([1.0f, 2.0f, 3.0f])]));

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["embedding"] = DataKind.Vector };

        return ManifestBuilder.Build(stats, kinds, 1);
    }

    private static QueryResultsManifest BuildTemporalManifest()
    {
        StatisticsCollector collector = new();
        collector.AddRow(new Row(["date"], [DataValue.FromDate(new DateOnly(2024, 6, 15))]));

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["date"] = DataKind.Date };

        return ManifestBuilder.Build(stats, kinds, 1);
    }

    [Fact]
    public void Serialize_NullRatio_PresentInJson()
    {
        StatisticsCollector collector = new();
        collector.AddRow(new Row(["x"], [DataValue.FromScalar(1.0f)]));
        collector.AddRow(new Row(["x"], [DataValue.Null(DataKind.Scalar)]));

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["x"] = DataKind.Scalar };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 2);
        string json = ManifestSerializer.Serialize(manifest);

        Assert.Contains("\"nullRatio\": 0.5", json);
    }

    [Fact]
    public void Serialize_MissingRuns_PresentInJson()
    {
        StatisticsCollector collector = new();
        collector.AddRow(new Row(["x"], [DataValue.Null(DataKind.Scalar)]));
        collector.AddRow(new Row(["x"], [DataValue.FromScalar(1.0f)]));
        collector.AddRow(new Row(["x"], [DataValue.Null(DataKind.Scalar)]));

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["x"] = DataKind.Scalar };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 3);
        string json = ManifestSerializer.Serialize(manifest);

        Assert.Contains("\"missingRuns\": 2", json);
    }
}
