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
    /// CSV files are auto-discovered and registered with the full filename as table name.
    /// </summary>
    [Fact]
    public async Task Create_CsvFile_RegistersTable()
    {
        File.WriteAllText(Path.Combine(_tempDirectory, "sales.csv"), "id,amount\n1,100\n");

        TableCatalog catalog = await DatasetCatalogFactory.CreateAsync(_tempDirectory);

        Assert.Contains("sales_csv", catalog.TableNames);
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

        Assert.Contains("data_csv", catalog.TableNames);
        Assert.Contains("config_json", catalog.TableNames);
        Assert.Contains("events_jsonl", catalog.TableNames);
    }

    /// <summary>
    /// Files with different extensions produce distinct table names and are both registered.
    /// </summary>
    [Fact]
    public async Task Create_DifferentExtensions_RegistersBoth()
    {
        File.WriteAllText(Path.Combine(_tempDirectory, "data.csv"), "a\n1\n");
        File.WriteAllText(Path.Combine(_tempDirectory, "data.json"), "[]");

        TableCatalog catalog = await DatasetCatalogFactory.CreateAsync(_tempDirectory);

        Assert.Contains("data_csv", catalog.TableNames);
        Assert.Contains("data_json", catalog.TableNames);
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

        Assert.DoesNotContain("readme_txt", catalog.TableNames);
        Assert.Contains("data_csv", catalog.TableNames);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Directory.Delete(_tempDirectory, recursive: true);
    }
}
