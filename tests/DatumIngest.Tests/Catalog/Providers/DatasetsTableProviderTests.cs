using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.DatasetLibrary;
using DatumIngest.Model;
using DatumIngest.ModelLibrary;
using DatumIngest.Pooling;

using Microsoft.Extensions.Logging.Abstractions;

namespace DatumIngest.Tests.Catalog.Providers;

/// <summary>
/// Verifies <see cref="DatasetsTableProvider"/>'s `system.datasets`
/// virtual-table surface — schema columns, rows-per-installed-variant,
/// the single/multi-job naming rule, and the file-presence status flip.
/// </summary>
public sealed class DatasetsTableProviderTests : ServiceTestBase, IDisposable
{
    private readonly string _ingestedRoot = Path.Combine(
        Path.GetTempPath(), $"datasets_provider_test_{Guid.NewGuid():N}");

    public DatasetsTableProviderTests()
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
    public void GetSchema_ExposesExpectedColumns()
    {
        DatasetSchemaBinder binder = BuildBinder(
            entries: [],
            installed: new Dictionary<string, DatasetInstallState>());
        using DatasetsTableProvider provider = new(CreatePool(), binder);

        Schema schema = provider.GetSchema();
        Assert.Equal(13, schema.Columns.Count);
        Assert.Equal("schema", schema.Columns[0].Name);
        Assert.Equal("name", schema.Columns[1].Name);
        Assert.Equal("variant_id", schema.Columns[2].Name);
        Assert.Equal("entry_name", schema.Columns[3].Name);
        Assert.Equal("display_name", schema.Columns[4].Name);
        Assert.Equal("version", schema.Columns[5].Name);
        Assert.Equal("modalities", schema.Columns[6].Name);
        Assert.True(schema.Columns[6].IsArray);
        Assert.Equal("license_ids", schema.Columns[7].Name);
        Assert.True(schema.Columns[7].IsArray);
        Assert.Equal("approx_archive_bytes", schema.Columns[8].Name);
        Assert.Equal(DataKind.Int64, schema.Columns[8].Kind);
        Assert.Equal("approx_ingested_bytes", schema.Columns[9].Name);
        Assert.Equal("file_path", schema.Columns[10].Name);
        Assert.Equal("file_size_bytes", schema.Columns[11].Name);
        Assert.True(schema.Columns[11].Nullable);
        Assert.Equal("status", schema.Columns[12].Name);
    }

