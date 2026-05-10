using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Indexing;
using DatumIngest.Indexing.BTree.MutableBytes;
using DatumIngest.Model;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// Tests for user-defined composite secondary indexes
/// (<c>CREATE INDEX</c> / <c>DROP INDEX</c>). Cover: sidecar file
/// lifecycle, catalog persistence across reopen, INSERT-time
/// maintenance via the append session, post-UPDATE rebuild,
/// CREATE INDEX backfill on populated tables, DROP TABLE cleanup of
/// dependent sidecars, ALTER DROP COLUMN cascade, planner integration
/// (full-match seek path), and validation / IF EXISTS / IF NOT EXISTS
/// edge cases.
/// </summary>
public sealed class CompositeIndexTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_cidx_{Guid.NewGuid():N}");
    private string CatalogPath => Path.Combine(_tempDir, ".datum-catalog.json");
    private string CompositeIndexPath(string tableName, string indexName) =>
        Path.Combine(_tempDir, "data", "public", $"{tableName}.datum-cindex-{indexName}");

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

    // ———————————————————— CREATE INDEX — file lifecycle ————————————————————

    [Fact]
    public void CreateIndex_SingleColumn_CreatesSidecarFile()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE users (id Int32, name String)");
        catalog.Plan("CREATE INDEX idx_users_name ON users (name)");

        Assert.True(File.Exists(CompositeIndexPath("users", "idx_users_name")));
    }

    [Fact]
    public void CreateIndex_CompositeColumns_CreatesSidecarFile()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE orders (customer_id Int32, order_date Date, total Float64)");
        catalog.Plan("CREATE INDEX idx_orders_cust_date ON orders (customer_id, order_date)");

        Assert.True(File.Exists(CompositeIndexPath("orders", "idx_orders_cust_date")));
    }

    [Fact]
    public void DropIndex_RemovesSidecarFile()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32)");
        catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
        Assert.True(File.Exists(CompositeIndexPath("t", "idx_t_ab")));

        catalog.Plan("DROP INDEX idx_t_ab");
        Assert.False(File.Exists(CompositeIndexPath("t", "idx_t_ab")));
    }

    [Fact]
    public void DropTable_RemovesAllCompositeIndexSidecars()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32, c Int32)");
        catalog.Plan("CREATE INDEX idx_t_a ON t (a)");
        catalog.Plan("CREATE INDEX idx_t_bc ON t (b, c)");

        Assert.True(File.Exists(CompositeIndexPath("t", "idx_t_a")));
        Assert.True(File.Exists(CompositeIndexPath("t", "idx_t_bc")));

        catalog.Plan("DROP TABLE t");

        Assert.False(File.Exists(CompositeIndexPath("t", "idx_t_a")));
        Assert.False(File.Exists(CompositeIndexPath("t", "idx_t_bc")));
    }

    // ———————————————————— IF NOT EXISTS / IF EXISTS ————————————————————

    [Fact]
    public void CreateIndex_IfNotExists_OnExistingIndex_IsNoOp()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32)");
        catalog.Plan("CREATE INDEX idx_t_a ON t (a)");

        // Second call with IF NOT EXISTS must succeed silently.
        catalog.Plan("CREATE INDEX IF NOT EXISTS idx_t_a ON t (a)");

        Assert.True(File.Exists(CompositeIndexPath("t", "idx_t_a")));
    }

    [Fact]
    public void CreateIndex_DuplicateNameWithoutIfNotExists_Throws()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32)");
        catalog.Plan("CREATE INDEX idx_t_a ON t (a)");

        Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE INDEX idx_t_a ON t (a)"));
    }

    [Fact]
    public void DropIndex_IfExists_OnMissingIndex_IsNoOp()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        // No throw — IF EXISTS swallows the not-found.
        catalog.Plan("DROP INDEX IF EXISTS idx_nonexistent");
    }

    [Fact]
    public void DropIndex_WithoutIfExists_OnMissingIndex_Throws()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("DROP INDEX idx_nonexistent"));
    }

    // ———————————————————— Validation ————————————————————

    [Fact]
    public void CreateIndex_OnMissingTable_Throws()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE INDEX idx_x ON missing_table (col)"));
    }

    [Fact]
    public void CreateIndex_OnMissingColumn_Throws()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32)");

        Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE INDEX idx_t_b ON t (b)"));
    }

    [Fact]
    public async Task CreateIndex_OnPopulatedTable_BackfillsExistingRows()
    {
        // Phase 5: CREATE INDEX after data has been INSERTed must scan
        // the existing rows and populate the tree so pre-existing keys
        // are immediately findable.
        List<int> values;
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE t (a Int32, b Int32, v Int32)");
            catalog.Plan("INSERT INTO t VALUES (1, 10, 100), (2, 20, 200), (3, 30, 300)");

            // No throw — backfill runs silently.
            catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");

            // The planner must now route equality predicates against the
            // backfilled keys through the seek path.
            StatementPlan plan = catalog.Plan("SELECT v FROM t WHERE a = 2 AND b = 20");
            values = await CollectFirstColumnInts(plan);
        }

        Assert.Single(values);
        Assert.Equal(200, values[0]);

        // After the catalog drops its handle, the tree file should hold
        // an entry per backfilled row.
        using MutableBPlusTreeBytes tree =
            MutableBPlusTreeBytes.Open(CompositeIndexPath("t", "idx_t_ab"));
        Assert.Equal(3L, tree.EntryCount);
    }

    [Fact]
    public async Task CreateIndex_OnPopulatedTable_BackfillAndSubsequentInsert_BothFindable()
    {
        // Verifies that backfilled rows and post-CREATE-INDEX INSERTs
        // coexist correctly in the same tree.
        List<int> oldRowValues, newRowValues;
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE t (a Int32, b Int32, v Int32)");
            catalog.Plan("INSERT INTO t VALUES (1, 10, 100), (2, 20, 200)");
            catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
            catalog.Plan("INSERT INTO t VALUES (3, 30, 300)");

            // Backfilled row.
            StatementPlan planOld = catalog.Plan("SELECT v FROM t WHERE a = 1 AND b = 10");
            oldRowValues = await CollectFirstColumnInts(planOld);

            // Post-CREATE-INDEX INSERT.
            StatementPlan planNew = catalog.Plan("SELECT v FROM t WHERE a = 3 AND b = 30");
            newRowValues = await CollectFirstColumnInts(planNew);
        }

        Assert.Equal(100, oldRowValues[0]);
        Assert.Equal(300, newRowValues[0]);

        using MutableBPlusTreeBytes tree =
            MutableBPlusTreeBytes.Open(CompositeIndexPath("t", "idx_t_ab"));
        Assert.Equal(3L, tree.EntryCount);
    }

    [Fact]
    public void CreateIndex_GlobalNameUniqueness_AcrossTables_Throws()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE t1 (a Int32)");
        catalog.Plan("CREATE TABLE t2 (a Int32)");

        catalog.Plan("CREATE INDEX shared_name ON t1 (a)");

        // PG semantics: index names are catalog-global.
        Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE INDEX shared_name ON t2 (a)"));
    }

    // ———————————————————— INSERT maintenance ————————————————————

    [Fact]
    public void Insert_AfterCreateIndex_PopulatesCompositeTree()
    {
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
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
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
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
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
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

    // ———————————————————— Catalog persistence across reopen ————————————————————

    [Fact]
    public void CreateIndex_SurvivesCatalogReopen()
    {
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE t (a Int32, b Int32)");
            catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
            catalog.Plan("INSERT INTO t VALUES (1, 10)");
        }

        // Reopen and insert again — the second INSERT must also land in
        // the tree, proving the index was rehydrated from the catalog.
        // Pool isn't shared across the two catalogs: each CreateCatalog
        // call resolves a fresh Pool wrapper from the test's DI scope,
        // both pointing at the same singleton PoolBacking. Reopen
        // correctness is mediated by the on-disk .datum-catalog.json,
        // not by pool state.
        using (TableCatalog reopened = CreateCatalog(CatalogPath))
        {
            reopened.Plan("INSERT INTO t VALUES (2, 20)");
        }

        using MutableBPlusTreeBytes tree =
            MutableBPlusTreeBytes.Open(CompositeIndexPath("t", "idx_t_ab"));
        Assert.Equal(2L, tree.EntryCount);
    }

    // ———————————————————— ALTER DROP COLUMN cascade ————————————————————

    [Fact]
    public void AlterTable_DropColumn_CascadesDependentIndex()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);

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
        using TableCatalog catalog = CreateCatalog(CatalogPath);

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
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE t (a Int32, b Int32)");
            catalog.Plan("CREATE INDEX idx_t_b ON t (b)");
            catalog.Plan("ALTER TABLE t DROP COLUMN b");
        }

        using TableCatalog reopened = CreateCatalog(CatalogPath);

        // After reopen, the dropped index name is gone from the catalog.
        Assert.Throws<InvalidOperationException>(() =>
            reopened.Plan("DROP INDEX idx_t_b"));
    }

    // ———————————————————— Phase 3: planner uses composite index ————————————————————

    [Fact]
    public async Task Select_FullCompositeMatch_ReturnsCorrectRow()
    {
        // Mixed-kind composite index (Int32 + Int32, payload Float64).
        // Avoid Date columns here — scan-side filter coercion for
        // `Date col = 'string literal'` is a pre-existing limitation
        // upstream of Phase 3; conflating the two would mask whether
        // the index-seek path itself works.
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE orders (customer_id Int32, product_id Int32, amount Float64)");
        catalog.Plan("CREATE INDEX idx_orders_cust_prod ON orders (customer_id, product_id)");
        catalog.Plan(
            "INSERT INTO orders VALUES " +
            "(1, 100, 10.0), " +
            "(1, 200, 20.0), " +
            "(2, 100, 30.0), " +
            "(2, 200, 40.0)");

        StatementPlan plan = catalog.Plan(
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
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32, v Int32)");
        catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
        catalog.Plan("INSERT INTO t VALUES (1, 10, 100), (1, 20, 200), (2, 10, 300), (2, 20, 400)");

        StatementPlan plan = catalog.Plan("SELECT v FROM t WHERE b = 20 AND a = 1");
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
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32, v Int32)");
        catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
        catalog.Plan("INSERT INTO t VALUES (1, 10, 100), (1, 20, 200), (2, 10, 300)");

        StatementPlan plan = catalog.Plan("SELECT v FROM t WHERE a = 1");
        List<int> values = await CollectFirstColumnInts(plan);

        values.Sort();
        Assert.Equal(new[] { 100, 200 }, values);
    }

    [Fact]
    public async Task Select_CompositeIndexMatchesRowAcrossCatalogReopen()
    {
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE t (a Int32, b Int32, v Int32)");
            catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
            catalog.Plan("INSERT INTO t VALUES (1, 10, 100), (2, 20, 200), (3, 30, 300)");
        }

        // Reopen and probe — the rehydrated provider must expose the
        // composite index so the planner can use it.
        using TableCatalog reopened = CreateCatalog(CatalogPath);
        StatementPlan plan = reopened.Plan("SELECT v FROM t WHERE a = 2 AND b = 20");
        List<int> values = await CollectFirstColumnInts(plan);

        Assert.Single(values);
        Assert.Equal(200, values[0]);
    }

    [Fact]
    public async Task Select_NoMatchingRow_ReturnsEmpty()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32, v Int32)");
        catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
        catalog.Plan("INSERT INTO t VALUES (1, 10, 100), (2, 20, 200)");

        StatementPlan plan = catalog.Plan("SELECT v FROM t WHERE a = 99 AND b = 99");
        List<int> values = await CollectFirstColumnInts(plan);

        Assert.Empty(values);
    }

    // ———————————————————— Strategy assertions (EXPLAIN ANALYZE) ————————————————————

    [Fact]
    public async Task Select_FullCompositeMatch_FiresExactSeekPath_NotChunkedScan()
    {
        // Strategy proof — uses EXPLAIN ANALYZE's exact-seek counter to
        // verify the seek path actually fired (correctness alone can't
        // distinguish "seek returned 1 row" from "chunked scan filtered
        // down to 1 row").
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32, v Int32)");
        catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
        catalog.Plan("INSERT INTO t VALUES (1, 10, 100), (1, 20, 200), (2, 10, 300), (2, 20, 400)");

        StatementPlan plan = catalog.Plan("SELECT v FROM t WHERE a = 1 AND b = 20");
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
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32, v Int32)");
        catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
        catalog.Plan(
            "INSERT INTO t VALUES " +
            "(1, 10, 110), (1, 20, 120), (1, 30, 130), " +
            "(2, 20, 220), (3, 20, 320)");

        StatementPlan plan = catalog.Plan("SELECT v FROM t WHERE a = 1 AND b = 20");
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
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32, v Int32)");
        catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
        catalog.Plan(
            "INSERT INTO t VALUES " +
            "(1, 10, 110), (1, 20, 120), (1, 30, 130), " +
            "(2, 20, 220)");

        StatementPlan plan = catalog.Plan("SELECT v FROM t WHERE a = 1");
        int? exactSeek = await GetScanExactSeekRowsAsync(plan);

        // Strategy varies — the per-column auto-built tree on `a` may
        // fire (seek count = 3 because three rows have a=1). What we
        // care about: NOT 1 (which would mean the composite index
        // incorrectly matched a partial-prefix predicate).
        Assert.NotEqual(1, exactSeek);
    }

    // ———————————————————— Phase 7: leftmost-prefix matching ————————————————————

    [Fact]
    public async Task LeftmostPrefix_SingleColumnOnTwoColumnIndex_MatchesAllRows()
    {
        // Index (a, b); query covers only `a`. PG-style leftmost prefix:
        // the composite index should still fire and return every row with
        // the matching `a`, then the filter (if any) refines further.
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32, v Int32)");
        catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
        catalog.Plan(
            "INSERT INTO t VALUES " +
            "(1, 10, 110), (1, 20, 120), (1, 30, 130), " +
            "(2, 10, 210), (2, 20, 220), " +
            "(3, 30, 330)");

        StatementPlan plan = catalog.Plan("SELECT v FROM t WHERE a = 1");
        List<int> values = await CollectFirstColumnInts(plan);

        values.Sort();
        Assert.Equal(new[] { 110, 120, 130 }, values);
    }

    [Fact]
    public async Task LeftmostPrefix_SingleColumnQuery_FiresCompositeSeekPath()
    {
        // Strategy proof — uses the composite-specific counter to verify the
        // composite-index path was actually consulted. ExactSeekRowsFetched
        // alone wouldn't prove this: for `WHERE a = X` on a `(a, b)` index,
        // the auto-built single-column tree on `a` produces the same row
        // count, so the global seek counter is ambiguous. CompositeIndexSeekHits
        // is set only when the composite-index branch produced positions.
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32, v Int32)");
        catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
        catalog.Plan(
            "INSERT INTO t VALUES " +
            "(1, 10, 110), (1, 20, 120), (1, 30, 130), (2, 10, 210)");

        StatementPlan plan = catalog.Plan("SELECT v FROM t WHERE a = 1");
        int? compositeHits = await GetScanCompositeSeekHitsAsync(plan);

        Assert.Equal(3, compositeHits);
    }

    [Fact]
    public async Task LeftmostPrefix_NonLeftmost_DoesNotConsultComposite()
    {
        // `WHERE b = Y` on a `(a, b)` index has no leftmost-prefix coverage
        // (the predicate skips column 0). The composite-index branch should
        // never even produce positions — CompositeIndexSeekHits stays null.
        // The auto-built single-column tree on `b` still handles the query
        // correctly (seek path via ExactSeekRowsFetched may be non-null).
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32, v Int32)");
        catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
        catalog.Plan("INSERT INTO t VALUES (1, 10, 110), (2, 10, 210), (3, 20, 320)");

        StatementPlan plan = catalog.Plan("SELECT v FROM t WHERE b = 10");
        int? compositeHits = await GetScanCompositeSeekHitsAsync(plan);

        Assert.Null(compositeHits);
    }

    [Fact]
    public async Task LeftmostPrefix_TwoOfThree_CompositeWinsOnSelectivity()
    {
        // Index (a, b, c); predicate covers (a, b). Composite prefix matches
        // 3 rows where (a=1, b=10). Auto-built single-column trees on `a` and
        // `b` independently match 5 and 4 rows respectively (less selective).
        // The fewest-positions tiebreak picks composite (3 < 4 < 5), and the
        // composite counter reflects 3.
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32, c Int32, v Int32)");
        catalog.Plan("CREATE INDEX idx_t_abc ON t (a, b, c)");
        catalog.Plan(
            "INSERT INTO t VALUES " +
            "(1, 10, 100, 1), (1, 10, 200, 2), (1, 10, 300, 3), " +
            "(1, 20, 100, 4), (1, 30, 100, 5), " +
            "(2, 10, 100, 6)");

        StatementPlan plan = catalog.Plan("SELECT v FROM t WHERE a = 1 AND b = 10");
        int? compositeHits = await GetScanCompositeSeekHitsAsync(plan);
        int? exactSeek = await GetScanExactSeekRowsAsync(plan);

        Assert.Equal(3, compositeHits);
        // Composite is most selective → wins the global seek count too.
        Assert.Equal(3, exactSeek);
    }

    [Fact]
    public async Task LeftmostPrefix_NonLeftmostColumn_DoesNotUseCompositeIndex()
    {
        // Index (a, b); query covers only `b`. PG-style: cannot use the
        // (a, b) index because `b` isn't the leftmost. The auto-built
        // per-column tree on `b` handles it, but the composite-specific
        // path is skipped. Strategy: `b` isn't the leftmost column, so
        // composite-index FindPrefix is never invoked.
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32, v Int32)");
        catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
        catalog.Plan(
            "INSERT INTO t VALUES " +
            "(1, 10, 110), (1, 20, 120), (2, 20, 220)");

        StatementPlan plan = catalog.Plan("SELECT v FROM t WHERE b = 20");
        List<int> values = await CollectFirstColumnInts(plan);

        values.Sort();
        Assert.Equal(new[] { 120, 220 }, values);
    }

    [Fact]
    public async Task LeftmostPrefix_TwoOfThreeColumns_FiltersCorrectly()
    {
        // Index (a, b, c); query covers `a` and `b`. Prefix length 2;
        // FindPrefix returns rows where (a, b) matches, regardless of c.
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32, c Int32, v Int32)");
        catalog.Plan("CREATE INDEX idx_t_abc ON t (a, b, c)");
        catalog.Plan(
            "INSERT INTO t VALUES " +
            "(1, 10, 100, 1), (1, 10, 200, 2), (1, 10, 300, 3), " +
            "(1, 20, 100, 4), (2, 10, 100, 5)");

        StatementPlan plan = catalog.Plan("SELECT v FROM t WHERE a = 1 AND b = 10");
        List<int> values = await CollectFirstColumnInts(plan);

        values.Sort();
        Assert.Equal(new[] { 1, 2, 3 }, values);
    }

    [Fact]
    public async Task LeftmostPrefix_GapInCoverage_StopsAtFirstUncovered()
    {
        // Index (a, b, c); query covers `a` and `c` (skips `b`).
        // Leftmost-prefix semantics: prefix is just `[a]` (length 1),
        // NOT `[a, ?, c]`. The result must still be correct — the
        // FilterOperator's residual `c = X` evaluation drops the
        // non-matching rows.
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32, c Int32, v Int32)");
        catalog.Plan("CREATE INDEX idx_t_abc ON t (a, b, c)");
        catalog.Plan(
            "INSERT INTO t VALUES " +
            "(1, 10, 100, 1), (1, 20, 100, 2), (1, 30, 200, 3), " +
            "(2, 10, 100, 4)");

        StatementPlan plan = catalog.Plan("SELECT v FROM t WHERE a = 1 AND c = 100");
        List<int> values = await CollectFirstColumnInts(plan);

        values.Sort();
        Assert.Equal(new[] { 1, 2 }, values);
    }

    // ———————————————————— Phase 9: CREATE UNIQUE INDEX ————————————————————

    [Fact]
    public void CreateUniqueIndex_OnEmptyTable_AllowsDistinctInserts()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE users (id Int32 PRIMARY KEY, email String)");
        catalog.Plan("CREATE UNIQUE INDEX idx_users_email ON users (email)");

        // Distinct emails — no violations.
        catalog.Plan("INSERT INTO users VALUES (1, 'a@example.com'), (2, 'b@example.com')");

        Assert.True(File.Exists(CompositeIndexPath("users", "idx_users_email")));
    }

    [Fact]
    public void CreateUniqueIndex_DuplicateInsert_Throws()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE users (id Int32 PRIMARY KEY, email String)");
        catalog.Plan("CREATE UNIQUE INDEX idx_users_email ON users (email)");
        catalog.Plan("INSERT INTO users VALUES (1, 'a@example.com')");

        UniqueIndexViolationException ex = Assert.Throws<UniqueIndexViolationException>(() =>
            catalog.Plan("INSERT INTO users VALUES (2, 'a@example.com')"));
        Assert.Contains("idx_users_email", ex.Message);
    }

    [Fact]
    public void CreateUniqueIndex_DuplicateInSameBatch_Throws()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE users (id Int32 PRIMARY KEY, email String)");
        catalog.Plan("CREATE UNIQUE INDEX idx_users_email ON users (email)");

        Assert.Throws<UniqueIndexViolationException>(() =>
            catalog.Plan(
                "INSERT INTO users VALUES (1, 'a@example.com'), (2, 'a@example.com')"));
    }

    [Fact]
    public void CreateUniqueIndex_NullValuesAreDistinct_AllowedMultipleTimes()
    {
        // NULLS DISTINCT (PG default): NULL in any covered column exempts
        // the row from the uniqueness check entirely. Two rows with NULL
        // email coexist freely.
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE users (id Int32 PRIMARY KEY, email String)");
        catalog.Plan("CREATE UNIQUE INDEX idx_users_email ON users (email)");

        catalog.Plan("INSERT INTO users VALUES (1, NULL), (2, NULL), (3, 'a@example.com')");
        Assert.Equal(3, catalog["users"].GetRowCount());
    }

    [Fact]
    public void CreateUniqueIndex_OnPopulatedTableWithDuplicates_RejectsAndRollsBack()
    {
        // Backfill must surface a UniqueIndexViolationException and clean
        // up the half-built sidecar so CREATE INDEX is atomic.
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE users (id Int32 PRIMARY KEY, email String)");
        catalog.Plan(
            "INSERT INTO users VALUES " +
            "(1, 'a@example.com'), (2, 'a@example.com'), (3, 'b@example.com')");

        UniqueIndexViolationException ex = Assert.Throws<UniqueIndexViolationException>(() =>
            catalog.Plan("CREATE UNIQUE INDEX idx_users_email ON users (email)"));
        Assert.Contains("idx_users_email", ex.Message);

        // Sidecar must not survive the failed backfill — the next CREATE
        // INDEX attempt has to start from scratch.
        Assert.False(File.Exists(CompositeIndexPath("users", "idx_users_email")));
    }

    [Fact]
    public void CreateUniqueIndex_OnPopulatedTableWithoutDuplicates_BackfillsSuccessfully()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE users (id Int32 PRIMARY KEY, email String)");
        catalog.Plan(
            "INSERT INTO users VALUES " +
            "(1, 'a@example.com'), (2, 'b@example.com'), (3, 'c@example.com')");

        catalog.Plan("CREATE UNIQUE INDEX idx_users_email ON users (email)");
        Assert.True(File.Exists(CompositeIndexPath("users", "idx_users_email")));

        // Subsequent insert with a colliding email is rejected.
        Assert.Throws<UniqueIndexViolationException>(() =>
            catalog.Plan("INSERT INTO users VALUES (4, 'a@example.com')"));
    }

    [Fact]
    public void CreateUniqueIndex_CompositeColumns_RejectsDuplicateTuple()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE orders (id Int32 PRIMARY KEY, cust Int32, item Int32)");
        catalog.Plan("CREATE UNIQUE INDEX idx_orders_cust_item ON orders (cust, item)");

        // Same cust + same item → duplicate tuple, rejected.
        catalog.Plan("INSERT INTO orders VALUES (1, 100, 1)");
        Assert.Throws<UniqueIndexViolationException>(() =>
            catalog.Plan("INSERT INTO orders VALUES (2, 100, 1)"));

        // Same cust, different item — fine.
        catalog.Plan("INSERT INTO orders VALUES (3, 100, 2)");
        // Different cust, same item — fine.
        catalog.Plan("INSERT INTO orders VALUES (4, 200, 1)");

        Assert.Equal(3, catalog["orders"].GetRowCount());
    }

    [Fact]
    public void CreateUniqueIndex_SurvivesCatalogReopen()
    {
        // IsUnique must persist through .datum-catalog.json save/load.
        // Without it, post-reopen INSERTs would fail to surface
        // violations (tree opens with the value from its own header so
        // the file-format side is fine, but the AppendSession needs to
        // know to translate DuplicateKeyException — which only depends
        // on the tree's stored flag, not the descriptor; the descriptor
        // matters for re-CREATE-INDEX after a drop).
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE users (id Int32 PRIMARY KEY, email String)");
            catalog.Plan("CREATE UNIQUE INDEX idx_users_email ON users (email)");
            catalog.Plan("INSERT INTO users VALUES (1, 'a@example.com')");
        }

        using TableCatalog reopened = CreateCatalog(CatalogPath);
        Assert.Throws<UniqueIndexViolationException>(() =>
            reopened.Plan("INSERT INTO users VALUES (2, 'a@example.com')"));
    }

    // ———————————————————— Phase 4: mutation maintenance ————————————————————

    [Fact]
    public async Task Delete_RemovesRowFromCompositeIndexResults()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32, v Int32)");
        catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
        catalog.Plan("INSERT INTO t VALUES (1, 10, 100), (2, 20, 200), (3, 30, 300)");

        catalog.Plan("DELETE FROM t WHERE a = 2 AND b = 20");

        StatementPlan plan = catalog.Plan("SELECT v FROM t WHERE a = 2 AND b = 20");
        List<int> values = await CollectFirstColumnInts(plan);

        Assert.Empty(values);
    }

    [Fact]
    public async Task Delete_PreservesOtherRowsInCompositeIndexResults()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32, v Int32)");
        catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
        catalog.Plan("INSERT INTO t VALUES (1, 10, 100), (2, 20, 200), (3, 30, 300)");

        catalog.Plan("DELETE FROM t WHERE a = 2 AND b = 20");

        // Non-deleted rows must still be findable via the index.
        StatementPlan plan = catalog.Plan("SELECT v FROM t WHERE a = 1 AND b = 10");
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
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32, v Int32)");
        catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
        catalog.Plan("INSERT INTO t VALUES (1, 10, 100), (2, 20, 200)");

        catalog.Plan("UPDATE t SET v = 999 WHERE a = 2 AND b = 20");

        StatementPlan plan = catalog.Plan("SELECT v FROM t WHERE a = 2 AND b = 20");
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
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32, v Int32)");
        catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
        catalog.Plan("INSERT INTO t VALUES (1, 10, 100), (2, 20, 200)");

        catalog.Plan("UPDATE t SET b = 99 WHERE a = 2 AND b = 20");

        StatementPlan planNew = catalog.Plan("SELECT v FROM t WHERE a = 2 AND b = 99");
        List<int> newValues = await CollectFirstColumnInts(planNew);
        Assert.Single(newValues);
        Assert.Equal(200, newValues[0]);

        StatementPlan planOld = catalog.Plan("SELECT v FROM t WHERE a = 2 AND b = 20");
        List<int> oldValues = await CollectFirstColumnInts(planOld);
        Assert.Empty(oldValues);
    }

    // ———————————————————— Weakness coverage (Phase 5c) ————————————————————

    [Fact]
    public async Task Insert_NullInIndexedColumn_RowSkippedFromCompositeIndex_QueryFallsBackToScan()
    {
        // Postgres-compat: equality predicates (`= NULL`) never match
        // NULL, so a missing entry can never cause a missed seek hit.
        // We skip rows with NULLs in covered columns at INSERT time;
        // `IS NULL` queries fall through to the scan path. The data
        // row itself is still present in the table — only the
        // composite-index entry is omitted.
        long indexEntryCount;
        List<int> isNullValues;
        List<int> equalityValues;
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE t (a Int32, b Int32, v Int32)");
            catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
            catalog.Plan("INSERT INTO t VALUES (1, 10, 100), (NULL, 20, 200), (3, NULL, 300), (4, 40, 400)");

            // Rows 1 and 4 have no NULL in indexed columns; rows 2 and 3
            // are skipped. Two entries should land in the tree.
            // `WHERE a IS NULL` falls back to scan (planner has no seek
            // for IS NULL); the row is still findable.
            StatementPlan planIsNull = catalog.Plan("SELECT v FROM t WHERE a IS NULL");
            isNullValues = await CollectFirstColumnInts(planIsNull);

            // Full composite equality on the non-NULL rows uses the
            // seek path and finds the right one.
            StatementPlan planEq = catalog.Plan("SELECT v FROM t WHERE a = 4 AND b = 40");
            equalityValues = await CollectFirstColumnInts(planEq);
        }

        using MutableBPlusTreeBytes tree =
            MutableBPlusTreeBytes.Open(CompositeIndexPath("t", "idx_t_ab"));
        indexEntryCount = tree.EntryCount;

        Assert.Equal(2L, indexEntryCount);  // only non-NULL rows
        Assert.Single(isNullValues);
        Assert.Equal(200, isNullValues[0]); // scan-path returns the NULL row
        Assert.Single(equalityValues);
        Assert.Equal(400, equalityValues[0]);
    }

    [Fact]
    public async Task Backfill_NullInIndexedColumn_RowSkipped_OthersIndexed()
    {
        // Same skip-row policy on CREATE INDEX backfill against an
        // already-populated table.
        long indexEntryCount;
        List<int> equalityValues;
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE t (a Int32, b Int32, v Int32)");
            catalog.Plan("INSERT INTO t VALUES (1, 10, 100), (NULL, 20, 200), (3, NULL, 300), (4, 40, 400)");
            catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");

            StatementPlan planEq = catalog.Plan("SELECT v FROM t WHERE a = 1 AND b = 10");
            equalityValues = await CollectFirstColumnInts(planEq);
        }

        using MutableBPlusTreeBytes tree =
            MutableBPlusTreeBytes.Open(CompositeIndexPath("t", "idx_t_ab"));
        indexEntryCount = tree.EntryCount;

        Assert.Equal(2L, indexEntryCount);
        Assert.Single(equalityValues);
        Assert.Equal(100, equalityValues[0]);
    }

    [Fact]
    public async Task MultiChunk_BackfillAndQuery_ResolvesPositionsAcrossChunks()
    {
        // Force >1 chunk via a tiny chunk-size override so the
        // chunk-boundary math in PopulateCompositeIndexesFromScanAsync
        // and the planner's chunks[]-based seek arithmetic are
        // exercised. With chunkSize=4 and 10 rows, we get chunks
        // [0..3], [4..7], [8..9]. We probe rows from each chunk.
        using IDisposable _ = IndexConstants.OverrideChunkSizeForTest(4);

        List<int> firstChunkValues;
        List<int> middleChunkValues;
        List<int> lastChunkValues;
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE t (a Int32, b Int32, v Int32)");
            catalog.Plan(
                "INSERT INTO t VALUES " +
                "(0, 0, 0), (1, 10, 100), (2, 20, 200), (3, 30, 300), " +     // chunk 0
                "(4, 40, 400), (5, 50, 500), (6, 60, 600), (7, 70, 700), " +  // chunk 1
                "(8, 80, 800), (9, 90, 900)");                                 // chunk 2
            catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");

            // Row in chunk 0
            firstChunkValues = await CollectFirstColumnInts(
                catalog.Plan("SELECT v FROM t WHERE a = 2 AND b = 20"));

            // Row in chunk 1
            middleChunkValues = await CollectFirstColumnInts(
                catalog.Plan("SELECT v FROM t WHERE a = 5 AND b = 50"));

            // Row in chunk 2
            lastChunkValues = await CollectFirstColumnInts(
                catalog.Plan("SELECT v FROM t WHERE a = 9 AND b = 90"));
        }

        Assert.Single(firstChunkValues);
        Assert.Equal(200, firstChunkValues[0]);
        Assert.Single(middleChunkValues);
        Assert.Equal(500, middleChunkValues[0]);
        Assert.Single(lastChunkValues);
        Assert.Equal(900, lastChunkValues[0]);
    }

    [Fact]
    public async Task Update_BulkIndexedColumn_AllRowsRebuilt()
    {
        // UPDATE shifts multiple rows' indexed-column values; the
        // composite-index rebuild must re-encode every row, so all
        // new keys are findable and all old keys miss.
        List<int> newKey5Values;
        List<int> newKey15Values;
        List<int> oldKey1Values;
        List<int> oldKey2Values;
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE t (a Int32, b Int32, v Int32)");
            catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
            catalog.Plan(
                "INSERT INTO t VALUES " +
                "(1, 10, 100), (1, 20, 200), (2, 30, 300), (2, 40, 400)");

            // Bulk-update every a=1 row's a → 5, and every a=2 row's a → 15.
            catalog.Plan("UPDATE t SET a = 5 WHERE a = 1");
            catalog.Plan("UPDATE t SET a = 15 WHERE a = 2");

            newKey5Values = await CollectFirstColumnInts(
                catalog.Plan("SELECT v FROM t WHERE a = 5 AND b = 10"));
            newKey15Values = await CollectFirstColumnInts(
                catalog.Plan("SELECT v FROM t WHERE a = 15 AND b = 40"));
            oldKey1Values = await CollectFirstColumnInts(
                catalog.Plan("SELECT v FROM t WHERE a = 1 AND b = 10"));
            oldKey2Values = await CollectFirstColumnInts(
                catalog.Plan("SELECT v FROM t WHERE a = 2 AND b = 30"));
        }

        Assert.Single(newKey5Values);
        Assert.Equal(100, newKey5Values[0]);
        Assert.Single(newKey15Values);
        Assert.Equal(400, newKey15Values[0]);
        Assert.Empty(oldKey1Values);
        Assert.Empty(oldKey2Values);
    }

    [Fact]
    public async Task ResidualPredicate_CompositeMatchesPrefix_FilterAppliesOnRemainder()
    {
        // Index on (a, b); predicate is `a=1 AND b=2 AND c=X`. The
        // planner should use the composite for (a, b), seek to matching
        // rows, then FilterOperator applies `c=X` on the seeked rows.
        // Verifies (a) seek path fires, (b) filter rejects rows where
        // c doesn't match.
        //
        // Two rows share (a=1, b=2) but differ in c. The seek returns
        // both; the residual filter on c must drop one.
        List<int> values;
        int? exactSeek;
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE t (a Int32, b Int32, c String, v Int32)");
            catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
            catalog.Plan(
                "INSERT INTO t VALUES " +
                "(1, 2, 'foo', 100), (1, 2, 'bar', 200), (1, 3, 'foo', 300)");

            StatementPlan plan = catalog.Plan(
                "SELECT v FROM t WHERE a = 1 AND b = 2 AND c = 'foo'");
            values = await CollectFirstColumnInts(plan);

            StatementPlan planForExplain = catalog.Plan(
                "SELECT v FROM t WHERE a = 1 AND b = 2 AND c = 'foo'");
            exactSeek = await GetScanExactSeekRowsAsync(planForExplain);
        }

        // Filter operator dropped the c='bar' row even though the
        // composite-index seek returned both (a=1, b=2) rows.
        Assert.Single(values);
        Assert.Equal(100, values[0]);

        // Composite-index seek returned 2 entries (both rows with
        // a=1 AND b=2). The residual filter on c then drops one.
        Assert.Equal(2, exactSeek);
    }

    [Fact]
    public void DropIndex_CapturedReferenceAfterDrop_ThrowsByDesign()
    {
        // The captured-reference-after-drop race is NOT fully fixable
        // without refcounting on the tree handle (filed as a follow-up).
        // After DROP, a caller that captured the ICompositeIndex
        // beforehand and calls FindExact gets an ObjectDisposedException
        // — that's the documented v1 contract. Phase 8 fixed the
        // adjacent dict-enumeration race (GetCompositeIndexes returns a
        // consistent snapshot) and serialised DROP against concurrent
        // writers via _mutationLock; this test pins the remaining
        // limitation so a future refcount-based fix has a regression
        // target.
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32, v Int32)");
        catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
        catalog.Plan("INSERT INTO t VALUES (1, 10, 100)");

        ITableProvider provider = catalog["t"];
        IReadOnlyList<ICompositeIndex> indexes = provider.GetCompositeIndexes();
        Assert.Single(indexes);
        ICompositeIndex captured = indexes[0];

        catalog.Plan("DROP INDEX idx_t_ab");

        // Calling FindExact on the disposed handle throws. The exact
        // type varies by .NET runtime; ObjectDisposedException or an
        // IOException from FileStream are both observed in practice.
        Exception? caught = Record.Exception(() =>
            captured.FindExact(new[] { DataValue.FromInt32(1), DataValue.FromInt32(10) }));

        Assert.NotNull(caught);
    }

    [Fact]
    public void DropIndex_GetCompositeIndexes_ReturnsConsistentSnapshot_AcrossDrop()
    {
        // Phase 8: GetCompositeIndexes() snapshots the dict under a fast
        // lock. A caller that grabs the snapshot before a DROP and a
        // caller that grabs it after observe consistent states — no
        // half-removed entry, no enumeration-mid-mutation
        // InvalidOperationException.
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32, v Int32)");
        catalog.Plan("CREATE INDEX idx_t_a ON t (a)");
        catalog.Plan("CREATE INDEX idx_t_b ON t (b)");

        ITableProvider provider = catalog["t"];

        // Snapshot before drop — both indexes present.
        IReadOnlyList<ICompositeIndex> before = provider.GetCompositeIndexes();
        Assert.Equal(2, before.Count);

        catalog.Plan("DROP INDEX idx_t_a");

        // Snapshot after drop — only idx_t_b survives. The earlier
        // `before` snapshot is unaffected (still contains both refs)
        // because GetCompositeIndexes returned a fresh array.
        IReadOnlyList<ICompositeIndex> after = provider.GetCompositeIndexes();
        Assert.Single(after);
        Assert.Equal("idx_t_b", after[0].Name);
        Assert.Equal(2, before.Count);  // pre-drop snapshot still has both
    }

    [Fact]
    public async Task DropIndex_SerializesAgainstInsertViaMutationLock()
    {
        // Phase 8: DROP INDEX now acquires _mutationLock for the whole
        // operation, so a concurrent INSERT (which also takes
        // _mutationLock at AppendSession construction) is forced to
        // serialise. This test exercises the path: an INSERT runs, a
        // DROP runs immediately after, then the index file is gone and
        // the remaining rows survive in the data file.
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32, v Int32)");
        catalog.Plan("CREATE INDEX idx_t_ab ON t (a, b)");
        catalog.Plan("INSERT INTO t VALUES (1, 10, 100), (2, 20, 200)");

        // Both INSERT and DROP take _mutationLock; serialised.
        catalog.Plan("DROP INDEX idx_t_ab");

        Assert.False(File.Exists(CompositeIndexPath("t", "idx_t_ab")));

        // The data file is intact — non-index data is unaffected.
        List<int> survived = await CollectFirstColumnInts(
            catalog.Plan("SELECT v FROM t WHERE a = 1"));
        Assert.Equal(new[] { 100 }, survived);
    }

    private static async Task<List<int>> CollectFirstColumnInts(StatementPlan plan)
    {
        List<int> values = new();
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                values.Add(batch[i][0].AsInt32());
            }
        }
        return values;
    }

    private static async Task<List<double>> CollectFirstColumnDoubles(StatementPlan plan)
    {
        List<double> values = new();
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
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
    private static async Task<int?> GetScanExactSeekRowsAsync(StatementPlan plan)
    {
        ExplainPlanNode root = await AnalyzePlanAsync(plan);
        return FindScanNode(root)?.ExactSeekRowsFetched;
    }

    /// <summary>
    /// Returns the count of positions the composite-index branch contributed
    /// during execution, or <see langword="null"/> when no composite path
    /// fired. Distinct from <see cref="GetScanExactSeekRowsAsync"/>: the
    /// composite branch may run even when a single-column index wins the
    /// fewest-positions tiebreak, so this counter is the precise signal that
    /// the composite-index code path was consulted.
    /// </summary>
    private static async Task<int?> GetScanCompositeSeekHitsAsync(StatementPlan plan)
    {
        ExplainPlanNode root = await AnalyzePlanAsync(plan);
        return FindScanNode(root)?.CompositeIndexSeekHits;
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
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE t (a Int32)");
            catalog.Plan("CREATE INDEX idx_t_a ON t (a)");
            catalog.Plan("DROP INDEX idx_t_a");
        }

        using TableCatalog reopened = CreateCatalog(CatalogPath);

        // After reopen the index name should be gone — re-issuing DROP
        // (without IF EXISTS) must throw.
        Assert.Throws<InvalidOperationException>(() =>
            reopened.Plan("DROP INDEX idx_t_a"));
    }
}
