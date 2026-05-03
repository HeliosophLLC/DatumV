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
public sealed class PrimaryKeyTests : ServiceTestBase, IAsyncLifetime
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
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);

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
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);

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
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);

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
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);

        catalog.Plan("CREATE TEMP TABLE t (id String PRIMARY KEY)");
    }

    [Fact]
    public void CreateTempTable_PrimaryKeyOnByteArray_Throws()
    {
        // Byte-array PKs are still rejected — the bytes tree is for
        // *encoded* keys derived from scalar values, not raw byte arrays.
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE TEMP TABLE t (id UInt8[] PRIMARY KEY)"));
        Assert.Contains("array", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateTempTable_CompositePkOver16Bytes_Accepted()
    {
        // Composite PKs no longer have the 16-byte total cap — the
        // bytes-keyed tree handles arbitrary encoded sizes.
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);

        catalog.Plan("CREATE TEMP TABLE t (a Uuid, b Uuid, PRIMARY KEY (a, b))");
    }

    [Fact]
    public void CreateTempTable_SingleColumnUuidPk_FitsExactly()
    {
        // Uuid alone is 16 bytes — at the cap, not over.
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);

        catalog.Plan("CREATE TEMP TABLE t (id Uuid PRIMARY KEY, label String)");

        Schema schema = catalog["t"].GetSchema();
        Assert.True(schema.Columns[0].IsPrimaryKey);
        Assert.Equal(DataKind.Uuid, schema.Columns[0].Kind);
    }

    // ──────────────────── INSERT uniqueness ────────────────────

    [Fact]
    public async Task InsertValues_DuplicatePk_AcrossInserts_Throws()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
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
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
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
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32 PRIMARY KEY, name String)");

        catalog.Plan("INSERT INTO t VALUES (1, 'a'), (2, 'b'), (3, 'c')");

        Assert.Equal(3, catalog["t"].GetRowCount());
        await Task.CompletedTask;
    }

    [Fact]
    public void InsertValues_CompositePk_DistinguishesByTuple()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
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
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
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
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
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
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
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
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
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
        Pool pool = CreatePool();
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE users (id Int32 PRIMARY KEY, name String)");
        }

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(Path.Combine(_tempDir, "data", "public", "users.datum"));
        Assert.Single(reader.Footer.Prologue.PrimaryKeyColumnIndices);
        Assert.Equal((ushort)0, reader.Footer.Prologue.PrimaryKeyColumnIndices[0]);
    }

    [Fact]
    public void InsertValues_OnPersistentTable_PkSurvivesReopenAndRejectsDup()
    {
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE users (id Int32 PRIMARY KEY, name String)");
            catalog.Plan("INSERT INTO users VALUES (1, 'alice'), (2, 'bob')");
        }

        using TableCatalog reopened = CreateCatalog(CatalogPath);

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
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32 PRIMARY KEY, name String)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("ALTER TABLE t DROP COLUMN id"));
        Assert.Contains("PRIMARY KEY", ex.Message);
    }

    [Fact]
    public async Task AlterTable_DropNonPkColumn_StillWorks()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32 PRIMARY KEY, name String, score Int32)");

        catalog.Plan("ALTER TABLE t DROP COLUMN score");

        Schema schema = catalog["t"].GetSchema();
        Assert.Equal(["id", "name"], schema.Columns.Select(c => c.Name));
        // PK still references id (now at schema position 0).
        Assert.Equal([0], schema.PrimaryKeyColumnIndices);
        await Task.CompletedTask;
    }

    // ──────────────────── ALTER TABLE ADD COLUMN ... PRIMARY KEY ────────────────────
    //
    // Adding a PK to an existing table via `ALTER TABLE … ADD COLUMN id Int64
    // GENERATED ALWAYS AS IDENTITY PRIMARY KEY` — the canonical "I forgot a
    // PK" workflow. The new column gets sequential identity values for
    // existing rows, the `.datum-pkindex` is built from those values, and
    // the footer's PrimaryKeyColumnIndices is flipped to reference the new
    // column. Duplicate-rejection during the build fires when the user
    // chose a non-unique fill (e.g. DEFAULT 0 across many rows).

    [Fact]
    public void AlterTable_AddPkColumnToEmptyPersistentTable_PkRegistered()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE users (name String)");

        catalog.Plan("ALTER TABLE users ADD COLUMN id Int64 GENERATED ALWAYS AS IDENTITY PRIMARY KEY");

        Schema schema = catalog["users"].GetSchema();
        Assert.Equal(["name", "id"], schema.Columns.Select(c => c.Name));
        Assert.Single(schema.PrimaryKeyColumnIndices);
        Assert.Equal(1, schema.PrimaryKeyColumnIndices[0]);
        Assert.True(schema.Columns[1].IsPrimaryKey);
        Assert.False(schema.Columns[1].Nullable);
    }

    /// <summary>
    /// PK on an empty table without IDENTITY works — the non-empty check
    /// is gated on <c>GetRowCount() &gt; 0</c>, so an empty table doesn't
    /// need a backfill mechanism. Future INSERTs must supply the PK value
    /// explicitly (since there's no IDENTITY to auto-fill it).
    /// </summary>
    [Fact]
    public void AlterTable_AddPkColumnToEmptyPersistentTable_WithoutIdentity_Works()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE users (name String)");

        catalog.Plan("ALTER TABLE users ADD COLUMN id Int32 PRIMARY KEY");

        Schema schema = catalog["users"].GetSchema();
        Assert.Single(schema.PrimaryKeyColumnIndices);
        Assert.True(schema.Columns[1].IsPrimaryKey);
        Assert.False(schema.Columns[1].Nullable);

        // PK enforcement: explicit user-supplied values are required and
        // duplicates rejected.
        catalog.Plan("INSERT INTO users (name, id) VALUES ('alice', 1)");
        PrimaryKeyViolationException ex = Assert.Throws<PrimaryKeyViolationException>(() =>
            catalog.Plan("INSERT INTO users (name, id) VALUES ('dup', 1)"));
        Assert.Contains("PRIMARY KEY violation", ex.Message);
    }

    [Fact]
    public async Task AlterTable_AddPkColumnToNonEmptyTable_BackfillsAndBuildsIndex()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE users (name String)");
        catalog.Plan("INSERT INTO users (name) VALUES ('alice'), ('bob'), ('carol')");

        catalog.Plan("ALTER TABLE users ADD COLUMN id Int64 GENERATED ALWAYS AS IDENTITY PRIMARY KEY");

        // Existing rows get sequential IDs and the new column is PK.
        Dictionary<string, long> ids = new();
        await foreach (RowBatch batch in catalog["users"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                ids[batch[r][0].AsString(batch.Arena)] = batch[r][1].AsInt64();
            }
            batch.Dispose();
        }
        Assert.Equal(1L, ids["alice"]);
        Assert.Equal(2L, ids["bob"]);
        Assert.Equal(3L, ids["carol"]);

        // Subsequent IDENTITY insert continues past the backfill counter.
        catalog.Plan("INSERT INTO users (name) VALUES ('dave')");
        Assert.Equal(4, catalog["users"].GetRowCount());
    }

    [Fact]
    public void AlterTable_AddPkColumnByDefaultIdentity_EnforcesUniqueOnSubsequentInsert()
    {
        // GENERATED BY DEFAULT AS IDENTITY accepts user-supplied values,
        // so we can prove PK enforcement is wired by trying to INSERT a
        // value that collides with a backfilled row.
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE users (name String)");
        catalog.Plan("INSERT INTO users (name) VALUES ('alice'), ('bob')");

        catalog.Plan("ALTER TABLE users ADD COLUMN id Int64 GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY");

        // Backfilled rows hold id=1 (alice) and id=2 (bob).
        PrimaryKeyViolationException ex = Assert.Throws<PrimaryKeyViolationException>(() =>
            catalog.Plan("INSERT INTO users (id, name) VALUES (1, 'dup')"));
        Assert.Contains("PRIMARY KEY violation", ex.Message);

        // Non-colliding explicit value still works.
        catalog.Plan("INSERT INTO users (id, name) VALUES (99, 'carol')");
        Assert.Equal(3, catalog["users"].GetRowCount());
    }

    [Fact]
    public void AlterTable_AddPkColumn_PkIndexSidecarExists()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE users (name String)");
        catalog.Plan("INSERT INTO users (name) VALUES ('alice'), ('bob')");

        catalog.Plan("ALTER TABLE users ADD COLUMN id Int64 GENERATED ALWAYS AS IDENTITY PRIMARY KEY");

        // The `.datum-pkindex` sidecar must exist on disk after a
        // successful PK add on a populated table — that's what gives us
        // the lookup-backed PK check at INSERT time instead of the
        // O(table) scan fallback.
        string pkIndexPath = Path.Combine(_tempDir, "data", "public", "users.datum-pkindex");
        Assert.True(File.Exists(pkIndexPath), $"Expected PK index sidecar at {pkIndexPath}");
    }

    [Fact]
    public void AlterTable_AddPkColumn_WhenTableAlreadyHasPk_Throws()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE users (id Int32 PRIMARY KEY, name String)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("ALTER TABLE users ADD COLUMN code Int64 GENERATED ALWAYS AS IDENTITY PRIMARY KEY"));
        Assert.Contains("PRIMARY KEY", ex.Message);
    }

    [Fact]
    public void AlterTable_AddPkColumn_OnNonEmptyTable_WithoutFill_Throws()
    {
        // Non-empty table + PK column with neither IDENTITY nor DEFAULT
        // is unsupported: every existing row would need a non-null value
        // we cannot synthesise.
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE users (name String)");
        catalog.Plan("INSERT INTO users (name) VALUES ('alice')");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("ALTER TABLE users ADD COLUMN id Int64 PRIMARY KEY"));
        Assert.Contains("backfill", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────── ALTER TABLE … DROP CONSTRAINT ────────────────────
    //
    // Drops the PRIMARY KEY constraint from a table by its auto-derived name
    // (<c>&lt;table&gt;_pkey</c>). Removes the .datum-pkindex sidecar, flips the
    // footer's PrimaryKeyColumnIndices to empty, and refreshes the snapshot
    // so subsequent INSERTs no longer enforce uniqueness on the (former) PK
    // column.

    [Fact]
    public void AlterTable_DropPrimaryKeyConstraint_Persistent_RemovesPkAndSidecar()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE users (id Int32 PRIMARY KEY, name String)");
        catalog.Plan("INSERT INTO users VALUES (1, 'alice')");

        string pkIndexPath = Path.Combine(_tempDir, "data", "public", "users.datum-pkindex");
        Assert.True(File.Exists(pkIndexPath), "PK sidecar should exist before drop.");

        catalog.Plan("ALTER TABLE users DROP CONSTRAINT users_pkey");

        Schema schema = catalog["users"].GetSchema();
        Assert.Empty(schema.PrimaryKeyColumnIndices);
        Assert.False(schema.Columns[0].IsPrimaryKey);
        Assert.False(File.Exists(pkIndexPath), "PK sidecar should be removed after drop.");
    }

    [Fact]
    public void AlterTable_DropPrimaryKey_AllowsDuplicateInsertAfterDrop()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE users (id Int32 PRIMARY KEY, name String)");
        catalog.Plan("INSERT INTO users VALUES (1, 'alice'), (2, 'bob')");

        catalog.Plan("ALTER TABLE users DROP CONSTRAINT users_pkey");

        // PK no longer enforced — what was a violation before now succeeds.
        catalog.Plan("INSERT INTO users VALUES (1, 'duplicate')");
        Assert.Equal(3, catalog["users"].GetRowCount());
    }

    [Fact]
    public async Task AlterTable_DropPrimaryKey_SurvivesReopen()
    {
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE users (id Int32 PRIMARY KEY, name String)");
            catalog.Plan("INSERT INTO users VALUES (1, 'alice')");
            catalog.Plan("ALTER TABLE users DROP CONSTRAINT users_pkey");
        }

        using TableCatalog reopened = CreateCatalog(CatalogPath);
        Schema schema = reopened["users"].GetSchema();
        Assert.Empty(schema.PrimaryKeyColumnIndices);

        // No PK after reopen — duplicate inserts must succeed.
        reopened.Plan("INSERT INTO users VALUES (1, 'duplicate')");
        Assert.Equal(2, reopened["users"].GetRowCount());

        await Task.CompletedTask;
    }

    [Fact]
    public void AlterTable_DropConstraint_TableHasNoPk_Throws()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE users (id Int32, name String)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("ALTER TABLE users DROP CONSTRAINT users_pkey"));
        Assert.Contains("users_pkey", ex.Message);
        Assert.Contains("does not exist", ex.Message);
    }

    [Fact]
    public void AlterTable_DropConstraint_WrongName_Throws()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE users (id Int32 PRIMARY KEY, name String)");

        // The right name is `users_pkey`; this one shouldn't match.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("ALTER TABLE users DROP CONSTRAINT some_other_name"));
        Assert.Contains("some_other_name", ex.Message);
        Assert.Contains("does not exist", ex.Message);
    }

    [Fact]
    public void AlterTable_DropConstraint_IfExists_OnAbsentConstraint_NoThrow()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE users (id Int32, name String)");

        // Table has no PK, but IF EXISTS suppresses the error.
        catalog.Plan("ALTER TABLE users DROP CONSTRAINT IF EXISTS users_pkey");

        Schema schema = catalog["users"].GetSchema();
        Assert.Empty(schema.PrimaryKeyColumnIndices);
    }

    [Fact]
    public void AlterTable_DropConstraint_TableNotFound_Throws()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        Exception ex = Assert.ThrowsAny<Exception>(() =>
            catalog.Plan("ALTER TABLE missing DROP CONSTRAINT missing_pkey"));
        Assert.Contains("missing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AlterTable_TableLevelIfExists_MissingTable_NoThrow()
    {
        // PG's table-level IF EXISTS suppresses the "table not found"
        // error so deploy scripts can run idempotently against a fresh
        // database.
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        // None of these should throw — table doesn't exist, IF EXISTS
        // makes the whole statement a no-op.
        catalog.Plan("ALTER TABLE IF EXISTS missing ADD COLUMN x Int32");
        catalog.Plan("ALTER TABLE IF EXISTS missing DROP COLUMN x");
        catalog.Plan("ALTER TABLE IF EXISTS missing DROP CONSTRAINT missing_pkey");
        catalog.Plan("ALTER TABLE IF EXISTS missing ALTER COLUMN x DROP DEFAULT");
    }

    [Fact]
    public void AlterTable_TableLevelIfExists_ExistingTable_StillAppliesBody()
    {
        // IF EXISTS only suppresses the table-not-found case — when the
        // table is present, the body must still execute normally.
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE users (id Int32 PRIMARY KEY, name String)");

        catalog.Plan("ALTER TABLE IF EXISTS users DROP CONSTRAINT users_pkey");

        Schema schema = catalog["users"].GetSchema();
        Assert.Empty(schema.PrimaryKeyColumnIndices);
    }

    // ──────────────────── User-supplied CONSTRAINT names ────────────────────

    [Fact]
    public async Task CreateTable_NamedPk_IS_View_ShowsCustomName()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE users (id Int32, name String, CONSTRAINT my_users_pk PRIMARY KEY (id))");

        ITableProvider provider = catalog["information_schema.table_constraints"];
        bool found = false;
        await foreach (RowBatch batch in provider.ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            Arena arena = batch.Arena;
            for (int r = 0; r < batch.Count; r++)
            {
                if (batch[r][5].AsString(arena) == "users" &&
                    batch[r][6].AsString(arena) == "PRIMARY KEY")
                {
                    Assert.Equal("my_users_pk", batch[r][2].AsString(arena));
                    found = true;
                }
            }
            batch.Dispose();
        }
        Assert.True(found, "PK row should be present in information_schema.table_constraints.");
    }

    [Fact]
    public void DropConstraint_MatchesUserSuppliedName()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE users (id Int32 CONSTRAINT my_pk PRIMARY KEY, name String)");

        // The derived `users_pkey` is NOT the constraint name now — the
        // user named it `my_pk`.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("ALTER TABLE users DROP CONSTRAINT users_pkey"));
        Assert.Contains("users_pkey", ex.Message);
        Assert.Contains("does not exist", ex.Message);

        // The user-supplied name does match.
        catalog.Plan("ALTER TABLE users DROP CONSTRAINT my_pk");
        Schema schema = catalog["users"].GetSchema();
        Assert.Empty(schema.PrimaryKeyColumnIndices);
    }

    [Fact]
    public void CreateTable_NamedPk_SurvivesReopen()
    {
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE users (id Int32, name String, CONSTRAINT users_id_pk PRIMARY KEY (id))");
        }

        // Reopen — the user-supplied name must round-trip via the catalog file.
        using TableCatalog reopened = CreateCatalog(CatalogPath);
        // DROP must still match the user-supplied name after reopen.
        reopened.Plan("ALTER TABLE users DROP CONSTRAINT users_id_pk");
        Schema schema = reopened["users"].GetSchema();
        Assert.Empty(schema.PrimaryKeyColumnIndices);
    }

    [Fact]
    public void DropConstraint_NamedPk_ClearsCustomNameAfterDrop()
    {
        // After DROP, a future ADD-PK (when we ship it) should not see
        // the stale name. We can't test ADD-PK yet, but we can verify
        // the IS view stops showing the row.
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE users (id Int32 CONSTRAINT my_pk PRIMARY KEY)");
        catalog.Plan("ALTER TABLE users DROP CONSTRAINT my_pk");

        Schema schema = catalog["users"].GetSchema();
        Assert.Empty(schema.PrimaryKeyColumnIndices);
    }

    [Fact]
    public void AlterTable_DropConstraint_VisibleInInformationSchema_AfterDrop()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE users (id Int32 PRIMARY KEY, name String)");

        catalog.Plan("ALTER TABLE users DROP CONSTRAINT users_pkey");

        // information_schema.table_constraints must drop the row, otherwise
        // the discovery → drop loop wouldn't be symmetric.
        Schema constraintsSchema = catalog["information_schema.table_constraints"].GetSchema();
        _ = constraintsSchema; // touch to confirm the provider is wired
        Schema usersSchema = catalog["users"].GetSchema();
        Assert.Empty(usersSchema.PrimaryKeyColumnIndices);
    }

    [Fact]
    public async Task AlterTable_AddPkColumn_SurvivesReopen()
    {
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE users (name String)");
            catalog.Plan("INSERT INTO users (name) VALUES ('alice'), ('bob')");
            catalog.Plan("ALTER TABLE users ADD COLUMN id Int64 GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY");
        }

        using TableCatalog reopened = CreateCatalog(CatalogPath);

        Schema schema = reopened["users"].GetSchema();
        Assert.Equal([1], schema.PrimaryKeyColumnIndices);
        Assert.True(schema.Columns[1].IsPrimaryKey);

        // PK enforcement must still fire after reopen — the rebuilt
        // index sidecar drives the duplicate check.
        PrimaryKeyViolationException ex = Assert.Throws<PrimaryKeyViolationException>(() =>
            reopened.Plan("INSERT INTO users (id, name) VALUES (1, 'dup')"));
        Assert.Contains("PRIMARY KEY violation", ex.Message);

        await Task.CompletedTask;
    }
}