    [Fact]
    public async Task ScanAsync_OnlyInstalledVariantsSurface()
    {
        // Two variants: one installed (file on disk + state probe reports
        // Installed), one not. Only the installed one should land in the
        // table.
        TouchDatumFile("coco_test2017", "2017", "images");
        DatasetSchemaBinder binder = BuildBinder(
            entries: [
                Entry("COCO 2017", variants: [
                    Variant("coco_test2017", ["images"]),
                    Variant("coco_val2017", ["images"]),
                ]),
            ],
            installed: new Dictionary<string, DatasetInstallState>
            {
                ["coco_test2017"] = DatasetInstallState.Installed,
                ["coco_val2017"] = DatasetInstallState.NotDownloaded,
            });
        await binder.RebuildAsync();
        using DatasetsTableProvider provider = new(CreatePool(), binder);

        List<string> names = new();
        await foreach (RowBatch batch in provider.ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null,
            cancellationToken: default))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                names.Add(batch[i][1].AsString(batch.Arena)); // name column
            }
        }
        Assert.Single(names);
        Assert.Equal("coco_test2017", names[0]);
        Assert.Equal(1, provider.GetRowCount());
    }

    [Fact]
    public async Task ScanAsync_MultiJobVariant_SurfacesOneRowPerTable()
    {
        // Multi-job variant: two ingest jobs (images + annotations). Both
        // should surface as separate rows with the table-name suffix.
        TouchDatumFile("coco_train2017", "2017", "images");
        TouchDatumFile("coco_train2017", "2017", "annotations");
        DatasetSchemaBinder binder = BuildBinder(
            entries: [
                Entry("COCO 2017", variants: [
                    Variant("coco_train2017", ["images", "annotations"]),
                ]),
            ],
            installed: new Dictionary<string, DatasetInstallState>
            {
                ["coco_train2017"] = DatasetInstallState.Installed,
            });
        await binder.RebuildAsync();
        using DatasetsTableProvider provider = new(CreatePool(), binder);

        List<string> names = new();
        await foreach (RowBatch batch in provider.ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null,
            cancellationToken: default))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                names.Add(batch[i][1].AsString(batch.Arena));
            }
        }
        names.Sort(StringComparer.Ordinal);
        Assert.Equal(["coco_train2017_annotations", "coco_train2017_images"], names);
    }

    [Fact]
    public async Task ScanAsync_FileMissing_StatusReportsMissing()
    {
        // Probe reports Installed but the file is gone (someone wiped the
        // folder out-of-band). The binder skips it during RebuildAsync
        // for the schema mount, but system.datasets still wants to see
        // it as missing — that's the diagnostic.
        //
        // NOTE: the binder's RebuildAsync swallows missing-file rows with
        // a warning so they don't enter the catalog. system.datasets walks
        // the binder's enumeration, which IS filtered to installed-state.
        // For this test we simulate "the file was just deleted between
        // RebuildAsync and the scan" by removing the file after rebuild.
        TouchDatumFile("coco_test2017", "2017", "images");
        DatasetSchemaBinder binder = BuildBinder(
            entries: [Entry("COCO 2017", variants: [Variant("coco_test2017", ["images"])])],
            installed: new Dictionary<string, DatasetInstallState>
            {
                ["coco_test2017"] = DatasetInstallState.Installed,
            });
        await binder.RebuildAsync();

        // Delete the file post-rebuild so the provider's stat returns
        // not-found while the binder's install-state snapshot still says
        // Installed.
        string datumPath = Path.Combine(_ingestedRoot, "coco_test2017", "2017", "images.datum");
        File.Delete(datumPath);

        using DatasetsTableProvider provider = new(CreatePool(), binder);
        string? status = null;
        long? fileSizeIfPresent = null;
        await foreach (RowBatch batch in provider.ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null,
            cancellationToken: default))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                status = batch[i][12].AsString(batch.Arena);
                fileSizeIfPresent = batch[i][11].IsNull ? null : batch[i][11].AsInt64();
            }
        }
        Assert.Equal("missing", status);
        Assert.Null(fileSizeIfPresent);
    }

    // ───────────────── fixtures ─────────────────

    private void TouchDatumFile(string variantId, string version, string tableName)
    {
        string dir = Path.Combine(_ingestedRoot, variantId, version);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, $"{tableName}.datum"), [0x00]);
    }

    private static DatasetEntry Entry(string name, IReadOnlyList<DatasetVariant> variants)
        => new(
            Name: name,
            Summary: "fixture",
            Description: "fixture",
            Modalities: ["Image"],
            LicenseIds: ["cc-by-4.0"],
            Attributions: [],
            SuitableForTasks: null,
            Variants: variants);

    private static DatasetVariant Variant(string id, IReadOnlyList<string> ingestTables)
        => new(
            Id: id,
            DisplayName: $"{id} display",
            Summary: null,
            ApproxArchiveBytes: 1_000_000_000,
            ApproxIngestedBytes: 900_000_000,
            ExpectedRowCounts: null,
            RequiresHfLogin: false,
            Versions:
            [
                new CatalogDatasetVersion(
                    Version: "2017",
                    Sources: [new HttpsSource([new HttpsFile("https://example.invalid/x.zip", "x.zip")])],
                    Ingest: [.. ingestTables.Select(t => new CatalogIngestJob($"{t}.zip", t))]),
            ]);

    private DatasetSchemaBinder BuildBinder(
        IReadOnlyList<DatasetEntry> entries,
        IReadOnlyDictionary<string, DatasetInstallState> installed)
    {
        DatasetCatalogManifest manifest = new(SchemaVersion: 1, Datasets: entries);
        StubManifestStore store = new(manifest);
        DatasetSchemaCatalog catalog = new(
            entries.Select(e => e.Schema).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        VersionedDatasetPathResolver paths = new(
            datasetsCacheRoot: Path.GetTempPath(),
            ingestedDatasetsRoot: _ingestedRoot);
        StubDownloadService downloads = new(installed);
        return new DatasetSchemaBinder(
            manifest: store,
            paths: paths,
            downloads: downloads,
            pool: CreatePool(),
            catalog: catalog,
            logger: NullLogger<DatasetSchemaBinder>.Instance);
    }

    private sealed class StubManifestStore : DatumIngest.DatasetLibrary.IManifestStore
    {
        public StubManifestStore(DatasetCatalogManifest manifest) { Manifest = manifest; }
        public DatasetCatalogManifest Manifest { get; }
        public string ManifestDirectory => Path.GetTempPath();
        public string? GetEntryCardMarkdown(string entryName) => null;
        public string? ResolveEntryAssetPath(string entryName, string relativePath) => null;
        public string? ResolveHeroImagePath(string entryName) => null;
        public (DatasetEntry Entry, DatasetVariant Variant)? FindVariant(string variantId) => null;
    }

    private sealed class StubDownloadService : IDatasetDownloadService
    {
        private readonly IReadOnlyDictionary<string, DatasetInstallState> _states;
        public StubDownloadService(IReadOnlyDictionary<string, DatasetInstallState> states)
        { _states = states; }

        public Func<CancellationToken, Task>? OnVariantsChanged { get; set; }

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
    }
}
