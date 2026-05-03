using DatumIngest.Catalog;
using DatumIngest.Catalog.Registries;
using DatumIngest.Inference;
using DatumIngest.Model;
using DatumIngest.ModelLibrary;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// Exercises the v6 catalog file's two model-row shapes:
/// <list type="bullet">
///   <item><description><em>catalog-installed</em> rows persist
///   <c>(catalog_id, catalog_version, pinned_as?)</c> pointers; the
///   source text is null because rehydrate resolves it against the
///   live manifest's installSql.</description></item>
///   <item><description><em>user-authored</em> rows persist the verbatim
///   <c>CREATE MODEL</c> source text because no installSql file
///   exists for them.</description></item>
/// </list>
/// Tests stay below the <c>RehydrateModelsAsync</c> integration line —
/// the resolver / manifest plumbing is exercised end-to-end by the host
/// startup tests; here we pin the round-trip shape only.
/// </summary>
public sealed class CatalogStoreModelRowsV6Tests : ServiceTestBase, IDisposable
{
    private readonly string _scratchDir;
    private readonly string _catalogPath;
    private readonly string _modelFile;
    private readonly string _absoluteUsingPath;

    public CatalogStoreModelRowsV6Tests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            $"datum-catalog-v6-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchDir);
        _catalogPath = Path.Combine(_scratchDir, CatalogStore.DefaultFileName);

