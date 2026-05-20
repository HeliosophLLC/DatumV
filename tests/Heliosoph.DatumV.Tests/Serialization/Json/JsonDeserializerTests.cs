using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;
using Heliosoph.DatumV.Serialization;
using Heliosoph.DatumV.Serialization.Json;

namespace Heliosoph.DatumV.Tests.Serialization.Json;

/// <summary>
/// End-to-end coverage for <see cref="JsonDeserializer"/>: schema inference
/// over the full document, materialization of each row into a
/// <see cref="RowBatch"/>, root-shape validation, and JSON fall-through for
/// nested values.
/// </summary>
public sealed class JsonDeserializerTests : ServiceTestBase
{
    [Fact]
    public async Task RootArray_OfObjects_ProducesOneRowPerElement()
    {
        const string json = """[{"id":1,"name":"alice"},{"id":2,"name":"bob"}]""";

        List<RowBatch> batches = await DeserializeAsync(json);

        Assert.Single(batches);
        Assert.Equal(2, batches[0].Count);
        Assert.Equal(2, batches[0].ColumnLookup.Count);
    }

    [Fact]
    public async Task RootObject_ProducesOneRowTable()
    {
        const string json = """{"name":"only","count":42}""";

        List<RowBatch> batches = await DeserializeAsync(json);

        Assert.Single(batches);
        Assert.Equal(1, batches[0].Count);
        Assert.Equal(2, batches[0].ColumnLookup.Count);
    }

    [Fact]
    public async Task RootPrimitive_ThrowsInvalidData()
    {
        const string json = "42";

        await Assert.ThrowsAsync<InvalidDataException>(() => DeserializeAsync(json));
    }

    [Fact]
    public async Task RootArrayOfPrimitives_ThrowsInvalidData()
    {
        const string json = """[1,2,3]""";

        await Assert.ThrowsAsync<InvalidDataException>(() => DeserializeAsync(json));
    }

    [Fact]
    public async Task RootArrayOfArrays_ThrowsInvalidData()
    {
        const string json = """[[1,2],[3,4]]""";

        await Assert.ThrowsAsync<InvalidDataException>(() => DeserializeAsync(json));
    }

    [Fact]
    public async Task ScalarValues_MaterializeWithNarrowedKinds()
    {
        const string json = """[{"n":1,"x":1.5,"on":true,"name":"a"},{"n":2,"x":2.5,"on":false,"name":"b"}]""";

        List<RowBatch> batches = await DeserializeAsync(json);
        RowBatch batch = Assert.Single(batches);

        Row row0 = batch[0];
        Assert.Equal(DataKind.UInt8, row0["n"].Kind);
        Assert.Equal(DataKind.Float32, row0["x"].Kind);
        Assert.Equal(DataKind.Boolean, row0["on"].Kind);
        Assert.Equal(DataKind.String, row0["name"].Kind);

        Assert.Equal(1, (byte)row0["n"].AsUInt8());
        Assert.True(row0["on"].AsBoolean());
        Assert.False(batch[1]["on"].AsBoolean());
    }

    [Fact]
    public async Task NestedValues_StoredAsJsonKind()
    {
        const string json = """[{"id":1,"bbox":[0,0,10,10]},{"id":2,"bbox":[5,5,20,20]}]""";

        List<RowBatch> batches = await DeserializeAsync(json);
        RowBatch batch = Assert.Single(batches);

        Assert.Equal(DataKind.Json, batch[0]["bbox"].Kind);
        Assert.Equal(DataKind.Json, batch[1]["bbox"].Kind);
        Assert.False(batch[0]["bbox"].IsNull);
    }

    [Fact]
    public async Task MissingKey_AppearsAsTypedNull()
    {
        const string json = """[{"a":1,"b":"x"},{"a":2}]""";

        List<RowBatch> batches = await DeserializeAsync(json);
        RowBatch batch = Assert.Single(batches);

        Assert.False(batch[0]["b"].IsNull);
        Assert.True(batch[1]["b"].IsNull);
        Assert.Equal(DataKind.String, batch[1]["b"].Kind);
    }

    [Fact]
    public async Task ExplicitNull_AppearsAsTypedNull()
    {
        const string json = """[{"a":1,"b":"x"},{"a":2,"b":null}]""";

        List<RowBatch> batches = await DeserializeAsync(json);
        RowBatch batch = Assert.Single(batches);

        Assert.True(batch[1]["b"].IsNull);
    }

    [Fact]
    public async Task ScanMetrics_PopulatedAfterDeserialization()
    {
        const string json = """[{"a":1},{"a":2},{"a":3}]""";

        Pool pool = CreatePool();
        SerializationContext context = new(pool);
        MemoryFileDescriptor descriptor = new(json, "test.json");
        JsonDeserializer deserializer = new(descriptor);

        await foreach (RowBatch batch in deserializer.DeserializeAsync(context))
        {
            batch.Dispose();
        }

        Assert.NotNull(deserializer.ScanMetrics);
        Assert.Equal(3, deserializer.ScanMetrics!.RowCount);
    }

    [Fact]
    public void JsonFileFormat_MatchesJsonExtension()
    {
        JsonFileFormat format = new();
        MemoryFileDescriptor jsonDescriptor = new("{}", "data.json");
        MemoryFileDescriptor csvDescriptor = new("a,b", "data.csv");

        Assert.True(format.CanHandle(jsonDescriptor, out IFormatDeserializer? jsonDeserializer));
        Assert.NotNull(jsonDeserializer);
        Assert.False(format.CanHandle(csvDescriptor, out _));
    }

    // ──────────────── Helpers ────────────────

    private async Task<List<RowBatch>> DeserializeAsync(string json)
    {
        Pool pool = CreatePool();
        SerializationContext context = new(pool);
        MemoryFileDescriptor descriptor = new(json, "test.json");
        JsonDeserializer deserializer = new(descriptor);

        List<RowBatch> batches = [];
        await foreach (RowBatch batch in deserializer.DeserializeAsync(context))
        {
            batches.Add(batch);
        }
        return batches;
    }
}
