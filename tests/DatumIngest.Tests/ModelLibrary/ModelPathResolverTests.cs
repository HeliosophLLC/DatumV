using DatumIngest.ModelLibrary;
using DatumIngest.Models;

namespace DatumIngest.Tests.ModelLibrary;

/// <summary>
/// Tests the path-resolver substrate: <see cref="VersionedModelPathResolver"/>
/// (production), <see cref="FlatModelPathResolver"/> (legacy fallback), and
/// <see cref="ModelCatalog.ResolveFilePath"/> (the SQL-side entry point that
/// routes through whatever resolver the catalog carries).
/// </summary>
public sealed class ModelPathResolverTests : IDisposable
{
    private readonly string _root;

    public ModelPathResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "DatumIngest.PathResolverTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort */ }
    }

    [Fact]
    public void VersionedResolver_NoActivePointer_FallsBackToVersionlessFolder()
    {
        VersionedModelPathResolver resolver = new(_root);
        string actual = resolver.GetModelRoot("foo");
        Assert.Equal(Path.Combine(_root, "foo"), actual);
        Assert.Null(resolver.GetActiveVersion("foo"));
    }

    [Fact]
    public void VersionedResolver_ActivePointerSet_InjectsVersionSegment()
    {
        VersionedModelPathResolver resolver = new(_root);
        resolver.SetActiveVersion("foo", "2026-05-29");

        Assert.Equal(
            Path.Combine(_root, "foo", "2026-05-29"),
            resolver.GetModelRoot("foo"));
        Assert.Equal("2026-05-29", resolver.GetActiveVersion("foo"));
    }

    [Fact]
    public void VersionedResolver_ExplicitVersionPin_OverridesActive()
    {
        VersionedModelPathResolver resolver = new(_root);
        resolver.SetActiveVersion("foo", "2026-05-29");

        Assert.Equal(
            Path.Combine(_root, "foo", "2026-04-15"),
            resolver.GetModelRoot("foo", versionPin: "2026-04-15"));
    }

    [Fact]
    public void VersionedResolver_SetActiveVersion_AtomicallyReplacesPointer()
    {
        VersionedModelPathResolver resolver = new(_root);
        resolver.SetActiveVersion("foo", "2026-04-15");
        resolver.SetActiveVersion("foo", "2026-05-29");

        // After overwrite the pointer reflects the newest write; no .tmp
        // leftovers stranded next to it.
        Assert.Equal("2026-05-29", resolver.GetActiveVersion("foo"));
        string idFolder = Path.Combine(_root, "foo");
        Assert.True(File.Exists(Path.Combine(idFolder, VersionedModelPathResolver.ActivePointerFilename)));
        Assert.False(File.Exists(Path.Combine(idFolder, VersionedModelPathResolver.ActivePointerFilename + ".tmp")));
    }

    [Fact]
    public void VersionedResolver_InvalidateActiveVersionCache_RefreshesNextRead()
    {
        VersionedModelPathResolver resolver = new(_root);

        // Read once with no pointer — caches "missing".
        Assert.Null(resolver.GetActiveVersion("foo"));

        // Write the pointer directly to the filesystem (bypassing
        // SetActiveVersion's cache update) to simulate a sibling resolver
        // / external installer doing the write.
        string idFolder = Path.Combine(_root, "foo");
        Directory.CreateDirectory(idFolder);
        File.WriteAllText(
            Path.Combine(idFolder, VersionedModelPathResolver.ActivePointerFilename),
            "2026-05-29");

        // Cache still says "missing" until invalidation.
        Assert.Null(resolver.GetActiveVersion("foo"));
        resolver.InvalidateActiveVersionCache("foo");
        Assert.Equal("2026-05-29", resolver.GetActiveVersion("foo"));
    }

    [Fact]
    public void VersionedResolver_ResolveIdPrefixedPath_SplitsAtFirstSlash()
    {
        VersionedModelPathResolver resolver = new(_root);
        resolver.SetActiveVersion("all-minilm-l6-v2", "2026-05-29");

        // SQL USING 'all-minilm-l6-v2/model.onnx' must resolve into the
        // active-version folder — this is the bug fix.
        string actual = resolver.ResolveIdPrefixedPath("all-minilm-l6-v2/model.onnx");
        Assert.Equal(
            Path.Combine(_root, "all-minilm-l6-v2", "2026-05-29", "model.onnx"),
            actual);
    }

    [Fact]
    public void VersionedResolver_ResolveIdPrefixedPath_PreservesFlatFallbackForNonCatalogPaths()
    {
        VersionedModelPathResolver resolver = new(_root);
        // No active pointer for "my-experiments" — user's own subfolder.
        // Falls back to <root>/my-experiments/x.onnx, byte-identical to
        // the pre-substrate behaviour.
        Assert.Equal(
            Path.Combine(_root, "my-experiments", "x.onnx"),
            resolver.ResolveIdPrefixedPath("my-experiments/x.onnx"));
    }

    [Fact]
    public void VersionedResolver_ResolveIdPrefixedPath_NoSlashLandsDirectlyUnderRoot()
    {
        VersionedModelPathResolver resolver = new(_root);
        // Preserves the pre-catalog "bare GGUF filename in $DATUM_MODELS"
        // layout that a couple of BuiltinModels registrations still use.
        Assert.Equal(
            Path.Combine(_root, "Phi-3-mini-Q4_K_M.gguf"),
            resolver.ResolveIdPrefixedPath("Phi-3-mini-Q4_K_M.gguf"));
    }

    [Fact]
    public void ResolveFilePath_RelativeUsingPath_RoutesThroughActiveVersion()
    {
        VersionedModelPathResolver resolver = new(_root);
        resolver.SetActiveVersion("all-minilm-l6-v2", "2026-05-29");
        using ModelCatalog catalog = new(_root, vramBudgetBytes: 0, admissionTimeout: null,
            calibrationStore: null, hostFingerprint: null, pathResolver: resolver);

        string actual = ModelCatalog.ResolveFilePath(
            "all-minilm-l6-v2/model.onnx", catalog, "test");
        Assert.Equal(
            Path.GetFullPath(Path.Combine(_root, "all-minilm-l6-v2", "2026-05-29", "model.onnx")),
            actual);
    }

    [Fact]
    public void ResolveFilePath_NoActivePointer_FallsBackToFlatLayout()
    {
        VersionedModelPathResolver resolver = new(_root);
        using ModelCatalog catalog = new(_root, vramBudgetBytes: 0, admissionTimeout: null,
            calibrationStore: null, hostFingerprint: null, pathResolver: resolver);

        // No <id>/active pointer → the resolver returns the flat path so
        // freshly-downloaded-but-not-activated installs, user-owned
        // subfolders, and the E2E test fleet all keep working without
        // anyone having to plant `active` files in their fixtures.
        string actual = ModelCatalog.ResolveFilePath(
            "some-model/model.onnx", catalog, "test");
        Assert.Equal(
            Path.GetFullPath(Path.Combine(_root, "some-model", "model.onnx")),
            actual);
    }

    [Fact]
    public void ResolveFilePath_FileUriPrefix_BypassesResolverEntirely()
    {
        VersionedModelPathResolver resolver = new(_root);
        resolver.SetActiveVersion("foo", "2026-05-29");
        using ModelCatalog catalog = new(_root, vramBudgetBytes: 0, admissionTimeout: null,
            calibrationStore: null, hostFingerprint: null, pathResolver: resolver);

        string absoluteSomewhereElse = Path.Combine(Path.GetTempPath(), "external", "model.onnx");
        string actual = ModelCatalog.ResolveFilePath(
            $"file://{absoluteSomewhereElse}", catalog, "test");
        Assert.Equal(absoluteSomewhereElse, actual);
    }

    [Fact]
    public void ResolveFilePath_AbsolutePath_NormalizedWithoutResolver()
    {
        VersionedModelPathResolver resolver = new(_root);
        resolver.SetActiveVersion("foo", "2026-05-29");
        using ModelCatalog catalog = new(_root, vramBudgetBytes: 0, admissionTimeout: null,
            calibrationStore: null, hostFingerprint: null, pathResolver: resolver);

        // An absolute USING path (e.g. user pointing at a side-loaded weight
        // file outside DATUM_MODELS) must not have id-prefix splitting
        // applied to it — the first segment is a drive root or "/", not
        // a catalog id.
        string absolute = Path.GetFullPath(Path.Combine(_root, "external", "model.onnx"));
        string actual = ModelCatalog.ResolveFilePath(absolute, catalog, "test");
        Assert.Equal(absolute, actual);
    }

    [Fact]
    public void FlatResolver_AlwaysReturnsVersionlessPath()
    {
        FlatModelPathResolver resolver = new(_root);
        Assert.Null(resolver.GetActiveVersion("foo"));
        Assert.Equal(Path.Combine(_root, "foo"), resolver.GetModelRoot("foo"));
        // SetActiveVersion + InvalidateActiveVersionCache are no-ops; the
        // resolver has no version concept and must not throw.
        resolver.SetActiveVersion("foo", "2026-05-29");
        resolver.InvalidateActiveVersionCache("foo");
        Assert.Equal(Path.Combine(_root, "foo"), resolver.GetModelRoot("foo"));
    }
}
