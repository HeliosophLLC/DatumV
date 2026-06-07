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

    // ──────────── datumv.* auto-typing round trip ────────────

    [Fact]
    public async Task ValidateArguments_OnTaggedFile_SurfacesTypedKindOnSchema()
    {
        // Write a Parquet file through the Heliosoph.DatumV export sink with
        // an Image column — that path attaches the datumv.kind/format/version
        // column-chunk metadata. open_parquet's plan-time peek should pick
        // up the tag and report the column on the SQL schema as
        // DataKind.Image (scalar) rather than UInt8 (array).
        string path = TempParquet("tagged-image.parquet");
        await WriteTaggedImageFixture(path);

        OpenParquetFunction fn = new();
        Schema schema = ((ITableValuedFunction)fn).ValidateArguments(
            argumentKinds: [DataKind.String],
            constantArguments: [Const(path)],
            constantStore: _constantStore,
            cancellationToken: default);

        Assert.Equal(2, schema.Columns.Count);
        Assert.Equal("id", schema.Columns[0].Name);
        Assert.Equal(DataKind.Int32, schema.Columns[0].Kind);
        Assert.Equal("pic", schema.Columns[1].Name);
        Assert.Equal(DataKind.Image, schema.Columns[1].Kind);
        Assert.False(schema.Columns[1].IsArray,
            "Tagged Image columns should surface as scalar Image, not UInt8[].");
    }

    [Fact]
    public async Task Open_TaggedFile_RowsCarryTypedKind()
    {
        string path = TempParquet("tagged-image-rows.parquet");
        await WriteTaggedImageFixture(path);

        OpenParquetFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(path)], ctx), ctx);

        Assert.Equal(2, rows.Count);
        DataValue pic0 = rows[0]["pic"];
        DataValue pic1 = rows[1]["pic"];
        Assert.Equal(DataKind.Image, pic0.Kind);
        Assert.Equal(DataKind.Image, pic1.Kind);
        // Passthrough format — the bytes survive verbatim.
        Assert.Equal(MakeFakePngBytes(0x10, 32), pic0.AsImage(ctx.Store));
        Assert.Equal(MakeFakePngBytes(0x20, 48), pic1.AsImage(ctx.Store));
    }

    [Fact]
    public async Task ValidateArguments_OnTaggedFile_WithTypedFalse_SkipsAutoTyping()
    {
        // typed=false: the metadata routing is bypassed and the column reads
        // as plain UInt8[] regardless of the datumv.kind tag. Useful for
        // recipes that want raw bytes for re-export to a different target.
        string path = TempParquet("typed-false.parquet");
        await WriteTaggedImageFixture(path);

        OpenParquetFunction fn = new();
        Schema schema = ((ITableValuedFunction)fn).ValidateArguments(
            argumentKinds: [DataKind.String, DataKind.Boolean],
            constantArguments: [Const(path), DataValue.FromBoolean(false)],
            constantStore: _constantStore,
            cancellationToken: default);

        Assert.Equal(DataKind.UInt8, schema.Columns[1].Kind);
        Assert.True(schema.Columns[1].IsArray);
    }

    [Fact]
    public async Task ValidateArguments_OnUntaggedFile_FallsBackToRawSchema()
    {
        // Backward compatibility: a Parquet file produced by any tool that
        // doesn't write the datumv.* keys (every external tool, plus pre-
        // metadata Heliosoph.DatumV builds) keeps reading as the raw column
        // type. No silent kind drift, no throw.
        string path = TempParquet("untagged.parquet");
        await WriteClassificationFixture(path);

        OpenParquetFunction fn = new();
        Schema schema = ((ITableValuedFunction)fn).ValidateArguments(
            argumentKinds: [DataKind.String],
            constantArguments: [Const(path)],
            constantStore: _constantStore,
            cancellationToken: default);

        Assert.Equal(DataKind.Int32, schema.Columns[0].Kind);
        Assert.Equal(DataKind.String, schema.Columns[1].Kind);
        Assert.Equal(DataKind.Float64, schema.Columns[2].Kind);
    }

    // ──────────── Scalar datumv.* round-trip ────────────

    [Fact]
    public async Task Open_TaggedDateColumn_RoundTripsAsDateKind()
    {
        // Slice A regression: DateOnly columns went out as Parquet INT32
        // logical Date and Parquet.Net lifted them back to DateTime on read,
        // which the column-type map narrowed to DataKind.Timestamp — the
        // source DataKind.Date was lost. The encoder now tags Date columns
        // with datumv.kind=Date, and open_parquet narrows the lifted
        // DateTime back to DateOnly so the SQL kind survives the round trip.
        DateOnly birthday = new(1991, 4, 7);
        string path = TempParquet("date-roundtrip.parquet");
        await WriteSingleColumnTaggedFixture(
            path, "day", DataKind.Date, [DataValue.FromDate(birthday)]);

        OpenParquetFunction fn = new();
        Schema schema = ((ITableValuedFunction)fn).ValidateArguments(
            argumentKinds: [DataKind.String],
            constantArguments: [Const(path)],
            constantStore: _constantStore,
            cancellationToken: default);

        Assert.Equal(DataKind.Date, schema.Columns[0].Kind);

        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(path)], ctx), ctx);

        Assert.Single(rows);
        Assert.Equal(DataKind.Date, rows[0]["day"].Kind);
        Assert.Equal(birthday, rows[0]["day"].AsDate());
    }

    [Fact]
    public async Task Open_TaggedTimestampTzColumn_RoundTripsAsTimestampTzKind()
    {
        // Slice A regression: TimestampTz columns went out as Parquet
        // TIMESTAMP isAdjustedToUTC=true, surfaced as a UTC DateTime on read,
        // and the column-type map landed on DataKind.Timestamp. The instant
        // survives; the source DataKind.TimestampTz didn't. The encoder now
        // tags TimestampTz columns and open_parquet rebuilds a UTC-offset
        // DateTimeOffset so the SQL kind round-trips.
        DateTimeOffset event_ = new(2024, 6, 15, 9, 30, 0, TimeSpan.FromHours(-4));
        string path = TempParquet("timestamptz-roundtrip.parquet");
        await WriteSingleColumnTaggedFixture(
            path, "event_ts", DataKind.TimestampTz, [DataValue.FromTimestampTz(event_)]);

        OpenParquetFunction fn = new();
        Schema schema = ((ITableValuedFunction)fn).ValidateArguments(
            argumentKinds: [DataKind.String],
            constantArguments: [Const(path)],
            constantStore: _constantStore,
            cancellationToken: default);

        Assert.Equal(DataKind.TimestampTz, schema.Columns[0].Kind);

        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(path)], ctx), ctx);

        Assert.Single(rows);
        Assert.Equal(DataKind.TimestampTz, rows[0]["event_ts"].Kind);
        // Parquet TIMESTAMP isAdjustedToUTC stores the instant only — the
        // original wall-clock offset isn't preserved on disk. Compare on
        // UtcDateTime so the round trip is exact on the instant.
        Assert.Equal(event_.UtcDateTime, rows[0]["event_ts"].AsTimestampTz().UtcDateTime);
    }

    // ───────────────────────── STRUCT columns ─────────────────────────

    [Fact]
    public async Task ValidateArguments_OnStructColumn_SurfacesStructKindWithChildFields()
    {
        // HuggingFace-shape bounding box: top-level `bbox` struct with four
        // float children. The plan-time schema peek should surface a Struct
        // ColumnInfo whose Fields list the children with their typed kinds —
        // recipes that build a query like `SELECT bbox.x FROM open_parquet(...)`
        // type-check against this without re-opening the file.
        string path = TempParquet("struct-bbox.parquet");
        await WriteBoundingBoxStructFixture(path);

        OpenParquetFunction fn = new();
        Schema schema = ((ITableValuedFunction)fn).ValidateArguments(
            argumentKinds: [DataKind.String],
            constantArguments: [Const(path)],
            constantStore: _constantStore,
            cancellationToken: default);

        Assert.Equal(2, schema.Columns.Count);
        Assert.Equal("id", schema.Columns[0].Name);
        Assert.Equal(DataKind.Int32, schema.Columns[0].Kind);

        Assert.Equal("bbox", schema.Columns[1].Name);
        Assert.Equal(DataKind.Struct, schema.Columns[1].Kind);
        IReadOnlyList<ColumnInfo> fields = schema.Columns[1].Fields
            ?? throw new InvalidOperationException("struct column should carry Fields metadata.");
        Assert.Equal(4, fields.Count);
        Assert.Equal("x", fields[0].Name);
        Assert.Equal(DataKind.Float32, fields[0].Kind);
        Assert.Equal("y", fields[1].Name);
        Assert.Equal("w", fields[2].Name);
        Assert.Equal("h", fields[3].Name);
    }

    [Fact]
    public async Task Open_StructColumn_RowsCarryFromStructWithPositionalFields()
    {
        // End-to-end read: each row's bbox cell is a Struct DataValue carrying
        // the four float fields in declaration order.
        string path = TempParquet("struct-bbox-rows.parquet");
        await WriteBoundingBoxStructFixture(path);

        OpenParquetFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectAsync(
            ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(path)], ctx), ctx);

        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rows[0]["id"].AsInt32());
        Assert.Equal(2, rows[1]["id"].AsInt32());

        DataValue bbox0 = rows[0]["bbox"];
        DataValue bbox1 = rows[1]["bbox"];
        Assert.Equal(DataKind.Struct, bbox0.Kind);
        Assert.Equal(DataKind.Struct, bbox1.Kind);
        // Non-zero TypeId proves the per-query TypeRegistry interned the
        // struct shape — downstream `typeof()` and field-by-name access rely
        // on this. The exact value isn't asserted because it's registry-
        // local; just that it's set.
        Assert.NotEqual(0, bbox0.TypeId);

        DataValue[] fields0 = bbox0.AsStruct(ctx.Store);
        Assert.Equal(4, fields0.Length);
        Assert.Equal(10f, fields0[0].AsFloat32());
        Assert.Equal(20f, fields0[1].AsFloat32());
        Assert.Equal(30f, fields0[2].AsFloat32());
        Assert.Equal(40f, fields0[3].AsFloat32());

        DataValue[] fields1 = bbox1.AsStruct(ctx.Store);
        Assert.Equal(50f, fields1[0].AsFloat32());
        Assert.Equal(60f, fields1[1].AsFloat32());
        Assert.Equal(70f, fields1[2].AsFloat32());
        Assert.Equal(80f, fields1[3].AsFloat32());
    }

    [Fact]
    public async Task ValidateArguments_OnNestedStructColumn_ThrowsClearError()
    {
        // v1 only supports one level of struct nesting. A deeper schema
        // (struct-of-struct) should fail at validation with a column-named
        // error so the caller knows to flatten or skip the column.
        string path = TempParquet("nested-struct.parquet");
        var pField = new DataField<float>("p");
        var nestedInner = new StructField("inner", pField);
        var nestedOuter = new StructField("outer", nestedInner);
        var schemaWrite = new ParquetSchema(nestedOuter);

        await using (Stream writeStream = File.Create(path))
        using (ParquetWriter writer = await ParquetWriter.CreateAsync(schemaWrite, writeStream))
        using (ParquetRowGroupWriter rg = writer.CreateRowGroup())
        {
            await rg.WriteColumnAsync(new DataColumn(pField, new float[] { 0.5f }));
        }

        OpenParquetFunction fn = new();
        FunctionArgumentException ex = Assert.Throws<FunctionArgumentException>(() =>
            ((ITableValuedFunction)fn).ValidateArguments(
                argumentKinds: [DataKind.String],
                constantArguments: [Const(path)],
                constantStore: _constantStore,
                cancellationToken: default));
        Assert.Contains("outer", ex.Message);
        Assert.Contains("nested", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Writes the COCO-shape bounding-box fixture: one Int32 <c>id</c>
    /// column and a top-level <c>bbox</c> struct with four Float32 children
    /// (<c>x, y, w, h</c>). Two rows so the per-row struct assembly path
    /// gets exercised.
    /// </summary>
    private static async Task WriteBoundingBoxStructFixture(string path)
    {
        var idField = new DataField<int>("id");
        var xField = new DataField<float>("x");
        var yField = new DataField<float>("y");
        var wField = new DataField<float>("w");
        var hField = new DataField<float>("h");
        var bboxField = new StructField("bbox", xField, yField, wField, hField);
        var schemaWrite = new ParquetSchema(idField, bboxField);

        await using Stream writeStream = File.Create(path);
        using ParquetWriter writer = await ParquetWriter.CreateAsync(schemaWrite, writeStream);
        using ParquetRowGroupWriter rg = writer.CreateRowGroup();

        await rg.WriteColumnAsync(new DataColumn(idField, new int[] { 1, 2 }));
        await rg.WriteColumnAsync(new DataColumn(xField, new float[] { 10f, 50f }));
        await rg.WriteColumnAsync(new DataColumn(yField, new float[] { 20f, 60f }));
        await rg.WriteColumnAsync(new DataColumn(wField, new float[] { 30f, 70f }));
        await rg.WriteColumnAsync(new DataColumn(hField, new float[] { 40f, 80f }));
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

    /// <summary>
    /// Writes a 2-row Parquet file through Heliosoph.DatumV's
    /// <see cref="ParquetExportSink"/> with an <c>id Int32</c> column and
    /// a <c>pic Image</c> column. The sink attaches datumv.kind/format/version
    /// column-chunk metadata to the Image column — that's the metadata
    /// <c>open_parquet</c> reads back to surface the column as a typed
    /// Image rather than a raw UInt8[].
    /// </summary>
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
        using RowBatch batch = pool.RentRowBatch(lookup, capacity: 2, arena: arena);
        batch.Add(
        [
            DataValue.FromInt32(1),
            DataValue.FromImage(MakeFakePngBytes(0x10, 32), arena),
        ]);
        batch.Add(
        [
            DataValue.FromInt32(2),
            DataValue.FromImage(MakeFakePngBytes(0x20, 48), arena),
        ]);

        ParquetExportFormat format = new();
        await using IExportSink sink = format.CreateSink(
            new ExportTarget.File(path),
            schema,
            [MediaDisposition.Inline, MediaDisposition.Inline],
            ExportOptions.Empty,
            registry);
        await sink.WriteAsync(batch, default);
        await sink.FinishAsync(default);
    }

    /// <summary>
    /// Writes a single-column Parquet file via <see cref="ParquetExportSink"/>
    /// with the column declared at <paramref name="kind"/>. The sink attaches
    /// the matching <c>datumv.kind</c> metadata for tagged kinds, which is
    /// the round-trip path the Date / TimestampTz tests exercise. Caller
    /// supplies the row values as a <see cref="DataValue"/> array — one row
    /// per entry.
    /// </summary>
    private async Task WriteSingleColumnTaggedFixture(
        string path, string columnName, DataKind kind, DataValue[] values)
    {
        Pool pool = CreatePool();
        SidecarRegistry registry = new();
        Schema schema = new([new ColumnInfo(columnName, kind, nullable: false)]);
        ColumnLookup lookup = new([columnName]);
        using Arena arena = new();
        using RowBatch batch = pool.RentRowBatch(lookup, capacity: values.Length, arena: arena);
        foreach (DataValue v in values)
        {
            batch.Add([v]);
        }

        ParquetExportFormat format = new();
        await using IExportSink sink = format.CreateSink(
            new ExportTarget.File(path), schema,
            [MediaDisposition.Inline], ExportOptions.Empty, registry);
        await sink.WriteAsync(batch, default);
        await sink.FinishAsync(default);
    }

    /// <summary>
    /// Returns a byte sequence that begins with the 8-byte PNG magic
    /// followed by <paramref name="length"/>−8 deterministic fill bytes.
    /// Lets the test assert byte-for-byte round-trip survival without
    /// constructing a real PNG (the Image-export path doesn't decode the
    /// payload — it carries bytes through verbatim).
    /// </summary>
    private static byte[] MakeFakePngBytes(byte seed, int length)
    {
        if (length < 8) throw new ArgumentOutOfRangeException(nameof(length));
        byte[] bytes = new byte[length];
        bytes[0] = 0x89; bytes[1] = 0x50; bytes[2] = 0x4E; bytes[3] = 0x47;
        bytes[4] = 0x0D; bytes[5] = 0x0A; bytes[6] = 0x1A; bytes[7] = 0x0A;
        for (int i = 8; i < length; i++) bytes[i] = (byte)(seed ^ i);
        return bytes;
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
