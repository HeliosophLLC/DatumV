using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Execution;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// End-to-end integration tests for composite PRIMARY KEY enforcement
/// backed by the bytes-keyed B+Tree. Composite PKs were previously
/// rejected at CREATE TABLE time (16-byte cap) or fell back to a
/// scan-based pre-load; the bytes tree + CompositeKeyEncoder unlock
/// proper O(log n) lookup-backed enforcement for arbitrary kinds.
/// </summary>
public sealed class CompositePrimaryKeyTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_composite_pk_{Guid.NewGuid():N}");
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

    // ───── Two-column composite PK ─────

    [Fact]
    public void TwoColumnComposite_DistinctRows_AllAccepted()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = new(pool, CatalogPath);
        catalog.Plan("CREATE TABLE t (a Int32, b String, c Int64, PRIMARY KEY (a, b))");

        catalog.Plan("INSERT INTO t VALUES (1, 'alpha', 100), (1, 'beta', 200), (2, 'alpha', 300)");

        Assert.Equal(3, catalog["t"].GetRowCount());
    }

    [Fact]
    public void TwoColumnComposite_DuplicateTuple_Rejected()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = new(pool, CatalogPath);
        catalog.Plan("CREATE TABLE t (a Int32, b String, PRIMARY KEY (a, b))");

        catalog.Plan("INSERT INTO t VALUES (1, 'alpha'), (2, 'beta')");

        Assert.Throws<PrimaryKeyViolationException>(() =>
            catalog.Plan("INSERT INTO t VALUES (1, 'alpha')"));
    }

    [Fact]
    public void TwoColumnComposite_PartialOverlap_NotADuplicate()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = new(pool, CatalogPath);
        catalog.Plan("CREATE TABLE t (a Int32, b String, PRIMARY KEY (a, b))");

        catalog.Plan("INSERT INTO t VALUES (1, 'alpha')");
        // (1, 'beta') shares the first column but not the tuple — accepted.
        catalog.Plan("INSERT INTO t VALUES (1, 'beta')");
        // (2, 'alpha') shares the second column — accepted.
        catalog.Plan("INSERT INTO t VALUES (2, 'alpha')");

        Assert.Equal(3, catalog["t"].GetRowCount());
    }

    [Fact]
    public void TwoColumnComposite_WithinBatchDuplicate_Rejected()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = new(pool, CatalogPath);
        catalog.Plan("CREATE TABLE t (a Int32, b String, PRIMARY KEY (a, b))");

        Assert.Throws<PrimaryKeyViolationException>(() =>
            catalog.Plan("INSERT INTO t VALUES (1, 'alpha'), (2, 'beta'), (1, 'alpha')"));
    }

    // ───── Three-column composite with mixed kinds ─────

    [Fact]
    public void ThreeColumnComposite_MixedKinds_Works()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = new(pool, CatalogPath);
        catalog.Plan(
            "CREATE TABLE t (id Int64, day Date, tag Uuid, value Float64, " +
            "PRIMARY KEY (id, day, tag))");

        catalog.Plan(
            "INSERT INTO t VALUES " +
            "(1, '2024-01-01', '00000000-0000-0000-0000-000000000001', 1.5), " +
            "(1, '2024-01-01', '00000000-0000-0000-0000-000000000002', 2.5), " +
            "(1, '2024-01-02', '00000000-0000-0000-0000-000000000001', 3.5), " +
            "(2, '2024-01-01', '00000000-0000-0000-0000-000000000001', 4.5)");

        Assert.Equal(4, catalog["t"].GetRowCount());

        Assert.Throws<PrimaryKeyViolationException>(() =>
            catalog.Plan("INSERT INTO t VALUES " +
                "(1, '2024-01-01', '00000000-0000-0000-0000-000000000001', 99.9)"));
    }

    // ───── COCO-style filename PK (single-column long String) ─────

    [Fact]
    public void SingleColumnStringPk_LongFilename_Works()
    {
        // The COCO2017 filename motivation: PRIMARY KEY on a 25-byte
        // ASCII filename. Previously rejected by the 16-byte cap; now
        // routes through the bytes-keyed tree.
        Pool pool = CreatePool();
        using TableCatalog catalog = new(pool, CatalogPath);
        catalog.Plan("CREATE TABLE images (filename String PRIMARY KEY, width Int32, height Int32)");

        catalog.Plan(
            "INSERT INTO images VALUES " +
            "('test2017/000000290550.jpg', 640, 480), " +
            "('test2017/000000290551.jpg', 800, 600), " +
            "('test2017/000000290552.jpg', 1024, 768)");

        Assert.Equal(3, catalog["images"].GetRowCount());

        Assert.Throws<PrimaryKeyViolationException>(() =>
            catalog.Plan(
                "INSERT INTO images VALUES ('test2017/000000290551.jpg', 9999, 9999)"));
    }

    // ───── Persistence across reopen ─────

    [Fact]
    public void CompositePk_PersistsAcrossReopen()
    {
        Pool pool = CreatePool();

        using (TableCatalog catalog = new(pool, CatalogPath))
        {
            catalog.Plan("CREATE TABLE t (a Int32, b String, PRIMARY KEY (a, b))");
            catalog.Plan("INSERT INTO t VALUES (1, 'alpha'), (2, 'beta')");
        }

        // Reopen — composite PK index must be loaded and enforce.
        using TableCatalog reopened = new(pool, CatalogPath);

        Assert.Throws<PrimaryKeyViolationException>(() =>
            reopened.Plan("INSERT INTO t VALUES (1, 'alpha')"));

        reopened.Plan("INSERT INTO t VALUES (3, 'gamma')");
        Assert.Equal(3, reopened["t"].GetRowCount());
    }

    [Fact]
    public void SingleColumnStringPk_PersistsAcrossReopen()
    {
        Pool pool = CreatePool();

        using (TableCatalog catalog = new(pool, CatalogPath))
        {
            catalog.Plan("CREATE TABLE t (id String PRIMARY KEY, value Int32)");
            catalog.Plan("INSERT INTO t VALUES ('test2017/000000290551.jpg', 42)");
        }

        using TableCatalog reopened = new(pool, CatalogPath);
        Assert.Throws<PrimaryKeyViolationException>(() =>
            reopened.Plan("INSERT INTO t VALUES ('test2017/000000290551.jpg', 99)"));
    }

    // ───── Scrambled insert order ─────

    [Fact]
    public void CompositePk_ScrambledOrder_AllLookupsHit()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = new(pool, CatalogPath);
        catalog.Plan("CREATE TABLE t (a Int32, b String, PRIMARY KEY (a, b))");

        // Insert tuples in random-ish order, then probe each.
        int[] aValues = { 5, 1, 3, 4, 2 };
        string[] bValues = { "zebra", "alpha", "delta", "echo", "beta" };
        for (int i = 0; i < aValues.Length; i++)
        {
            catalog.Plan($"INSERT INTO t VALUES ({aValues[i]}, '{bValues[i]}')");
        }
        Assert.Equal(5, catalog["t"].GetRowCount());

        // Probe via duplicate-rejection.
        for (int i = 0; i < aValues.Length; i++)
        {
            int a = aValues[i];
            string b = bValues[i];
            Assert.Throws<PrimaryKeyViolationException>(() =>
                catalog.Plan($"INSERT INTO t VALUES ({a}, '{b}')"));
        }
    }

    // ────────────────────── QA probes ──────────────────────

    [Fact]
    public void Probe_UpdateOnCompositePkColumn_Rejected()
    {
        // UPDATE on any PK column is rejected at validation time —
        // PK columns are immutable, callers must DELETE + INSERT.
        // Confirms the rule fires for composite PK component too.
        Pool pool = CreatePool();
        using TableCatalog catalog = new(pool, CatalogPath);
        catalog.Plan("CREATE TABLE t (a Int32, b String, payload Int64, PRIMARY KEY (a, b))");
        catalog.Plan("INSERT INTO t VALUES (1, 'alpha', 100)");

        // Update a PK component → reject
        Exception exA = Assert.ThrowsAny<Exception>(() =>
            catalog.Plan("UPDATE t SET a = 2 WHERE b = 'alpha'"));
        Assert.Contains("PRIMARY KEY", exA.Message, StringComparison.OrdinalIgnoreCase);

        Exception exB = Assert.ThrowsAny<Exception>(() =>
            catalog.Plan("UPDATE t SET b = 'beta' WHERE a = 1"));
        Assert.Contains("PRIMARY KEY", exB.Message, StringComparison.OrdinalIgnoreCase);

        // Updating a non-PK column is fine.
        catalog.Plan("UPDATE t SET payload = 999 WHERE a = 1");
        Assert.Equal(1, catalog["t"].GetRowCount());
    }

    [Fact]
    public void Probe_ReindexOnCompositePkTable_DoesNotBreakPkEnforcement()
    {
        // REINDEX rebuilds the .datum-index acceleration sidecar; it
        // does NOT rebuild .datum-pkindex. After REINDEX, composite PK
        // enforcement must still work (the bytes tree's tree handle
        // must survive the rebuild flow).
        Pool pool = CreatePool();
        using TableCatalog catalog = new(pool, CatalogPath);
        catalog.Plan("CREATE TABLE t (a Int32, b String, payload Int64, PRIMARY KEY (a, b))");
        catalog.Plan("INSERT INTO t VALUES (1, 'alpha', 100), (2, 'beta', 200)");

        catalog.Plan("REINDEX TABLE t");

        // Existing rows still trigger duplicate rejection.
        Assert.Throws<PrimaryKeyViolationException>(() =>
            catalog.Plan("INSERT INTO t VALUES (1, 'alpha', 999)"));

        // New rows still get tracked.
        catalog.Plan("INSERT INTO t VALUES (3, 'gamma', 300)");
        Assert.Equal(3, catalog["t"].GetRowCount());
        Assert.Throws<PrimaryKeyViolationException>(() =>
            catalog.Plan("INSERT INTO t VALUES (3, 'gamma', 999)"));
    }

    [Fact]
    public void Probe_NullInCompositePkComponent_Rejected_PersistentTable()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = new(pool, CatalogPath);
        catalog.Plan("CREATE TABLE t (a Int32, b String, PRIMARY KEY (a, b))");

        // Set b to NULL via a column-list INSERT with NULL literal.
        // Nullable check on PK columns must fire.
        Exception ex = Assert.ThrowsAny<Exception>(() =>
            catalog.Plan("INSERT INTO t (a, b) VALUES (1, NULL)"));
        Assert.Contains("NULL", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Probe_NullInCompositePkComponent_Rejected_TempTable()
    {
        // TEMP table → InMemoryProvider falls back to scan-path
        // PrimaryKeyChecker (no on-disk lookup). NULL must reject there too.
        Pool pool = CreatePool();
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (a Int32, b String, PRIMARY KEY (a, b))");

        Exception ex = Assert.ThrowsAny<Exception>(() =>
            catalog.Plan("INSERT INTO t (a, b) VALUES (1, NULL)"));
        Assert.Contains("NULL", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Probe_InsertSelectIntoCompositePk_EnforcesUniqueness()
    {
        // INSERT … SELECT is a different code path than VALUES (streams
        // batches from the source plan instead of building one in memory).
        // Composite PK enforcement must still work.
        Pool pool = CreatePool();
        using TableCatalog catalog = new(pool, CatalogPath);
        catalog.Plan("CREATE TABLE src (a Int32, b String, payload Int64)");
        catalog.Plan(
            "INSERT INTO src VALUES " +
            "(1, 'alpha', 100), (2, 'beta', 200), (1, 'alpha', 999)");  // last row collides

        catalog.Plan("CREATE TABLE dst (a Int32, b String, payload Int64, PRIMARY KEY (a, b))");

        // INSERT … SELECT must reject the within-batch duplicate (1, 'alpha').
        Assert.Throws<PrimaryKeyViolationException>(() =>
            catalog.Plan("INSERT INTO dst SELECT a, b, payload FROM src"));
    }

    [Fact]
    public void Probe_InsertSelectIntoCompositePk_DistinctRows_AllAccepted()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = new(pool, CatalogPath);
        catalog.Plan("CREATE TABLE src (a Int32, b String, payload Int64)");
        catalog.Plan(
            "INSERT INTO src VALUES " +
            "(1, 'alpha', 100), (2, 'beta', 200), (3, 'gamma', 300)");

        catalog.Plan("CREATE TABLE dst (a Int32, b String, payload Int64, PRIMARY KEY (a, b))");
        catalog.Plan("INSERT INTO dst SELECT a, b, payload FROM src");

        Assert.Equal(3, catalog["dst"].GetRowCount());

        // Cross-batch duplicate via another INSERT … SELECT must reject.
        Assert.Throws<PrimaryKeyViolationException>(() =>
            catalog.Plan("INSERT INTO dst SELECT a, b, payload FROM src"));
    }

    [Fact]
    public void Probe_CrossFormatFileOpen_TypedAsBytes_Throws()
    {
        // Typed-tree file (PKBT magic) opened as a bytes tree must throw.
        // Covered already in MutableBPlusTreeBytesFormatTests but re-stated
        // here for the composite-PK QA narrative.
        string typedPath = Path.Combine(_tempDir, "typed.datum-pkindex");
        using (DatumIngest.Indexing.BTree.Mutable.MutableBPlusTree typedTree =
            DatumIngest.Indexing.BTree.Mutable.MutableBPlusTree.Create(typedPath, DatumIngest.Model.DataKind.Int32))
        {
            // Just create an empty typed tree.
        }

        Assert.Throws<InvalidDataException>(() =>
            DatumIngest.Indexing.BTree.MutableBytes.MutableBPlusTreeBytes.Open(typedPath));
    }

    [Fact]
    public void Probe_CrossFormatFileOpen_BytesAsTyped_Throws()
    {
        // Bytes-tree file (BKBT magic) opened as a typed tree must throw.
        // The reverse direction — the other half of the firewall.
        string bytesPath = Path.Combine(_tempDir, "bytes.datum-pkindex");
        using (DatumIngest.Indexing.BTree.MutableBytes.MutableBPlusTreeBytes bytesTree =
            DatumIngest.Indexing.BTree.MutableBytes.MutableBPlusTreeBytes.Create(bytesPath))
        {
            // Just create an empty bytes tree.
        }

        Assert.Throws<InvalidDataException>(() =>
            DatumIngest.Indexing.BTree.Mutable.MutableBPlusTree.Open(bytesPath));
    }
}
