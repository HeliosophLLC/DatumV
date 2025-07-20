using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Model;

namespace DatumIngest.Tests.Catalog;

public sealed class CsvTableProviderTests
{
    private static string FixturePath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
    }

    private static TableDescriptor Descriptor(string fileName, Dictionary<string, string>? options = null)
    {
        return new TableDescriptor("csv", "test", FixturePath(fileName), options ?? new Dictionary<string, string>());
    }

    private static async Task<List<Row>> ReadAllAsync(IAsyncEnumerable<Row> source)
    {
        List<Row> rows = new();
        await foreach (Row row in source)
        {
            rows.Add(row);
        }
        return rows;
    }

    // ───────────────────── Schema inference ─────────────────────

    [Fact]
    public async Task GetSchema_InfersColumnsFromHeader()
    {
        CsvTableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(Descriptor("simple.csv"), CancellationToken.None);

        Assert.Equal(3, schema.Columns.Count);
        Assert.Equal("name", schema.Columns[0].Name);
        Assert.Equal("age", schema.Columns[1].Name);
        Assert.Equal("score", schema.Columns[2].Name);
    }

    [Fact]
    public async Task GetSchema_DetectsNumericColumns()
    {
        CsvTableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(Descriptor("simple.csv"), CancellationToken.None);

        Assert.Equal(DataKind.String, schema.Columns[0].Kind);
        Assert.Equal(DataKind.Scalar, schema.Columns[1].Kind);
        Assert.Equal(DataKind.Scalar, schema.Columns[2].Kind);
    }

    [Fact]
    public async Task GetSchema_CustomDelimiter()
    {
        CsvTableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(
            Descriptor("semicolon.csv", new Dictionary<string, string> { ["delimiter"] = ";" }),
            CancellationToken.None);

        Assert.Equal(3, schema.Columns.Count);
        Assert.Equal("id", schema.Columns[0].Name);
        Assert.Equal("value", schema.Columns[1].Name);
        Assert.Equal("label", schema.Columns[2].Name);
    }

    // ───────────────────── Row reading ─────────────────────

    [Fact]
    public async Task Open_ReadsAllRows()
    {
        CsvTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor("simple.csv"), null, CancellationToken.None));

        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public async Task Open_ParsesStringValues()
    {
        CsvTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor("simple.csv"), null, CancellationToken.None));

        Assert.Equal("Alice", rows[0]["name"].AsString());
        Assert.Equal("Bob", rows[1]["name"].AsString());
        Assert.Equal("Charlie", rows[2]["name"].AsString());
    }

    [Fact]
    public async Task Open_ParsesNumericValues()
    {
        CsvTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor("simple.csv"), null, CancellationToken.None));

        Assert.Equal(30f, rows[0]["age"].AsScalar());
        Assert.Equal(95.5f, rows[0]["score"].AsScalar());
    }

    [Fact]
    public async Task Open_CustomDelimiter()
    {
        CsvTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(
                Descriptor("semicolon.csv", new Dictionary<string, string> { ["delimiter"] = ";" }),
                null,
                CancellationToken.None));

        Assert.Equal(3, rows.Count);
        Assert.Equal(1f, rows[0]["id"].AsScalar());
        Assert.Equal("cat", rows[0]["label"].AsString());
    }

    // ───────────────────── RFC 4180 quoting ─────────────────────

    [Fact]
    public async Task Open_HandlesQuotedFields()
    {
        CsvTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor("quoted.csv"), null, CancellationToken.None));

        Assert.Equal(3, rows.Count);
        Assert.Equal("Has a \"nickname\"", rows[0]["description"].AsString());
        Assert.Equal("Likes, commas", rows[1]["description"].AsString());
        Assert.Equal("Line\nbreak", rows[2]["description"].AsString());
    }

    // ───────────────────── Nulls and empty values ─────────────────────

    [Fact]
    public async Task Open_EmptyFieldsAreNull()
    {
        CsvTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor("nulls.csv"), null, CancellationToken.None));

        Assert.Equal(3, rows.Count);

        // First row: x=1, y=null
        Assert.Equal(1f, rows[0]["x"].AsScalar());
        Assert.True(rows[0]["y"].IsNull);

        // Second row: x=null, y=3
        Assert.True(rows[1]["x"].IsNull);
        Assert.Equal(3f, rows[1]["y"].AsScalar());

        // Third row: x=4, y=5
        Assert.Equal(4f, rows[2]["x"].AsScalar());
        Assert.Equal(5f, rows[2]["y"].AsScalar());
    }

    // ───────────────────── Empty file (header only) ─────────────────────

    [Fact]
    public async Task Open_EmptyFileYieldsNoRows()
    {
        CsvTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor("empty.csv"), null, CancellationToken.None));

        Assert.Empty(rows);
    }

    [Fact]
    public async Task GetSchema_EmptyFileStillHasColumns()
    {
        CsvTableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(Descriptor("empty.csv"), CancellationToken.None);

        Assert.Equal(3, schema.Columns.Count);
        Assert.Equal("col_a", schema.Columns[0].Name);
    }

    // ───────────────────── Projection pushdown ─────────────────────

    [Fact]
    public async Task Open_ProjectionPushdown_LimitsColumns()
    {
        CsvTableProvider provider = new();
        HashSet<string> required = new(StringComparer.OrdinalIgnoreCase) { "name", "score" };

        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor("simple.csv"), required, CancellationToken.None));

        Assert.Equal(3, rows.Count);
        Assert.Equal(2, rows[0].FieldCount);
        Assert.Equal("Alice", rows[0]["name"].AsString());
        Assert.Equal(95.5f, rows[0]["score"].AsScalar());
    }

    // ───────────────────── Capabilities ─────────────────────

    [Fact]
    public async Task GetCapabilities_ReturnsDefaults()
    {
        CsvTableProvider provider = new();
        ProviderCapabilities capabilities = await provider.GetCapabilitiesAsync(
            Descriptor("simple.csv"), CancellationToken.None);

        Assert.Null(capabilities.EstimatedRowCount);
        Assert.True(capabilities.SupportsSeek);
    }

    // ───────────────────── Delimiter auto-detection ─────────────────────

    [Fact]
    public async Task GetSchema_AutoDetectsSemicolonDelimiter()
    {
        CsvTableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(
            Descriptor("semicolon.csv"), CancellationToken.None);

        Assert.Equal(3, schema.Columns.Count);
        Assert.Equal("id", schema.Columns[0].Name);
        Assert.Equal("value", schema.Columns[1].Name);
        Assert.Equal("label", schema.Columns[2].Name);
    }

    [Fact]
    public async Task Open_AutoDetectsSemicolonDelimiter()
    {
        CsvTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor("semicolon.csv"), null, CancellationToken.None));

        Assert.Equal(3, rows.Count);
        Assert.Equal(1f, rows[0]["id"].AsScalar());
        Assert.Equal("cat", rows[0]["label"].AsString());
    }

    // ───────────────────── Cancellation ─────────────────────

    [Fact]
    public async Task Open_RespectsCancellation()
    {
        CsvTableProvider provider = new();
        CancellationTokenSource cancellationTokenSource = new();
        await cancellationTokenSource.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await ReadAllAsync(
                provider.OpenAsync(Descriptor("simple.csv"), null, cancellationTokenSource.Token));
        });
    }

    // ───────────────────── Header auto-detection ─────────────────────

    [Fact]
    public async Task GetSchema_HeaderlessNumeric_GeneratesColumnNames()
    {
        CsvTableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(
            Descriptor("headerless_numeric.csv"), CancellationToken.None);

        Assert.Equal(5, schema.Columns.Count);
        Assert.Equal("col_0", schema.Columns[0].Name);
        Assert.Equal("col_1", schema.Columns[1].Name);
        Assert.Equal("col_4", schema.Columns[4].Name);
    }

    [Fact]
    public async Task GetSchema_HeaderlessNumeric_InfersAllScalar()
    {
        CsvTableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(
            Descriptor("headerless_numeric.csv"), CancellationToken.None);

        foreach (ColumnInfo column in schema.Columns)
        {
            Assert.Equal(DataKind.Scalar, column.Kind);
        }
    }

    [Fact]
    public async Task Open_HeaderlessNumeric_FirstRowIsData()
    {
        CsvTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor("headerless_numeric.csv"), null, CancellationToken.None));

        // 5 data rows — first row (39,77516,13,...) should be data, not skipped.
        Assert.Equal(5, rows.Count);
        Assert.Equal(39f, rows[0]["col_0"].AsScalar());
        Assert.Equal(77516f, rows[0]["col_1"].AsScalar());
    }

    [Fact]
    public async Task GetSchema_WithHeader_DetectsHeaderRow()
    {
        CsvTableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(
            Descriptor("simple.csv"), CancellationToken.None);

        // simple.csv has "name,age,score" header — non-numeric "name" vs numeric data → header detected.
        Assert.Equal("name", schema.Columns[0].Name);
        Assert.Equal("age", schema.Columns[1].Name);
        Assert.Equal("score", schema.Columns[2].Name);
    }

    [Fact]
    public async Task GetSchema_HeaderFalseOverride_ForcesGeneratedNames()
    {
        CsvTableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(
            Descriptor("simple.csv", new Dictionary<string, string> { ["header"] = "false" }),
            CancellationToken.None);

        // Even though simple.csv has string headers, header=false forces generated names.
        Assert.Equal("col_0", schema.Columns[0].Name);
        Assert.Equal("col_1", schema.Columns[1].Name);
        Assert.Equal("col_2", schema.Columns[2].Name);
    }

    [Fact]
    public async Task Open_HeaderFalseOverride_FirstRowIsData()
    {
        CsvTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(
                Descriptor("simple.csv", new Dictionary<string, string> { ["header"] = "false" }),
                null, CancellationToken.None));

        // simple.csv has 3 data rows + 1 header row → 4 rows when header=false.
        Assert.Equal(4, rows.Count);

        // First row is the former header line: "name,age,score" — all String since row 1 is non-numeric.
        Assert.Equal("name", rows[0]["col_0"].AsString());
    }

    [Fact]
    public async Task GetSchema_HeaderTrueOverride_ForcesHeaderEvenWhenAllNumeric()
    {
        CsvTableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(
            Descriptor("headerless_numeric.csv", new Dictionary<string, string> { ["header"] = "true" }),
            CancellationToken.None);

        // header=true forces row 1 ("39,77516,13,2174,0") to be treated as column names.
        Assert.Equal("39", schema.Columns[0].Name);
        Assert.Equal("77516", schema.Columns[1].Name);
    }

    [Fact]
    public async Task Open_HeaderTrueOverride_SkipsFirstRow()
    {
        CsvTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(
                Descriptor("headerless_numeric.csv", new Dictionary<string, string> { ["header"] = "true" }),
                null, CancellationToken.None));

        // 5 rows in file, header=true skips row 1 → 4 data rows.
        Assert.Equal(4, rows.Count);
        Assert.Equal(50f, rows[0]["39"].AsScalar());
    }

    [Fact]
    public async Task Open_HeaderlessNumeric_ProjectionPushdown()
    {
        CsvTableProvider provider = new();
        HashSet<string> required = new(StringComparer.OrdinalIgnoreCase) { "col_0", "col_2" };

        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor("headerless_numeric.csv"), required, CancellationToken.None));

        Assert.Equal(5, rows.Count);
        Assert.Equal(2, rows[0].FieldCount);
        Assert.Equal(39f, rows[0]["col_0"].AsScalar());
        Assert.Equal(13f, rows[0]["col_2"].AsScalar());
    }
}
