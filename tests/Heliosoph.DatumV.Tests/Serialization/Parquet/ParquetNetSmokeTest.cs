using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace Heliosoph.DatumV.Tests.Serialization.Parquet;

/// <summary>
/// Smoke test for Parquet.Net — the pure-managed Parquet library already
/// referenced by the project (no native binary distribution concern, same
/// as PureHDF). Confirms the read/write/schema-introspection API surface
/// we need for the <c>open_parquet</c> + <c>open_parquet_meta</c> TVFs:
/// programmatic fixture build, schema enumeration, row-group reads,
/// per-column <see cref="DataColumn"/> access. If this passes the Parquet
/// reader can be built without surprises.
/// </summary>
public sealed class ParquetNetSmokeTest
{
    [Fact]
    public async Task RoundTrip_PrimitiveSchema_ReadsTypedColumnsBackOut()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"parquet-smoke-{Guid.NewGuid():N}.parquet");

        try
        {
            await WritePrimitiveFixture(path);

            await using Stream readStream = File.OpenRead(path);
            using ParquetReader reader = await ParquetReader.CreateAsync(readStream);

            // Schema introspection — what open_parquet_meta will read.
            ParquetSchema schema = reader.Schema;
            DataField[] dataFields = schema.GetDataFields();
            Assert.Equal(3, dataFields.Length);
            Assert.Equal("id", dataFields[0].Name);
            Assert.Equal(typeof(int), dataFields[0].ClrType);
            Assert.Equal("label", dataFields[1].Name);
            Assert.Equal(typeof(string), dataFields[1].ClrType);
            Assert.Equal("mag", dataFields[2].Name);
            Assert.Equal(typeof(double), dataFields[2].ClrType);

            // Row-group iteration — what open_parquet will use to stream.
            Assert.Equal(1, reader.RowGroupCount);
            using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);

            DataColumn idCol = await rg.ReadColumnAsync(dataFields[0]);
            int[] ids = (int[])idCol.Data;
            Assert.Equal(new int[] { 1, 2, 3 }, ids);

            DataColumn labelCol = await rg.ReadColumnAsync(dataFields[1]);
            string[] labels = (string[])labelCol.Data;
            Assert.Equal(new string[] { "star", "galaxy", "qso" }, labels);

            DataColumn magCol = await rg.ReadColumnAsync(dataFields[2]);
            double[] mags = (double[])magCol.Data;
            Assert.Equal(new double[] { 19.5, 20.25, 18.75 }, mags);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ArrayField_RoundTrip_ReadsRepetitionLevels()
    {
        // Parquet.Net exposes "array column" as DataField with isArray = true.
        // Underneath, it emits a list field. The writer auto-computes
        // repetition levels from the flat array shape we feed it.
        string path = Path.Combine(
            Path.GetTempPath(),
            $"parquet-array-smoke-{Guid.NewGuid():N}.parquet");

        try
        {
            // Two rows: [10, 20, 30] and [40, 50].
            var tokensField = new DataField("tokens", typeof(int), isNullable: false, isArray: true);
            var schema = new ParquetSchema(tokensField);

            await using (Stream writeStream = File.Create(path))
            using (ParquetWriter writer = await ParquetWriter.CreateAsync(schema, writeStream))
            using (ParquetRowGroupWriter rg = writer.CreateRowGroup())
            {
                int[] values = [10, 20, 30, 40, 50];
                int[] repetitionLevels = [0, 1, 1, 0, 1];
                await rg.WriteColumnAsync(new DataColumn(tokensField, values, repetitionLevels));
            }

            await using Stream readStream = File.OpenRead(path);
            using ParquetReader reader = await ParquetReader.CreateAsync(readStream);
            using ParquetRowGroupReader rgReader = reader.OpenRowGroupReader(0);
            DataField[] fields = reader.Schema.GetDataFields();
            Assert.Single(fields);
            Assert.True(fields[0].IsArray);
            Assert.Equal(typeof(int), fields[0].ClrType);

            DataColumn col = await rgReader.ReadColumnAsync(fields[0]);
            // Array-shaped columns come back as nullable types regardless of
            // the IsNullable flag — Parquet.Net's read path widens to the
            // nullable storage shape because the array-encoded format can't
            // distinguish "absent" from "null" without it.
            int?[] roundTrippedValues = (int?[])col.Data;
            Assert.Equal(new int?[] { 10, 20, 30, 40, 50 }, roundTrippedValues);
            Assert.NotNull(col.RepetitionLevels);
            Assert.Equal(new int[] { 0, 1, 1, 0, 1 }, col.RepetitionLevels);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private static async Task WritePrimitiveFixture(string path)
    {
        var idField = new DataField<int>("id");
        var labelField = new DataField<string>("label");
        var magField = new DataField<double>("mag");
        var schema = new ParquetSchema(idField, labelField, magField);

        await using Stream writeStream = File.Create(path);
        using ParquetWriter writer = await ParquetWriter.CreateAsync(schema, writeStream);
        using ParquetRowGroupWriter rg = writer.CreateRowGroup();

        await rg.WriteColumnAsync(new DataColumn(idField, new int[] { 1, 2, 3 }));
        await rg.WriteColumnAsync(new DataColumn(labelField, new string[] { "star", "galaxy", "qso" }));
        await rg.WriteColumnAsync(new DataColumn(magField, new double[] { 19.5, 20.25, 18.75 }));
    }
}
