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
