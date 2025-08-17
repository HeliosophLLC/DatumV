using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Model;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// Tests for the <see cref="TableCatalog.GetSchemaAsync"/> convenience method.
/// </summary>
public sealed class TableCatalogSchemaTests
{
    private static string FixturePath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
    }

    [Fact]
    public async Task GetSchemaAsync_KnownTable_ReturnsSchema()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        catalog.Register(new TableDescriptor(
            "csv", "data", FixturePath("simple.csv"),
            new Dictionary<string, string>()));

        Schema schema = await catalog.GetSchemaAsync("data", CancellationToken.None);

        Assert.Equal(3, schema.Columns.Count);
        Assert.Equal("name", schema.Columns[0].Name);
        Assert.Equal(DataKind.String, schema.Columns[0].Kind);
        Assert.Equal("age", schema.Columns[1].Name);
        Assert.Equal(DataKind.Int32, schema.Columns[1].Kind);
    }

    [Fact]
    public async Task GetSchemaAsync_UnknownTable_ThrowsKeyNotFoundException()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => catalog.GetSchemaAsync("nonexistent", CancellationToken.None));
    }

    [Fact]
    public async Task GetSchemaAsync_CaseInsensitiveName_ReturnsSchema()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        catalog.Register(new TableDescriptor(
            "csv", "MyTable", FixturePath("simple.csv"),
            new Dictionary<string, string>()));

        Schema schema = await catalog.GetSchemaAsync("mytable", CancellationToken.None);

        Assert.Equal(3, schema.Columns.Count);
    }
}
