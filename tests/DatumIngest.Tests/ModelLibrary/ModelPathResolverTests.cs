using DatumIngest.ModelLibrary;
using DatumIngest.Models;

namespace DatumIngest.Tests.ModelLibrary;

/// <summary>
/// Tests the path-resolver substrate: <see cref="VersionedModelPathResolver"/>
/// (production) and <see cref="FlatModelPathResolver"/> (legacy fallback),
/// plus <see cref="ModelCatalog.ResolveFilePath"/> — the SQL-facing entry
/// point that returns supplied paths verbatim (no implicit version-segment
/// injection).
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
    public void VersionedResolver_NoLookup_FallsBackToVersionlessFolder()
    {
        // No catalog active-version lookup wired (Null instance) → resolver
        // has no way to know "what version is active for foo" so it falls
        // back to the version-less folder shape.
        VersionedModelPathResolver resolver = new(_root);
        Assert.Equal(Path.Combine(_root, "foo"), resolver.GetModelRoot("foo"));
        Assert.Null(resolver.GetActiveVersion("foo"));
    }

    [Fact]
    public void VersionedResolver_LookupReturnsActive_InjectsVersionSegment()
    {
        StubLookup lookup = new();
        lookup.Set("foo", "2026-05-29");
        VersionedModelPathResolver resolver = new(_root, lookup);

        Assert.Equal(
            Path.Combine(_root, "foo", "2026-05-29"),
            resolver.GetModelRoot("foo"));
        Assert.Equal("2026-05-29", resolver.GetActiveVersion("foo"));
    }

    [Fact]
    public void VersionedResolver_ExplicitVersionPin_OverridesLookup()
    {
        StubLookup lookup = new();
        lookup.Set("foo", "2026-05-29");
        VersionedModelPathResolver resolver = new(_root, lookup);

        Assert.Equal(
            Path.Combine(_root, "foo", "2026-04-15"),
            resolver.GetModelRoot("foo", versionPin: "2026-04-15"));
    }

    [Fact]
    public void VersionedResolver_CurrentVersionPin_OverridesLookup()
    {
        // ModelInstallContext.CurrentVersionPin AsyncLocal beats the lookup —
        // this is how installs/rehydrates pin paths to the version they're
        // staging before the catalog row exists.
        StubLookup lookup = new();
        lookup.Set("foo", "2026-05-29");
        VersionedModelPathResolver resolver = new(_root, lookup);

        string? previous = ModelInstallContext.CurrentVersionPin;
        ModelInstallContext.CurrentVersionPin = "2026-04-15";
        try
        {
            Assert.Equal("2026-04-15", resolver.GetActiveVersion("foo"));
            Assert.Equal(
                Path.Combine(_root, "foo", "2026-04-15"),
                resolver.GetModelRoot("foo"));
        }
        finally
        {
            ModelInstallContext.CurrentVersionPin = previous;
        }
    }

    [Fact]
    public void VersionedResolver_LookupUpdatesObservedImmediately()
    {
        // No persistent cache layer — the lookup is the source of truth, so
        // an updated answer flows through on the next read.
        StubLookup lookup = new();
        VersionedModelPathResolver resolver = new(_root, lookup);

        Assert.Null(resolver.GetActiveVersion("foo"));

        lookup.Set("foo", "2026-05-29");
        Assert.Equal("2026-05-29", resolver.GetActiveVersion("foo"));

        lookup.Clear("foo");
        Assert.Null(resolver.GetActiveVersion("foo"));
    }

    [Fact]
    public void VersionedResolver_ResolveIdPrefixedPath_SplitsAtFirstSlash()
    {
        StubLookup lookup = new();
        lookup.Set("all-minilm-l6-v2", "2026-05-29");
        VersionedModelPathResolver resolver = new(_root, lookup);

        // Internal C# callers (BuiltinModels loaders, ResidencyManager,
        // CalibrationCoordinator) still use the id-prefixed shorthand and
        // get the version segment injected for them via the lookup.
        Assert.Equal(
            Path.Combine(_root, "all-minilm-l6-v2", "2026-05-29", "model.onnx"),
            resolver.ResolveIdPrefixedPath("all-minilm-l6-v2/model.onnx"));
    }

    [Fact]
    public void VersionedResolver_ResolveIdPrefixedPath_PreservesFlatFallbackForNonCatalogPaths()
    {
        VersionedModelPathResolver resolver = new(_root);
        // No active version for "my-experiments" — user's own subfolder.
        // Falls back to <root>/my-experiments/x.onnx.
        Assert.Equal(
            Path.Combine(_root, "my-experiments", "x.onnx"),
            resolver.ResolveIdPrefixedPath("my-experiments/x.onnx"));
    }

    [Fact]
    public void VersionedResolver_ResolveIdPrefixedPath_NoSlashLandsDirectlyUnderRoot()
    {
        VersionedModelPathResolver resolver = new(_root);
        Assert.Equal(
            Path.Combine(_root, "Phi-3-mini-Q4_K_M.gguf"),
            resolver.ResolveIdPrefixedPath("Phi-3-mini-Q4_K_M.gguf"));
    }

    [Fact]
    public void ResolveFilePath_RelativeUsingPath_PassesThroughVerbatim()
    {
        // ResolveFilePath no longer injects an implicit version segment.
        // The SQL author writes the literal version-explicit path; the
        // resolver hands it back joined with ModelDirectory. Fixes the
        // segment-doubling bug that bit `inference.onnx_inspect` when
        // users passed paths that already contained a version segment.
        StubLookup lookup = new();
        lookup.Set("all-minilm-l6-v2", "2026-05-29");
        VersionedModelPathResolver resolver = new(_root, lookup);
        using ModelCatalog catalog = new(_root, vramBudgetBytes: 0, admissionTimeout: null,
            calibrationStore: null, hostFingerprint: null, pathResolver: resolver);

        // Literal versioned path — author wrote 2026-05-29; resolver doesn't
        // inject it a second time.
        string actual = ModelCatalog.ResolveFilePath(
            "all-minilm-l6-v2/2026-05-29/model.onnx", catalog, "test");
        Assert.Equal(
            Path.GetFullPath(Path.Combine(_root, "all-minilm-l6-v2", "2026-05-29", "model.onnx")),
            actual);
    }

    [Fact]
    public void ResolveFilePath_VersionlessPath_AlsoPassesThroughVerbatim()
    {
        // Even when the lookup knows "foo's active is 2026-05-29",
        // ResolveFilePath does NOT inject it. What the author wrote is what
        // gets loaded — predictable for `inference.onnx_inspect` callers
        // and for users debugging USING paths.
        StubLookup lookup = new();
        lookup.Set("foo", "2026-05-29");
        VersionedModelPathResolver resolver = new(_root, lookup);
        using ModelCatalog catalog = new(_root, vramBudgetBytes: 0, admissionTimeout: null,
            calibrationStore: null, hostFingerprint: null, pathResolver: resolver);

        Assert.Equal(
            Path.GetFullPath(Path.Combine(_root, "foo", "model.onnx")),
            ModelCatalog.ResolveFilePath("foo/model.onnx", catalog, "test"));
    }

    [Fact]
    public void ResolveFilePath_FileUriPrefix_BypassesResolverEntirely()
    {
        VersionedModelPathResolver resolver = new(_root);
        using ModelCatalog catalog = new(_root, vramBudgetBytes: 0, admissionTimeout: null,
            calibrationStore: null, hostFingerprint: null, pathResolver: resolver);

        string absoluteSomewhereElse = Path.Combine(Path.GetTempPath(), "external", "model.onnx");
        Assert.Equal(absoluteSomewhereElse,
            ModelCatalog.ResolveFilePath($"file://{absoluteSomewhereElse}", catalog, "test"));
    }

    [Fact]
    public void ResolveFilePath_AbsolutePath_NormalizedWithoutResolver()
    {
        VersionedModelPathResolver resolver = new(_root);
        using ModelCatalog catalog = new(_root, vramBudgetBytes: 0, admissionTimeout: null,
            calibrationStore: null, hostFingerprint: null, pathResolver: resolver);

        string absolute = Path.GetFullPath(Path.Combine(_root, "external", "model.onnx"));
        Assert.Equal(absolute, ModelCatalog.ResolveFilePath(absolute, catalog, "test"));
    }

    [Fact]
    public void FlatResolver_AlwaysReturnsVersionlessPath()
    {
        FlatModelPathResolver resolver = new(_root);
        Assert.Null(resolver.GetActiveVersion("foo"));
        Assert.Equal(Path.Combine(_root, "foo"), resolver.GetModelRoot("foo"));
        Assert.False(resolver.IsVersionOnDisk("foo", "2026-05-29"));
    }

    /// <summary>
    /// In-memory <see cref="ICatalogActiveVersionLookup"/> for tests —
    /// replaces the old filesystem-pointer approach where tests had to
    /// plant <c>&lt;id&gt;/active</c> text files in their scratch dirs.
    /// </summary>
    private sealed class StubLookup : ICatalogActiveVersionLookup
    {
        private readonly Dictionary<string, string> _active = new(StringComparer.Ordinal);

        public string? GetActiveVersion(string catalogId)
            => _active.TryGetValue(catalogId, out string? v) ? v : null;

        public void Set(string catalogId, string version) => _active[catalogId] = version;
        public void Clear(string catalogId) => _active.Remove(catalogId);
    }
}
