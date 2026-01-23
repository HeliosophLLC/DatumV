using DatumIngest.Catalog;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// S3 tests for <c>GENERATED ALWAYS AS</c> (STORED) computed columns —
/// declaration via CREATE TABLE and ALTER TABLE ADD COLUMN, per-row
/// materialisation at INSERT time, rejection of explicit values on
/// INSERT / UPDATE, and persistence across catalog reopen.
/// </summary>
public sealed class ComputedColumnsTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_computed_{Guid.NewGuid():N}");
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

    // ──────────────────── CREATE TABLE shape ────────────────────

    [Fact]
    public void CreateTable_BasicComputedColumn_StoredOnSchema()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE orders (qty Int32, price Float64, total Float64 AS (price * qty))");

        Schema schema = catalog["orders"].GetSchema();
        Assert.Equal(3, schema.Columns.Count);
        Assert.NotNull(schema.Columns[2].ComputedExpression);
        Assert.Null(schema.Columns[0].ComputedExpression);
        Assert.Null(schema.Columns[1].ComputedExpression);
    }

    [Fact]
    public void CreateTable_ComputedWithDefault_Rejects()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE TEMP TABLE t (a Int32, b Int32 DEFAULT 0 AS (a + 1))"));
        Assert.Contains("DEFAULT", ex.Message);
        Assert.Contains("GENERATED", ex.Message);
    }

    [Fact]
    public void CreateTable_ComputedWithIdentity_Rejects()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE TEMP TABLE t (a Int32, b Int64 AS (a + 1) IDENTITY)"));
        Assert.Contains("IDENTITY", ex.Message);
        Assert.Contains("GENERATED", ex.Message);
    }

    // ──────────────────── INSERT materialisation ────────────────────

    [Fact]
    public async Task Insert_OmitComputedColumn_FillsFromExpression()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE orders (qty Int32, price Float64, total Float64 AS (price * qty))");

        catalog.Plan("INSERT INTO orders (qty, price) VALUES (3, 19.99)");

        await foreach (RowBatch batch in catalog["orders"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            Assert.Equal(1, batch.Count);
            Assert.Equal(3, batch[0][0].AsInt32());
            Assert.Equal(19.99, batch[0][1].AsFloat64(), 6);
            Assert.Equal(59.97, batch[0][2].AsFloat64(), 6);  // 3 * 19.99
            batch.Dispose();
        }
    }

    [Fact]
    public void Insert_ExplicitComputedColumn_Rejected()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE orders (qty Int32, price Float64, total Float64 AS (price * qty))");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("INSERT INTO orders (qty, price, total) VALUES (3, 19.99, 100.0)"));
        Assert.Contains("GENERATED ALWAYS AS", ex.Message);
    }

    [Fact]
    public async Task Insert_MultipleRows_AllComputed()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (a Int32, b Int32, sum Int32 AS (a + b))");

        catalog.Plan("INSERT INTO t (a, b) VALUES (1, 2), (10, 20), (100, 200)");

        await foreach (RowBatch batch in catalog["t"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            Assert.Equal(3, batch.Count);
            Assert.Equal(3, batch[0][2].AsInt32());
            Assert.Equal(30, batch[1][2].AsInt32());
            Assert.Equal(300, batch[2][2].AsInt32());
            batch.Dispose();
        }
    }

    [Fact]
    public async Task Insert_ComputedReferencesIdentity_PicksUpAssignedValue()
    {
        // Computed column that references an IDENTITY column should see the
        // post-reservation value (IDENTITY fills before computed evaluation).
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int64 IDENTITY, name String, label String AS ('row-' || cast(id as string)))");

        catalog.Plan("INSERT INTO t (name) VALUES ('alice'), ('bob')");

        await foreach (RowBatch batch in catalog["t"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            Assert.Equal(2, batch.Count);
            Assert.Equal("row-1", batch[0][2].AsString(batch.Arena));
            Assert.Equal("row-2", batch[1][2].AsString(batch.Arena));
            batch.Dispose();
        }
    }

    [Fact]
    public async Task Insert_ComputedReferencesDefault_PicksUpDefaultValue()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (a Int32, b Int32 DEFAULT 10, sum Int32 AS (a + b))");

        catalog.Plan("INSERT INTO t (a) VALUES (5)");

        await foreach (RowBatch batch in catalog["t"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            Assert.Single(Enumerable.Range(0, batch.Count));
            Assert.Equal(15, batch[0][2].AsInt32());
            batch.Dispose();
        }
    }

    // ──────────────────── INSERT … SELECT ────────────────────

    [Fact]
    public async Task InsertSelect_ComputedColumn_PicksUpFromSourceRow()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE src (qty Int32, price Float64)");
        catalog.Plan("CREATE TEMP TABLE dst (qty Int32, price Float64, total Float64 AS (price * qty))");
        catalog.Plan("INSERT INTO src VALUES (2, 5.0), (4, 7.5)");

        catalog.Plan("INSERT INTO dst (qty, price) SELECT qty, price FROM src");

        await foreach (RowBatch batch in catalog["dst"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            Assert.Equal(2, batch.Count);
            Assert.Equal(10.0, batch[0][2].AsFloat64(), 6);
            Assert.Equal(30.0, batch[1][2].AsFloat64(), 6);
            batch.Dispose();
        }
    }

    // ──────────────────── UPDATE rejection ────────────────────

    [Fact]
    public void Update_AssignsComputedColumn_Rejected()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (a Int32, sum Int32 AS (a + 1))");
        catalog.Plan("INSERT INTO t (a) VALUES (1)");

        Exception ex = Assert.ThrowsAny<Exception>(() =>
            catalog.Plan("UPDATE t SET sum = 99"));
        Assert.Contains("computed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────── V2-F1: UPDATE recompute ────────────────────

    [Fact]
    public async Task Update_SetReferencedColumn_RecomputesDependent()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE orders (id Int32, qty Int32, price Float64, total Float64 AS (price * qty))");
        catalog.Plan("INSERT INTO orders (id, qty, price) VALUES (1, 3, 10.0), (2, 5, 4.0)");

        catalog.Plan("UPDATE orders SET price = 20.0 WHERE id = 1");

        Dictionary<int, double> totals = new();
        await foreach (RowBatch batch in catalog["orders"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                totals[batch[r][0].AsInt32()] = batch[r][3].AsFloat64();
            }
            batch.Dispose();
        }

        Assert.Equal(60.0, totals[1], 6);  // 3 * 20.0 (recomputed)
        Assert.Equal(20.0, totals[2], 6);  // 5 * 4.0  (untouched)
    }

    [Fact]
    public async Task Update_MultipleDependentsOnSameSource_AllRecompute()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32, price Float64, low Float64 AS (price * 0.9), high Float64 AS (price * 1.1))");
        catalog.Plan("INSERT INTO t (id, price) VALUES (1, 100.0)");

        catalog.Plan("UPDATE t SET price = 200.0 WHERE id = 1");

        await foreach (RowBatch batch in catalog["t"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            Assert.Equal(1, batch.Count);
            Assert.Equal(180.0, batch[0][2].AsFloat64(), 6);  // 200 * 0.9
            Assert.Equal(220.0, batch[0][3].AsFloat64(), 6);  // 200 * 1.1
            batch.Dispose();
        }
    }

    [Fact]
    public async Task Update_NonReferencedColumn_LeavesComputedUnchanged()
    {
        // 'notes' is not referenced by 'total'. UPDATE on 'notes' should
        // not trigger a recompute of 'total'.
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE orders (id Int32, qty Int32, price Float64, notes String, total Float64 AS (price * qty))");
        catalog.Plan("INSERT INTO orders (id, qty, price, notes) VALUES (1, 3, 10.0, 'one')");

        catalog.Plan("UPDATE orders SET notes = 'changed' WHERE id = 1");

        await foreach (RowBatch batch in catalog["orders"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            Assert.Equal(1, batch.Count);
            Assert.Equal("changed", batch[0][3].AsString(batch.Arena));
            Assert.Equal(30.0, batch[0][4].AsFloat64(), 6);  // unchanged: 3 * 10.0
            batch.Dispose();
        }
    }

    [Fact]
    public async Task Update_RecomputeOnAllMatchingRows()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32, a Int32, b Int32, sum Int32 AS (a + b))");
        catalog.Plan("INSERT INTO t (id, a, b) VALUES (1, 1, 2), (2, 10, 20), (3, 100, 200)");

        // WHERE matches all three rows.
        catalog.Plan("UPDATE t SET a = a * 2");

        Dictionary<int, int> sums = new();
        await foreach (RowBatch batch in catalog["t"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                sums[batch[r][0].AsInt32()] = batch[r][3].AsInt32();
            }
            batch.Dispose();
        }

        Assert.Equal(4, sums[1]);     // 2 + 2
        Assert.Equal(40, sums[2]);    // 20 + 20
        Assert.Equal(400, sums[3]);   // 200 + 200
    }

    [Fact]
    public async Task Update_StringComputed_RecomputesAcrossPersistentReopen()
    {
        // Wide-string computed: exercises the workArena stabilisation path
        // for non-inline existing slots and verifies the recompute writes
        // a fresh String result that lands in workArena (survives past the
        // scan batch's per-batch arena).
        Pool pool = new(new PoolBacking());
        using (TableCatalog catalog = new(pool, CatalogPath))
        {
            catalog.Plan("CREATE TABLE t (id Int32, name String, label String AS ('row-' || name))");
            catalog.Plan("INSERT INTO t (id, name) VALUES (1, 'alice'), (2, 'bob')");
            catalog.Plan("UPDATE t SET name = 'ALICE' WHERE id = 1");
        }

        using TableCatalog reopened = new(pool, CatalogPath);
        Dictionary<int, string> labels = new();
        await foreach (RowBatch batch in reopened["t"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                labels[batch[r][0].AsInt32()] = batch[r][2].AsString(batch.Arena);
            }
            batch.Dispose();
        }

        Assert.Equal("row-ALICE", labels[1]);  // recomputed from new name
        Assert.Equal("row-bob", labels[2]);    // untouched
    }

    [Fact]
    public async Task UpdateFrom_RecomputesDependent()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE orders (id Int32, qty Int32, price Float64, total Float64 AS (price * qty))");
        catalog.Plan("CREATE TEMP TABLE price_updates (order_id Int32, new_price Float64)");
        catalog.Plan("INSERT INTO orders (id, qty, price) VALUES (1, 4, 5.0), (2, 2, 10.0)");
        catalog.Plan("INSERT INTO price_updates VALUES (1, 25.0)");

        catalog.Plan("UPDATE orders SET price = price_updates.new_price FROM price_updates WHERE orders.id = price_updates.order_id");

        Dictionary<int, double> totals = new();
        await foreach (RowBatch batch in catalog["orders"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                totals[batch[r][0].AsInt32()] = batch[r][3].AsFloat64();
            }
            batch.Dispose();
        }

        Assert.Equal(100.0, totals[1], 6);  // 4 * 25.0 (recomputed via FROM)
        Assert.Equal(20.0, totals[2], 6);   // 2 * 10.0 (untouched)
    }

    // ──────────────────── V2-F4: computed-to-computed rejection ────────────────────

    [Fact]
    public void CreateTable_ComputedReferencesComputed_Rejects()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE TEMP TABLE t (a Int32, b Int32 AS (a + 1), c Int32 AS (b * 2))"));
        Assert.Contains("GENERATED", ex.Message);
        Assert.Contains("b", ex.Message);
    }

    [Fact]
    public void AlterAddComputed_ReferencesExistingComputed_Rejects()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (a Int32, b Int32 AS (a + 1))");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("ALTER TABLE t ADD COLUMN c Int32 AS (b * 2)"));
        Assert.Contains("GENERATED", ex.Message);
        Assert.Contains("b", ex.Message);
    }

    // ──────────────────── ALTER TABLE ADD COLUMN ────────────────────

    [Fact]
    public async Task AlterTable_AddComputedColumn_NewInsertsCompute()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (a Int32, b Int32)");
        catalog.Plan("INSERT INTO t VALUES (1, 2), (10, 20)");

        catalog.Plan("ALTER TABLE t ADD COLUMN sum Int32 AS (a + b)");

        // Pre-existing rows read NULL (column wasn't present when they
        // were inserted); new INSERT picks up the computed value.
        catalog.Plan("INSERT INTO t (a, b) VALUES (100, 200)");

        await foreach (RowBatch batch in catalog["t"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            Assert.Equal(3, batch.Count);
            Assert.True(batch[0][2].IsNull);
            Assert.True(batch[1][2].IsNull);
            Assert.Equal(300, batch[2][2].AsInt32());
            batch.Dispose();
        }
    }

    // ──────────────────── Persistent reopen ────────────────────

    [Fact]
    public async Task PersistentTable_ComputedColumn_RoundTripsAcrossReopen()
    {
        Pool pool = new(new PoolBacking());
        using (TableCatalog catalog = new(pool, CatalogPath))
        {
            catalog.Plan("CREATE TABLE orders (qty Int32, price Float64, total Float64 AS (price * qty))");
            catalog.Plan("INSERT INTO orders (qty, price) VALUES (3, 5.0)");
        }

        using TableCatalog reopened = new(pool, CatalogPath);
        Schema schema = reopened["orders"].GetSchema();
        Assert.NotNull(schema.Columns[2].ComputedExpression);

        // Inserting again after reopen should still compute.
        reopened.Plan("INSERT INTO orders (qty, price) VALUES (4, 2.5)");

        await foreach (RowBatch batch in reopened["orders"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            Assert.Equal(2, batch.Count);
            Assert.Equal(15.0, batch[0][2].AsFloat64(), 6);
            Assert.Equal(10.0, batch[1][2].AsFloat64(), 6);
            batch.Dispose();
        }
    }
}
