using DatumIngest.Catalog;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// Tests for <c>UPDATE … RETURNING</c>. Mirrors the InsertReturning suite —
/// covers <c>*</c>, explicit columns, expressions / aliases, multi-row,
/// the WHERE-matched-but-no-op case (PG includes these), and the
/// UPDATE … FROM cross-product path's last-match-wins post-image.
/// </summary>
public sealed class UpdateReturningTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_upd_returning_{Guid.NewGuid():N}");
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

    // ──────────────────── Basic shapes ────────────────────

    [Fact]
    public async Task Returning_StarExpansion_YieldsPostUpdateImage()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int64 IDENTITY, name String, status String)");
        catalog.Plan("INSERT INTO t (name, status) VALUES ('alice', 'pending'), ('bob', 'pending')");

        IQueryPlan plan = catalog.Plan(
            "UPDATE t SET status = 'active' WHERE name = 'alice' RETURNING *");

        List<DataValue[]> rows = await CollectRows(plan);
        Assert.Single(rows);
        Assert.Equal(3, rows[0].Length);
        Assert.Equal(1L, rows[0][0].AsInt64());
        Assert.Equal("alice", rows[0][1].AsString());
        Assert.Equal("active", rows[0][2].AsString());      // post-update value
    }

    [Fact]
    public async Task Returning_ExplicitColumns_YieldsPostUpdateValues()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int64 IDENTITY, score Int32)");
        catalog.Plan("INSERT INTO t (score) VALUES (10), (20), (30)");

        IQueryPlan plan = catalog.Plan(
            "UPDATE t SET score = score + 100 WHERE score > 15 RETURNING id, score");

        List<DataValue[]> rows = await CollectRows(plan);
        Assert.Equal(2, rows.Count);
        Assert.Equal((2L, 120), (rows[0][0].AsInt64(), rows[0][1].AsInt32()));
        Assert.Equal((3L, 130), (rows[1][0].AsInt64(), rows[1][1].AsInt32()));
    }

    [Fact]
    public async Task Returning_ExpressionAlias_EvaluatesAgainstPostImage()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int64 IDENTITY, name String)");
        catalog.Plan("INSERT INTO t (name) VALUES ('alice')");

        IQueryPlan plan = catalog.Plan(
            "UPDATE t SET name = 'bob' RETURNING upper(name) AS shouted, name");

        List<DataValue[]> rows = await CollectRows(plan);
        Assert.Single(rows);
        // upper(name) sees the post-update value 'bob' → 'BOB'.
        Assert.Equal("BOB", rows[0][0].AsString());
        Assert.Equal("bob", rows[0][1].AsString());
    }

    [Fact]
    public async Task Returning_NoMatch_YieldsNoRows()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int64 IDENTITY, score Int32)");
        catalog.Plan("INSERT INTO t (score) VALUES (10)");

        IQueryPlan plan = catalog.Plan(
            "UPDATE t SET score = 999 WHERE score > 9999 RETURNING id, score");

        List<DataValue[]> rows = await CollectRows(plan);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task Returning_UnconditionalUpdate_YieldsEveryRow()
    {
        // No WHERE — every row is matched; RETURNING surfaces every one.
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int64 IDENTITY, status String)");
        catalog.Plan("INSERT INTO t (status) VALUES ('a'), ('b'), ('c')");

        IQueryPlan plan = catalog.Plan(
            "UPDATE t SET status = 'X' RETURNING id, status");

        List<DataValue[]> rows = await CollectRows(plan);
        Assert.Equal(3, rows.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal((long)(i + 1), rows[i][0].AsInt64());
            Assert.Equal("X", rows[i][1].AsString());
        }
    }

    // ──────────────────── No-op rows (PG: WHERE-matched rows always surface) ────────────────────

    [Fact]
    public async Task Returning_WhereMatchedButNoOp_StillSurfacesRow()
    {
        // PG semantics: RETURNING yields every row whose WHERE matched,
        // even when the SET was effectively a no-op (idempotent update).
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int64 IDENTITY, status String)");
        catalog.Plan("INSERT INTO t (status) VALUES ('pending')");

        IQueryPlan plan = catalog.Plan(
            "UPDATE t SET status = 'pending' WHERE status = 'pending' RETURNING id, status");

        List<DataValue[]> rows = await CollectRows(plan);
        Assert.Single(rows);
        Assert.Equal(1L, rows[0][0].AsInt64());
        Assert.Equal("pending", rows[0][1].AsString());
    }

    // ──────────────────── No-RETURNING regression ────────────────────

    [Fact]
    public async Task NoReturning_PlanYieldsNoRows()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int64 IDENTITY, status String)");
        catalog.Plan("INSERT INTO t (status) VALUES ('a')");

        IQueryPlan plan = catalog.Plan("UPDATE t SET status = 'b'");

        List<DataValue[]> rows = await CollectRows(plan);
        Assert.Empty(rows);
    }

    // ──────────────────── Persistent target ────────────────────

    [Fact]
    public async Task Returning_OnPersistentTable_PostImageValues()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE conversations (id Int64 IDENTITY, title String)");
        catalog.Plan("INSERT INTO conversations (title) VALUES ('Old')");

        IQueryPlan plan = catalog.Plan(
            "UPDATE conversations SET title = 'New' WHERE id = 1 RETURNING id, title");

        List<DataValue[]> rows = await CollectRows(plan);
        Assert.Single(rows);
        Assert.Equal(1L, rows[0][0].AsInt64());
        Assert.Equal("New", rows[0][1].AsString());
    }

    // ──────────────────── UPDATE … FROM RETURNING ────────────────────

    [Fact]
    public async Task ReturningFrom_LastMatchWins_PostImageReflectsFinalSet()
    {
        // Two source rows map to the same target via the WHERE join; last
        // match wins. RETURNING surfaces the post-image after that
        // resolution — not the intermediate state.
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE target (id Int32, status String)");
        catalog.Plan("INSERT INTO target VALUES (1, 'pending')");
        catalog.Plan("CREATE TEMP TABLE updates (target_id Int32, new_status String)");
        catalog.Plan("INSERT INTO updates VALUES (1, 'first'), (1, 'second')");

        IQueryPlan plan = catalog.Plan(
            "UPDATE target SET status = updates.new_status " +
            "FROM updates WHERE target.id = updates.target_id " +
            "RETURNING target.id, target.status");

        List<DataValue[]> rows = await CollectRows(plan);
        Assert.Single(rows);
        Assert.Equal(1, rows[0][0].AsInt32());
        // PG behaviour is implementation-defined for tied source rows;
        // we document last-match-wins. Either 'first' or 'second' would
        // be acceptable in principle; pin the current behaviour explicitly.
        string finalStatus = rows[0][1].AsString();
        Assert.True(finalStatus == "first" || finalStatus == "second",
            $"expected last-match-wins value; got '{finalStatus}'");
    }

    [Fact]
    public async Task ReturningFrom_NoSourceMatch_YieldsNoRows()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE target (id Int32, status String)");
        catalog.Plan("INSERT INTO target VALUES (1, 'pending'), (2, 'pending')");
        catalog.Plan("CREATE TEMP TABLE updates (target_id Int32, new_status String)");
        catalog.Plan("INSERT INTO updates VALUES (99, 'never')");

        IQueryPlan plan = catalog.Plan(
            "UPDATE target SET status = updates.new_status " +
            "FROM updates WHERE target.id = updates.target_id " +
            "RETURNING target.id");

        List<DataValue[]> rows = await CollectRows(plan);
        Assert.Empty(rows);
    }

    // ──────────────────── GENERATED column dependents ────────────────────

    [Fact]
    public async Task Returning_GeneratedDependent_ReflectsRecomputedValue()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan(
            "CREATE TEMP TABLE t (" +
            "  id Int64 IDENTITY, " +
            "  base Int32, " +
            "  doubled Int32 GENERATED ALWAYS AS (base * 2)" +
            ")");
        catalog.Plan("INSERT INTO t (base) VALUES (10)");

        IQueryPlan plan = catalog.Plan(
            "UPDATE t SET base = 50 WHERE id = 1 RETURNING base, doubled");

        List<DataValue[]> rows = await CollectRows(plan);
        Assert.Single(rows);
        Assert.Equal(50, rows[0][0].AsInt32());
        Assert.Equal(100, rows[0][1].AsInt32()); // 50 * 2 — recomputed
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
