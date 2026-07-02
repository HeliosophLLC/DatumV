using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.DatasetLibrary;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.ModelLibrary;
using Heliosoph.DatumV.Pooling;

using Microsoft.Extensions.Logging.Abstractions;

// Both Heliosoph.DatumV.DatasetLibrary and Heliosoph.DatumV.Manifest export a
// DatasetEntry type; this file builds both, so disambiguate per-use
// at the call site rather than aliasing the import.

namespace Heliosoph.DatumV.Tests.LanguageServer;

/// <summary>
/// Verifies the end-to-end manifest path: a real
/// <see cref="TableCatalog"/> with a mounted
/// <see cref="DatasetSchemaCatalog"/> and a populated
/// <see cref="DatasetSchemaBinder"/> produces a manifest whose
/// <c>Tables</c> include the installed dataset's provider entry (so
/// `&lt;schema&gt;.&lt;name&gt;` autocomplete + hover light up).
/// </summary>
public sealed class DatasetManifestIntegrationTests : ServiceTestBase, IDisposable
{
    private readonly string _ingestedRoot = Path.Combine(
        Path.GetTempPath(), $"ds_manifest_int_{Guid.NewGuid():N}");

    public DatasetManifestIntegrationTests()
    {
        Directory.CreateDirectory(_ingestedRoot);
    }

    public override void Dispose()
    {
        base.Dispose();
        if (Directory.Exists(_ingestedRoot))
        {
            try { Directory.Delete(_ingestedRoot, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task InstalledDataset_AppearsInManifestTables()
    {
        // The DatumFileTableProviderV2 ctor opens the .datum, so the
        // sentinel byte fails to construct. The binder's RebuildAsync
        // swallows the per-file open error and skips the mount. That's
        // OK for this test — we don't actually need a v2-valid file;
        // we just need the binder's catalog (DatasetSchemaCatalog) to
        // expose a provider for the manifest builder to enumerate.
        // Skip the binder's rebuild flow and directly inject a synthetic
        // provider into the catalog.
        TouchDatumFile("coco_test2017", "2017", "images");
        TableCatalog catalog = CreateCatalog();

        DatasetSchemaCatalog datasetCatalog = new(["datasets"], catalog.SidecarRegistry);
        catalog.MountSchemaBackend("datasets", datasetCatalog);

        // Synthetic provider mimicking a mounted dataset table.
        Heliosoph.DatumV.Catalog.Providers.InMemoryTableProvider provider = new(
            CreatePool(),
            "datasets.coco_test2017",
            rows: []);
        datasetCatalog.SetTables([provider]);

        DatasetSchemaBinder binder = BuildBinder(
            installed: new Dictionary<string, DatasetInstallState>
            {
                ["coco_test2017"] = DatasetInstallState.Installed,
            },
            datasetCatalog);
        await binder.RebuildAsync();
        // RebuildAsync just refreshed install state; we keep our synthetic
        // provider mount because the binder's own SetTables call replaced
        // the snapshot with whatever it produced from the bad-file path
        // (empty list, since the open throws). Re-attach the synthetic.
        datasetCatalog.SetTables([provider]);

        // The actual assertion: build the manifest and confirm the
        // installed dataset's qualified name surfaces in Tables, plus
        // the LS-side DatasetEntry exists with Installed status.
        LanguageServerManifest manifest = CatalogManifestBuilder.Build(
            catalog, catalog.Functions, binder);

        Assert.Contains(manifest.Tables,
            t => string.Equals(t.Name, "datasets.coco_test2017", StringComparison.Ordinal));
        Assert.NotNull(manifest.Datasets);
        Assert.Contains(manifest.Datasets,
            d => d.Name == "coco_test2017" && d.Status == DatasetInstallStatus.Installed);
    }

    private void TouchDatumFile(string variantId, string version, string tableName)
    {
        string dir = Path.Combine(_ingestedRoot, variantId, version);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, $"{tableName}.datum"), [0x00]);
    }

    private DatasetSchemaBinder BuildBinder(
        IReadOnlyDictionary<string, DatasetInstallState> installed,
        DatasetSchemaCatalog datasetCatalog)
    {
        Heliosoph.DatumV.DatasetLibrary.DatasetEntry entry = new(
            Name: "COCO 2017",
            Summary: "fixture",
            Description: "fixture",
            Modalities: ["Image"],
            LicenseIds: ["cc-by-4.0"],
            Attributions: [],
            SuitableForTasks: null,
            Variants:
            [
                new DatasetVariant(
                    Id: "coco_test2017",
                    DisplayName: "test2017 (images)",
                    Summary: null,
                    ApproxArchiveBytes: 6_646_972_416L,
                    ApproxIngestedBytes: 6_643_406_422L,
                    ExpectedRowCounts: null,
                    RequiresHfLogin: false,
                    Versions:
                    [
                        new CatalogDatasetVersion(
                            Version: "2017",
                            Sources: [new HttpsSource([new HttpsFile("https://example.invalid/x.zip", "x.zip")])],
                            Ingest: [new CatalogIngestJob(TableName: "images", SourcePath: "x.zip")]),
                    ]),
            ]);
        DatasetCatalogManifest manifest = new(SchemaVersion: 1, Datasets: [entry]);
        StubManifestStore store = new(manifest);
        return new DatasetSchemaBinder(
            manifest: store,
            paths: new VersionedDatasetPathResolver(
                datasetsCacheRoot: Path.GetTempPath(),
                ingestedDatasetsRoot: _ingestedRoot),
            downloads: new StubDownloadService(installed),
            pool: CreatePool(),
            catalog: datasetCatalog,
            logger: NullLogger<DatasetSchemaBinder>.Instance);
    }

    private sealed class StubManifestStore : Heliosoph.DatumV.DatasetLibrary.IManifestStore
    {
        public StubManifestStore(DatasetCatalogManifest manifest) { Manifest = manifest; }
        public DatasetCatalogManifest Manifest { get; }
        public string ManifestDirectory => Path.GetTempPath();
        public string? GetEntryCardMarkdown(string entryName) => null;
        public string? ResolveEntryAssetPath(string entryName, string relativePath) => null;
        public string? ResolveHeroImagePath(string entryName) => null;
        public string? GetRecipeSql(string relativePath) => null;
        public (Heliosoph.DatumV.DatasetLibrary.DatasetEntry Entry, DatasetVariant Variant)? FindVariant(string variantId) => null;
    }

    private sealed class StubDownloadService : IDatasetDownloadService
    {
        private readonly IReadOnlyDictionary<string, DatasetInstallState> _states;
        public StubDownloadService(IReadOnlyDictionary<string, DatasetInstallState> states)
        { _states = states; }
        public Func<CancellationToken, Task>? OnVariantsChanged { get; set; }
        public Action<string>? OnVariantUninstalling { get; set; }
        public Task<DatasetInstallState> ProbeAsync(string datasetId, CancellationToken ct = default)
            => Task.FromResult(_states.TryGetValue(datasetId, out DatasetInstallState s) ? s : DatasetInstallState.NotDownloaded);
        public Task<IReadOnlyDictionary<string, DatasetInstallState>> ProbeAllAsync(CancellationToken ct = default)
            => Task.FromResult(_states);
        public Task InstallAsync(string datasetId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UninstallAsync(string datasetId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<long> GetPartialBytesAsync(string datasetId, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<IReadOnlyDictionary<string, long>> GetAllPartialBytesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>());
        public Task DeletePartialsAsync(string datasetId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> SweepStagingDirsAsync(CancellationToken ct = default) => Task.FromResult(0);
    }
}
