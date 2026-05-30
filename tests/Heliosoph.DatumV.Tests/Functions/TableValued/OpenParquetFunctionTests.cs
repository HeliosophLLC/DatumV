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
/// <c>open_parquet(path)</c> table-valued function: opens a Parquet file
/// and yields its rows with one column per leaf field. Covers the
/// constant-args validation hook (plan-time peek surfaces the file's
/// real schema), per-type row decoding for primitive columns and 1-D
/// array columns, and the explicit failure modes (non-constant arg,
/// missing file).
/// </summary>
public sealed class OpenParquetFunctionTests : ServiceTestBase, IDisposable
{
    private readonly ByteArrayValueStore _constantStore = new();
    private readonly string _scratchDir = Path.Combine(
        Path.GetTempPath(), $"open-parquet-{Guid.NewGuid():N}");

    public OpenParquetFunctionTests()
    {
        Directory.CreateDirectory(_scratchDir);
    }

    public new void Dispose()
    {
        try { if (Directory.Exists(_scratchDir)) Directory.Delete(_scratchDir, recursive: true); }
        catch { /* best-effort */ }
        base.Dispose();
    }

    private DataValue Const(string s) => DataValue.FromString(s, _constantStore);
    private string TempParquet(string name) => Path.Combine(_scratchDir, name);

    // ───────────────────── Plan-time schema peek ─────────────────────

    [Fact]
    public async Task ValidateArguments_OnConstantPath_PeeksFileAndReturnsRealSchema()
    {
        string path = TempParquet("hf-classification.parquet");
        await WriteClassificationFixture(path);

        OpenParquetFunction fn = new();
        Schema schema = ((ITableValuedFunction)fn).ValidateArguments(
            argumentKinds: [DataKind.String],
            constantArguments: [Const(path)],
            constantStore: _constantStore,
            cancellationToken: default);

        Assert.Equal(3, schema.Columns.Count);
        Assert.Equal("label", schema.Columns[0].Name);
        Assert.Equal(DataKind.Int32, schema.Columns[0].Kind);
        Assert.Equal("text", schema.Columns[1].Name);
        Assert.Equal(DataKind.String, schema.Columns[1].Kind);
        Assert.Equal("score", schema.Columns[2].Name);
        Assert.Equal(DataKind.Float64, schema.Columns[2].Kind);
    }

    [Fact]
    public void ValidateArguments_OnNonConstantPath_Throws()
    {
        OpenParquetFunction fn = new();
        FunctionArgumentException ex = Assert.Throws<FunctionArgumentException>(() =>
            ((ITableValuedFunction)fn).ValidateArguments(
                argumentKinds: [DataKind.String],
                constantArguments: [null],
                constantStore: _constantStore,
                cancellationToken: default));
        Assert.Contains("constant STRING", ex.Message);
    }

    [Fact]
    public void ValidateArguments_OnMissingFile_Throws()
    {
        OpenParquetFunction fn = new();
        FunctionArgumentException ex = Assert.Throws<FunctionArgumentException>(() =>
            ((ITableValuedFunction)fn).ValidateArguments(
                argumentKinds: [DataKind.String],
                constantArguments: [Const("/no/such.parquet")],
                constantStore: _constantStore,
                cancellationToken: default));
        Assert.Contains("not found", ex.Message);
    }

    // ───────────────────── Runtime row decode ─────────────────────

