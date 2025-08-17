using System.Text.Json;
using DatumIngest.Model;

namespace DatumIngest.Tests.Model;

/// <summary>
/// Tests for <see cref="SchemaSerializer"/> JSON round-trip serialization
/// and <see cref="SourceSchema"/> construction.
/// </summary>
public sealed class SchemaSerializerTests
{
    [Fact]
    public void RoundTrip_SingleTable_PreservesSchema()
    {
        Schema schema = new([
            new ColumnInfo("id", DataKind.Float32, nullable: false),
            new ColumnInfo("name", DataKind.String, nullable: true),
        ]);

        SourceSchema original = SourceSchema.Create("data", schema);

        string json = SchemaSerializer.Serialize(original);
        SourceSchema? deserialized = SchemaSerializer.Deserialize(json);

        Assert.NotNull(deserialized);
        Assert.Single(deserialized.Tables);
        Assert.True(deserialized.Tables.ContainsKey("data"));

        Schema roundTripped = deserialized.Tables["data"];
        Assert.Equal(2, roundTripped.Columns.Count);
        Assert.Equal("id", roundTripped.Columns[0].Name);
        Assert.Equal(DataKind.Float32, roundTripped.Columns[0].Kind);
        Assert.False(roundTripped.Columns[0].Nullable);
        Assert.Equal("name", roundTripped.Columns[1].Name);
        Assert.Equal(DataKind.String, roundTripped.Columns[1].Kind);
        Assert.True(roundTripped.Columns[1].Nullable);
    }

    [Fact]
    public void RoundTrip_MultipleTables_PreservesAllEntries()
    {
        Dictionary<string, Schema> tables = new()
        {
            ["images"] = new Schema([new ColumnInfo("pixel", DataKind.Float32, nullable: false)]),
            ["labels"] = new Schema([new ColumnInfo("label", DataKind.String, nullable: false)]),
        };

        SourceSchema original = new() { Tables = tables };

        string json = SchemaSerializer.Serialize(original);
        SourceSchema? deserialized = SchemaSerializer.Deserialize(json);

        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized.Tables.Count);
        Assert.True(deserialized.Tables.ContainsKey("images"));
        Assert.True(deserialized.Tables.ContainsKey("labels"));
        Assert.Equal("pixel", deserialized.Tables["images"].Columns[0].Name);
        Assert.Equal("label", deserialized.Tables["labels"].Columns[0].Name);
    }

    [Fact]
    public void Serialize_SingleTableOverload_ProducesValidJson()
    {
        Schema schema = new([new ColumnInfo("value", DataKind.Float32, nullable: false)]);

        string json = SchemaSerializer.Serialize("test", schema);
        SourceSchema? deserialized = SchemaSerializer.Deserialize(json);

        Assert.NotNull(deserialized);
        Assert.Single(deserialized.Tables);
        Assert.True(deserialized.Tables.ContainsKey("test"));
    }

    [Fact]
    public void SerializeToUtf8Bytes_ProducesDeserializableOutput()
    {
        Schema schema = new([new ColumnInfo("col", DataKind.String, nullable: true)]);
        SourceSchema original = SourceSchema.Create("tbl", schema);

        byte[] bytes = SchemaSerializer.SerializeToUtf8Bytes(original);
        string json = System.Text.Encoding.UTF8.GetString(bytes);
        SourceSchema? deserialized = SchemaSerializer.Deserialize(json);

        Assert.NotNull(deserialized);
        Assert.Equal("col", deserialized.Tables["tbl"].Columns[0].Name);
    }

    [Fact]
    public void Deserialize_InvalidJson_Throws()
    {
        Assert.Throws<JsonException>(() => SchemaSerializer.Deserialize("not valid json"));
    }

    [Fact]
    public async Task WriteToFileAsync_CreatesReadableFile()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"schema-test-{Guid.NewGuid():N}.datum-schema");

        try
        {
            Schema schema = new([new ColumnInfo("x", DataKind.Float32, nullable: false)]);
            SourceSchema original = SourceSchema.Create("data", schema);

            await SchemaSerializer.WriteToFileAsync(original, tempPath);

            Assert.True(File.Exists(tempPath));

            string json = await File.ReadAllTextAsync(tempPath);
            SourceSchema? deserialized = SchemaSerializer.Deserialize(json);

            Assert.NotNull(deserialized);
            Assert.Equal("x", deserialized.Tables["data"].Columns[0].Name);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    [Fact]
    public async Task WriteToFileAsync_SingleTableOverload_CreatesReadableFile()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"schema-test-{Guid.NewGuid():N}.datum-schema");

        try
        {
            Schema schema = new([new ColumnInfo("y", DataKind.String, nullable: true)]);

            await SchemaSerializer.WriteToFileAsync("tbl", schema, tempPath);

            string json = await File.ReadAllTextAsync(tempPath);
            SourceSchema? deserialized = SchemaSerializer.Deserialize(json);

            Assert.NotNull(deserialized);
            Assert.Equal("y", deserialized.Tables["tbl"].Columns[0].Name);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    [Fact]
    public void SourceSchema_Create_WrapsInSingleEntryDictionary()
    {
        Schema schema = new([new ColumnInfo("a", DataKind.Float32, nullable: false)]);

        SourceSchema result = SourceSchema.Create("myTable", schema);

        Assert.Single(result.Tables);
        Assert.True(result.Tables.ContainsKey("myTable"));
        Assert.Same(schema, result.Tables["myTable"]);
    }

    [Fact]
    public void RoundTrip_PreservesAllDataKinds()
    {
        Schema schema = new([
            new ColumnInfo("float32", DataKind.Float32, nullable: false),
            new ColumnInfo("text", DataKind.String, nullable: false),
            new ColumnInfo("blob", DataKind.UInt8Array, nullable: false),
        ]);

        SourceSchema original = SourceSchema.Create("data", schema);
        string json = SchemaSerializer.Serialize(original);
        SourceSchema? deserialized = SchemaSerializer.Deserialize(json);

        Assert.NotNull(deserialized);
        Schema roundTripped = deserialized.Tables["data"];
        Assert.Equal(DataKind.Float32, roundTripped.Columns[0].Kind);
        Assert.Equal(DataKind.String, roundTripped.Columns[1].Kind);
        Assert.Equal(DataKind.UInt8Array, roundTripped.Columns[2].Kind);
    }
}
