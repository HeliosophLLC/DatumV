using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// PR11c end-to-end tests for the <c>UPDATE</c> executor. Cover plain
/// UPDATE against both <c>InMemoryTableProvider</c> (TEMP TABLE) and
/// <c>DatumFileTableProviderV2</c> (persistent) — predicated and
/// unconditional, multi-column SET, no-match no-op, type coercion,
/// NOT NULL enforcement, expression-based RHS, persistence across
/// catalog reopen.
/// </summary>
public sealed class UpdateExecutorTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_pr11c_{Guid.NewGuid():N}");
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

    // ──────────────────── Plain UPDATE on TEMP TABLE ────────────────────

    [Fact]
    public async Task Update_TempTable_UnconditionalSetAll()
    {
        using TableCatalog catalog = NewMemoryCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, name String)");
        catalog.Plan("INSERT INTO t VALUES (1, 'a'), (2, 'b'), (3, 'c')");

        catalog.Plan("UPDATE t SET name = 'X'");

        var rows = await ScanAsTuples(catalog["t"]);
        Assert.Equal(new[] { (1, "X"), (2, "X"), (3, "X") }, rows);
    }

    [Fact]
    public async Task Update_TempTable_PredicatedSet()
    {
        // Float32-exact literals chosen so the INSERT side's lossless
        // Float64 → Float32 coercion accepts them (0.1 etc. round-trip-fail).
        using TableCatalog catalog = NewMemoryCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, score Float32)");
        catalog.Plan("INSERT INTO t VALUES (1, 0.5), (2, 0.25), (3, 0.125)");

        catalog.Plan("UPDATE t SET score = 1.0 WHERE id = 2");

        var rows = await ScanAsFloatTuples(catalog["t"]);
        Assert.Equal(0.5f, rows[0].Item2);
        Assert.Equal(1.0f, rows[1].Item2);
        Assert.Equal(0.125f, rows[2].Item2);
    }

    [Fact]
    public async Task Update_TempTable_NoMatchIsNoOp()
    {
        using TableCatalog catalog = NewMemoryCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, name String)");
        catalog.Plan("INSERT INTO t VALUES (1, 'a'), (2, 'b')");

        catalog.Plan("UPDATE t SET name = 'X' WHERE id = 999");

        var rows = await ScanAsTuples(catalog["t"]);
        Assert.Equal(new[] { (1, "a"), (2, "b") }, rows);
    }

    [Fact]
    public async Task Update_TempTable_MultiColumnSet()
    {
        using TableCatalog catalog = NewMemoryCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, name String, score Float32)");
        catalog.Plan("INSERT INTO t VALUES (1, 'a', 0.25), (2, 'b', 0.5)");

        catalog.Plan("UPDATE t SET name = 'updated', score = 9.5 WHERE id = 2");

        var rows = await ScanAsThree(catalog["t"]);
        Assert.Equal((1, "a", 0.25f), rows[0]);
        Assert.Equal((2, "updated", 9.5f), rows[1]);
    }

    [Fact]
    public async Task Update_TempTable_ColumnReferenceOnRhs()
    {
        // SET expression references another column on the same row.
        using TableCatalog catalog = NewMemoryCatalog();
        catalog.Plan("CREATE TEMP TABLE t (a Int32, b Int32)");
        catalog.Plan("INSERT INTO t VALUES (1, 10), (2, 20), (3, 30)");

        catalog.Plan("UPDATE t SET a = b WHERE b > 15");

        var rows = await ScanAsIntPairs(catalog["t"]);
        Assert.Equal(new[] { (1, 10), (20, 20), (30, 30) }, rows);
    }

    [Fact]
    public async Task Update_TempTable_ArithmeticExpression()
    {
        using TableCatalog catalog = NewMemoryCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, score Float64)");
        catalog.Plan("INSERT INTO t VALUES (1, 0.1), (2, 0.2)");

        catalog.Plan("UPDATE t SET score = score * 10.0");

        var rows = await ScanAsFloat64(catalog["t"]);
        Assert.Equal(1.0, rows[0].Item2, precision: 5);
        Assert.Equal(2.0, rows[1].Item2, precision: 5);
    }

    // ──────────────────── NOT NULL enforcement ────────────────────

    [Fact]
    public void Update_TempTable_RejectsNullForNotNullColumn()
    {
        using TableCatalog catalog = NewMemoryCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32 NOT NULL, name String)");
        catalog.Plan("INSERT INTO t VALUES (1, 'a')");

        // Build a SET that produces NULL: id (a non-nullable column) is fine to read,
        // but an unconditional NULL on the RHS hits the NOT NULL guard.
        QueryPlanException ex = Assert.Throws<QueryPlanException>(
            () => catalog.Plan("UPDATE t SET id = NULL"));
        Assert.Contains("NOT NULL", ex.Message);
    }

    // ──────────────────── Type coercion ────────────────────

    [Fact]
    public async Task Update_TempTable_NumericWidening()
    {
        using TableCatalog catalog = NewMemoryCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, score Float64)");
        catalog.Plan("INSERT INTO t VALUES (1, 0.0)");

        // Integer literal coerced to Float64.
        catalog.Plan("UPDATE t SET score = 42 WHERE id = 1");

        var rows = await ScanAsFloat64(catalog["t"]);
        Assert.Equal(42.0, rows[0].Item2, precision: 5);
    }

    // ──────────────────── Plain UPDATE on persistent .datum table ────────────────────

    [Fact]
    public async Task Update_DatumFile_UnconditionalSet()
    {
        using (TableCatalog catalog = NewFileCatalog())
        {
            catalog.Plan("CREATE TABLE t (id Int32, name String)");
            catalog.Plan("INSERT INTO t VALUES (1, 'a'), (2, 'b'), (3, 'c')");

            catalog.Plan("UPDATE t SET name = 'X'");

            var rows = await ScanAsTuples(catalog["t"]);
            Assert.Equal(new[] { (1, "X"), (2, "X"), (3, "X") }, rows);
        }
    }

    [Fact]
    public async Task Update_DatumFile_PredicatedSet()
    {
        using (TableCatalog catalog = NewFileCatalog())
        {
            catalog.Plan("CREATE TABLE t (id Int32, score Float64)");
            catalog.Plan("INSERT INTO t VALUES (1, 0.1), (2, 0.2), (3, 0.3)");

            catalog.Plan("UPDATE t SET score = 9.99 WHERE id = 2");

            var rows = await ScanAsFloat64(catalog["t"]);
            Assert.Equal(0.1, rows[0].Item2, precision: 5);
            Assert.Equal(9.99, rows[1].Item2, precision: 5);
            Assert.Equal(0.3, rows[2].Item2, precision: 5);
        }
    }

    [Fact]
    public async Task Update_DatumFile_PersistsAcrossCatalogReopen()
    {
        using (TableCatalog catalog = NewFileCatalog())
        {
            catalog.Plan("CREATE TABLE t (id Int32, name String)");
            catalog.Plan("INSERT INTO t VALUES (1, 'a'), (2, 'b')");
            catalog.Plan("UPDATE t SET name = 'updated' WHERE id = 1");
        }

        using (TableCatalog reopened = NewFileCatalog())
        {
            var rows = await ScanAsTuples(reopened["t"]);
            Assert.Equal(new[] { (1, "updated"), (2, "b") }, rows);
        }
    }

    [Fact]
    public async Task Update_DatumFile_NoMatchLeavesFileUnchanged()
    {
        string filePath = Path.Combine(_tempDir, "t.datum");
        ulong genBefore;
        using (TableCatalog catalog = NewFileCatalog())
        {
            catalog.Plan("CREATE TABLE t (id Int32, name String)");
            catalog.Plan("INSERT INTO t VALUES (1, 'a')");
            genBefore = ReadGeneration(filePath);

            catalog.Plan("UPDATE t SET name = 'x' WHERE id = 999");
        }

        ulong genAfter = ReadGeneration(filePath);
        // No match → no UpdateRows call → no rewrite → no generation bump.
        Assert.Equal(genBefore, genAfter);

        using TableCatalog reopened = NewFileCatalog();
        var rows = await ScanAsTuples(reopened["t"]);
        Assert.Equal(new[] { (1, "a") }, rows);
    }

    [Fact]
    public async Task Update_DatumFile_SetLongStringSpillsToSidecar()
    {
        // Regression: SET to a string longer than the in-memory inline cap
        // (27 bytes) put the new value in workArena. CoerceForUpdate's slow
        // path was reading the bytes back through the batch arena (often
        // empty for tables that don't eager-decode), throwing
        // "Arena[#N] has not been allocated". Reproduces the user-reported
        // failure on `UPDATE messages SET content = '<34 bytes>' WHERE id = 46`.
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (id Int32, content String)");
        catalog.Plan("INSERT INTO t VALUES (1, 'short'), (2, 'short'), (3, 'short')");

        const string longValue = "test test test test test test test"; // 35 bytes UTF-8
        catalog.Plan($"UPDATE t SET content = '{longValue}' WHERE id = 2");

        var rows = await ScanAsTuples(catalog["t"]);
        Assert.Equal(new[] { (1, "short"), (2, longValue), (3, "short") }, rows);
    }

    [Fact]
    public async Task Update_DatumFile_AfterDelete_LiveIndexMaps()
    {
        // Verifies that UPDATE's live-row indexing skips tombstoned rows
        // — DELETE then UPDATE references the live (post-tombstone) index space.
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (id Int32, name String)");
        catalog.Plan("INSERT INTO t VALUES (1, 'a'), (2, 'b'), (3, 'c'), (4, 'd')");
        catalog.Plan("DELETE FROM t WHERE id = 2");

        // Live rows now: (1,'a'), (3,'c'), (4,'d'). Update id = 3 to 'x'.
        catalog.Plan("UPDATE t SET name = 'x' WHERE id = 3");

        var rows = await ScanAsTuples(catalog["t"]);
        Assert.Equal(new[] { (1, "a"), (3, "x"), (4, "d") }, rows);
    }

    // ──────────────────── helpers ────────────────────

    private static ulong ReadGeneration(string datumPath)
    {
        using var reader = DatumIngest.DatumFile.V2.DatumFileReaderV2.Open(datumPath);
        return reader.Footer.Prologue.Generation;
    }

    private static async Task<List<(int, string)>> ScanAsTuples(ITableProvider provider)
    {
        DatumIngest.DatumFile.Sidecar.SidecarRegistry? registry =
            (provider as DatumIngest.Catalog.Providers.DatumFileTableProviderV2)?.SidecarRegistry;
        List<(int, string)> rows = new();
        await foreach (RowBatch batch in provider.ScanAsync(null, null, null, CancellationToken.None))
        {
            try
            {
                Arena arena = batch.Arena;
                for (int r = 0; r < batch.Count; r++)
                {
                    Row row = batch[r];
                    rows.Add((row[0].AsInt32(), row[1].AsString(arena, registry)));
                }
            }
            finally { batch.Dispose(); }
        }
        return rows;
    }

    private static async Task<List<(int, float)>> ScanAsFloatTuples(ITableProvider provider)
    {
        List<(int, float)> rows = new();
        await foreach (RowBatch batch in provider.ScanAsync(null, null, null, CancellationToken.None))
        {
            try
            {
                for (int r = 0; r < batch.Count; r++)
                {
                    Row row = batch[r];
                    rows.Add((row[0].AsInt32(), row[1].AsFloat32()));
                }
            }
            finally { batch.Dispose(); }
        }
        return rows;
    }

    private static async Task<List<(int, double)>> ScanAsFloat64(ITableProvider provider)
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

    private static async Task<List<(int, int)>> ScanAsIntPairs(ITableProvider provider)
    {
        List<(int, int)> rows = new();
        await foreach (RowBatch batch in provider.ScanAsync(null, null, null, CancellationToken.None))
        {
            try
            {
                for (int r = 0; r < batch.Count; r++)
                {
                    Row row = batch[r];
                    rows.Add((row[0].AsInt32(), row[1].AsInt32()));
                }
            }
            finally { batch.Dispose(); }
        }
        return rows;
    }

    private static async Task<List<(int, string, float)>> ScanAsThree(ITableProvider provider)
    {
        List<(int, string, float)> rows = new();
        await foreach (RowBatch batch in provider.ScanAsync(null, null, null, CancellationToken.None))
        {
            try
            {
                Arena arena = batch.Arena;
                for (int r = 0; r < batch.Count; r++)
                {
                    Row row = batch[r];
                    rows.Add((row[0].AsInt32(), row[1].AsString(arena), row[2].AsFloat32()));
                }
            }
            finally { batch.Dispose(); }
        }
        return rows;
    }
}
