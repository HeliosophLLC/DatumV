using DatumIngest.ModelLibrary;
using DatumIngest.Models.Python;

using Microsoft.Extensions.Logging.Abstractions;

namespace DatumIngest.Tests.ModelLibrary;

/// <summary>
/// Pins the install-sequence ordering invariants in
/// <see cref="ModelDownloadService"/> that, when violated, surface as
/// runtime "file not found" errors from CREATE MODEL inside the catalog
/// entry's installSql or as in-flight queries observing torn active state.
/// </summary>
/// <remarks>
/// The bug this guards against:
/// <list type="bullet">
///   <item>installSql contains <c>CREATE MODEL ... USING 'id/file.onnx'</c>.</item>
///   <item>USING resolves through <see cref="IModelPathResolver"/>, which
///   reads <c>&lt;id&gt;/active</c> and injects the active version unless
///   <see cref="ModelInstallContext.CurrentVersionPin"/> overrides.</item>
///   <item>The download service sets the install-context pin around the
///   installer call so USING paths resolve to the version being installed
///   without flipping the active pointer mid-install (which would race
///   with in-flight queries on a cross-check failure).</item>
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
    public async Task InstallAsync_SetsAmbientPinForInstaller_FlipsActiveAfterSuccess()
    {
        // ARRANGE: a tiny catalog with one entry that declares installSql,
        // a no-op source client (download returns success without writing
        // any bytes), and a recording installer that captures both the
        // active version and the install-context pin at the moment
        // InstallAsync is called.
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

        // Wait for the post-installer active-pointer flip — it runs in the
        // background Task.Run after the installer's TCS completes.
        while (paths.GetActiveVersion(ModelId) is null)
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(5, timeout.Token);
        }

        // ASSERT: at the moment installSql ran the active pointer was NOT
        // yet set (it flips only after a successful install + cross-check)
        // and the install-context pin named the version being installed.
        // After the install finished, the active pointer is the target
        // version.
        Assert.Equal(1, installer.InstallCallCount);
        Assert.Null(installer.CapturedActiveVersionAtInstallTime);
        Assert.Equal(Version, installer.CapturedInstallContextPinAtInstallTime);
        Assert.Equal(Version, paths.GetActiveVersion(ModelId));
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

    [Fact]
    public async Task InstallAsync_CrossCheckMismatch_DropsPartialRegistrations_AndLeavesActiveUnflipped()
    {
        // ARRANGE: a manifest with declared identifiers [foo, bar], but
        // the installer reports only [foo] observed. The cross-check must
        // detect the missing 'bar', DROP the partially-registered 'foo',
        // and leave the active pointer unset (it never flipped, because
        // the cross-check runs before the flip).
        const string ModelId = "cross-check-entry";
        const string Version = "2026-05-29";

        VersionedModelPathResolver paths = new(_root);
        CatalogManifest manifest = BuildManifestWithDeclaredIdentifiers(
            ModelId, Version, ["foo", "bar"]);
        FakeManifestStore store = new(manifest);
        PartialObservingInstaller installer = new(observed: ["foo"]);

        ModelDownloadService service = new(
            store:         store,
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
        await installer.DropCalled.Task.WaitAsync(timeout.Token);

        // ASSERT: 'foo' (the partial registration) was dropped, and the
        // active pointer was never flipped (so probes see Downloaded).
        Assert.Equal(["foo"], installer.DroppedIdentifiers);
        Assert.Null(paths.GetActiveVersion(ModelId));
    }

    private static CatalogManifest BuildManifestWithDeclaredIdentifiers(
        string modelId, string version, string[] identifiers)
    {
        CatalogVersionModel[] decl = new CatalogVersionModel[identifiers.Length];
        for (int i = 0; i < identifiers.Length; i++)
        {
            decl[i] = new CatalogVersionModel(identifiers[i]);
        }
        CatalogVersion ver = new(
            Version: version,
            Sources: [],
            InstallSql: $"sql/{modelId}/{version}.sql",
            Models: decl);
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

    /// <summary>
    /// Installer that returns a fixed list of "observed" identifiers
    /// regardless of input. Used by the cross-check test to simulate a
    /// catalog/SQL drift where the JSON declares more identifiers than
    /// the SQL actually registers.
    /// </summary>
    private sealed class PartialObservingInstaller(IReadOnlyList<string> observed) : IModelInstaller
    {
        public TaskCompletionSource InstallCalled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DropCalled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> DroppedIdentifiers { get; } = [];

        public ValueTask<bool> IsInstalledAsync(CatalogModel model, CancellationToken ct)
            => ValueTask.FromResult(true);

        public ValueTask<IReadOnlyList<string>> InstallAsync(
            CatalogModel model, CatalogVersion version, bool pinnedMode, CancellationToken ct)
        {
            InstallCalled.TrySetResult();
            return ValueTask.FromResult(observed);
        }

        public ValueTask DropModelsAsync(IReadOnlyList<string> identifiers, CancellationToken ct)
        {
            DroppedIdentifiers.AddRange(identifiers);
            DropCalled.TrySetResult();
            return ValueTask.CompletedTask;
        }
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

        public string? CapturedInstallContextPinAtInstallTime { get; private set; }

        public ValueTask<IReadOnlyList<string>> InstallAsync(
            CatalogModel model, CatalogVersion version, bool pinnedMode, CancellationToken ct)
        {
            InstallCallCount++;
            CapturedActiveVersionAtInstallTime = paths.GetActiveVersion(modelId);
            CapturedInstallContextPinAtInstallTime = ModelInstallContext.CurrentVersionPin;
            InstallCalled.TrySetResult();
            return ValueTask.FromResult<IReadOnlyList<string>>([]);
        }

        public ValueTask DropModelsAsync(IReadOnlyList<string> identifiers, CancellationToken ct)
            => ValueTask.CompletedTask;
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
        public ValueTask<IReadOnlyList<string>> InstallAsync(
            CatalogModel model, CatalogVersion version, bool pinnedMode, CancellationToken ct)
            => throw new InvalidOperationException("installer must not be called for entries without installSql");
        public ValueTask DropModelsAsync(IReadOnlyList<string> identifiers, CancellationToken ct)
            => ValueTask.CompletedTask;
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
