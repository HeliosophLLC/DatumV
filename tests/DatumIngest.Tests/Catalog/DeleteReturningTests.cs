using DatumIngest.Catalog;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// Tests for <c>DELETE … RETURNING</c>. The RETURNING projection surfaces
/// the <em>pre-delete</em> row image (PG semantics) — the rows as they
/// existed immediately before being tombstoned.
/// </summary>
public sealed class DeleteReturningTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_del_returning_{Guid.NewGuid():N}");
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

    // ———————————————————— Basic shapes ————————————————————

    [Fact]
    public async Task Returning_StarExpansion_YieldsPreDeleteImage()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int64 IDENTITY, name String)");
        catalog.Plan("INSERT INTO t (name) VALUES ('alice'), ('bob')");

        StatementPlan plan = catalog.Plan(
            "DELETE FROM t WHERE name = 'alice' RETURNING *");

        List<DataValue[]> rows = await CollectRows(plan);
        Assert.Single(rows);
        Assert.Equal(2, rows[0].Length);
        Assert.Equal(1L, rows[0][0].AsInt64());
        Assert.Equal("alice", rows[0][1].AsString());

        // Side effect happened.
        Assert.Equal(1, catalog["t"].GetRowCount());
    }

    [Fact]
    public async Task Returning_ExplicitColumns_YieldsPreDeleteValues()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int64 IDENTITY, score Int32)");
        catalog.Plan("INSERT INTO t (score) VALUES (10), (20), (30)");

        StatementPlan plan = catalog.Plan(
            "DELETE FROM t WHERE score > 15 RETURNING id, score");

        List<DataValue[]> rows = await CollectRows(plan);
        Assert.Equal(2, rows.Count);
        Assert.Equal((2L, 20), (rows[0][0].AsInt64(), rows[0][1].AsInt32()));
        Assert.Equal((3L, 30), (rows[1][0].AsInt64(), rows[1][1].AsInt32()));

        Assert.Equal(1, catalog["t"].GetRowCount());
    }

    [Fact]
    public async Task Returning_ExpressionAlias_EvaluatesAgainstPreImage()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int64 IDENTITY, name String)");
        catalog.Plan("INSERT INTO t (name) VALUES ('alice')");

        StatementPlan plan = catalog.Plan(
            "DELETE FROM t WHERE id = 1 RETURNING upper(name) AS shouted, id * 100 AS scaled");

        List<DataValue[]> rows = await CollectRows(plan);
        Assert.Single(rows);
        Assert.Equal("ALICE", rows[0][0].AsString());
        Assert.Equal(100L, rows[0][1].AsInt64());
    }

    [Fact]
    public async Task Returning_NoMatch_YieldsNoRows()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int64 IDENTITY, name String)");
        catalog.Plan("INSERT INTO t (name) VALUES ('alice')");

        StatementPlan plan = catalog.Plan(
            "DELETE FROM t WHERE name = 'nobody' RETURNING id");

        List<DataValue[]> rows = await CollectRows(plan);
        Assert.Empty(rows);
        Assert.Equal(1, catalog["t"].GetRowCount());
    }

    [Fact]
    public async Task Returning_UnconditionalDelete_YieldsEveryRow()
    {
        // No WHERE → every row tombstoned; RETURNING surfaces every one.
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int64 IDENTITY, name String)");
        catalog.Plan("INSERT INTO t (name) VALUES ('a'), ('b'), ('c')");

        StatementPlan plan = catalog.Plan("DELETE FROM t RETURNING id, name");

        List<DataValue[]> rows = await CollectRows(plan);
        Assert.Equal(3, rows.Count);
        Assert.Equal((1L, "a"), (rows[0][0].AsInt64(), rows[0][1].AsString()));
        Assert.Equal((2L, "b"), (rows[1][0].AsInt64(), rows[1][1].AsString()));
        Assert.Equal((3L, "c"), (rows[2][0].AsInt64(), rows[2][1].AsString()));

        Assert.Equal(0, catalog["t"].GetRowCount());
    }

    // ———————————————————— No-RETURNING regression ————————————————————

    [Fact]
    public async Task NoReturning_PlanYieldsNoRows()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int64 IDENTITY, name String)");
        catalog.Plan("INSERT INTO t (name) VALUES ('a')");

        StatementPlan plan = catalog.Plan("DELETE FROM t WHERE id = 1");

        List<DataValue[]> rows = await CollectRows(plan);
        Assert.Empty(rows);
        Assert.Equal(0, catalog["t"].GetRowCount());
    }

    // ———————————————————— Persistent target ————————————————————

    [Fact]
    public async Task Returning_OnPersistentTable_PreImageValues()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE conversations (id Int64 IDENTITY, title String)");
        catalog.Plan("INSERT INTO conversations (title) VALUES ('Old'), ('Keep')");

        StatementPlan plan = catalog.Plan(
            "DELETE FROM conversations WHERE title = 'Old' RETURNING id, title");

        List<DataValue[]> rows = await CollectRows(plan);
        Assert.Single(rows);
        Assert.Equal(1L, rows[0][0].AsInt64());
        Assert.Equal("Old", rows[0][1].AsString());

        // Note: persistent providers report gross row count (includes
        // tombstones), so we verify the post-delete live-row set via SELECT.
        StatementPlan postSelect = catalog.Plan("SELECT title FROM conversations");
        List<DataValue[]> liveRows = await CollectRows(postSelect);
        Assert.Single(liveRows);
        Assert.Equal("Keep", liveRows[0][0].AsString());
    }

    // ———————————————————— Modifying CTE (WITH … DELETE … RETURNING) ————————————————————

    [Fact]
    public async Task ModifyingCte_SelectFromCte_YieldsPreDeleteRows()
    {
        // Canonical WITH-DELETE shape: data-modifying CTE + outer SELECT
        // pulls from it. DELETE side effect runs at plan time; the outer
        // query projects from the captured RETURNING (pre-image) rows.
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int64 IDENTITY, name String)");
        catalog.Plan("INSERT INTO t (name) VALUES ('alice'), ('bob')");

        StatementPlan plan = catalog.Plan(
            "WITH removed AS (" +
            "  DELETE FROM t WHERE name = 'alice' RETURNING id, name" +
            ") " +
            "SELECT id, name FROM removed");

        List<DataValue[]> rows = await CollectRows(plan);
        Assert.Single(rows);
        Assert.Equal((1L, "alice"), (rows[0][0].AsInt64(), rows[0][1].AsString()));
        // Side effect actually applied (one live row remains).
        Assert.Equal(1, catalog["t"].GetRowCount());
    }

    [Fact]
    public async Task ModifyingCte_OuterFiltersRows_OnlyMatchingYield()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int64 IDENTITY, score Int32)");
        catalog.Plan("INSERT INTO t (score) VALUES (10), (20), (30)");

        StatementPlan plan = catalog.Plan(
            "WITH removed AS (" +
            "  DELETE FROM t RETURNING id, score" +
            ") " +
            "SELECT id, score FROM removed WHERE score > 15");

        List<DataValue[]> rows = await CollectRows(plan);
        Assert.Equal(2, rows.Count);
        Assert.Equal((2L, 20), (rows[0][0].AsInt64(), rows[0][1].AsInt32()));
        Assert.Equal((3L, 30), (rows[1][0].AsInt64(), rows[1][1].AsInt32()));
        // All rows tombstoned regardless of outer filter (no live rows).
        Assert.Equal(0, catalog["t"].GetRowCount());
    }

    [Fact]
    public async Task ModifyingCte_StarReturning_ProjectsAllColumns()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int64 IDENTITY, name String, status String)");
        catalog.Plan("INSERT INTO t (name, status) VALUES ('alice', 'pending')");

        StatementPlan plan = catalog.Plan(
            "WITH gone AS (DELETE FROM t RETURNING *) " +
            "SELECT * FROM gone");

        List<DataValue[]> rows = await CollectRows(plan);
        Assert.Single(rows);
        Assert.Equal(3, rows[0].Length);
        Assert.Equal(1L, rows[0][0].AsInt64());
        Assert.Equal("alice", rows[0][1].AsString());
        Assert.Equal("pending", rows[0][2].AsString());
    }

    [Fact]
    public void ModifyingCte_WithoutReturning_Throws()
    {
        // A DELETE without RETURNING has no rows to project — reject
        // explicitly at plan time so the user gets a clear error.
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int64 IDENTITY, name String)");
        catalog.Plan("INSERT INTO t (name) VALUES ('a')");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan(
                "WITH gone AS (DELETE FROM t) " +
                "SELECT * FROM gone"));
        Assert.Contains("RETURNING", ex.Message);
    }

    // ———————————————————— Helpers ————————————————————

    private static async Task<List<DataValue[]>> CollectRows(StatementPlan plan)
    {
        List<DataValue[]> rows = new();
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                Row row = batch[r];
                DataValue[] copy = new DataValue[row.FieldCount];
                for (int c = 0; c < copy.Length; c++)
                {
                    copy[c] = row[c];
                }
                rows.Add(copy);
            }
        }
        return rows;
    }
}
