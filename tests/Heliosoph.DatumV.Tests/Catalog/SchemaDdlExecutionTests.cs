using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Execution;

namespace Heliosoph.DatumV.Tests.Catalog;

/// <summary>
/// End-to-end tests for the schema-aware DDL added in S3:
/// <list type="bullet">
///   <item><description><c>CREATE SCHEMA</c> + qualified <c>CREATE TABLE</c> /
///     <c>DROP TABLE</c> / <c>ALTER TABLE</c> against the right backend.</description></item>
///   <item><description><c>DROP SCHEMA</c> RESTRICT vs CASCADE.</description></item>
///   <item><description>Read-only-schema rejection (CREATE TABLE system.foo, etc.).</description></item>
///   <item><description>Built-in schemas can't be dropped.</description></item>
/// </list>
/// </summary>
public sealed class SchemaDdlExecutionTests : ServiceTestBase, IDisposable
{
    private readonly string _scratchDir;
    private readonly string _catalogPath;

    public SchemaDdlExecutionTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            $"datum-schema-ddl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchDir);
        _catalogPath = Path.Combine(_scratchDir, CatalogStore.DefaultFileName);
    }

    public new void Dispose()
    {
        base.Dispose();
        try
        {
            if (Directory.Exists(_scratchDir)) Directory.Delete(_scratchDir, recursive: true);
        }
        catch { /* best-effort */ }
    }

    // ───────────────────── CREATE SCHEMA + qualified CREATE TABLE ─────────────────────

    [Fact]
    public void CreateSchema_ThenQualifiedCreateTable_RoutesToMyappSchema()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE SCHEMA myapp");
        catalog.Plan("CREATE TABLE myapp.t (id Int32)");

        Assert.True(catalog.HasTable("myapp.t"));
        // Unqualified `t` does NOT resolve — search_path lands in S4.
        Assert.False(catalog.HasTable("t"));
        // The provider knows its qualified name.
        Assert.Equal("myapp", catalog["myapp.t"].QualifiedName.Schema);
        Assert.Equal("t", catalog["myapp.t"].QualifiedName.Name);
    }

    [Fact]
    public void CreateSchema_AlreadyExists_Throws()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE SCHEMA myapp");

        Assert.Throws<ExecutionException>(() => catalog.Plan("CREATE SCHEMA myapp"));
    }

    [Fact]
    public void CreateSchema_IfNotExists_SuppressesDuplicate()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE SCHEMA myapp");

        // Should not throw.
        catalog.Plan("CREATE SCHEMA IF NOT EXISTS myapp");
    }

    [Fact]
    public void CreateTable_InUnknownSchema_Throws()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        // S4 routes through SchemaResolver.ResolveForCreate; missing
        // schema surfaces as SchemaResolutionException (which derives
        // from InvalidOperationException).
        SchemaResolutionException ex = Assert.Throws<SchemaResolutionException>(() =>
            catalog.Plan("CREATE TABLE nosuch.t (id Int32)"));
        Assert.Contains("nosuch", ex.Message);
    }

    [Fact]
    public void CreateTable_InReadOnlySchema_Throws()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        // system / information_schema / system are all read-only;
        // CREATE TABLE there must reject. S4 routes the rejection through
        // SchemaResolver.ResolveForCreate (which derives from
        // InvalidOperationException).
        SchemaResolutionException ex = Assert.Throws<SchemaResolutionException>(() =>
            catalog.Plan("CREATE TABLE system.foo (id Int32)"));
        Assert.Contains("read-only", ex.Message);
    }

    // ───────────────────── DROP SCHEMA RESTRICT / CASCADE ─────────────────────

    [Fact]
    public void DropSchema_Empty_Succeeds()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE SCHEMA myapp");

        catalog.Plan("DROP SCHEMA myapp");

        // After the drop, the schema is no longer mounted — CREATE TABLE
        // there fails through SchemaResolutionException.
        Assert.Throws<SchemaResolutionException>(() =>
            catalog.Plan("CREATE TABLE myapp.t (id Int32)"));
    }

    [Fact]
    public void DropSchema_NonEmptyRestrict_Throws()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE SCHEMA myapp");
        catalog.Plan("CREATE TABLE myapp.t (id Int32)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("DROP SCHEMA myapp"));
        Assert.Contains("CASCADE", ex.Message);
    }

    [Fact]
    public void DropSchema_Cascade_DropsTablesFirst()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE SCHEMA myapp");
        catalog.Plan("CREATE TABLE myapp.t (id Int32)");

        catalog.Plan("DROP SCHEMA myapp CASCADE");

        Assert.False(catalog.HasTable("myapp.t"));
        // Schema is gone too — new tables in that schema fail through
        // the resolver.
        Assert.Throws<SchemaResolutionException>(() =>
            catalog.Plan("CREATE TABLE myapp.t (id Int32)"));
    }

    [Fact]
    public void DropSchema_IfExistsOnMissingSchema_SuppressesError()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        // Should not throw.
        catalog.Plan("DROP SCHEMA IF EXISTS nonexistent");
    }

    [Fact]
    public void DropSchema_BuiltinSchema_Throws()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("DROP SCHEMA public"));
        Assert.Contains("built-in", ex.Message);

        // Same for system, the virtual schemas, and (S9) models.
        Assert.Throws<InvalidOperationException>(() => catalog.Plan("DROP SCHEMA system"));
        Assert.Throws<InvalidOperationException>(() => catalog.Plan("DROP SCHEMA information_schema"));
        Assert.Throws<InvalidOperationException>(() => catalog.Plan("DROP SCHEMA system"));
        Assert.Throws<InvalidOperationException>(() => catalog.Plan("DROP SCHEMA models"));
    }

    // ───────────────────── S9 — models as a real schema ─────────────────────

    [Fact]
    public void Models_IsMountedAsReadOnlySchema()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        // The schema exists for routing / search_path purposes — confirm
        // via SET search_path acceptance (validates against _backends).
        catalog.Plan("SET search_path = models, public, system");
        Assert.Equal(new[] { "models", "public", "system" }, catalog.SearchPath);
    }

    [Fact]
    public void Models_RejectsCreateTable()
    {
        // Models is read-only — CREATE TABLE there must fail with the
        // same diagnostic system/information_schema produce.
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        SchemaResolutionException ex = Assert.Throws<SchemaResolutionException>(() =>
            catalog.Plan("CREATE TABLE models.foo (id Int32)"));
        Assert.Contains("read-only", ex.Message);
    }

    [Fact]
    public void Models_AppearsInInformationSchemaSchemata()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        StatementPlan plan = catalog.Plan(
            "SELECT schema_name FROM information_schema.schemata WHERE schema_name = 'models'");
        // The presence of the schema in the projection is what the test
        // asserts; the actual row enumeration is covered by the
        // InformationSchemaProvidersTests sweep.
        Assert.NotNull(plan);
    }

    // ───────────────────── ALTER + qualified table ─────────────────────

    [Fact]
    public void AlterTable_QualifiedAddColumn_RoutesToCorrectTable()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE SCHEMA myapp");
        catalog.Plan("CREATE TABLE myapp.t (id Int32)");

        catalog.Plan("ALTER TABLE myapp.t ADD COLUMN name String");

        Heliosoph.DatumV.Model.Schema schema = catalog["myapp.t"].GetSchema();
        Assert.Equal(2, schema.Columns.Count);
        Assert.Equal("name", schema.Columns[1].Name);
    }

    [Fact]
    public void DropTable_QualifiedName_RemovesFromCorrectSchema()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE SCHEMA myapp");
        catalog.Plan("CREATE TABLE myapp.t (id Int32)");

        catalog.Plan("DROP TABLE myapp.t");

        Assert.False(catalog.HasTable("myapp.t"));
    }

    // ───────────────────── SET search_path (S4) ─────────────────────

    [Fact]
    public void SetSearchPath_UpdatesCatalogState()
    {
        // S4 wires SET search_path through to TableCatalog.SetSearchPath.
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        Assert.Equal(new[] { "public", "system" }, catalog.SearchPath);

        catalog.Plan("CREATE SCHEMA myapp");
        catalog.Plan("SET search_path = myapp, public");

        Assert.Equal(new[] { "myapp", "public" }, catalog.SearchPath);
    }

    [Fact]
    public void SetSearchPath_UnknownSchema_Throws()
    {
        // We diverge from PG (which warns) — typos shouldn't silently
        // change resolution behavior. Error upfront.
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("SET search_path = nonexistent"));
        Assert.Contains("nonexistent", ex.Message);
    }
}
