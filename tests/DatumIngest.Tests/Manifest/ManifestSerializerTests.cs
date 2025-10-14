namespace DatumIngest.Tests.Manifest;

using System.Text.Json;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Statistics;
using DatumIngest.Statistics.Accumulators;

public sealed class ManifestSerializerTests : IDisposable
{
    private readonly Arena _arena = new();

    public void Dispose() => _arena.Dispose();
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
    public void Serialize_ImageManifest_ContainsDimensionFields()
    {
        QueryResultsManifest manifest = BuildImageManifest();
        string json = ManifestSerializer.Serialize("test", manifest);

        Assert.Contains("\"type\": \"image\"", json);
        Assert.Contains("\"minWidth\":", json);
        Assert.Contains("\"maxWidth\":", json);
        Assert.Contains("\"channelCounts\":", json);
        Assert.Contains("\"fileSizeStats\":", json);
        Assert.Contains("\"megapixelStats\":", json);
        Assert.Contains("\"aspectRatioStats\":", json);
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
        StatisticsCollector collector = new();
        Row row = new(
            ["id", "name"],
            [DataValue.FromFloat32(1.0f), DataValue.FromString("test", _arena)]);
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
        StatisticsCollector collector = new();

        for (int i = 0; i < 10; i++)
        {
            collector.AddRow(new Row(["cat"], [DataValue.FromString("A", _arena)]), _arena);
        }

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["cat"] = DataKind.String };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 10);
        string json = ManifestSerializer.Serialize("test", manifest);

        Assert.Contains("\"frequency\": 10", json);
    }

    private QueryResultsManifest BuildNumericManifest()
    {
        StatisticsCollector collector = new();
        collector.AddRow(new Row(["value"], [DataValue.FromFloat32(1.0f)]), _arena);
        collector.AddRow(new Row(["value"], [DataValue.FromFloat32(2.0f)]), _arena);
        collector.AddRow(new Row(["value"], [DataValue.FromFloat32(3.0f)]), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["value"] = DataKind.Float32 };

        return ManifestBuilder.Build(stats, kinds, 3);
    }

    private QueryResultsManifest BuildStringManifest()
    {
        StatisticsCollector collector = new();
        collector.AddRow(new Row(["name"], [DataValue.FromString("Alice", _arena)]), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["name"] = DataKind.String };

        return ManifestBuilder.Build(stats, kinds, 1);
    }

    private QueryResultsManifest BuildImageManifest()
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

        collector.AddRow(new Row(["photo"], [DataValue.FromImage(jpeg, _arena)]), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["photo"] = DataKind.Image };

        return ManifestBuilder.Build(stats, kinds, 1);
    }

    private QueryResultsManifest BuildVectorManifest()
    {
        StatisticsCollector collector = new();
        collector.AddRow(new Row(["embedding"], [DataValue.FromVector([1.0f, 2.0f, 3.0f], _arena)]), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["embedding"] = DataKind.Vector };

        return ManifestBuilder.Build(stats, kinds, 1);
    }

    private QueryResultsManifest BuildTemporalManifest()
    {
        StatisticsCollector collector = new();
        collector.AddRow(new Row(["date"], [DataValue.FromDate(new DateOnly(2024, 6, 15))]), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["date"] = DataKind.Date };

        return ManifestBuilder.Build(stats, kinds, 1);
    }

    [Fact]
    public void Serialize_NullRatio_PresentInJson()
    {
        StatisticsCollector collector = new();
        collector.AddRow(new Row(["x"], [DataValue.FromFloat32(1.0f)]), _arena);
        collector.AddRow(new Row(["x"], [DataValue.Null(DataKind.Float32)]), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["x"] = DataKind.Float32 };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 2);
        string json = ManifestSerializer.Serialize("test", manifest);

        Assert.Contains("\"nullRatio\": 0.5", json);
    }

    [Fact]
    public void Serialize_MissingRuns_PresentInJson()
    {
        StatisticsCollector collector = new();
        collector.AddRow(new Row(["x"], [DataValue.Null(DataKind.Float32)]), _arena);
        collector.AddRow(new Row(["x"], [DataValue.FromFloat32(1.0f)]), _arena);
        collector.AddRow(new Row(["x"], [DataValue.Null(DataKind.Float32)]), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["x"] = DataKind.Float32 };

        QueryResultsManifest manifest = ManifestBuilder.Build(stats, kinds, 3);
        string json = ManifestSerializer.Serialize("test", manifest);

        Assert.Contains("\"missingRuns\": 2", json);
    }

    [Fact]
    public void Serialize_DominantValueRatio_PresentInJson()
    {
        StatisticsCollector collector = new();

        for (int i = 0; i < 9; i++)
        {
            collector.AddRow(new Row(["x"], [DataValue.FromString("same", _arena)]), _arena);
        }

        collector.AddRow(new Row(["x"], [DataValue.FromString("other", _arena)]), _arena);

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
        StatisticsCollector collector = new();
        collector.AddRow(new Row(["flag"], [DataValue.FromBoolean(true)]), _arena);
        collector.AddRow(new Row(["flag"], [DataValue.FromBoolean(false)]), _arena);
        collector.AddRow(new Row(["flag"], [DataValue.FromBoolean(true)]), _arena);

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
        StatisticsCollector collector = new();
        collector.AddRow(new Row(["flag"], [DataValue.FromBoolean(true)]), _arena);
        collector.AddRow(new Row(["flag"], [DataValue.FromBoolean(false)]), _arena);

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

    [Fact]
    public void SerializeVocabulary_RoundTrips()
    {
        SourceVocabularySet original = new()
        {
            Tables = new Dictionary<string, TableVocabularySet>
            {
                ["orders"] = new TableVocabularySet
                {
                    Columns = new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["order_id"] = new[] { "001", "002", "003" },
                        ["customer_id"] = new[] { "abc", "def" }
                    }
                }
            }
        };

        string json = ManifestSerializer.SerializeVocabulary(original);
        SourceVocabularySet? deserialized = ManifestSerializer.DeserializeVocabulary(json);

        Assert.NotNull(deserialized);
        Assert.Single(deserialized.Tables);
        Assert.True(deserialized.Tables.ContainsKey("orders"));

        TableVocabularySet table = deserialized.Tables["orders"];
        Assert.Equal(2, table.Columns.Count);
        Assert.Equal(new[] { "001", "002", "003" }, table.Columns["order_id"]);
        Assert.Equal(new[] { "abc", "def" }, table.Columns["customer_id"]);
    }

    [Fact]
    public void SerializeVocabulary_MultipleTables_RoundTrips()
    {
        SourceVocabularySet original = new()
        {
            Tables = new Dictionary<string, TableVocabularySet>
            {
                ["orders"] = new TableVocabularySet
                {
                    Columns = new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["order_id"] = new[] { "A", "B" }
                    }
                },
                ["customers"] = new TableVocabularySet
                {
                    Columns = new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["customer_id"] = new[] { "X", "Y", "Z" }
                    }
                }
            }
        };

        string json = ManifestSerializer.SerializeVocabulary(original);
        SourceVocabularySet? deserialized = ManifestSerializer.DeserializeVocabulary(json);

        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized.Tables.Count);
        Assert.Equal(new[] { "A", "B" }, deserialized.Tables["orders"].Columns["order_id"]);
        Assert.Equal(new[] { "X", "Y", "Z" }, deserialized.Tables["customers"].Columns["customer_id"]);
    }

    [Fact]
    public void ExtractFrom_ManifestWithVocabularies_ExtractsCorrectly()
    {
        QueryResultsManifest manifest = BuildIdentifierManifest("id_col", "v1", "v2", "v3");
        manifest.Features[0].Vocabulary = new ColumnVocabulary { Values = new[] { "v1", "v2", "v3" } };

        SourceManifest sourceManifest = SourceManifest.Create("test_table", manifest);
        SourceVocabularySet? vocabularySet = SourceVocabularySet.ExtractFrom(sourceManifest);

        Assert.NotNull(vocabularySet);
        Assert.Single(vocabularySet.Tables);
        Assert.True(vocabularySet.Tables.ContainsKey("test_table"));

        TableVocabularySet tableSet = vocabularySet.Tables["test_table"];
        Assert.Single(tableSet.Columns);
        Assert.Equal(new[] { "v1", "v2", "v3" }, tableSet.Columns["id_col"]);
    }

    [Fact]
    public void ExtractFrom_ManifestWithoutVocabularies_ReturnsNull()
    {
        QueryResultsManifest manifest = BuildNumericManifest();

        SourceManifest sourceManifest = SourceManifest.Create("test", manifest);
        SourceVocabularySet? vocabularySet = SourceVocabularySet.ExtractFrom(sourceManifest);

        Assert.Null(vocabularySet);
    }

    [Fact]
    public void ApplyTo_AttachesVocabulariesToMatchingFeatures()
    {
        QueryResultsManifest manifest = BuildTwoColumnManifest();

        SourceVocabularySet vocabularySet = new()
        {
            Tables = new Dictionary<string, TableVocabularySet>
            {
                ["test"] = new TableVocabularySet
                {
                    Columns = new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["name"] = new[] { "Alice", "Bob", "Charlie" }
                    }
                }
            }
        };

        SourceManifest sourceManifest = SourceManifest.Create("test", manifest);
        vocabularySet.ApplyTo(sourceManifest);

        // "name" column matches the vocabulary entry.
        FeatureManifest nameFeature = manifest.Features.First(f => f.Name == "name");
        Assert.NotNull(nameFeature.Vocabulary);
        Assert.Equal(3, nameFeature.Vocabulary.Count);
        Assert.Equal(new[] { "Alice", "Bob", "Charlie" }, nameFeature.Vocabulary.Values);

        // "value" column has no vocabulary entry — should remain null.
        FeatureManifest valueFeature = manifest.Features.First(f => f.Name == "value");
        Assert.Null(valueFeature.Vocabulary);
    }

    [Fact]
    public void VocabularyRoundTrip_ExtractSerializeDeserializeApply()
    {
        QueryResultsManifest manifest = BuildIdentifierManifest("key", "aaa", "bbb", "ccc");
        manifest.Features[0].Vocabulary = new ColumnVocabulary { Values = new[] { "aaa", "bbb", "ccc" } };

        // Extract → serialize → deserialize.
        SourceManifest sourceManifest = SourceManifest.Create("t1", manifest);
        SourceVocabularySet? extracted = SourceVocabularySet.ExtractFrom(sourceManifest);
        Assert.NotNull(extracted);

        string json = ManifestSerializer.SerializeVocabulary(extracted);
        SourceVocabularySet? deserialized = ManifestSerializer.DeserializeVocabulary(json);
        Assert.NotNull(deserialized);

        // Apply to a fresh manifest (simulating load from disk without vocabulary).
        manifest.Features[0].Vocabulary = null;
        Assert.Null(manifest.Features[0].Vocabulary);

        deserialized.ApplyTo(sourceManifest);
        Assert.NotNull(manifest.Features[0].Vocabulary);
        Assert.Equal(new[] { "aaa", "bbb", "ccc" }, manifest.Features[0].Vocabulary!.Values);
    }

    /// <summary>
    /// Builds a manifest with a single string column containing the given distinct values.
    /// </summary>
    private QueryResultsManifest BuildIdentifierManifest(string columnName, params string[] values)
    {
        StatisticsCollector collector = new();

        foreach (string value in values)
        {
            collector.AddRow(new Row([columnName], [DataValue.FromString(value, _arena)]), _arena);
        }

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { [columnName] = DataKind.String };

        return ManifestBuilder.Build(stats, kinds, values.Length);
    }

    /// <summary>
    /// Builds a manifest with two columns: "name" (string) and "value" (numeric).
    /// </summary>
    private QueryResultsManifest BuildTwoColumnManifest()
    {
        StatisticsCollector collector = new();
        collector.AddRow(new Row(["name", "value"], [DataValue.FromString("Alice", _arena), DataValue.FromFloat32(1.0f)]), _arena);
        collector.AddRow(new Row(["name", "value"], [DataValue.FromString("Bob", _arena), DataValue.FromFloat32(2.0f)]), _arena);
        collector.AddRow(new Row(["name", "value"], [DataValue.FromString("Charlie", _arena), DataValue.FromFloat32(3.0f)]), _arena);

        IReadOnlyDictionary<string, ColumnStatistics> stats = collector.GetStatistics();
        Dictionary<string, DataKind> kinds = new() { ["name"] = DataKind.String, ["value"] = DataKind.Float32 };

        return ManifestBuilder.Build(stats, kinds, 3);
    }
}
