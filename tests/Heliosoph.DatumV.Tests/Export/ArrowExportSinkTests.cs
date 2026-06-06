using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Catalog.Plans;
using Heliosoph.DatumV.Catalog.Providers;
using Heliosoph.DatumV.Export;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Serialization.Arrow;
using Heliosoph.DatumV.Pooling;
using Heliosoph.DatumV.Serialization.Parquet;
using ArrowFormat = Heliosoph.DatumV.Export.Arrow.ArrowExportFormat;
using CborJsonCodecRef = Heliosoph.DatumV.Functions.Json.CborJsonCodec;
using HelioSchema = Heliosoph.DatumV.Model.Schema;
using SidecarRegistryRef = Heliosoph.DatumV.DatumFile.Sidecar.SidecarRegistry;

namespace Heliosoph.DatumV.Tests.Export;

/// <summary>
/// End-to-end exercise of <c>COPY (query) TO 'path.arrow'</c> — the
/// parser, <see cref="ExportPlan"/>, <see cref="ArrowFormat"/>,
/// and <see cref="Export.Arrow.ArrowExportSink"/>. Round-trip via the
/// <c>open_arrow</c> TVF is the headline check: anything the writer
/// emits in a supported shape must re-import losslessly through the
/// reader. Field metadata (<c>datumv.*</c>) is also pinned so a future
/// read-side enhancement that retypes columns will be lit by these
/// tests.
/// </summary>
public sealed class ArrowExportSinkTests : ServiceTestBase, IDisposable
{
    private readonly string _scratchDir;

