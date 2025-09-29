using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Model;

namespace DatumIngest.Tests.Catalog;

public sealed class JsonTableProviderTests
{
    private static string FixturePath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
    }

    private static TableDescriptor Descriptor(string fileName, Dictionary<string, string>? options = null)
    {
        return new TableDescriptor("json", "test", FixturePath(fileName), options ?? new Dictionary<string, string>());
    }

    private static async Task<List<Row>> ReadAllAsync(IAsyncEnumerable<RowBatch> source)
    {
        return await source.CollectRowsAsync();
    }

    // ───────────────────── Root array ─────────────────────

    [Fact]
    public async Task GetSchema_RootArray_InfersColumns()
    {
        JsonTableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(Descriptor("array.json"), CancellationToken.None);

        Assert.Equal(3, schema.Columns.Count);
        Assert.NotNull(schema.FindColumn("id"));
        Assert.NotNull(schema.FindColumn("name"));
        Assert.NotNull(schema.FindColumn("score"));
    }

    [Fact]
    public async Task GetSchema_RootArray_DetectsTypes()
    {
        JsonTableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(Descriptor("array.json"), CancellationToken.None);

        Assert.Equal(DataKind.Int64, schema.FindColumn("id")!.Kind);
        Assert.Equal(DataKind.String, schema.FindColumn("name")!.Kind);
        Assert.Equal(DataKind.Float64, schema.FindColumn("score")!.Kind);
    }

    [Fact]
    public async Task Open_RootArray_ReadsAllRows()
    {
        JsonTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor("array.json"), null, CancellationToken.None));

        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public async Task Open_RootArray_ParsesValues()
    {
        JsonTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor("array.json"), null, CancellationToken.None));

        Assert.Equal(1L, rows[0]["id"].AsInt64());
        Assert.Equal("Alice", rows[0]["name"].AsString());
        Assert.Equal(95.5, rows[0]["score"].AsFloat64());
    }

    // ───────────────────── JSON_PATH navigation ─────────────────────

    [Fact]
    public async Task GetSchema_WithJsonPath_InfersColumnsFromNestedArray()
    {
        JsonTableProvider provider = new();
        Schema schema = await provider.GetSchemaAsync(
            Descriptor("nested.json", new Dictionary<string, string> { ["json_path"] = "data.items" }),
            CancellationToken.None);

        Assert.Equal(2, schema.Columns.Count);
        Assert.NotNull(schema.FindColumn("id"));
        Assert.NotNull(schema.FindColumn("value"));
    }

    [Fact]
    public async Task Open_WithJsonPath_ReadsNestedArray()
    {
        JsonTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(
                Descriptor("nested.json", new Dictionary<string, string> { ["json_path"] = "data.items" }),
                null,
                CancellationToken.None));

        Assert.Equal(3, rows.Count);
        Assert.Equal(1L, rows[0]["id"].AsInt64());
        Assert.Equal("alpha", rows[0]["value"].AsString());
    }

    // ───────────────────── Complex/nested value types ─────────────────────

    [Fact]
    public async Task Open_MixedTypes_PreservesComplexValuesAsJson()
    {
        JsonTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor("mixed_types.json"), null, CancellationToken.None));

        Assert.Equal(2, rows.Count);

        // "tags" is an array — stored as JsonValue
        Assert.Equal(DataKind.JsonValue, rows[0]["tags"].Kind);

        // "extra" is an object — stored as JsonValue
        Assert.Equal(DataKind.JsonValue, rows[1]["extra"].Kind);
    }

    [Fact]
    public async Task Open_MixedTypes_NullForMissingProperties()
    {
        JsonTableProvider provider = new();
        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor("mixed_types.json"), null, CancellationToken.None));

        // Row 0 has "tags" but not "extra"
        Assert.False(rows[0]["tags"].IsNull);
        Assert.True(rows[0]["extra"].IsNull);

        // Row 1 has "extra" but not "tags"
        Assert.True(rows[1]["tags"].IsNull);
        Assert.False(rows[1]["extra"].IsNull);
    }

    // ───────────────────── Projection pushdown ─────────────────────

    [Fact]
    public async Task Open_ProjectionPushdown_LimitsColumns()
    {
        JsonTableProvider provider = new();
        HashSet<string> required = new(StringComparer.OrdinalIgnoreCase) { "name" };

        List<Row> rows = await ReadAllAsync(
            provider.OpenAsync(Descriptor("array.json"), required, CancellationToken.None));

        Assert.Equal(3, rows.Count);
        Assert.Equal(1, rows[0].FieldCount);
        Assert.Equal("Alice", rows[0]["name"].AsString());
    }

    // ───────────────────── Capabilities ─────────────────────

    [Fact]
    public async Task GetCapabilities_ReturnsDefaults()
    {
        JsonTableProvider provider = new();
        ProviderCapabilities capabilities = await provider.GetCapabilitiesAsync(
            Descriptor("array.json"), CancellationToken.None);

        Assert.Null(capabilities.EstimatedRowCount);
        Assert.False(capabilities.SupportsSeek);
    }

    // ───────────────────── Root object error ─────────────────────

    /// <summary>
    /// Verifies that calling <see cref="JsonTableProvider.GetSchemaAsync"/> on a root-object
    /// JSON file (without a <c>json_path</c> option) produces an error message listing
    /// the available array properties rather than a generic failure.
    /// </summary>
    [Fact]
    public async Task GetSchema_RootObject_WithoutJsonPath_ThrowsWithArrayPropertyNames()
    {
        JsonTableProvider provider = new();

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetSchemaAsync(Descriptor("root_object.json"), CancellationToken.None));

        Assert.Contains("licenses", exception.Message);
        Assert.Contains("captions", exception.Message);
        Assert.Contains("json_path", exception.Message);
    }

    /// <summary>
    /// Verifies that <see cref="IMultiTableSource.DiscoverTablesAsync"/> discovers array
    /// properties in a root-object JSON file and sets the <c>json_path</c> option on each.
    /// </summary>
    [Fact]
    public async Task DiscoverTables_RootObject_ReturnsOneEntryPerArrayProperty()
    {
        JsonTableProvider provider = new();
        IReadOnlyList<DiscoveredTable>? tables = await provider.DiscoverTablesAsync(
            Descriptor("root_object.json"), CancellationToken.None);

        Assert.NotNull(tables);
        Assert.Equal(2, tables.Count);
        Assert.Contains(tables, table => table.Name == "licenses" && table.Options["json_path"] == "licenses");
        Assert.Contains(tables, table => table.Name == "captions" && table.Options["json_path"] == "captions");
    }

    // ───────────────────── Cancellation ─────────────────────

    [Fact]
    public async Task Open_RespectsCancellation()
    {
        JsonTableProvider provider = new();
        CancellationTokenSource cancellationTokenSource = new();
        await cancellationTokenSource.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await ReadAllAsync(
                provider.OpenAsync(Descriptor("array.json"), null, cancellationTokenSource.Token));
        });
    }
}
