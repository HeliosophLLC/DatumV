using System.IO.Compression;

using DatumIngest.DatasetLibrary;
using DatumIngest.ModelLibrary;
using DatumIngest.Pooling;
using DatumIngest.Serialization;

using Microsoft.Extensions.Logging.Abstractions;

using SkiaSharp;

// IManifestStore exists in both DatumIngest.DatasetLibrary and
// DatumIngest.ModelLibrary; this file only exercises the dataset one.
using IManifestStore = DatumIngest.DatasetLibrary.IManifestStore;

namespace DatumIngest.Tests.DatasetLibrary;

/// <summary>
/// End-to-end pipeline tests for <see cref="DatasetDownloadService"/>.
/// Wires the orchestrator against a stub source client that "downloads"
/// a hand-built ZIP archive, then verifies the install pipeline drives
/// <c>Ingester.IngestAsync</c> against the engine's <c>ZipDeserializer</c>
/// and produces a <c>.datum</c> file at the expected path under the
/// ingested-datasets root.
/// </summary>
public sealed class DatasetDownloadServicePipelineTests : ServiceTestBase, IDisposable
{
    private const string DatasetId = "fixture-images";
    private const string Version = "v1";
    private const string TableName = "images";

    private readonly string _scratchRoot;
    private readonly string _cacheRoot;
    private readonly string _ingestedRoot;
    private readonly byte[] _fixtureZipBytes;

    public DatasetDownloadServicePipelineTests()
    {
        _scratchRoot = Path.Combine(Path.GetTempPath(),
            "DatumIngest.DatasetPipelineTests", Guid.NewGuid().ToString("N"));
        _cacheRoot = Path.Combine(_scratchRoot, "cache");
        _ingestedRoot = Path.Combine(_scratchRoot, "ingested");
        Directory.CreateDirectory(_cacheRoot);
        Directory.CreateDirectory(_ingestedRoot);

        _fixtureZipBytes = BuildFixtureZip();
    }

    public override void Dispose()
    {
        base.Dispose();
        try { Directory.Delete(_scratchRoot, recursive: true); }
        catch (IOException) { /* best-effort */ }
    }

    [Fact]
    public async Task InstallAsync_DownloadsAndIngests_ProducesDatumFile()
    {
        TestDatasetDownloadProgressReporter reporter = new();
        DatasetDownloadService service = NewService(reporter);

        Task terminal = reporter.WaitForTerminalAsync(DatasetId, NewTimeout());
        await service.InstallAsync(DatasetId);
        await terminal;

        // The orchestrator produces <ingestedRoot>/<id>/<version>/<table>.datum.
        string datumPath = Path.Combine(_ingestedRoot, DatasetId, Version, $"{TableName}.datum");
        Assert.True(File.Exists(datumPath),
            $"Expected ingested .datum at {datumPath} after successful install.");

        // The raw archive is left in the cache (PR 3 will add the
        // keepRawDownloads setting that controls deletion).
        string rawArchive = Path.Combine(_cacheRoot, DatasetId, Version, "fixture.zip");
        Assert.True(File.Exists(rawArchive),
            $"Expected raw archive at {rawArchive} to survive ingest in PR 2 (no keepRawDownloads setting yet).");

        // Probe transitions to Installed after the pipeline finishes.
        DatasetInstallState state = await service.ProbeAsync(DatasetId);
        Assert.Equal(DatasetInstallState.Installed, state);
    }

    [Fact]
    public async Task InstallAsync_EmitsLifecycleEventsInOrder()
    {
        TestDatasetDownloadProgressReporter reporter = new();
        DatasetDownloadService service = NewService(reporter);

        Task terminal = reporter.WaitForTerminalAsync(DatasetId, NewTimeout());
        await service.InstallAsync(DatasetId);
        await terminal;

        Assert.Single(reporter.Started);
        Assert.Single(reporter.Completes);
        DatasetIngesting ingesting = Assert.Single(reporter.Ingestings);
        Assert.Equal(TableName, ingesting.CurrentTable);
        Assert.Equal(1, ingesting.JobIndex);
        Assert.Equal(1, ingesting.JobCount);

        DatasetTableIngested tableIngested = Assert.Single(reporter.TableIngesteds);
        Assert.Equal(TableName, tableIngested.Table);
        // Our fixture archive carries one JPEG-magic-headered entry, so
        // exactly one row should be written. BytesWritten is whatever the
        // writer flushed — we just assert non-zero.
        Assert.Equal(1, tableIngested.RowsWritten);
        Assert.True(tableIngested.BytesWritten > 0);

        Assert.Single(reporter.Installeds);
        Assert.Empty(reporter.Faileds);
    }

