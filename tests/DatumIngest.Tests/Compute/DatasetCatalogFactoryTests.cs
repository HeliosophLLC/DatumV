using DatumIngest.Catalog;
using DatumIngest.Compute.Services;

namespace DatumIngest.Tests.Compute;

/// <summary>
/// Tests for <see cref="DatasetCatalogFactory"/>, verifying auto-discovery
/// of supported files in a dataset directory.
/// </summary>
public sealed class DatasetCatalogFactoryTests : IDisposable
{
    private readonly string _tempDirectory;

    /// <summary>
    /// Creates a temporary directory for each test.
    /// </summary>
    public DatasetCatalogFactoryTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDirectory);
    }

    /// <summary>
    /// Empty directory produces a catalog with no tables but all providers registered.
    /// </summary>
    [Fact]
    public async Task Create_EmptyDirectory_RegistersProvidersOnly()
    {
        TableCatalog catalog = await DatasetCatalogFactory.CreateAsync(_tempDirectory);

        Assert.Empty(catalog.TableNames);
        Assert.Contains("csv", catalog.ProviderNames);
        Assert.Contains("parquet", catalog.ProviderNames);
        Assert.Contains("hdf5", catalog.ProviderNames);
        Assert.Contains("json", catalog.ProviderNames);
        Assert.Contains("jsonl", catalog.ProviderNames);
        Assert.Contains("zip", catalog.ProviderNames);
        Assert.Contains("idx", catalog.ProviderNames);
    }

    /// <summary>
    /// CSV files are auto-discovered and registered with the filename as table name.
    /// </summary>
    [Fact]
    public async Task Create_CsvFile_RegistersTable()
    {
        File.WriteAllText(Path.Combine(_tempDirectory, "sales.csv"), "id,amount\n1,100\n");

        TableCatalog catalog = await DatasetCatalogFactory.CreateAsync(_tempDirectory);

        Assert.Contains("sales", catalog.TableNames);
    }

    /// <summary>
    /// Multiple files with different extensions are all discovered.
    /// </summary>
    [Fact]
    public async Task Create_MultipleFormats_RegistersAll()
    {
        File.WriteAllText(Path.Combine(_tempDirectory, "data.csv"), "a\n1\n");
        File.WriteAllText(Path.Combine(_tempDirectory, "config.json"), "{}");
        File.WriteAllText(Path.Combine(_tempDirectory, "events.jsonl"), "{}\n");

        TableCatalog catalog = await DatasetCatalogFactory.CreateAsync(_tempDirectory);

        Assert.Contains("data", catalog.TableNames);
        Assert.Contains("config", catalog.TableNames);
        Assert.Contains("events", catalog.TableNames);
    }

    /// <summary>
    /// When two files produce the same table name (different extensions), the first wins
    /// and the duplicate is skipped without error.
    /// </summary>
    [Fact]
    public async Task Create_DuplicateTableName_FirstFileWins()
    {
        File.WriteAllText(Path.Combine(_tempDirectory, "data.csv"), "a\n1\n");
        File.WriteAllText(Path.Combine(_tempDirectory, "data.json"), "[]");

        TableCatalog catalog = await DatasetCatalogFactory.CreateAsync(_tempDirectory);

        // Both files map to "data" — only one should be registered.
        Assert.Single(catalog.TableNames, name => name == "data");
    }

    /// <summary>
    /// Unsupported file extensions are ignored.
    /// </summary>
    [Fact]
    public async Task Create_UnsupportedExtension_Ignored()
    {
        File.WriteAllText(Path.Combine(_tempDirectory, "readme.txt"), "hello");
        File.WriteAllText(Path.Combine(_tempDirectory, "data.csv"), "a\n1\n");

        TableCatalog catalog = await DatasetCatalogFactory.CreateAsync(_tempDirectory);

        Assert.DoesNotContain("readme", catalog.TableNames);
        Assert.Contains("data", catalog.TableNames);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Directory.Delete(_tempDirectory, recursive: true);
    }
}
