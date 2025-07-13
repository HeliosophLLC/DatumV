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
        WriteSidecar(csvPath, index);

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
        WriteSidecar(csvPath, sidecarIndex);

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
        WriteSidecar(csvPath1, CreateTestIndex(rowCount: 2));
        WriteSidecar(csvPath2, CreateTestIndex(rowCount: 2));

        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        catalog.Register(new TableDescriptor("csv", "images", csvPath1, new Dictionary<string, string>()));
        catalog.Register(new TableDescriptor("csv", "labels", csvPath2, new Dictionary<string, string>()));

        DiscoverSidecarIndexes(catalog);

        Assert.True(catalog.TryGetIndex("images", out _));
        Assert.True(catalog.TryGetIndex("labels", out _));
    }

    /// <summary>
    /// Replicates the auto-discovery logic from <c>Program.cs</c> so it can be
    /// exercised without running the full CLI entry point.
    /// </summary>
    private static void DiscoverSidecarIndexes(TableCatalog catalog)
    {
        IndexReader reader = new();

        foreach (string tableName in catalog.TableNames)
        {
            if (catalog.TryGetIndex(tableName, out _))
            {
                continue;
            }

            TableDescriptor descriptor = catalog.Resolve(tableName);
            string sidecarPath = descriptor.FilePath + ".datum-index";

            if (!File.Exists(sidecarPath))
            {
                continue;
            }

            using FileStream stream = File.OpenRead(sidecarPath);
            SourceIndex index = reader.Read(stream);
            catalog.RegisterIndex(tableName, index);
        }
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
        Schema schema = new([new ColumnInfo("col", DataKind.Scalar, nullable: false)]);
        IndexSchema indexSchema = new(schema, rowCount);
        return new SourceIndex(fingerprint, indexSchema, Array.Empty<IndexChunk>());
    }

    private static void WriteSidecar(string sourceFilePath, SourceIndex index)
    {
        string sidecarPath = sourceFilePath + ".datum-index";
        using FileStream stream = File.Create(sidecarPath);
        IndexWriter writer = new();
        writer.Write(index, stream);
    }
}
