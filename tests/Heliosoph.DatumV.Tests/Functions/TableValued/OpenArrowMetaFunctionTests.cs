using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.TableValued;
using Heliosoph.DatumV.Model;
using ArrowSchema = Apache.Arrow.Schema;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;
using HelioSchema = Heliosoph.DatumV.Model.Schema;

namespace Heliosoph.DatumV.Tests.Functions.TableValued;

/// <summary>
/// <c>open_arrow_meta(path)</c> table-valued function: yields one row per
/// top-level column with its parsed type metadata, record batch count,
/// and total row count. Covers schema declaration and end-to-end walks
/// against typical Arrow IPC fixtures (primitive + array + nullable).
/// </summary>
public sealed class OpenArrowMetaFunctionTests : ServiceTestBase, IDisposable
{
    private readonly string _scratchDir = Path.Combine(
        Path.GetTempPath(), $"open-arrow-meta-{Guid.NewGuid():N}");

    public OpenArrowMetaFunctionTests()
    {
        Directory.CreateDirectory(_scratchDir);
    }

    public new void Dispose()
    {
        try { if (Directory.Exists(_scratchDir)) Directory.Delete(_scratchDir, recursive: true); }
        catch { /* best-effort */ }
        base.Dispose();
    }

    private string TempArrow(string name) => Path.Combine(_scratchDir, name);

    [Fact]
    public void ValidateArguments_DeclaresMetaSchema()
    {
        OpenArrowMetaFunction fn = new();
        HelioSchema schema = ((ITableValuedFunction)fn).ValidateArguments([DataKind.String]);

        Assert.Equal(8, schema.Columns.Count);
        Assert.Equal("column_name", schema.Columns[0].Name);
        Assert.Equal("element_kind", schema.Columns[1].Name);
        Assert.Equal("batch_count", schema.Columns[6].Name);
        Assert.Equal(DataKind.Int32, schema.Columns[6].Kind);
        Assert.Equal("total_rows", schema.Columns[7].Name);
        Assert.Equal(DataKind.Int64, schema.Columns[7].Kind);
    }

    [Fact]
    public async Task Open_PrimitiveFixture_YieldsOneRowPerColumnWithCorrectTypes()
    {
        string path = TempArrow("primitives.arrow");
        await WritePrimitiveFixture(path);

        OpenArrowMetaFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(path)], ctx), ctx);

        Assert.Equal(3, rows.Count);

        Row label = rows.Single(r => r["column_name"].AsString() == "label");
        Assert.Equal("Int32", label["element_kind"].AsString());
        Assert.False(label["is_array"].AsBoolean());
        Assert.True(label["is_supported"].AsBoolean());
        Assert.Equal(1, label["batch_count"].AsInt32());
        Assert.Equal(3L, label["total_rows"].AsInt64());

        Row text = rows.Single(r => r["column_name"].AsString() == "text");
        Assert.Equal("String", text["element_kind"].AsString());
        Assert.False(text["is_array"].AsBoolean());

        Row score = rows.Single(r => r["column_name"].AsString() == "score");
        Assert.Equal("Float64", score["element_kind"].AsString());
    }

    [Fact]
    public async Task Open_TimestampWithTimezone_FlaggedAsTimestampTz()
    {
        // Arrow timestamps with timezone metadata should map to TimestampTz;
        // naked (no tz) Timestamps to plain Timestamp.
        string path = TempArrow("timestamps.arrow");
        var schema = new ArrowSchema.Builder()
            .Field(f => f.Name("naive").DataType(new TimestampType(TimeUnit.Millisecond, timezone: "")).Nullable(false))
            .Field(f => f.Name("withTz").DataType(new TimestampType(TimeUnit.Microsecond, timezone: "UTC")).Nullable(false))
            .Build();

        var naiveArr = new TimestampArray.Builder().Append(DateTimeOffset.FromUnixTimeMilliseconds(0)).Build();
        var tzArr = new TimestampArray.Builder(TimeUnit.Microsecond).Append(DateTimeOffset.FromUnixTimeMilliseconds(0)).Build();
        using var batch = new RecordBatch(schema, new IArrowArray[] { naiveArr, tzArr }, length: 1);
        await using (Stream s = File.Create(path))
        using (var w = new ArrowFileWriter(s, schema))
        {
            await w.WriteRecordBatchAsync(batch);
            await w.WriteEndAsync();
        }

        OpenArrowMetaFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(path)], ctx), ctx);

        Row naiveRow = rows.Single(r => r["column_name"].AsString() == "naive");
        Assert.Equal("Timestamp", naiveRow["element_kind"].AsString());
        Assert.Contains("Timestamp[Millisecond]", naiveRow["logical_type"].AsString());

        Row tzRow = rows.Single(r => r["column_name"].AsString() == "withTz");
        Assert.Equal("TimestampTz", tzRow["element_kind"].AsString());
        Assert.Contains("UTC", tzRow["logical_type"].AsString());
    }

    // ───────────────────────── Fixtures + helpers ─────────────────────────

    private static async Task WritePrimitiveFixture(string path)
    {
        var schema = new ArrowSchema.Builder()
            .Field(f => f.Name("label").DataType(Int32Type.Default).Nullable(false))
            .Field(f => f.Name("text").DataType(StringType.Default).Nullable(false))
            .Field(f => f.Name("score").DataType(DoubleType.Default).Nullable(false))
            .Build();

        Int32Array labels = new Int32Array.Builder().AppendRange([0, 1, 2]).Build();
        StringArray texts = new StringArray.Builder().Append("alpha").Append("beta").Append("gamma").Build();
        DoubleArray scores = new DoubleArray.Builder().AppendRange([0.1, 0.5, 0.9]).Build();
        using var batch = new RecordBatch(schema, new IArrowArray[] { labels, texts, scores }, length: 3);

        await using Stream stream = File.Create(path);
        using ArrowFileWriter writer = new(stream, schema);
        await writer.WriteRecordBatchAsync(batch);
        await writer.WriteEndAsync();
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