    [Fact]
    public async Task Open_PrimitiveColumns_DecodesEachRowWithTypedFields()
    {
        string path = TempParquet("primitives.parquet");
        await WriteClassificationFixture(path);

        OpenParquetFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(path)], ctx), ctx);

        Assert.Equal(3, rows.Count);
        Assert.Equal(0, rows[0]["label"].AsInt32());
        Assert.Equal("alpha", rows[0]["text"].AsString());
        Assert.Equal(0.1, rows[0]["score"].AsFloat64());

        Assert.Equal(2, rows[2]["label"].AsInt32());
        Assert.Equal("gamma", rows[2]["text"].AsString());
        Assert.Equal(0.9, rows[2]["score"].AsFloat64());
    }

    [Fact]
    public async Task Open_ArrayColumn_DecodesPerRowSlicesAsTypedArrays()
    {
        // Token-sequence shape: two rows, [101, 202, 303] and [404, 505].
        string path = TempParquet("tokens.parquet");
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

        OpenParquetFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(path)], ctx), ctx);

        Assert.Equal(2, rows.Count);
        ReadOnlySpan<int> row0 = rows[0]["tokens"].AsArraySpan<int>(ctx.Store);
        Assert.Equal(new int[] { 101, 202, 303 }, row0.ToArray());
        ReadOnlySpan<int> row1 = rows[1]["tokens"].AsArraySpan<int>(ctx.Store);
        Assert.Equal(new int[] { 404, 505 }, row1.ToArray());
    }

    [Fact]
    public async Task Open_TemporalAndDecimalColumns_DecodeAsTypedScalars()
    {
        // Mirrors the NYC taxi trip shape: a Timestamp pickup/dropoff plus
        // a Decimal fare column. Parquet.Net surfaces these CLR types
        // (DateTime, decimal); the row decoder needs scalar arms wired.
        string path = TempParquet("trips.parquet");
        var pickupField = new DataField<DateTime>("pickup");
        var fareField = new DataField<decimal>("fare");
        var schema = new ParquetSchema(pickupField, fareField);

        DateTime t0 = new(2026, 1, 15, 9, 30, 0, DateTimeKind.Utc);
        DateTime t1 = new(2026, 1, 15, 9, 45, 0, DateTimeKind.Utc);

        await using (Stream writeStream = File.Create(path))
        using (ParquetWriter writer = await ParquetWriter.CreateAsync(schema, writeStream))
        using (ParquetRowGroupWriter rg = writer.CreateRowGroup())
        {
            await rg.WriteColumnAsync(new DataColumn(pickupField, new DateTime[] { t0, t1 }));
            await rg.WriteColumnAsync(new DataColumn(fareField, new decimal[] { 12.50m, 7.25m }));
        }

        OpenParquetFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(path)], ctx), ctx);

        Assert.Equal(2, rows.Count);
        Assert.Equal(t0, rows[0]["pickup"].AsTimestamp());
        Assert.Equal(12.50m, rows[0]["fare"].AsDecimal());
        Assert.Equal(t1, rows[1]["pickup"].AsTimestamp());
        Assert.Equal(7.25m, rows[1]["fare"].AsDecimal());
    }

    [Fact]
    public async Task Open_MultipleRowGroups_StreamsAcrossThem()
    {
        // Write a fixture with 2 row groups (different sizes) and verify the
        // emitted row stream covers all rows across both groups.
        string path = TempParquet("multi-rg.parquet");
        var labelField = new DataField<int>("label");
        var schema = new ParquetSchema(labelField);

        await using (Stream writeStream = File.Create(path))
        using (ParquetWriter writer = await ParquetWriter.CreateAsync(schema, writeStream))
        {
            using (ParquetRowGroupWriter rg = writer.CreateRowGroup())
            {
                await rg.WriteColumnAsync(new DataColumn(labelField, new int[] { 10, 20, 30 }));
            }
            using (ParquetRowGroupWriter rg = writer.CreateRowGroup())
            {
                await rg.WriteColumnAsync(new DataColumn(labelField, new int[] { 40, 50 }));
            }
        }

        OpenParquetFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(path)], ctx), ctx);

        Assert.Equal(5, rows.Count);
        Assert.Equal(new int[] { 10, 20, 30, 40, 50 },
            rows.Select(r => r["label"].AsInt32()).ToArray());
    }

    // ───────────────────────── Fixtures + helpers ─────────────────────────

    private static async Task WriteClassificationFixture(string path)
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
