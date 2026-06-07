using System.Globalization;
using System.Text;
using System.Text.Json;
using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Catalog.Plans;
using Heliosoph.DatumV.Catalog.Providers;
using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.Export;
using Heliosoph.DatumV.Export.Csv;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Tests.Export;

/// <summary>
/// End-to-end exercise of <c>COPY (query) TO 'path.csv'</c> — the parser,
/// <see cref="ExportPlan"/>, <see cref="Export.Csv.CsvExportFormat"/>, and
/// <see cref="Export.Csv.CsvExportSink"/>. Validates RFC 4180 quoting,
/// configurable option keys, NULL handling, struct / array JSON encoding,
/// and plan-time rejection of typed-media columns that CSV cannot
/// represent.
/// </summary>
public sealed class CsvExportSinkTests : ServiceTestBase, IDisposable
{
    private readonly string _scratchDir;

    public CsvExportSinkTests()
    {
        _scratchDir = Path.Combine(Path.GetTempPath(), $"datum-csv-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchDir);
    }

    public new void Dispose()
    {
        base.Dispose();
        try { if (Directory.Exists(_scratchDir)) Directory.Delete(_scratchDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task CopyToCsv_ScalarsOnly_WritesHeaderAndRows()
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

        string outPath = Path.Combine(_scratchDir, "scalars.csv");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT id, name, score FROM public.scalars) TO '{EscapeSql(outPath)}'");

        Assert.IsType<ExportPlan>(plan);
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.True(File.Exists(outPath));
        string text = File.ReadAllText(outPath, Encoding.UTF8);
        // Header row + three data rows + trailing newline.
        string[] lines = text.TrimEnd('\n', '\r').Split('\n');
        Assert.Equal(4, lines.Length);
        Assert.Equal("id,name,score", lines[0]);
        Assert.Equal("1,alice,0.1", lines[1]);
        Assert.Equal("2,bob,0.55", lines[2]);
        Assert.Equal("3,carol,0.95", lines[3]);
    }

    [Fact]
    public async Task CopyToCsv_HeaderFalse_OmitsHeaderRow()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.scalars",
            columns: ["id", "name"],
            columnKinds: [DataKind.Int32, DataKind.String],
            rows: [[1, "alice"], [2, "bob"]]));

