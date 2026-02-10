using DatumIngest.Catalog;
using DatumIngest.Indexing;
using DatumIngest.Model;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// S8: end-to-end execution of <c>schema.table</c>-qualified
/// INSERT / UPDATE / DELETE plus index DDL (CREATE INDEX) and
/// maintenance commands (REINDEX, ANALYZE). Mirrors the
/// SchemaDdlExecutionTests coverage but for the DML / maintenance
/// surface — the parser, planner, and executors all have to agree on
/// the (schema, table) pair end-to-end.
/// </summary>
public sealed class QualifiedDmlExecutionTests : ServiceTestBase, IDisposable
{
    private readonly string _scratchDir;
    private readonly string _catalogPath;

    public QualifiedDmlExecutionTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            $"datum-qdml-{Guid.NewGuid():N}");
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
    public async Task Insert_SchemaQualified_LandsInCorrectSchema()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE SCHEMA myapp");
        catalog.Plan("CREATE TABLE myapp.users (id Int32, name String)");

        catalog.Plan("INSERT INTO myapp.users VALUES (1, 'alice'), (2, 'bob')");

        Assert.Equal(2, await CountRows(catalog["myapp.users"]));
    }

    [Fact]
    public async Task Update_SchemaQualified_MutatesCorrectSchema()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE SCHEMA myapp");
        catalog.Plan("CREATE TABLE myapp.users (id Int32, name String)");
        catalog.Plan("INSERT INTO myapp.users VALUES (1, 'alice'), (2, 'bob')");

        catalog.Plan("UPDATE myapp.users SET name = 'zed' WHERE id = 1");

        List<(int, string)> rows = await ScanAsTuples(catalog["myapp.users"]);
        Assert.Equal(new[] { (1, "zed"), (2, "bob") }, rows);
    }

    [Fact]
    public async Task Delete_SchemaQualified_MutatesCorrectSchema()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE SCHEMA myapp");
        catalog.Plan("CREATE TABLE myapp.users (id Int32, name String)");
        catalog.Plan("INSERT INTO myapp.users VALUES (1, 'alice'), (2, 'bob')");

        catalog.Plan("DELETE FROM myapp.users WHERE id = 1");

        List<(int, string)> rows = await ScanAsTuples(catalog["myapp.users"]);
        Assert.Equal(new[] { (2, "bob") }, rows);
    }

    [Fact]
    public void Insert_UnknownSchema_ThrowsSchemaResolution()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        SchemaResolutionException ex = Assert.Throws<SchemaResolutionException>(() =>
            catalog.Plan("INSERT INTO nosuch.users VALUES (1)"));
        Assert.Contains("nosuch", ex.Message);
    }

    [Fact]
    public void CreateIndex_SchemaQualifiedTable_RoutesToCorrectTable()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE SCHEMA myapp");
        catalog.Plan("CREATE TABLE myapp.users (id Int32, email String)");
        catalog.Plan("INSERT INTO myapp.users VALUES (1, 'a@x'), (2, 'b@y')");

        catalog.Plan("CREATE INDEX idx_email ON myapp.users (email)");

        // After CREATE INDEX, the table's source index should be Valid.
        Assert.Equal(IndexValidity.Valid, catalog["myapp.users"].GetIndexValidity());
    }

    [Fact]
    public void Reindex_SchemaQualifiedTable_RebuildsCorrectTable()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE SCHEMA myapp");
        catalog.Plan("CREATE TABLE myapp.users (id Int32, name String)");
        catalog.Plan("INSERT INTO myapp.users VALUES (1, 'a')");

        // Should not throw — REINDEX on a qualified table resolves to
        // myapp.users and rebuilds its .datum-index sidecar.
        catalog.Plan("REINDEX myapp.users");
        catalog.Plan("REINDEX TABLE myapp.users");

        Assert.Equal(IndexValidity.Valid, catalog["myapp.users"].GetIndexValidity());
    }

    [Fact]
    public void Analyze_SchemaQualifiedTable_RunsAgainstCorrectTable()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE SCHEMA myapp");
        catalog.Plan("CREATE TABLE myapp.users (id Int32, name String)");
        catalog.Plan("INSERT INTO myapp.users VALUES (1, 'a'), (2, 'b')");

        // Should not throw. ANALYZE refreshes the manifest stats for the
        // qualified target.
        catalog.Plan("ANALYZE myapp.users");
    }

    [Fact]
    public void Update_UnknownSchema_ThrowsSchemaResolution()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        SchemaResolutionException ex = Assert.Throws<SchemaResolutionException>(() =>
            catalog.Plan("UPDATE nosuch.users SET name = 'x'"));
        Assert.Contains("nosuch", ex.Message);
    }

    [Fact]
    public void Delete_UnknownSchema_ThrowsSchemaResolution()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        SchemaResolutionException ex = Assert.Throws<SchemaResolutionException>(() =>
            catalog.Plan("DELETE FROM nosuch.users"));
        Assert.Contains("nosuch", ex.Message);
    }

    // ──────────────────── helpers ────────────────────

    private static async Task<int> CountRows(ITableProvider provider)
    {
        int n = 0;
        await foreach (RowBatch batch in provider.ScanAsync(null, null, null, CancellationToken.None))
        {
            try { n += batch.Count; }
            finally { batch.Dispose(); }
        }
        return n;
    }

    private static async Task<List<(int, string)>> ScanAsTuples(ITableProvider provider)
    {
        List<(int, string)> rows = new();
        await foreach (RowBatch batch in provider.ScanAsync(null, null, null, CancellationToken.None))
        {
            try
            {
                Arena arena = batch.Arena;
                for (int r = 0; r < batch.Count; r++)
                {
                    Row row = batch[r];
                    rows.Add((row[0].AsInt32(), row[1].AsString(arena)));
                }
            }
            finally
            {
                batch.Dispose();
            }
        }
        return rows;
    }
}
