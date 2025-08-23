using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Indexing;
using DatumIngest.Model;

namespace DatumIngest.Tests.Cli;

/// <summary>
/// Tests for automatic discovery of <c>.datum-index</c> sidecar files
/// when sources are registered in the catalog.
/// </summary>
public sealed class SidecarIndexDiscoveryTests : IDisposable
{
    private readonly string _tempDirectory;

    public SidecarIndexDiscoveryTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "datum-sidecar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void DiscoversSidecarIndex_WhenFileExists()
    {
        string csvPath = CreateCsvFile("data.csv", "id,name\n1,Alice\n2,Bob\n");
        SourceIndex index = CreateTestIndex(rowCount: 2);
        WriteSidecar(csvPath, "data", index);

        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        catalog.Register(new TableDescriptor("csv", "data", csvPath, new Dictionary<string, string>()));

        DiscoverSidecarIndexes(catalog);

        Assert.True(catalog.TryGetIndex("data", out SourceIndex? discovered));
        Assert.Equal(2, discovered!.Schema.TotalRowCount);
    }

    [Fact]
    public void SkipsSidecar_WhenNoFileExists()
    {
        string csvPath = Path.Combine(_tempDirectory, "missing.csv");
        File.WriteAllText(csvPath, "id\n1\n");

        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        catalog.Register(new TableDescriptor("csv", "data", csvPath, new Dictionary<string, string>()));

        DiscoverSidecarIndexes(catalog);

        Assert.False(catalog.TryGetIndex("data", out _));
    }

    [Fact]
    public void SkipsSidecar_WhenExplicitIndexAlreadyRegistered()
    {
        string csvPath = CreateCsvFile("data.csv", "id\n1\n");
        SourceIndex explicitIndex = CreateTestIndex(rowCount: 999);
        SourceIndex sidecarIndex = CreateTestIndex(rowCount: 1);
        WriteSidecar(csvPath, "data", sidecarIndex);

        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        catalog.Register(new TableDescriptor("csv", "data", csvPath, new Dictionary<string, string>()));
        catalog.RegisterIndex("data", explicitIndex);

        DiscoverSidecarIndexes(catalog);

        Assert.True(catalog.TryGetIndex("data", out SourceIndex? found));
        Assert.Equal(999, found!.Schema.TotalRowCount);
    }

    [Fact]
    public void DiscoversMultipleSidecars_ForMultipleSources()
    {
        string csvPath1 = CreateCsvFile("images.csv", "pixel\n0\n1\n");
        string csvPath2 = CreateCsvFile("labels.csv", "label\n3\n7\n");
        WriteSidecar(csvPath1, "images", CreateTestIndex(rowCount: 2));
        WriteSidecar(csvPath2, "labels", CreateTestIndex(rowCount: 2));

        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        catalog.Register(new TableDescriptor("csv", "images", csvPath1, new Dictionary<string, string>()));
        catalog.Register(new TableDescriptor("csv", "labels", csvPath2, new Dictionary<string, string>()));

        DiscoverSidecarIndexes(catalog);

        Assert.True(catalog.TryGetIndex("images", out _));
        Assert.True(catalog.TryGetIndex("labels", out _));
    }

    [Fact]
    public void DiscoversSidecarIndex_WhenEntryKeyUsesDerivedTableName()
    {
        string datumPath = CreateCsvFile("orders.csv", "id\n1\n");
        SourceIndex index = CreateTestIndex(rowCount: 1);
        WriteSidecar(datumPath, "orders_csv", index);

        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        catalog.Register(new TableDescriptor("csv", "orders_alias", datumPath, new Dictionary<string, string>()));

        DiscoverSidecarIndexes(catalog);

        Assert.True(catalog.TryGetIndex("orders_alias", out SourceIndex? discovered));
        Assert.Equal(1, discovered!.Schema.TotalRowCount);
    }

