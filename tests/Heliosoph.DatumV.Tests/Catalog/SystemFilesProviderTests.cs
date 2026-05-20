using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Catalog.Providers;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Tests.Catalog;

/// <summary>
/// Tests for <see cref="SystemFilesProvider"/> — the on-disk complement to
/// <c>system.udfs</c>/<c>procedures</c>/<c>models</c>. Verifies path
/// classification, orphan detection, sidecar surfacing, and the empty case
/// (in-memory catalog with no directory).
/// </summary>
public sealed class SystemFilesProviderTests : ServiceTestBase, IDisposable
{
    private readonly string _scratchDir;
    private readonly string _catalogPath;

    public SystemFilesProviderTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            $"datum-system-files-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchDir);
        _catalogPath = Path.Combine(_scratchDir, CatalogStore.DefaultFileName);
    }

    public new void Dispose()
    {
        base.Dispose();
        try { if (Directory.Exists(_scratchDir)) Directory.Delete(_scratchDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    /// <summary>Plain-CLR snapshot of a system.files row.</summary>
    private sealed record FileRow(
        string Path,
        string Kind,
        string? Schema,
        string? Name,
        long SizeBytes,
        bool IsOrphan);

    private static async Task<List<FileRow>> ScanAsync(TableCatalog catalog)
    {
        ITableProvider provider = catalog[SystemFilesProvider.TableName];
        List<FileRow> rows = new();
        await foreach (RowBatch batch in provider.ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null,
            CancellationToken.None))
        {
            Arena arena = batch.Arena;
            for (int i = 0; i < batch.Count; i++)
            {
                Row row = batch[i];
                rows.Add(new FileRow(
                    Path: row[0].AsString(arena),
                    Kind: row[1].AsString(arena),
                    Schema: row[2].IsNull ? null : row[2].AsString(arena),
                    Name: row[3].IsNull ? null : row[3].AsString(arena),
                    SizeBytes: row[4].AsInt64(),
                    IsOrphan: row[6].AsBoolean()));
            }
        }
        return rows;
    }

    [Fact]
    public void Schema_HasSevenColumnsInDeclaredOrder()
    {
        TableCatalog catalog = CreateCatalog();
        Schema schema = catalog[SystemFilesProvider.TableName].GetSchema();

        Assert.Equal(7, schema.Columns.Count);
        Assert.Equal("path", schema.Columns[0].Name);
        Assert.Equal("kind", schema.Columns[1].Name);
        Assert.Equal("schema", schema.Columns[2].Name);
        Assert.Equal("name", schema.Columns[3].Name);
        Assert.Equal("size_bytes", schema.Columns[4].Name);
        Assert.Equal("modified_at", schema.Columns[5].Name);
        Assert.Equal("is_orphan", schema.Columns[6].Name);

        Assert.False(schema.Columns[0].Nullable);
        Assert.True(schema.Columns[2].Nullable);  // schema can be null
        Assert.True(schema.Columns[3].Nullable);  // name can be null
        Assert.Equal(DataKind.TimestampTz, schema.Columns[5].Kind);
        Assert.Equal(DataKind.Boolean, schema.Columns[6].Kind);
    }

    [Fact]
    public async Task NoCatalogPath_ReturnsZeroRows()
    {
        // In-memory catalog (no path) → directory is null → scan is empty.
        TableCatalog catalog = CreateCatalog();
        List<FileRow> rows = await ScanAsync(catalog);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task FreshCatalog_AfterUdfCreate_SurfacesManifestAndUdfFile()
    {
        TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE FUNCTION shout(s STRING) AS upper(s)");

        List<FileRow> rows = await ScanAsync(catalog);

        // Expect at minimum: .datum-catalog.json, .gitignore, udfs/public/shout.sql.
        Assert.Contains(rows, r => r.Path == ".datum-catalog.json" && r.Kind == "manifest");
        Assert.Contains(rows, r => r.Path == ".gitignore" && r.Kind == "gitignore");

        FileRow udf = Assert.Single(rows, r => r.Kind == "udf");
        Assert.Equal("udfs/public/shout.sql", udf.Path);
        Assert.Equal("public", udf.Schema);
        Assert.Equal("shout", udf.Name);
        Assert.False(udf.IsOrphan);
        Assert.True(udf.SizeBytes > 0);
    }

    [Fact]
    public async Task ProcedureAndModel_ClassifyByDirectory()
    {
        TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE PROCEDURE noop() AS BEGIN SELECT 1 END");

        // Drop a hand-written user model .sql so we don't need a dispatcher.
        // The orphan column should call it out because the manifest doesn't
        // know about it.
        Directory.CreateDirectory(Path.Combine(_scratchDir, "models"));
        File.WriteAllText(
            Path.Combine(_scratchDir, "models", "ghost.sql"),
            "CREATE OR REPLACE MODEL ghost(x INT32) RETURNS INT32 USING 'x' AS BEGIN RETURN x END");

        List<FileRow> rows = await ScanAsync(catalog);

        FileRow proc = Assert.Single(rows, r => r.Kind == "procedure");
        Assert.Equal("procedures/public/noop.sql", proc.Path);
        Assert.Equal("public", proc.Schema);
        Assert.Equal("noop", proc.Name);
        Assert.False(proc.IsOrphan);

        FileRow model = Assert.Single(rows, r => r.Kind == "model");
        Assert.Equal("models/ghost.sql", model.Path);
        Assert.Equal("models", model.Schema);
        Assert.Equal("ghost", model.Name);
        Assert.True(model.IsOrphan); // not in DeclaredModels
    }

    [Fact]
    public async Task PersistentTable_SurfacesDatumFileAndSidecars()
    {
        TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE TABLE users (id Int32 PRIMARY KEY, name String)");
        catalog.Plan("INSERT INTO users VALUES (1, 'alice')");

        List<FileRow> rows = await ScanAsync(catalog);

        FileRow data = Assert.Single(rows, r => r.Kind == "data");
        Assert.Equal("data/public/users.datum", data.Path);
        Assert.Equal("public", data.Schema);
        Assert.Equal("users", data.Name);
        Assert.False(data.IsOrphan);

        // PK sidecar + manifest sidecar (after INSERT) surface as data_sidecar
        // entries — same schema/name as the parent table.
        IEnumerable<FileRow> sidecars = rows.Where(r => r.Kind == "data_sidecar");
        Assert.Contains(sidecars, r => r.Path.EndsWith(".datum-pkindex"));
        foreach (FileRow s in sidecars)
        {
            Assert.Equal("public", s.Schema);
            Assert.Equal("users", s.Name);
            Assert.False(s.IsOrphan); // sidecars are never orphans (unmanaged)
        }
    }

    [Fact]
    public async Task View_ClassifiesAsView_AndIsNotOrphan()
    {
        TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE TABLE t (a Int32, b String)");
        catalog.Plan("CREATE VIEW v AS SELECT a FROM t");

        List<FileRow> rows = await ScanAsync(catalog);

        FileRow view = Assert.Single(rows, r => r.Kind == "view");
        Assert.Equal("views/public/v.sql", view.Path);
        Assert.Equal("public", view.Schema);
        Assert.Equal("v", view.Name);
        Assert.False(view.IsOrphan);
        Assert.True(view.SizeBytes > 0);
    }

    [Fact]
    public async Task ViewFile_WithoutManifestEntry_IsFlaggedOrphan()
    {
        // Pre-create the catalog so the directory exists + manifest is wired.
        TableCatalog seed = CreateCatalog(_catalogPath);
        seed.Plan("CREATE TABLE t (a Int32)");
        seed.Plan("CREATE VIEW known AS SELECT a FROM t");

        string orphanDir = Path.Combine(_scratchDir, "views", "public");
        Directory.CreateDirectory(orphanDir);
        File.WriteAllText(Path.Combine(orphanDir, "ghost.sql"),
            "CREATE OR REPLACE VIEW ghost AS SELECT a FROM t");

        List<FileRow> rows = await ScanAsync(seed);

        FileRow known = rows.Single(r => r.Path == "views/public/known.sql");
        Assert.False(known.IsOrphan);

        FileRow ghost = rows.Single(r => r.Path == "views/public/ghost.sql");
        Assert.True(ghost.IsOrphan);
    }

    [Fact]
    public async Task UdfFile_WithoutManifestEntry_IsFlaggedOrphan()
    {
        // Pre-create the catalog so the directory exists + manifest is wired.
        TableCatalog seed = CreateCatalog(_catalogPath);
        seed.Plan("CREATE FUNCTION known(x INT32) AS x + 1");

        // Drop a stray .sql file as if a crash or hand-edit left one behind.
        string orphanPath = Path.Combine(_scratchDir, "udfs", "public", "ghost.sql");
        File.WriteAllText(orphanPath, "CREATE OR REPLACE FUNCTION ghost(x INT32) AS x");

        List<FileRow> rows = await ScanAsync(seed);

        FileRow known = rows.Single(r => r.Path == "udfs/public/known.sql");
        Assert.False(known.IsOrphan);

        FileRow ghost = rows.Single(r => r.Path == "udfs/public/ghost.sql");
        Assert.True(ghost.IsOrphan);
    }

    [Fact]
    public async Task UnknownTopLevelFile_ClassifiesAsOther()
    {
        TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE FUNCTION shout(s STRING) AS upper(s)");

        File.WriteAllText(Path.Combine(_scratchDir, "README.md"), "# my catalog");

        List<FileRow> rows = await ScanAsync(catalog);

        FileRow readme = rows.Single(r => r.Path == "README.md");
        Assert.Equal("other", readme.Kind);
        Assert.Null(readme.Schema);
        Assert.Null(readme.Name);
        Assert.False(readme.IsOrphan);
    }

    [Fact]
    public async Task Rows_AreOrderedByPath()
    {
        TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE FUNCTION zeta(x INT32) AS x");
        catalog.Plan("CREATE FUNCTION alpha(x INT32) AS x");
        catalog.Plan("CREATE PROCEDURE foo() AS BEGIN SELECT 1 END");

        List<FileRow> rows = await ScanAsync(catalog);

        // Ordinal-ignore-case ordering means `.datum-catalog.json` /
        // `.gitignore` come first (leading dot < letters), then
        // procedures/, then udfs/.
        List<string> paths = rows.Select(r => r.Path).ToList();
        Assert.Equal(paths, paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
    }
}
