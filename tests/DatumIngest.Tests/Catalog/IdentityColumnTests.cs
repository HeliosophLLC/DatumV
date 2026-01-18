using DatumIngest.Catalog;
using DatumIngest.DatumFile.V2;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// PR10e tests for SQL <c>IDENTITY</c> columns. Cover:
/// bare <c>IDENTITY</c> (defaults 1, 1) and parametrized
/// <c>IDENTITY(seed, step)</c>; auto-fill on omission; rejection of
/// explicit values for the IDENTITY column (column-list and positional
/// paths); single-IDENTITY-per-table validation; non-integer rejection;
/// counter persistence across reopen for persistent tables;
/// catalog-level CREATE/INSERT round-trip on temp + persistent.
/// </summary>
public sealed class IdentityColumnTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_pr10e_{Guid.NewGuid():N}");
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
    public void CreateTempTable_BareIdentity_DefaultsToOneOne()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);

        catalog.Plan("CREATE TEMP TABLE t (id Int64 IDENTITY, name String)");

        Schema schema = catalog["t"].GetSchema();
        IdentitySpec spec = Assert.IsType<IdentitySpec>(schema.Columns[0].Identity);
        Assert.Equal(1, spec.Seed);
        Assert.Equal(1, spec.Step);
        Assert.Null(schema.Columns[1].Identity);
    }

    [Fact]
    public void CreateTempTable_ParametrizedIdentity_KeepsSeedAndStep()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);

        catalog.Plan("CREATE TEMP TABLE t (id Int32 IDENTITY(100, 5))");

        IdentitySpec spec = catalog["t"].GetSchema().Columns[0].Identity!;
        Assert.Equal(100, spec.Seed);
        Assert.Equal(5, spec.Step);
    }

    [Fact]
    public void CreateTempTable_NegativeStepIdentity_Allowed()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);

        catalog.Plan("CREATE TEMP TABLE t (id Int32 IDENTITY(0, -1))");

        IdentitySpec spec = catalog["t"].GetSchema().Columns[0].Identity!;
        Assert.Equal(0, spec.Seed);
        Assert.Equal(-1, spec.Step);
    }

    [Fact]
    public void CreateTempTable_TwoIdentityColumns_Throws()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE TEMP TABLE t (a Int32 IDENTITY, b Int64 IDENTITY)"));
        Assert.Contains("at most one IDENTITY", ex.Message);
    }

    [Fact]
    public void CreateTempTable_NonIntegerIdentity_Throws()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE TEMP TABLE t (id String IDENTITY)"));
        Assert.Contains("integer", ex.Message);
    }

    [Fact]
    public void CreateTempTable_ZeroStep_Throws()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE TEMP TABLE t (id Int32 IDENTITY(1, 0))"));
        Assert.Contains("non-zero", ex.Message);
    }

    [Fact]
    public void CreateTempTable_SeedOutOfRangeForKind_Throws()
    {
        // Int8 holds [-128, 127]. Seed 200 doesn't fit.
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE TEMP TABLE t (id Int8 IDENTITY(200, 1))"));
        Assert.Contains("Int8", ex.Message);
    }

    // ──────────────────── INSERT auto-fill (TEMP) ────────────────────

    [Fact]
    public async Task InsertValues_AutoFillsIdentity_FromOne()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int64 IDENTITY, name String)");

        catalog.Plan("INSERT INTO t (name) VALUES ('alice'), ('bob'), ('carol')");

        List<(long id, string name)> rows = await ScanLongFirstString(catalog["t"]);
        Assert.Equal([(1L, "alice"), (2L, "bob"), (3L, "carol")], rows);
    }

    [Fact]
    public async Task InsertValues_AutoFillsIdentity_WithCustomSeedAndStep()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32 IDENTITY(100, 5), name String)");

        catalog.Plan("INSERT INTO t (name) VALUES ('a'), ('b')");

        List<(long id, string name)> rows = await ScanLongFirstString(catalog["t"]);
        Assert.Equal([(100L, "a"), (105L, "b")], rows);
    }

    [Fact]
    public async Task InsertValues_TwoBatches_CounterAdvancesAcrossInserts()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32 IDENTITY, name String)");

        catalog.Plan("INSERT INTO t (name) VALUES ('a'), ('b')");
        catalog.Plan("INSERT INTO t (name) VALUES ('c')");

        List<(long id, string name)> rows = await ScanLongFirstString(catalog["t"]);
        Assert.Equal([(1L, "a"), (2L, "b"), (3L, "c")], rows);
    }

    [Fact]
    public void InsertValues_ColumnListIncludesIdentity_Throws()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32 IDENTITY, name String)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("INSERT INTO t (id, name) VALUES (5, 'a')"));
        Assert.Contains("IDENTITY", ex.Message);
    }

    [Fact]
    public void InsertValues_PositionalAgainstIdentityTable_Throws()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32 IDENTITY, name String)");

        // Positional supplies values for every column including the
        // IDENTITY column → reject.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("INSERT INTO t VALUES (5, 'a')"));
        Assert.Contains("IDENTITY", ex.Message);
    }

    [Fact]
    public async Task InsertSelect_OmitsIdentityColumn_AutoFills()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE src (n String)");
        catalog.Plan("CREATE TEMP TABLE dst (id Int32 IDENTITY, n String)");
        catalog.Plan("INSERT INTO src VALUES ('x'), ('y')");

        catalog.Plan("INSERT INTO dst (n) SELECT n FROM src");

        List<(long id, string name)> rows = await ScanLongFirstString(catalog["dst"]);
        Assert.Equal([(1L, "x"), (2L, "y")], rows);
    }

    [Fact]
    public void InsertSelect_ColumnListIncludesIdentity_Throws()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE src (n Int32, m String)");
        catalog.Plan("CREATE TEMP TABLE dst (id Int32 IDENTITY, n String)");
        catalog.Plan("INSERT INTO src VALUES (1, 'x')");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("INSERT INTO dst (id, n) SELECT n, m FROM src"));
        Assert.Contains("IDENTITY", ex.Message);
    }

    // ──────────────────── Persistent — counter survives reopen ────────────────────

    [Fact]
    public async Task InsertValues_PersistentTable_CounterSurvivesReopen()
    {
        Pool pool = new(new PoolBacking());
        using (TableCatalog catalog = new(pool, CatalogPath))
        {
            catalog.Plan("CREATE TABLE users (id Int64 IDENTITY, name String)");
            catalog.Plan("INSERT INTO users (name) VALUES ('alice'), ('bob')");
        }

        using (TableCatalog reopened = new(pool, CatalogPath))
        {
            // Insert another row — the counter must continue from 3,
            // not restart at 1.
            reopened.Plan("INSERT INTO users (name) VALUES ('carol')");

            List<(long id, string name)> rows = await ScanLongFirstString(reopened["users"]);
            Assert.Equal([(1L, "alice"), (2L, "bob"), (3L, "carol")], rows);
        }
    }

    [Fact]
    public void CreatePersistentTable_IdentityPersistsInFooterPrologue()
    {
        Pool pool = new(new PoolBacking());
        using (TableCatalog catalog = new(pool, CatalogPath))
        {
            catalog.Plan("CREATE TABLE users (id Int32 IDENTITY(50, 2), name String)");
        }

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(Path.Combine(_tempDir, "users.datum"));
        Assert.Equal(0, reader.Footer.Prologue.IdentityColumnIndex);
        Assert.Equal(50, reader.Footer.Prologue.IdentitySeed);
        Assert.Equal(2, reader.Footer.Prologue.IdentityStep);
        Assert.Equal(50, reader.Footer.Prologue.IdentityNextValue); // not yet advanced
    }

    [Fact]
    public async Task InsertValues_PersistentTable_AdvancesCounterInFooter()
    {
        Pool pool = new(new PoolBacking());
        using (TableCatalog catalog = new(pool, CatalogPath))
        {
            catalog.Plan("CREATE TABLE users (id Int64 IDENTITY, name String)");
            catalog.Plan("INSERT INTO users (name) VALUES ('a'), ('b'), ('c')");
        }

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(Path.Combine(_tempDir, "users.datum"));
        Assert.Equal(4, reader.Footer.Prologue.IdentityNextValue);

        // Independent verify: scan back and count rows.
        Pool pool2 = new(new PoolBacking());
        using TableCatalog reopened = new(pool2, CatalogPath);
        List<(long id, string name)> rows = await ScanLongFirstString(reopened["users"]);
        Assert.Equal([(1L, "a"), (2L, "b"), (3L, "c")], rows);
    }

    // ──────────────────── ALTER TABLE ADD COLUMN with IDENTITY ────────────────────

    [Fact]
    public void AlterTableAddColumn_IdentityNotAllowed_OutOfScope()
    {
        // PR10b's ALTER ADD COLUMN already rejects DEFAULT and NOT NULL.
        // IDENTITY through ALTER ADD isn't wired in PR10e — the parser
        // produces an Identity field on the AST but the executor's
        // path doesn't propagate it. Pin the current behavior so a
        // future PR can flip this test to verify the new path.
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (a Int32)");

        // Current ALTER ADD COLUMN parser doesn't accept IDENTITY in
        // its body, so the parse itself fails. Either way: not a
        // supported operation today.
        Assert.ThrowsAny<Exception>(() =>
            catalog.Plan("ALTER TABLE t ADD COLUMN id Int32 IDENTITY"));
    }

    // ──────────────────── ident_current() scalar ────────────────────

    [Fact]
    public async Task IdentCurrent_AfterInsert_ReturnsLastReservedValue()
    {
        // Mirrors the chat-INSERT-then-FK pattern: insert produces the
        // IDENTITY value, ident_current() reads it back for the next
        // statement's FK reference.
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE conversations (id Int64 IDENTITY, title String)");

        catalog.Plan("INSERT INTO conversations (title) VALUES ('Chat')");

        long? id = await ScanIdentCurrent(catalog, "conversations");
        Assert.Equal(1L, id);

        catalog.Plan("INSERT INTO conversations (title) VALUES ('Second')");
        id = await ScanIdentCurrent(catalog, "conversations");
        Assert.Equal(2L, id);
    }

    [Fact]
    public async Task IdentCurrent_ParametrizedSeed_TracksStepFromSeed()
    {
        // IDENTITY(100, 5): first reservation = 100, second = 105.
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32 IDENTITY(100, 5), name String)");
        catalog.Plan("INSERT INTO t (name) VALUES ('a'), ('b'), ('c')");

        long? id = await ScanIdentCurrent(catalog, "t");
        Assert.Equal(110L, id);
    }

    [Fact]
    public async Task IdentCurrent_NoInsertsYet_ReturnsNull()
    {
        // Counter is still at the seed → no values reserved → NULL.
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int64 IDENTITY, name String)");

        long? id = await ScanIdentCurrent(catalog, "t");
        Assert.Null(id);
    }

    [Fact]
    public async Task IdentCurrent_TableWithoutIdentity_ReturnsNull()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE plain (id Int32, name String)");
        catalog.Plan("INSERT INTO plain VALUES (1, 'a')");

        long? id = await ScanIdentCurrent(catalog, "plain");
        Assert.Null(id);
    }

    [Fact]
    public async Task IdentCurrent_UnknownTable_ReturnsNull()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int64 IDENTITY, name String)");

        long? id = await ScanIdentCurrent(catalog, "nope");
        Assert.Null(id);
    }

    [Fact]
    public async Task IdentCurrent_UsableInsideInsertValues_ResolvesFkReference()
    {
        // The actual chat workflow: insert into conversations, then use
        // ident_current to pull the new id when inserting the message.
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE conversations (id Int64 IDENTITY, title String)");
        catalog.Plan("CREATE TEMP TABLE messages (id Int64 IDENTITY, conversation_id Int64, body String)");

        catalog.Plan("INSERT INTO conversations (title) VALUES ('Chat')");
        catalog.Plan("INSERT INTO messages (conversation_id, body) VALUES (ident_current('conversations'), 'Hello')");

        await foreach (RowBatch batch in catalog["messages"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            Assert.Equal(1, batch.Count);
            Assert.Equal(1L, batch[0][1].AsInt64());
            batch.Dispose();
        }
    }

    [Fact]
    public async Task IdentCurrent_PersistentTable_RoundTripsAcrossReopen()
    {
        Pool pool = new(new PoolBacking());
        using (TableCatalog catalog = new(pool, CatalogPath))
        {
            catalog.Plan("CREATE TABLE conversations (id Int64 IDENTITY, title String)");
            catalog.Plan("INSERT INTO conversations (title) VALUES ('First'), ('Second'), ('Third')");
        }

        using TableCatalog reopened = new(pool, CatalogPath);
        long? id = await ScanIdentCurrent(reopened, "conversations");
        Assert.Equal(3L, id);
    }

    private static async Task<long?> ScanIdentCurrent(TableCatalog catalog, string tableName)
    {
        // QueryPlan.ExecuteAsync owns and returns each batch via its
        // own finally — extracting a copy of the value before the next
        // iteration step is enough; don't dispose explicitly.
        IQueryPlan plan = catalog.Plan($"SELECT ident_current('{tableName}') AS v");
        long? captured = null;
        bool first = true;
        await foreach (RowBatch batch in plan.ExecuteAsync(default))
        {
            if (first && batch.Count > 0)
            {
                DataValue v = batch[0][0];
                captured = v.IsNull ? null : v.AsInt64();
                first = false;
            }
        }
        return captured;
    }

    // ──────────────────── Helpers ────────────────────

    /// <summary>
    /// Reads back rows shaped (Int*, String) where the first column is
    /// any signed integer kind. Returns the integer as long for cross-
    /// kind comparison.
    /// </summary>
    private static async Task<List<(long id, string name)>> ScanLongFirstString(ITableProvider provider)
    {
        List<(long, string)> rows = new();
        await foreach (RowBatch batch in provider.ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                Row row = batch[r];
                long id = row[0].Kind switch
                {
                    DataKind.Int8 => row[0].AsInt8(),
                    DataKind.Int16 => row[0].AsInt16(),
                    DataKind.Int32 => row[0].AsInt32(),
                    DataKind.Int64 => row[0].AsInt64(),
                    DataKind.UInt8 => row[0].AsUInt8(),
                    DataKind.UInt16 => row[0].AsUInt16(),
                    DataKind.UInt32 => row[0].AsUInt32(),
                    _ => throw new InvalidOperationException($"unexpected id kind {row[0].Kind}"),
                };
                string name = row[1].IsNull ? "<null>" : row[1].AsString();
                rows.Add((id, name));
            }
            batch.Dispose();
        }
        return rows;
    }
}
