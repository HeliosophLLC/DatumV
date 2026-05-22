using Heliosoph.DatumV.Catalog;

namespace Heliosoph.DatumV.Tests.Catalog;

/// <summary>
/// Unit tests for <see cref="SchemaResolver"/> covering the four cases:
/// explicit-schema hits/misses, unqualified search-path walks,
/// CREATE-table writability checks, and the diagnostics carried on
/// <see cref="SchemaResolutionException"/>.
/// </summary>
public sealed class SchemaResolverTests : ServiceTestBase, IDisposable
{
    private readonly string _scratchDir;
    private readonly string _catalogPath;

    public SchemaResolverTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            $"datum-resolver-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchDir);
        _catalogPath = Path.Combine(_scratchDir, CatalogStore.DefaultFileName);
    }

    public new void Dispose()
    {
        base.Dispose();
        try { if (Directory.Exists(_scratchDir)) Directory.Delete(_scratchDir, recursive: true); }
        catch { /* best-effort */ }
    }

    private TableCatalog OpenCatalog() => CreateCatalog(_catalogPath);

    // ───────────────────── Explicit-schema resolution ─────────────────────

    [Fact]
    public void Resolve_ExplicitSchema_Hit_ReturnsQualifiedName()
    {
        using TableCatalog catalog = OpenCatalog();
        SchemaResolver resolver = new(catalog, new[] { "public", "system" });

        QualifiedName resolved = resolver.Resolve("system", "udfs");

        Assert.Equal("system", resolved.Schema);
        Assert.Equal("udfs", resolved.Name);
    }

    [Fact]
    public void Resolve_ExplicitSchema_TableMissing_ThrowsWithDistinctMessage()
    {
        using TableCatalog catalog = OpenCatalog();
        SchemaResolver resolver = new(catalog, new[] { "public", "system" });

        SchemaResolutionException ex = Assert.Throws<SchemaResolutionException>(() =>
            resolver.Resolve("system", "nonexistent"));

        Assert.Contains("does not exist in schema 'system'", ex.Message);
        Assert.Equal("system", ex.ExplicitSchema);
        Assert.Equal("nonexistent", ex.TableName);
    }

    [Fact]
    public void Resolve_ExplicitSchema_SchemaMissing_ThrowsWithDistinctMessage()
    {
        using TableCatalog catalog = OpenCatalog();
        SchemaResolver resolver = new(catalog, new[] { "public", "system" });

        SchemaResolutionException ex = Assert.Throws<SchemaResolutionException>(() =>
            resolver.Resolve("nosuch", "anything"));

        // Schema-not-found and table-not-found in schema are distinct messages.
        Assert.Contains("Schema 'nosuch' does not exist", ex.Message);
    }

    // ───────────────────── Unqualified search_path walk ─────────────────────

    [Fact]
    public void Resolve_Unqualified_WalksSearchPath_FirstHitWins()
    {
        // Default search_path is [public, system]. `udfs` lives in system,
        // so unqualified resolution finds it on the second hop.
        using TableCatalog catalog = OpenCatalog();
        SchemaResolver resolver = new(catalog, catalog.SearchPath);

        QualifiedName resolved = resolver.Resolve(explicitSchema: null, "udfs");

        Assert.Equal("system", resolved.Schema);
        Assert.Equal("udfs", resolved.Name);
    }

    [Fact]
    public void Resolve_Unqualified_HonoursPathOrder()
    {
        // Reverse the path so system is consulted before public.
        using TableCatalog catalog = OpenCatalog();
        SchemaResolver resolver = new(catalog, new[] { "system", "public" });

        QualifiedName resolved = resolver.Resolve(explicitSchema: null, "procedures");
        Assert.Equal("system", resolved.Schema);
    }

    [Fact]
    public void Resolve_Unqualified_NoMatch_IncludesSearchPathInError()
    {
        using TableCatalog catalog = OpenCatalog();
        SchemaResolver resolver = new(catalog, new[] { "public", "system" });

        SchemaResolutionException ex = Assert.Throws<SchemaResolutionException>(() =>
            resolver.Resolve(explicitSchema: null, "nonexistent"));

        Assert.Contains("[public, system]", ex.Message);
        Assert.Equal(new[] { "public", "system" }, ex.SearchPath);
    }

    // ───────────────────── TryResolve (IF EXISTS path) ─────────────────────

    [Fact]
    public void TryResolve_NotFound_ReturnsFalse_NoThrow()
    {
        using TableCatalog catalog = OpenCatalog();
        SchemaResolver resolver = new(catalog, new[] { "public", "system" });

        bool found = resolver.TryResolve(null, "nonexistent", out QualifiedName resolved);

        Assert.False(found);
        // Best-guess: first search_path entry.
        Assert.Equal("public", resolved.Schema);
        Assert.Equal("nonexistent", resolved.Name);
    }

    [Fact]
    public void TryResolve_Found_ReturnsTrue_WithCorrectSchema()
    {
        using TableCatalog catalog = OpenCatalog();
        SchemaResolver resolver = new(catalog, catalog.SearchPath);

        bool found = resolver.TryResolve(null, "udfs", out QualifiedName resolved);

        Assert.True(found);
        Assert.Equal("system", resolved.Schema);
    }

    // ───────────────────── ResolveForCreate ─────────────────────

    [Fact]
    public void ResolveForCreate_Unqualified_PicksFirstWritableSchema()
    {
        using TableCatalog catalog = OpenCatalog();
        SchemaResolver resolver = new(catalog, new[] { "public", "system" });

        QualifiedName resolved = resolver.ResolveForCreate(null, "newtbl");

        // public is the first writable; system would be skipped (read-only).
        Assert.Equal("public", resolved.Schema);
    }

    [Fact]
    public void ResolveForCreate_Unqualified_SkipsReadOnlySchemas()
    {
        // Put system first; ResolveForCreate must skip it because system
        // is read-only.
        using TableCatalog catalog = OpenCatalog();
        SchemaResolver resolver = new(catalog, new[] { "system", "public" });

        QualifiedName resolved = resolver.ResolveForCreate(null, "newtbl");

        Assert.Equal("public", resolved.Schema);
    }

    [Fact]
    public void ResolveForCreate_Unqualified_NoWritableSchema_Throws()
    {
        using TableCatalog catalog = OpenCatalog();
        // Path contains only read-only schemas.
        SchemaResolver resolver = new(catalog, new[] { "system", "information_schema", "system" });

        SchemaResolutionException ex = Assert.Throws<SchemaResolutionException>(() =>
            resolver.ResolveForCreate(null, "newtbl"));

        Assert.Contains("No DDL-capable schema", ex.Message);
    }

    [Fact]
    public void ResolveForCreate_Explicit_ReadOnlySchema_Throws()
    {
        using TableCatalog catalog = OpenCatalog();
        SchemaResolver resolver = new(catalog, catalog.SearchPath);

        SchemaResolutionException ex = Assert.Throws<SchemaResolutionException>(() =>
            resolver.ResolveForCreate("system", "newtbl"));

        Assert.Contains("read-only", ex.Message);
    }

    [Fact]
    public void ResolveForCreate_Explicit_UnknownSchema_Throws()
    {
        using TableCatalog catalog = OpenCatalog();
        SchemaResolver resolver = new(catalog, catalog.SearchPath);

        SchemaResolutionException ex = Assert.Throws<SchemaResolutionException>(() =>
            resolver.ResolveForCreate("nosuch", "newtbl"));

        Assert.Contains("does not exist", ex.Message);
    }

    // ───────────────────── Snapshot semantics ─────────────────────

    [Fact]
    public void SearchPath_Snapshot_IsImmutableAcrossSetMutations()
    {
        // Resolver captures the path at construction. A subsequent SET
        // updates catalog state but doesn't bleed into the captured
        // snapshot — in-flight queries see a stable resolution policy.
        using TableCatalog catalog = OpenCatalog();
        SchemaResolver resolverA = new(catalog, catalog.SearchPath);

        catalog.Plan("CREATE SCHEMA myapp");
        catalog.Plan("SET search_path = myapp, public");

        // Catalog moved on, but resolverA still sees the original path.
        Assert.Equal(new[] { "public", "system" }, resolverA.SearchPath);

        // A fresh resolver picks up the new path.
        SchemaResolver resolverB = new(catalog, catalog.SearchPath);
        Assert.Equal(new[] { "myapp", "public" }, resolverB.SearchPath);
    }
}
