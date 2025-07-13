using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Model;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// Tests for the <see cref="TableCatalog.Register(string, string)"/> and
/// <see cref="TableCatalog.Register(string, string, IReadOnlyDictionary{string, string})"/>
/// auto-detecting overloads.
/// </summary>
public sealed class TableCatalogAutoDetectTests
{
    private static string FixturePath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
    }

    [Fact]
    public async Task Register_CsvByExtension_ResolvesCorrectly()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());

        catalog.Register("data", FixturePath("simple.csv"));

        TableDescriptor descriptor = catalog.Resolve("data");
        Assert.Equal("csv", descriptor.Provider);
        Assert.Equal("data", descriptor.Name);

        Schema schema = await catalog.GetSchemaAsync("data", CancellationToken.None);
        Assert.True(schema.Columns.Count > 0);
    }

    [Fact]
    public void Register_WithOptions_PassesOptionsThrough()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());

        Dictionary<string, string> options = new() { ["delimiter"] = ";" };
        catalog.Register("data", FixturePath("semicolon.csv"), options);

        TableDescriptor descriptor = catalog.Resolve("data");
        Assert.Equal("csv", descriptor.Provider);
        Assert.True(descriptor.Options.ContainsKey("delimiter"));
        Assert.Equal(";", descriptor.Options["delimiter"]);
    }

    [Fact]
    public void Register_UnknownFormat_ThrowsArgumentException()
    {
        TableCatalog catalog = new();

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => catalog.Register("data", "/nonexistent/file.xyz"));

        Assert.Contains("Cannot detect file format", exception.Message);
        Assert.Contains("Supported formats", exception.Message);
    }

    [Fact]
    public void Register_JsonByExtension_ResolvesCorrectly()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("json", () => new JsonTableProvider());

        catalog.Register("data", FixturePath("array.json"));

        TableDescriptor descriptor = catalog.Resolve("data");
        Assert.Equal("json", descriptor.Provider);
    }

    [Fact]
    public void Register_JsonlByExtension_ResolvesCorrectly()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("jsonl", () => new JsonlTableProvider());

        catalog.Register("data", FixturePath("simple.jsonl"));

        TableDescriptor descriptor = catalog.Resolve("data");
        Assert.Equal("jsonl", descriptor.Provider);
    }
}
