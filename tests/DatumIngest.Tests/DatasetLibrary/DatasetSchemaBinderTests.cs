using DatumIngest.Catalog;
using DatumIngest.DatasetLibrary;
using DatumIngest.Execution;
using DatumIngest.ModelLibrary;

using Microsoft.Extensions.Logging.Abstractions;

// Both ModelLibrary and DatasetLibrary expose IManifestStore; alias the
// dataset variant so the test reads naturally.
using IManifestStore = DatumIngest.DatasetLibrary.IManifestStore;

namespace DatumIngest.Tests.DatasetLibrary;

/// <summary>
/// Focused unit tests over <see cref="DatasetSchemaBinder"/>'s
/// <see cref="IPreFlightDatasetSource"/> implementation. The full
/// install-state probe path is exercised end-to-end via the manifest
/// load + pipeline tests; this file covers the manifest-derived
/// candidate lookup so the single-job / multi-job naming rule (and the
/// per-entry schema override) has direct coverage.
/// </summary>
public sealed class DatasetSchemaBinderTests : ServiceTestBase
{
    [Fact]
    public void TryDescribe_SingleJobVariant_TableNameIsVariantId()
    {
        DatasetSchemaBinder binder = BuildBinder(
            schema: "datasets",
            variantId: "coco_test2017",
            ingestTables: ["images"]);

        Assert.True(binder.IsDatasetSchema("datasets"));
        Assert.True(binder.IsDatasetSchema("DATASETS")); // case-insensitive
        Assert.False(binder.IsDatasetSchema("public"));

        Assert.True(binder.TryDescribe("datasets", "coco_test2017", out PreFlightDatasetCandidate? c));
        Assert.Equal("coco_test2017", c!.VariantId);
        Assert.False(c.IsInstalled); // no probe ran; default state == not installed
    }

    [Fact]
    public void TryDescribe_MultiJobVariant_TableNameCarriesTableSuffix()
    {
        DatasetSchemaBinder binder = BuildBinder(
            schema: "datasets",
            variantId: "coco_train2017",
            ingestTables: ["images", "annotations"]);

        // The bare variant id no longer resolves — multi-job variants
        // force the table-name suffix so the user names a specific
        // table per job.
        Assert.False(binder.TryDescribe("datasets", "coco_train2017", out _));

        Assert.True(binder.TryDescribe("datasets", "coco_train2017_images", out PreFlightDatasetCandidate? images));
        Assert.Equal("coco_train2017", images!.VariantId);

        Assert.True(binder.TryDescribe("datasets", "coco_train2017_annotations", out PreFlightDatasetCandidate? ann));
        Assert.Equal("coco_train2017", ann!.VariantId);
    }

    [Fact]
    public void TryDescribe_PerEntrySchemaOverride_RoutesToConfiguredSchema()
    {
        DatasetSchemaBinder binder = BuildBinder(
            schema: "imagenet",
            variantId: "ilsvrc2012",
            ingestTables: ["images"]);

        Assert.True(binder.IsDatasetSchema("imagenet"));
        Assert.False(binder.IsDatasetSchema("datasets"));

        Assert.True(binder.TryDescribe("imagenet", "ilsvrc2012", out PreFlightDatasetCandidate? c));
        Assert.Equal("ilsvrc2012", c!.VariantId);
        // Wrong schema is a miss even though the variant id is known.
        Assert.False(binder.TryDescribe("datasets", "ilsvrc2012", out _));
    }

    private DatasetSchemaBinder BuildBinder(
        string schema,
        string variantId,
        IReadOnlyList<string> ingestTables)
    {
        DatasetEntry entry = new(
            Name: "Test Entry",
            Summary: "Fixture entry.",
            Description: "Fixture for the dataset binder tests.",
            Modalities: ["Image"],
            LicenseIds: ["cc-by-4.0"],
            Attributions: [],
            SuitableForTasks: null,
            Variants:
            [
                new DatasetVariant(
                    Id: variantId,
                    DisplayName: variantId,
                    Summary: null,
                    ApproxArchiveBytes: 1_000_000_000,
                    ApproxIngestedBytes: 1_000_000_000,
                    ExpectedRowCounts: null,
                    RequiresHfLogin: false,
                    Versions:
                    [
                        new CatalogDatasetVersion(
                            Version: "v1",
                            Sources: [new HttpsSource([new HttpsFile("https://example.invalid/x.zip", "x.zip")])],
                            Ingest:
                            [
                                .. ingestTables.Select(t => new CatalogIngestJob($"{t}.zip", t)),
                            ]),
                    ]),
            ],
            Schema: schema);

        DatasetCatalogManifest manifest = new(
            SchemaVersion: 1,
            Datasets: [entry]);

        StubManifestStore store = new(manifest);
        DatasetSchemaCatalog catalog = new([schema]);
        return new DatasetSchemaBinder(
            manifest: store,
            paths: new VersionedDatasetPathResolver(
                datasetsCacheRoot: Path.GetTempPath(),
                ingestedDatasetsRoot: Path.GetTempPath()),
            downloads: new StubDownloadService(),
            pool: CreatePool(),
            catalog: catalog,
            logger: NullLogger<DatasetSchemaBinder>.Instance);
    }

    private sealed class StubManifestStore : IManifestStore
    {
        public StubManifestStore(DatasetCatalogManifest manifest)
        {
            Manifest = manifest;
        }

        public DatasetCatalogManifest Manifest { get; }
        public string ManifestDirectory => Path.GetTempPath();

        public string? GetLicenseText(string licenseId) => null;
        public string? GetEntryCardMarkdown(string entryName) => null;
        public string? ResolveEntryAssetPath(string entryName, string relativePath) => null;
        public string? ResolveHeroImagePath(string entryName) => null;
        public (DatasetEntry Entry, DatasetVariant Variant)? FindVariant(string variantId)
            => null;
    }

    private sealed class StubDownloadService : IDatasetDownloadService
    {
        public Func<CancellationToken, Task>? OnVariantsChanged { get; set; }

        public Task<DatasetInstallState> ProbeAsync(string datasetId, CancellationToken ct = default)
            => Task.FromResult(DatasetInstallState.NotDownloaded);

        public Task<IReadOnlyDictionary<string, DatasetInstallState>> ProbeAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, DatasetInstallState>>(
                new Dictionary<string, DatasetInstallState>());

        public Task InstallAsync(string datasetId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task UninstallAsync(string datasetId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<long> GetPartialBytesAsync(string datasetId, CancellationToken ct = default)
            => Task.FromResult(0L);

        public Task<IReadOnlyDictionary<string, long>> GetAllPartialBytesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>());

        public Task DeletePartialsAsync(string datasetId, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
