using DatumIngest.Catalog;
using DatumIngest.Indexing;
using DatumIngest.Model;

namespace DatumIngest.Tests.Indexing;

public sealed class TableCatalogIndexTests
{
    [Fact]
    public void RegisterIndex_TryGetIndex_RoundTrips()
    {
        TableCatalog catalog = new();
        catalog.Register(new TableDescriptor("csv", "data", "data.csv", new Dictionary<string, string>()));

        SourceIndex index = CreateTestIndex();
        catalog.RegisterIndex("data", index);

        bool found = catalog.TryGetIndex("data", out SourceIndex? retrieved);

        Assert.True(found);
        Assert.Same(index, retrieved);
    }

    [Fact]
    public void TryGetIndex_UnregisteredTable_ReturnsFalse()
    {
        TableCatalog catalog = new();

        bool found = catalog.TryGetIndex("nonexistent", out SourceIndex? retrieved);

        Assert.False(found);
        Assert.Null(retrieved);
    }

    [Fact]
    public void TryGetIndex_IsCaseInsensitive()
    {
        TableCatalog catalog = new();
        catalog.Register(new TableDescriptor("csv", "Data", "data.csv", new Dictionary<string, string>()));

        SourceIndex index = CreateTestIndex();
        catalog.RegisterIndex("Data", index);

        bool found = catalog.TryGetIndex("data", out SourceIndex? retrieved);

        Assert.True(found);
        Assert.Same(index, retrieved);
    }

    [Fact]
    public async Task GetSchemaAsync_WithIndex_ReturnsCachedSchema()
    {
        // The catalog should return the schema from the index without
        // needing an actual provider.
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => throw new InvalidOperationException("Should not be called"));
        catalog.Register(new TableDescriptor("csv", "data", "data.csv", new Dictionary<string, string>()));

        Schema expectedSchema = new([
            new ColumnInfo("id", DataKind.Float32, nullable: false),
            new ColumnInfo("value", DataKind.String, nullable: true),
        ]);
        IndexSchema indexSchema = new(expectedSchema, 500);
        SourceIndex index = new(new SourceFingerprint(0, new byte[32]), indexSchema, []);
        catalog.RegisterIndex("data", index);

        Schema result = await catalog.GetSchemaAsync("data", CancellationToken.None);

        Assert.Equal(2, result.Columns.Count);
        Assert.Equal("id", result.Columns[0].Name);
        Assert.Equal("value", result.Columns[1].Name);
    }

    [Fact]
    public void RegisterIndex_OverwritesPreviousIndex()
    {
        TableCatalog catalog = new();
        catalog.Register(new TableDescriptor("csv", "data", "data.csv", new Dictionary<string, string>()));

        SourceIndex first = CreateTestIndex();
        SourceIndex second = CreateTestIndex();
        catalog.RegisterIndex("data", first);
        catalog.RegisterIndex("data", second);

        catalog.TryGetIndex("data", out SourceIndex? retrieved);
        Assert.Same(second, retrieved);
    }

    private static SourceIndex CreateTestIndex()
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        Schema schema = new([new ColumnInfo("x", DataKind.Float32, nullable: false)]);
        IndexSchema indexSchema = new(schema, 100);
        return new SourceIndex(fingerprint, indexSchema, []);
    }
}
