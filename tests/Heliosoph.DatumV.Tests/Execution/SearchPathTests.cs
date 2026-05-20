using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Execution;

/// <summary>
/// End-to-end tests for the session-scoped <c>search_path</c>: default
/// behavior, <c>SET search_path</c> mutation through the planner, and
/// the headline behavior of unqualified <c>SELECT * FROM udfs</c>
/// resolving to <c>system.udfs</c> via the path walk.
/// </summary>
public sealed class SearchPathTests : ServiceTestBase, IDisposable
{
    private readonly string _scratchDir;
    private readonly string _catalogPath;

    public SearchPathTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            $"datum-searchpath-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchDir);
        _catalogPath = Path.Combine(_scratchDir, CatalogStore.DefaultFileName);
    }

    public new void Dispose()
    {
        base.Dispose();
        try { if (Directory.Exists(_scratchDir)) Directory.Delete(_scratchDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void DefaultSearchPath_IsPublicThenSystem()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        Assert.Equal(new[] { "public", "system" }, catalog.SearchPath);
    }

    [Fact]
    public async Task UnqualifiedSelect_FromSystemTable_ResolvesViaSearchPath()
    {
        // The headline behavior: with default search_path = [public, system],
        // `SELECT * FROM udfs` resolves to `system.udfs` even though `udfs`
        // doesn't live in public.
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        StatementPlan plan = catalog.Plan("SELECT name FROM udfs");
        // Just exercise execution; udfs is empty so we just need a clean
        // run with no resolution error.
        int rowCount = 0;
        await foreach (RowBatch _ in ExecutePlanAsync(plan))
        {
            rowCount++;
        }
        // Empty registry → zero batches.
        Assert.Equal(0, rowCount);
    }

    [Fact]
    public async Task UnqualifiedSelect_FromPublicTable_StillResolves()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE TABLE users (id Int32)");

        StatementPlan plan = catalog.Plan("SELECT id FROM users");
        await foreach (RowBatch _ in ExecutePlanAsync(plan)) { }
        // No exception is the success criterion.
    }

    [Fact]
    public void SetSearchPath_OverridesDefault_AndSubsequentQueriesUseIt()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE SCHEMA myapp");

        // Put myapp first; subsequent CREATE TABLE without qualifier
        // should land in myapp (first DDL-capable schema on the path).
        catalog.Plan("SET search_path = myapp, public");
        catalog.Plan("CREATE TABLE t (id Int32)");

        Assert.True(catalog.HasTable("myapp.t"));
        Assert.False(catalog.HasTable("public.t"));
    }

    [Fact]
    public void SetSearchPath_UnqualifiedDropTable_ResolvesViaPath()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE SCHEMA myapp");
        catalog.Plan("SET search_path = myapp, public");
        catalog.Plan("CREATE TABLE t (id Int32)");

        Assert.True(catalog.HasTable("myapp.t"));

        // Unqualified DROP follows the same search_path walk.
        catalog.Plan("DROP TABLE t");

        Assert.False(catalog.HasTable("myapp.t"));
    }

    [Fact]
    public void UnqualifiedSelect_NoSchemaContainsTable_ThrowsResolutionException()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        SchemaResolutionException ex = Assert.Throws<SchemaResolutionException>(() =>
            catalog.Plan("SELECT * FROM nonexistent"));

        // Diagnostic message names every schema attempted.
        Assert.Contains("public", ex.Message);
        Assert.Contains("system", ex.Message);
    }

    [Fact]
    public void QualifiedSelect_KnownSchema_BypassesSearchPath()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        // Even with default search_path that includes system, an explicit
        // qualifier goes straight to the named schema.
        StatementPlan plan = catalog.Plan("SELECT name FROM system.udfs");
        Assert.NotNull(plan);
    }

    [Fact]
    public void QualifiedSelect_UnknownSchema_ReportsSchemaMissing()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        SchemaResolutionException ex = Assert.Throws<SchemaResolutionException>(() =>
            catalog.Plan("SELECT * FROM nosuch.t"));

        // Schema-missing has a different message than table-missing-in-schema.
        Assert.Contains("Schema 'nosuch' does not exist", ex.Message);
    }
}
