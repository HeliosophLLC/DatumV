using System.Numerics;
using System.Runtime.InteropServices;
using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Catalog.Plans;
using Heliosoph.DatumV.Catalog.Providers;
using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Export;
using Heliosoph.DatumV.Export.Parquet;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Spatial;
using Heliosoph.DatumV.Functions.TableValued;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Model.Spatial;
using Heliosoph.DatumV.Pooling;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Tests.Export;

/// <summary>
/// End-to-end exercise of <c>COPY (query) TO 'path' (FORMAT parquet)</c> —
/// the parser slot, <see cref="ExportPlan"/> + planner glue,
/// <see cref="Heliosoph.DatumV.Export.Parquet.ParquetExportFormat"/>, and
/// <see cref="Heliosoph.DatumV.Export.Parquet.ParquetExportSink"/>.
///
/// The headline case exports a typed-media (Image) column alongside a scalar
/// (Int32) and verifies the bytes round-trip through Parquet's
/// <c>BYTE_ARRAY</c> physical type — the v1 Inline media disposition.
/// </summary>
public sealed class ParquetExportSinkTests : ServiceTestBase, IDisposable
{
    private readonly string _scratchDir;

    public ParquetExportSinkTests()
    {
        _scratchDir = Path.Combine(Path.GetTempPath(), $"datum-parquet-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchDir);
    }

    public new void Dispose()
    {
        base.Dispose();
        try { if (Directory.Exists(_scratchDir)) Directory.Delete(_scratchDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task CopyToParquet_ScalarsOnly_WritesReadableFile()
    {
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

        string outPath = Path.Combine(_scratchDir, "scalars.parquet");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT id, name, score FROM public.scalars) TO '{EscapeSql(outPath)}' (FORMAT parquet)");

        Assert.IsType<ExportPlan>(plan);
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.True(File.Exists(outPath), $"export did not produce a file at '{outPath}'");
        Assert.True(new FileInfo(outPath).Length > 0, "export file is empty");

        await using FileStream readStream = File.OpenRead(outPath);
        using ParquetReader reader = await ParquetReader.CreateAsync(readStream);
        DataField[] fields = reader.Schema.GetDataFields();
        Assert.Equal(3, fields.Length);
        Assert.Equal("id", fields[0].Name);
        Assert.Equal("name", fields[1].Name);
        Assert.Equal("score", fields[2].Name);
    }

    [Fact]
    public async Task CopyToParquet_YieldsSummaryRow_WithRowsWrittenAndBytesWritten()
    {
        // Slice A: COPY surfaces a one-row (rows_written, bytes_written)
        // summary, matching DuckDB's COPY-returns-counts shape. Pins the
        // schema (column names + Int64 kind) and the actual row count so
        // any future change to the sink's RowsWritten / BytesWritten
        // accounting surfaces in the assertion.
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.scalars",
            columns: ["id", "name"],
            columnKinds: [DataKind.Int32, DataKind.String],
            rows:
            [
                [1, "alice"],
                [2, "bob"],
                [3, "carol"],
            ]));

        string outPath = Path.Combine(_scratchDir, "summary.parquet");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT id, name FROM public.scalars) TO '{EscapeSql(outPath)}'");

        // Drain the plan; the summary row is the only batch.
        List<RowBatch> batches = [];
        await foreach (RowBatch batch in catalog.ExecuteAsync(plan))
        {
            batches.Add(batch);
        }

        RowBatch summary = Assert.Single(batches);
        Assert.Equal(1, summary.Count);
        Assert.Equal(2, summary.ColumnLookup.Count);
        Assert.True(summary.ColumnLookup.TryGetColumnOrdinal("rows_written", out int rowsCol));
        Assert.True(summary.ColumnLookup.TryGetColumnOrdinal("bytes_written", out int bytesCol));

        Row row = summary[0];
        Assert.Equal(3L, row[rowsCol].AsInt64());
        Assert.True(row[bytesCol].AsInt64() > 0L,
            "bytes_written should be the on-disk file size, which is non-zero.");
        Assert.Equal(new FileInfo(outPath).Length, row[bytesCol].AsInt64());
    }

