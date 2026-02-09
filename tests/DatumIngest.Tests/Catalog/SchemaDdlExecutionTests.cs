using DatumIngest.Catalog;

namespace DatumIngest.Tests.Catalog;

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

        Assert.Throws<InvalidOperationException>(() => catalog.Plan("CREATE SCHEMA myapp"));
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

        Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE TABLE nosuch.t (id Int32)"));
    }

    [Fact]
    public void CreateTable_InReadOnlySchema_Throws()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        // system / information_schema / datum_catalog are all read-only;
        // CREATE TABLE there must reject.
        Assert.Throws<NotSupportedException>(() =>
            catalog.Plan("CREATE TABLE system.foo (id Int32)"));
    }

    // ───────────────────── DROP SCHEMA RESTRICT / CASCADE ─────────────────────

    [Fact]
    public void DropSchema_Empty_Succeeds()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE SCHEMA myapp");

        catalog.Plan("DROP SCHEMA myapp");

        Assert.Throws<InvalidOperationException>(() =>
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
        // Schema is gone too — new tables in that schema fail.
        Assert.Throws<InvalidOperationException>(() =>
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

        // Same for system and the virtual schemas.
        Assert.Throws<InvalidOperationException>(() => catalog.Plan("DROP SCHEMA system"));
        Assert.Throws<InvalidOperationException>(() => catalog.Plan("DROP SCHEMA information_schema"));
        Assert.Throws<InvalidOperationException>(() => catalog.Plan("DROP SCHEMA datum_catalog"));
    }

    // ───────────────────── ALTER + qualified table ─────────────────────

    [Fact]
    public void AlterTable_QualifiedAddColumn_RoutesToCorrectTable()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE SCHEMA myapp");
        catalog.Plan("CREATE TABLE myapp.t (id Int32)");

        catalog.Plan("ALTER TABLE myapp.t ADD COLUMN name String");

        DatumIngest.Model.Schema schema = catalog["myapp.t"].GetSchema();
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

    // ───────────────────── SET search_path (parse-only stub for S3) ─────────────────────

    [Fact]
    public void SetSearchPath_Throws_NotImplemented_Yet()
    {
        // Parser accepts it; applier deliberately throws so tests can't
        // false-pass before the S4 resolver lands.
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        Assert.Throws<NotImplementedException>(() =>
            catalog.Plan("SET search_path = myapp, public"));
    }
}
