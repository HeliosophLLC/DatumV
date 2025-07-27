using DatumIngest.Catalog;
using DatumIngest.Model;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// Tests for <see cref="TableCatalog"/> constructor defaults and <c>RegisterAsync</c> methods.
/// </summary>
public sealed class TableCatalogRegistrationTests
{
    private static string FixturePath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
    }

    /// <summary>
    /// Verifies that built-in providers are pre-registered by the constructor.
    /// </summary>
    [Theory]
    [InlineData("csv")]
    [InlineData("json")]
    [InlineData("jsonl")]
    [InlineData("parquet")]
    [InlineData("hdf5")]
    [InlineData("zip")]
    [InlineData("idx")]
    public void Constructor_RegistersBuiltInProvider(string providerName)
    {
        TableCatalog catalog = new();

        Assert.Contains(providerName, catalog.ProviderNames);
    }

    /// <summary>
    /// Verifies that a CSV table can be used without any explicit <c>RegisterProvider</c> call.
    /// </summary>
    [Fact]
    public async Task Constructor_CsvProviderWorksWithoutManualRegistration()
    {
        TableCatalog catalog = new();
        catalog.Register("data", FixturePath("simple.csv"));

        Schema schema = await catalog.GetSchemaAsync("data", CancellationToken.None);

        Assert.True(schema.Columns.Count > 0);
    }

    /// <summary>
    /// Verifies that <c>RegisterProvider</c> can override a built-in factory.
    /// </summary>
    [Fact]
    public void RegisterProvider_OverridesBuiltInFactory()
    {
        TableCatalog catalog = new();
        bool customFactoryCalled = false;

        catalog.RegisterProvider("csv", () =>
        {
            customFactoryCalled = true;
            return new DatumIngest.Catalog.Providers.CsvTableProvider();
        });

        TableDescriptor descriptor = new("csv", "data", FixturePath("simple.csv"), new Dictionary<string, string>());
        catalog.Register(descriptor);
        ITableProvider provider = catalog.CreateProvider(descriptor);

        Assert.True(customFactoryCalled);
    }

    /// <summary>
    /// Verifies that <c>RegisterAsync</c> with name and path auto-expands multi-table JSON sources.
    /// </summary>
    [Fact]
    public async Task RegisterAsync_ExpandsMultiTableJson()
    {
        TableCatalog catalog = new();

        await catalog.RegisterAsync("data", FixturePath("root_object.json"), CancellationToken.None);

        // The original "data" registration should be replaced by sub-tables.
        Assert.False(catalog.TryResolve("data", out _));
        Assert.True(catalog.TryResolve("data.licenses", out _));
        Assert.True(catalog.TryResolve("data.captions", out _));
    }

    /// <summary>
    /// Verifies that <c>RegisterAsync</c> with a descriptor auto-expands multi-table sources.
    /// </summary>
    [Fact]
    public async Task RegisterAsync_WithDescriptor_ExpandsMultiTableJson()
    {
        TableCatalog catalog = new();
        TableDescriptor descriptor = new("json", "data", FixturePath("root_object.json"), new Dictionary<string, string>());

        await catalog.RegisterAsync(descriptor, CancellationToken.None);

        Assert.False(catalog.TryResolve("data", out _));
        Assert.True(catalog.TryResolve("data.licenses", out _));
        Assert.True(catalog.TryResolve("data.captions", out _));
    }

    /// <summary>
    /// Verifies that <c>RegisterAsync</c> for a single-table source keeps the original registration.
    /// </summary>
    [Fact]
    public async Task RegisterAsync_SingleTableSource_KeepsOriginalName()
    {
        TableCatalog catalog = new();

        await catalog.RegisterAsync("data", FixturePath("simple.csv"), CancellationToken.None);

        Assert.True(catalog.TryResolve("data", out _));
    }

    /// <summary>
    /// Verifies that sub-table schemas are readable after <c>RegisterAsync</c>.
    /// </summary>
    [Fact]
    public async Task RegisterAsync_ExpandedTablesHaveReadableSchemas()
    {
        TableCatalog catalog = new();
        await catalog.RegisterAsync("data", FixturePath("root_object.json"), CancellationToken.None);

        Schema licensesSchema = await catalog.GetSchemaAsync("data.licenses", CancellationToken.None);
        Schema captionsSchema = await catalog.GetSchemaAsync("data.captions", CancellationToken.None);

        Assert.True(licensesSchema.Columns.Count > 0);
        Assert.True(captionsSchema.Columns.Count > 0);
    }

    /// <summary>
    /// Verifies that <c>RegisterAsync</c> with options passes them through to the descriptor.
    /// </summary>
    [Fact]
    public async Task RegisterAsync_WithOptions_PassesOptionsThrough()
    {
        TableCatalog catalog = new();
        Dictionary<string, string> options = new() { ["json_path"] = "$.licenses" };

        await catalog.RegisterAsync(
            "data",
            FixturePath("root_object.json"),
            options,
            CancellationToken.None);

        // With json_path specified, it should NOT expand — it targets a specific array.
        Assert.True(catalog.TryResolve("data", out TableDescriptor? descriptor));
        Assert.Equal("$.licenses", descriptor!.Options["json_path"]);
    }
}