    [Fact]
    public async Task CopyToParquet_WithImageColumn_RoundTripsBytes()
    {
        // Two distinct payloads so the round-trip can confirm per-row identity.
        byte[] image1 = MakeFakeImageBytes(0xAA, 32);
        byte[] image2 = MakeFakeImageBytes(0xBB, 96);

        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.samples",
            columns: ["id", "pic"],
            columnKinds: [DataKind.Int32, DataKind.Image],
            rows:
            [
                [1, image1],
                [2, image2],
            ]));

        string outPath = Path.Combine(_scratchDir, "samples.parquet");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT id, pic FROM public.samples) TO '{EscapeSql(outPath)}' (FORMAT parquet)");

        ExportPlan exportPlan = Assert.IsType<ExportPlan>(plan);
        Assert.Equal("Copy", exportPlan.ExplainTree.OperatorName);

        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.True(File.Exists(outPath), $"export did not produce a file at '{outPath}'");

        // Round-trip: open the file via Parquet.Net directly so the test
        // verifies the on-disk bytes rather than re-using our own writer's
        // schema translation. Image is stored as a BYTE_ARRAY column, which
        // Parquet.Net surfaces as a CLR byte[].
        await using FileStream readStream = File.OpenRead(outPath);
        using ParquetReader reader = await ParquetReader.CreateAsync(readStream);
        DataField[] fields = reader.Schema.GetDataFields();
        Assert.Equal(2, fields.Length);
        Assert.Equal("id", fields[0].Name);
        Assert.Equal("pic", fields[1].Name);

        Assert.Equal(1, reader.RowGroupCount);
        using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);
        Assert.Equal(2, rg.RowCount);

        DataColumn idColumn = await rg.ReadColumnAsync(fields[0]);
        DataColumn imageColumn = await rg.ReadColumnAsync(fields[1]);

        int[] ids = ConvertToNonNullable<int>(idColumn.Data);
        // The sink writes Image columns as LIST<UInt8>, so the column data
        // is a flat byte[] split by repetition levels — slice it back into
        // per-row sub-arrays.
        byte[][] images = UnflattenByteList(imageColumn);

        Assert.Equal([1, 2], ids);
        Assert.Equal(image1, images[0]);
        Assert.Equal(image2, images[1]);
    }

    [Fact]
    public async Task CopyToParquet_TemporalAndDecimalAndUuid_RoundTrip()
    {
        // Mirrors the NYC-taxi shape that surfaced the original Timestamp gap.
        // Covers every non-primitive scalar kind the Parquet ingestion side
        // already understands so the export surface matches the read surface.
        DateTime pickup = new(2024, 6, 15, 9, 30, 0, DateTimeKind.Unspecified);
        DateTimeOffset eventTs = new(2024, 6, 15, 9, 30, 0, TimeSpan.FromHours(-4));
        DateOnly birthday = new(1991, 4, 7);
        TimeOnly clockIn = new(8, 15, 0);
        decimal fare = 12.50m;
        Guid id = Guid.Parse("11111111-2222-3333-4444-555555555555");

        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.trips",
            columns: ["pickup", "event_ts", "birthday", "clock_in", "fare", "trip_uuid"],
            columnKinds:
            [
                DataKind.Timestamp,
                DataKind.TimestampTz,
                DataKind.Date,
                DataKind.Time,
                DataKind.Decimal,
                DataKind.Uuid,
            ],
            rows: [[pickup, eventTs, birthday, clockIn, fare, id]]));

        string outPath = Path.Combine(_scratchDir, "trips.parquet");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT pickup, event_ts, birthday, clock_in, fare, trip_uuid FROM public.trips) " +
            $"TO '{EscapeSql(outPath)}'");

        Assert.IsType<ExportPlan>(plan);
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        await using FileStream readStream = File.OpenRead(outPath);
        using ParquetReader reader = await ParquetReader.CreateAsync(readStream);
        DataField[] fields = reader.Schema.GetDataFields();
        Assert.Equal(6, fields.Length);

        using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);
        Assert.Equal(1, rg.RowCount);

        DataColumn pickupCol = await rg.ReadColumnAsync(fields[0]);
        DataColumn eventCol = await rg.ReadColumnAsync(fields[1]);
        DataColumn birthdayCol = await rg.ReadColumnAsync(fields[2]);
        DataColumn clockInCol = await rg.ReadColumnAsync(fields[3]);
        DataColumn fareCol = await rg.ReadColumnAsync(fields[4]);
        DataColumn uuidCol = await rg.ReadColumnAsync(fields[5]);

        Assert.Equal(pickup, ConvertToNonNullable<DateTime>(pickupCol.Data)[0]);
        // Parquet.Net 5.x serialises TimestampTz as a UTC-normalised DateTime
        // (DateTimeOffset writer support was removed); compare instants.
        Assert.Equal(eventTs.UtcDateTime, ConvertToNonNullable<DateTime>(eventCol.Data)[0]);
        // Parquet.Net reads Date (INT32 logical) and Time (INT64 logical) back
        // as DateTime / TimeSpan respectively — the round-trip preserves the
        // value, not the CLR widening. Compare against the lifted form.
        Assert.Equal(birthday.ToDateTime(TimeOnly.MinValue),
            ConvertToNonNullable<DateTime>(birthdayCol.Data)[0]);
        Assert.Equal(clockIn.ToTimeSpan(),
            ConvertToNonNullable<TimeSpan>(clockInCol.Data)[0]);
        Assert.Equal(fare, ConvertToNonNullable<decimal>(fareCol.Data)[0]);
        Assert.Equal(id, ConvertToNonNullable<Guid>(uuidCol.Data)[0]);
    }

    [Fact]
    public async Task CopyToParquet_SidecarBackedReferenceTypes_ResolveViaRegistry()
    {
        // Regression: real-world ingested datasets store Image *and String*
        // bytes in a .datum-blob sidecar, not in the row arena (NYC taxi's
        // `file_name`, COCO's `file` Image column). The Parquet sink used
        // to call AsImage(store) / AsString(store) without a
        // SidecarRegistry, which threw "DataValue is sidecar-backed but no
        // SidecarRegistry was provided." Pin the fix by feeding a RowBatch
        // of sidecar-backed Image *and* String values directly into the
        // sink and verifying both columns survive the round trip.
        //
        // We drive the sink directly rather than through SQL because
        // InMemoryTableProvider rejects pre-built sidecar DataValues by
        // design — it materialises cells into the per-batch arena and a
        // sidecar offset would resolve against the wrong store. Real
        // ingested DatumFileTableProviderV2 readers emit sidecar values
        // through the scan path, which is what this exercises.
        byte[] image1 = MakeFakeImageBytes(0x10, 64);
        byte[] image2 = MakeFakeImageBytes(0x20, 96);
        byte[] name1 = System.Text.Encoding.UTF8.GetBytes("sample-one.jpg");
        byte[] name2 = System.Text.Encoding.UTF8.GetBytes("sample-two-with-a-much-longer-name.jpg");
        FakeSidecarBlobSource blobs = new();
        long offImage1 = blobs.Append(image1);
        long offImage2 = blobs.Append(image2);
        long offName1 = blobs.Append(name1);
        long offName2 = blobs.Append(name2);

        Pool pool = CreatePool();
        SidecarRegistry registry = new();
        byte storeId = registry.Register(blobs);

        Schema schema = new(
        [
            new ColumnInfo("id", DataKind.Int32, nullable: true),
            new ColumnInfo("pic", DataKind.Image, nullable: true),
            new ColumnInfo("file_name", DataKind.String, nullable: true),
        ]);
        ColumnLookup lookup = new(["id", "pic", "file_name"]);
        using Heliosoph.DatumV.Model.Arena arena = new();
        using RowBatch batch = pool.RentRowBatch(lookup, capacity: 2, arena: arena);
        batch.Add(
        [
            DataValue.FromInt32(1),
            DataValue.FromImageInSidecar(offImage1, image1.Length, storeId),
            DataValue.FromStringInSidecar(offName1, name1.Length, storeId),
        ]);
        batch.Add(
        [
            DataValue.FromInt32(2),
            DataValue.FromImageInSidecar(offImage2, image2.Length, storeId),
            DataValue.FromStringInSidecar(offName2, name2.Length, storeId),
        ]);

        string outPath = Path.Combine(_scratchDir, "sidecar-samples.parquet");
        ParquetExportFormat format = new();
        await using (IExportSink sink = format.CreateSink(
            new ExportTarget.File(outPath),
            schema,
            [MediaDisposition.Inline, MediaDisposition.Inline, MediaDisposition.Inline],
            ExportOptions.Empty,
            registry))
        {
            await sink.WriteAsync(batch, default);
            await sink.FinishAsync(default);
        }

        await using FileStream readStream = File.OpenRead(outPath);
        using ParquetReader reader = await ParquetReader.CreateAsync(readStream);
        DataField[] fields = reader.Schema.GetDataFields();
        using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);
        DataColumn idColumn = await rg.ReadColumnAsync(fields[0]);
        DataColumn picColumn = await rg.ReadColumnAsync(fields[1]);
        DataColumn nameColumn = await rg.ReadColumnAsync(fields[2]);

        int[] ids = ConvertToNonNullable<int>(idColumn.Data);
        byte[][] pics = UnflattenByteList(picColumn);
        string[] names = ConvertToReferenceArray<string>(nameColumn.Data);
        Assert.Equal([1, 2], ids);
        Assert.Equal(image1, pics[0]);
        Assert.Equal(image2, pics[1]);
        Assert.Equal("sample-one.jpg", names[0]);
        Assert.Equal("sample-two-with-a-much-longer-name.jpg", names[1]);
    }

    [Fact]
    public async Task CopyToParquet_LargeBlobsFlushMultipleRowGroups()
    {
        // Regression: a typed-media column with ~200 KB per row × 50,000 rows
        // (default ROW_GROUP_SIZE) overflowed the Int32 totalBytes accumulator
        // and tripped Parquet.Net's writer with a negative minimumLength deep
        // in the level encoder. The byte-budget flush should fire long before
        // accumulated bytes reach that boundary. Exercises the trigger with a
        // tight budget so the test stays fast.
        //
        // Audio is the typed-media kind used here because it stays on the raw-
        // passthrough export path — Mesh / PointCloud go through GltfExporter
        // / PlyExporter so fake byte payloads would be rejected at append
        // time. The flush logic is shared across every typed-media kind.
        Pool pool = CreatePool();
        SidecarRegistry registry = new();

        Schema schema = new(
        [
            new ColumnInfo("clip", DataKind.Audio, nullable: false),
        ]);
        ColumnLookup lookup = new(["clip"]);

        // 16 KB per blob × 8 blobs = 128 KB total. Budget set to 64 KB → the
        // sink should flush after ~4 blobs, producing two row groups (or more
        // if rounding lands on a row boundary).
        const int blobsPerBatch = 8;
        const int blobSize = 16 * 1024;
        const long byteBudget = 64L * 1024L;

        using Heliosoph.DatumV.Model.Arena arena = new();
        using RowBatch batch = pool.RentRowBatch(lookup, capacity: blobsPerBatch, arena: arena);
        for (int i = 0; i < blobsPerBatch; i++)
        {
            byte[] audioBytes = MakeFakeImageBytes((byte)(0xA0 + i), blobSize);
            batch.Add([DataValue.FromAudio(audioBytes, arena)]);
        }

        string outPath = Path.Combine(_scratchDir, "many-meshes.parquet");
        await using (ParquetExportSink sink = new(
            outPath,
            schema,
            rowGroupSize: 1_000_000,                  // out of the way
            sidecarRegistry: registry,
            rowGroupByteBudget: byteBudget))
        {
            await sink.WriteAsync(batch, default);
            await sink.FinishAsync(default);
        }

        await using FileStream readStream = File.OpenRead(outPath);
        using ParquetReader reader = await ParquetReader.CreateAsync(readStream);
        Assert.True(reader.RowGroupCount >= 2,
            $"Expected at least 2 row groups for {blobsPerBatch} × {blobSize} byte blobs under a " +
            $"{byteBudget} byte budget; got {reader.RowGroupCount}.");

        // Read every row back and confirm the bytes survived.
        DataField field = reader.Schema.GetDataFields()[0];
        List<byte[]> recovered = [];
        for (int rg = 0; rg < reader.RowGroupCount; rg++)
        {
            using ParquetRowGroupReader rgReader = reader.OpenRowGroupReader(rg);
            DataColumn col = await rgReader.ReadColumnAsync(field);
            recovered.AddRange(UnflattenByteList(col));
        }
        Assert.Equal(blobsPerBatch, recovered.Count);
        for (int i = 0; i < blobsPerBatch; i++)
        {
            Assert.Equal(MakeFakeImageBytes((byte)(0xA0 + i), blobSize), recovered[i]);
        }
    }

    [Fact]
    public async Task CopyToParquet_RowGroupByteBudgetOption_DrivesFlushTrigger()
    {
        // SQL-surface counterpart to LargeBlobsFlushMultipleRowGroups: the
        // ROW_GROUP_BYTE_BUDGET COPY option threads through ParquetExportFormat
        // into the sink. Wide rows + tight budget should produce multiple
        // row groups even when ROW_GROUP_SIZE leaves the row-count trigger
        // well above the data.
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        // 6 rows × 16 KB blob = ~96 KB total. Budget 32 KB → ≥3 row groups.
        const int blobSize = 16 * 1024;
        object?[][] rows = new object?[6][];
        for (int i = 0; i < 6; i++)
        {
            rows[i] = [i + 1, MakeFakeImageBytes((byte)(0x10 + i), blobSize)];
        }
        catalog.Add(new InMemoryTableProvider(
            pool, "public.media",
            columns: ["id", "clip"],
            columnKinds: [DataKind.Int32, DataKind.Audio],
            rows: rows));

        string outPath = Path.Combine(_scratchDir, "byte-budget.parquet");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT id, clip FROM public.media) TO '{EscapeSql(outPath)}' " +
            $"(FORMAT parquet, ROW_GROUP_SIZE 1000000, ROW_GROUP_BYTE_BUDGET 32768)");
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.True(File.Exists(outPath));
        await using FileStream readStream = File.OpenRead(outPath);
        using ParquetReader reader = await ParquetReader.CreateAsync(readStream);
        Assert.True(reader.RowGroupCount >= 3,
            $"Expected the ~96 KB payload to split across ≥3 row groups under a 32 KB budget; " +
            $"got {reader.RowGroupCount}.");
    }

    [Fact]
    public async Task CopyToParquet_RowGroupByteBudgetOption_RejectsNonPositiveValue()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.scalars",
            columns: ["id"],
            columnKinds: [DataKind.Int32],
            rows: [[1]]));

        string outPath = Path.Combine(_scratchDir, "bad-budget.parquet");
        ExportPlanException ex = await Assert.ThrowsAsync<ExportPlanException>(async () =>
            await catalog.PlanAsync(
                $"COPY (SELECT id FROM public.scalars) TO '{EscapeSql(outPath)}' " +
                $"(FORMAT parquet, ROW_GROUP_BYTE_BUDGET 0)"));
        Assert.Contains("ROW_GROUP_BYTE_BUDGET", ex.Message);
    }

    [Fact]
    public async Task CopyToParquet_RuntimeKindOverridesPlannerKindFallback()
    {
        // Regression: QuerySchemaResolver falls back to DataKind.String for
        // expressions it can't statically classify — notably model invocations
        // whose return kind isn't visible to the static resolver. The sink
        // used to trust that and build a String encoder that then crashed
        // with "Cannot read Mesh as String" when the actual runtime value
        // was a typed-media kind. ExportPlan now observes the first
        // non-empty batch and reconciles the schema before building the sink.
        //
        // Direct in-memory provider: declare the column as String at the
        // planner schema level, but populate it with raw byte[] cells that
        // the provider materialises as Image. Mirrors the COCO / models LET
        // shape without needing a model server.
        byte[] image1 = MakeFakeImageBytes(0x70, 64);
        byte[] image2 = MakeFakeImageBytes(0x80, 96);

        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.images",
            columns: ["id", "pic_mislabeled"],
            // Provider materialises pic_mislabeled as Image at scan time
            // even though we'll declare it as String to the COPY planner
            // (mirrors the model-LET case where the static resolver can't
            // figure out the kind and the helper falls back to String).
            columnKinds: [DataKind.Int32, DataKind.Image],
            rows:
            [
                [1, image1],
                [2, image2],
            ]));

        string outPath = Path.Combine(_scratchDir, "runtime-kind.parquet");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT id, pic_mislabeled FROM public.images) TO '{EscapeSql(outPath)}'");
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.True(File.Exists(outPath));
        await using FileStream readStream = File.OpenRead(outPath);
        using ParquetReader reader = await ParquetReader.CreateAsync(readStream);
        DataField[] fields = reader.Schema.GetDataFields();
        using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);
        DataColumn idColumn = await rg.ReadColumnAsync(fields[0]);
        DataColumn picColumn = await rg.ReadColumnAsync(fields[1]);

        // Image bytes survived the round trip — meaning the sink ended up
        // with the Image encoder (LIST<UInt8>) at runtime, not String.
        Assert.True(fields[1].IsArray, "Image column should have rebound to LIST<UInt8>.");
        byte[][] pics = UnflattenByteList(picColumn);
        Assert.Equal(image1, pics[0]);
        Assert.Equal(image2, pics[1]);
    }

    [Fact]
    public async Task CopyToParquet_AudioVideo_RoundTripBlobsViaSidecar()
    {
        // Audio / Video keep raw passthrough — their bytes (MP4 / WAV / etc.)
        // already are the universal interchange format, so the encoder hands
        // them through verbatim and the exported Parquet round-trips the
        // exact bytes. Pins the sidecar-registry resolution for both kinds.
        // Mesh / PointCloud are exercised separately because they go through
        // GltfExporter / PlyExporter on the way out (see the round-trip test).
        byte[] audio = MakeFakeImageBytes(0x30, 48);
        byte[] video = MakeFakeImageBytes(0x40, 128);

        FakeSidecarBlobSource blobs = new();
        long offAudio = blobs.Append(audio);
        long offVideo = blobs.Append(video);

        Pool pool = CreatePool();
        SidecarRegistry registry = new();
        byte storeId = registry.Register(blobs);

        Schema schema = new(
        [
            new ColumnInfo("audio_clip", DataKind.Audio, nullable: true),
            new ColumnInfo("video_clip", DataKind.Video, nullable: true),
        ]);
        ColumnLookup lookup = new(["audio_clip", "video_clip"]);
        using Heliosoph.DatumV.Model.Arena arena = new();
        using RowBatch batch = pool.RentRowBatch(lookup, capacity: 1, arena: arena);
        batch.Add(
        [
            DataValue.FromAudioInSidecar(offAudio, audio.Length, storeId),
            DataValue.FromVideoInSidecar(offVideo, video.Length, storeId),
        ]);

        string outPath = Path.Combine(_scratchDir, "typed-media.parquet");
        ParquetExportFormat format = new();
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

        await using FileStream readStream = File.OpenRead(outPath);
        using ParquetReader reader = await ParquetReader.CreateAsync(readStream);
        DataField[] fields = reader.Schema.GetDataFields();
        using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);

        Assert.Equal(audio, UnflattenByteList(await rg.ReadColumnAsync(fields[0]))[0]);
        Assert.Equal(video, UnflattenByteList(await rg.ReadColumnAsync(fields[1]))[0]);
    }

    [Fact]
    public async Task CopyToParquet_MeshAndPointCloud_RoundTripViaImporters()
    {
        // Mesh and PointCloud go through GltfExporter / PlyExporter on the way
        // out — the exported Parquet column carries .glb / PLY bytes that
        // Blender / MeshLab / Three.js / Open3D can consume directly. Round-
        // tripping back to a typed value goes through the matching
        // mesh_from_gltf / pointcloud_from_ply scalar functions, which is
        // what the user calls in `SELECT mesh_from_gltf(col) FROM
        // open_parquet(...)`. Pin the full export → re-import path so the
        // exporter / importer signatures stay in sync.
        Pool pool = CreatePool();
        SidecarRegistry registry = new();

        // Build a tiny valid PointCloud blob (1 colored point).
        byte[] cloudBlob = BuildTinyPointCloud(out Vector3 pcPoint, out (byte R, byte G, byte B) pcColor);
        // Build a tiny valid Mesh blob (3 vertices forming 1 triangle).
        byte[] meshBlob = BuildTinyMesh(out Vector3[] meshVerts);

        Schema schema = new(
        [
            new ColumnInfo("mesh", DataKind.Mesh, nullable: false),
            new ColumnInfo("cloud", DataKind.PointCloud, nullable: false),
        ]);
        ColumnLookup lookup = new(["mesh", "cloud"]);
        using Heliosoph.DatumV.Model.Arena arena = new();
        using RowBatch batch = pool.RentRowBatch(lookup, capacity: 1, arena: arena);
        batch.Add(
        [
            DataValue.FromMesh(meshBlob, arena),
            DataValue.FromPointCloud(cloudBlob, arena),
        ]);

        string outPath = Path.Combine(_scratchDir, "spatial-roundtrip.parquet");
        ParquetExportFormat format = new();
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

        // Read the exported bytes back, verify the format magic / header
        // (proving the export ran through the universal encoders), then
        // re-import through the matching importer and confirm the vertex
        // payload round-tripped.
        await using FileStream readStream = File.OpenRead(outPath);
        using ParquetReader reader = await ParquetReader.CreateAsync(readStream);
        DataField[] fields = reader.Schema.GetDataFields();
        using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);

        byte[] exportedMesh = UnflattenByteList(await rg.ReadColumnAsync(fields[0]))[0];
        byte[] exportedCloud = UnflattenByteList(await rg.ReadColumnAsync(fields[1]))[0];

        // .glb magic = "glTF" little-endian (0x46546C67); PLY starts with
        // the ASCII line "ply\n".
        Assert.Equal((byte)'g', exportedMesh[0]);
        Assert.Equal((byte)'l', exportedMesh[1]);
        Assert.Equal((byte)'T', exportedMesh[2]);
        Assert.Equal((byte)'F', exportedMesh[3]);
        Assert.Equal((byte)'p', exportedCloud[0]);
        Assert.Equal((byte)'l', exportedCloud[1]);
        Assert.Equal((byte)'y', exportedCloud[2]);

        // Round-trip back through the importers — the inverse half of the
        // exporter→importer pair the engine ships to make exports usable
        // both internally and in foreign tools.
        byte[] reimportedMesh = GltfImporter.Import(exportedMesh);
        byte[] reimportedCloud = PlyImporter.Import(exportedCloud);

        MeshHeader rehydratedMesh = MeshHeader.Read(reimportedMesh);
        Assert.Equal(3u, rehydratedMesh.VertexCount);
        Assert.Equal(1u, rehydratedMesh.TriangleCount);

        PointCloudHeader rehydratedCloud = PointCloudHeader.Read(reimportedCloud);
        Assert.Equal(1u, rehydratedCloud.PointCount);
        Assert.True(rehydratedCloud.HasColor);

        // First vertex of the rehydrated mesh blob — sanity-check that the
        // position survived the .glb round trip bit-for-bit (within float
        // precision; the export path does no quantisation).
        ReadOnlySpan<byte> meshPayload = reimportedMesh.AsSpan(MeshHeader.SizeBytes);
        float x = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(meshPayload[..4]);
        float y = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(meshPayload[4..8]);
        float z = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(meshPayload[8..12]);
        Assert.Equal(meshVerts[0].X, x, precision: 3);
        Assert.Equal(meshVerts[0].Y, y, precision: 3);
        Assert.Equal(meshVerts[0].Z, z, precision: 3);

        // First point of the rehydrated cloud blob — same sanity check.
        ReadOnlySpan<byte> cloudPayload = reimportedCloud.AsSpan(PointCloudHeader.SizeBytes);
        float px = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(cloudPayload[..4]);
        float py = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(cloudPayload[4..8]);
        float pz = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(cloudPayload[8..12]);
        Assert.Equal(pcPoint.X, px, precision: 3);
        Assert.Equal(pcPoint.Y, py, precision: 3);
        Assert.Equal(pcPoint.Z, pz, precision: 3);

        Assert.Equal(pcColor.R, cloudPayload[12]);
        Assert.Equal(pcColor.G, cloudPayload[13]);
        Assert.Equal(pcColor.B, cloudPayload[14]);
    }

    private static byte[] BuildTinyPointCloud(out Vector3 point, out (byte R, byte G, byte B) color)
    {
        point = new Vector3(1.5f, 2.5f, -3.5f);
        color = (200, 100, 50);

        PointCloudHeader header = new(
            PointCloudHeader.CurrentVersion,
            PointCloudFlags.HasColor,
            PointCloudCoordinateFrame.CameraOpenGl,
            PointCount: 1,
            BboxMin: point,
            BboxMax: point,
            Width: 0,
            Height: 0);

        int stride = PointCloudHeader.PositionStrideBytes + PointCloudHeader.ColorStrideBytes;
        byte[] blob = new byte[PointCloudHeader.SizeBytes + stride];
        header.Write(blob);

        Span<byte> payload = blob.AsSpan(PointCloudHeader.SizeBytes);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(payload[..4], point.X);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(payload[4..8], point.Y);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(payload[8..12], point.Z);
        payload[12] = color.R;
        payload[13] = color.G;
        payload[14] = color.B;
        payload[15] = 255;
        return blob;
    }

    private static byte[] BuildTinyMesh(out Vector3[] verts)
    {
        verts =
        [
            new Vector3(0f, 0f, 0f),
            new Vector3(1f, 0f, 0f),
            new Vector3(0f, 1f, 0f),
        ];

        Vector3 bboxMin = new(0f, 0f, 0f);
        Vector3 bboxMax = new(1f, 1f, 0f);

        MeshHeader header = new(
            MeshHeader.CurrentVersion,
            MeshFlags.None,
            PointCloudCoordinateFrame.CameraOpenGl,
            VertexCount: 3,
            TriangleCount: 1,
            BboxMin: bboxMin,
            BboxMax: bboxMax,
            TextureOffset: 0,
            TextureLength: 0);

        int vertexStride = MeshHeader.PositionStrideBytes;
        int payloadBytes = verts.Length * vertexStride + 1 * MeshHeader.IndexStrideBytes;
        byte[] blob = new byte[MeshHeader.SizeBytes + payloadBytes];
        header.Write(blob);

        Span<byte> payload = blob.AsSpan(MeshHeader.SizeBytes);
        int o = 0;
        foreach (Vector3 v in verts)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(payload.Slice(o + 0, 4), v.X);
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(payload.Slice(o + 4, 4), v.Y);
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(payload.Slice(o + 8, 4), v.Z);
            o += vertexStride;
        }
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload.Slice(o + 0, 4), 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload.Slice(o + 4, 4), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload.Slice(o + 8, 4), 2);
        return blob;
    }

    [Fact]
    public async Task CopyToParquet_RejectsDrawingColumn_WithRenderHint()
    {
        // Drawing is a procedural recipe (DrawingPayload), not bytes. Reject
        // at plan time and tell the caller to rasterise via render() — making
        // the conversion implicit would silently pick a size and surprise the
        // user. Pin both the rejection and the hint text.
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.recipes",
            columns: ["id", "scene"],
            columnKinds: [DataKind.Int32, DataKind.Drawing],
            rows: [[1, null]]));

        string outPath = Path.Combine(_scratchDir, "drawings.parquet");
        ExportPlanException ex = await Assert.ThrowsAsync<ExportPlanException>(async () =>
            await catalog.PlanAsync(
                $"COPY (SELECT id, scene FROM public.recipes) TO '{EscapeSql(outPath)}'"));
        Assert.Contains("kind Drawing", ex.Message);
        Assert.Contains("render(", ex.Message);
    }

    [Fact]
    public async Task CopyToParquet_SourceFromNonPublicSchema_HonorsSchemaQualifier()
    {
        // Regression: COPY (SELECT * FROM datasets.foo) TO ... used to error
        // with "Table 'public.foo' is not registered in the catalog." because
        // QuerySchemaResolver dropped the AST's SchemaName and routed every
        // unqualified lookup through the public-schema default. Pin a non-
        // public mounted schema so the broken path can't come back.
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();

        Heliosoph.DatumV.Catalog.ReadOnlyTableCatalog datasets = new(["datasets"]);
        catalog.MountSchemaBackend("datasets", datasets);
        datasets.Add(new InMemoryTableProvider(
            pool, "datasets.samples",
            columns: ["id", "label"],
            columnKinds: [DataKind.Int32, DataKind.String],
            rows:
            [
                [1, "alpha"],
                [2, "beta"],
            ]));

        string outPath = Path.Combine(_scratchDir, "datasets.parquet");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT id, label FROM datasets.samples) TO '{EscapeSql(outPath)}'");

        Assert.IsType<ExportPlan>(plan);
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.True(File.Exists(outPath));
        await using FileStream readStream = File.OpenRead(outPath);
        using ParquetReader reader = await ParquetReader.CreateAsync(readStream);
        Assert.Equal(1, reader.RowGroupCount);
        using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);
        Assert.Equal(2, rg.RowCount);
    }

    [Fact]
    public async Task CopyToParquet_InfersFormatFromExtension_WhenFormatOptionAbsent()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.scalars",
            columns: ["id"],
            columnKinds: [DataKind.Int32],
            rows: [[1], [2]]));

        string outPath = Path.Combine(_scratchDir, "by-extension.parquet");

        // No (FORMAT parquet) option, no option block at all — the planner
        // should still resolve the format from the .parquet extension.
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT id FROM public.scalars) TO '{EscapeSql(outPath)}'");

        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.True(File.Exists(outPath));
    }

    [Fact]
    public async Task CopyToParquet_RejectsUnknownFormatName()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.scalars",
            columns: ["id"],
            columnKinds: [DataKind.Int32],
            rows: [[1]]));

        string outPath = Path.Combine(_scratchDir, "x.weird");

        ExportPlanException ex = await Assert.ThrowsAsync<ExportPlanException>(async () =>
            await catalog.PlanAsync(
                $"COPY (SELECT id FROM public.scalars) TO '{EscapeSql(outPath)}' (FORMAT mystery)"));
        Assert.Contains("unknown format 'mystery'", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CopyToParquet_RejectsUnsupportedExtensionWithoutFormatOption()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.scalars",
            columns: ["id"],
            columnKinds: [DataKind.Int32],
            rows: [[1]]));

        string outPath = Path.Combine(_scratchDir, "x.unknown");

        ExportPlanException ex = await Assert.ThrowsAsync<ExportPlanException>(async () =>
            await catalog.PlanAsync(
                $"COPY (SELECT id FROM public.scalars) TO '{EscapeSql(outPath)}'"));
        Assert.Contains("cannot infer format from extension", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CopyToParquet_PrimitiveArrayColumns_RoundTripAsListT()
    {
        // Int32, Float64, and Boolean arrays — the LLM tokenization, embedding,
        // and feature-mask shapes that show up in HuggingFace dataset rows.
        // Drive the sink directly because InMemoryTableProvider doesn't surface
        // an Array<T> IsArray declaration through its columnKinds constructor;
        // a future end-to-end coverage pass against the SQL surface will catch
        // the planner side.
        Pool pool = CreatePool();
        SidecarRegistry registry = new();

        Schema schema = new(
        [
            new ColumnInfo("tokens", DataKind.Int32, nullable: false) { IsArray = true },
            new ColumnInfo("embedding", DataKind.Float64, nullable: false) { IsArray = true },
            new ColumnInfo("mask", DataKind.Boolean, nullable: false) { IsArray = true },
        ]);
        ColumnLookup lookup = new(["tokens", "embedding", "mask"]);
        using Heliosoph.DatumV.Model.Arena arena = new();
        using RowBatch batch = pool.RentRowBatch(lookup, capacity: 2, arena: arena);

        int[] tokens0 = [101, 202, 303];
        int[] tokens1 = [11, 22];
        double[] emb0 = [0.1, 0.2, 0.3, 0.4];
        double[] emb1 = [-0.5, 0.5];
        bool[] mask0 = [true, false, true];
        bool[] mask1 = [false, true];

        batch.Add(
        [
            DataValue.FromArenaArray<int>(tokens0, DataKind.Int32, arena),
            DataValue.FromArenaArray<double>(emb0, DataKind.Float64, arena),
            DataValue.FromArenaArray<bool>(mask0, DataKind.Boolean, arena),
        ]);
        batch.Add(
        [
            DataValue.FromArenaArray<int>(tokens1, DataKind.Int32, arena),
            DataValue.FromArenaArray<double>(emb1, DataKind.Float64, arena),
            DataValue.FromArenaArray<bool>(mask1, DataKind.Boolean, arena),
        ]);

        string outPath = Path.Combine(_scratchDir, "primitive-arrays.parquet");
        ParquetExportFormat format = new();
        await using (IExportSink sink = format.CreateSink(
            new ExportTarget.File(outPath),
            schema,
            [MediaDisposition.Inline, MediaDisposition.Inline, MediaDisposition.Inline],
            ExportOptions.Empty,
            registry))
        {
            await sink.WriteAsync(batch, default);
            await sink.FinishAsync(default);
        }

        await using FileStream readStream = File.OpenRead(outPath);
        using ParquetReader reader = await ParquetReader.CreateAsync(readStream);
        DataField[] fields = reader.Schema.GetDataFields();
        Assert.True(fields[0].IsArray, "tokens should round-trip as LIST<Int32>.");
        Assert.True(fields[1].IsArray, "embedding should round-trip as LIST<Float64>.");
        Assert.True(fields[2].IsArray, "mask should round-trip as LIST<Boolean>.");
        Assert.Equal(typeof(int), fields[0].ClrType);
        Assert.Equal(typeof(double), fields[1].ClrType);
        Assert.Equal(typeof(bool), fields[2].ClrType);

        using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);
        Assert.Equal(2, rg.RowCount);

        int[][] tokensRoundTrip = UnflattenPrimitiveList<int>(await rg.ReadColumnAsync(fields[0]));
        double[][] embRoundTrip = UnflattenPrimitiveList<double>(await rg.ReadColumnAsync(fields[1]));
        bool[][] maskRoundTrip = UnflattenPrimitiveList<bool>(await rg.ReadColumnAsync(fields[2]));

        Assert.Equal(tokens0, tokensRoundTrip[0]);
        Assert.Equal(tokens1, tokensRoundTrip[1]);
        Assert.Equal(emb0, embRoundTrip[0]);
        Assert.Equal(emb1, embRoundTrip[1]);
        Assert.Equal(mask0, maskRoundTrip[0]);
        Assert.Equal(mask1, maskRoundTrip[1]);
    }

    [Fact]
    public async Task CopyToParquet_StringArrayColumn_RoundTripsAsListString()
    {
        // Array<String> — the labels/tags/categorical shape. Reference-element
        // arrays go through StringArrayListEncoder, which resolves arena-backed
        // element bytes via AsStringArray(store).
        Pool pool = CreatePool();
        SidecarRegistry registry = new();

        Schema schema = new(
        [
            new ColumnInfo("tags", DataKind.String, nullable: false) { IsArray = true },
        ]);
        ColumnLookup lookup = new(["tags"]);
        using Heliosoph.DatumV.Model.Arena arena = new();
        using RowBatch batch = pool.RentRowBatch(lookup, capacity: 2, arena: arena);

        string[] tags0 = ["red", "small", "round"];
        string[] tags1 = ["blue", "large"];
        batch.Add([DataValue.FromStringArray(tags0, arena)]);
        batch.Add([DataValue.FromStringArray(tags1, arena)]);

        string outPath = Path.Combine(_scratchDir, "string-arrays.parquet");
        ParquetExportFormat format = new();
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

        await using FileStream readStream = File.OpenRead(outPath);
        using ParquetReader reader = await ParquetReader.CreateAsync(readStream);
        DataField[] fields = reader.Schema.GetDataFields();
        Assert.True(fields[0].IsArray);
        Assert.Equal(typeof(string), fields[0].ClrType);

        using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);
        string[][] tagsRoundTrip = UnflattenStringList(await rg.ReadColumnAsync(fields[0]));
        Assert.Equal(tags0, tagsRoundTrip[0]);
        Assert.Equal(tags1, tagsRoundTrip[1]);
    }

    /// <summary>
    /// Reconstructs per-row <c>T[]</c> sub-arrays from a Parquet
    /// <c>LIST&lt;T&gt;</c> column — the typed counterpart to
    /// <see cref="UnflattenByteList"/>. Parquet.Net widens the per-element
    /// CLR type to <c>Nullable&lt;T&gt;</c> for primitive list columns even
    /// when the field declares <c>isNullable: false</c>; collapse those to
    /// the underlying value-type array.
    /// </summary>
    private static T[][] UnflattenPrimitiveList<T>(DataColumn column) where T : struct
    {
        T[] flat;
        switch (column.Data)
        {
            case T[] direct:
                flat = direct;
                break;
            case T?[] nullable:
                flat = new T[nullable.Length];
                for (int i = 0; i < nullable.Length; i++) flat[i] = nullable[i] ?? default;
                break;
            default:
                throw new InvalidOperationException(
                    $"Column '{column.Field.Name}' has unexpected data shape: {column.Data.GetType().Name}");
        }

        int[] rep = column.RepetitionLevels ?? throw new InvalidOperationException(
            $"Column '{column.Field.Name}' is missing repetition levels — expected a LIST<T> shape.");

        int rowCount = 0;
        for (int i = 0; i < rep.Length; i++) if (rep[i] == 0) rowCount++;
        T[][] result = new T[rowCount][];

        int rowIndex = -1;
        int rowStart = 0;
        for (int i = 0; i < rep.Length; i++)
        {
            if (rep[i] == 0)
            {
                if (rowIndex >= 0) result[rowIndex] = flat[rowStart..i];
                rowIndex++;
                rowStart = i;
            }
        }
        if (rowIndex >= 0) result[rowIndex] = flat[rowStart..rep.Length];
        return result;
    }

    /// <summary>
    /// <see cref="UnflattenPrimitiveList{T}"/> for string-element columns —
    /// the CLR widening to <c>string?[]</c> means we copy through nullable
    /// strings, collapsing <c>null</c> to empty for assertion convenience.
    /// </summary>
    private static string[][] UnflattenStringList(DataColumn column)
    {
        // Parquet.Net hands LIST<String> data back as string?[] at the runtime
        // level — same array shape as string[] under nullable reference types,
        // so a single cast covers both. Copy through to coalesce trailing
        // nulls (Parquet.Net widens to ensure a stable column shape even when
        // the field declares isNullable: false).
        string?[] raw = (string?[])column.Data;
        string[] flat = new string[raw.Length];
        for (int i = 0; i < raw.Length; i++) flat[i] = raw[i] ?? string.Empty;

        int[] rep = column.RepetitionLevels ?? throw new InvalidOperationException(
            $"Column '{column.Field.Name}' is missing repetition levels — expected a LIST<String> shape.");

        int rowCount = 0;
        for (int i = 0; i < rep.Length; i++) if (rep[i] == 0) rowCount++;
        string[][] result = new string[rowCount][];

        int rowIndex = -1;
        int rowStart = 0;
        for (int i = 0; i < rep.Length; i++)
        {
            if (rep[i] == 0)
            {
                if (rowIndex >= 0) result[rowIndex] = flat[rowStart..i];
                rowIndex++;
                rowStart = i;
            }
        }
        if (rowIndex >= 0) result[rowIndex] = flat[rowStart..rep.Length];
        return result;
    }

    [Fact]
    public async Task CopyToParquet_ArrayOfStructDirect_RoundTripsPerLeaf()
    {
        // Direct-sink probe for Array<Struct>: skip the SQL pipeline so any
        // failure here pins the encoder itself. One outer row carrying a
        // one-element Array<Struct{name: String, count: Int32}> array — the
        // simplest shape that exercises both reference and value leaf
        // channels.
        Pool pool = CreatePool();
        SidecarRegistry registry = new();

        ColumnInfo[] childFields =
        [
            new("name", DataKind.String, nullable: false),
            new("count", DataKind.Int32, nullable: false),
        ];
        Schema schema = new(
        [
            new ColumnInfo("items", nullable: false, fields: childFields) { IsArray = true },
        ]);
        ColumnLookup lookup = new(["items"]);
        using Heliosoph.DatumV.Model.Arena arena = new();
        using RowBatch batch = pool.RentRowBatch(lookup, capacity: 1, arena: arena);

        DataValue[][] elements =
        [
            [DataValue.FromString("the test", arena), DataValue.FromInt32(123)],
        ];
        batch.Add([DataValue.FromUntypedStructArray(elements, arena)]);

        string outPath = Path.Combine(_scratchDir, "array-struct-direct.parquet");
        ParquetExportFormat format = new();
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

        await using FileStream readStream = File.OpenRead(outPath);
        using ParquetReader reader = await ParquetReader.CreateAsync(readStream);
        Assert.Single(reader.Schema.Fields);
        ListField list = Assert.IsType<ListField>(reader.Schema.Fields[0]);
        StructField item = Assert.IsType<StructField>(list.Item);
        DataField nameLeaf = (DataField)item.Fields[0];
        DataField countLeaf = (DataField)item.Fields[1];

        using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);
        DataColumn nameCol = await rg.ReadColumnAsync(nameLeaf);
        DataColumn countCol = await rg.ReadColumnAsync(countLeaf);
        Assert.Equal("the test", ((string?[])nameCol.Data)[0]);
        Assert.Equal(123, ConvertToNonNullable<int>(countCol.Data)[0]);
    }

    [Fact]
    public async Task CopyToParquet_ArrayOfStructLiteral_WritesListOfStructAndRoundTripsBytes()
    {
        // Mirrors the user-reported SQL:
        //   SELECT { a: 1, b: 'b' }, [{ field1: 'the test', field2: 123 }]
        // The struct literal projection survives (covered elsewhere); this
        // test pins the array-literal-of-struct path so the on-disk shape is
        // a Parquet LIST<STRUCT<...>> and the per-element field values
        // round-trip through Parquet.Net's reader.
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.dummy",
            columns: ["k"],
            columnKinds: [DataKind.Int32],
            rows: [[1]]));

        string outPath = Path.Combine(_scratchDir, "array-of-struct.parquet");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT {{ a: 1, b: 'b' }}, [{{ field1: 'the test', field2: 123 }}] FROM public.dummy) " +
            $"TO '{EscapeSql(outPath)}' (FORMAT parquet)");
        Assert.IsType<ExportPlan>(plan);
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.True(File.Exists(outPath));
        await using FileStream readStream = File.OpenRead(outPath);
        using ParquetReader reader = await ParquetReader.CreateAsync(readStream);

        // Two top-level columns: STRUCT scalar + LIST<STRUCT>.
        Assert.Equal(2, reader.Schema.Fields.Count);
        Assert.IsType<StructField>(reader.Schema.Fields[0]);
        ListField list = Assert.IsType<ListField>(reader.Schema.Fields[1]);
        Assert.Equal("array", list.Name);
        StructField listItem = Assert.IsType<StructField>(list.Item);
        Assert.Equal(2, listItem.Fields.Count);
        Assert.Equal("field1", listItem.Fields[0].Name);
        Assert.Equal("field2", listItem.Fields[1].Name);

        // Read each leaf back: per row's one-element list should carry
        // ('the test', 123) under the matching rep / def levels.
        using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);
        DataField field1Leaf = (DataField)listItem.Fields[0];
        DataField field2Leaf = (DataField)listItem.Fields[1];
        DataColumn field1Col = await rg.ReadColumnAsync(field1Leaf);
        DataColumn field2Col = await rg.ReadColumnAsync(field2Leaf);

        Assert.Equal("the test", ((string?[])field1Col.Data)[0]);
        // SQL parses small integer literals as Int8 — the on-disk column lifts
        // to Parquet INT32 but Parquet.Net surfaces the data back as sbyte[]
        // when that's what the DataField was declared as. Compare via boxed
        // object equality which works across the numeric promotion gap.
        Assert.Equal((sbyte)123, field2Col.Data.GetValue(0));

        // Now read it back through open_parquet so the LIST<STRUCT> read
        // path is exercised end-to-end. Each outer row should carry a
        // 1-element Array<Struct{field1, field2}> with the right values.
        OpenParquetFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectRowsAsync(
            ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(outPath)], ctx), ctx);
        Assert.Single(rows);
        DataValue arr = rows[0]["array"];
        Assert.Equal(DataKind.Struct, arr.Kind);
        Assert.True(arr.IsArray, "Top-level LIST<STRUCT> should round-trip as Array<Struct>.");
        DataValue[] elements = arr.AsStructArray(ctx.Store);
        Assert.Single(elements);
        DataValue[] elementFields = elements[0].AsStruct(ctx.Store);
        Assert.Equal(2, elementFields.Length);
        Assert.Equal("the test", elementFields[0].AsString(ctx.Store));
        Assert.Equal((sbyte)123, elementFields[1].AsInt8());
    }

    [Fact]
    public async Task CopyToParquet_NullableImageColumn_RoundTripsNullsAndPresentRows()
    {
        // Nullable typed-media columns now surface as `optional` LIST<UInt8>
        // on disk — present rows carry their bytes, NULL rows survive the
        // round trip as DataValue.Null(Image) instead of throwing at append
        // time. Drive the sink directly because InMemoryTableProvider would
        // re-materialise the null cell into the per-batch arena before
        // reaching the sink; the real ingestion-side scan path produces
        // DataValue.Null at column scope, which is what the sink should
        // accept.
        byte[] presentBytes = MakeFakeImageBytes(0x42, 64);

        Pool pool = CreatePool();
        SidecarRegistry registry = new();
        Schema schema = new(
        [
            new ColumnInfo("id", DataKind.Int32, nullable: false),
            new ColumnInfo("pic", DataKind.Image, nullable: true),
        ]);
        ColumnLookup lookup = new(["id", "pic"]);
        using Heliosoph.DatumV.Model.Arena arena = new();
        using RowBatch batch = pool.RentRowBatch(lookup, capacity: 3, arena: arena);
        batch.Add([DataValue.FromInt32(1), DataValue.FromImage(presentBytes, arena)]);
        batch.Add([DataValue.FromInt32(2), DataValue.Null(DataKind.Image)]);
        batch.Add([DataValue.FromInt32(3), DataValue.FromImage(presentBytes, arena)]);

        string outPath = Path.Combine(_scratchDir, "nullable-image.parquet");
        ParquetExportFormat format = new();
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

        // Round-trip via open_parquet so the read side's nullable-array
        // walker is exercised end-to-end (rows 0 and 2 carry bytes; row 1
        // carries a typed Image NULL).
        OpenParquetFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectRowsAsync(
            ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(outPath)], ctx), ctx);
        Assert.Equal(3, rows.Count);
        Assert.Equal(presentBytes, rows[0]["pic"].AsImage(ctx.Store));
        Assert.True(rows[1]["pic"].IsNull, "Row 1 should round-trip as NULL Image.");
        Assert.Equal(DataKind.Image, rows[1]["pic"].Kind);
        Assert.Equal(presentBytes, rows[2]["pic"].AsImage(ctx.Store));
    }

    [Fact]
    public async Task CopyToParquet_JsonColumn_RoundTripsAsCanonicalJsonTextViaCborCodec()
    {
        // Json columns export as a plain UTF-8 string column so pandas /
        // DuckDB / Spark / Polars read the values as ordinary text out of
        // the box. The datumv.kind=Json + datumv.format=text tag tells
        // open_parquet to re-encode the text back to canonical CBOR so the
        // engine's DataKind.Json contract (bytes are CBOR) survives the
        // round trip. Drive the sink directly so the test can stage real
        // CBOR-backed Json values.
        Pool pool = CreatePool();
        SidecarRegistry registry = new();

        byte[] cbor0 = Heliosoph.DatumV.Functions.Json.CborJsonCodec
            .EncodeFromJsonText("{\"a\":1,\"b\":\"hello\"}");
        byte[] cbor1 = Heliosoph.DatumV.Functions.Json.CborJsonCodec
            .EncodeFromJsonText("[1,2,3]");

        Schema schema = new(
        [
            new ColumnInfo("id", DataKind.Int32, nullable: false),
            new ColumnInfo("payload", DataKind.Json, nullable: false),
        ]);
        ColumnLookup lookup = new(["id", "payload"]);
        using Heliosoph.DatumV.Model.Arena arena = new();
        using RowBatch batch = pool.RentRowBatch(lookup, capacity: 2, arena: arena);
        batch.Add([DataValue.FromInt32(1), DataValue.FromJson(cbor0, arena)]);
        batch.Add([DataValue.FromInt32(2), DataValue.FromJson(cbor1, arena)]);

        string outPath = Path.Combine(_scratchDir, "json-roundtrip.parquet");
        ParquetExportFormat format = new();
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

        // Direct Parquet.Net read first: the on-disk column should be a
        // plain UTF-8 string, not a LIST<UInt8> shortcut — that's the
        // external-tool contract.
        await using FileStream readStream = File.OpenRead(outPath);
        using ParquetReader reader = await ParquetReader.CreateAsync(readStream);
        DataField payloadLeaf = Assert.IsType<DataField>(reader.Schema.Fields[1]);
        Assert.Equal(typeof(string), payloadLeaf.ClrType);
        using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);
        DataColumn payloadCol = await rg.ReadColumnAsync(payloadLeaf);
        string?[] rawTexts = (string?[])payloadCol.Data;
        Assert.Equal("{\"a\":1,\"b\":\"hello\"}", rawTexts[0]);
        Assert.Equal("[1,2,3]", rawTexts[1]);

        // Now read through open_parquet — the datumv.kind=Json tag should
        // re-encode each text payload back to canonical CBOR so the column
        // surfaces as DataKind.Json with the original byte payload.
        OpenParquetFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectRowsAsync(
            ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(outPath)], ctx), ctx);
        Assert.Equal(2, rows.Count);
        Assert.Equal(DataKind.Json, rows[0]["payload"].Kind);
        Assert.Equal(cbor0, rows[0]["payload"].AsByteSpan(ctx.Store).ToArray());
        Assert.Equal(cbor1, rows[1]["payload"].AsByteSpan(ctx.Store).ToArray());
    }

    [Fact]
    public async Task CopyToParquet_StructWithListStringChild_WritesProperListFieldNotByteArrayShortcut()
    {
        // User-reported regression: a scalar STRUCT containing an
        // Array<String> child used to encode the list child through the
        // standalone-LIST shortcut (DataField with isArray=true), which
        // Parquet.Net mis-serialises when the field is nested inside a
        // StructField — the on-disk column came back as raw bytes. The
        // sink now wires struct list children through a real ListField
        // inside the wrapping StructField, so the column round-trips as
        // LIST<String> and reads back as a per-element string array.
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.dummy",
            columns: ["k"],
            columnKinds: [DataKind.Int32],
            rows: [[1]]));

        string outPath = Path.Combine(_scratchDir, "struct-with-list.parquet");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT {{ a: 1, b: 'b', h: ['test', 'asdf', 'sdf'] }} FROM public.dummy) " +
            $"TO '{EscapeSql(outPath)}' (FORMAT parquet)");
        Assert.IsType<ExportPlan>(plan);
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.True(File.Exists(outPath));
        await using FileStream readStream = File.OpenRead(outPath);
        using ParquetReader reader = await ParquetReader.CreateAsync(readStream);
        Assert.Single(reader.Schema.Fields);
        StructField structField = Assert.IsType<StructField>(reader.Schema.Fields[0]);
        Assert.Equal(3, structField.Fields.Count);
        // Parquet.Net's reader collapses LIST<primitive> back to its
        // DataField(isArray: true) shortcut for ergonomic access, even
        // when the on-disk schema is a proper ListField — what matters
        // is that the column carries an array shape and the per-element
        // bytes round-trip as strings rather than the raw-byte garbage
        // the pre-fix code produced.
        // The critical assertion — h must surface as a real LIST<String>
        // on disk (not the DataField byte-shortcut that mis-encodes when
        // nested in a STRUCT and produced the user-reported byte garbage).
        ListField hList = Assert.IsType<ListField>(structField.Fields[2]);
        Assert.Equal("h", hList.Name);
        DataField hLeaf = Assert.IsType<DataField>(hList.Item);
        Assert.Equal(typeof(string), hLeaf.ClrType);

        using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);
        DataColumn hCol = await rg.ReadColumnAsync(hLeaf);
        string?[] raw = (string?[])hCol.Data;
        string[] hData = new string[raw.Length];
        for (int i = 0; i < raw.Length; i++) hData[i] = raw[i] ?? string.Empty;
        Assert.Equal(["test", "asdf", "sdf"], hData);
        // Three elements, all under the same outer row → rep levels [0,1,1].
        Assert.NotNull(hCol.RepetitionLevels);
        Assert.Equal([0, 1, 1], hCol.RepetitionLevels);

        // Read it back through open_parquet — the read side now unwraps
        // LIST<primitive> struct children and surfaces them as Array<T>
        // ColumnInfo fields, so a typed value comes back instead of the
        // pre-fix "nested LIST not supported" error.
        OpenParquetFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectRowsAsync(
            ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(outPath)], ctx), ctx);
        Assert.Single(rows);
        DataValue structVal = rows[0]["struct"];
        Assert.Equal(DataKind.Struct, structVal.Kind);
        DataValue[] structFields = structVal.AsStruct(ctx.Store);
        // structFields[2] is `h`, the LIST<String> child.
        DataValue h = structFields[2];
        Assert.Equal(DataKind.String, h.Kind);
        Assert.True(h.IsArray, "Round-tripped LIST<String> struct child should carry IsArray=true.");
        Assert.Equal(["test", "asdf", "sdf"], h.AsStringArray(ctx.Store));
    }

    [Fact]
    public async Task CopyToParquet_StructLiteralProjection_WiresStructFieldsThroughResolver()
    {
        // Regression for the user-reported "Struct but no field metadata"
        // error on `COPY (SELECT { a: 1, b: 'b' }) TO ...`: the projection's
        // struct shape now flows through QuerySchemaResolver as
        // ResolvedColumn.Fields and ExportPlan.BuildSchema turns it back
        // into a struct ColumnInfo so the sink can build a real StructField.
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.dummy",
            columns: ["k"],
            columnKinds: [DataKind.Int32],
            rows: [[1]]));

        string outPath = Path.Combine(_scratchDir, "struct-literal.parquet");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT {{ a: 1, b: 'b' }} FROM public.dummy) TO '{EscapeSql(outPath)}' (FORMAT parquet)");
        Assert.IsType<ExportPlan>(plan);
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.True(File.Exists(outPath));
        await using FileStream readStream = File.OpenRead(outPath);
        using ParquetReader reader = await ParquetReader.CreateAsync(readStream);
        // The projection's struct literal lands as a top-level STRUCT
        // Parquet field with two children matching the literal's shape.
        Assert.Single(reader.Schema.Fields);
        StructField structField = Assert.IsType<StructField>(reader.Schema.Fields[0]);
        Assert.Equal("struct", structField.Name);
        Assert.Equal(2, structField.Fields.Count);
        Assert.Equal("a", structField.Fields[0].Name);
        Assert.Equal("b", structField.Fields[1].Name);
    }

    [Fact]
    public async Task CopyToParquet_StructColumn_RoundTripsThroughOpenParquet()
    {
        // COCO-shape bbox: one Int32 id column and a top-level Struct column
        // with four Float32 children (x, y, w, h). Drive the sink directly
        // because InMemoryTableProvider doesn't surface struct ColumnInfo
        // declarations through its constructors. End-to-end coverage:
        // write through ParquetExportSink + read through ParquetReader to
        // pin the Parquet schema shape, and then back through open_parquet
        // to confirm the read side reassembles the Struct DataValue.
        Pool pool = CreatePool();
        SidecarRegistry registry = new();

        ColumnInfo[] bboxFields =
        [
            new("x", DataKind.Float32, nullable: false),
            new("y", DataKind.Float32, nullable: false),
            new("w", DataKind.Float32, nullable: false),
            new("h", DataKind.Float32, nullable: false),
        ];
        Schema schema = new(
        [
            new ColumnInfo("id", DataKind.Int32, nullable: false),
            new ColumnInfo("bbox", nullable: false, fields: bboxFields),
        ]);
        ColumnLookup lookup = new(["id", "bbox"]);
        using Heliosoph.DatumV.Model.Arena arena = new();
        using RowBatch batch = pool.RentRowBatch(lookup, capacity: 2, arena: arena);
        batch.Add(
        [
            DataValue.FromInt32(1),
            DataValue.FromUntypedStruct(
                [DataValue.FromFloat32(10f), DataValue.FromFloat32(20f),
                 DataValue.FromFloat32(30f), DataValue.FromFloat32(40f)],
                arena),
        ]);
        batch.Add(
        [
            DataValue.FromInt32(2),
            DataValue.FromUntypedStruct(
                [DataValue.FromFloat32(50f), DataValue.FromFloat32(60f),
                 DataValue.FromFloat32(70f), DataValue.FromFloat32(80f)],
                arena),
        ]);

        string outPath = Path.Combine(_scratchDir, "struct-roundtrip.parquet");
        ParquetExportFormat format = new();
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

        // First confirm the on-disk schema declares the struct: top-level
        // fields should be `id` + `bbox`, with bbox carrying four children.
        await using FileStream readStream = File.OpenRead(outPath);
        using ParquetReader reader = await ParquetReader.CreateAsync(readStream);
        Assert.Equal(2, reader.Schema.Fields.Count);
        Assert.IsType<DataField>(reader.Schema.Fields[0]);
        StructField bbox = Assert.IsType<StructField>(reader.Schema.Fields[1]);
        Assert.Equal("bbox", bbox.Name);
        Assert.Equal(4, bbox.Fields.Count);
        Assert.Equal("x", bbox.Fields[0].Name);
        Assert.Equal("h", bbox.Fields[3].Name);

        // Now round-trip through open_parquet so the Struct DataValue
        // reassembly path runs against this file.
        OpenParquetFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        List<Row> rows = await CollectRowsAsync(
            ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(outPath)], ctx), ctx);

        Assert.Equal(2, rows.Count);
        DataValue bbox0 = rows[0]["bbox"];
        DataValue bbox1 = rows[1]["bbox"];
        Assert.Equal(DataKind.Struct, bbox0.Kind);
        DataValue[] fields0 = bbox0.AsStruct(ctx.Store);
        Assert.Equal(10f, fields0[0].AsFloat32());
        Assert.Equal(40f, fields0[3].AsFloat32());
        DataValue[] fields1 = bbox1.AsStruct(ctx.Store);
        Assert.Equal(50f, fields1[0].AsFloat32());
        Assert.Equal(80f, fields1[3].AsFloat32());
    }

    /// <summary>
    /// Drains a TVF result stream into a flat <see cref="Row"/> list,
    /// stabilizing every <see cref="DataValue"/> against the supplied
    /// <see cref="ExecutionContext"/>'s store so the assertions can read
    /// values after the per-batch arena disposes.
    /// </summary>
    private static async Task<List<Row>> CollectRowsAsync(IAsyncEnumerable<RowBatch> batches, ExecutionContext ctx)
    {
        List<Row> rows = [];
        await foreach (RowBatch b in batches)
        {
            for (int i = 0; i < b.Count; i++)
            {
                Row source = b[i];
                DataValue[] stabilized = new DataValue[source.FieldCount];
                for (int f = 0; f < source.FieldCount; f++)
                {
                    stabilized[f] = DataValueRetention.Stabilize(source[f], b.Arena, ctx.Store);
                }
                rows.Add(new Row(source.ColumnLookup, stabilized));
            }
        }
        return rows;
    }

    [Theory]
    [InlineData("none")]
    [InlineData("snappy")]
    [InlineData("gzip")]
    [InlineData("zstd")]
    [InlineData("brotli")]
    [InlineData("lz4")]
    public async Task CopyToParquet_CompressionCodecOption_RoundTripsThroughEveryCodec(string codec)
    {
        // Every supported codec writes a valid file that reads back to the
        // same payload. The compressed-vs-uncompressed payload size delta
        // is intentionally not asserted — Parquet.Net's snappy / lz4 paths
        // sometimes produce *larger* output for tiny payloads because the
        // per-page framing overhead dominates. Tools verify by re-reading
        // the file, which is what this does.
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.notes",
            columns: ["id", "text"],
            columnKinds: [DataKind.Int32, DataKind.String],
            rows:
            [
                [1, "alpha alpha alpha alpha alpha alpha alpha alpha"],
                [2, "beta beta beta beta beta beta beta beta beta beta"],
                [3, "gamma gamma gamma gamma gamma gamma gamma gamma"],
            ]));

        string outPath = Path.Combine(_scratchDir, $"notes-{codec}.parquet");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT id, text FROM public.notes) TO '{EscapeSql(outPath)}' " +
            $"(FORMAT parquet, COMPRESSION '{codec}')");

        Assert.IsType<ExportPlan>(plan);
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.True(File.Exists(outPath), $"export with COMPRESSION '{codec}' produced no file.");

        await using FileStream readStream = File.OpenRead(outPath);
        using ParquetReader reader = await ParquetReader.CreateAsync(readStream);
        DataField[] fields = reader.Schema.GetDataFields();
        Assert.Equal(2, fields.Length);

        using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);
        Assert.Equal(3, rg.RowCount);

        DataColumn idCol = await rg.ReadColumnAsync(fields[0]);
        DataColumn textCol = await rg.ReadColumnAsync(fields[1]);
        Assert.Equal([1, 2, 3], ConvertToNonNullable<int>(idCol.Data));
        string[] texts = ConvertToReferenceArray<string>(textCol.Data);
        Assert.Equal(3, texts.Length);
        Assert.StartsWith("alpha", texts[0]);
        Assert.StartsWith("beta", texts[1]);
        Assert.StartsWith("gamma", texts[2]);
    }

    [Fact]
    public async Task CopyToParquet_UnknownCompressionCodec_ThrowsExportPlanException()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.notes",
            columns: ["id"],
            columnKinds: [DataKind.Int32],
            rows: [[1]]));

        string outPath = Path.Combine(_scratchDir, "notes-bad-codec.parquet");
        ExportPlanException ex = await Assert.ThrowsAsync<ExportPlanException>(async () =>
            await catalog.PlanAsync(
                $"COPY (SELECT id FROM public.notes) TO '{EscapeSql(outPath)}' " +
                $"(FORMAT parquet, COMPRESSION 'lzma')"));
        Assert.Contains("COMPRESSION", ex.Message);
        Assert.Contains("lzma", ex.Message);
    }

    private static byte[] MakeFakeImageBytes(byte fillByte, int length)
    {
        // Not a real PNG — the sink writes raw BYTE_ARRAY, so it never decodes.
        // Each fixture is just a recognisable byte pattern that the round-trip
        // assertion can compare against.
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)(fillByte ^ i);
        return bytes;
    }

    /// <summary>
    /// Parquet.Net's <see cref="DataColumn.Data"/> for a nullable value-type
    /// column returns <c>T?[]</c>; for a non-nullable column it returns
    /// <c>T[]</c>. The export sink writes Int32 with <see cref="ColumnInfo.Nullable"/>
    /// = <c>true</c> (the planner's default when the source is an
    /// in-memory provider), so the read path lands a <c>int?[]</c>. Collapse
    /// to <c>int[]</c> for assertion convenience — the test fixtures never
    /// emit nulls.
    /// </summary>
    private static T[] ConvertToNonNullable<T>(Array raw) where T : struct
    {
        if (raw is T[] direct) return direct;
        // Parquet.Net returns nullable value-type columns as `T?[]`. The
        // `raw is T?[]` pattern binds cleanly for some Ts (int, byte) but
        // not reliably for DateTime / DateOnly / TimeOnly / Guid / decimal
        // under the generic specialisation rules — fall through to a
        // reflection-driven copy that boxes each element.
        T[] result = new T[raw.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            object? boxed = raw.GetValue(i);
            result[i] = boxed is null ? default : (T)boxed;
        }
        return result;
    }

    private static T[] ConvertToReferenceArray<T>(Array raw) where T : class
    {
        if (raw is T[] direct) return direct;
        if (raw is T?[] nullable)
        {
            T[] result = new T[nullable.Length];
            for (int i = 0; i < nullable.Length; i++) result[i] = nullable[i]!;
            return result;
        }
        throw new InvalidOperationException(
            $"Unexpected column data shape: {raw.GetType().Name}");
    }

    /// <summary>
    /// Reconstructs per-row <c>byte[]</c> sub-arrays from a Parquet
    /// <c>LIST&lt;UInt8&gt;</c> column. The column's flat <c>Data</c> array
    /// holds every row's bytes concatenated; <c>RepetitionLevels[i] == 0</c>
    /// marks the first element of a new row's list. Mirrors what the engine's
    /// own <c>ParquetColumnReader.ReadArrayColumn</c> does internally.
    /// </summary>
    private static byte[][] UnflattenByteList(DataColumn column)
    {
        // Parquet.Net hands LIST<UInt8> data back as byte?[] (element type
        // is always lifted to Nullable<T> for LIST element positions, even
        // when the underlying field declares isNullable=false). Collapse to
        // a plain byte[] for the per-row slice helper — the writer never
        // emits nulls inside a list.
        byte[] flat;
        switch (column.Data)
        {
            case byte[] direct:
                flat = direct;
                break;
            case byte?[] nullable:
                flat = new byte[nullable.Length];
                for (int i = 0; i < nullable.Length; i++) flat[i] = nullable[i] ?? 0;
                break;
            default:
                throw new InvalidOperationException(
                    $"Column '{column.Field.Name}' has unexpected data shape: {column.Data.GetType().Name}");
        }

        int[] rep = column.RepetitionLevels ?? throw new InvalidOperationException(
            $"Column '{column.Field.Name}' is missing repetition levels — expected a LIST<UInt8> shape.");

        int rowCount = 0;
        for (int i = 0; i < rep.Length; i++) if (rep[i] == 0) rowCount++;
        byte[][] result = new byte[rowCount][];

        int rowIndex = -1;
        int rowStart = 0;
        for (int i = 0; i < rep.Length; i++)
        {
            if (rep[i] == 0)
            {
                if (rowIndex >= 0)
                {
                    result[rowIndex] = flat[rowStart..i];
                }
                rowIndex++;
                rowStart = i;
            }
        }
        if (rowIndex >= 0)
        {
            result[rowIndex] = flat[rowStart..rep.Length];
        }
        return result;
    }

    private static string EscapeSql(string path) => path.Replace("'", "''");

    /// <summary>
    /// In-memory IBlobSource backing the sidecar-Image regression test.
    /// Appends bytes to an internal buffer and returns the offset; reads
    /// by absolute offset + length. Mirrors the read shape of a real
    /// <see cref="SidecarReadStore"/> without touching the disk.
    /// </summary>
    private sealed class FakeSidecarBlobSource : IBlobSource
    {
        private readonly List<byte> _buf = [];

        public long Append(byte[] payload)
        {
            long offset = _buf.Count;
            _buf.AddRange(payload);
            return offset;
        }

        public ReadOnlySpan<byte> Read(long offset, long length)
            => CollectionsMarshal.AsSpan(_buf).Slice((int)offset, (int)length);

        public void Dispose() { }
    }
}
