using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Tests.Catalog;

/// <summary>
/// PR11d end-to-end tests for <c>UPDATE … FROM &lt;single-source&gt;</c>.
/// Cover 1:1 join, no-match no-op, multiple source rows matching the
/// same target (last-wins), target alias, source alias, qualified
/// column references on both sides, target-in-FROM rejection, missing
/// source rejection, and persistence on .datum tables.
/// </summary>
public sealed class UpdateFromExecutorTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_pr11d_{Guid.NewGuid():N}");
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

    private TableCatalog NewMemoryCatalog() => CreateCatalog();
    private TableCatalog NewFileCatalog() => CreateCatalog(CatalogPath);

    // ──────────────────── 1:1 match ────────────────────

    [Fact]
    public async Task UpdateFrom_OneToOneMatch_OnTempTable()
    {
        using TableCatalog catalog = NewMemoryCatalog();
        catalog.Plan("CREATE TEMP TABLE features (id Int32, score Float64)");
        catalog.Plan("CREATE TEMP TABLE raw (id Int32, value Float64)");
        catalog.Plan("INSERT INTO features VALUES (1, 0.0), (2, 0.0), (3, 0.0)");
        catalog.Plan("INSERT INTO raw VALUES (1, 1.5), (3, 3.5)");

        catalog.Plan(
            "UPDATE features SET score = raw.value FROM raw WHERE features.id = raw.id");

        var rows = await ScanInt32Float64(catalog["features"]);
        Assert.Equal((1, 1.5), rows[0]);
        Assert.Equal((2, 0.0), rows[1]);   // no source match
        Assert.Equal((3, 3.5), rows[2]);
    }

    [Fact]
    public async Task UpdateFrom_OneToOneMatch_OnDatumFile()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE features (id Int32, score Float64)");
        catalog.Plan("CREATE TABLE raw (id Int32, value Float64)");
        catalog.Plan("INSERT INTO features VALUES (1, 0.0), (2, 0.0), (3, 0.0)");
        catalog.Plan("INSERT INTO raw VALUES (1, 1.5), (2, 2.5)");

        catalog.Plan(
            "UPDATE features SET score = raw.value FROM raw WHERE features.id = raw.id");

        var rows = await ScanInt32Float64(catalog["features"]);
        Assert.Equal((1, 1.5), rows[0]);
        Assert.Equal((2, 2.5), rows[1]);
        Assert.Equal((3, 0.0), rows[2]);
    }

    // ──────────────────── No source rows / no matches ────────────────────

    [Fact]
    public async Task UpdateFrom_EmptySource_IsNoOp()
    {
        using TableCatalog catalog = NewMemoryCatalog();
        catalog.Plan("CREATE TEMP TABLE features (id Int32, score Float64)");
        catalog.Plan("CREATE TEMP TABLE raw (id Int32, value Float64)");
        catalog.Plan("INSERT INTO features VALUES (1, 1.0), (2, 2.0)");
        // raw is empty.

        catalog.Plan("UPDATE features SET score = raw.value FROM raw WHERE features.id = raw.id");

        var rows = await ScanInt32Float64(catalog["features"]);
        Assert.Equal((1, 1.0), rows[0]);
        Assert.Equal((2, 2.0), rows[1]);
    }

    [Fact]
    public async Task UpdateFrom_NoMatchingRows_IsNoOp()
    {
        using TableCatalog catalog = NewMemoryCatalog();
        catalog.Plan("CREATE TEMP TABLE features (id Int32, score Float64)");
        catalog.Plan("CREATE TEMP TABLE raw (id Int32, value Float64)");
        catalog.Plan("INSERT INTO features VALUES (1, 1.0), (2, 2.0)");
        catalog.Plan("INSERT INTO raw VALUES (99, 99.0), (100, 100.0)");

        catalog.Plan("UPDATE features SET score = raw.value FROM raw WHERE features.id = raw.id");

        var rows = await ScanInt32Float64(catalog["features"]);
        Assert.Equal((1, 1.0), rows[0]);
        Assert.Equal((2, 2.0), rows[1]);
    }

    // ──────────────────── Multi-match (last-wins) ────────────────────

    [Fact]
    public async Task UpdateFrom_MultipleSourceMatches_LastWins()
    {
        using TableCatalog catalog = NewMemoryCatalog();
        catalog.Plan("CREATE TEMP TABLE features (id Int32, score Float64)");
        catalog.Plan("CREATE TEMP TABLE raw (id Int32, value Float64)");
        catalog.Plan("INSERT INTO features VALUES (1, 0.0)");
        // Three source rows, all match feature 1. Last-wins (PostgreSQL semantics).
        catalog.Plan("INSERT INTO raw VALUES (1, 1.0), (1, 2.0), (1, 3.0)");

        catalog.Plan("UPDATE features SET score = raw.value FROM raw WHERE features.id = raw.id");

        var rows = await ScanInt32Float64(catalog["features"]);
        // The "last" source row is the last-emitted by the scan; for the
        // in-memory provider this is the insertion order's last row.
        Assert.Equal((1, 3.0), rows[0]);
    }

    // ──────────────────── Target alias ────────────────────

    [Fact]
    public async Task UpdateFrom_TargetAlias_AliasResolves()
    {
        using TableCatalog catalog = NewMemoryCatalog();
        catalog.Plan("CREATE TEMP TABLE features (id Int32, score Float64)");
        catalog.Plan("CREATE TEMP TABLE raw (id Int32, value Float64)");
        catalog.Plan("INSERT INTO features VALUES (1, 0.0), (2, 0.0)");
        catalog.Plan("INSERT INTO raw VALUES (1, 11.0), (2, 22.0)");

        catalog.Plan(
            "UPDATE features f SET score = raw.value FROM raw WHERE f.id = raw.id");

        var rows = await ScanInt32Float64(catalog["features"]);
        Assert.Equal((1, 11.0), rows[0]);
        Assert.Equal((2, 22.0), rows[1]);
    }

    [Fact]
    public async Task UpdateFrom_TargetAndSourceAliases_BothResolve()
    {
        using TableCatalog catalog = NewMemoryCatalog();
        catalog.Plan("CREATE TEMP TABLE features (id Int32, score Float64)");
        catalog.Plan("CREATE TEMP TABLE raw (id Int32, value Float64)");
        catalog.Plan("INSERT INTO features VALUES (1, 0.0)");
        catalog.Plan("INSERT INTO raw VALUES (1, 42.0)");

        catalog.Plan(
            "UPDATE features AS f SET score = r.value FROM raw AS r WHERE f.id = r.id");

        var rows = await ScanInt32Float64(catalog["features"]);
        Assert.Equal((1, 42.0), rows[0]);
    }

    // ──────────────────── Multi-column SET, mixed sides ────────────────────

    [Fact]
    public async Task UpdateFrom_MultiColumnSet_MixedSourceAndTargetRefs()
    {
        using TableCatalog catalog = NewMemoryCatalog();
        catalog.Plan("CREATE TEMP TABLE features (id Int32, name String, score Float64)");
        catalog.Plan("CREATE TEMP TABLE raw (id Int32, label String, value Float64)");
        catalog.Plan("INSERT INTO features VALUES (1, 'old', 0.0), (2, 'old', 0.0)");
        catalog.Plan("INSERT INTO raw VALUES (1, 'new1', 1.0), (2, 'new2', 2.0)");

        catalog.Plan(
            "UPDATE features SET name = raw.label, score = raw.value " +
            "FROM raw WHERE features.id = raw.id");

        var rows = await ScanInt32StringFloat64(catalog["features"]);
        Assert.Equal((1, "new1", 1.0), rows[0]);
        Assert.Equal((2, "new2", 2.0), rows[1]);
    }

    // ──────────────────── WHERE = join + filter ────────────────────

    [Fact]
    public async Task UpdateFrom_WhereCombinesJoinAndFilter()
    {
        using TableCatalog catalog = NewMemoryCatalog();
        catalog.Plan("CREATE TEMP TABLE features (id Int32, score Float64)");
        catalog.Plan("CREATE TEMP TABLE raw (id Int32, value Float64)");
        catalog.Plan("INSERT INTO features VALUES (1, 0.0), (2, 0.0), (3, 0.0)");
        catalog.Plan("INSERT INTO raw VALUES (1, 0.5), (2, 1.5), (3, 2.5)");

        // Only update target rows where the joined source value is > 1.0.
        catalog.Plan(
            "UPDATE features SET score = raw.value FROM raw " +
            "WHERE features.id = raw.id AND raw.value > 1.0");

        var rows = await ScanInt32Float64(catalog["features"]);
        Assert.Equal((1, 0.0), rows[0]);   // raw.value = 0.5, filtered
        Assert.Equal((2, 1.5), rows[1]);
        Assert.Equal((3, 2.5), rows[2]);
    }

    // ──────────────────── SET expressions referencing both sides ────────────────────

    [Fact]
    public async Task UpdateFrom_SetExpression_ReferencesBothSides()
    {
        using TableCatalog catalog = NewMemoryCatalog();
        catalog.Plan("CREATE TEMP TABLE features (id Int32, score Float64)");
        catalog.Plan("CREATE TEMP TABLE raw (id Int32, value Float64)");
        catalog.Plan("INSERT INTO features VALUES (1, 10.0), (2, 20.0)");
        catalog.Plan("INSERT INTO raw VALUES (1, 1.0), (2, 2.0)");

        // SET expression sums target's existing score with source's value.
        catalog.Plan(
            "UPDATE features SET score = features.score + raw.value " +
            "FROM raw WHERE features.id = raw.id");

        var rows = await ScanInt32Float64(catalog["features"]);
        Assert.Equal((1, 11.0), rows[0]);
        Assert.Equal((2, 22.0), rows[1]);
    }

    // ──────────────────── Rejections ────────────────────

    [Fact]
    public void UpdateFrom_TargetInFrom_Rejected()
    {
        using TableCatalog catalog = NewMemoryCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, name String)");
        catalog.Plan("INSERT INTO t VALUES (1, 'a')");

        QueryPlanException ex = Assert.Throws<QueryPlanException>(
            () => catalog.Plan("UPDATE t SET name = 'x' FROM t WHERE 1 = 1"));
        Assert.Contains("must not appear in the FROM clause", ex.Message);
    }

    [Fact]
    public void UpdateFrom_MissingSource_Rejected()
    {
        using TableCatalog catalog = NewMemoryCatalog();
        catalog.Plan("CREATE TEMP TABLE features (id Int32, score Float64)");

        QueryPlanException ex = Assert.Throws<QueryPlanException>(
            () => catalog.Plan(
                "UPDATE features SET score = raw.value FROM raw WHERE features.id = raw.id"));
        Assert.Contains("not registered", ex.Message);
    }

    // ──────────────────── helpers ────────────────────

    private static async Task<List<(int, double)>> ScanInt32Float64(ITableProvider provider)
    {
        List<(int, double)> rows = new();
        await foreach (RowBatch batch in provider.ScanAsync(null, null, null, CancellationToken.None))
        {
            try
            {
                for (int r = 0; r < batch.Count; r++)
                {
                    Row row = batch[r];
                    rows.Add((row[0].AsInt32(), row[1].AsFloat64()));
                }
            }
            finally { batch.Dispose(); }
        }
        return rows;
    }

    private static async Task<List<(int, string, double)>> ScanInt32StringFloat64(ITableProvider provider)
    {
        List<(int, string, double)> rows = new();
        await foreach (RowBatch batch in provider.ScanAsync(null, null, null, CancellationToken.None))
        {
            try
            {
                Arena arena = batch.Arena;
                for (int r = 0; r < batch.Count; r++)
                {
                    Row row = batch[r];
                    rows.Add((row[0].AsInt32(), row[1].AsString(arena), row[2].AsFloat64()));
                }
            }
            finally { batch.Dispose(); }
        }
        return rows;
    }
}
