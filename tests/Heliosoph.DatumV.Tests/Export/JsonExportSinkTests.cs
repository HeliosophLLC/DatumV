using System.Globalization;
using System.Text.Json;
using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Catalog.Plans;
using Heliosoph.DatumV.Catalog.Providers;
using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.Export;
using Heliosoph.DatumV.Export.Json;
using Heliosoph.DatumV.Functions.Json;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Tests.Export;

/// <summary>
/// End-to-end exercise of <c>COPY (query) TO 'path.json'</c> — the parser,
/// <see cref="ExportPlan"/>, <see cref="Export.Json.JsonExportFormat"/>,
/// and <see cref="Export.Json.JsonExportSink"/>. Validates both shapes
/// (array vs JSONL), structure-preservation for struct / array / json
/// columns (the thing that distinguishes JSON from CSV), schema-aware
/// struct field names, and plan-time rejection of typed-media columns
/// that JSON cannot represent.
/// </summary>
public sealed class JsonExportSinkTests : ServiceTestBase, IDisposable
{
    private readonly string _scratchDir;

    public JsonExportSinkTests()
    {
        _scratchDir = Path.Combine(Path.GetTempPath(), $"datum-json-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchDir);
    }

    public new void Dispose()
    {
        base.Dispose();
        try { if (Directory.Exists(_scratchDir)) Directory.Delete(_scratchDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task CopyToJson_ScalarsOnly_WritesTopLevelArray()
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

        string outPath = Path.Combine(_scratchDir, "scalars.json");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT id, name, score FROM public.scalars) TO '{EscapeSql(outPath)}'");
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(outPath));
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(3, doc.RootElement.GetArrayLength());

        JsonElement row1 = doc.RootElement[0];
        Assert.Equal(1, row1.GetProperty("id").GetInt32());
        Assert.Equal("alice", row1.GetProperty("name").GetString());
        Assert.Equal(0.10, row1.GetProperty("score").GetDouble(), 5);
    }

    [Fact]
    public async Task CopyToJson_NullValues_EmitJsonNull()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.nullable",
            columns: ["id", "name"],
            columnKinds: [DataKind.Int32, DataKind.String],
            rows: [[1, null], [null, "bob"]]));

        string outPath = Path.Combine(_scratchDir, "nulls.json");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT id, name FROM public.nullable) TO '{EscapeSql(outPath)}'");
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(outPath));
        Assert.Equal(JsonValueKind.Null, doc.RootElement[0].GetProperty("name").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement[1].GetProperty("id").ValueKind);
    }

    [Fact]
    public async Task CopyToJson_LinesMode_EmitsOneObjectPerLine()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.scalars",
            columns: ["id", "name"],
            columnKinds: [DataKind.Int32, DataKind.String],
            rows: [[1, "alice"], [2, "bob"], [3, "carol"]]));

        string outPath = Path.Combine(_scratchDir, "scalars.json");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT id, name FROM public.scalars) TO '{EscapeSql(outPath)}' " +
            "(FORMAT 'json', LINES 'true')");
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        string raw = File.ReadAllText(outPath);
        string[] lines = raw.TrimEnd('\n').Split('\n');
        Assert.Equal(3, lines.Length);
        // Each line is a complete top-level object — no outer brackets,
        // no leading whitespace, no comma terminators.
        Assert.StartsWith("{\"id\":1,\"name\":\"alice\"", lines[0]);
        Assert.StartsWith("{\"id\":2,\"name\":\"bob\"", lines[1]);
        Assert.StartsWith("{\"id\":3,\"name\":\"carol\"", lines[2]);
        // Each parses independently as JSON.
        foreach (string line in lines)
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        }
    }

    [Fact]
    public async Task CopyToJson_JsonlExtension_DefaultsToLinesMode()
    {
        // Without an explicit LINES option, the extension wins. `.jsonl`
        // and `.ndjson` are the two conventional newline-delimited
        // extensions; both should pick lines mode automatically.
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.x",
            columns: ["a"],
            columnKinds: [DataKind.Int32],
            rows: [[1], [2]]));

        foreach (string ext in new[] { ".jsonl", ".ndjson" })
        {
            string outPath = Path.Combine(_scratchDir, $"x{ext}");
            StatementPlan plan = await catalog.PlanAsync(
                $"COPY (SELECT a FROM public.x) TO '{EscapeSql(outPath)}'");
            await foreach (var _ in catalog.ExecuteAsync(plan)) { }

            string raw = File.ReadAllText(outPath);
            // No leading `[` — that's the giveaway for lines mode.
            Assert.False(raw.TrimStart().StartsWith('['),
                $"{ext}: expected lines mode (no outer array) but got '{raw[..Math.Min(40, raw.Length)]}'");
            Assert.Equal(2, raw.TrimEnd('\n').Split('\n').Length);
        }
    }

    [Fact]
    public async Task CopyToJson_IndentTrue_PrettyPrints()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.x",
            columns: ["a", "b"],
            columnKinds: [DataKind.Int32, DataKind.String],
            rows: [[1, "alice"]]));

        string outPath = Path.Combine(_scratchDir, "pretty.json");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT a, b FROM public.x) TO '{EscapeSql(outPath)}' (INDENT 'true')");
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        string raw = File.ReadAllText(outPath);
        // Pretty-printed output spans multiple lines for the single row.
        // Spot-check that the property is on its own line — without
        // committing to a specific indent width (Utf8JsonWriter picks 2).
        Assert.Contains("\n", raw);
        using JsonDocument doc = JsonDocument.Parse(raw);
        Assert.Equal("alice", doc.RootElement[0].GetProperty("b").GetString());
    }

    [Fact]
    public async Task CopyToJson_IndentTrueWithLinesTrue_RejectedAtPlanTime()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.x",
            columns: ["a"],
            columnKinds: [DataKind.Int32],
            rows: [[1]]));

        string outPath = Path.Combine(_scratchDir, "bad.json");
        ExportPlanException ex = await Assert.ThrowsAsync<ExportPlanException>(async () =>
            await catalog.PlanAsync(
                $"COPY (SELECT a FROM public.x) TO '{EscapeSql(outPath)}' " +
                "(LINES 'true', INDENT 'true')"));
        Assert.Contains("INDENT", ex.Message);
        Assert.Contains("LINES", ex.Message);
    }

    [Fact]
    public async Task CopyToJson_StructColumn_UsesSchemaFieldNames()
    {
        // The point of JSON over CSV: nested structures keep their shape
        // and real field names, not stringified blobs with f0/f1
        // placeholders.
        Pool pool = CreatePool();
        SidecarRegistry registry = new();

        Schema schema = new(
        [
            new ColumnInfo("id", DataKind.Int32, nullable: false),
            new ColumnInfo("location", nullable: false, fields:
            [
                new ColumnInfo("city", DataKind.String, nullable: false),
                new ColumnInfo("zip", DataKind.Int32, nullable: false),
            ]),
        ]);
        ColumnLookup lookup = new(["id", "location"]);
        using Arena arena = new();
        using RowBatch batch = pool.RentRowBatch(lookup, capacity: 1, arena: arena);
        DataValue[] locFields =
        [
            DataValue.FromString("Boston", arena),
            DataValue.FromInt32(02115),
        ];
        DataValue locStruct = DataValue.FromUntypedStruct(locFields, arena);
        batch.Add([DataValue.FromInt32(1), locStruct]);

        string outPath = Path.Combine(_scratchDir, "struct.json");
        JsonExportFormat format = new();
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

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(outPath));
        JsonElement row = doc.RootElement[0];
        JsonElement location = row.GetProperty("location");
        // Schema-aware names: `city` and `zip`, not `f0` / `f1`.
        Assert.Equal("Boston", location.GetProperty("city").GetString());
        Assert.Equal(2115, location.GetProperty("zip").GetInt32());
    }

    [Fact]
    public async Task CopyToJson_ArrayOfFloat32_EmitsRealJsonArray()
    {
        // Where the CSV sink stringifies the JSON inside one CSV field,
        // the JSON sink writes it as a real nested array. Confirm the
        // embedding values land as JSON numbers, not a stringified blob.
        Pool pool = CreatePool();
        SidecarRegistry registry = new();

        Schema schema = new(
        [
            new ColumnInfo("id", DataKind.Int32, nullable: false),
            new ColumnInfo("embedding", DataKind.Float32, nullable: false) { IsArray = true },
        ]);
        ColumnLookup lookup = new(["id", "embedding"]);
        using Arena arena = new();
        using RowBatch batch = pool.RentRowBatch(lookup, capacity: 1, arena: arena);
        batch.Add(
        [
            DataValue.FromInt32(1),
            DataValue.FromArenaArray([0.1f, 0.2f, 0.3f], DataKind.Float32, arena),
        ]);

        string outPath = Path.Combine(_scratchDir, "embeddings.json");
        JsonExportFormat format = new();
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

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(outPath));
        JsonElement emb = doc.RootElement[0].GetProperty("embedding");
        Assert.Equal(JsonValueKind.Array, emb.ValueKind);
        Assert.Equal(3, emb.GetArrayLength());
        Assert.Equal(0.1f, emb[0].GetSingle());
    }

    [Fact]
    public async Task CopyToJson_JsonColumn_DecodesCborIntoNestedNode()
    {
        // Json column on disk is CBOR; the JSON sink must decode it back
        // to JSON text and inline the parsed structure so consumers see
        // a real nested object, not an escaped CBOR string.
        Pool pool = CreatePool();
        SidecarRegistry registry = new();

        Schema schema = new(
        [
            new ColumnInfo("id", DataKind.Int32, nullable: false),
            new ColumnInfo("meta", DataKind.Json, nullable: false),
        ]);
        ColumnLookup lookup = new(["id", "meta"]);
        using Arena arena = new();
        using RowBatch batch = pool.RentRowBatch(lookup, capacity: 1, arena: arena);
        byte[] cbor = CborJsonCodec.EncodeFromJsonText("{\"tags\":[\"a\",\"b\"],\"count\":7}");
        batch.Add(
        [
            DataValue.FromInt32(1),
            DataValue.FromJson(cbor, arena),
        ]);

        string outPath = Path.Combine(_scratchDir, "json-col.json");
        JsonExportFormat format = new();
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

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(outPath));
        JsonElement meta = doc.RootElement[0].GetProperty("meta");
        Assert.Equal(JsonValueKind.Object, meta.ValueKind);
        Assert.Equal(7, meta.GetProperty("count").GetInt32());
        JsonElement tags = meta.GetProperty("tags");
        Assert.Equal(2, tags.GetArrayLength());
        Assert.Equal("a", tags[0].GetString());
    }

    [Fact]
    public async Task CopyToJson_TemporalKinds_RoundTripIsoText()
    {
        DateTime pickup = new(2024, 6, 15, 9, 30, 0, DateTimeKind.Unspecified);
        DateTimeOffset eventTs = new(2024, 6, 15, 9, 30, 0, TimeSpan.FromHours(-4));
        DateOnly birthday = new(1991, 4, 7);

        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.trips",
            columns: ["pickup", "event_ts", "birthday"],
            columnKinds: [DataKind.Timestamp, DataKind.TimestampTz, DataKind.Date],
            rows: [[pickup, eventTs, birthday]]));

        string outPath = Path.Combine(_scratchDir, "trips.json");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT pickup, event_ts, birthday FROM public.trips) TO '{EscapeSql(outPath)}'");
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(outPath));
        JsonElement row = doc.RootElement[0];
        Assert.Equal(
            pickup.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
            row.GetProperty("pickup").GetString());
        Assert.Equal(
            eventTs.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            row.GetProperty("event_ts").GetString());
        Assert.Equal("1991-04-07", row.GetProperty("birthday").GetString());
    }

    [Fact]
    public async Task CopyToJson_RejectsImageColumn_AtPlanTime()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.samples",
            columns: ["id", "pic"],
            columnKinds: [DataKind.Int32, DataKind.Image],
            rows: [[1, new byte[] { 0x89, 0x50 }]]));

        string outPath = Path.Combine(_scratchDir, "rejected.json");
        ExportPlanException ex = await Assert.ThrowsAsync<ExportPlanException>(async () =>
            await catalog.PlanAsync(
                $"COPY (SELECT id, pic FROM public.samples) TO '{EscapeSql(outPath)}'"));
        Assert.Contains("'pic'", ex.Message);
        Assert.Contains("Image", ex.Message);
        Assert.Contains("parquet", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CopyToJson_EmptySource_WritesEmptyArray()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.empty",
            columns: ["a"],
            columnKinds: [DataKind.Int32],
            rows: []));

        string outPath = Path.Combine(_scratchDir, "empty.json");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT a FROM public.empty) TO '{EscapeSql(outPath)}'");
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        string raw = File.ReadAllText(outPath).Trim();
        Assert.Equal("[]", raw);
    }

    [Fact]
    public async Task CopyToJson_YieldsSummaryRow_WithRowsWrittenAndBytesWritten()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.x",
            columns: ["a"],
            columnKinds: [DataKind.Int32],
            rows: [[1], [2], [3]]));

        string outPath = Path.Combine(_scratchDir, "summary.json");
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

    private static string EscapeSql(string path) => path.Replace("'", "''");
}
