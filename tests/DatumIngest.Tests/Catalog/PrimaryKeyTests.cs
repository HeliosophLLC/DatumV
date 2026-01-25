using DatumIngest.Catalog;
using DatumIngest.DatumFile.V2;
using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// PR10f tests for SQL <c>PRIMARY KEY</c> enforcement. Cover:
/// column-level + table-level PK declarations; PK auto-promotes
/// to NOT NULL; total-key-size cap (16 bytes); duplicate rejection on
/// VALUES, SELECT, and within-batch; NULL-in-PK rejection; reopen
/// preserves the PK constraint; PK + IDENTITY interplay; ALTER DROP
/// COLUMN of a PK column rejected.
/// </summary>
public sealed class PrimaryKeyTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_pr10f_{Guid.NewGuid():N}");
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
    public void CreateTempTable_ColumnLevelPrimaryKey_SchemaCarriesPk()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);

        catalog.Plan("CREATE TEMP TABLE t (id Int32 PRIMARY KEY, name String)");

        Schema schema = catalog["t"].GetSchema();
        Assert.Single(schema.PrimaryKeyColumnIndices);
        Assert.Equal(0, schema.PrimaryKeyColumnIndices[0]);
        Assert.True(schema.Columns[0].IsPrimaryKey);
        Assert.False(schema.Columns[0].Nullable, "PK columns are implicitly NOT NULL.");
        Assert.False(schema.Columns[1].IsPrimaryKey);
    }

    [Fact]
    public void CreateTempTable_TableLevelCompositePrimaryKey_PreservesOrder()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);

        // Table-level PK lists columns in (b, a) order — order matters
        // for the index key and must be preserved.
        catalog.Plan("CREATE TEMP TABLE t (a Int32, b Int32, c String, PRIMARY KEY (b, a))");

        Schema schema = catalog["t"].GetSchema();
        Assert.Equal([1, 0], schema.PrimaryKeyColumnIndices);
        Assert.True(schema.Columns[0].IsPrimaryKey);
        Assert.True(schema.Columns[1].IsPrimaryKey);
        Assert.False(schema.Columns[2].IsPrimaryKey);
        // Both PK columns auto-promoted to NOT NULL.
        Assert.False(schema.Columns[0].Nullable);
        Assert.False(schema.Columns[1].Nullable);
    }

    [Fact]
    public void CreateTempTable_PrimaryKeyOnUnknownColumn_Throws()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE TEMP TABLE t (a Int32, PRIMARY KEY (nope))"));
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public void CreateTempTable_PrimaryKeyOnString_Accepted()
    {
        // Strings are accepted as PK kinds — they route through the
        // bytes-keyed B+Tree (variable-length) rather than the typed
        // tree's inline-only path. The COCO-filename use case:
        // PRIMARY KEY (filename) for filenames >12 bytes.
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);

        catalog.Plan("CREATE TEMP TABLE t (id String PRIMARY KEY)");
    }

    [Fact]
    public void CreateTempTable_PrimaryKeyOnByteArray_Throws()
    {
        // Byte-array PKs are still rejected — the bytes tree is for
        // *encoded* keys derived from scalar values, not raw byte arrays.
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE TEMP TABLE t (id UInt8[] PRIMARY KEY)"));
        Assert.Contains("array", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateTempTable_CompositePkOver16Bytes_Accepted()
    {
        // Composite PKs no longer have the 16-byte total cap — the
        // bytes-keyed tree handles arbitrary encoded sizes.
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);

        catalog.Plan("CREATE TEMP TABLE t (a Uuid, b Uuid, PRIMARY KEY (a, b))");
    }

    [Fact]
    public void CreateTempTable_SingleColumnUuidPk_FitsExactly()
    {
        // Uuid alone is 16 bytes — at the cap, not over.
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);

        catalog.Plan("CREATE TEMP TABLE t (id Uuid PRIMARY KEY, label String)");

        Schema schema = catalog["t"].GetSchema();
        Assert.True(schema.Columns[0].IsPrimaryKey);
        Assert.Equal(DataKind.Uuid, schema.Columns[0].Kind);
    }

    // ──────────────────── INSERT uniqueness ────────────────────

    [Fact]
    public async Task InsertValues_DuplicatePk_AcrossInserts_Throws()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32 PRIMARY KEY, name String)");
        catalog.Plan("INSERT INTO t VALUES (1, 'alice')");

        PrimaryKeyViolationException ex = Assert.Throws<PrimaryKeyViolationException>(() =>
            catalog.Plan("INSERT INTO t VALUES (1, 'bob')"));
        Assert.Contains("PRIMARY KEY violation", ex.Message);
        Assert.Contains("id=1", ex.Message);

        // Failed INSERT didn't add a second row.
        Assert.Equal(1, catalog["t"].GetRowCount());
        await Task.CompletedTask;
    }

    [Fact]
    public void InsertValues_DuplicatePk_WithinSameBatch_Throws()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32 PRIMARY KEY, name String)");

        // (1, 'a') and (1, 'b') in the same VALUES — second row collides
        // with the first within the same INSERT.
        PrimaryKeyViolationException ex = Assert.Throws<PrimaryKeyViolationException>(() =>
            catalog.Plan("INSERT INTO t VALUES (1, 'a'), (1, 'b')"));
        Assert.Contains("PRIMARY KEY violation", ex.Message);
    }

    [Fact]
    public async Task InsertValues_UniquePk_ManyRows_AllInserted()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32 PRIMARY KEY, name String)");

        catalog.Plan("INSERT INTO t VALUES (1, 'a'), (2, 'b'), (3, 'c')");

        Assert.Equal(3, catalog["t"].GetRowCount());
        await Task.CompletedTask;
    }

    [Fact]
    public void InsertValues_CompositePk_DistinguishesByTuple()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (a Int32, b Int32, n String, PRIMARY KEY (a, b))");

        // (1,1), (1,2), (2,1) all distinct.
        catalog.Plan("INSERT INTO t (a, b, n) VALUES (1, 1, 'x'), (1, 2, 'y'), (2, 1, 'z')");
        Assert.Equal(3, catalog["t"].GetRowCount());

        // (1,1) collides with row inserted above.
        PrimaryKeyViolationException ex = Assert.Throws<PrimaryKeyViolationException>(() =>
            catalog.Plan("INSERT INTO t (a, b, n) VALUES (1, 1, 'dup')"));
        Assert.Contains("PRIMARY KEY violation", ex.Message);
    }

    [Fact]
    public void InsertValues_NullPk_Throws()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32 PRIMARY KEY, name String)");

        // NULL on the PK column. PK columns are auto-NOT-NULL — the
        // catalog rejects this even before the uniqueness check runs.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("INSERT INTO t VALUES (NULL, 'oops')"));
        // Either "NOT NULL" (LiteralCoercion) or "PRIMARY KEY column ...
        // is NULL" (PrimaryKeyChecker) — both surfaces are correct
        // because the PK column carries Nullable=false from the
        // catalog's auto-promotion.
        Assert.True(
            ex.Message.Contains("NOT NULL") || ex.Message.Contains("PRIMARY KEY column"),
            $"unexpected message: {ex.Message}");
    }

    [Fact]
    public void InsertSelect_DuplicatePkFromSource_Throws()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE src (id Int32, name String)");
        catalog.Plan("CREATE TEMP TABLE dst (id Int32 PRIMARY KEY, name String)");
        catalog.Plan("INSERT INTO src VALUES (1, 'a'), (1, 'b')"); // src has duplicate ids; src has no PK so this is fine

        PrimaryKeyViolationException ex = Assert.Throws<PrimaryKeyViolationException>(() =>
            catalog.Plan("INSERT INTO dst SELECT id, name FROM src"));
        Assert.Contains("PRIMARY KEY violation", ex.Message);
    }

    [Fact]
    public async Task InsertSelect_UniquePkFromSource_AllRowsInserted()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE src (id Int32, name String)");
        catalog.Plan("CREATE TEMP TABLE dst (id Int32 PRIMARY KEY, name String)");
        catalog.Plan("INSERT INTO src VALUES (1, 'a'), (2, 'b'), (3, 'c')");

        catalog.Plan("INSERT INTO dst SELECT id, name FROM src");

        Assert.Equal(3, catalog["dst"].GetRowCount());
        await Task.CompletedTask;
    }

    // ──────────────────── PK + IDENTITY interplay ────────────────────

    [Fact]
    public async Task PkAndIdentity_Combined_AutoFillsAndStaysUnique()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        // Column-clause order in PR10f: NOT NULL → PRIMARY KEY → DEFAULT → IDENTITY.
        catalog.Plan("CREATE TEMP TABLE t (id Int64 PRIMARY KEY IDENTITY, name String)");

        catalog.Plan("INSERT INTO t (name) VALUES ('a'), ('b'), ('c')");

        Assert.Equal(3, catalog["t"].GetRowCount());

        // Subsequent insert continues from id=4 — uniqueness trivially
        // preserved.
        catalog.Plan("INSERT INTO t (name) VALUES ('d')");
        Assert.Equal(4, catalog["t"].GetRowCount());

        await Task.CompletedTask;
    }

    // ──────────────────── Persistent — PK survives reopen ────────────────────

    [Fact]
    public void CreatePersistentTable_PkInFooterPrologue()
    {
        Pool pool = new(new PoolBacking());
        using (TableCatalog catalog = new(pool, CatalogPath))
        {
            catalog.Plan("CREATE TABLE users (id Int32 PRIMARY KEY, name String)");
        }

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(Path.Combine(_tempDir, "users.datum"));
        Assert.Single(reader.Footer.Prologue.PrimaryKeyColumnIndices);
        Assert.Equal((ushort)0, reader.Footer.Prologue.PrimaryKeyColumnIndices[0]);
    }

    [Fact]
    public void InsertValues_OnPersistentTable_PkSurvivesReopenAndRejectsDup()
    {
        Pool pool = new(new PoolBacking());
        using (TableCatalog catalog = new(pool, CatalogPath))
        {
            catalog.Plan("CREATE TABLE users (id Int32 PRIMARY KEY, name String)");
            catalog.Plan("INSERT INTO users VALUES (1, 'alice'), (2, 'bob')");
        }

        using TableCatalog reopened = new(pool, CatalogPath);

        // PK is reconstructed on reopen — duplicate must still be rejected.
        PrimaryKeyViolationException ex = Assert.Throws<PrimaryKeyViolationException>(() =>
            reopened.Plan("INSERT INTO users VALUES (1, 'collision')"));
        Assert.Contains("PRIMARY KEY violation", ex.Message);

        // Non-colliding insert still works.
        reopened.Plan("INSERT INTO users VALUES (3, 'carol')");
        Assert.Equal(3, reopened["users"].GetRowCount());
    }

    // ──────────────────── ALTER ────────────────────

    [Fact]
    public void AlterTable_DropPkColumn_Throws()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32 PRIMARY KEY, name String)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("ALTER TABLE t DROP COLUMN id"));
        Assert.Contains("PRIMARY KEY", ex.Message);
    }

    [Fact]
    public async Task AlterTable_DropNonPkColumn_StillWorks()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32 PRIMARY KEY, name String, score Int32)");

        catalog.Plan("ALTER TABLE t DROP COLUMN score");

        Schema schema = catalog["t"].GetSchema();
        Assert.Equal(["id", "name"], schema.Columns.Select(c => c.Name));
        // PK still references id (now at schema position 0).
        Assert.Equal([0], schema.PrimaryKeyColumnIndices);
        await Task.CompletedTask;
    }
}
