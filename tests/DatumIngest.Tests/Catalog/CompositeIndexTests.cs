using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Execution;
using DatumIngest.Indexing;
using DatumIngest.Indexing.BTree.MutableBytes;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// Phase 2b/c tests for user-defined composite secondary indexes
/// (<c>CREATE INDEX</c> / <c>DROP INDEX</c>). Cover: sidecar file
/// lifecycle, catalog persistence across reopen, INSERT-time
/// maintenance via the append session, DROP TABLE cleanup of
/// dependent sidecars, validation errors, and IF EXISTS / IF NOT EXISTS
/// edge cases. Backfill of pre-existing rows is a v1 limitation:
/// CREATE INDEX on a populated table is rejected.
/// </summary>
public sealed class CompositeIndexTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_cidx_{Guid.NewGuid():N}");
    private string CatalogPath => Path.Combine(_tempDir, ".datum-catalog.json");
    private string CompositeIndexPath(string tableName, string indexName) =>
        Path.Combine(_tempDir, $"{tableName}.datum-cindex-{indexName}");

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

    // ──────────────────── CREATE INDEX — file lifecycle ────────────────────

    [Fact]
    public void CreateIndex_SingleColumn_CreatesSidecarFile()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool, CatalogPath);

        catalog.Plan("CREATE TABLE users (id Int32, name String)");
        catalog.Plan("CREATE INDEX idx_users_name ON users (name)");

        Assert.True(File.Exists(CompositeIndexPath("users", "idx_users_name")));
    }

    [Fact]
    public void CreateIndex_CompositeColumns_CreatesSidecarFile()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool, CatalogPath);

        catalog.Plan("CREATE TABLE orders (customer_id Int32, order_date Date, total Float64)");
        catalog.Plan("CREATE INDEX idx_orders_cust_date ON orders (customer_id, order_date)");

        Assert.True(File.Exists(CompositeIndexPath("orders", "idx_orders_cust_date")));
    }

    [Fact]
    public void DropIndex_RemovesSidecarFile()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool, CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32)");
        catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
        Assert.True(File.Exists(CompositeIndexPath("t", "idx_t_ab")));

        catalog.Plan("DROP INDEX idx_t_ab");
        Assert.False(File.Exists(CompositeIndexPath("t", "idx_t_ab")));
    }

    [Fact]
    public void DropTable_RemovesAllCompositeIndexSidecars()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool, CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32, c Int32)");
        catalog.Plan("CREATE INDEX idx_t_a ON t (a)");
        catalog.Plan("CREATE INDEX idx_t_bc ON t (b, c)");

        Assert.True(File.Exists(CompositeIndexPath("t", "idx_t_a")));
        Assert.True(File.Exists(CompositeIndexPath("t", "idx_t_bc")));

        catalog.Plan("DROP TABLE t");

        Assert.False(File.Exists(CompositeIndexPath("t", "idx_t_a")));
        Assert.False(File.Exists(CompositeIndexPath("t", "idx_t_bc")));
    }

    // ──────────────────── IF NOT EXISTS / IF EXISTS ────────────────────

    [Fact]
    public void CreateIndex_IfNotExists_OnExistingIndex_IsNoOp()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool, CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32)");
        catalog.Plan("CREATE INDEX idx_t_a ON t (a)");

        // Second call with IF NOT EXISTS must succeed silently.
        catalog.Plan("CREATE INDEX IF NOT EXISTS idx_t_a ON t (a)");

        Assert.True(File.Exists(CompositeIndexPath("t", "idx_t_a")));
    }

    [Fact]
    public void CreateIndex_DuplicateNameWithoutIfNotExists_Throws()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool, CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32)");
        catalog.Plan("CREATE INDEX idx_t_a ON t (a)");

        Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE INDEX idx_t_a ON t (a)"));
    }

    [Fact]
    public void DropIndex_IfExists_OnMissingIndex_IsNoOp()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool, CatalogPath);

        // No throw — IF EXISTS swallows the not-found.
        catalog.Plan("DROP INDEX IF EXISTS idx_nonexistent");
    }

    [Fact]
    public void DropIndex_WithoutIfExists_OnMissingIndex_Throws()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool, CatalogPath);

        Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("DROP INDEX idx_nonexistent"));
    }

    // ──────────────────── Validation ────────────────────

    [Fact]
    public void CreateIndex_OnMissingTable_Throws()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool, CatalogPath);

        Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE INDEX idx_x ON missing_table (col)"));
    }

    [Fact]
    public void CreateIndex_OnMissingColumn_Throws()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool, CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32)");

        Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE INDEX idx_t_b ON t (b)"));
    }

    [Fact]
    public void CreateIndex_OnPopulatedTable_Throws_V1Limitation()
    {
        // Phase 2b/c v1 limitation: backfill of existing rows isn't
        // implemented. Issue CREATE INDEX before any INSERTs.
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool, CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32)");
        catalog.Plan("INSERT INTO t VALUES (1, 10)");

        Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE INDEX idx_t_a ON t (a)"));
    }

    [Fact]
    public void CreateIndex_GlobalNameUniqueness_AcrossTables_Throws()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool, CatalogPath);

        catalog.Plan("CREATE TABLE t1 (a Int32)");
        catalog.Plan("CREATE TABLE t2 (a Int32)");

        catalog.Plan("CREATE INDEX shared_name ON t1 (a)");

        // PG semantics: index names are catalog-global.
        Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE INDEX shared_name ON t2 (a)"));
    }

    // ──────────────────── INSERT maintenance ────────────────────

    [Fact]
    public void Insert_AfterCreateIndex_PopulatesCompositeTree()
    {
        Pool pool = new(new PoolBacking());

        using (TableCatalog catalog = new(pool, CatalogPath))
        {
            catalog.Plan("CREATE TABLE t (a Int32, b Int32)");
            catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
            catalog.Plan("INSERT INTO t VALUES (1, 10), (2, 20), (3, 30)");
        }

        // Open the tree directly post-commit and verify the row count.
        using MutableBPlusTreeBytes tree =
            MutableBPlusTreeBytes.Open(CompositeIndexPath("t", "idx_t_ab"));
        Assert.Equal(3L, tree.EntryCount);
    }

    [Fact]
    public void Insert_AfterCreateIndex_TupleEncodingIsFindable()
    {
        Pool pool = new(new PoolBacking());

        using (TableCatalog catalog = new(pool, CatalogPath))
        {
            catalog.Plan("CREATE TABLE t (a Int32, b Int32)");
            catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
            catalog.Plan("INSERT INTO t VALUES (42, 99)");
        }

        using MutableBPlusTreeBytes tree =
            MutableBPlusTreeBytes.Open(CompositeIndexPath("t", "idx_t_ab"));

        byte[] probe = CompositeKeyEncoder.Encode(
            new[] { DataValue.FromInt32(42), DataValue.FromInt32(99) });

        Assert.True(tree.TryFind(probe, out BytesIndexEntry hit));
        Assert.Equal(probe, hit.Key);
    }

    [Fact]
    public void Insert_MultipleIndexes_AllMaintained()
    {
        Pool pool = new(new PoolBacking());

        using (TableCatalog catalog = new(pool, CatalogPath))
        {
            catalog.Plan("CREATE TABLE orders (customer_id Int32, order_date Date, status String)");
            catalog.Plan("CREATE INDEX idx_orders_cust ON orders (customer_id)");
            catalog.Plan("CREATE INDEX idx_orders_status_cust ON orders (status, customer_id)");
            catalog.Plan("INSERT INTO orders VALUES (1, '2026-01-01', 'open'), (2, '2026-01-02', 'closed')");
        }

        using MutableBPlusTreeBytes treeCust =
            MutableBPlusTreeBytes.Open(CompositeIndexPath("orders", "idx_orders_cust"));
        Assert.Equal(2L, treeCust.EntryCount);

        using MutableBPlusTreeBytes treeStatusCust =
            MutableBPlusTreeBytes.Open(CompositeIndexPath("orders", "idx_orders_status_cust"));
        Assert.Equal(2L, treeStatusCust.EntryCount);
    }

    // ──────────────────── Catalog persistence across reopen ────────────────────

    [Fact]
    public void CreateIndex_SurvivesCatalogReopen()
    {
        Pool pool = new(new PoolBacking());

        using (TableCatalog catalog = new(pool, CatalogPath))
        {
            catalog.Plan("CREATE TABLE t (a Int32, b Int32)");
            catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
            catalog.Plan("INSERT INTO t VALUES (1, 10)");
        }

        // Reopen and insert again — the second INSERT must also land in
        // the tree, proving the index was rehydrated from the catalog.
        using (TableCatalog reopened = new(pool, CatalogPath))
        {
            reopened.Plan("INSERT INTO t VALUES (2, 20)");
        }

        using MutableBPlusTreeBytes tree =
            MutableBPlusTreeBytes.Open(CompositeIndexPath("t", "idx_t_ab"));
        Assert.Equal(2L, tree.EntryCount);
    }

    // ──────────────────── ALTER DROP COLUMN cascade ────────────────────

    [Fact]
    public void AlterTable_DropColumn_CascadesDependentIndex()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool, CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32, c Int32)");
        catalog.Plan("CREATE INDEX idx_t_b ON t (b)");
        Assert.True(File.Exists(CompositeIndexPath("t", "idx_t_b")));

        catalog.Plan("ALTER TABLE t DROP COLUMN b");

        // Index file is gone; the index name is no longer registered.
        Assert.False(File.Exists(CompositeIndexPath("t", "idx_t_b")));
        Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("DROP INDEX idx_t_b"));
    }

    [Fact]
    public void AlterTable_DropColumn_LeavesUnrelatedIndexIntact()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool, CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32, c Int32)");
        catalog.Plan("CREATE INDEX idx_t_a ON t (a)");
        catalog.Plan("CREATE INDEX idx_t_bc ON t (b, c)");

        catalog.Plan("ALTER TABLE t DROP COLUMN b");

        // idx_t_a doesn't reference b — should survive.
        Assert.True(File.Exists(CompositeIndexPath("t", "idx_t_a")));
        // idx_t_bc references b — should be cascaded away.
        Assert.False(File.Exists(CompositeIndexPath("t", "idx_t_bc")));
    }

    [Fact]
    public void AlterTable_DropColumn_CascadeSurvivesCatalogReopen()
    {
        Pool pool = new(new PoolBacking());

        using (TableCatalog catalog = new(pool, CatalogPath))
        {
            catalog.Plan("CREATE TABLE t (a Int32, b Int32)");
            catalog.Plan("CREATE INDEX idx_t_b ON t (b)");
            catalog.Plan("ALTER TABLE t DROP COLUMN b");
        }

        using TableCatalog reopened = new(pool, CatalogPath);

        // After reopen, the dropped index name is gone from the catalog.
        Assert.Throws<InvalidOperationException>(() =>
            reopened.Plan("DROP INDEX idx_t_b"));
    }

    // ──────────────────── Phase 3: planner uses composite index ────────────────────

    [Fact]
    public async Task Select_FullCompositeMatch_ReturnsCorrectRow()
    {
        // Mixed-kind composite index (Int32 + Int32, payload Float64).
        // Avoid Date columns here — scan-side filter coercion for
        // `Date col = 'string literal'` is a pre-existing limitation
        // upstream of Phase 3; conflating the two would mask whether
        // the index-seek path itself works.
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool, CatalogPath);

        catalog.Plan("CREATE TABLE orders (customer_id Int32, product_id Int32, amount Float64)");
        catalog.Plan("CREATE INDEX idx_orders_cust_prod ON orders (customer_id, product_id)");
        catalog.Plan(
            "INSERT INTO orders VALUES " +
            "(1, 100, 10.0), " +
            "(1, 200, 20.0), " +
            "(2, 100, 30.0), " +
            "(2, 200, 40.0)");

        IQueryPlan plan = catalog.Plan(
            "SELECT amount FROM orders WHERE customer_id = 2 AND product_id = 200");
        List<double> amounts = await CollectFirstColumnDoubles(plan);

        Assert.Single(amounts);
        Assert.Equal(40.0, amounts[0]);
    }

    [Fact]
    public async Task Select_FullCompositeMatch_PredicateOrderInvariant()
    {
        // Equality predicates can appear in any order in the WHERE — the
        // planner must rebuild the tuple in the index's declared order.
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool, CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32, v Int32)");
        catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
        catalog.Plan("INSERT INTO t VALUES (1, 10, 100), (1, 20, 200), (2, 10, 300), (2, 20, 400)");

        IQueryPlan plan = catalog.Plan("SELECT v FROM t WHERE b = 20 AND a = 1");
        List<int> values = await CollectFirstColumnInts(plan);

        Assert.Single(values);
        Assert.Equal(200, values[0]);
    }

    [Fact]
    public async Task Select_PartialPredicateCoverage_FallsBackToScan_StillCorrect()
    {
        // v1 only handles full-prefix matches. Predicate covers only some
        // of the index's columns — the planner skips this index but the
        // query still returns the right rows via scan.
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool, CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32, v Int32)");
        catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
        catalog.Plan("INSERT INTO t VALUES (1, 10, 100), (1, 20, 200), (2, 10, 300)");

        IQueryPlan plan = catalog.Plan("SELECT v FROM t WHERE a = 1");
        List<int> values = await CollectFirstColumnInts(plan);

        values.Sort();
        Assert.Equal(new[] { 100, 200 }, values);
    }

    [Fact]
    public async Task Select_CompositeIndexMatchesRowAcrossCatalogReopen()
    {
        Pool pool = new(new PoolBacking());

        using (TableCatalog catalog = new(pool, CatalogPath))
        {
            catalog.Plan("CREATE TABLE t (a Int32, b Int32, v Int32)");
            catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
            catalog.Plan("INSERT INTO t VALUES (1, 10, 100), (2, 20, 200), (3, 30, 300)");
        }

        // Reopen and probe — the rehydrated provider must expose the
        // composite index so the planner can use it.
        using TableCatalog reopened = new(pool, CatalogPath);
        IQueryPlan plan = reopened.Plan("SELECT v FROM t WHERE a = 2 AND b = 20");
        List<int> values = await CollectFirstColumnInts(plan);

        Assert.Single(values);
        Assert.Equal(200, values[0]);
    }

    [Fact]
    public async Task Select_NoMatchingRow_ReturnsEmpty()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool, CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32, v Int32)");
        catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
        catalog.Plan("INSERT INTO t VALUES (1, 10, 100), (2, 20, 200)");

        IQueryPlan plan = catalog.Plan("SELECT v FROM t WHERE a = 99 AND b = 99");
        List<int> values = await CollectFirstColumnInts(plan);

        Assert.Empty(values);
    }

    // ──────────────────── Strategy assertions (EXPLAIN ANALYZE) ────────────────────

    [Fact]
    public async Task Select_FullCompositeMatch_FiresExactSeekPath_NotChunkedScan()
    {
        // Strategy proof — uses EXPLAIN ANALYZE's exact-seek counter to
        // verify the seek path actually fired (correctness alone can't
        // distinguish "seek returned 1 row" from "chunked scan filtered
        // down to 1 row").
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool, CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32, v Int32)");
        catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
        catalog.Plan("INSERT INTO t VALUES (1, 10, 100), (1, 20, 200), (2, 10, 300), (2, 20, 400)");

        IQueryPlan plan = catalog.Plan("SELECT v FROM t WHERE a = 1 AND b = 20");
        int? exactSeek = await GetScanExactSeekRowsAsync(plan);

        Assert.True(exactSeek.HasValue, "Scan should report an exact-seek count when an index satisfies the predicate.");
    }

    [Fact]
    public async Task Select_FullCompositeMatch_ProvesCompositePathWinsOverSingleColumn()
    {
        // Each individual column on its own would seek many rows (a=1
        // appears in 3 rows; b=20 in 3 rows); only the composite key
        // (a=1, b=20) resolves to a unique row. If the seek count is 1,
        // the composite path won the selectivity contest.
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool, CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32, v Int32)");
        catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
        catalog.Plan(
            "INSERT INTO t VALUES " +
            "(1, 10, 110), (1, 20, 120), (1, 30, 130), " +
            "(2, 20, 220), (3, 20, 320)");

        IQueryPlan plan = catalog.Plan("SELECT v FROM t WHERE a = 1 AND b = 20");
        int? exactSeek = await GetScanExactSeekRowsAsync(plan);

        Assert.Equal(1, exactSeek);
    }

    [Fact]
    public async Task Select_PartialPredicateCoverage_DoesNotUseCompositeIndex()
    {
        // Single-column predicate can't satisfy a multi-column composite
        // index in v1 (no leftmost-prefix range scan yet). The auto-built
        // per-column tree on `a` may still fire, so the seek count would
        // reflect that — but it'll be 3 (count of a=1 rows), not 1
        // (count of (a=1, b=20)).
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool, CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32, v Int32)");
        catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
        catalog.Plan(
            "INSERT INTO t VALUES " +
            "(1, 10, 110), (1, 20, 120), (1, 30, 130), " +
            "(2, 20, 220)");

        IQueryPlan plan = catalog.Plan("SELECT v FROM t WHERE a = 1");
        int? exactSeek = await GetScanExactSeekRowsAsync(plan);

        // Strategy varies — the per-column auto-built tree on `a` may
        // fire (seek count = 3 because three rows have a=1). What we
        // care about: NOT 1 (which would mean the composite index
        // incorrectly matched a partial-prefix predicate).
        Assert.NotEqual(1, exactSeek);
    }

    // ──────────────────── Phase 4: mutation maintenance ────────────────────

    [Fact]
    public async Task Delete_RemovesRowFromCompositeIndexResults()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool, CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32, v Int32)");
        catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
        catalog.Plan("INSERT INTO t VALUES (1, 10, 100), (2, 20, 200), (3, 30, 300)");

        catalog.Plan("DELETE FROM t WHERE a = 2 AND b = 20");

        IQueryPlan plan = catalog.Plan("SELECT v FROM t WHERE a = 2 AND b = 20");
        List<int> values = await CollectFirstColumnInts(plan);

        Assert.Empty(values);
    }

    [Fact]
    public async Task Delete_PreservesOtherRowsInCompositeIndexResults()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool, CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32, v Int32)");
        catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
        catalog.Plan("INSERT INTO t VALUES (1, 10, 100), (2, 20, 200), (3, 30, 300)");

        catalog.Plan("DELETE FROM t WHERE a = 2 AND b = 20");

        // Non-deleted rows must still be findable via the index.
        IQueryPlan plan = catalog.Plan("SELECT v FROM t WHERE a = 1 AND b = 10");
        List<int> values = await CollectFirstColumnInts(plan);

        Assert.Single(values);
        Assert.Equal(100, values[0]);
    }

    [Fact]
    public async Task Update_NonIndexedColumn_PreservesIndexHits()
    {
        // UPDATE changes only the payload column; the composite key is
        // unchanged. The index must still resolve the row to the right
        // (post-update) value.
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool, CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32, v Int32)");
        catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
        catalog.Plan("INSERT INTO t VALUES (1, 10, 100), (2, 20, 200)");

        catalog.Plan("UPDATE t SET v = 999 WHERE a = 2 AND b = 20");

        IQueryPlan plan = catalog.Plan("SELECT v FROM t WHERE a = 2 AND b = 20");
        List<int> values = await CollectFirstColumnInts(plan);

        Assert.Single(values);
        Assert.Equal(999, values[0]);
    }

    [Fact]
    public async Task Update_IndexedColumn_NewKeyFindable_OldKeyMisses()
    {
        // UPDATE changes a column covered by the composite index.
        // After the update: the new key (a=2, b=99) must be findable;
        // the old key (a=2, b=20) must NOT match any rows.
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool, CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32, v Int32)");
        catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
        catalog.Plan("INSERT INTO t VALUES (1, 10, 100), (2, 20, 200)");

        catalog.Plan("UPDATE t SET b = 99 WHERE a = 2 AND b = 20");

        IQueryPlan planNew = catalog.Plan("SELECT v FROM t WHERE a = 2 AND b = 99");
        List<int> newValues = await CollectFirstColumnInts(planNew);
        Assert.Single(newValues);
        Assert.Equal(200, newValues[0]);

        IQueryPlan planOld = catalog.Plan("SELECT v FROM t WHERE a = 2 AND b = 20");
        List<int> oldValues = await CollectFirstColumnInts(planOld);
        Assert.Empty(oldValues);
    }

    private static async Task<List<int>> CollectFirstColumnInts(IQueryPlan plan)
    {
        List<int> values = new();
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                values.Add(batch[i][0].AsInt32());
            }
        }
        return values;
    }

    private static async Task<List<double>> CollectFirstColumnDoubles(IQueryPlan plan)
    {
        List<double> values = new();
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                values.Add(batch[i][0].AsFloat64());
            }
        }
        return values;
    }

    /// <summary>
    /// Runs the query under EXPLAIN ANALYZE and returns the scan node's
    /// exact-seek count — the count is set only when the seek path
    /// (point lookup via an index) actually fired. Tests use this to
    /// verify the composite index was consulted, not just that the
    /// result rows were correct.
    /// </summary>
    private static async Task<int?> GetScanExactSeekRowsAsync(IQueryPlan plan)
    {
        ExplainPlanNode root = await plan.AnalyzeAsync(CancellationToken.None);
        return FindScanNode(root)?.ExactSeekRowsFetched;
    }

    private static ExplainPlanNode? FindScanNode(ExplainPlanNode node)
    {
        if (node.OperatorName == "Scan") return node;
        foreach (ExplainPlanNode child in node.Children)
        {
            ExplainPlanNode? found = FindScanNode(child);
            if (found is not null) return found;
        }
        return null;
    }

    [Fact]
    public void DropIndex_SurvivesCatalogReopen()
    {
        Pool pool = new(new PoolBacking());

        using (TableCatalog catalog = new(pool, CatalogPath))
        {
            catalog.Plan("CREATE TABLE t (a Int32)");
            catalog.Plan("CREATE INDEX idx_t_a ON t (a)");
            catalog.Plan("DROP INDEX idx_t_a");
        }

        using TableCatalog reopened = new(pool, CatalogPath);

        // After reopen the index name should be gone — re-issuing DROP
        // (without IF EXISTS) must throw.
        Assert.Throws<InvalidOperationException>(() =>
            reopened.Plan("DROP INDEX idx_t_a"));
    }
}