        string outPath = Path.Combine(_scratchDir, "no-header.csv");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT id, name FROM public.scalars) TO '{EscapeSql(outPath)}' (HEADER 'false')");
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        string[] lines = File.ReadAllText(outPath).TrimEnd('\n', '\r').Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal("1,alice", lines[0]);
        Assert.Equal("2,bob", lines[1]);
    }

    [Fact]
    public async Task CopyToCsv_NullValues_EmitEmptyField_ByDefault()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.nullable",
            columns: ["id", "name"],
            columnKinds: [DataKind.Int32, DataKind.String],
            rows: [[1, "alice"], [2, null], [null, "carol"]]));

        string outPath = Path.Combine(_scratchDir, "nulls.csv");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT id, name FROM public.nullable) TO '{EscapeSql(outPath)}'");
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        string[] lines = File.ReadAllText(outPath).TrimEnd('\n', '\r').Split('\n');
        Assert.Equal("id,name", lines[0]);
        Assert.Equal("1,alice", lines[1]);
        Assert.Equal("2,", lines[2]);
        Assert.Equal(",carol", lines[3]);
    }

    [Fact]
    public async Task CopyToCsv_NullStringOption_ReplacesEmptyField()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.nullable",
            columns: ["id", "name"],
            columnKinds: [DataKind.Int32, DataKind.String],
            rows: [[1, null], [null, "bob"]]));

        string outPath = Path.Combine(_scratchDir, "null-string.csv");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT id, name FROM public.nullable) TO '{EscapeSql(outPath)}' (NULL_STRING 'NULL')");
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        string[] lines = File.ReadAllText(outPath).TrimEnd('\n', '\r').Split('\n');
        Assert.Equal("1,NULL", lines[1]);
        Assert.Equal("NULL,bob", lines[2]);
    }

    [Fact]
    public async Task CopyToCsv_QuotesFieldsContainingDelimiterQuoteOrNewline()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.tricky",
            columns: ["id", "text"],
            columnKinds: [DataKind.Int32, DataKind.String],
            rows:
            [
                [1, "comma,inside"],
                [2, "quote\"inside"],
                [3, "newline\nhere"],
                [4, "plain"],
            ]));

        string outPath = Path.Combine(_scratchDir, "quoting.csv");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT id, text FROM public.tricky) TO '{EscapeSql(outPath)}'");
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        // RFC 4180: quote-wrap when needed; double internal quotes; quoted
        // body can contain raw newlines (so a "field" can straddle physical
        // lines).
        string raw = File.ReadAllText(outPath);
        Assert.Contains("1,\"comma,inside\"", raw);
        Assert.Contains("2,\"quote\"\"inside\"", raw);
        Assert.Contains("3,\"newline\nhere\"", raw);
        Assert.Contains("4,plain", raw);
    }

    [Fact]
    public async Task CopyToCsv_DelimiterOption_AcceptsSemicolonAndTab()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.x",
            columns: ["a", "b"],
            columnKinds: [DataKind.Int32, DataKind.String],
            rows: [[1, "x"], [2, "y"]]));

        string semiPath = Path.Combine(_scratchDir, "semi.csv");
        StatementPlan plan1 = await catalog.PlanAsync(
            $"COPY (SELECT a, b FROM public.x) TO '{EscapeSql(semiPath)}' (DELIMITER ';')");
        await foreach (var _ in catalog.ExecuteAsync(plan1)) { }
        string[] semiLines = File.ReadAllText(semiPath).TrimEnd('\n', '\r').Split('\n');
        Assert.Equal("a;b", semiLines[0]);
        Assert.Equal("1;x", semiLines[1]);

        string tabPath = Path.Combine(_scratchDir, "tab.csv");
        StatementPlan plan2 = await catalog.PlanAsync(
            $"COPY (SELECT a, b FROM public.x) TO '{EscapeSql(tabPath)}' (DELIMITER 'tab')");
        await foreach (var _ in catalog.ExecuteAsync(plan2)) { }
        string[] tabLines = File.ReadAllText(tabPath).TrimEnd('\n', '\r').Split('\n');
        Assert.Equal("a\tb", tabLines[0]);
        Assert.Equal("1\tx", tabLines[1]);
    }

    [Fact]
    public async Task CopyToCsv_LineEndingCrlf_UsesCrlf()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.x",
            columns: ["a"],
            columnKinds: [DataKind.Int32],
            rows: [[1], [2]]));

        string outPath = Path.Combine(_scratchDir, "crlf.csv");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT a FROM public.x) TO '{EscapeSql(outPath)}' (LINE_ENDING 'crlf')");
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        string raw = File.ReadAllText(outPath);
        Assert.Equal("a\r\n1\r\n2\r\n", raw);
    }

    [Fact]
    public async Task CopyToCsv_YieldsSummaryRow_WithRowsWrittenAndBytesWritten()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.x",
            columns: ["a"],
            columnKinds: [DataKind.Int32],
            rows: [[1], [2], [3]]));

        string outPath = Path.Combine(_scratchDir, "summary.csv");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT a FROM public.x) TO '{EscapeSql(outPath)}'");

        List<RowBatch> batches = [];
        await foreach (RowBatch batch in catalog.ExecuteAsync(plan)) batches.Add(batch);

        RowBatch summary = Assert.Single(batches);
        Assert.Equal(1, summary.Count);
        Assert.True(summary.ColumnLookup.TryGetColumnOrdinal("rows_written", out int rowsCol));
        Assert.True(summary.ColumnLookup.TryGetColumnOrdinal("bytes_written", out int bytesCol));
        Row row = summary[0];
        Assert.Equal(3L, row[rowsCol].AsInt64());
        Assert.Equal(new FileInfo(outPath).Length, row[bytesCol].AsInt64());
    }

    [Fact]
    public async Task CopyToCsv_TemporalKinds_RoundTripIsoText()
    {
        // The scanner narrows ISO 8601 dates / timestamps back to their
        // typed kinds — this is the load-bearing reason for the explicit
        // formatting choices in the sink. Pin the exact text so a drift
        // in formatting that breaks the round trip surfaces here.
        DateTime pickup = new(2024, 6, 15, 9, 30, 0, DateTimeKind.Unspecified);
        DateTimeOffset eventTs = new(2024, 6, 15, 9, 30, 0, TimeSpan.FromHours(-4));
        DateOnly birthday = new(1991, 4, 7);
        TimeOnly clockIn = new(8, 15, 30);
        decimal fare = 12.50m;
        Guid id = Guid.Parse("11111111-2222-3333-4444-555555555555");

        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.trips",
            columns: ["pickup", "event_ts", "birthday", "clock_in", "fare", "trip_uuid"],
            columnKinds:
            [
                DataKind.Timestamp, DataKind.TimestampTz, DataKind.Date,
                DataKind.Time, DataKind.Decimal, DataKind.Uuid,
            ],
            rows: [[pickup, eventTs, birthday, clockIn, fare, id]]));

        string outPath = Path.Combine(_scratchDir, "trips.csv");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT pickup, event_ts, birthday, clock_in, fare, trip_uuid FROM public.trips) " +
            $"TO '{EscapeSql(outPath)}'");
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        string[] lines = File.ReadAllText(outPath).TrimEnd('\n', '\r').Split('\n');
        Assert.Equal("pickup,event_ts,birthday,clock_in,fare,trip_uuid", lines[0]);
        string[] cells = lines[1].Split(',');
        Assert.Equal(pickup.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture), cells[0]);
        // TimestampTz is stored as UTC ticks — the on-disk offset is +00:00.
        Assert.Equal(eventTs.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), cells[1]);
        Assert.Equal("1991-04-07", cells[2]);
        Assert.Equal("08:15:30", cells[3]);
        Assert.Equal("12.50", cells[4]);
        Assert.Equal("11111111-2222-3333-4444-555555555555", cells[5]);
    }

    [Fact]
    public async Task CopyToCsv_Booleans_LowercaseTrueFalse()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.flags",
            columns: ["b"],
            columnKinds: [DataKind.Boolean],
            rows: [[true], [false]]));

        string outPath = Path.Combine(_scratchDir, "flags.csv");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT b FROM public.flags) TO '{EscapeSql(outPath)}'");
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        string[] lines = File.ReadAllText(outPath).TrimEnd('\n', '\r').Split('\n');
        Assert.Equal("true", lines[1]);
        Assert.Equal("false", lines[2]);
    }

    [Fact]
    public async Task CopyToCsv_RejectsImageColumn_AtPlanTime()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.samples",
            columns: ["id", "pic"],
            columnKinds: [DataKind.Int32, DataKind.Image],
            rows: [[1, new byte[] { 0x89, 0x50, 0x4E, 0x47 }]]));

        string outPath = Path.Combine(_scratchDir, "rejected.csv");
        ExportPlanException ex = await Assert.ThrowsAsync<ExportPlanException>(async () =>
            await catalog.PlanAsync(
                $"COPY (SELECT id, pic FROM public.samples) TO '{EscapeSql(outPath)}'"));
        Assert.Contains("'pic'", ex.Message);
        Assert.Contains("Image", ex.Message);
        Assert.Contains("parquet", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CopyToCsv_RejectsByteArrayColumn_AtPlanTime()
    {
        // Exercises ResolveDisposition directly — InMemoryTableProvider's
        // columnKinds ctor doesn't surface IsArray, so SQL coverage of an
        // Array<UInt8> column requires CREATE TABLE plumbing. The format
        // contract guarantees the same rejection path regardless.
        CsvExportFormat format = new();
        ColumnInfo byteArray = new("raw", DataKind.UInt8, nullable: false) { IsArray = true };
        ExportPlanException ex = Assert.Throws<ExportPlanException>(
            () => format.ResolveDisposition(byteArray, ExportOptions.Empty));
        Assert.Contains("'raw'", ex.Message);
        Assert.Contains("byte array", ex.Message);
    }

    [Fact]
    public async Task CopyToCsv_UnknownLineEnding_ThrowsExportPlanException()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.x",
            columns: ["a"],
            columnKinds: [DataKind.Int32],
            rows: [[1]]));

        string outPath = Path.Combine(_scratchDir, "bad.csv");
        ExportPlanException ex = await Assert.ThrowsAsync<ExportPlanException>(async () =>
            await catalog.PlanAsync(
                $"COPY (SELECT a FROM public.x) TO '{EscapeSql(outPath)}' (LINE_ENDING 'cr')"));
        Assert.Contains("LINE_ENDING", ex.Message);
    }

    [Fact]
    public async Task CopyToCsv_InvalidDelimiter_ThrowsExportPlanException()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.x",
            columns: ["a"],
            columnKinds: [DataKind.Int32],
            rows: [[1]]));

        string outPath = Path.Combine(_scratchDir, "bad.csv");
        ExportPlanException ex = await Assert.ThrowsAsync<ExportPlanException>(async () =>
            await catalog.PlanAsync(
                $"COPY (SELECT a FROM public.x) TO '{EscapeSql(outPath)}' (DELIMITER ',,')"));
        Assert.Contains("DELIMITER", ex.Message);
    }

    [Fact]
    public async Task CopyToCsv_ExtensionInfersFormat()
    {
        // Drop the explicit (FORMAT csv) — the .csv extension should route
        // the export through the CSV format the same way Parquet's .parquet
        // does.
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.x",
            columns: ["a"],
            columnKinds: [DataKind.Int32],
            rows: [[1]]));

        string outPath = Path.Combine(_scratchDir, "inferred.csv");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT a FROM public.x) TO '{EscapeSql(outPath)}'");
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        string[] lines = File.ReadAllText(outPath).TrimEnd('\n', '\r').Split('\n');
        Assert.Equal("a", lines[0]);
        Assert.Equal("1", lines[1]);
    }

    [Fact]
    public async Task CopyToCsv_EmptySource_StillWritesHeader()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.empty",
            columns: ["a", "b"],
            columnKinds: [DataKind.Int32, DataKind.String],
            rows: []));

        string outPath = Path.Combine(_scratchDir, "empty.csv");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT a, b FROM public.empty) TO '{EscapeSql(outPath)}'");
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        Assert.True(File.Exists(outPath));
        string raw = File.ReadAllText(outPath);
        Assert.Equal("a,b\n", raw);
    }

    [Fact]
    public async Task CopyToCsv_ArrayColumn_EmitsJsonArray()
    {
        // Array<Int32> serialises as a JSON array inside a single CSV field.
        // The scanner re-reads the column as String on import — that's the
        // honest answer for CSV. Drive the sink directly because
        // InMemoryTableProvider's columnKinds ctor doesn't surface IsArray.
        Pool pool = CreatePool();
        SidecarRegistry registry = new();

        Schema schema = new(
        [
            new ColumnInfo("id", DataKind.Int32, nullable: false),
            new ColumnInfo("vals", DataKind.Int32, nullable: false) { IsArray = true },
        ]);
        ColumnLookup lookup = new(["id", "vals"]);
        using Arena arena = new();
        using RowBatch batch = pool.RentRowBatch(lookup, capacity: 2, arena: arena);
        batch.Add(
        [
            DataValue.FromInt32(1),
            DataValue.FromArenaArray<int>([10, 20, 30], DataKind.Int32, arena),
        ]);
        batch.Add(
        [
            DataValue.FromInt32(2),
            DataValue.FromArenaArray<int>([7], DataKind.Int32, arena),
        ]);

        string outPath = Path.Combine(_scratchDir, "arrays.csv");
        CsvExportFormat format = new();
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

        string[] lines = File.ReadAllText(outPath).TrimEnd('\n', '\r').Split('\n');
        Assert.Equal("id,vals", lines[0]);
        // Comma inside the JSON array forces RFC 4180 quoting; the JSON
        // body itself is unmodified.
        Assert.Equal("1,\"[10,20,30]\"", lines[1]);
        Assert.Equal("2,[7]", lines[2]);

        // Confirm the field re-parses as valid JSON after unquoting.
        string field1 = lines[1].Substring(2).Trim('"').Replace("\"\"", "\"");
        using JsonDocument doc = JsonDocument.Parse(field1);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(3, doc.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task CopyToCsv_CombinedOptionsViaFormatClause_ParsesAndExecutes()
    {
        // Pins the exact SQL shape the export dialog emits: FORMAT csv
        // alongside HEADER / DELIMITER / LINE_ENDING / NULL_STRING in a
        // single option block. A parse-error or option-bag bug here would
        // silently break the dialog flow even with all the per-option
        // unit tests above green.
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog();
        catalog.Add(new InMemoryTableProvider(
            pool, "public.combo",
            columns: ["id", "name"],
            columnKinds: [DataKind.Int32, DataKind.String],
            rows: [[1, "alice"], [2, null]]));

        string outPath = Path.Combine(_scratchDir, "combo.csv");
        StatementPlan plan = await catalog.PlanAsync(
            $"COPY (SELECT id, name FROM public.combo) TO '{EscapeSql(outPath)}' " +
            "(FORMAT csv, HEADER 'false', DELIMITER ';', LINE_ENDING 'crlf', NULL_STRING 'NULL')");
        await foreach (var _ in catalog.ExecuteAsync(plan)) { }

        string raw = File.ReadAllText(outPath);
        Assert.Equal("1;alice\r\n2;NULL\r\n", raw);
    }

    [Fact]
    public async Task CopyToCsv_ArrayOfFloat32_EmitsJsonNumberArray()
    {
        // Array<Float32> is the canonical embedding-vector shape — the
        // load-bearing array case for ML datasets. AsArraySpan<float>
        // reads the contiguous arena bytes; Utf8JsonWriter writes each
        // element as a JSON number. The whole array lands inside one
        // CSV field, quoted because of the internal commas.
        Pool pool = CreatePool();
        SidecarRegistry registry = new();

        Schema schema = new(
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
            DataValue.FromArenaArray([0.1f, 0.2f, 0.3f, 0.4f], DataKind.Float32, arena),
        ]);
        batch.Add(
        [
            DataValue.FromInt32(2),
            DataValue.FromArenaArray([-1.5f, 0.0f, 1.5f], DataKind.Float32, arena),
        ]);

        string outPath = Path.Combine(_scratchDir, "embeddings.csv");
        CsvExportFormat format = new();
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

        string[] lines = File.ReadAllText(outPath).TrimEnd('\n', '\r').Split('\n');
        Assert.Equal("id,embedding", lines[0]);

        // Parse the JSON back out and confirm shape + values. Comparing
        // float text directly would couple the test to the JSON writer's
        // rounding choices for floats like 0.1f.
        for (int row = 1; row <= 2; row++)
        {
            string cell = lines[row][2..];     // strip "{id},"
            Assert.StartsWith("\"[", cell);    // CSV-quoted JSON array
            string innerJson = cell[1..^1].Replace("\"\"", "\"");
            using JsonDocument doc = JsonDocument.Parse(innerJson);
            Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        }
        // Spot-check row 2's exact element values.
        string row2 = lines[2][2..];
        string json2 = row2[1..^1].Replace("\"\"", "\"");
        using JsonDocument doc2 = JsonDocument.Parse(json2);
        Assert.Equal(3, doc2.RootElement.GetArrayLength());
        Assert.Equal(-1.5f, doc2.RootElement[0].GetSingle());
        Assert.Equal(0.0f, doc2.RootElement[1].GetSingle());
        Assert.Equal(1.5f, doc2.RootElement[2].GetSingle());
    }

    [Fact]
    public async Task CopyToCsv_ArrayOfStrings_EmitsJsonWithEscapedQuotes()
    {
        // Array<String> renders as a JSON string array inside one CSV
        // field. The element accessor (AsStringArray) takes the sidecar
        // registry so values resolved out of a .datum-blob also flow
        // through — same path the scalar String fix uses.
        //
        // Pins two behaviours that interact:
        //   - JSON escaping of double-quotes inside element strings
        //     (handled by Utf8JsonWriter) versus
        //   - RFC 4180 escaping of CSV double-quotes when the cell wraps
        //     in CSV quotes (handled by WriteField).
        Pool pool = CreatePool();
        SidecarRegistry registry = new();

        Schema schema = new(
        [
            new ColumnInfo("id", DataKind.Int32, nullable: false),
            new ColumnInfo("tags", DataKind.String, nullable: false) { IsArray = true },
        ]);
        ColumnLookup lookup = new(["id", "tags"]);
        using Arena arena = new();
        using RowBatch batch = pool.RentRowBatch(lookup, capacity: 2, arena: arena);
        batch.Add(
        [
            DataValue.FromInt32(1),
            DataValue.FromStringArray(["alpha", "beta", "gamma"], arena),
        ]);
        batch.Add(
        [
            DataValue.FromInt32(2),
            DataValue.FromStringArray(["has,comma", "has\"quote", "plain"], arena),
        ]);

        string outPath = Path.Combine(_scratchDir, "string-arrays.csv");
        CsvExportFormat format = new();
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

        string raw = File.ReadAllText(outPath);
        string[] lines = raw.TrimEnd('\n', '\r').Split('\n');
        Assert.Equal("id,tags", lines[0]);
        // Row 1: plain strings — JSON array, internal commas force CSV
        // quoting; no internal quotes to escape inside the JSON elements.
        Assert.Equal("1,\"[\"\"alpha\"\",\"\"beta\"\",\"\"gamma\"\"]\"", lines[1]);

        // Row 2: confirm the cell un-quotes back to a valid JSON array of
        // exactly the strings we put in. Easier to read than spelling out
        // the doubled-quote escape soup at the literal level.
        string row2Cell = lines[2][2..];      // strip "2,"
        string innerJson = row2Cell[1..^1]    // strip outer CSV "
            .Replace("\"\"", "\"");           // un-double CSV quotes
        using JsonDocument doc = JsonDocument.Parse(innerJson);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(3, doc.RootElement.GetArrayLength());
        Assert.Equal("has,comma", doc.RootElement[0].GetString());
        Assert.Equal("has\"quote", doc.RootElement[1].GetString());
        Assert.Equal("plain", doc.RootElement[2].GetString());
    }

    [Fact]
    public async Task CopyToCsv_ArrayOfUnsupportedKind_EmitsPlaceholder()
    {
        // Element kinds with no fixed-stride CLR primitive backing
        // (Date / Time / Timestamp / TimestampTz / Decimal / Uuid /
        // Duration / Json) fall through to a placeholder so the CSV stays
        // well-formed instead of throwing mid-stream. Pin the contract —
        // changing the placeholder text is a deliberate decision that
        // should break this test.
        Pool pool = CreatePool();
        SidecarRegistry registry = new();

        Schema schema = new(
        [
            new ColumnInfo("when", DataKind.Date, nullable: false) { IsArray = true },
        ]);
        ColumnLookup lookup = new(["when"]);
        using Arena arena = new();
        using RowBatch batch = pool.RentRowBatch(lookup, capacity: 1, arena: arena);
        // Arena-backed Array<Date>: the slot block holds 4-byte DateOnly
        // values directly. We don't have a typed factory for this; build
        // the bytes manually so the row is well-formed at write time.
        DateOnly[] dates = [new(2024, 1, 1), new(2024, 6, 15)];
        DataValue arr = DataValue.FromArenaArray(dates, DataKind.Date, arena);
        batch.Add([arr]);

        string outPath = Path.Combine(_scratchDir, "date-arrays.csv");
        CsvExportFormat format = new();
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

        string[] lines = File.ReadAllText(outPath).TrimEnd('\n', '\r').Split('\n');
        Assert.Equal("when", lines[0]);
        // Placeholder text is JSON-encoded as a string, then CSV-quoted
        // because it contains `<` / `>` — no, those don't trigger CSV
        // quoting; only the delimiter / quote / newline do.
        Assert.Contains("Array<Date> not encodable in CSV", lines[1]);
    }

    [Fact]
    public async Task CopyToCsv_SidecarBackedString_ResolvesViaRegistry()
    {
        // Regression: real-world ingested datasets store String bytes in a
        // .datum-blob sidecar (NYC taxi's `file_name`, COCO's `file` paths).
        // The CSV sink's String path used to call AsString(store) without a
        // SidecarRegistry, which threw "DataValue is sidecar-backed but no
        // SidecarRegistry was provided." Drive the sink directly because
        // InMemoryTableProvider materialises rows into the per-batch arena
        // and a sidecar offset would resolve against the wrong store; real
        // ingested scan-path readers emit sidecar values, which is what
        // this exercises.
        byte[] name1 = Encoding.UTF8.GetBytes("sample-one.jpg");
        byte[] name2 = Encoding.UTF8.GetBytes("comma,inside.jpg");
        InMemoryBlobSource blobs = new();
        long offName1 = blobs.Append(name1);
        long offName2 = blobs.Append(name2);

        Pool pool = CreatePool();
        SidecarRegistry registry = new();
        byte storeId = registry.Register(blobs);

        Schema schema = new(
        [
            new ColumnInfo("id", DataKind.Int32, nullable: false),
            new ColumnInfo("file_name", DataKind.String, nullable: false),
        ]);
        ColumnLookup lookup = new(["id", "file_name"]);
        using Arena arena = new();
        using RowBatch batch = pool.RentRowBatch(lookup, capacity: 2, arena: arena);
        batch.Add(
        [
            DataValue.FromInt32(1),
            DataValue.FromStringInSidecar(offName1, name1.Length, storeId),
        ]);
        batch.Add(
        [
            DataValue.FromInt32(2),
            DataValue.FromStringInSidecar(offName2, name2.Length, storeId),
        ]);

        string outPath = Path.Combine(_scratchDir, "sidecar-strings.csv");
        CsvExportFormat format = new();
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

        string[] lines = File.ReadAllText(outPath).TrimEnd('\n', '\r').Split('\n');
        Assert.Equal("id,file_name", lines[0]);
        Assert.Equal("1,sample-one.jpg", lines[1]);
        // Second value contains the delimiter — confirms RFC 4180 quoting
        // still applies to sidecar-resolved strings.
        Assert.Equal("2,\"comma,inside.jpg\"", lines[2]);
    }

    private static string EscapeSql(string path) => path.Replace("'", "''");

    /// <summary>
    /// In-memory <see cref="IBlobSource"/> for the sidecar-resolution
    /// regression test. Appends bytes to an internal buffer and returns
    /// the offset; reads by absolute offset + length.
    /// </summary>
    private sealed class InMemoryBlobSource : IBlobSource
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
}
