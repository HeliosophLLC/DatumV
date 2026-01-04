using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.DatumFile.V2;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// PR10a tests for SQL <c>CREATE TABLE</c> / <c>CREATE TEMP TABLE</c> /
/// <c>DROP TABLE</c>. Cover: TEMP vs persistent dispatch, schema
/// resolution from column type names, <c>IF NOT EXISTS</c> /
/// <c>IF EXISTS</c>, the <c>AT 'path'</c> clause + production flag,
/// catalog-file persistence (v2), and rehydration on catalog reopen.
/// PRIMARY KEY syntax is parsed but not enforced yet (PR10f).
/// </summary>
public sealed class CreateTableTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_pr10a_{Guid.NewGuid():N}");
    private string CatalogPath => Path.Combine(_tempDir, ".datum-catalog.json");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
        }
        return Task.CompletedTask;
    }

    // ──────────────────── CREATE TEMP TABLE ────────────────────

    [Fact]
    public void CreateTempTable_WithoutCatalogFile_RegistersInMemoryProvider()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool); // no catalog path

        catalog.Plan("CREATE TEMP TABLE staging (id Int32, name String)");

        Assert.True(catalog.HasTable("staging"));
        ITableProvider provider = catalog["staging"];
        Assert.IsType<InMemoryTableProvider>(provider);
        Assert.Equal(0, provider.GetRowCount());

        Schema schema = provider.GetSchema();
        Assert.Equal(["id", "name"], schema.Columns.Select(c => c.Name));
        Assert.Equal(DataKind.Int32, schema.Columns[0].Kind);
        Assert.Equal(DataKind.String, schema.Columns[1].Kind);
    }

    [Fact]
    public void CreateTempTable_RespectsNullability()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);

        catalog.Plan("CREATE TEMP TABLE t (a Int32 NOT NULL, b String)");

        Schema schema = catalog["t"].GetSchema();
        Assert.False(schema.Columns[0].Nullable);
        Assert.True(schema.Columns[1].Nullable);
    }

    [Fact]
    public void CreateTempTable_AlreadyExists_Throws()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (a Int32)");

        Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE TEMP TABLE t (a Int32)"));
    }

    [Fact]
    public void CreateTempTable_IfNotExists_NoOpOnSecondCreate()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (a Int32)");
        // Second create with IF NOT EXISTS is a no-op, no exception.
        catalog.Plan("CREATE TEMP TABLE IF NOT EXISTS t (a Int64)");

        // Original schema preserved.
        Assert.Equal(DataKind.Int32, catalog["t"].GetSchema().Columns[0].Kind);
    }

    // ──────────────────── CREATE TABLE (persistent) ────────────────────

    [Fact]
    public void CreatePersistentTable_WithoutCatalogFile_Throws()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool); // no catalog path

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE TABLE t (a Int32)"));
        Assert.Contains(".datum-catalog.json", ex.Message);
    }

    [Fact]
    public void CreatePersistentTable_WithCatalogFile_MaterialisesEmptyDatumFile()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool, CatalogPath);

        catalog.Plan("CREATE TABLE users (id Int32, name String)");

        // Default storage location: {catalog_dir}/{name}.datum
        string expectedPath = Path.Combine(_tempDir, "users.datum");
        Assert.True(File.Exists(expectedPath));

        ITableProvider provider = catalog["users"];
        Assert.IsType<DatumFileTableProviderV2>(provider);
        Assert.Equal(0, provider.GetRowCount());
    }

    [Fact]
    public void CreatePersistentTable_PersistsToCatalogJson()
    {
        Pool pool = new(new PoolBacking());
        using (TableCatalog catalog = new(pool, CatalogPath))
        {
            catalog.Plan("CREATE TABLE users (id Int32, name String)");
        }

        Assert.True(File.Exists(CatalogPath));
        string json = File.ReadAllText(CatalogPath);
        Assert.Contains("\"version\": 2", json);
        Assert.Contains("\"tables\":", json);
        Assert.Contains("\"users\"", json);
    }

    [Fact]
    public void CreatePersistentTable_ReopeningCatalog_RehydratesTable()
    {
        Pool pool = new(new PoolBacking());
        using (TableCatalog firstCatalog = new(pool, CatalogPath))
        {
            firstCatalog.Plan("CREATE TABLE users (id Int32, name String)");
        }

        using TableCatalog reopened = new(pool, CatalogPath);
        Assert.True(reopened.HasTable("users"));
        Assert.IsType<DatumFileTableProviderV2>(reopened["users"]);
        Assert.Equal(0, reopened["users"].GetRowCount());
    }

    [Fact]
    public void CreatePersistentTable_ReopeningCatalog_StaleEntryWithMissingFile_IsSkipped()
    {
        Pool pool = new(new PoolBacking());
        using (TableCatalog firstCatalog = new(pool, CatalogPath))
        {
            firstCatalog.Plan("CREATE TABLE users (id Int32)");
        }

        // Simulate the .datum file disappearing out from under the catalog.
        File.Delete(Path.Combine(_tempDir, "users.datum"));

        using TableCatalog reopened = new(pool, CatalogPath);
        // Stale entry silently dropped; catalog still opens cleanly.
        Assert.False(reopened.HasTable("users"));
    }

    // ──────────────────── AT clause ────────────────────

    [Fact]
    public void CreatePersistentTable_AtClause_RequiresAllowExplicitTablePaths()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool, CatalogPath); // production default: AllowExplicitTablePaths=false

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan($"CREATE TABLE t (a Int32) AT '{Path.Combine(_tempDir, "elsewhere.datum")}'"));
        Assert.Contains("AllowExplicitTablePaths", ex.Message);
    }

    [Fact]
    public void CreatePersistentTable_AtClause_AllowedInTestMode()
    {
        Pool pool = new(new PoolBacking());
        string explicitPath = Path.Combine(_tempDir, "elsewhere.datum");
        using TableCatalog catalog = new(pool, CatalogPath, allowExplicitTablePaths: true);

        catalog.Plan($"CREATE TABLE t (a Int32) AT '{explicitPath}'");

        Assert.True(File.Exists(explicitPath));
        Assert.True(catalog.HasTable("t"));
    }

    // ──────────────────── DROP TABLE ────────────────────

    [Fact]
    public void DropTempTable_RemovesFromCatalog()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (a Int32)");
        Assert.True(catalog.HasTable("t"));

        catalog.Plan("DROP TABLE t");

        Assert.False(catalog.HasTable("t"));
    }

    [Fact]
    public void DropPersistentTable_RemovesCatalogEntryAndDeletesFile()
    {
        Pool pool = new(new PoolBacking());
        string datumPath = Path.Combine(_tempDir, "users.datum");
        using TableCatalog catalog = new(pool, CatalogPath);
        catalog.Plan("CREATE TABLE users (id Int32, name String)");
        Assert.True(File.Exists(datumPath));

        catalog.Plan("DROP TABLE users");

        Assert.False(catalog.HasTable("users"));
        Assert.False(File.Exists(datumPath));

        // Catalog json should no longer reference the table.
        Assert.DoesNotContain("\"users\"", File.ReadAllText(CatalogPath));
    }

    [Fact]
    public void DropTable_WhenMissing_Throws()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        Assert.Throws<InvalidOperationException>(() => catalog.Plan("DROP TABLE nope"));
    }

    [Fact]
    public void DropTable_IfExists_NoOpWhenMissing()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        // Should not throw.
        catalog.Plan("DROP TABLE IF EXISTS nope");
    }

    // ──────────────────── Type spec coverage ────────────────────

    [Fact]
    public void CreateTempTable_FullTypeSpec_ResolvesEachKind()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);

        catalog.Plan(
            "CREATE TEMP TABLE wide (" +
            "i8 Int8, i16 Int16, i32 Int32, i64 Int64, " +
            "u8 UInt8, u16 UInt16, u32 UInt32, u64 UInt64, " +
            "f32 Float32, f64 Float64, " +
            "s String, u Uuid, " +
            "d Date, t Time, dt DateTime, dur Duration" +
            ")");

        Schema schema = catalog["wide"].GetSchema();
        Assert.Equal(DataKind.Int8, schema.Columns[0].Kind);
        Assert.Equal(DataKind.Int16, schema.Columns[1].Kind);
        Assert.Equal(DataKind.Int32, schema.Columns[2].Kind);
        Assert.Equal(DataKind.Int64, schema.Columns[3].Kind);
        Assert.Equal(DataKind.UInt8, schema.Columns[4].Kind);
        Assert.Equal(DataKind.Float64, schema.Columns[9].Kind);
        Assert.Equal(DataKind.String, schema.Columns[10].Kind);
        Assert.Equal(DataKind.Uuid, schema.Columns[11].Kind);
        Assert.Equal(DataKind.DateTime, schema.Columns[14].Kind);
    }

    [Fact]
    public void CreateTempTable_UnknownType_Throws()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE TEMP TABLE t (a NotARealType)"));
        Assert.Contains("Unknown column type", ex.Message);
    }
}
