using DatumIngest.ModelLibrary;
using DatumIngest.Models.Python;

using Microsoft.Extensions.Logging.Abstractions;

namespace DatumIngest.Tests.ModelLibrary;

/// <summary>
/// Pins the install-sequence ordering invariants in
/// <see cref="ModelDownloadService"/> that, when violated, surface as
/// runtime "file not found" errors from CREATE MODEL inside the catalog
/// entry's installSql.
/// </summary>
/// <remarks>
/// The bug this guards against:
/// <list type="bullet">
///   <item>installSql contains <c>CREATE MODEL ... USING 'id/file.onnx'</c>.</item>
///   <item>USING resolves through <see cref="IModelPathResolver"/>, which
///   reads <c>&lt;id&gt;/active</c> and injects the active version.</item>
///   <item>If <see cref="IModelPathResolver.SetActiveVersion"/> hasn't run
///   yet at the moment installSql executes, the resolver falls back to
///   the flat layout — and the weights are at
///   <c>&lt;root&gt;/&lt;id&gt;/&lt;version&gt;/file.onnx</c>, not
///   <c>&lt;root&gt;/&lt;id&gt;/file.onnx</c>. CREATE MODEL throws
///   FileNotFoundException before the user can ever call the model.</item>
/// </list>
/// </remarks>
public sealed class ModelDownloadServiceOrderingTests : ServiceTestBase
{
    private readonly string _root;

    public ModelDownloadServiceOrderingTests()
    {
        _root = Path.Combine(Path.GetTempPath(),
            "DatumIngest.OrderingTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public override void Dispose()
    {
        base.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort */ }
    }

    [Fact]
    public async Task InstallAsync_FlipsActivePointerBeforeRunningInstallSql()
    {
        // ARRANGE: a tiny catalog with one entry that declares installSql,
        // a no-op source client (download returns success without writing
        // any bytes), and a recording installer that captures the active
        // version at the moment InstallAsync is called AND signals
        // completion via a TCS so the test doesn't have to race the
        // background Task.Run that drives RunDownloadAsync.
        const string ModelId = "test-model";
        const string Version = "2026-05-29";

        VersionedModelPathResolver paths = new(_root);
        FakeManifestStore manifest = new(BuildOneEntryManifest(ModelId, Version));
        VersionRecordingInstaller installer = new(modelId: ModelId, paths: paths);

        ModelDownloadService service = new(
            store:         manifest,
            sourceClients: [new NoOpSourceClient()],
            licenses:      new AlwaysAcceptedLicenseService(),
            reporter:      new NullProgressReporter(),
            installer:     installer,
            python:        new NullPythonEnvironmentManager(),
            paths:         paths,
            logger:        NullLogger<ModelDownloadService>.Instance);

        // ACT
        await service.InstallAsync(ModelId, CancellationToken.None);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await installer.InstallCalled.Task.WaitAsync(timeout.Token);

        // ASSERT: the installer was called exactly once and saw the active
        // version already set to the catalog's recommended version. If
        // SetActiveVersion runs after the installer, the captured value is
        // null and CREATE MODEL would have been resolving against the wrong
        // path.
        Assert.Equal(1, installer.InstallCallCount);
        Assert.Equal(Version, installer.CapturedActiveVersionAtInstallTime);
    }

    [Fact]
    public async Task InstallAsync_NoInstallSql_StillFlipsActivePointer()
    {
        // For catalog entries with no installSql (download-only entries
        // like raw GGUF blobs), the active pointer still needs to flip
        // so later queries against `models.<id>` resolve to the right
        // version folder.
        const string ModelId = "test-blob";
        const string Version = "2026-05-29";

        VersionedModelPathResolver paths = new(_root);
        FakeManifestStore manifest = new(BuildOneEntryManifest(ModelId, Version, withInstallSql: false));

        ModelDownloadService service = new(
            store: manifest, sourceClients: [new NoOpSourceClient()],
            licenses: new AlwaysAcceptedLicenseService(),
            reporter: new NullProgressReporter(),
            installer: new ThrowingInstaller(),
            python: new NullPythonEnvironmentManager(),
            paths: paths, logger: NullLogger<ModelDownloadService>.Instance);

        await service.InstallAsync(ModelId, CancellationToken.None);
        // RunDownloadAsync is fire-and-forget under Task.Run; spin
        // briefly until the active pointer flips (it's the last thing
        // before the python/installSql steps return). 5 s is generous
        // for a no-I/O test run; the actual path is microseconds.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (paths.GetActiveVersion(ModelId) is null)
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(5, timeout.Token);
        }

        Assert.Equal(Version, paths.GetActiveVersion(ModelId));
    }

    // ----- harness types ---------------------------------------------------

