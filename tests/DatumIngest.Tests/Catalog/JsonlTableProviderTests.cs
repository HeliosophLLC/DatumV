using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Model;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// Tests for <see cref="JsonlTableProvider"/> covering schema inference,
/// row reading, type widening, projection, blank line handling, and
/// cancellation behavior.
/// </summary>
public sealed class JsonlTableProviderTests
{
    private static string FixturePath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
    }

    private static TableDescriptor Descriptor(string fileName)
    {
        return new TableDescriptor("jsonl", "test", FixturePath(fileName), new Dictionary<string, string>());
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
    public async Task GetSchemaAsync_SimpleFile_InfersCorrectSchema()
    {
        JsonlTableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(Descriptor("simple.jsonl"), CancellationToken.None);

        Assert.Equal(3, schema.Columns.Count);
        Assert.NotNull(schema.FindColumn("id"));
        Assert.NotNull(schema.FindColumn("name"));
        Assert.NotNull(schema.FindColumn("score"));
    }

    [Fact]
    public async Task GetSchemaAsync_SimpleFile_DetectsTypes()
    {
        JsonlTableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(Descriptor("simple.jsonl"), CancellationToken.None);

        Assert.Equal(DataKind.Scalar, schema.FindColumn("id")!.Kind);
        Assert.Equal(DataKind.String, schema.FindColumn("name")!.Kind);
        Assert.Equal(DataKind.Scalar, schema.FindColumn("score")!.Kind);
    }

    [Fact]
    public async Task GetSchemaAsync_MixedTypes_WidensToString()
    {
        JsonlTableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(Descriptor("mixed_types.jsonl"), CancellationToken.None);

        // "value" is number on line 1, string on line 2 → widened to String
        Assert.Equal(DataKind.String, schema.FindColumn("value")!.Kind);
        Assert.Equal(DataKind.String, schema.FindColumn("label")!.Kind);
    }

    [Fact]
    public async Task GetSchemaAsync_NestedValues_InfersJsonValue()
    {
        JsonlTableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(Descriptor("nested_values.jsonl"), CancellationToken.None);

        Assert.Equal(DataKind.Scalar, schema.FindColumn("id")!.Kind);
        Assert.Equal(DataKind.JsonValue, schema.FindColumn("tags")!.Kind);
        Assert.Equal(DataKind.JsonValue, schema.FindColumn("meta")!.Kind);
    }

    [Fact]
    public async Task GetSchemaAsync_SparseProperties_UnionsAllColumns()
    {
        JsonlTableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(Descriptor("sparse.jsonl"), CancellationToken.None);

        Assert.Equal(3, schema.Columns.Count);
        Assert.NotNull(schema.FindColumn("id"));
        Assert.NotNull(schema.FindColumn("name"));
        Assert.NotNull(schema.FindColumn("score"));
    }

    // ───────────────────── Row reading ─────────────────────

    [Fact]
    public async Task OpenAsync_SimpleFile_ReadsAllRows()
    {
        JsonlTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor("simple.jsonl"), null, CancellationToken.None));

        Assert.Equal(3, rows.Count);
        Assert.Equal("Alice", rows[0]["name"].AsString());
        Assert.Equal("Bob", rows[1]["name"].AsString());
        Assert.Equal("Charlie", rows[2]["name"].AsString());
    }

    [Fact]
    public async Task OpenAsync_SimpleFile_ReadsNumericValues()
    {
        JsonlTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor("simple.jsonl"), null, CancellationToken.None));

        Assert.Equal(1f, rows[0]["id"].AsScalar());
        Assert.Equal(95.5f, rows[0]["score"].AsScalar());
    }

    [Fact]
    public async Task OpenAsync_NestedValues_PreservesAsJsonValue()
    {
        JsonlTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor("nested_values.jsonl"), null, CancellationToken.None));

        Assert.Equal(2, rows.Count);
        string tags = rows[0]["tags"].AsJsonValue();
        Assert.Contains("\"a\"", tags);
        Assert.Contains("\"b\"", tags);

        string meta = rows[0]["meta"].AsJsonValue();
        Assert.Contains("\"source\"", meta);
        Assert.Contains("\"web\"", meta);
    }

    [Fact]
    public async Task OpenAsync_SparseProperties_MissingIsNull()
    {
        JsonlTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor("sparse.jsonl"), null, CancellationToken.None));

        Assert.Equal(3, rows.Count);

        // Row 0 has id and name, but no score
        Assert.Equal(1f, rows[0]["id"].AsScalar());
        Assert.Equal("Alice", rows[0]["name"].AsString());
        Assert.True(rows[0]["score"].IsNull);

        // Row 1 has id and score, but no name
        Assert.Equal(2f, rows[1]["id"].AsScalar());
        Assert.True(rows[1]["name"].IsNull);
        Assert.Equal(88.0f, rows[1]["score"].AsScalar());
    }

    [Fact]
    public async Task OpenAsync_BlankLines_SkipsGracefully()
    {
        JsonlTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor("blank_lines.jsonl"), null, CancellationToken.None));

        Assert.Equal(3, rows.Count);
        Assert.Equal("hello", rows[0]["text"].AsString());
        Assert.Equal("world", rows[1]["text"].AsString());
        Assert.Equal("test", rows[2]["text"].AsString());
    }

    // ───────────────────── Projection pushdown ─────────────────────

    [Fact]
    public async Task OpenAsync_ProjectionPushdown_LimitsColumns()
    {
        JsonlTableProvider provider = new();
        HashSet<string> required = new() { "name" };
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor("simple.jsonl"), required, CancellationToken.None));

        Assert.Equal(3, rows.Count);
        Assert.Equal("Alice", rows[0]["name"].AsString());

        // Only the "name" column should be present
        Assert.Equal(1, rows[0].FieldCount);
    }

    // ───────────────────── Empty file ─────────────────────

    [Fact]
    public async Task GetSchemaAsync_EmptyFile_Throws()
    {
        string tempPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempPath, "");
            TableDescriptor descriptor = new("jsonl", "test", tempPath, new Dictionary<string, string>());
            JsonlTableProvider provider = new();

            await Assert.ThrowsAsync<ArgumentException>(
                () => provider.GetSchemaAsync(descriptor, CancellationToken.None));
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    // ───────────────────── Cancellation ─────────────────────

    [Fact]
    public async Task OpenAsync_CancellationToken_Respected()
    {
        JsonlTableProvider provider = new();
        CancellationTokenSource cancellationTokenSource = new();
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (Row _ in provider.OpenAsync(
                Descriptor("simple.jsonl"), null, cancellationTokenSource.Token))
            {
            }
        });
    }

    // ───────────────────── Capabilities ─────────────────────

    [Fact]
    public async Task GetCapabilitiesAsync_ReturnsDefaults()
    {
        JsonlTableProvider provider = new();
        ProviderCapabilities capabilities = await provider.GetCapabilitiesAsync(
            Descriptor("simple.jsonl"), CancellationToken.None);

        Assert.Null(capabilities.EstimatedRowCount);
        Assert.True(capabilities.SupportsSeek);
    }

    // ───────────────────── ISO 8601 date auto-detection ─────────────────────

    [Fact]
    public async Task GetSchema_DetectsDateAndDateTimeColumns()
    {
        JsonlTableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(Descriptor("dates.jsonl"), CancellationToken.None);

        ColumnInfo? created = schema.FindColumn("created");
        ColumnInfo? updated = schema.FindColumn("updated");
        Assert.NotNull(created);
        Assert.NotNull(updated);
        Assert.Equal(DataKind.Date, created!.Kind);
        Assert.Equal(DataKind.DateTime, updated!.Kind);
    }

    [Fact]
    public async Task Open_ParsesDateValues()
    {
        JsonlTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor("dates.jsonl"), null, CancellationToken.None));

        Assert.Equal(3, rows.Count);
        Assert.Equal(new DateOnly(2024, 1, 15), rows[0]["created"].AsDate());
        Assert.Equal(new DateOnly(2024, 6, 20), rows[1]["created"].AsDate());
    }

    [Fact]
    public async Task Open_ParsesDateTimeValues()
    {
        JsonlTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor("dates.jsonl"), null, CancellationToken.None));

        Assert.Equal(3, rows.Count);
        Assert.False(rows[0]["updated"].IsNull);
        Assert.Equal(DataKind.DateTime, rows[0]["updated"].Kind);
    }
}
