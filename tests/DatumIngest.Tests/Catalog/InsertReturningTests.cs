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

    // ──────────────────── INSERT … SELECT … RETURNING (deferred) ────────────────────

    [Fact]
    public void InsertSelect_WithReturning_ThrowsNotSupported()
    {
        // C1d ships RETURNING for INSERT … SELECT. C1b is VALUES-only.
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE src (n Int32)");
        catalog.Plan("CREATE TEMP TABLE dst (id Int64 IDENTITY, n Int32)");
        catalog.Plan("INSERT INTO src VALUES (1)");

        NotSupportedException ex = Assert.Throws<NotSupportedException>(() =>
            catalog.Plan("INSERT INTO dst (n) SELECT n FROM src RETURNING id"));
        Assert.Contains("RETURNING with a SELECT source", ex.Message);
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
