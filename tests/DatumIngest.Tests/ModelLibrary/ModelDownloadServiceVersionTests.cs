using DatumIngest.ModelLibrary;
using DatumIngest.Models.Python;

using Microsoft.Extensions.Logging.Abstractions;

namespace DatumIngest.Tests.ModelLibrary;

/// <summary>
/// Pins the per-version Activate + Delete contract surfaced through the
/// model card's "Previous versions" disclosure. Activate runs a
/// version-switch (drop outgoing bare identifiers, re-register outgoing
/// in pinned form, install incoming bare); Delete removes one version
/// folder plus the identifiers it registered. The bare-form catalog
/// row produced by the incoming install is itself the active-version
/// signal — no filesystem pointer to flip.
/// </summary>
public sealed class ModelDownloadServiceVersionTests : ServiceTestBase
{
    private readonly string _root;

    public ModelDownloadServiceVersionTests()
    {
        _root = Path.Combine(Path.GetTempPath(),
            "DatumIngest.VersionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public override void Dispose()
    {
        base.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort */ }
    }

    [Fact]
    public async Task ActivateVersion_FlipsPointer_DropsOutgoingBare_RegistersOutgoingPinned_InstallsIncomingBare()
    {
        const string ModelId = "switcher";
        const string OldVersion = "2026-04-15";
        const string NewVersion = "2026-05-29";

        StubLookup lookup = new();
        lookup.Set(ModelId, OldVersion);
        VersionedModelPathResolver paths = new(_root, lookup);
        // Both version folders must exist on disk for the activate path
        // to engage — IsVersionOnDisk gates the target check, and the
        // outgoing pinned re-register also requires its folder.
        Directory.CreateDirectory(Path.Combine(_root, ModelId, OldVersion));
        Directory.CreateDirectory(Path.Combine(_root, ModelId, NewVersion));

        CatalogManifest manifest = BuildTwoVersionManifest(
            ModelId,
            oldVersion: OldVersion, oldIdentifiers: ["foo", "foo_meters"],
            newVersion: NewVersion, newIdentifiers: ["foo", "foo_pose"]);
        FakeManifestStore store = new(manifest);
        RecordingInstaller installer = new();

        ModelDownloadService service = new(
            store:         store,
            sourceClients: [new NoOpSourceClient()],
            licenses:      new AlwaysAcceptedLicenseService(),
            licenseRegistry: DatumIngest.Tests.Support.TestLicenseRegistry.Instance,
            reporter:      new NullProgressReporter(),
            installer:     installer,
            python:        new NullPythonEnvironmentManager(),
            paths:         paths,
            logger:        NullLogger<ModelDownloadService>.Instance);

        await service.ActivateVersionAsync(ModelId, NewVersion, CancellationToken.None);

        // Outgoing version's bare identifiers were dropped.
        Assert.Equal(new[] { "foo", "foo_meters" }, installer.DroppedIdentifiers);

        // Two installer calls happened: outgoing pinned re-register first
        // (registers foo@20260415 + foo_meters@20260415), then incoming
        // bare install (registers foo, foo_pose). The incoming bare-mode
        // install is what makes NewVersion the new active — its CatalogVersion
        // becomes the lookup's answer for ModelId.
        Assert.Equal(2, installer.Calls.Count);
        Assert.True(installer.Calls[0].PinnedMode);
        Assert.Equal(OldVersion, installer.Calls[0].Version);
        Assert.False(installer.Calls[1].PinnedMode);
        Assert.Equal(NewVersion, installer.Calls[1].Version);
    }

    [Fact]
    public async Task ActivateVersion_TargetEqualsActive_IsNoOp()
    {
        const string ModelId = "switcher";
        const string Version = "2026-05-29";

        StubLookup lookup = new();
        lookup.Set(ModelId, Version);
        VersionedModelPathResolver paths = new(_root, lookup);
        Directory.CreateDirectory(Path.Combine(_root, ModelId, Version));

        CatalogManifest manifest = BuildTwoVersionManifest(
            ModelId,
            oldVersion: "2026-04-15", oldIdentifiers: ["foo"],
            newVersion: Version, newIdentifiers: ["foo"]);
        FakeManifestStore store = new(manifest);
        RecordingInstaller installer = new();

        ModelDownloadService service = new(
            store: store, sourceClients: [new NoOpSourceClient()],
            licenses: new AlwaysAcceptedLicenseService(),
            licenseRegistry: DatumIngest.Tests.Support.TestLicenseRegistry.Instance,
            reporter: new NullProgressReporter(),
            installer: installer, python: new NullPythonEnvironmentManager(),
            paths: paths, logger: NullLogger<ModelDownloadService>.Instance);

        await service.ActivateVersionAsync(ModelId, Version, CancellationToken.None);

        Assert.Empty(installer.Calls);
        Assert.Empty(installer.DroppedIdentifiers);
    }

    [Fact]
    public async Task ActivateVersion_TargetNotOnDisk_Throws()
    {
        const string ModelId = "switcher";
        const string Version = "2026-05-29";

        VersionedModelPathResolver paths = new(_root);
        CatalogManifest manifest = BuildTwoVersionManifest(
            ModelId,
            oldVersion: "2026-04-15", oldIdentifiers: ["foo"],
            newVersion: Version, newIdentifiers: ["foo"]);

        ModelDownloadService service = new(
            store: new FakeManifestStore(manifest),
            sourceClients: [new NoOpSourceClient()],
            licenses: new AlwaysAcceptedLicenseService(),
            licenseRegistry: DatumIngest.Tests.Support.TestLicenseRegistry.Instance,
            reporter: new NullProgressReporter(),
            installer: new RecordingInstaller(),
            python: new NullPythonEnvironmentManager(),
            paths: paths, logger: NullLogger<ModelDownloadService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ActivateVersionAsync(ModelId, Version, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteVersion_NonActiveOnDisk_DropsPinnedIdentifiers_RemovesFolder()
    {
        const string ModelId = "deleter";
        const string ActiveVersion = "2026-05-29";
        const string OldVersion = "2026-04-15";

        StubLookup lookup = new();
        lookup.Set(ModelId, ActiveVersion);
        VersionedModelPathResolver paths = new(_root, lookup);
        Directory.CreateDirectory(Path.Combine(_root, ModelId, ActiveVersion));
        Directory.CreateDirectory(Path.Combine(_root, ModelId, OldVersion));

        CatalogManifest manifest = BuildTwoVersionManifest(
            ModelId,
            oldVersion: OldVersion, oldIdentifiers: ["foo", "foo_meters"],
            newVersion: ActiveVersion, newIdentifiers: ["foo"]);
        RecordingInstaller installer = new();

        ModelDownloadService service = new(
            store: new FakeManifestStore(manifest),
            sourceClients: [new NoOpSourceClient()],
            licenses: new AlwaysAcceptedLicenseService(),
            licenseRegistry: DatumIngest.Tests.Support.TestLicenseRegistry.Instance,
            reporter: new NullProgressReporter(),
            installer: installer, python: new NullPythonEnvironmentManager(),
            paths: paths, logger: NullLogger<ModelDownloadService>.Instance);

        await service.DeleteVersionAsync(ModelId, OldVersion, CancellationToken.None);

        // Pinned-form identifiers dropped (suffix derived from OldVersion's digits).
        Assert.Equal(new[] { "foo@20260415", "foo_meters@20260415" }, installer.DroppedIdentifiers);
        // Version folder gone; the active version (bare-form catalog row)
        // is the installer's concern and stays intact because nothing was
        // dropped for ActiveVersion.
        Assert.False(Directory.Exists(Path.Combine(_root, ModelId, OldVersion)));
    }

    [Fact]
    public async Task DeleteVersion_Active_DropsBareIdentifiers_ClearsActivePointer_RemovesFolder()
    {
        const string ModelId = "deleter";
        const string ActiveVersion = "2026-05-29";

        StubLookup lookup = new();
        lookup.Set(ModelId, ActiveVersion);
        VersionedModelPathResolver paths = new(_root, lookup);
        Directory.CreateDirectory(Path.Combine(_root, ModelId, ActiveVersion));

        CatalogManifest manifest = BuildTwoVersionManifest(
            ModelId,
            oldVersion: "2026-04-15", oldIdentifiers: ["foo"],
            newVersion: ActiveVersion, newIdentifiers: ["foo", "foo_pose"]);
        RecordingInstaller installer = new();

        ModelDownloadService service = new(
            store: new FakeManifestStore(manifest),
            sourceClients: [new NoOpSourceClient()],
            licenses: new AlwaysAcceptedLicenseService(),
            licenseRegistry: DatumIngest.Tests.Support.TestLicenseRegistry.Instance,
            reporter: new NullProgressReporter(),
            installer: installer, python: new NullPythonEnvironmentManager(),
            paths: paths, logger: NullLogger<ModelDownloadService>.Instance);

        await service.DeleteVersionAsync(ModelId, ActiveVersion, CancellationToken.None);

        // Bare identifiers dropped (no @-suffix because this was active).
        // Dropping the bare-form catalog rows removes the active-version
        // signal — the lookup will now answer null for ModelId on next
        // read (the installer's DROP MODEL chain produced that side-effect).
        Assert.Equal(new[] { "foo", "foo_pose" }, installer.DroppedIdentifiers);
        Assert.False(Directory.Exists(Path.Combine(_root, ModelId, ActiveVersion)));
    }

    [Fact]
    public async Task DeleteVersion_NotOnDiskAndNotActive_IsNoOp()
    {
        const string ModelId = "deleter";
        const string MissingVersion = "2026-04-15";

        VersionedModelPathResolver paths = new(_root);
        CatalogManifest manifest = BuildTwoVersionManifest(
            ModelId,
            oldVersion: MissingVersion, oldIdentifiers: ["foo"],
            newVersion: "2026-05-29", newIdentifiers: ["foo"]);
        RecordingInstaller installer = new();

        ModelDownloadService service = new(
            store: new FakeManifestStore(manifest),
            sourceClients: [new NoOpSourceClient()],
            licenses: new AlwaysAcceptedLicenseService(),
            licenseRegistry: DatumIngest.Tests.Support.TestLicenseRegistry.Instance,
            reporter: new NullProgressReporter(),
            installer: installer, python: new NullPythonEnvironmentManager(),
            paths: paths, logger: NullLogger<ModelDownloadService>.Instance);

        await service.DeleteVersionAsync(ModelId, MissingVersion, CancellationToken.None);

        Assert.Empty(installer.DroppedIdentifiers);
    }

    // ----- helpers ---------------------------------------------------

    private static CatalogManifest BuildTwoVersionManifest(
        string modelId,
        string oldVersion, string[] oldIdentifiers,
        string newVersion, string[] newIdentifiers)
    {
        CatalogVersion newer = new(
            Version: newVersion,
            Sources: [],
            InstallSql: $"sql/{modelId}/{newVersion}.sql",
            Models: ToVersionModels(newIdentifiers));
        CatalogVersion older = new(
            Version: oldVersion,
            Sources: [],
            InstallSql: $"sql/{modelId}/{oldVersion}.sql",
            Models: ToVersionModels(oldIdentifiers));

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
            Versions: [newer, older],
            ApproxSizeMb: 0);

        return new CatalogManifest(
            SchemaVersion: 2,
            Models: [model]);

        static IReadOnlyList<CatalogVersionModel> ToVersionModels(string[] identifiers)
        {
            CatalogVersionModel[] result = new CatalogVersionModel[identifiers.Length];
            for (int i = 0; i < identifiers.Length; i++)
            {
                result[i] = new CatalogVersionModel(identifiers[i]);
            }
            return result;
        }
    }

    private sealed record InstallCall(string Version, bool PinnedMode);

    /// <summary>
    /// Echoes back identifiers the catalog declares for the call's
    /// version (in pinned-suffix form when <c>pinnedMode</c> is true,
    /// bare otherwise) so cross-check inside the service sees a
    /// match. Records every call + every DROP for assertions.
    /// </summary>
    private sealed class RecordingInstaller : IModelInstaller
    {
        public List<InstallCall> Calls { get; } = [];
        public List<string> DroppedIdentifiers { get; } = [];

        public ValueTask<bool> IsInstalledAsync(CatalogModel model, CancellationToken ct)
            => ValueTask.FromResult(true);

        public ValueTask<IReadOnlyList<string>> InstallAsync(
            CatalogModel model, CatalogVersion version, bool pinnedMode, CancellationToken ct)
        {
            Calls.Add(new InstallCall(version.Version, pinnedMode));
            List<string> observed = [];
            foreach (CatalogVersionModel vm in version.Models ?? [])
            {
                observed.Add(pinnedMode ? vm.EffectivePinnedAs(version.Version) : vm.Identifier);
            }
            return ValueTask.FromResult<IReadOnlyList<string>>(observed);
        }

        public ValueTask DropModelsAsync(IReadOnlyList<string> identifiers, CancellationToken ct)
        {
            DroppedIdentifiers.AddRange(identifiers);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeManifestStore(CatalogManifest manifest) : IManifestStore
    {
        public CatalogManifest Manifest { get; } = manifest;
        public string ManifestDirectory { get; } = Path.GetTempPath();
        public ICatalogVocabulary Vocabulary { get; } = new CatalogVocabulary(manifest);
        public string? GetLicenseText(string licenseId) => null;
        public string? GetFamilyCardMarkdown(string modelFamily) => null;
        public string? ResolveFamilyCardAssetPath(string modelFamily, string relativePath) => null;
        public string? ResolveHeroImagePath(string modelId) => null;
    }

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

    /// <summary>
    /// In-memory <see cref="ICatalogActiveVersionLookup"/> stub. Tests
    /// pre-seed it with the desired pre-state ("for this catalog id,
    /// version X is active") and read back through the resolver.
    /// </summary>
    private sealed class StubLookup : ICatalogActiveVersionLookup
    {
        private readonly Dictionary<string, string> _active = new(StringComparer.Ordinal);

        public string? GetActiveVersion(string catalogId)
            => _active.TryGetValue(catalogId, out string? v) ? v : null;

        public void Set(string catalogId, string version) => _active[catalogId] = version;
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
