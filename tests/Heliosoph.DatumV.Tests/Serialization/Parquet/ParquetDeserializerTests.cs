using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;
using Heliosoph.DatumV.Serialization;
using Heliosoph.DatumV.Serialization.Parquet;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace Heliosoph.DatumV.Tests.Serialization.Parquet;

/// <summary>
/// Integration tests for <see cref="ParquetDeserializer"/>: the
/// ingest-path counterpart of the <c>open_parquet</c> TVF. Yields rows
/// with the file's real typed schema so <c>datum ingest foo.parquet</c>
/// lands a queryable table into a .datum file by default.
/// </summary>
public sealed class ParquetDeserializerTests : ServiceTestBase, IDisposable
{
    private readonly string _scratchDir = Path.Combine(
        Path.GetTempPath(), $"parquet-deserializer-{Guid.NewGuid():N}");

    public ParquetDeserializerTests()
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
        string path = Path.Combine(_scratchDir, "ingest.parquet");
        var labelField = new DataField<int>("label");
        var textField = new DataField<string>("text");
        var schema = new ParquetSchema(labelField, textField);

        await using (Stream writeStream = File.Create(path))
        using (ParquetWriter writer = await ParquetWriter.CreateAsync(schema, writeStream))
        using (ParquetRowGroupWriter rg = writer.CreateRowGroup())
        {
            await rg.WriteColumnAsync(new DataColumn(labelField, new int[] { 0, 1, 2 }));
            await rg.WriteColumnAsync(new DataColumn(textField, new string[] { "a", "b", "c" }));
        }

        FileFormatDescriptor descriptor = new(path);
        ParquetDeserializer deserializer = new(descriptor);

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
        string path = Path.Combine(_scratchDir, "registered.parquet");
        var idField = new DataField<int>("id");
        var schema = new ParquetSchema(idField);
        await using (Stream writeStream = File.Create(path))
        using (ParquetWriter writer = await ParquetWriter.CreateAsync(schema, writeStream))
        using (ParquetRowGroupWriter rg = writer.CreateRowGroup())
        {
            await rg.WriteColumnAsync(new DataColumn(idField, new int[] { 42 }));
        }

        FileFormatDescriptor descriptor = new(path);
        FormatRegistry registry = new([new ParquetFileFormat()]);
        IFormatDeserializer deserializer = registry.CreateDeserializer(descriptor);
        Assert.IsType<ParquetDeserializer>(deserializer);

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
