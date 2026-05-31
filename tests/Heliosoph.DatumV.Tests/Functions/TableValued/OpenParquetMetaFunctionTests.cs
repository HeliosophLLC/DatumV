using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Export;
using Heliosoph.DatumV.Export.Parquet;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.TableValued;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;
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

        Assert.Equal(11, schema.Columns.Count);
        Assert.Equal("column_path", schema.Columns[0].Name);
        Assert.Equal(DataKind.String, schema.Columns[0].Kind);
        Assert.Equal("element_kind", schema.Columns[1].Name);
        Assert.Equal("is_array", schema.Columns[2].Name);
        Assert.Equal(DataKind.Boolean, schema.Columns[2].Kind);
        Assert.Equal("total_rows", schema.Columns[7].Name);
        Assert.Equal(DataKind.Int64, schema.Columns[7].Kind);
        // Trailing datumv_* columns surface the Heliosoph.DatumV typed-kind
        // metadata embedded by ParquetExportSink. Nullable so third-party
        // Parquet files (no metadata) read as NULL rather than empty string.
        Assert.Equal("datumv_kind", schema.Columns[8].Name);
        Assert.Equal(DataKind.String, schema.Columns[8].Kind);
        Assert.True(schema.Columns[8].Nullable);
        Assert.Equal("datumv_format", schema.Columns[9].Name);
        Assert.Equal("datumv_version", schema.Columns[10].Name);
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

    [Fact]
    public async Task Open_FileWithDatumvTaggedColumn_SurfacesKindFormatVersion()
    {
        // Slice A: open_parquet_meta now surfaces the datumv.kind / format /
        // version column-chunk metadata so a user can inspect "what kind tag
        // does this file carry" without firing a trial open_parquet. The
        // ParquetExportSink path attaches the tag on every typed-media
        // column — exercise it with an Image column here since the round
        // trip is the simplest of the bunch.
        string path = TempParquet("tagged-image.parquet");
        await WriteTaggedImageFixture(path);

        OpenParquetMetaFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(path)], ctx), ctx);

        Assert.Equal(2, rows.Count);
        Row idRow = rows.Single(r => r["column_path"].AsString() == "id");
        Row picRow = rows.Single(r => r["column_path"].AsString() == "pic");

        // The id column is plain Int32 — no datumv tag.
        Assert.True(idRow["datumv_kind"].IsNull);
        Assert.True(idRow["datumv_format"].IsNull);
        Assert.True(idRow["datumv_version"].IsNull);

        // The pic column carries the typed-media tag block.
        Assert.Equal("Image", picRow["datumv_kind"].AsString());
        Assert.Equal("passthrough", picRow["datumv_format"].AsString());
        Assert.Equal("1", picRow["datumv_version"].AsString());
    }

    [Fact]
    public async Task Open_UntaggedFile_DatumvColumnsAreNull()
    {
        // Backward compat: a Parquet file produced by any tool that doesn't
        // write the datumv keys (every external tool, plus pre-metadata
        // Heliosoph.DatumV builds) surfaces NULL for the trailing columns
        // instead of empty strings.
        string path = TempParquet("untagged.parquet");
        await WritePrimitiveFixture(path);

        OpenParquetMetaFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(path)], ctx), ctx);

        foreach (Row row in rows)
        {
            Assert.True(row["datumv_kind"].IsNull,
                $"column {row["column_path"].AsString()} unexpectedly carries a datumv_kind value.");
            Assert.True(row["datumv_format"].IsNull);
            Assert.True(row["datumv_version"].IsNull);
        }
    }

    // ───────────────────────── Fixtures + helpers ─────────────────────────

    private async Task WriteTaggedImageFixture(string path)
    {
        Pool pool = CreatePool();
        SidecarRegistry registry = new();
        Schema schema = new(
        [
            new ColumnInfo("id", DataKind.Int32, nullable: false),
            new ColumnInfo("pic", DataKind.Image, nullable: false),
        ]);
        ColumnLookup lookup = new(["id", "pic"]);
        using Arena arena = new();
        using RowBatch batch = pool.RentRowBatch(lookup, capacity: 1, arena: arena);
        // One row with a recognisably-PNG byte pattern — open_parquet_meta
        // doesn't inspect the bytes, but using the magic keeps the file
        // sensible if anyone opens it manually later.
        byte[] pic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xDE, 0xAD];
        batch.Add([DataValue.FromInt32(1), DataValue.FromImage(pic, arena)]);

        ParquetExportFormat format = new();
        await using IExportSink sink = format.CreateSink(
            new ExportTarget.File(path), schema,
            [MediaDisposition.Inline, MediaDisposition.Inline],
            ExportOptions.Empty, registry);
        await sink.WriteAsync(batch, default);
        await sink.FinishAsync(default);
    }

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