    private static CatalogManifest BuildOneEntryManifest(
        string modelId, string version, bool withInstallSql = true)
    {
        CatalogVersion ver = new(
            Version: version,
            Sources: [],
            InstallSql: withInstallSql ? $"sql/{modelId}/{version}.sql" : null,
            Models: null);

        CatalogModel model = new(
            Id: modelId,
            DisplayName: modelId,
            Summary: "Test entry.",
            Description: "Test.",
            Tasks: ["TextEmbedder"],
            Tags: [],
            LicenseIds: [],
            Attributions: [],
            Hardware: new CatalogHardware(MinRamMb: 0, MinVramMb: 0, Preferred: "cpu"),
            Versions: [ver],
            ApproxSizeMb: 0);

        return new CatalogManifest(
            SchemaVersion: 2,
            Licenses: new Dictionary<string, CatalogLicense>(),
            Tiers: new CatalogTiers([], []),
            Models: [model]);
    }

    private sealed class FakeManifestStore(CatalogManifest manifest) : IManifestStore
    {
        public CatalogManifest Manifest { get; } = manifest;
        public string ManifestDirectory { get; } = Path.GetTempPath();
        public ICatalogVocabulary Vocabulary { get; } = new CatalogVocabulary(manifest);
        public string? GetLicenseText(string licenseId) => null;
    }

    /// <summary>
    /// Captures <see cref="IModelPathResolver.GetActiveVersion"/> at the
    /// moment <see cref="InstallAsync"/> runs. This is the contract under
    /// test: when installSql executes (which the real installer does
    /// inside <see cref="InstallAsync"/>), the active pointer must
    /// already name the version being installed.
    /// </summary>
    private sealed class VersionRecordingInstaller(string modelId, IModelPathResolver paths) : IModelInstaller
    {
        public int InstallCallCount { get; private set; }
        public string? CapturedActiveVersionAtInstallTime { get; private set; }
        public TaskCompletionSource InstallCalled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<bool> IsInstalledAsync(CatalogModel model, CancellationToken ct)
            => ValueTask.FromResult(true);

        public ValueTask InstallAsync(CatalogModel model, CancellationToken ct)
        {
            InstallCallCount++;
            CapturedActiveVersionAtInstallTime = paths.GetActiveVersion(modelId);
            InstallCalled.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Bare <see cref="IDownloadProgressReporter"/> that swallows every
    /// event. The ordering tests don't care about progress — they assert
    /// on post-install state directly.
    /// </summary>
    private sealed class NullProgressReporter : IDownloadProgressReporter
    {
        public ValueTask OnStartedAsync(ModelDownloadStarted s, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask OnProgressAsync(ModelDownloadProgress p, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask OnCompleteAsync(ModelDownloadComplete c, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask OnInstallingAsync(ModelInstalling i, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask OnInstalledAsync(ModelInstalled i, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask OnFailedAsync(ModelDownloadFailed f, CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class ThrowingInstaller : IModelInstaller
    {
        public ValueTask<bool> IsInstalledAsync(CatalogModel model, CancellationToken ct)
            => ValueTask.FromResult(true);
        public ValueTask InstallAsync(CatalogModel model, CancellationToken ct)
            => throw new InvalidOperationException("installer must not be called for entries without installSql");
    }

    private sealed class NoOpSourceClient : IModelSourceClient
    {
        public string SupportedType => "huggingface";
        public ValueTask<IReadOnlyList<SourceFile>> ListFilesAsync(CatalogSource source, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<SourceFile>>([]);
        public ValueTask<string> DownloadFileAsync(
            CatalogSource source, SourceFile file, string destPath,
            IProgress<DownloadByteProgress>? progress, CancellationToken ct)
            => ValueTask.FromResult(string.Empty);
    }

    private sealed class AlwaysAcceptedLicenseService : ILicenseAcceptanceService
    {
        public Task<bool> IsAcceptedAsync(string licenseId, CancellationToken ct = default)
            => Task.FromResult(true);
        public Task AcceptAsync(string licenseId, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetAcceptedAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class NullPythonEnvironmentManager : IPythonEnvironmentManager
    {
        public Task<string> EnsureUvAsync(CancellationToken cancellationToken)
            => Task.FromResult(string.Empty);
        public Task<string> EnsurePythonAsync(string version, CancellationToken cancellationToken)
            => Task.FromResult(string.Empty);
        public Task<string> EnsureVenvAsync(string venvName, string pythonVersion,
            IReadOnlyList<string> requirements, CancellationToken cancellationToken)
            => Task.FromResult(string.Empty);
        public Task<bool> RemoveVenvAsync(string venvName, CancellationToken cancellationToken)
            => Task.FromResult(false);
    }
}