    public ArrowExportSinkTests()
    {
        _scratchDir = Path.Combine(Path.GetTempPath(), $"datum-arrow-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchDir);
    }

    public new void Dispose()
    {
        base.Dispose();
        try { if (Directory.Exists(_scratchDir)) Directory.Delete(_scratchDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task CopyToArrow_ScalarsOnly_RoundTripsThroughOpenArrow()
    {
        // The headline round-trip: write scalars, re-open via open_arrow,
        // compare row counts and per-row values. Anchors the writer-reader
        // pairing so any future drift in scalar encoding is caught here.
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.scalars",
            columns: ["id", "name", "score"],
            columnKinds: [DataKind.Int32, DataKind.String, DataKind.Float64],
            rows:
            [
                [1, "alice", 0.10],
                [2, "bob",   0.55],
                [3, "carol", 0.95],
            ]));

        string outPath = Path.Combine(_scratchDir, "scalars.arrow");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT id, name, score FROM public.scalars) TO '{EscapeSql(outPath)}'");
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.True(File.Exists(outPath));

        // Confirm the file has actual data before exercising open_arrow.
        // A 0-row file would show up as an empty round-trip downstream,
        // which is harder to root-cause.
        await using (FileStream fs = File.OpenRead(outPath))
        using (ArrowFileReader directReader = new(fs))
        {
            using RecordBatch? rb = await directReader.ReadNextRecordBatchAsync();
            Assert.NotNull(rb);
            Assert.Equal(3, rb!.Length);
        }

        // Round-trip via open_arrow. The function reads the file's real
        // schema and surfaces a typed projection. Materialise the per-row
        // values inline while each batch is still live — the catalog
        // recycles batches after the enumerator advances past them, and
        // values that point at recycled-arena memory read as empty.
        List<int> ids = [];
        List<string> names = [];
        List<double> scores = [];
        using TableCatalog readCatalog = CreateCatalog();
        StatementPlan readPlan = await readCatalog.PlanAsync(
            $"SELECT id, name, score FROM open_arrow('{EscapeSql(outPath)}')");
        await foreach (RowBatch b in readCatalog.ExecuteAsync(readPlan))
        {
            b.ColumnLookup.TryGetColumnOrdinal("id", out int idCol);
            b.ColumnLookup.TryGetColumnOrdinal("name", out int nameCol);
            b.ColumnLookup.TryGetColumnOrdinal("score", out int scoreCol);
            for (int r = 0; r < b.Count; r++)
            {
                ids.Add(b[r][idCol].AsInt32());
                names.Add(b[r][nameCol].AsString(b.Arena));
                scores.Add(b[r][scoreCol].AsFloat64());
            }
        }
        Assert.Equal(new[] { 1, 2, 3 }, ids);
        Assert.Equal(new[] { "alice", "bob", "carol" }, names);
        Assert.Equal(new[] { 0.10, 0.55, 0.95 }, scores);
    }

    [Fact]
    public async Task CopyToArrow_NullValues_SurviveRoundTrip()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.nullable",
            columns: ["id", "name"],
            columnKinds: [DataKind.Int32, DataKind.String],
            rows: [[1, "alice"], [2, null], [null, "carol"]]));

        string outPath = Path.Combine(_scratchDir, "nullable.arrow");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT id, name FROM public.nullable) TO '{EscapeSql(outPath)}'");
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        // Process in-place — see the scalars test for why we don't
        // collect batches across the enumerator boundary.
        List<(bool idNull, int idVal, bool nameNull)> rows = [];
        using TableCatalog readCatalog = CreateCatalog();
        StatementPlan readPlan = await readCatalog.PlanAsync(
            $"SELECT id, name FROM open_arrow('{EscapeSql(outPath)}')");
        await foreach (RowBatch b in readCatalog.ExecuteAsync(readPlan))
        {
            b.ColumnLookup.TryGetColumnOrdinal("id", out int idCol);
            b.ColumnLookup.TryGetColumnOrdinal("name", out int nameCol);
            for (int r = 0; r < b.Count; r++)
            {
                DataValue idV = b[r][idCol];
                DataValue nameV = b[r][nameCol];
                rows.Add((idV.IsNull, idV.IsNull ? 0 : idV.AsInt32(), nameV.IsNull));
            }
        }
        Assert.Equal(3, rows.Count);
        Assert.False(rows[0].idNull); Assert.Equal(1, rows[0].idVal); Assert.False(rows[0].nameNull);
        Assert.False(rows[1].idNull); Assert.Equal(2, rows[1].idVal); Assert.True(rows[1].nameNull);
        Assert.True(rows[2].idNull); Assert.False(rows[2].nameNull);
    }

    [Fact]
    public async Task CopyToArrow_TemporalAndDecimalKinds_RoundTrip()
    {
        DateTimeOffset pickup = new(2026, 1, 15, 9, 30, 0, TimeSpan.Zero);
        DateOnly birthday = new(1991, 4, 7);
        decimal fare = 12.50m;

        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.trips",
            columns: ["pickup", "birthday", "fare"],
            columnKinds: [DataKind.TimestampTz, DataKind.Date, DataKind.Decimal],
            rows: [[pickup, birthday, fare]]));

        string outPath = Path.Combine(_scratchDir, "trips.arrow");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT pickup, birthday, fare FROM public.trips) TO '{EscapeSql(outPath)}'");
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        DateTimeOffset? gotPickup = null;
        DateOnly? gotBirthday = null;
        decimal? gotFare = null;
        using TableCatalog readCatalog = CreateCatalog();
        StatementPlan readPlan = await readCatalog.PlanAsync(
            $"SELECT pickup, birthday, fare FROM open_arrow('{EscapeSql(outPath)}')");
        await foreach (RowBatch b in readCatalog.ExecuteAsync(readPlan))
        {
            b.ColumnLookup.TryGetColumnOrdinal("pickup", out int pCol);
            b.ColumnLookup.TryGetColumnOrdinal("birthday", out int bCol);
            b.ColumnLookup.TryGetColumnOrdinal("fare", out int fCol);
            for (int r = 0; r < b.Count; r++)
            {
                gotPickup = b[r][pCol].AsTimestampTz();
                gotBirthday = b[r][bCol].AsDate();
                gotFare = b[r][fCol].AsDecimal();
            }
        }
        Assert.Equal(pickup, gotPickup);
        Assert.Equal(birthday, gotBirthday);
        Assert.Equal(fare, gotFare);
    }

    [Fact]
    public async Task CopyToArrow_Float32ListColumn_RoundTrips()
    {
        // The canonical embedding shape — Array<Float32>. Writes via
        // ListArray, reads back via the reader's ListArray scalar arm.
        Pool pool = CreatePool();
        SidecarRegistryRef registry = new();

        HelioSchema schema = new(
        [
            new ColumnInfo("id", DataKind.Int32, nullable: false),
            new ColumnInfo("embedding", DataKind.Float32, nullable: false) { IsArray = true },
        ]);
        ColumnLookup lookup = new(["id", "embedding"]);
        using Arena arena = new();
        using RowBatch batch = pool.RentRowBatch(lookup, capacity: 2, arena: arena);
        batch.Add(
        [
            DataValue.FromInt32(1),
            DataValue.FromArenaArray([0.1f, 0.2f, 0.3f], DataKind.Float32, arena),
        ]);
        batch.Add(
        [
            DataValue.FromInt32(2),
            DataValue.FromArenaArray([0.4f, 0.5f], DataKind.Float32, arena),
        ]);

        string outPath = Path.Combine(_scratchDir, "embeddings.arrow");
        ArrowFormat format = new();
        await using (IExportSink sink = format.CreateSink(
            new ExportTarget.File(outPath),
            schema,
            [MediaDisposition.Inline, MediaDisposition.Inline],
            ExportOptions.Empty,
            registry))
        {
            await sink.WriteAsync(batch, default);
            await sink.FinishAsync(default);
        }

        // Round-trip via the SQL surface — open_arrow ↔ same engine.
        // Materialise inline because the engine recycles batches as the
        // enumerator advances; copying the float spans to owned arrays
        // here is safe regardless.
        List<int> ids = [];
        List<float[]> embeddings = [];
        using TableCatalog readCatalog = CreateCatalog();
        StatementPlan readPlan = await readCatalog.PlanAsync(
            $"SELECT id, embedding FROM open_arrow('{EscapeSql(outPath)}')");
        await foreach (RowBatch b in readCatalog.ExecuteAsync(readPlan))
        {
            b.ColumnLookup.TryGetColumnOrdinal("id", out int idCol);
            b.ColumnLookup.TryGetColumnOrdinal("embedding", out int embCol);
            for (int r = 0; r < b.Count; r++)
            {
                ids.Add(b[r][idCol].AsInt32());
                embeddings.Add(b[r][embCol].AsArraySpan<float>(b.Arena).ToArray());
            }
        }
        Assert.Equal(new[] { 1, 2 }, ids);
        Assert.Equal(2, embeddings.Count);
        Assert.Equal(new[] { 0.1f, 0.2f, 0.3f }, embeddings[0]);
        Assert.Equal(new[] { 0.4f, 0.5f }, embeddings[1]);
    }

    [Fact]
    public async Task CopyToArrow_TypedMediaFieldMetadata_IsWritten()
    {
        // Image / Audio / Video / Mesh / PointCloud / Json carry the
        // datumv.* tags on the Arrow field's custom metadata. open_arrow
        // doesn't read them back yet, but the file should be unambiguous
        // when inspected directly. Pin the metadata shape so a future
        // read-side enhancement lights up cleanly.
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.samples",
            columns: ["id", "pic"],
            columnKinds: [DataKind.Int32, DataKind.Image],
            rows: [[1, new byte[] { 0x89, 0x50, 0x4E, 0x47 }]]));

        string outPath = Path.Combine(_scratchDir, "samples.arrow");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT id, pic FROM public.samples) TO '{EscapeSql(outPath)}'");
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        // Read the field metadata directly via Apache.Arrow.
        await using FileStream fs = File.OpenRead(outPath);
        using ArrowFileReader reader = new(fs);
        Apache.Arrow.Field picField = reader.Schema.GetFieldByName("pic")!;
        Assert.NotNull(picField.Metadata);
        Assert.Equal("Image", picField.Metadata[ParquetDatumvMetadata.KindKey]);
        Assert.Equal("passthrough", picField.Metadata[ParquetDatumvMetadata.FormatKey]);
        Assert.Equal(ParquetDatumvMetadata.CurrentVersion, picField.Metadata[ParquetDatumvMetadata.VersionKey]);

        // Image bytes themselves survive as Binary; the reader surfaces
        // them as UInt8 byte arrays today. Pin the bytes so a future
        // datumv-routing read path doesn't accidentally drop the payload.
        using RecordBatch arrowBatch = await reader.ReadNextRecordBatchAsync();
        BinaryArray picBytes = (BinaryArray)arrowBatch.Column("pic");
        Assert.Equal(1, picBytes.Length);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, picBytes.GetBytes(0).ToArray());
    }

    [Fact]
    public async Task CopyToArrow_JsonColumn_DecodesCborToJsonText()
    {
        // Json column is CBOR on the wire; the Arrow sink must decode it
        // to JSON text in a String column, with the datumv tag so a
        // future read-side enhancement can re-encode to CBOR on import.
        Pool pool = CreatePool();
        SidecarRegistryRef registry = new();

        HelioSchema schema = new(
        [
            new ColumnInfo("meta", DataKind.Json, nullable: false),
        ]);
        ColumnLookup lookup = new(["meta"]);
        using Arena arena = new();
        using RowBatch batch = pool.RentRowBatch(lookup, capacity: 1, arena: arena);
        byte[] cbor = CborJsonCodecRef.EncodeFromJsonText("{\"count\":7,\"tag\":\"a\"}");
        batch.Add([DataValue.FromJson(cbor, arena)]);

        string outPath = Path.Combine(_scratchDir, "json-col.arrow");
        ArrowFormat format = new();
        await using (IExportSink sink = format.CreateSink(
            new ExportTarget.File(outPath),
            schema,
            [MediaDisposition.Inline],
            ExportOptions.Empty,
            registry))
        {
            await sink.WriteAsync(batch, default);
            await sink.FinishAsync(default);
        }

        // Inspect via Apache.Arrow directly — open_arrow doesn't yet
        // retype Json columns. The on-disk shape is JSON text.
        await using FileStream fs = File.OpenRead(outPath);
        using ArrowFileReader reader = new(fs);
        Apache.Arrow.Field metaField = reader.Schema.GetFieldByName("meta")!;
        Assert.Equal("Json", metaField.Metadata[ParquetDatumvMetadata.KindKey]);
        Assert.Equal("text", metaField.Metadata[ParquetDatumvMetadata.FormatKey]);

        using RecordBatch arrowBatch = await reader.ReadNextRecordBatchAsync();
        StringArray metaText = (StringArray)arrowBatch.Column("meta");
        string text = metaText.GetString(0);
        // Order-insensitive check: JSON.NET canonicalisation may reorder
        // keys depending on the codec. Both expected keys present is enough.
        Assert.Contains("\"count\"", text);
        Assert.Contains("\"tag\"", text);
    }

    [Fact]
    public async Task CopyToArrow_StructColumn_RoundTripsThroughOpenArrow()
    {
        // Headline struct round-trip: write a struct column, re-import
        // via open_arrow, confirm the field shape + values survive with
        // real names (city, zip) — not f0/f1.
        Pool pool = CreatePool();
        SidecarRegistryRef registry = new();

        HelioSchema schema = new(
        [
            new ColumnInfo("id", DataKind.Int32, nullable: false),
            new ColumnInfo("loc", nullable: false, fields:
            [
                new ColumnInfo("city", DataKind.String, nullable: false),
                new ColumnInfo("zip", DataKind.Int32, nullable: false),
            ]),
        ]);
        ColumnLookup lookup = new(["id", "loc"]);
        using Arena arena = new();
        using RowBatch batch = pool.RentRowBatch(lookup, capacity: 2, arena: arena);
        batch.Add(
        [
            DataValue.FromInt32(1),
            DataValue.FromUntypedStruct(
                [DataValue.FromString("Boston", arena), DataValue.FromInt32(2115)], arena),
        ]);
        batch.Add(
        [
            DataValue.FromInt32(2),
            DataValue.FromUntypedStruct(
                [DataValue.FromString("Cambridge", arena), DataValue.FromInt32(2139)], arena),
        ]);

        string outPath = Path.Combine(_scratchDir, "structs.arrow");
        ArrowFormat format = new();
        await using (IExportSink sink = format.CreateSink(
            new ExportTarget.File(outPath),
            schema,
            [MediaDisposition.Inline, MediaDisposition.Inline],
            ExportOptions.Empty,
            registry))
        {
            await sink.WriteAsync(batch, default);
            await sink.FinishAsync(default);
        }

        // Re-read via open_arrow. The struct should land as
        // DataKind.Struct with ColumnInfo.Fields populated by the
        // child Arrow field names.
        List<(int id, string city, int zip)> got = [];
        using TableCatalog readCatalog = CreateCatalog();
        StatementPlan readPlan = await readCatalog.PlanAsync(
            $"SELECT id, loc FROM open_arrow('{EscapeSql(outPath)}')");
        await foreach (RowBatch b in readCatalog.ExecuteAsync(readPlan))
        {
            b.ColumnLookup.TryGetColumnOrdinal("id", out int idCol);
            b.ColumnLookup.TryGetColumnOrdinal("loc", out int locCol);
            for (int r = 0; r < b.Count; r++)
            {
                DataValue locV = b[r][locCol];
                Assert.Equal(DataKind.Struct, locV.Kind);
                DataValue[] fields = locV.AsStruct(b.Arena);
                Assert.Equal(2, fields.Length);
                got.Add((
                    b[r][idCol].AsInt32(),
                    fields[0].AsString(b.Arena),
                    fields[1].AsInt32()));
            }
        }
        Assert.Equal(2, got.Count);
        Assert.Equal((1, "Boston", 2115), got[0]);
        Assert.Equal((2, "Cambridge", 2139), got[1]);
    }

    [Fact]
    public async Task CopyToArrow_StructFieldNames_SurfaceThroughColumnLookup()
    {
        // ColumnInfo.Fields recovered from the Arrow file's StructType
        // children. A SELECT against the round-tripped column should
        // surface the field names from the original schema, not f0/f1.
        Pool pool = CreatePool();
        SidecarRegistryRef registry = new();

        HelioSchema schema = new(
        [
            new ColumnInfo("p", nullable: false, fields:
            [
                new ColumnInfo("x", DataKind.Float32, nullable: false),
                new ColumnInfo("y", DataKind.Float32, nullable: false),
            ]),
        ]);
        ColumnLookup lookup = new(["p"]);
        using Arena arena = new();
        using RowBatch batch = pool.RentRowBatch(lookup, capacity: 1, arena: arena);
        batch.Add(
        [
            DataValue.FromUntypedStruct(
                [DataValue.FromFloat32(1.5f), DataValue.FromFloat32(2.5f)], arena),
        ]);

        string outPath = Path.Combine(_scratchDir, "named-struct.arrow");
        ArrowFormat format = new();
        await using (IExportSink sink = format.CreateSink(
            new ExportTarget.File(outPath),
            schema,
            [MediaDisposition.Inline],
            ExportOptions.Empty,
            registry))
        {
            await sink.WriteAsync(batch, default);
            await sink.FinishAsync(default);
        }

        // Inspect the on-disk Arrow schema directly — the round-trip
        // test above proves values flow through correctly; here we pin
        // that the file's StructType carries the right child field
        // names so external Arrow consumers (pandas, Polars) see them
        // as `p.x` / `p.y`, not `p.f0` / `p.f1`.
        await using FileStream fs = File.OpenRead(outPath);
        using ArrowFileReader reader = new(fs);
        Apache.Arrow.Field pField = reader.Schema.GetFieldByName("p")!;
        Apache.Arrow.Types.StructType pType = Assert.IsType<Apache.Arrow.Types.StructType>(pField.DataType);
        Assert.Equal(2, pType.Fields.Count);
        Assert.Equal("x", pType.Fields[0].Name);
        Assert.Equal("y", pType.Fields[1].Name);
    }

    [Fact]
    public async Task CopyToArrow_ImageColumn_RoundTripsViaOpenArrow()
    {
        // Headline round-trip for typed media: an Image column written
        // through COPY must re-import as DataKind.Image (not raw
        // Array<UInt8>) through open_arrow, with bytes byte-identical.
        // Regression for the BinaryArray-as-UInt8Array cast bug and the
        // missing datumv.* routing on the read side.
        byte[] png1 = MakeFakeImageBytes(0xAA, 64);
        byte[] png2 = MakeFakeImageBytes(0xBB, 128);

        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.samples",
            columns: ["id", "pic"],
            columnKinds: [DataKind.Int32, DataKind.Image],
            rows: [[1, png1], [2, png2]]));

        string outPath = Path.Combine(_scratchDir, "images.arrow");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT id, pic FROM public.samples) TO '{EscapeSql(outPath)}'");
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        // Re-open via open_arrow; the projection schema must surface
        // 'pic' as DataKind.Image, and per-row reads must yield the
        // original PNG bytes through DataValue.AsImage.
        List<(int id, byte[] bytes)> got = [];
        SidecarRegistryRef registry = new();
        using TableCatalog readCatalog = CreateCatalog();
        StatementPlan readPlan = await readCatalog.PlanAsync(
            $"SELECT id, pic FROM open_arrow('{EscapeSql(outPath)}')");
        await foreach (RowBatch b in readCatalog.ExecuteAsync(readPlan))
        {
            b.ColumnLookup.TryGetColumnOrdinal("id", out int idCol);
            b.ColumnLookup.TryGetColumnOrdinal("pic", out int picCol);
            for (int r = 0; r < b.Count; r++)
            {
                DataValue picV = b[r][picCol];
                Assert.Equal(DataKind.Image, picV.Kind);
                got.Add((b[r][idCol].AsInt32(), picV.AsImage(b.Arena, registry)));
            }
        }
        Assert.Equal(2, got.Count);
        Assert.Equal(1, got[0].id);
        Assert.Equal(png1, got[0].bytes);
        Assert.Equal(2, got[1].id);
        Assert.Equal(png2, got[1].bytes);
    }

    [Fact]
    public async Task CopyToArrow_JsonColumn_RoundTripsViaOpenArrow()
    {
        // Json columns are CBOR in the engine, JSON text in the Arrow
        // file, CBOR again after open_arrow's datumv route re-encodes.
        Pool pool = CreatePool();
        SidecarRegistryRef registry = new();

        HelioSchema schema = new(
        [
            new ColumnInfo("meta", DataKind.Json, nullable: false),
        ]);
        ColumnLookup lookup = new(["meta"]);
        using Arena arena = new();
        using RowBatch batch = pool.RentRowBatch(lookup, capacity: 1, arena: arena);
        byte[] cbor = CborJsonCodecRef.EncodeFromJsonText("{\"count\":7,\"tag\":\"x\"}");
        batch.Add([DataValue.FromJson(cbor, arena)]);

        string outPath = Path.Combine(_scratchDir, "json-round.arrow");
        ArrowFormat format = new();
        await using (IExportSink sink = format.CreateSink(
            new ExportTarget.File(outPath),
            schema,
            [MediaDisposition.Inline],
            ExportOptions.Empty,
            registry))
        {
            await sink.WriteAsync(batch, default);
            await sink.FinishAsync(default);
        }

        // Round-trip via open_arrow; the Json column should surface as
        // DataKind.Json with CBOR bytes that decode back to the original
        // JSON object.
        bool sawRow = false;
        using TableCatalog readCatalog = CreateCatalog();
        StatementPlan readPlan = await readCatalog.PlanAsync(
            $"SELECT meta FROM open_arrow('{EscapeSql(outPath)}')");
        await foreach (RowBatch b in readCatalog.ExecuteAsync(readPlan))
        {
            b.ColumnLookup.TryGetColumnOrdinal("meta", out int col);
            for (int r = 0; r < b.Count; r++)
            {
                sawRow = true;
                DataValue v = b[r][col];
                Assert.Equal(DataKind.Json, v.Kind);
                string roundTrippedText = CborJsonCodecRef.DecodeToJsonText(
                    v.AsByteSpan(b.Arena, registry));
                Assert.Contains("\"count\"", roundTrippedText);
                Assert.Contains("\"tag\"", roundTrippedText);
            }
        }
        Assert.True(sawRow);
    }

    [Fact]
    public async Task CopyToArrow_ByteArrayColumn_SurfacesAsArrayOfUInt8()
    {
        // A third-party Arrow file with a Binary column (no datumv.*
        // tag) should surface as Array<UInt8> — not crash with the
        // BinaryArray-as-UInt8Array cast that was the original bug. We
        // simulate the third-party case by writing the file directly via
        // Apache.Arrow with no field metadata.
        byte[] payload1 = [1, 2, 3, 4];
        byte[] payload2 = [9, 8, 7];

        string outPath = Path.Combine(_scratchDir, "third-party.arrow");
        Apache.Arrow.Schema arrowSchema = new Apache.Arrow.Schema.Builder()
            .Field(f => f.Name("blob").DataType(BinaryType.Default).Nullable(true))
            .Build();
        BinaryArray.Builder bb = new();
        bb.Append(payload1);
        bb.Append(payload2);
        using (RecordBatch arrowBatch = new(arrowSchema, [bb.Build()], length: 2))
        await using (FileStream fs = File.Create(outPath))
        using (ArrowFileWriter w = new(fs, arrowSchema))
        {
            await w.WriteRecordBatchAsync(arrowBatch);
            await w.WriteEndAsync();
        }

        // Read back via open_arrow — should surface as Array<UInt8>.
        List<byte[]> got = [];
        using TableCatalog readCatalog = CreateCatalog();
        StatementPlan readPlan = await readCatalog.PlanAsync(
            $"SELECT blob FROM open_arrow('{EscapeSql(outPath)}')");
        await foreach (RowBatch b in readCatalog.ExecuteAsync(readPlan))
        {
            b.ColumnLookup.TryGetColumnOrdinal("blob", out int col);
            for (int r = 0; r < b.Count; r++)
            {
                DataValue v = b[r][col];
                Assert.True(v.IsArray);
                Assert.Equal(DataKind.UInt8, v.Kind);
                got.Add(v.AsArraySpan<byte>(b.Arena).ToArray());
            }
        }
        Assert.Equal(2, got.Count);
        Assert.Equal(payload1, got[0]);
        Assert.Equal(payload2, got[1]);
    }

    [Fact]
    public async Task CopyToArrow_ArrayOfStructColumn_RoundTripsThroughOpenArrow()
    {
        // Round-trip Array<Struct>: per-row variable-length array whose
        // elements are themselves structs with named fields. Mirrors the
        // shape HF datasets use for `tags`-of-`{ label, score }`.
        Pool pool = CreatePool();
        SidecarRegistryRef registry = new();

        HelioSchema schema = new(
        [
            new ColumnInfo("id", DataKind.Int32, nullable: false),
            new ColumnInfo("tags", nullable: false, fields:
            [
                new ColumnInfo("label", DataKind.String, nullable: false),
                new ColumnInfo("score", DataKind.Float32, nullable: false),
            ]) { IsArray = true },
        ]);
        ColumnLookup lookup = new(["id", "tags"]);
        using Arena arena = new();
        using RowBatch batch = pool.RentRowBatch(lookup, capacity: 2, arena: arena);
        DataValue[][] row1Tags =
        [
            [DataValue.FromString("cat", arena), DataValue.FromFloat32(0.9f)],
            [DataValue.FromString("dog", arena), DataValue.FromFloat32(0.7f)],
            [DataValue.FromString("fish", arena), DataValue.FromFloat32(0.2f)],
        ];
        DataValue[][] row2Tags =
        [
            [DataValue.FromString("bird", arena), DataValue.FromFloat32(0.5f)],
        ];
        batch.Add(
        [
            DataValue.FromInt32(1),
            DataValue.FromUntypedStructArray(row1Tags, arena),
        ]);
        batch.Add(
        [
            DataValue.FromInt32(2),
            DataValue.FromUntypedStructArray(row2Tags, arena),
        ]);

        string outPath = Path.Combine(_scratchDir, "tags.arrow");
        ArrowFormat format = new();
        await using (IExportSink sink = format.CreateSink(
            new ExportTarget.File(outPath),
            schema,
            [MediaDisposition.Inline, MediaDisposition.Inline],
            ExportOptions.Empty,
            registry))
        {
            await sink.WriteAsync(batch, default);
            await sink.FinishAsync(default);
        }

        // Read back via open_arrow and walk the per-row tag arrays.
        List<(int id, (string label, float score)[] tags)> got = [];
        using TableCatalog readCatalog = CreateCatalog();
        StatementPlan readPlan = await readCatalog.PlanAsync(
            $"SELECT id, tags FROM open_arrow('{EscapeSql(outPath)}')");
        await foreach (RowBatch b in readCatalog.ExecuteAsync(readPlan))
        {
            b.ColumnLookup.TryGetColumnOrdinal("id", out int idCol);
            b.ColumnLookup.TryGetColumnOrdinal("tags", out int tagsCol);
            for (int r = 0; r < b.Count; r++)
            {
                DataValue tagsV = b[r][tagsCol];
                Assert.True(tagsV.IsArray);
                Assert.Equal(DataKind.Struct, tagsV.Kind);
                DataValue[] elements = tagsV.AsStructArray(b.Arena);
                (string label, float score)[] tagPairs = new (string, float)[elements.Length];
                for (int e = 0; e < elements.Length; e++)
                {
                    DataValue[] fields = elements[e].AsStruct(b.Arena);
                    tagPairs[e] = (fields[0].AsString(b.Arena), fields[1].AsFloat32());
                }
                got.Add((b[r][idCol].AsInt32(), tagPairs));
            }
        }
        Assert.Equal(2, got.Count);
        Assert.Equal(1, got[0].id);
        Assert.Equal(3, got[0].tags.Length);
        Assert.Equal(("cat", 0.9f), got[0].tags[0]);
        Assert.Equal(("dog", 0.7f), got[0].tags[1]);
        Assert.Equal(("fish", 0.2f), got[0].tags[2]);
        Assert.Equal(2, got[1].id);
        Assert.Single(got[1].tags);
        Assert.Equal(("bird", 0.5f), got[1].tags[0]);
    }

    [Fact]
    public async Task OpenArrow_DictionaryEncodedStringColumn_DecodesViaIndices()
    {
        // Regression for the HuggingFace-style label-column case:
        // dictionary-encoded `label` columns crashed previously because
        // BuildScalar cast the runtime DictionaryArray to StringArray.
        // The reader now resolves the per-row index against the value
        // dictionary first, then dispatches to the scalar decoder.
        string path = Path.Combine(_scratchDir, "labels-dict.arrow");

        // Write a dictionary-encoded String column directly via
        // Apache.Arrow so we don't need engine writer support for
        // dictionary encoding (which isn't on our roadmap as a writer
        // path — readers see them, writers emit plain Strings).
        StringArray dictValues = new StringArray.Builder()
            .Append("alpha").Append("beta").Append("gamma").Build();
        Int32Array indices = new Int32Array.Builder().AppendRange([0, 1, 2, 1, 0]).Build();
        DictionaryArray dictArr = new(
            new Apache.Arrow.Types.DictionaryType(
                Apache.Arrow.Types.Int32Type.Default,
                Apache.Arrow.Types.StringType.Default,
                ordered: false),
            indices, dictValues);
        Apache.Arrow.Schema arrowSchema = new Apache.Arrow.Schema.Builder()
            .Field(f => f.Name("label").DataType(dictArr.Data.DataType).Nullable(false))
            .Build();
        using (RecordBatch arrowBatch = new(arrowSchema, [dictArr], length: 5))
        await using (FileStream fs = File.Create(path))
        using (ArrowFileWriter w = new(fs, arrowSchema))
        {
            await w.WriteRecordBatchAsync(arrowBatch);
            await w.WriteEndAsync();
        }

        List<string> labels = [];
        using TableCatalog readCatalog = CreateCatalog();
        StatementPlan plan = await readCatalog.PlanAsync(
            $"SELECT label FROM open_arrow('{EscapeSql(path)}')");
        await foreach (RowBatch b in readCatalog.ExecuteAsync(plan))
        {
            b.ColumnLookup.TryGetColumnOrdinal("label", out int col);
            for (int r = 0; r < b.Count; r++)
            {
                labels.Add(b[r][col].AsString(b.Arena));
            }
        }
        Assert.Equal(new[] { "alpha", "beta", "gamma", "beta", "alpha" }, labels);
    }

    [Fact]
    public async Task CopyToArrow_UuidColumn_RoundTripsAsUuidNotString()
    {
        // Uuid is stringified via the writer's datumv tag and parsed
        // back on read. Without the round-trip path, the column would
        // come back as a plain String — losing the typed kind. Pin both
        // the kind and the exact Guid value.
        Guid id1 = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Guid id2 = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.ids",
            columns: ["id"],
            columnKinds: [DataKind.Uuid],
            rows: [[id1], [id2]]));

        string outPath = Path.Combine(_scratchDir, "uuids.arrow");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT id FROM public.ids) TO '{EscapeSql(outPath)}'");
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        List<(DataKind kind, Guid value)> got = [];
        using TableCatalog readCatalog = CreateCatalog();
        StatementPlan readPlan = await readCatalog.PlanAsync(
            $"SELECT id FROM open_arrow('{EscapeSql(outPath)}')");
        await foreach (RowBatch b in readCatalog.ExecuteAsync(readPlan))
        {
            b.ColumnLookup.TryGetColumnOrdinal("id", out int col);
            for (int r = 0; r < b.Count; r++)
            {
                DataValue v = b[r][col];
                got.Add((v.Kind, v.AsUuid()));
            }
        }
        Assert.Equal(2, got.Count);
        Assert.Equal(DataKind.Uuid, got[0].kind);
        Assert.Equal(id1, got[0].value);
        Assert.Equal(DataKind.Uuid, got[1].kind);
        Assert.Equal(id2, got[1].value);
    }

    [Fact]
    public async Task CopyToArrow_ByteArrayColumn_RoundTripsAsArrayOfUInt8()
    {
        // Pin the writer/reader symmetry: a true Array<UInt8> column
        // (the engine's general byte-array shape, distinct from Image)
        // writes as Arrow ListArray<UInt8> and re-reads as Array<UInt8>.
        Pool pool = CreatePool();
        SidecarRegistryRef registry = new();

        HelioSchema schema = new(
        [
            new ColumnInfo("blob", DataKind.UInt8, nullable: false) { IsArray = true },
        ]);
        ColumnLookup lookup = new(["blob"]);
        using Arena arena = new();
        using RowBatch batch = pool.RentRowBatch(lookup, capacity: 2, arena: arena);
        batch.Add([DataValue.FromByteArray([1, 2, 3, 4], arena)]);
        batch.Add([DataValue.FromByteArray([9, 8], arena)]);

        string outPath = Path.Combine(_scratchDir, "byte-arrays.arrow");
        ArrowFormat format = new();
        await using (IExportSink sink = format.CreateSink(
            new ExportTarget.File(outPath),
            schema,
            [MediaDisposition.Inline],
            ExportOptions.Empty,
            registry))
        {
            await sink.WriteAsync(batch, default);
            await sink.FinishAsync(default);
        }

        List<byte[]> got = [];
        using TableCatalog readCatalog = CreateCatalog();
        StatementPlan plan = await readCatalog.PlanAsync(
            $"SELECT blob FROM open_arrow('{EscapeSql(outPath)}')");
        await foreach (RowBatch b in readCatalog.ExecuteAsync(plan))
        {
            b.ColumnLookup.TryGetColumnOrdinal("blob", out int col);
            for (int r = 0; r < b.Count; r++)
            {
                DataValue v = b[r][col];
                Assert.True(v.IsArray);
                Assert.Equal(DataKind.UInt8, v.Kind);
                got.Add(v.AsArraySpan<byte>(b.Arena).ToArray());
            }
        }
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, got[0]);
        Assert.Equal(new byte[] { 9, 8 }, got[1]);
    }

    /// <summary>
    /// Fabricates a fake per-row byte payload. Just <paramref name="length"/>
    /// copies of <paramref name="seed"/> — content is opaque, the round-trip
    /// test only cares about exact byte equality after re-import.
    /// </summary>
    private static byte[] MakeFakeImageBytes(byte seed, int length)
    {
        byte[] result = new byte[length];
        for (int i = 0; i < length; i++) result[i] = (byte)(seed ^ (i & 0x7F));
        return result;
    }

    [Fact]
    public async Task CopyToArrow_YieldsSummaryRow_WithRowsWrittenAndBytesWritten()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.x",
            columns: ["a"],
            columnKinds: [DataKind.Int32],
            rows: [[1], [2], [3]]));

        string outPath = Path.Combine(_scratchDir, "summary.arrow");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT a FROM public.x) TO '{EscapeSql(outPath)}'");

        List<RowBatch> batches = [];
        await foreach (RowBatch batch in catalog.ExecuteAsync(plan)) batches.Add(batch);
        RowBatch summary = Assert.Single(batches);
        Row row = summary[0];
        Assert.True(summary.ColumnLookup.TryGetColumnOrdinal("rows_written", out int rowsCol));
        Assert.True(summary.ColumnLookup.TryGetColumnOrdinal("bytes_written", out int bytesCol));
        Assert.Equal(3L, row[rowsCol].AsInt64());
        Assert.Equal(new FileInfo(outPath).Length, row[bytesCol].AsInt64());
    }

    [Fact]
    public async Task CopyToArrow_ExtensionInferenceWorks_ForArrowAndFeather()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.x",
            columns: ["a"],
            columnKinds: [DataKind.Int32],
            rows: [[1]]));

        foreach (string ext in new[] { ".arrow", ".feather" })
        {
            string outPath = Path.Combine(_scratchDir, $"x{ext}");
            StatementPlan plan = await catalog.PlanAsync(
                $"COPY (SELECT a FROM public.x) TO '{EscapeSql(outPath)}'");
            await foreach (var _ in catalog.ExecuteAsync(plan)) { }
            Assert.True(File.Exists(outPath));

            await using FileStream fs = File.OpenRead(outPath);
            using ArrowFileReader reader = new(fs);
            Assert.Single(reader.Schema.FieldsList);
        }
    }

    // ─────────────── Struct edge cases ───────────────

    [Fact]
    public async Task CopyToArrow_NestedStructColumn_RoundTrips()
    {
        // Nested struct: Struct{ id, Struct{ inner_x, inner_y } }. The
        // recursion in both writer and reader treats inner structs the
        // same as top-level; this test pins that nested field names
        // survive and inner field values come back intact.
        Pool pool = CreatePool();
        SidecarRegistryRef registry = new();

        HelioSchema schema = new(
        [
            new ColumnInfo("id", DataKind.Int32, nullable: false),
            new ColumnInfo("nest", nullable: false, fields:
            [
                new ColumnInfo("label", DataKind.String, nullable: false),
                new ColumnInfo("inner", nullable: false, fields:
                [
                    new ColumnInfo("x", DataKind.Float32, nullable: false),
                    new ColumnInfo("y", DataKind.Float32, nullable: false),
                ]),
            ]),
        ]);
        ColumnLookup lookup = new(["id", "nest"]);
        using Arena arena = new();
        using RowBatch batch = pool.RentRowBatch(lookup, capacity: 1, arena: arena);
        DataValue innerStruct = DataValue.FromUntypedStruct(
            [DataValue.FromFloat32(1.5f), DataValue.FromFloat32(2.5f)], arena);
        DataValue nestStruct = DataValue.FromUntypedStruct(
            [DataValue.FromString("foo", arena), innerStruct], arena);
        batch.Add([DataValue.FromInt32(7), nestStruct]);

        string outPath = Path.Combine(_scratchDir, "nested.arrow");
        ArrowFormat format = new();
        await using (IExportSink sink = format.CreateSink(
            new ExportTarget.File(outPath),
            schema,
            [MediaDisposition.Inline, MediaDisposition.Inline],
            ExportOptions.Empty,
            registry))
        {
            await sink.WriteAsync(batch, default);
            await sink.FinishAsync(default);
        }

        // Verify the on-disk Arrow schema carries the nested struct
        // shape with real names at every level.
        await using (FileStream fs = File.OpenRead(outPath))
        using (ArrowFileReader reader = new(fs))
        {
            Apache.Arrow.Field nestField = reader.Schema.GetFieldByName("nest")!;
            StructType nestType = Assert.IsType<StructType>(nestField.DataType);
            Assert.Equal("label", nestType.Fields[0].Name);
            Assert.Equal("inner", nestType.Fields[1].Name);
            StructType innerType = Assert.IsType<StructType>(nestType.Fields[1].DataType);
            Assert.Equal("x", innerType.Fields[0].Name);
            Assert.Equal("y", innerType.Fields[1].Name);
        }

        // Round-trip via open_arrow walks the nested struct field by
        // field, in-place inside the batch enumerator.
        (int id, string label, float x, float y)? got = null;
        using TableCatalog readCatalog = CreateCatalog();
        StatementPlan readPlan = await readCatalog.PlanAsync(
            $"SELECT id, nest FROM open_arrow('{EscapeSql(outPath)}')");
        await foreach (RowBatch b in readCatalog.ExecuteAsync(readPlan))
        {
            b.ColumnLookup.TryGetColumnOrdinal("id", out int idCol);
            b.ColumnLookup.TryGetColumnOrdinal("nest", out int nestCol);
            for (int r = 0; r < b.Count; r++)
            {
                DataValue[] outer = b[r][nestCol].AsStruct(b.Arena);
                DataValue[] inner = outer[1].AsStruct(b.Arena);
                got = (
                    b[r][idCol].AsInt32(),
                    outer[0].AsString(b.Arena),
                    inner[0].AsFloat32(),
                    inner[1].AsFloat32());
            }
        }
        Assert.Equal((7, "foo", 1.5f, 2.5f), got);
    }

    [Fact]
    public async Task CopyToArrow_StructWithImageField_RoundTripsImageViaDatumvRoute()
    {
        // Typed media inside a struct: each Image field travels through
        // the same datumv.* routing the top-level Image column uses. The
        // recursion in BuildColumnInfo + ApplyMediaRoute should retype
        // the field back to DataKind.Image on read.
        byte[] png = MakeFakeImageBytes(0x42, 96);

        Pool pool = CreatePool();
        SidecarRegistryRef registry = new();

        HelioSchema schema = new(
        [
            new ColumnInfo("sample", nullable: false, fields:
            [
                new ColumnInfo("id", DataKind.Int32, nullable: false),
                new ColumnInfo("pic", DataKind.Image, nullable: false),
            ]),
        ]);
        ColumnLookup lookup = new(["sample"]);
        using Arena arena = new();
        using RowBatch batch = pool.RentRowBatch(lookup, capacity: 1, arena: arena);
        DataValue sampleStruct = DataValue.FromUntypedStruct(
            [DataValue.FromInt32(1), DataValue.FromImage(png, arena)], arena);
        batch.Add([sampleStruct]);

        string outPath = Path.Combine(_scratchDir, "struct-with-image.arrow");
        ArrowFormat format = new();
        await using (IExportSink sink = format.CreateSink(
            new ExportTarget.File(outPath),
            schema,
            [MediaDisposition.Inline],
            ExportOptions.Empty,
            registry))
        {
            await sink.WriteAsync(batch, default);
            await sink.FinishAsync(default);
        }

        byte[]? recovered = null;
        DataKind recoveredKind = DataKind.Unknown;
        using TableCatalog readCatalog = CreateCatalog();
        StatementPlan readPlan = await readCatalog.PlanAsync(
            $"SELECT sample FROM open_arrow('{EscapeSql(outPath)}')");
        await foreach (RowBatch b in readCatalog.ExecuteAsync(readPlan))
        {
            b.ColumnLookup.TryGetColumnOrdinal("sample", out int col);
            for (int r = 0; r < b.Count; r++)
            {
                DataValue[] fields = b[r][col].AsStruct(b.Arena);
                recoveredKind = fields[1].Kind;
                recovered = fields[1].AsImage(b.Arena, registry);
            }
        }
        Assert.Equal(DataKind.Image, recoveredKind);
        Assert.Equal(png, recovered);
    }

    [Fact]
    public async Task CopyToArrow_NullStructValue_RoundTripsAsNull()
    {
        // Whole-row NULL struct. The writer flips the struct's validity
        // bit (and appends null placeholders to each child to keep
        // counts aligned); the reader's IsNull check should surface the
        // row as a NULL DataValue without trying to read child fields.
        Pool pool = CreatePool();
        SidecarRegistryRef registry = new();

        HelioSchema schema = new(
        [
            new ColumnInfo("id", DataKind.Int32, nullable: false),
            new ColumnInfo("loc", nullable: true, fields:
            [
                new ColumnInfo("city", DataKind.String, nullable: false),
                new ColumnInfo("zip", DataKind.Int32, nullable: false),
            ]),
        ]);
        ColumnLookup lookup = new(["id", "loc"]);
        using Arena arena = new();
        using RowBatch batch = pool.RentRowBatch(lookup, capacity: 2, arena: arena);
        batch.Add(
        [
            DataValue.FromInt32(1),
            DataValue.FromUntypedStruct(
                [DataValue.FromString("Boston", arena), DataValue.FromInt32(2115)], arena),
        ]);
        batch.Add(
        [
            DataValue.FromInt32(2),
            DataValue.NullUntypedStruct(),
        ]);

        string outPath = Path.Combine(_scratchDir, "null-struct.arrow");
        ArrowFormat format = new();
        await using (IExportSink sink = format.CreateSink(
            new ExportTarget.File(outPath),
            schema,
            [MediaDisposition.Inline, MediaDisposition.Inline],
            ExportOptions.Empty,
            registry))
        {
            await sink.WriteAsync(batch, default);
            await sink.FinishAsync(default);
        }

        List<(int id, bool locNull, string? city)> got = [];
        using TableCatalog readCatalog = CreateCatalog();
        StatementPlan readPlan = await readCatalog.PlanAsync(
            $"SELECT id, loc FROM open_arrow('{EscapeSql(outPath)}')");
        await foreach (RowBatch b in readCatalog.ExecuteAsync(readPlan))
        {
            b.ColumnLookup.TryGetColumnOrdinal("id", out int idCol);
            b.ColumnLookup.TryGetColumnOrdinal("loc", out int locCol);
            for (int r = 0; r < b.Count; r++)
            {
                DataValue locV = b[r][locCol];
                if (locV.IsNull)
                {
                    got.Add((b[r][idCol].AsInt32(), true, null));
                }
                else
                {
                    DataValue[] fields = locV.AsStruct(b.Arena);
                    got.Add((b[r][idCol].AsInt32(), false, fields[0].AsString(b.Arena)));
                }
            }
        }
        Assert.Equal(2, got.Count);
        Assert.Equal((1, false, "Boston"), got[0]);
        Assert.Equal((2, true, (string?)null), got[1]);
    }

    [Fact]
    public async Task CopyToArrow_StructWithSidecarBackedStringField_ResolvesViaRegistry()
    {
        // Real ingested datasets store String bytes in a .datum-blob
        // sidecar; a struct field reading those needs the registry
        // threaded through the recursive builder. Pin the registry
        // plumbing so a future drive-by edit that drops the parameter
        // doesn't silently break sidecar-resolved struct fields.
        byte[] cityBytes = System.Text.Encoding.UTF8.GetBytes("Cambridge");
        InMemoryBlobSource blobs = new();
        long cityOffset = blobs.Append(cityBytes);

        Pool pool = CreatePool();
        SidecarRegistryRef registry = new();
        byte storeId = registry.Register(blobs);

        HelioSchema schema = new(
        [
            new ColumnInfo("loc", nullable: false, fields:
            [
                new ColumnInfo("city", DataKind.String, nullable: false),
                new ColumnInfo("zip", DataKind.Int32, nullable: false),
            ]),
        ]);
        ColumnLookup lookup = new(["loc"]);
        using Arena arena = new();
        using RowBatch batch = pool.RentRowBatch(lookup, capacity: 1, arena: arena);
        DataValue sidecarCity = DataValue.FromStringInSidecar(cityOffset, cityBytes.Length, storeId);
        batch.Add(
        [
            DataValue.FromUntypedStruct([sidecarCity, DataValue.FromInt32(2139)], arena),
        ]);

        string outPath = Path.Combine(_scratchDir, "sidecar-struct.arrow");
        ArrowFormat format = new();
        await using (IExportSink sink = format.CreateSink(
            new ExportTarget.File(outPath),
            schema,
            [MediaDisposition.Inline],
            ExportOptions.Empty,
            registry))
        {
            await sink.WriteAsync(batch, default);
            await sink.FinishAsync(default);
        }

        // The exported file no longer has sidecar bytes (the writer
        // resolves them through the registry at append time). Reading
        // back via open_arrow exercises a clean path that doesn't need
        // the registry.
        string? gotCity = null;
        using TableCatalog readCatalog = CreateCatalog();
        StatementPlan readPlan = await readCatalog.PlanAsync(
            $"SELECT loc FROM open_arrow('{EscapeSql(outPath)}')");
        await foreach (RowBatch b in readCatalog.ExecuteAsync(readPlan))
        {
            b.ColumnLookup.TryGetColumnOrdinal("loc", out int col);
            for (int r = 0; r < b.Count; r++)
            {
                DataValue[] fields = b[r][col].AsStruct(b.Arena);
                gotCity = fields[0].AsString(b.Arena);
            }
        }
        Assert.Equal("Cambridge", gotCity);
    }

    // ─────────────── Dangler-throw tests ───────────────

    [Fact]
    public void CopyToArrow_MultiDimArrayColumn_RejectedAtPlanTime()
    {
        // Multi-dim arrays would silently flatten to 1-D in Arrow. The
        // writer rejects upfront with an actionable hint; this pins the
        // contract so a future silent-flatten regression surfaces here.
        ArrowFormat format = new();
        ColumnInfo multiDim = new("embedding", DataKind.Float32, nullable: false)
        {
            IsArray = true,
            IsMultiDim = true,
            FixedShape = [2, 3],
        };
        ExportPlanException ex = Assert.Throws<ExportPlanException>(
            () => format.ResolveDisposition(multiDim, ExportOptions.Empty));
        Assert.Contains("'embedding'", ex.Message);
        Assert.Contains("multi-dimensional", ex.Message);
        Assert.Contains("CAST", ex.Message);
    }

    [Fact]
    public void OpenArrow_FixedSizeBinaryColumn_ClassifiedAsUnsupportedWithHint()
    {
        // FixedSizeBinary has no concrete reader-array class in
        // Apache.Arrow .NET v23, so the schema layer now classifies it
        // as unsupported with an actionable hint embedded in the logical
        // type name (which open_arrow's error message uses verbatim).
        // We can't construct a FixedSizeBinary file directly from the
        // test — that's the underlying gap — but we can verify the
        // classification path that fires when one comes in.
        Apache.Arrow.Field field = new("hash", new FixedSizeBinaryType(byteWidth: 16), nullable: false);
        ArrowColumnType type = ArrowColumnType.From(field);
        Assert.False(type.IsSupported);
        Assert.Contains("FixedSizeBinary", type.LogicalTypeName);
        Assert.Contains("Binary", type.LogicalTypeName);  // hint points at supported variant
        Assert.Contains("cast", type.LogicalTypeName);    // hint includes the conversion idiom
    }

    [Fact]
    public void OpenArrow_LargeStringColumn_ClassifiedAsUnsupportedWithHint()
    {
        // Same shape as FixedSizeBinary — Apache.Arrow .NET v23 doesn't
        // expose a writer-side LargeString surface either. Verify the
        // schema layer flags it with an actionable hint.
        Apache.Arrow.Field field = new("big_text",
            new global::Apache.Arrow.Types.LargeStringType(),
            nullable: false);
        ArrowColumnType type = ArrowColumnType.From(field);
        Assert.False(type.IsSupported);
        Assert.Contains("LargeString", type.LogicalTypeName);
        Assert.Contains("String", type.LogicalTypeName);
        Assert.Contains("cast", type.LogicalTypeName);
    }

    /// <summary>
    /// Minimal in-memory <see cref="DatumFile.Sidecar.IBlobSource"/> for
    /// the sidecar-resolution test. Same shape as the existing Parquet/
    /// CSV regression fixtures.
    /// </summary>
    private sealed class InMemoryBlobSource : global::Heliosoph.DatumV.DatumFile.Sidecar.IBlobSource
    {
        private byte[] _buf = [];
        public long Append(byte[] payload)
        {
            long offset = _buf.Length;
            byte[] next = new byte[_buf.Length + payload.Length];
            _buf.CopyTo(next, 0);
            payload.CopyTo(next, _buf.Length);
            _buf = next;
            return offset;
        }
        public ReadOnlySpan<byte> Read(long offset, long length)
            => _buf.AsSpan().Slice((int)offset, (int)length);
        public void Dispose() { }
    }

    private static string EscapeSql(string path) => path.Replace("'", "''");
}
