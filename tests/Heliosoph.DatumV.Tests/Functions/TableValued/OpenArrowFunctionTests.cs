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
/// <c>open_arrow(path)</c> table-valued function: opens an Apache Arrow
/// IPC / Feather v2 file and yields its rows with one column per
/// top-level field. Covers the constant-args validation hook (plan-time
/// peek surfaces the file's real schema), per-type row decoding for
/// primitive columns and 1-D array columns (List + FixedSizeList), and
/// the explicit failure modes (non-constant arg, missing file).
/// </summary>
public sealed class OpenArrowFunctionTests : ServiceTestBase, IDisposable
{
    private readonly ByteArrayValueStore _constantStore = new();
    private readonly string _scratchDir = Path.Combine(
        Path.GetTempPath(), $"open-arrow-{Guid.NewGuid():N}");

    public OpenArrowFunctionTests()
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
    private string TempArrow(string name) => Path.Combine(_scratchDir, name);

    // ───────────────────── Plan-time schema peek ─────────────────────

    [Fact]
    public async Task ValidateArguments_OnConstantPath_PeeksFileAndReturnsRealSchema()
    {
        string path = TempArrow("hf-classification.arrow");
        await WriteClassificationFixture(path);

        OpenArrowFunction fn = new();
        HelioSchema schema = ((ITableValuedFunction)fn).ValidateArguments(
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
        OpenArrowFunction fn = new();
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
        OpenArrowFunction fn = new();
        FunctionArgumentException ex = Assert.Throws<FunctionArgumentException>(() =>
            ((ITableValuedFunction)fn).ValidateArguments(
                argumentKinds: [DataKind.String],
                constantArguments: [Const("/no/such.arrow")],
                constantStore: _constantStore,
                cancellationToken: default));
        Assert.Contains("not found", ex.Message);
    }

    // ───────────────────── Runtime row decode ─────────────────────

    [Fact]
    public async Task Open_PrimitiveColumns_DecodesEachRowWithTypedFields()
    {
        string path = TempArrow("primitives.arrow");
        await WriteClassificationFixture(path);

        OpenArrowFunction fn = new();
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
    public async Task Open_ListArrayColumn_DecodesPerRowSlicesAsTypedArrays()
    {
        // Token-sequence shape: two rows, [101, 202, 303] and [404, 505].
        string path = TempArrow("tokens.arrow");
        var schema = new ArrowSchema.Builder()
            .Field(f => f.Name("tokens").DataType(new ListType(Int32Type.Default)).Nullable(false))
            .Build();

        var valuesArr = new Int32Array.Builder().AppendRange([101, 202, 303, 404, 505]).Build();
        // Offsets: row 0 covers [0,3), row 1 covers [3,5).
        ArrowBuffer offsets = new ArrowBuffer.Builder<int>()
            .Append(0).Append(3).Append(5)
            .Build();
        ArrowBuffer validity = ArrowBuffer.Empty;
        var listArr = new ListArray(
            new ListType(Int32Type.Default), length: 2, offsets, valuesArr, validity, nullCount: 0);

        using var batch = new RecordBatch(schema, new IArrowArray[] { listArr }, length: 2);
        await using (Stream s = File.Create(path))
        using (var w = new ArrowFileWriter(s, schema))
        {
            await w.WriteRecordBatchAsync(batch);
            await w.WriteEndAsync();
        }

        OpenArrowFunction fn = new();
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
    public async Task Open_FixedSizeListColumn_DecodesEachRowAsFixedLengthArray()
    {
        // Embedding shape: two rows, [1.0, 2.0, 3.0, 4.0] each (FixedSize=4).
        string path = TempArrow("embeddings.arrow");
        var fslType = new FixedSizeListType(FloatType.Default, listSize: 4);
        var schema = new ArrowSchema.Builder()
            .Field(f => f.Name("embedding").DataType(fslType).Nullable(false))
            .Build();

        var valuesArr = new FloatArray.Builder().AppendRange(
            [1.0f, 2.0f, 3.0f, 4.0f, 5.0f, 6.0f, 7.0f, 8.0f]).Build();
        var fslArr = new FixedSizeListArray(
            fslType, length: 2, valuesArr, ArrowBuffer.Empty, nullCount: 0);

        using var batch = new RecordBatch(schema, new IArrowArray[] { fslArr }, length: 2);
        await using (Stream s = File.Create(path))
        using (var w = new ArrowFileWriter(s, schema))
        {
            await w.WriteRecordBatchAsync(batch);
            await w.WriteEndAsync();
        }

        OpenArrowFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(path)], ctx), ctx);

        Assert.Equal(2, rows.Count);
        ReadOnlySpan<float> row0 = rows[0]["embedding"].AsArraySpan<float>(ctx.Store);
        Assert.Equal(new float[] { 1.0f, 2.0f, 3.0f, 4.0f }, row0.ToArray());
        ReadOnlySpan<float> row1 = rows[1]["embedding"].AsArraySpan<float>(ctx.Store);
        Assert.Equal(new float[] { 5.0f, 6.0f, 7.0f, 8.0f }, row1.ToArray());
    }

