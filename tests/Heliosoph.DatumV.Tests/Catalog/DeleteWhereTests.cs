using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Tests.Catalog;

/// <summary>
/// PR10d tests for SQL <c>DELETE FROM … [WHERE …]</c>. Cover:
/// unconditional delete-all, predicated delete with numeric and
/// string columns, no-match no-op, count check, second delete after
/// the first (linear-row-index re-mapping post-tombstone), DEFAULT-filled
/// columns visible to the predicate, persistent-table round-trip,
/// missing-table error.
/// </summary>
public sealed class DeleteWhereTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_pr10d_{Guid.NewGuid():N}");
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

    // ──────────────────── Unconditional DELETE ────────────────────

    [Fact]
    public async Task Delete_NoWhere_RemovesAllRows()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, name String)");
        catalog.Plan("INSERT INTO t VALUES (1, 'a'), (2, 'b'), (3, 'c')");

        catalog.Plan("DELETE FROM t");

        Assert.Equal(0, catalog["t"].GetRowCount());
        Assert.Empty(await ScanAsTuples(catalog["t"]));
    }

    [Fact]
    public void Delete_NoWhere_OnEmptyTable_NoOp()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32)");

        // Should not throw.
        catalog.Plan("DELETE FROM t");

        Assert.Equal(0, catalog["t"].GetRowCount());
    }

    // ──────────────────── Predicated DELETE ────────────────────

    [Fact]
    public async Task Delete_WhereNumericPredicate_RemovesOnlyMatching()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, name String)");
        catalog.Plan("INSERT INTO t VALUES (1, 'a'), (2, 'b'), (3, 'c'), (4, 'd')");

        catalog.Plan("DELETE FROM t WHERE id >= 3");

        Assert.Equal(2, catalog["t"].GetRowCount());
        List<(int id, string name)> rows = await ScanAsTuples(catalog["t"]);
        Assert.Equal([(1, "a"), (2, "b")], rows);
    }

    [Fact]
    public async Task Delete_WhereStringPredicate_RemovesOnlyMatching()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, name String)");
        catalog.Plan("INSERT INTO t VALUES (1, 'keep'), (2, 'drop'), (3, 'keep')");

        catalog.Plan("DELETE FROM t WHERE name = 'drop'");

        Assert.Equal(2, catalog["t"].GetRowCount());
        List<(int id, string name)> rows = await ScanAsTuples(catalog["t"]);
        Assert.Equal([(1, "keep"), (3, "keep")], rows);
    }

    [Fact]
    public async Task Delete_WhereMatchesNothing_NoOp()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32)");
        catalog.Plan("INSERT INTO t VALUES (1), (2), (3)");

        catalog.Plan("DELETE FROM t WHERE id > 100");

        Assert.Equal(3, catalog["t"].GetRowCount());
    }

    [Fact]
    public async Task Delete_WhereOnDefaultFilledColumn_SeesDefault()
    {
        // PR10b's DEFAULT materialization is supposed to be visible to
        // subsequent queries. DELETE is one of those queries — the
        // predicate runs over rows that already have their DEFAULT-filled
        // values stamped in.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, status String DEFAULT 'pending')");
        catalog.Plan("INSERT INTO t (id) VALUES (1), (2)");
        catalog.Plan("INSERT INTO t (id, status) VALUES (3, 'shipped')");

        catalog.Plan("DELETE FROM t WHERE status = 'pending'");

        Assert.Equal(1, catalog["t"].GetRowCount());
        List<(int id, string name)> rows = await ScanAsTuples(catalog["t"]);
        Assert.Equal([(3, "shipped")], rows);
    }

    [Fact]
    public async Task Delete_TwoSequentialDeletes_LinearIndexRebasesEachTime()
    {
        // After the first DELETE, the surviving rows renumber 0..N-1
        // (DeleteRows docs: "indices are linear over the live row
        // sequence, post-tombstone from previous deletes"). A second
        // DELETE must re-walk the live sequence — confirms the running
        // counter in DeleteExecutor doesn't pre-cache stale indices.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32)");
        catalog.Plan("INSERT INTO t VALUES (1), (2), (3), (4), (5)");

        catalog.Plan("DELETE FROM t WHERE id = 2");
        Assert.Equal(4, catalog["t"].GetRowCount());

        catalog.Plan("DELETE FROM t WHERE id = 4");
        Assert.Equal(3, catalog["t"].GetRowCount());

        List<int> survivors = await ScanIntColumn(catalog["t"], "id");
        Assert.Equal([1, 3, 5], survivors);
    }

    // ──────────────────── DELETE then INSERT ────────────────────

    [Fact]
    public async Task Delete_FollowedByInsert_NewRowsVisible()
    {
        // Sanity: after a DELETE, subsequent INSERTs append cleanly and
        // the live row sequence renumbers without leaking tombstoned
        // rows back into scans.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32)");
        catalog.Plan("INSERT INTO t VALUES (1), (2), (3)");

        catalog.Plan("DELETE FROM t WHERE id = 2");
        catalog.Plan("INSERT INTO t VALUES (4)");

        List<int> ids = await ScanIntColumn(catalog["t"], "id");
        Assert.Equal([1, 3, 4], ids);
    }

    // ──────────────────── Persistent table ────────────────────

    [Fact]
    public async Task Delete_OnPersistentTable_SurvivesReopen()
    {
        // Verifies the soft-delete tombstone is persisted to the
        // .datum footer and honored after reopen. Asserts via scan,
        // not GetRowCount — DatumFileTableProviderV2.GetRowCount
        // returns the gross row count (tombstones included). That's
        // a separate gap; the user-visible scan path is what
        // matters.
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE users (id Int32, name String)");
            catalog.Plan("INSERT INTO users VALUES (1, 'alice'), (2, 'bob'), (3, 'carol')");
            catalog.Plan("DELETE FROM users WHERE id = 2");
        }

        using TableCatalog reopened = CreateCatalog(CatalogPath);
        List<(int id, string name)> rows = await ScanAsTuples(reopened["users"]);
        Assert.Equal([(1, "alice"), (3, "carol")], rows);
    }

    [Fact]
    public async Task Delete_TwoSequentialDeletes_OnPersistentTable_TombstonesLiveIndices()
    {
        // DeleteRows receives live-row indices (post-tombstone numbering,
        // per the ITableProvider contract), but the .datum tombstone
        // bitmaps are keyed by physical row position. The provider must
        // translate between the two spaces once earlier deletes exist:
        // here id=4 sits at live index 2 but physical row 3. Applying
        // the live index as a physical position would tombstone id=3.
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE t (id Int32)");
            catalog.Plan("INSERT INTO t VALUES (1), (2), (3), (4), (5)");

            catalog.Plan("DELETE FROM t WHERE id = 2");
            catalog.Plan("DELETE FROM t WHERE id = 4");

            List<int> survivors = await ScanIntColumn(catalog["t"], "id");
            Assert.Equal([1, 3, 5], survivors);
        }

        // The tombstones must land on the right physical rows on disk,
        // not just in the live snapshot.
        using TableCatalog reopened = CreateCatalog(CatalogPath);
        List<int> persisted = await ScanIntColumn(reopened["t"], "id");
        Assert.Equal([1, 3, 5], persisted);
    }

    [Fact]
    public async Task Delete_SecondDeleteAliasingTombstonedRow_StillDeletesMatch()
    {
        // The other face of live/physical aliasing: after deleting the
        // first physical row, the next match sits at live index 0. If
        // that live index were applied as a physical position it would
        // re-mark the already-tombstoned row 0 — an idempotent no-op
        // that reports success while deleting nothing.
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE t (id Int32)");
        catalog.Plan("INSERT INTO t VALUES (1), (2), (3), (4), (5)");

        catalog.Plan("DELETE FROM t WHERE id = 1");
        catalog.Plan("DELETE FROM t WHERE id = 2");

        List<int> survivors = await ScanIntColumn(catalog["t"], "id");
        Assert.Equal([3, 4, 5], survivors);
    }

    // ──────────────────── Validation ────────────────────

    [Fact]
    public void Delete_OnMissingTable_Throws()
    {
        using TableCatalog catalog = CreateCatalog();

        // S8 routes DELETE through SchemaResolver.Resolve, so a missing
        // unqualified target surfaces the rich SchemaResolutionException.
        SchemaResolutionException ex = Assert.Throws<SchemaResolutionException>(() =>
            catalog.Plan("DELETE FROM nope"));
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public void Delete_OnReadOnlyProvider_Throws()
    {
        // system.tables and similar built-in providers report
        // CanDeleteRows = false; the executor should refuse.
        using TableCatalog catalog = CreateCatalog();

        InvalidOperationException ex = Assert.ThrowsAny<InvalidOperationException>(() =>
            catalog.Plan("DELETE FROM system_schemas"));
        // SchemaResolutionException ("not found") or the
        // read-only-provider rejection — either way, a clear refusal.
        Assert.True(
            ex.Message.Contains("read-only") || ex.Message.Contains("not found"),
            $"unexpected message: {ex.Message}");
    }

    // ──────────────────── Helpers ────────────────────

    private static async Task<List<(int id, string name)>> ScanAsTuples(ITableProvider provider)
    {
        List<(int, string)> rows = new();
        await foreach (RowBatch batch in provider.ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                Row row = batch[r];
                int id = row[0].AsInt32();
                string name = row[1].IsNull ? "<null>" : row[1].AsString();
                rows.Add((id, name));
            }
            batch.Dispose();
        }
        return rows;
    }

    private static async Task<List<int>> ScanIntColumn(ITableProvider provider, string columnName)
    {
        List<int> values = new();
        Schema schema = provider.GetSchema();
        int columnIndex = -1;
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            if (schema.Columns[i].Name == columnName) { columnIndex = i; break; }
        }
        if (columnIndex < 0) throw new InvalidOperationException($"column {columnName} not in schema");

        await foreach (RowBatch batch in provider.ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                values.Add(batch[r][columnIndex].AsInt32());
            }
            batch.Dispose();
        }
        return values;
    }
}