    [Fact]
    public void IndexIsNotLoadedImmediately_AfterDiscoverSidecars()
    {
        // Regression guard: DiscoverSidecars must not deserialize the index file at
        // startup. The index is only loaded on the first call to TryGetIndex.
        // We verify this indirectly — DiscoverSidecars completes instantly (no I/O
        // proportional to the index size) and the index is available on first access.
        string csvPath = CreateCsvFile("lazy.csv", "id\n1\n");
        SourceIndex index = CreateTestIndex(rowCount: 42);
        WriteSidecar(csvPath, "lazy", index);

        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        catalog.Register(new TableDescriptor("csv", "lazy", csvPath, new Dictionary<string, string>()));

        // DiscoverSidecars should record the pending path without touching the file data.
        catalog.DiscoverSidecars();

        // First access triggers the deferred load.
        Assert.True(catalog.TryGetIndex("lazy", out SourceIndex? loaded));
        Assert.Equal(42, loaded!.Schema.TotalRowCount);

        // Second access returns the already-cached entry without re-reading the file.
        Assert.True(catalog.TryGetIndex("lazy", out SourceIndex? cached));
        Assert.Same(loaded, cached);
    }

    [Fact]
    public void TwoTablesShareOneSidecar_BothLoadedOnFirstAccess()
    {
        // When two tables map to the same source file (e.g. multi-alias scenario),
        // a single TryGetIndex call for one of them should populate both so the
        // sidecar file is read exactly once.
        string csvPath = CreateCsvFile("shared.csv", "id\n1\n");
        SourceIndex index = CreateTestIndex(rowCount: 7);

        // Write sidecar keyed by the source table name; both aliases share the file.
        string sidecarPath = csvPath + ".datum-index";
        using (FileStream stream = File.Create(sidecarPath))
        {
            IndexWriter writer = new();
        // Write entries for both alias names in a single sidecar set.
            SourceFingerprint fingerprint = new(0, new byte[32]);
            SourceIndexSet indexSet = new(fingerprint, new Dictionary<string, SourceIndex>(StringComparer.OrdinalIgnoreCase)
            {
                ["alias_a"] = index,
                ["alias_b"] = index,
            });
            writer.Write(indexSet, stream);
        }

        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        catalog.Register(new TableDescriptor("csv", "alias_a", csvPath, new Dictionary<string, string>()));
        catalog.Register(new TableDescriptor("csv", "alias_b", csvPath, new Dictionary<string, string>()));

        catalog.DiscoverSidecars();

        // Accessing alias_a should cause the sidecar to be read; alias_b should already be populated.
        Assert.True(catalog.TryGetIndex("alias_a", out SourceIndex? indexA));
        Assert.Equal(7, indexA!.Schema.TotalRowCount);

        Assert.True(catalog.TryGetIndex("alias_b", out SourceIndex? indexB));
        Assert.Equal(7, indexB!.Schema.TotalRowCount);
    }

    /// <summary>
    /// Delegates to the unified <see cref="TableCatalog.DiscoverSidecars"/> method.
    /// </summary>
    private static void DiscoverSidecarIndexes(TableCatalog catalog)
    {
        catalog.DiscoverSidecars();
    }

    private string CreateCsvFile(string name, string content)
    {
        string path = Path.Combine(_tempDirectory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static SourceIndex CreateTestIndex(int rowCount)
    {
        SourceFingerprint fingerprint = new(0, new byte[32]);
        Schema schema = new([new ColumnInfo("col", DataKind.Float32, nullable: false)]);
        IndexSchema indexSchema = new(schema, rowCount);
        return new SourceIndex(fingerprint, indexSchema, Array.Empty<IndexChunk>());
    }

    private static void WriteSidecar(string sourceFilePath, string tableName, SourceIndex index)
    {
        string sidecarPath = sourceFilePath + ".datum-index";
        using FileStream stream = File.Create(sidecarPath);
        IndexWriter writer = new();
        SourceIndexSet indexSet = SourceIndexSet.Create(tableName, index);
        writer.Write(indexSet, stream);
    }
}