    [Fact]
    public async Task Open_TemporalAndDecimalColumns_DecodeAsTypedScalars()
    {
        // NYC-taxi-shaped: a tz-aware Timestamp pickup + a Decimal fare. Apache.Arrow
        // surfaces these as TimestampArray (with timezone metadata) and Decimal128Array;
        // the row decoder needs scalar arms wired for both.
        string path = TempArrow("trips.arrow");
        var fareType = new Decimal128Type(precision: 10, scale: 2);
        var schema = new ArrowSchema.Builder()
            .Field(f => f.Name("pickup").DataType(new TimestampType(TimeUnit.Microsecond, "UTC")).Nullable(false))
            .Field(f => f.Name("fare").DataType(fareType).Nullable(false))
            .Build();

        DateTimeOffset t0 = new(2026, 1, 15, 9, 30, 0, TimeSpan.Zero);
        DateTimeOffset t1 = new(2026, 1, 15, 9, 45, 0, TimeSpan.Zero);
        var pickupArr = new TimestampArray.Builder(TimeUnit.Microsecond).Append(t0).Append(t1).Build();
        var fareArr = new Decimal128Array.Builder(fareType).Append(12.50m).Append(7.25m).Build();
        using var batch = new RecordBatch(schema, new IArrowArray[] { pickupArr, fareArr }, length: 2);
        await using (Stream s = File.Create(path))
        using (var w = new ArrowFileWriter(s, schema))
        {
            await w.WriteRecordBatchAsync(batch);
            await w.WriteEndAsync();
        }

        OpenArrowFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(path)], ctx), ctx);

        Assert.Equal(2, rows.Count);
        Assert.Equal(t0, rows[0]["pickup"].AsTimestampTz());
        Assert.Equal(12.50m, rows[0]["fare"].AsDecimal());
        Assert.Equal(t1, rows[1]["pickup"].AsTimestampTz());
        Assert.Equal(7.25m, rows[1]["fare"].AsDecimal());
    }

    [Fact]
    public async Task Open_MultipleRecordBatches_StreamsAcrossThem()
    {
        // Write a fixture with 2 record batches (different sizes) and verify
        // the emitted row stream covers all rows across both batches.
        string path = TempArrow("multi-batch.arrow");
        var schema = new ArrowSchema.Builder()
            .Field(f => f.Name("label").DataType(Int32Type.Default).Nullable(false))
            .Build();

        var b1 = new Int32Array.Builder().AppendRange([10, 20, 30]).Build();
        var b2 = new Int32Array.Builder().AppendRange([40, 50]).Build();
        using var batch1 = new RecordBatch(schema, new IArrowArray[] { b1 }, length: 3);
        using var batch2 = new RecordBatch(schema, new IArrowArray[] { b2 }, length: 2);
        await using (Stream s = File.Create(path))
        using (var w = new ArrowFileWriter(s, schema))
        {
            await w.WriteRecordBatchAsync(batch1);
            await w.WriteRecordBatchAsync(batch2);
            await w.WriteEndAsync();
        }

        OpenArrowFunction fn = new();
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
