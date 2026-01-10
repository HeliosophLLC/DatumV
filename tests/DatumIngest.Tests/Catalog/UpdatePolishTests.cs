using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// UPDATE-path polish: no-op detection (skip rewrite when SET produces
/// the same value the cell already holds) + sidecar pass-through (avoid
/// CLR round-trip + sidecar duplication for value-copy SET expressions).
/// Both are pure executor-layer additions on top of PR11c's path.
/// </summary>
public sealed class UpdatePolishTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_pr11_polish_{Guid.NewGuid():N}");
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

    private TableCatalog NewFileCatalog() => new(new Pool(new PoolBacking()), CatalogPath);
    private TableCatalog NewMemoryCatalog() => new(new Pool(new PoolBacking()));

    // ──────────────────── No-op detection ────────────────────

    [Fact]
    public void Update_SetColumnToItself_NoGenerationBump()
    {
        // `UPDATE t SET name = name` evaluates SET against each row, but
        // the new value matches the existing value → no rewrite, no
        // commit, no generation bump.
        string filePath = Path.Combine(_tempDir, "t.datum");
        ulong genBefore;
        using (TableCatalog catalog = NewFileCatalog())
        {
            catalog.Plan("CREATE TABLE t (id Int32, name String)");
            catalog.Plan("INSERT INTO t VALUES (1, 'a'), (2, 'b'), (3, 'c')");
            genBefore = ReadGeneration(filePath);

            catalog.Plan("UPDATE t SET name = name");
        }

        ulong genAfter = ReadGeneration(filePath);
        Assert.Equal(genBefore, genAfter);
    }

    [Fact]
    public void Update_IdempotentLiteral_NoGenerationBump()
    {
        // `SET status = 'pending' WHERE status = 'pending'` is a degenerate
        // but legal pattern (e.g. retry loops). Every matched row's new
        // value equals the existing one → no rewrite.
        string filePath = Path.Combine(_tempDir, "t.datum");
        ulong genBefore;
        using (TableCatalog catalog = NewFileCatalog())
        {
            catalog.Plan("CREATE TABLE t (id Int32, status String)");
            catalog.Plan("INSERT INTO t VALUES (1, 'pending'), (2, 'done'), (3, 'pending')");
            genBefore = ReadGeneration(filePath);

            catalog.Plan("UPDATE t SET status = 'pending' WHERE status = 'pending'");
        }

        Assert.Equal(genBefore, ReadGeneration(filePath));
    }

    [Fact]
    public async Task Update_PartialChange_AppliesOnlyChanged()
    {
        // Multi-column SET where only one column actually changes per
        // row. The unchanged column drops out of the per-row map; the
        // changed column still rewrites. End state: row reflects the
        // single change correctly.
        using TableCatalog catalog = NewMemoryCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, name String, status String)");
        catalog.Plan("INSERT INTO t VALUES (1, 'a', 'open'), (2, 'b', 'open')");

        // SET name to itself, status to a new value. No-op detection
        // drops the name column; status writes through.
        catalog.Plan("UPDATE t SET name = name, status = 'closed' WHERE id = 1");

        var rows = await ScanAsTuples3(catalog["t"]);
        Assert.Equal(new[] { (1, "a", "closed"), (2, "b", "open") }, rows);
    }

    [Fact]
    public void Update_AllNoOps_ProducesNoCommit()
    {
        // Multi-column SET where every column collapses to a no-op for
        // every row. Should produce zero commits.
        string filePath = Path.Combine(_tempDir, "t.datum");
        ulong genBefore;
        using (TableCatalog catalog = NewFileCatalog())
        {
            catalog.Plan("CREATE TABLE t (id Int32, name String, status String)");
            catalog.Plan("INSERT INTO t VALUES (1, 'a', 'open'), (2, 'b', 'open')");
            genBefore = ReadGeneration(filePath);

            catalog.Plan("UPDATE t SET name = name, status = status");
        }

        Assert.Equal(genBefore, ReadGeneration(filePath));
    }

    [Fact]
    public async Task Update_RealChange_StillCommits()
    {
        // Sanity: no-op detection doesn't suppress legitimate updates.
        string filePath = Path.Combine(_tempDir, "t.datum");
        ulong genBefore;
        using (TableCatalog catalog = NewFileCatalog())
        {
            catalog.Plan("CREATE TABLE t (id Int32, name String)");
            catalog.Plan("INSERT INTO t VALUES (1, 'a'), (2, 'b')");
            genBefore = ReadGeneration(filePath);

            catalog.Plan("UPDATE t SET name = 'updated' WHERE id = 1");
        }

        Assert.NotEqual(genBefore, ReadGeneration(filePath));

        using TableCatalog reopened = NewFileCatalog();
        var rows = await ScanAsTuples(reopened["t"]);
        Assert.Equal(new[] { (1, "updated"), (2, "b") }, rows);
    }

    // ──────────────────── Sidecar pass-through ────────────────────

    [Fact]
    public void Update_WideStringValueCopy_NoSidecarGrowth()
    {
        // A wide string lives in the .datum-blob sidecar. `SET col = col`
        // (or any value-copy where source and target columns share kind)
        // must NOT round-trip through CLR + LiteralCoercion — otherwise
        // every matched row would append a duplicate of the bytes to the
        // sidecar. Pass-through preserves the existing pointer so the
        // sidecar size is stable across the no-op UPDATE.
        string filePath = Path.Combine(_tempDir, "t.datum");
        string sidecarPath = Path.ChangeExtension(filePath, ".datum-blob");

        // Strings long enough to spill to sidecar (well above the
        // inline-tier cap).
        string longA = new('a', 200);
        string longB = new('b', 200);

        long sidecarBefore;
        using (TableCatalog catalog = NewFileCatalog())
        {
            catalog.Plan("CREATE TABLE t (id Int32, body String)");
            catalog.Plan($"INSERT INTO t VALUES (1, '{longA}'), (2, '{longB}')");
            sidecarBefore = new FileInfo(sidecarPath).Length;

            // Pure self-copy. No-op detection should skip the rewrite
            // entirely; even if it didn't, sidecar pass-through prevents
            // any duplicate appends.
            catalog.Plan("UPDATE t SET body = body");
        }

        long sidecarAfter = new FileInfo(sidecarPath).Length;
        Assert.Equal(sidecarBefore, sidecarAfter);
    }

    [Fact]
    public async Task Update_WideStringFromSource_NoSidecarDuplication()
    {
        // UPDATE … FROM with a wide-string value-copy: target.body :=
        // source.body. Without sidecar pass-through, the value would
        // round-trip through CLR and produce a duplicate sidecar entry
        // for every matched target row.
        string filePath = Path.Combine(_tempDir, "features.datum");
        string sidecarPath = Path.ChangeExtension(filePath, ".datum-blob");

        string longText = new('x', 200);

        long sidecarBefore;
        using (TableCatalog catalog = NewFileCatalog())
        {
            catalog.Plan("CREATE TABLE features (id Int32, body String)");
            catalog.Plan("CREATE TABLE raw (id Int32, body String)");
            catalog.Plan($"INSERT INTO features VALUES (1, '{longText}'), (2, '{longText}')");
            catalog.Plan($"INSERT INTO raw VALUES (1, '{longText}'), (2, '{longText}')");

            sidecarBefore = new FileInfo(sidecarPath).Length;

            // Value identical for every (target, source) pair → pure
            // no-op. With both optimisations, no rewrite, no append.
            catalog.Plan(
                "UPDATE features SET body = raw.body FROM raw WHERE features.id = raw.id");
        }

        long sidecarAfter = new FileInfo(sidecarPath).Length;
        Assert.Equal(sidecarBefore, sidecarAfter);

        // Row count intact across reopen — wide-string body resolution
        // requires the sidecar registry, so we check via the inline id
        // column only (sufficient signal that the rows weren't dropped).
        using TableCatalog reopened = NewFileCatalog();
        List<int> ids = new();
        await foreach (RowBatch batch in reopened["features"]
            .ScanAsync(null, null, null, CancellationToken.None))
        {
            try
            {
                for (int r = 0; r < batch.Count; r++)
                {
                    ids.Add(batch[r][0].AsInt32());
                }
            }
            finally { batch.Dispose(); }
        }
        Assert.Equal(new[] { 1, 2 }, ids);
    }

    // ──────────────────── helpers ────────────────────

    private static ulong ReadGeneration(string datumPath)
    {
        using var reader = DatumIngest.DatumFile.V2.DatumFileReaderV2.Open(datumPath);
        return reader.Footer.Prologue.Generation;
    }

    private static async Task<List<(int, string)>> ScanAsTuples(ITableProvider provider)
    {
        List<(int, string)> rows = new();
        await foreach (RowBatch batch in provider.ScanAsync(null, null, null, CancellationToken.None))
        {
            try
            {
                Arena arena = batch.Arena;
                for (int r = 0; r < batch.Count; r++)
                {
                    Row row = batch[r];
                    rows.Add((row[0].AsInt32(), row[1].AsString(arena)));
                }
            }
            finally
            {
                batch.Dispose();
            }
        }
        return rows;
    }

    private static async Task<List<(int, string, string)>> ScanAsTuples3(ITableProvider provider)
    {
        List<(int, string, string)> rows = new();
        await foreach (RowBatch batch in provider.ScanAsync(null, null, null, CancellationToken.None))
        {
            try
            {
                Arena arena = batch.Arena;
                for (int r = 0; r < batch.Count; r++)
                {
                    Row row = batch[r];
                    rows.Add((row[0].AsInt32(), row[1].AsString(arena), row[2].AsString(arena)));
                }
            }
            finally
            {
                batch.Dispose();
            }
        }
        return rows;
    }
}