        _modelFile = Path.Combine(_scratchDir, $"model-{Guid.NewGuid():N}.onnx");
        File.WriteAllBytes(_modelFile, [0]);
        _absoluteUsingPath = "file://" + _modelFile;
    }

    public new void Dispose()
    {
        base.Dispose();
        try
        {
            if (Directory.Exists(_scratchDir))
            {
                Directory.Delete(_scratchDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup; Windows may still hold a handle.
        }
    }

    private TableCatalog OpenCatalogWithDispatcher()
    {
        TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.InferenceDispatcher = new NullDispatcher();
        return catalog;
    }

    private string UserCreateModelSql(string name) =>
        $"CREATE MODEL {name}(x INT32) RETURNS INT32 " +
        $"USING '{_absoluteUsingPath}' AS BEGIN RETURN x END";

    [Fact]
    public void OlderVersion_File_RejectedAtLoad()
    {
        // Pre-v7 readers no longer exist. Files pinned at an older shape
        // must fail loudly so a stale binary can't silently start fresh
        // and lose state.
        File.WriteAllText(_catalogPath,
            """
            {
              "version": 5,
              "udfs": []
            }
            """);

        CatalogStoreLoadException ex = Assert.Throws<CatalogStoreLoadException>(
            () => CreateCatalog(_catalogPath));
        Assert.Contains("version 5", ex.Message, StringComparison.Ordinal);
        Assert.Contains("requires version 7", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserCreateModel_PersistsFilePath_NoCatalogProvenance()
    {
        // User CREATE MODEL has no catalog parent — the install context's
        // catalog id / version stay null, so the persisted row carries
        // file_path pointing at the on-disk models/<name>.sql instead of
        // a catalog pointer. Verifies the JSON shape and the file content.
        using (TableCatalog first = OpenCatalogWithDispatcher())
        {
            first.Plan(UserCreateModelSql("user_model"));
        }

        string json = File.ReadAllText(_catalogPath);
        Assert.Contains("\"name\": \"user_model\"", json);
        Assert.Contains("\"file_path\": \"models/user_model.sql\"", json);
        // Catalog-pointer fields suppress for user rows because Save sets
        // them to null and the source generator omits null properties.
        Assert.DoesNotContain("\"catalog_id\":", json);
        Assert.DoesNotContain("\"catalog_version\":", json);
        Assert.DoesNotContain("\"pinned_as\":", json);

        // .sql file holds the verbatim CREATE OR REPLACE MODEL text.
        string modelSqlPath = Path.Combine(_scratchDir, "models", "user_model.sql");
        Assert.True(File.Exists(modelSqlPath));
        string source = File.ReadAllText(modelSqlPath);
        Assert.StartsWith("CREATE OR REPLACE MODEL", source);

        // Reopen + rehydrate: user rows take the file_path → source-text
        // path and the descriptor re-lands without a manifest store.
        // Observable signal that the row was treated as a user row.
        using TableCatalog reopened = OpenCatalogWithDispatcher();
        ModelRehydrationReport report = await reopened.RehydrateModelsAsync(
            manifest: null, ct: CancellationToken.None);
        Assert.Equal(1, report.Loaded);
        Assert.Equal(0, report.Skipped);
        Assert.True(reopened.DeclaredModels.TryGet(
            new QualifiedName("models", "user_model"), out _));
    }

    [Fact]
    public void CatalogInstall_PersistsCatalogPointer_NoSourceText()
    {
        // Simulate the install path by pushing the catalog provenance onto
        // ModelInstallContext for the duration of the CREATE MODEL — this
        // is what CatalogBackedModelInstaller does at install time.
        // RoutineRegistrar reads the AsyncLocals and stamps them onto the
        // descriptor, which Save threads into the persisted row.
        string? previousCatalogId = ModelInstallContext.CurrentCatalogId;
        string? previousVersionPin = ModelInstallContext.CurrentVersionPin;
        bool previousIsPinned = ModelInstallContext.CurrentInstallIsPinned;
        ModelInstallContext.CurrentCatalogId = "test-entry";
        ModelInstallContext.CurrentVersionPin = "2026-05-29";
        ModelInstallContext.CurrentInstallIsPinned = false;
        try
        {
            using TableCatalog first = OpenCatalogWithDispatcher();
            first.Plan(UserCreateModelSql("catalog_installed_model"));
        }
        finally
        {
            ModelInstallContext.CurrentInstallIsPinned = previousIsPinned;
            ModelInstallContext.CurrentVersionPin = previousVersionPin;
            ModelInstallContext.CurrentCatalogId = previousCatalogId;
        }

        string json = File.ReadAllText(_catalogPath);
        Assert.Contains("\"name\": \"catalog_installed_model\"", json);
        Assert.Contains("\"catalog_id\": \"test-entry\"", json);
        Assert.Contains("\"catalog_version\": \"2026-05-29\"", json);
        // Bare install: pinned_as stays null, source_text is omitted because
        // the row points at the on-disk installSql for its source of truth.
        Assert.DoesNotContain("\"pinned_as\":", json);
        Assert.DoesNotContain("\"file_path\":", json);
    }

    [Fact]
    public void PinnedCatalogInstall_PersistsPinnedAs()
    {
        // Pinned-mode installs register under the suffixed identifier
        // (e.g. `foo@20260529`). The persisted row records pinned_as so
        // rehydrate knows to re-apply the bare-→-pinned identifier
        // rewrite when re-running the originating installSql.
        string? previousCatalogId = ModelInstallContext.CurrentCatalogId;
        string? previousVersionPin = ModelInstallContext.CurrentVersionPin;
        bool previousIsPinned = ModelInstallContext.CurrentInstallIsPinned;
        ModelInstallContext.CurrentCatalogId = "test-entry";
        ModelInstallContext.CurrentVersionPin = "2026-04-15";
        ModelInstallContext.CurrentInstallIsPinned = true;
        try
        {
            using TableCatalog first = OpenCatalogWithDispatcher();
            first.Plan(UserCreateModelSql("catalog_model_pinned"));
        }
        finally
        {
            ModelInstallContext.CurrentInstallIsPinned = previousIsPinned;
            ModelInstallContext.CurrentVersionPin = previousVersionPin;
            ModelInstallContext.CurrentCatalogId = previousCatalogId;
        }

        string json = File.ReadAllText(_catalogPath);
        Assert.Contains("\"name\": \"catalog_model_pinned\"", json);
        Assert.Contains("\"catalog_id\": \"test-entry\"", json);
        Assert.Contains("\"catalog_version\": \"2026-04-15\"", json);
        Assert.Contains("\"pinned_as\": \"catalog_model_pinned\"", json);
        Assert.DoesNotContain("\"file_path\":", json);
    }

    [Fact]
    public async Task Rehydrate_WithoutManifest_SkipsCatalogRows_WithWarning()
    {
        // Pre-seed a catalog file containing a catalog-installed row and a
        // user-authored row. With no IManifestStore passed in (the Probe
        // tool's path), the catalog row is unrehydratable — there's no
        // way to resolve its installSql — so it should skip with a clear
        // warning. The user row's source_text still flows through.
        string? previousCatalogId = ModelInstallContext.CurrentCatalogId;
        string? previousVersionPin = ModelInstallContext.CurrentVersionPin;
        try
        {
            ModelInstallContext.CurrentCatalogId = "test-entry";
            ModelInstallContext.CurrentVersionPin = "2026-05-29";
            using TableCatalog seed = OpenCatalogWithDispatcher();
            seed.Plan(UserCreateModelSql("from_catalog"));

            ModelInstallContext.CurrentCatalogId = null;
            ModelInstallContext.CurrentVersionPin = null;
            seed.Plan(UserCreateModelSql("from_user"));
        }
        finally
        {
            ModelInstallContext.CurrentVersionPin = previousVersionPin;
            ModelInstallContext.CurrentCatalogId = previousCatalogId;
        }

        using TableCatalog reopened = OpenCatalogWithDispatcher();
        ModelRehydrationReport report = await reopened.RehydrateModelsAsync(
            manifest: null, ct: CancellationToken.None);

        Assert.Equal(1, report.Loaded);
        Assert.Equal(1, report.Skipped);
        Assert.Contains(report.Warnings,
            w => w.Contains("no manifest store available", StringComparison.Ordinal));
        // User row flowed through.
        Assert.True(reopened.DeclaredModels.TryGet(
            new QualifiedName("models", "from_user"), out _));
        // Catalog row stayed skipped.
        Assert.False(reopened.DeclaredModels.TryGet(
            new QualifiedName("models", "from_catalog"), out _));
    }

    /// <summary>
    /// Bare-bones <see cref="IInferenceDispatcher"/> stub — the tests in
    /// this file never trigger an actual session load (CREATE MODEL is
    /// lazy), so a no-op LoadBundleAsync is enough to satisfy registration.
    /// </summary>
    private sealed class NullDispatcher : IInferenceDispatcher
    {
        public IReadOnlyList<IInferenceBackend> Backends => Array.Empty<IInferenceBackend>();

        public ValueTask<IReadOnlyDictionary<string, IModelSession>> LoadBundleAsync(
            BundleManifest bundle,
            InferencePreferences preferences,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyDictionary<string, IModelSession>>(
                new Dictionary<string, IModelSession>(StringComparer.Ordinal));
    }
}
