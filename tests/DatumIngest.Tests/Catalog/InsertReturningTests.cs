using DatumIngest.Catalog;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// C1b tests: <c>INSERT INTO … VALUES (…) RETURNING …</c>. Covers single-row
/// and multi-row VALUES with column-ref, expression, and <c>*</c> projections;
/// validates post-DEFAULT and post-IDENTITY values surface correctly; pins
/// post-commit semantics (failed inserts yield nothing).
/// </summary>
public sealed class InsertReturningTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_returning_{Guid.NewGuid():N}");
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

    // ──────────────────── Basic VALUES + RETURNING ────────────────────

    [Fact]
    public async Task Returning_SingleColumn_YieldsOneRowOneColumn()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int64 IDENTITY, name String)");

        IQueryPlan plan = catalog.Plan("INSERT INTO t (name) VALUES ('alice') RETURNING id");

        List<DataValue[]> rows = await CollectRows(plan);
        Assert.Single(rows);
        Assert.Single(rows[0]);
        Assert.Equal(1L, rows[0][0].AsInt64());
    }

    [Fact]
    public async Task Returning_StarExpansion_YieldsAllResolvedColumns()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int64 IDENTITY, name String, status String DEFAULT 'pending')");

        IQueryPlan plan = catalog.Plan("INSERT INTO t (name) VALUES ('alice') RETURNING *");

        List<DataValue[]> rows = await CollectRows(plan);
        Assert.Single(rows);
        Assert.Equal(3, rows[0].Length);
        Assert.Equal(1L, rows[0][0].AsInt64());        // post-IDENTITY
        Assert.Equal("alice", rows[0][1].AsString());  // explicit
        Assert.Equal("pending", rows[0][2].AsString()); // post-DEFAULT
    }

    [Fact]
    public async Task Returning_MultiRowValues_YieldsAllInsertedRows()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int64 IDENTITY, name String)");

        IQueryPlan plan = catalog.Plan(
            "INSERT INTO t (name) VALUES ('a'), ('b'), ('c') RETURNING id, name");

        List<DataValue[]> rows = await CollectRows(plan);
        Assert.Equal(3, rows.Count);
        Assert.Equal((1L, "a"), (rows[0][0].AsInt64(), rows[0][1].AsString()));
        Assert.Equal((2L, "b"), (rows[1][0].AsInt64(), rows[1][1].AsString()));
        Assert.Equal((3L, "c"), (rows[2][0].AsInt64(), rows[2][1].AsString()));
    }

    [Fact]
    public async Task Returning_ComputedExpression_EvaluatesAgainstInsertedRow()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32 IDENTITY(100, 5), name String)");

        IQueryPlan plan = catalog.Plan(
            "INSERT INTO t (name) VALUES ('a') RETURNING id * 2 AS doubled, upper(name) AS shouted");

        List<DataValue[]> rows = await CollectRows(plan);
        Assert.Single(rows);
        Assert.Equal(200, rows[0][0].AsInt32());        // 100 (seed) * 2
        Assert.Equal("A", rows[0][1].AsString());
    }

    [Fact]
    public async Task Returning_OmittedColumnWithDefault_PicksUpDefaultValue()
    {
        // Omitted DEFAULT-bearing column gets filled by the executor;
        // RETURNING reads the resolved value, not the literal the user
        // typed (because they didn't type one).
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan(
            "CREATE TEMP TABLE t (id Int64 IDENTITY, name String, label String DEFAULT 'unset')");

        IQueryPlan plan = catalog.Plan(
            "INSERT INTO t (name) VALUES ('a') RETURNING label, id");

        List<DataValue[]> rows = await CollectRows(plan);
        Assert.Single(rows);
        Assert.Equal("unset", rows[0][0].AsString());
        Assert.Equal(1L, rows[0][1].AsInt64());
    }

    // ──────────────────── No-RETURNING regression ────────────────────

    [Fact]
    public async Task NoReturning_PlanYieldsNoRows()
    {
        // Confirms the existing side-effect-only INSERT path is unchanged
        // — Plan() returns a plan that iterates as zero rows.
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int64 IDENTITY, name String)");

        IQueryPlan plan = catalog.Plan("INSERT INTO t (name) VALUES ('alice')");

        List<DataValue[]> rows = await CollectRows(plan);
        Assert.Empty(rows);
        // But the side effect did happen.
        Assert.Equal(1, catalog["t"].GetRowCount());
    }

    // ──────────────────── Persistent target ────────────────────

    [Fact]
    public async Task Returning_OnPersistentTable_RoundTripsIdentity()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool, CatalogPath);
        catalog.Plan("CREATE TABLE conversations (id Int64 IDENTITY, title String)");

        IQueryPlan plan = catalog.Plan(
            "INSERT INTO conversations (title) VALUES ('Chat') RETURNING id, title");

        List<DataValue[]> rows = await CollectRows(plan);
        Assert.Single(rows);
        Assert.Equal(1L, rows[0][0].AsInt64());
        Assert.Equal("Chat", rows[0][1].AsString());
    }

    // ──────────────────── INSERT … SELECT … RETURNING (C1d) ────────────────────

    [Fact]
    public async Task ReturningSelect_PicksUpIdentitiesFromSource()
    {
        // The natural pattern: copy source rows into a target with IDENTITY,
        // RETURNING surfaces the assigned ids.
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE src (name String)");
        catalog.Plan("CREATE TEMP TABLE dst (id Int64 IDENTITY, name String)");
        catalog.Plan("INSERT INTO src VALUES ('alice'), ('bob'), ('carol')");

        IQueryPlan plan = catalog.Plan(
            "INSERT INTO dst (name) SELECT name FROM src RETURNING id, name");

        List<DataValue[]> rows = await CollectRows(plan);
        Assert.Equal(3, rows.Count);
        Assert.Equal((1L, "alice"), (rows[0][0].AsInt64(), rows[0][1].AsString()));
        Assert.Equal((2L, "bob"), (rows[1][0].AsInt64(), rows[1][1].AsString()));
        Assert.Equal((3L, "carol"), (rows[2][0].AsInt64(), rows[2][1].AsString()));
    }

    [Fact]
    public async Task ReturningSelect_StarYieldsAllResolvedColumns()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE src (n Int32)");
        catalog.Plan("CREATE TEMP TABLE dst (id Int64 IDENTITY, n Int32, status String DEFAULT 'new')");
        catalog.Plan("INSERT INTO src VALUES (10), (20)");

        IQueryPlan plan = catalog.Plan("INSERT INTO dst (n) SELECT n FROM src RETURNING *");

        List<DataValue[]> rows = await CollectRows(plan);
        Assert.Equal(2, rows.Count);
        Assert.Equal(3, rows[0].Length);
        Assert.Equal(1L, rows[0][0].AsInt64());
        Assert.Equal(10, rows[0][1].AsInt32());
        Assert.Equal("new", rows[0][2].AsString());
        Assert.Equal(2L, rows[1][0].AsInt64());
        Assert.Equal(20, rows[1][1].AsInt32());
        Assert.Equal("new", rows[1][2].AsString());
    }

    [Fact]
    public async Task ReturningSelect_EmptySource_YieldsNoRows()
    {
        // INSERT … SELECT against an empty source is a no-op (no commit
        // even fires); RETURNING yields zero rows.
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE src (n Int32)");
        catalog.Plan("CREATE TEMP TABLE dst (id Int64 IDENTITY, n Int32)");

        IQueryPlan plan = catalog.Plan("INSERT INTO dst (n) SELECT n FROM src RETURNING id");

        List<DataValue[]> rows = await CollectRows(plan);
        Assert.Empty(rows);
        Assert.Equal(0, catalog["dst"].GetRowCount());
    }

    [Fact]
    public async Task ReturningSelect_WhereFiltered_OnlyYieldsAcceptedRows()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE src (id Int32, name String)");
        catalog.Plan("CREATE TEMP TABLE dst (id Int64 IDENTITY, src_id Int32, name String)");
        catalog.Plan("INSERT INTO src VALUES (1, 'a'), (2, 'b'), (3, 'c')");

        IQueryPlan plan = catalog.Plan(
            "INSERT INTO dst (src_id, name) SELECT id, name FROM src WHERE id >= 2 RETURNING id, src_id");

        List<DataValue[]> rows = await CollectRows(plan);
        Assert.Equal(2, rows.Count);
        Assert.Equal((1L, 2), (rows[0][0].AsInt64(), rows[0][1].AsInt32()));
        Assert.Equal((2L, 3), (rows[1][0].AsInt64(), rows[1][1].AsInt32()));
    }

    // ──────────────────── WITH cte AS (INSERT … RETURNING) (C1e) ────────────────────

    [Fact]
    public async Task ModifyingCte_SelectFromCte_YieldsReturningRows()
    {
        // Canonical WITH-INSERT shape: data-modifying CTE + outer SELECT
        // pulls from it. The INSERT side effect runs at plan time; the
        // outer query then projects from the captured RETURNING rows.
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE conversations (id Int64 IDENTITY, title String)");

        IQueryPlan plan = catalog.Plan(
            "WITH new_conv AS (" +
            "  INSERT INTO conversations (title) VALUES ('Chat') RETURNING id, title" +
            ") " +
            "SELECT id, title FROM new_conv");

        List<DataValue[]> rows = await CollectRows(plan);
        Assert.Single(rows);
        Assert.Equal((1L, "Chat"), (rows[0][0].AsInt64(), rows[0][1].AsString()));
        // Side effect actually applied:
        Assert.Equal(1, catalog["conversations"].GetRowCount());
    }

    [Fact]
    public async Task ModifyingCte_OuterFiltersRows_OnlyMatchingYield()
    {
        // Verify the outer SELECT can filter the CTE's RETURNING rows.
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int64 IDENTITY, n Int32)");

        IQueryPlan plan = catalog.Plan(
            "WITH inserted AS (" +
            "  INSERT INTO t (n) VALUES (10), (20), (30) RETURNING id, n" +
            ") " +
            "SELECT id, n FROM inserted WHERE n >= 20");

        List<DataValue[]> rows = await CollectRows(plan);
        Assert.Equal(2, rows.Count);
        Assert.Equal((2L, 20), (rows[0][0].AsInt64(), rows[0][1].AsInt32()));
        Assert.Equal((3L, 30), (rows[1][0].AsInt64(), rows[1][1].AsInt32()));
        // All 3 inserts committed regardless of the outer WHERE:
        Assert.Equal(3, catalog["t"].GetRowCount());
    }

    [Fact]
    public async Task ModifyingCte_StarReturning_ProjectsAllColumns()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int64 IDENTITY, name String, status String DEFAULT 'new')");

        IQueryPlan plan = catalog.Plan(
            "WITH ins AS (INSERT INTO t (name) VALUES ('alice') RETURNING *) " +
            "SELECT * FROM ins");

        List<DataValue[]> rows = await CollectRows(plan);
        Assert.Single(rows);
        Assert.Equal(3, rows[0].Length);
        Assert.Equal(1L, rows[0][0].AsInt64());
        Assert.Equal("alice", rows[0][1].AsString());
        Assert.Equal("new", rows[0][2].AsString());
    }

    [Fact]
    public void ModifyingCte_WithoutReturning_Throws()
    {
        // An INSERT without RETURNING has no rows to project — reject
        // explicitly at plan time so the user gets a clear error.
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int64 IDENTITY, name String)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan(
                "WITH ins AS (INSERT INTO t (name) VALUES ('alice')) " +
                "SELECT * FROM ins"));
        Assert.Contains("RETURNING", ex.Message);
    }

    [Fact]
    public async Task ModifyingCte_FromInsertSelect_YieldsCapturedRows()
    {
        // INSERT … SELECT … RETURNING inside a CTE is also valid.
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE src (n Int32)");
        catalog.Plan("CREATE TEMP TABLE dst (id Int64 IDENTITY, n Int32)");
        catalog.Plan("INSERT INTO src VALUES (1), (2), (3)");

        IQueryPlan plan = catalog.Plan(
            "WITH copied AS (" +
            "  INSERT INTO dst (n) SELECT n FROM src RETURNING id, n" +
            ") " +
            "SELECT id, n FROM copied");

        List<DataValue[]> rows = await CollectRows(plan);
        Assert.Equal(3, rows.Count);
        Assert.Equal((1L, 1), (rows[0][0].AsInt64(), rows[0][1].AsInt32()));
        Assert.Equal((2L, 2), (rows[1][0].AsInt64(), rows[1][1].AsInt32()));
        Assert.Equal((3L, 3), (rows[2][0].AsInt64(), rows[2][1].AsInt32()));
    }

    // ──────────────────── Helpers ────────────────────

    private static async Task<List<DataValue[]>> CollectRows(IQueryPlan plan)
    {
        List<DataValue[]> rows = new();
        await foreach (RowBatch batch in plan.ExecuteAsync(default))
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