    [Fact]
    public async Task InstallAsync_LicenseRequiresAcceptance_ThrowsWithoutAcceptance()
    {
        // requiresAcceptance license that hasn't been accepted should block
        // the install before any bytes move.
        TestDatasetDownloadProgressReporter reporter = new();
        DatasetDownloadService service = NewService(reporter,
            requiresAcceptance: true, licenseAccepted: false);

        LicenseNotAcceptedException ex = await Assert.ThrowsAsync<LicenseNotAcceptedException>(
            () => service.InstallAsync(DatasetId));
        Assert.Equal("test-license", ex.LicenseId);

        // Nothing downloaded — raw cache stays empty for this dataset.
        string idCache = Path.Combine(_cacheRoot, DatasetId);
        Assert.False(Directory.Exists(idCache),
            "License gate should have prevented the raw cache folder from being created.");
    }

    [Fact]
    public async Task InstallAsync_LicenseRequiresAcceptance_RunsWhenAccepted()
    {
        TestDatasetDownloadProgressReporter reporter = new();
        DatasetDownloadService service = NewService(reporter,
            requiresAcceptance: true, licenseAccepted: true);

        Task terminal = reporter.WaitForTerminalAsync(DatasetId, NewTimeout());
        await service.InstallAsync(DatasetId);
        await terminal;

        Assert.Single(reporter.Installeds);
    }

    [Fact]
    public async Task InstallAsync_ConcurrentSecondCall_ThrowsAlreadyInstalling()
    {
        // Single-flight guard: a second InstallAsync for the same id while
        // the first is in flight should throw InvalidOperationException.
        TestDatasetDownloadProgressReporter reporter = new();
        DatasetDownloadService service = NewService(reporter, slowSourceClient: true);

        Task terminal = reporter.WaitForTerminalAsync(DatasetId, NewTimeout());
        await service.InstallAsync(DatasetId);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.InstallAsync(DatasetId));
        Assert.Contains("already installing", ex.Message);

