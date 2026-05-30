using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.TableValued;
using Heliosoph.DatumV.Model;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Tests.Functions.TableValued;

/// <summary>
/// <c>open_parquet_meta(path)</c> table-valued function: yields one row
/// per leaf column with its parsed type metadata, row group count, and
/// total row count. Covers schema declaration, end-to-end walks against
/// HuggingFace-shaped fixtures (id + label + features), and correct
/// typing of primitive and array-shaped columns.
/// </summary>
public sealed class OpenParquetMetaFunctionTests : ServiceTestBase, IDisposable
{
    private readonly string _scratchDir = Path.Combine(
        Path.GetTempPath(), $"open-parquet-meta-{Guid.NewGuid():N}");

    public OpenParquetMetaFunctionTests()
    {
        Directory.CreateDirectory(_scratchDir);
    }

    public new void Dispose()
    {
        try { if (Directory.Exists(_scratchDir)) Directory.Delete(_scratchDir, recursive: true); }
        catch { /* best-effort */ }
        base.Dispose();
    }

    private string TempParquet(string name) => Path.Combine(_scratchDir, name);

    [Fact]
    public void ValidateArguments_DeclaresMetaSchema()
    {
        OpenParquetMetaFunction fn = new();
        Schema schema = ((ITableValuedFunction)fn).ValidateArguments([DataKind.String]);

        Assert.Equal(8, schema.Columns.Count);
        Assert.Equal("column_path", schema.Columns[0].Name);
        Assert.Equal(DataKind.String, schema.Columns[0].Kind);
        Assert.Equal("element_kind", schema.Columns[1].Name);
        Assert.Equal("is_array", schema.Columns[2].Name);
        Assert.Equal(DataKind.Boolean, schema.Columns[2].Kind);
        Assert.Equal("total_rows", schema.Columns[7].Name);
        Assert.Equal(DataKind.Int64, schema.Columns[7].Kind);
    }

    [Fact]
    public async Task Open_HuggingFaceShapedFixture_YieldsOneRowPerLeafColumnWithCorrectTypes()
    {
        // A classification-shaped dataset: integer label, string text,
        // float score. Matches the most common HF dataset shape after
        // tokenization is materialized.
        string path = TempParquet("hf-shape.parquet");
        await WritePrimitiveFixture(path);

        OpenParquetMetaFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(path)], ctx), ctx);

        Assert.Equal(3, rows.Count);

        Row label = rows.Single(r => r["column_path"].AsString() == "label");
        Assert.Equal("Int32", label["element_kind"].AsString());
        Assert.False(label["is_array"].AsBoolean());
        Assert.True(label["is_supported"].AsBoolean());
        Assert.Equal(1, label["row_group_count"].AsInt32());
        Assert.Equal(3L, label["total_rows"].AsInt64());

        Row text = rows.Single(r => r["column_path"].AsString() == "text");
        Assert.Equal("String", text["element_kind"].AsString());
        Assert.False(text["is_array"].AsBoolean());

        Row score = rows.Single(r => r["column_path"].AsString() == "score");
        Assert.Equal("Float64", score["element_kind"].AsString());
    }

    [Fact]
    public async Task Open_ArrayShapedColumn_FlaggedAsArray()
    {
        // An LLM-tokenization-shaped column: each row is a list of int32 tokens.
        string path = TempParquet("array-shape.parquet");
        var tokensField = new DataField("tokens", typeof(int), isNullable: false, isArray: true);
        var schema = new ParquetSchema(tokensField);

        await using (Stream writeStream = File.Create(path))
        using (ParquetWriter writer = await ParquetWriter.CreateAsync(schema, writeStream))
        using (ParquetRowGroupWriter rg = writer.CreateRowGroup())
        {
            int[] values = [101, 202, 303, 404, 505];
            int[] repetitionLevels = [0, 1, 1, 0, 1];
            await rg.WriteColumnAsync(new DataColumn(tokensField, values, repetitionLevels));
        }

        OpenParquetMetaFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(path)], ctx), ctx);

        Assert.Single(rows);
        Assert.Equal("tokens", rows[0]["column_path"].AsString());
        Assert.Equal("Int32", rows[0]["element_kind"].AsString());
        Assert.True(rows[0]["is_array"].AsBoolean());
        Assert.True(rows[0]["is_supported"].AsBoolean());
    }

    // ───────────────────────── Fixtures + helpers ─────────────────────────

    private static async Task WritePrimitiveFixture(string path)
    {
        var labelField = new DataField<int>("label");
        var textField = new DataField<string>("text");
        var scoreField = new DataField<double>("score");
        var schema = new ParquetSchema(labelField, textField, scoreField);

        await using Stream writeStream = File.Create(path);
        using ParquetWriter writer = await ParquetWriter.CreateAsync(schema, writeStream);
        using ParquetRowGroupWriter rg = writer.CreateRowGroup();

        await rg.WriteColumnAsync(new DataColumn(labelField, new int[] { 0, 1, 2 }));
        await rg.WriteColumnAsync(new DataColumn(textField, new string[] { "alpha", "beta", "gamma" }));
        await rg.WriteColumnAsync(new DataColumn(scoreField, new double[] { 0.1, 0.5, 0.9 }));
    }

    private static async Task<List<Row>> CollectAsync(IAsyncEnumerable<RowBatch> batches, ExecutionContext ctx)
    {
        List<Row> rows = [];
        await foreach (RowBatch batch in batches)
        {
            for (int i = 0; i < batch.Count; i++)
            {
                Row source = batch[i];
                DataValue[] stabilized = new DataValue[source.FieldCount];
                for (int f = 0; f < source.FieldCount; f++)
                {
                    stabilized[f] = DataValueRetention.Stabilize(source[f], batch.Arena, ctx.Store);
                }
                rows.Add(new Row(source.ColumnLookup, stabilized));
            }
        }
        return rows;
    }
}
