using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;

namespace Heliosoph.DatumV.Tests.Serialization.Arrow;

/// <summary>
/// Smoke test for Apache.Arrow — the pure-managed .NET binding for Arrow
/// IPC files (.arrow / Feather v2). Confirms the read/write/schema-
/// introspection API surface we need for the <c>open_arrow</c> +
/// <c>open_arrow_meta</c> TVFs: programmatic fixture build with
/// <see cref="ArrowFileWriter"/>, schema enumeration via
/// <see cref="Apache.Arrow.Schema"/>, RecordBatch iteration via
/// <see cref="ArrowFileReader.ReadNextRecordBatchAsync"/>, and per-column
/// typed value access. If this passes, the Arrow reader can be built
/// without architectural surprises.
/// </summary>
public sealed class ArrowSmokeTest
{
    [Fact]
    public async Task RoundTrip_PrimitiveSchema_ReadsTypedColumnsBack()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"arrow-smoke-{Guid.NewGuid():N}.arrow");

        try
        {
            await WritePrimitiveFixture(path);

            await using Stream readStream = File.OpenRead(path);
            using ArrowFileReader reader = new(readStream);

            // Schema introspection — same surface open_arrow_meta will use.
            Apache.Arrow.Schema schema = reader.Schema;
            Assert.Equal(3, schema.FieldsList.Count);

            Field idField = schema.FieldsList[0];
            Assert.Equal("id", idField.Name);
            Assert.Equal(ArrowTypeId.Int32, idField.DataType.TypeId);

            Field labelField = schema.FieldsList[1];
            Assert.Equal("label", labelField.Name);
            Assert.Equal(ArrowTypeId.String, labelField.DataType.TypeId);

            Field magField = schema.FieldsList[2];
            Assert.Equal("mag", magField.Name);
            Assert.Equal(ArrowTypeId.Double, magField.DataType.TypeId);

            // Iterate record batches — what open_arrow will use to stream.
            RecordBatch? batch = await reader.ReadNextRecordBatchAsync();
            Assert.NotNull(batch);
            using (batch)
            {
                Assert.Equal(3, batch.Length);

                // Typed column access by index.
                Int32Array idCol = (Int32Array)batch.Column(0);
                Assert.Equal(1, idCol.GetValue(0));
                Assert.Equal(2, idCol.GetValue(1));
                Assert.Equal(3, idCol.GetValue(2));

                StringArray labelCol = (StringArray)batch.Column(1);
                Assert.Equal("star", labelCol.GetString(0));
                Assert.Equal("galaxy", labelCol.GetString(1));
                Assert.Equal("qso", labelCol.GetString(2));

                DoubleArray magCol = (DoubleArray)batch.Column(2);
                Assert.Equal(19.5, magCol.GetValue(0));
                Assert.Equal(20.25, magCol.GetValue(1));
                Assert.Equal(18.75, magCol.GetValue(2));
            }

            // No more batches.
            RecordBatch? second = await reader.ReadNextRecordBatchAsync();
            Assert.Null(second);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private static async Task WritePrimitiveFixture(string path)
    {
        var schema = new Apache.Arrow.Schema.Builder()
            .Field(f => f.Name("id").DataType(Int32Type.Default).Nullable(false))
            .Field(f => f.Name("label").DataType(StringType.Default).Nullable(false))
            .Field(f => f.Name("mag").DataType(DoubleType.Default).Nullable(false))
            .Build();

        Int32Array idCol = new Int32Array.Builder().AppendRange(new int[] { 1, 2, 3 }).Build();
        StringArray labelCol = new StringArray.Builder().Append("star").Append("galaxy").Append("qso").Build();
        DoubleArray magCol = new DoubleArray.Builder().AppendRange(new double[] { 19.5, 20.25, 18.75 }).Build();

        using var batch = new RecordBatch(schema, [idCol, labelCol, magCol], length: 3);

        await using Stream writeStream = File.Create(path);
        using ArrowFileWriter writer = new(writeStream, schema);
        await writer.WriteRecordBatchAsync(batch);
        await writer.WriteEndAsync();
    }
}