        // Let the first install complete so cleanup runs.
        await terminal;
    }

    [Fact]
    public async Task UninstallAsync_RemovesIngestedFolder_LeavesRawCacheAlone()
    {
        TestDatasetDownloadProgressReporter reporter = new();
        DatasetDownloadService service = NewService(reporter);

        Task terminal = reporter.WaitForTerminalAsync(DatasetId, NewTimeout());
        await service.InstallAsync(DatasetId);
        await terminal;

        string ingestedIdRoot = Path.Combine(_ingestedRoot, DatasetId);
        string rawIdRoot = Path.Combine(_cacheRoot, DatasetId);
        Assert.True(Directory.Exists(ingestedIdRoot));
        Assert.True(Directory.Exists(rawIdRoot));

        await service.UninstallAsync(DatasetId);

        Assert.False(Directory.Exists(ingestedIdRoot),
            "Uninstall should remove the entire ingested <id>/ tree.");
        Assert.True(Directory.Exists(rawIdRoot),
            "Uninstall should leave the raw cache alone — that's a separate purge surface.");
    }

    [Fact]
    public async Task InstallAsync_KeepRawDownloadsNever_DeletesRawCacheAfterIngest()
    {
        TestDatasetDownloadProgressReporter reporter = new();
        DatasetDownloadService service = NewService(reporter,
            keepRawPolicy: new FixedKeepRawDownloadsPolicy(KeepRawDownloadsMode.Never));

        Task terminal = reporter.WaitForTerminalAsync(DatasetId, NewTimeout());
        await service.InstallAsync(DatasetId);
        await terminal;

        // Ingested .datum still present (the dataset is usable).
        string datumPath = Path.Combine(_ingestedRoot, DatasetId, Version, $"{TableName}.datum");
        Assert.True(File.Exists(datumPath));

        // Per-version raw-cache folder wiped — Never means immediate cleanup.
        string rawVersionDir = Path.Combine(_cacheRoot, DatasetId, Version);
        Assert.False(Directory.Exists(rawVersionDir),
            $"Expected per-version raw cache at {rawVersionDir} to be deleted under keepRawDownloads=never.");
    }

    [Fact]
    public async Task InstallAsync_KeepRawDownloadsAlways_KeepsRawCache()
    {
        TestDatasetDownloadProgressReporter reporter = new();
        DatasetDownloadService service = NewService(reporter,
            keepRawPolicy: new FixedKeepRawDownloadsPolicy(KeepRawDownloadsMode.Always));

        Task terminal = reporter.WaitForTerminalAsync(DatasetId, NewTimeout());
        await service.InstallAsync(DatasetId);
        await terminal;

        string rawArchive = Path.Combine(_cacheRoot, DatasetId, Version, "fixture.zip");
        Assert.True(File.Exists(rawArchive),
            $"Expected raw archive at {rawArchive} to survive under keepRawDownloads=always.");
    }

    [Fact]
    public async Task ProbeAsync_NotDownloaded_BeforeInstall()
    {
        DatasetDownloadService service = NewService(new TestDatasetDownloadProgressReporter());
        DatasetInstallState state = await service.ProbeAsync(DatasetId);
        Assert.Equal(DatasetInstallState.NotDownloaded, state);
    }

    [Fact]
    public async Task ProbeAsync_UnknownDatasetId_Throws()
    {
        DatasetDownloadService service = NewService(new TestDatasetDownloadProgressReporter());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ProbeAsync("not-a-real-id"));
    }

    // ─────────────────────────── fixtures ───────────────────────────

    private DatasetDownloadService NewService(
        TestDatasetDownloadProgressReporter reporter,
        bool requiresAcceptance = false,
        bool licenseAccepted = true,
        bool slowSourceClient = false,
        IKeepRawDownloadsPolicy? keepRawPolicy = null)
    {
        FakeManifestStore manifest = new(BuildManifest(requiresAcceptance));
        FakeLicenseAcceptanceService licenses = new(accepted: licenseAccepted);
        FakeSourceClient source = new(_fixtureZipBytes, slow: slowSourceClient);
        VersionedDatasetPathResolver paths = new(_cacheRoot, _ingestedRoot);

        return new DatasetDownloadService(
            store: manifest,
            sourceClients: [source],
            licenses: licenses,
            reporter: reporter,
            paths: paths,
            fileFormats: GetService<IEnumerable<IFileFormat>>(),
            pool: GetService<Pool>(),
            keepRawPolicy: keepRawPolicy ?? DefaultKeepRawDownloadsPolicy.Instance,
            logger: NullLogger<DatasetDownloadService>.Instance);
    }

    private static DatasetCatalogManifest BuildManifest(bool requiresAcceptance)
    {
        CatalogLicense license = new(
            Title: "Test License",
            Spdx: "TEST-1.0",
            CanonicalUrl: "https://example.invalid/",
            TextFile: "licenses/test.txt",
            Summary: "Test.",
            RequiresAcceptance: requiresAcceptance);

        CatalogDatasetVersion version = new(
            Version: Version,
            Sources: [new HttpsSource([new HttpsFile("https://example.invalid/fixture.zip", "fixture.zip")])],
            Ingest: [new CatalogIngestJob("fixture.zip", TableName)],
            InstallSql: null);

        DatasetVariant variant = new(
            Id: DatasetId,
            DisplayName: "Fixture variant",
            Summary: "Fixture variant blurb.",
            ApproxArchiveBytes: 100,
            ApproxIngestedBytes: 100,
            ExpectedRowCounts: new Dictionary<string, long> { [TableName] = 1 },
            RequiresHfLogin: false,
            Versions: [version]);

        DatasetEntry entry = new(
            Name: "Fixture Entry",
            Summary: "Fixture entry for pipeline tests.",
            Description: "One variant carrying a synthetic-image zip fixture.",
            Modalities: ["Image"],
            LicenseIds: ["test-license"],
            Attributions: ["DatumIngest tests"],
            SuitableForTasks: null,
            Variants: [variant]);

        return new DatasetCatalogManifest(
            SchemaVersion: 1,
            Licenses: new Dictionary<string, CatalogLicense>(StringComparer.Ordinal) { ["test-license"] = license },
            Datasets: [entry]);
    }

    // Builds an in-memory zip containing one valid PNG entry. The PNG is
    // generated by Skia at fixture-build time so the engine's sample-
    // preview path (which decodes the bytes back through SKBitmap to
    // build a thumbnail) doesn't choke on synthetic headers.
    private static byte[] BuildFixtureZip()
    {
        byte[] pngBytes = BuildOnePixelPng();

        using MemoryStream ms = new();
        using (ZipArchive archive = new(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = archive.CreateEntry("fixture/000001.png");
            using Stream entryStream = entry.Open();
            entryStream.Write(pngBytes, 0, pngBytes.Length);
        }
        return ms.ToArray();
    }

    // Encodes a 1x1 opaque-white PNG via Skia so the fixture is byte-stable
    // across platforms (no hardcoded magic that might drift with codec
    // changes).
    private static byte[] BuildOnePixelPng()
    {
        using SKBitmap bitmap = new(width: 1, height: 1);
        bitmap.SetPixel(0, 0, SKColors.White);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, quality: 100);
        return data.ToArray();
    }

    private static CancellationToken NewTimeout() =>
        new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private sealed class FixedKeepRawDownloadsPolicy : IKeepRawDownloadsPolicy
    {
        private readonly KeepRawDownloadsMode _mode;
        public FixedKeepRawDownloadsPolicy(KeepRawDownloadsMode mode) { _mode = mode; }
        public ValueTask<KeepRawDownloadsMode> GetAsync(CancellationToken ct)
            => ValueTask.FromResult(_mode);
    }

    private sealed class FakeManifestStore : IManifestStore
    {
        public FakeManifestStore(DatasetCatalogManifest manifest)
        {
            Manifest = manifest;
            ManifestDirectory = string.Empty;
        }

        public DatasetCatalogManifest Manifest { get; }
        public string ManifestDirectory { get; }
        public string? GetLicenseText(string licenseId) => null;
        public string? GetEntryCardMarkdown(string entryName) => null;
        public string? ResolveEntryAssetPath(string entryName, string relativePath) => null;
        public string? ResolveHeroImagePath(string entryName) => null;
        public (DatasetEntry Entry, DatasetVariant Variant)? FindVariant(string variantId)
        {
            foreach (DatasetEntry e in Manifest.Datasets)
            {
                foreach (DatasetVariant v in e.Variants)
                {
                    if (v.Id == variantId) return (e, v);
                }
            }
            return null;
        }
    }

    private sealed class FakeLicenseAcceptanceService : ILicenseAcceptanceService
    {
        private readonly bool _accepted;
        public FakeLicenseAcceptanceService(bool accepted) { _accepted = accepted; }
        public Task<bool> IsAcceptedAsync(string licenseId, CancellationToken ct = default)
            => Task.FromResult(_accepted);
        public Task AcceptAsync(string licenseId, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetAcceptedAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(_accepted ? ["test-license"] : []);
    }

    // Stub IModelSourceClient that "downloads" the fixture zip bytes to
    // the requested destPath. Reuses the existing model-side source-client
    // surface — DatasetDownloadService treats whoever's registered as a
    // file fetcher.
    private sealed class FakeSourceClient : IModelSourceClient
    {
        private readonly byte[] _zipBytes;
        private readonly bool _slow;

        public FakeSourceClient(byte[] zipBytes, bool slow)
        {
            _zipBytes = zipBytes;
            _slow = slow;
        }

        public string SupportedType => "https";

        public ValueTask<IReadOnlyList<SourceFile>> ListFilesAsync(
            CatalogSource source, CancellationToken ct)
        {
            HttpsSource https = (HttpsSource)source;
            HttpsFile file = https.Urls[0];
            return ValueTask.FromResult<IReadOnlyList<SourceFile>>(
                [new SourceFile(file.DestFile, _zipBytes.LongLength, Sha256: null)]);
        }

        public async ValueTask<string> DownloadFileAsync(
            CatalogSource source,
            SourceFile file,
            string destPath,
            IProgress<DownloadByteProgress>? progress,
            CancellationToken ct)
        {
            if (_slow)
            {
                // Hold the download open long enough for the
                // single-flight test to issue a concurrent InstallAsync.
                await Task.Delay(200, ct);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            await File.WriteAllBytesAsync(destPath, _zipBytes, ct);
            return "deadbeef";
        }
    }
}
