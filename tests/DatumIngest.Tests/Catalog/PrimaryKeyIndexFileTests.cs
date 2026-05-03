using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Execution;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// PR10h tests covering the on-disk PK index file (<c>.datum-pkindex</c>):
/// lifecycle (created at CREATE TABLE, deleted at DROP TABLE), provider
/// integration (<see cref="ITableProvider.GetPrimaryKeyLookup"/> returns
/// the lookup for single-col PK, null for composite or no PK), and
/// end-to-end persistence of B+Tree-backed PK enforcement across catalog
/// reopen. PR10f's scan-based path remains under
/// <see cref="PrimaryKeyTests"/> for composite PK + InMemory cases.
/// </summary>
public sealed class PrimaryKeyIndexFileTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_pr10h_{Guid.NewGuid():N}");
    private string CatalogPath => Path.Combine(_tempDir, ".datum-catalog.json");
    private string PkIndexPath(string tableName) =>
        Path.Combine(_tempDir, "data", "public", tableName + ".datum-pkindex");

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

    // ──────────────────── Lifecycle: file created at CREATE TABLE ────────────────────

    [Fact]
    public void CreateTable_SingleColPk_PkIndexFileExists()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE users (id Int32 PRIMARY KEY, name String)");

        Assert.True(File.Exists(PkIndexPath("users")),
            "Single-col PK should create the .datum-pkindex sidecar.");
    }

    [Fact]
    public void CreateTable_CompositePk_CreatesBytesPkIndex()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE t (a Int32, b Int32, c String, PRIMARY KEY (a, b))");

        Assert.True(File.Exists(PkIndexPath("t")),
            "Composite PK creates a bytes-keyed .datum-pkindex sidecar (encoder-fed).");
    }

    [Fact]
    public void CreateTable_NoPk_DoesNotCreatePkIndex()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE t (id Int32, name String)");

        Assert.False(File.Exists(PkIndexPath("t")));
    }

    [Fact]
    public void DropTable_RemovesPkIndexFile()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE users (id Int32 PRIMARY KEY, name String)");
        Assert.True(File.Exists(PkIndexPath("users")));

        catalog.Plan("DROP TABLE users");

        Assert.False(File.Exists(PkIndexPath("users")),
            "DROP TABLE should remove .datum-pkindex along with .datum and other sidecars.");
    }

    // ──────────────────── Provider exposes the lookup ────────────────────

    [Fact]
    public void Provider_SingleColPk_GetPrimaryKeyLookupNonNull()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE users (id Int32 PRIMARY KEY, name String)");

        ITableProvider provider = catalog["users"];
        IPrimaryKeyLookup? lookup = provider.GetPrimaryKeyLookup();

        Assert.NotNull(lookup);
    }

    [Fact]
    public void Provider_CompositePk_GetPrimaryKeyLookupNonNull()
    {
        // Post-Phase-4: every PK shape (single-column or composite) routes
        // through the bytes-keyed tree; the provider exposes a non-null
        // lookup whenever the table has a PK and the index file exists.
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE t (a Int32, b Int32, PRIMARY KEY (a, b))");

        ITableProvider provider = catalog["t"];
        Assert.NotNull(provider.GetPrimaryKeyLookup());
    }

    [Fact]
    public void Provider_NoPk_GetPrimaryKeyLookupNull()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE t (id Int32, name String)");

        ITableProvider provider = catalog["t"];
        Assert.Null(provider.GetPrimaryKeyLookup());
    }

    [Fact]
    public void Provider_TempTable_GetPrimaryKeyLookupNull()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32 PRIMARY KEY, name String)");

        ITableProvider provider = catalog["t"];
        Assert.Null(provider.GetPrimaryKeyLookup());
    }

    // ──────────────────── End-to-end: B+Tree-backed PK across reopen ────────────────────

    [Fact]
    public void Insert_LookupBacked_RejectsDuplicate()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE users (id Int32 PRIMARY KEY, name String)");

        catalog.Plan("INSERT INTO users VALUES (1, 'alice'), (2, 'bob')");

        Assert.Throws<PrimaryKeyViolationException>(() =>
            catalog.Plan("INSERT INTO users VALUES (1, 'collision')"));
    }

    [Fact]
    public void Insert_LookupBacked_AcceptsNonColliding()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE users (id Int32 PRIMARY KEY, name String)");

        catalog.Plan("INSERT INTO users VALUES (1, 'alice')");
        catalog.Plan("INSERT INTO users VALUES (2, 'bob')");
        catalog.Plan("INSERT INTO users VALUES (3, 'carol')");

        Assert.Equal(3, catalog["users"].GetRowCount());
    }

    [Fact]
    public void Insert_AcrossCatalogReopen_PkIndexPersists()
    {
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE users (id Int32 PRIMARY KEY, name String)");
            catalog.Plan("INSERT INTO users VALUES (1, 'alice'), (2, 'bob')");
        }

        // Reopen — index file must still be loaded by the provider, and
        // duplicate detection must still fire on the second catalog instance.
        // Statement-form using blocks bound each catalog's lifetime so two
        // catalogs aren't open over the same .datum-pkindex simultaneously
        // (FileShare.None — single-writer per data path).
        using (TableCatalog reopened = CreateCatalog(CatalogPath))
        {
            Assert.Throws<PrimaryKeyViolationException>(() =>
                reopened.Plan("INSERT INTO users VALUES (1, 'still-collides')"));

            reopened.Plan("INSERT INTO users VALUES (3, 'carol')");
        }

        // Reopen once more and confirm the third key is now also tracked.
        using (TableCatalog third = CreateCatalog(CatalogPath))
        {
            Assert.Throws<PrimaryKeyViolationException>(() =>
                third.Plan("INSERT INTO users VALUES (3, 'still-collides')"));
        }
    }

    [Fact]
    public void Insert_ManyRowsInSingleSession_AllPkKeysFlushedToTree()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE users (id Int32 PRIMARY KEY, name String)");

        // 100 rows across multiple INSERT statements — each commit flushes
        // its queued PK keys into the tree.
        for (int i = 0; i < 100; i++)
        {
            catalog.Plan($"INSERT INTO users VALUES ({i}, 'name{i}')");
        }

        Assert.Equal(100, catalog["users"].GetRowCount());

        // Verify every key is rejected as a duplicate.
        for (int i = 0; i < 100; i += 17)
        {
            Assert.Throws<PrimaryKeyViolationException>(() =>
                catalog.Plan($"INSERT INTO users VALUES ({i}, 'dup')"));
        }
    }

    [Fact]
    public void Insert_WithIdentityAndPk_AutoFilledKeysTrackedInIndex()
    {
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE users (id Int32 PRIMARY KEY IDENTITY, name String)");

            // IDENTITY auto-fills id; each row's auto-fill must land in the tree.
            catalog.Plan("INSERT INTO users (name) VALUES ('alice'), ('bob'), ('carol')");

            Assert.Equal(3, catalog["users"].GetRowCount());
        }

        // Reopen + try to manually insert id=1 (which IDENTITY should have
        // assigned to alice). PR10e rejects explicit IDs on IDENTITY columns
        // — that contract is what trips first; the PK collision check would
        // catch it too if IDENTITY allowed it.
        using TableCatalog reopened = CreateCatalog(CatalogPath);

        Assert.Throws<InvalidOperationException>(() =>
            reopened.Plan("INSERT INTO users (id, name) VALUES (1, 'manual')"));
    }
}
