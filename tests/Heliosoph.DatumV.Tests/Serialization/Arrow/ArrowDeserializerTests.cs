using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;
using Heliosoph.DatumV.Serialization;
using Heliosoph.DatumV.Serialization.Arrow;
using ArrowSchema = Apache.Arrow.Schema;

namespace Heliosoph.DatumV.Tests.Serialization.Arrow;

/// <summary>
/// Integration tests for <see cref="ArrowDeserializer"/>: the ingest-path
/// counterpart of the <c>open_arrow</c> TVF. Yields rows with the file's
/// real typed schema so <c>datum ingest foo.arrow</c> lands a queryable
/// table into a .datum file by default.
/// </summary>
public sealed class ArrowDeserializerTests : ServiceTestBase, IDisposable
{
    private readonly string _scratchDir = Path.Combine(
        Path.GetTempPath(), $"arrow-deserializer-{Guid.NewGuid():N}");

    public ArrowDeserializerTests()
    {
        Directory.CreateDirectory(_scratchDir);
    }

    public new void Dispose()
    {
        try { if (Directory.Exists(_scratchDir)) Directory.Delete(_scratchDir, recursive: true); }
        catch { /* best-effort */ }
        base.Dispose();
    }

    [Fact]
    public async Task Deserialize_PrimitiveFixture_YieldsTypedRows()
    {
        string path = Path.Combine(_scratchDir, "ingest.arrow");
        var schema = new ArrowSchema.Builder()
            .Field(f => f.Name("label").DataType(Int32Type.Default).Nullable(false))
            .Field(f => f.Name("text").DataType(StringType.Default).Nullable(false))
            .Build();
        var labels = new Int32Array.Builder().AppendRange([0, 1, 2]).Build();
        var texts = new StringArray.Builder().Append("a").Append("b").Append("c").Build();
        using var batch = new RecordBatch(schema, new IArrowArray[] { labels, texts }, length: 3);
        await using (Stream s = File.Create(path))
        using (var w = new ArrowFileWriter(s, schema))
        {
            await w.WriteRecordBatchAsync(batch);
            await w.WriteEndAsync();
        }

        FileFormatDescriptor descriptor = new(path);
        ArrowDeserializer deserializer = new(descriptor);

        Pool pool = CreatePool();
        SerializationContext context = new(pool);
        List<DataValue[]> rows = await CollectRows(deserializer, context);

        Assert.Equal(3, rows.Count);
        Assert.Equal(0, rows[0][0].AsInt32());
        Assert.Equal("a", rows[0][1].AsString());
        Assert.Equal(2, rows[2][0].AsInt32());
        Assert.Equal("c", rows[2][1].AsString());
    }

    [Fact]
    public async Task Deserialize_RegisteredInFormatRegistry_OpensViaCanHandle()
    {
        string path = Path.Combine(_scratchDir, "registered.arrow");
        var schema = new ArrowSchema.Builder()
            .Field(f => f.Name("id").DataType(Int32Type.Default).Nullable(false))
            .Build();
        var ids = new Int32Array.Builder().AppendRange([42]).Build();
        using var batch = new RecordBatch(schema, new IArrowArray[] { ids }, length: 1);
        await using (Stream s = File.Create(path))
        using (var w = new ArrowFileWriter(s, schema))
        {
            await w.WriteRecordBatchAsync(batch);
            await w.WriteEndAsync();
        }

        FileFormatDescriptor descriptor = new(path);
        FormatRegistry registry = new([new ArrowFileFormat()]);
        IFormatDeserializer deserializer = registry.CreateDeserializer(descriptor);
        Assert.IsType<ArrowDeserializer>(deserializer);

        Pool pool = CreatePool();
        SerializationContext context = new(pool);
        List<DataValue[]> rows = await CollectRows(deserializer, context);
        Assert.Single(rows);
        Assert.Equal(42, rows[0][0].AsInt32());
    }

    private static async Task<List<DataValue[]>> CollectRows(
        IFormatDeserializer deserializer,
        SerializationContext context)
    {
        List<DataValue[]> rows = [];
        await foreach (RowBatch batch in deserializer.DeserializeAsync(context))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                Row source = batch[i];
                DataValue[] stabilized = new DataValue[source.FieldCount];
                for (int f = 0; f < source.FieldCount; f++)
                {
                    stabilized[f] = DataValueRetention.Stabilize(source[f], batch.Arena, batch.Arena);
                }
                rows.Add(stabilized);
            }
            batch.Dispose();
        }
        return rows;
    }
}
