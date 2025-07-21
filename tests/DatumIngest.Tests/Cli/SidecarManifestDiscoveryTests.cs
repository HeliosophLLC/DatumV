using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Tests.Cli;

/// <summary>
/// Tests for automatic discovery of <c>.datum-manifest</c> sidecar files
/// when sources are registered in the catalog.
/// </summary>
public sealed class SidecarManifestDiscoveryTests : IDisposable
{
    private readonly string _tempDirectory;

    public SidecarManifestDiscoveryTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "datum-manifest-" + Guid.NewGuid().ToString("N"));
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
    public void DiscoversSidecarManifest_WhenFileExists()
    {
        string csvPath = CreateCsvFile("data.csv", "id,name\n1,Alice\n2,Bob\n");
        QueryResultsManifest manifest = CreateTestManifest(rowCount: 2, columnName: "id", ndv: 2);
        WriteSidecar(csvPath, manifest);

        TableCatalog catalog = CreateCatalog("data", csvPath);

        DiscoverSidecarManifests(catalog);

        Assert.True(catalog.TryGetManifest("data", out QueryResultsManifest? discovered));
        Assert.Equal(2, discovered!.RowCount);
    }

    [Fact]
    public void SkipsSidecar_WhenNoFileExists()
    {
        string csvPath = Path.Combine(_tempDirectory, "missing.csv");
        File.WriteAllText(csvPath, "id\n1\n");

        TableCatalog catalog = CreateCatalog("data", csvPath);

        DiscoverSidecarManifests(catalog);

        Assert.False(catalog.TryGetManifest("data", out _));
    }

    [Fact]
    public void SkipsSidecar_WhenManifestAlreadyRegistered()
    {
        string csvPath = CreateCsvFile("data.csv", "id\n1\n");
        QueryResultsManifest explicitManifest = CreateTestManifest(rowCount: 999, columnName: "id", ndv: 999);
        QueryResultsManifest sidecarManifest = CreateTestManifest(rowCount: 1, columnName: "id", ndv: 1);
        WriteSidecar(csvPath, sidecarManifest);

        TableCatalog catalog = CreateCatalog("data", csvPath);
        catalog.RegisterManifest("data", explicitManifest);

        DiscoverSidecarManifests(catalog);

        Assert.True(catalog.TryGetManifest("data", out QueryResultsManifest? found));
        Assert.Equal(999, found!.RowCount);
    }

    [Fact]
    public void DiscoversMultipleSidecars_ForMultipleSources()
    {
        string csvPath1 = CreateCsvFile("images.csv", "pixel\n0\n1\n");
        string csvPath2 = CreateCsvFile("labels.csv", "label\n3\n7\n");
        WriteSidecar(csvPath1, CreateTestManifest(rowCount: 2, columnName: "pixel", ndv: 2));
        WriteSidecar(csvPath2, CreateTestManifest(rowCount: 2, columnName: "label", ndv: 2));

        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        catalog.Register(new TableDescriptor("csv", "images", csvPath1, new Dictionary<string, string>()));
        catalog.Register(new TableDescriptor("csv", "labels", csvPath2, new Dictionary<string, string>()));

        DiscoverSidecarManifests(catalog);

        Assert.True(catalog.TryGetManifest("images", out _));
        Assert.True(catalog.TryGetManifest("labels", out _));
    }

    [Fact]
    public void DiscoveredManifest_PreservesColumnStatistics()
    {
        string csvPath = CreateCsvFile("data.csv", "age,name\n25,Alice\n30,Bob\n");
        QueryResultsManifest manifest = CreateTestManifest(rowCount: 2, columnName: "age", ndv: 2);
        WriteSidecar(csvPath, manifest);

        TableCatalog catalog = CreateCatalog("data", csvPath);

        DiscoverSidecarManifests(catalog);

        Assert.True(catalog.TryGetManifest("data", out QueryResultsManifest? discovered));
        Assert.Single(discovered!.Features);
        Assert.Equal("age", discovered.Features[0].Name);
        Assert.Equal(2, discovered.Features[0].EstimatedDistinctCount);
    }

    /// <summary>
    /// Replicates the auto-discovery logic from <c>Program.cs</c> so it can be
    /// exercised without running the full CLI entry point.
    /// </summary>
    private static void DiscoverSidecarManifests(TableCatalog catalog)
    {
        foreach (string tableName in catalog.TableNames)
        {
            if (catalog.TryGetManifest(tableName, out _))
            {
                continue;
            }

            TableDescriptor descriptor = catalog.Resolve(tableName);
            string sidecarPath = descriptor.FilePath + ".datum-manifest";

            if (!File.Exists(sidecarPath))
            {
                continue;
            }

            string json = File.ReadAllText(sidecarPath);
            QueryResultsManifest? manifest = ManifestSerializer.Deserialize(json);

            if (manifest is not null)
            {
                catalog.RegisterManifest(tableName, manifest);
            }
        }
    }

    private string CreateCsvFile(string name, string content)
    {
        string path = Path.Combine(_tempDirectory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static TableCatalog CreateCatalog(string tableName, string csvPath)
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        catalog.Register(new TableDescriptor("csv", tableName, csvPath, new Dictionary<string, string>()));
        return catalog;
    }

    private static QueryResultsManifest CreateTestManifest(long rowCount, string columnName, long ndv)
    {
        NumericFeatureManifest feature = new()
        {
            Name = columnName,
            Kind = DataKind.Scalar,
            Count = rowCount,
            NullCount = 0,
            ValidCount = rowCount,
            EstimatedDistinctCount = ndv,
            TopKValues = [],
            NullRatio = 0.0,
            Min = 0,
            Max = 100,
            Mean = 50,
            Variance = 25,
            StandardDeviation = 5,
            Skewness = 0,
            Kurtosis = 0,
            Histogram = new HistogramData([0, 100], [rowCount]),
            Quantiles = null,
            ZeroCount = 0,
            ZeroRatio = 0,
            OutlierCount = 0,
            OutlierRatio = 0,
            IntegerValued = true,
        };

        return new QueryResultsManifest
        {
            RowCount = rowCount,
            GeneratedAtUtc = DateTime.UtcNow,
            Features = [feature],
        };
    }

    private static void WriteSidecar(string sourceFilePath, QueryResultsManifest manifest)
    {
        string sidecarPath = sourceFilePath + ".datum-manifest";
        string json = ManifestSerializer.Serialize(manifest);
        File.WriteAllText(sidecarPath, json);
    }
}
