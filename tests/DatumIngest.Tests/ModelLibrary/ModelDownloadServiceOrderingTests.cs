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
///   <item>installSql declares per-version USING paths like
///   <c>'id/&lt;version&gt;/file.onnx'</c>; the version segment is part
///   of the SQL authoring contract.</item>
///   <item>Internal C# loaders (BuiltinModels et al.) still use
///   id-prefixed shorthand and need the resolver to inject the version
///   segment for them; the resolver consults
///   <see cref="ICatalogActiveVersionLookup"/> for "active" and
///   <see cref="ModelInstallContext.CurrentVersionPin"/> for the
///   in-flight install override.</item>
///   <item>The download service sets the install-context pin around the
///   installer call so resolver-driven paths see the version being
///   installed; the active version becomes that version once the
///   installer's CREATE MODEL chain registers the descriptor (the
///   bare-form catalog row is itself the active-version signal — no
///   separate filesystem pointer to flip).</item>
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

        // ASSERT: the install-context pin named the target version during
        // installSql execution. The resolver's GetActiveVersion read the
        // pin (the lookup itself has no row yet — the installer is a
        // stub), so paths inside installSql resolve to the right version
        // folder. After the installer returns, the bare-form catalog row
        // it produced becomes the active-version signal — no separate
        // filesystem pointer to flip.
        Assert.Equal(1, installer.InstallCallCount);
        Assert.Equal(Version, installer.CapturedActiveVersionAtInstallTime);
        Assert.Equal(Version, installer.CapturedInstallContextPinAtInstallTime);
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

        // ASSERT: 'foo' (the partial registration) was dropped. The lookup
        // never saw a bare-form row register (the failing installer didn't
        // produce one), so probes continue to surface this entry as
        // Downloaded — the user can retry the install.
        Assert.Equal(["foo"], installer.DroppedIdentifiers);
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

    private static CatalogManifest BuildOneEntryManifest(string modelId, string version)
    {
        CatalogVersion ver = new(
            Version: version,
            Sources: [],
            InstallSql: $"sql/{modelId}/{version}.sql",
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
            Models: [model]);
    }

    private sealed class FakeManifestStore(CatalogManifest manifest) : IManifestStore
    {
        public CatalogManifest Manifest { get; } = manifest;
        public string ManifestDirectory { get; } = Path.GetTempPath();
        public ICatalogVocabulary Vocabulary { get; } = new CatalogVocabulary(manifest);
        public string? GetLicenseText(string licenseId) => null;
        public string? GetFamilyCardMarkdown(string modelFamily) => null;
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
