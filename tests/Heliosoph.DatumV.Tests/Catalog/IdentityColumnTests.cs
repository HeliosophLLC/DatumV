using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.DatumFile.V2;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Tests.Catalog;

/// <summary>
/// PR10e tests for SQL <c>IDENTITY</c> columns. Cover:
/// bare <c>IDENTITY</c> (defaults 1, 1) and parametrized
/// <c>IDENTITY(seed, step)</c>; auto-fill on omission; rejection of
/// explicit values for the IDENTITY column (column-list and positional
/// paths); single-IDENTITY-per-table validation; non-integer rejection;
/// counter persistence across reopen for persistent tables;
/// catalog-level CREATE/INSERT round-trip on temp + persistent.
/// </summary>
public sealed class IdentityColumnTests : ServiceTestBase, IAsyncLifetime
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
        using TableCatalog catalog = CreateCatalog();

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
        using TableCatalog catalog = CreateCatalog();

        catalog.Plan("CREATE TEMP TABLE t (id Int32 IDENTITY(100, 5))");

        IdentitySpec spec = catalog["t"].GetSchema().Columns[0].Identity!;
        Assert.Equal(100, spec.Seed);
        Assert.Equal(5, spec.Step);
    }

    [Fact]
    public void CreateTempTable_NegativeStepIdentity_Allowed()
    {
        using TableCatalog catalog = CreateCatalog();

        catalog.Plan("CREATE TEMP TABLE t (id Int32 IDENTITY(0, -1))");

        IdentitySpec spec = catalog["t"].GetSchema().Columns[0].Identity!;
        Assert.Equal(0, spec.Seed);
        Assert.Equal(-1, spec.Step);
    }

    [Fact]
    public void CreateTempTable_TwoIdentityColumns_Throws()
    {
        using TableCatalog catalog = CreateCatalog();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE TEMP TABLE t (a Int32 IDENTITY, b Int64 IDENTITY)"));
        Assert.Contains("at most one IDENTITY", ex.Message);
    }

    [Fact]
    public void CreateTempTable_NonIntegerIdentity_Throws()
    {
        using TableCatalog catalog = CreateCatalog();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE TEMP TABLE t (id String IDENTITY)"));
        Assert.Contains("integer", ex.Message);
    }

    [Fact]
    public void CreateTempTable_ZeroStep_Throws()
    {
        using TableCatalog catalog = CreateCatalog();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE TEMP TABLE t (id Int32 IDENTITY(1, 0))"));
        Assert.Contains("non-zero", ex.Message);
    }

    [Fact]
    public void CreateTempTable_SeedOutOfRangeForKind_Throws()
    {
        // Int8 holds [-128, 127]. Seed 200 doesn't fit.
        using TableCatalog catalog = CreateCatalog();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE TEMP TABLE t (id Int8 IDENTITY(200, 1))"));
        Assert.Contains("Int8", ex.Message);
    }

    // ──────────────────── INSERT auto-fill (TEMP) ────────────────────

    [Fact]
    public async Task InsertValues_AutoFillsIdentity_FromOne()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int64 IDENTITY, name String)");

        catalog.Plan("INSERT INTO t (name) VALUES ('alice'), ('bob'), ('carol')");

        List<(long id, string name)> rows = await ScanLongFirstString(catalog["t"]);
        Assert.Equal([(1L, "alice"), (2L, "bob"), (3L, "carol")], rows);
    }

    [Fact]
    public async Task InsertValues_AutoFillsIdentity_WithCustomSeedAndStep()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32 IDENTITY(100, 5), name String)");

        catalog.Plan("INSERT INTO t (name) VALUES ('a'), ('b')");

        List<(long id, string name)> rows = await ScanLongFirstString(catalog["t"]);
        Assert.Equal([(100L, "a"), (105L, "b")], rows);
    }

    [Fact]
    public async Task InsertValues_TwoBatches_CounterAdvancesAcrossInserts()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32 IDENTITY, name String)");

        catalog.Plan("INSERT INTO t (name) VALUES ('a'), ('b')");
        catalog.Plan("INSERT INTO t (name) VALUES ('c')");

        List<(long id, string name)> rows = await ScanLongFirstString(catalog["t"]);
        Assert.Equal([(1L, "a"), (2L, "b"), (3L, "c")], rows);
    }

    [Fact]
    public void InsertValues_ColumnListIncludesIdentity_Throws()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32 IDENTITY, name String)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("INSERT INTO t (id, name) VALUES (5, 'a')"));
        Assert.Contains("IDENTITY", ex.Message);
    }

    [Fact]
    public void InsertValues_PositionalAgainstIdentityTable_Throws()
    {
        using TableCatalog catalog = CreateCatalog();
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
        using TableCatalog catalog = CreateCatalog();
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
        using TableCatalog catalog = CreateCatalog();
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
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE users (id Int64 IDENTITY, name String)");
            catalog.Plan("INSERT INTO users (name) VALUES ('alice'), ('bob')");
        }

        using (TableCatalog reopened = CreateCatalog(CatalogPath))
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
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE users (id Int32 IDENTITY(50, 2), name String)");
        }

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(Path.Combine(_tempDir, "data", "public", "users.datum"));
        Assert.Equal(0, reader.Footer.Prologue.IdentityColumnIndex);
        Assert.Equal(50, reader.Footer.Prologue.IdentitySeed);
        Assert.Equal(2, reader.Footer.Prologue.IdentityStep);
        Assert.Equal(50, reader.Footer.Prologue.IdentityNextValue); // not yet advanced
    }

    [Fact]
    public async Task InsertValues_PersistentTable_AdvancesCounterInFooter()
    {
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE users (id Int64 IDENTITY, name String)");
            catalog.Plan("INSERT INTO users (name) VALUES ('a'), ('b'), ('c')");
        }

        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(Path.Combine(_tempDir, "data", "public", "users.datum"));
        Assert.Equal(4, reader.Footer.Prologue.IdentityNextValue);

        // Independent verify: scan back and count rows.
        using TableCatalog reopened = CreateCatalog(CatalogPath);
        List<(long id, string name)> rows = await ScanLongFirstString(reopened["users"]);
        Assert.Equal([(1L, "a"), (2L, "b"), (3L, "c")], rows);
    }

    // ──────────────────── Probe #4: ALTER ADD IDENTITY backfill overflow ────────────────────

    [Fact]
    public async Task AlterTable_AddIdentity_BackfillOverflow_LeavesTableUnchanged()
    {
        // Int8 holds [-128, 127]. Seed 120, step 1 — backfill produces
        // 120, 121, ..., 127 cleanly, then needs 128 on row 9 →
        // overflow. The writer's AddColumn pumps inside its append
        // session; an OverflowException should abort the session via
        // dispose-without-commit, leaving the file in its pre-ALTER
        // state. The user-visible error should name the kind and the
        // overflow value.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, name String)");
        // 10 rows — backfill will overflow at row 9 (counter would be 128).
        for (int i = 1; i <= 10; i++)
        {
            catalog.Plan($"INSERT INTO t (id, name) VALUES ({i}, 'row{i}')");
        }

        Exception ex = Assert.ThrowsAny<Exception>(() =>
            catalog.Plan("ALTER TABLE t ADD COLUMN seq Int8 GENERATED ALWAYS AS IDENTITY(120, 1)"));
        Assert.Contains("Int8", ex.Message);

        // Table state must be unchanged: original 2 columns, 10 rows,
        // no `seq` column.
        Schema schema = catalog["t"].GetSchema();
        Assert.Equal(["id", "name"], schema.Columns.Select(c => c.Name));
        int rows = 0;
        await foreach (RowBatch batch in catalog["t"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            rows += batch.Count;
            batch.Dispose();
        }
        Assert.Equal(10, rows);

        // Retry with a wider kind should succeed — the failed ALTER
        // didn't poison the schema (and didn't tombstone any column
        // that would compete for the `seq` name).
        catalog.Plan("ALTER TABLE t ADD COLUMN seq Int32 GENERATED ALWAYS AS IDENTITY(120, 1)");
        Schema afterRetry = catalog["t"].GetSchema();
        Assert.Equal(["id", "name", "seq"], afterRetry.Columns.Select(c => c.Name));
    }

    [Fact]
    public async Task AlterTable_AddIdentity_PersistentTable_BackfillOverflow_LeavesTableUnchanged()
    {
        // Same scenario on a persistent table — the writer's
        // dispose-without-commit must roll back the file via torn-tail
        // recovery semantics. Reopening should still see the original
        // schema and rows.
        string tempDir = Path.Combine(Path.GetTempPath(), $"datum_idoverflow_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string catalogPath = Path.Combine(tempDir, ".datum-catalog.json");
            using (TableCatalog catalog = CreateCatalog(catalogPath))
            {
                catalog.Plan("CREATE TABLE t (id Int32, name String)");
                for (int i = 1; i <= 10; i++)
                {
                    catalog.Plan($"INSERT INTO t (id, name) VALUES ({i}, 'row{i}')");
                }
                Assert.ThrowsAny<Exception>(() =>
                    catalog.Plan("ALTER TABLE t ADD COLUMN seq Int8 GENERATED ALWAYS AS IDENTITY(120, 1)"));
            }

            using TableCatalog reopened = CreateCatalog(catalogPath);
            Schema schema = reopened["t"].GetSchema();
            Assert.Equal(["id", "name"], schema.Columns.Select(c => c.Name));
            int rows = 0;
            await foreach (RowBatch batch in reopened["t"].ScanAsync(
                requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
            {
                rows += batch.Count;
                batch.Dispose();
            }
            Assert.Equal(10, rows);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
        }
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

    // ──────────────────── GENERATED [ALWAYS|BY DEFAULT] AS IDENTITY ────────────────────

    [Fact]
    public void CreateTable_GeneratedAlwaysAsIdentity_BehavesLikeBareIdentity()
    {
        // The PG-canonical syntax `GENERATED ALWAYS AS IDENTITY` is the
        // direct replacement for bare `IDENTITY`. Default seed/step (1,1),
        // explicit values rejected.
        using TableCatalog catalog = CreateCatalog();

        catalog.Plan("CREATE TEMP TABLE t (id Int64 GENERATED ALWAYS AS IDENTITY, name String)");

        IdentitySpec spec = catalog["t"].GetSchema().Columns[0].Identity!;
        Assert.Equal(1, spec.Seed);
        Assert.Equal(1, spec.Step);
        Assert.False(spec.AcceptUserValues);
    }

    [Fact]
    public void CreateTable_GeneratedAlwaysAsIdentityWithSeedStep_KeepsValues()
    {
        using TableCatalog catalog = CreateCatalog();

        catalog.Plan("CREATE TEMP TABLE t (id Int32 GENERATED ALWAYS AS IDENTITY(100, 5))");

        IdentitySpec spec = catalog["t"].GetSchema().Columns[0].Identity!;
        Assert.Equal(100, spec.Seed);
        Assert.Equal(5, spec.Step);
        Assert.False(spec.AcceptUserValues);
    }

    [Fact]
    public void CreateTable_GeneratedByDefaultAsIdentity_SetsAcceptUserValuesTrue()
    {
        using TableCatalog catalog = CreateCatalog();

        catalog.Plan("CREATE TEMP TABLE t (id Int64 GENERATED BY DEFAULT AS IDENTITY, name String)");

        IdentitySpec spec = catalog["t"].GetSchema().Columns[0].Identity!;
        Assert.Equal(1, spec.Seed);
        Assert.Equal(1, spec.Step);
        Assert.True(spec.AcceptUserValues);
    }

    [Fact]
    public async Task Insert_AlwaysIdentity_RejectsExplicitValue()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int64 GENERATED ALWAYS AS IDENTITY, name String)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("INSERT INTO t (id, name) VALUES (99, 'forced')"));
        Assert.Contains("GENERATED ALWAYS AS IDENTITY", ex.Message);

        // Sanity: subsequent omitted-id insert still works.
        catalog.Plan("INSERT INTO t (name) VALUES ('after')");
        long id = -1;
        await foreach (RowBatch batch in catalog["t"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            Assert.Equal(1, batch.Count);
            id = batch[0][0].AsInt64();
            batch.Dispose();
        }
        Assert.Equal(1L, id);
    }

    [Fact]
    public async Task Insert_ByDefaultIdentity_AcceptsExplicitValue()
    {
        // GENERATED BY DEFAULT AS IDENTITY accepts user-supplied values
        // and only invokes the counter when the column is omitted. PG
        // semantics: the counter is NOT auto-advanced by explicit
        // inserts, so a later omitted INSERT can collide if the user
        // explicitly hit a soon-to-be-generated value (their problem).
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int64 GENERATED BY DEFAULT AS IDENTITY, name String)");

        catalog.Plan("INSERT INTO t (id, name) VALUES (42, 'explicit'), (43, 'also explicit')");
        catalog.Plan("INSERT INTO t (name) VALUES ('auto')");

        Dictionary<string, long> byName = new();
        await foreach (RowBatch batch in catalog["t"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                byName[batch[r][1].AsString(batch.Arena)] = batch[r][0].AsInt64();
            }
            batch.Dispose();
        }
        Assert.Equal(42L, byName["explicit"]);
        Assert.Equal(43L, byName["also explicit"]);
        Assert.Equal(1L, byName["auto"]);  // counter started at seed (1), independent of explicit inserts
    }

    [Fact]
    public async Task PersistentTable_ByDefaultIdentity_RoundTripsAcceptFlagAcrossReopen()
    {
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE t (id Int64 GENERATED BY DEFAULT AS IDENTITY, name String)");
            catalog.Plan("INSERT INTO t (id, name) VALUES (100, 'pre-reopen')");
        }

        using TableCatalog reopened = CreateCatalog(CatalogPath);
        IdentitySpec spec = reopened["t"].GetSchema().Columns[0].Identity!;
        Assert.True(spec.AcceptUserValues);

        // Explicit value still accepted after reopen.
        reopened.Plan("INSERT INTO t (id, name) VALUES (200, 'post-reopen')");

        long[] ids = Array.Empty<long>();
        await foreach (RowBatch batch in reopened["t"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            ids = new long[batch.Count];
            for (int r = 0; r < batch.Count; r++) ids[r] = batch[r][0].AsInt64();
            batch.Dispose();
        }
        Assert.Equal(new[] { 100L, 200L }, ids);
    }

    [Fact]
    public void CreateTable_GeneratedAlwaysAsExpression_RoutesToComputedColumn()
    {
        // Disambiguation check — `GENERATED ALWAYS AS (expr)` produces a
        // computed column, not an IDENTITY. The token after AS (`(` vs
        // `IDENTITY`) is the discriminator.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (a Int32, b Int32 GENERATED ALWAYS AS (a + 1))");

        Schema schema = catalog["t"].GetSchema();
        Assert.NotNull(schema.Columns[1].ComputedExpression);
        Assert.Null(schema.Columns[1].Identity);
    }

    // ──────────────────── Identity counter boundary handling ────────────────────

    [Fact]
    public void Insert_Int8Identity_PastMax_Throws()
    {
        // Int8 range is [-128, 127]. With seed=126, two omitted INSERTs
        // fill 126 and 127; the third reservation would advance the
        // counter to 128 — outside Int8 range. The error must surface
        // cleanly (no silent wrap to -128).
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int8 IDENTITY(126, 1), name String)");
        catalog.Plan("INSERT INTO t (name) VALUES ('a'), ('b')");

        Exception ex = Assert.ThrowsAny<Exception>(() =>
            catalog.Plan("INSERT INTO t (name) VALUES ('c')"));
        Assert.Contains("Int8", ex.Message);
    }

    [Fact]
    public void Insert_UInt32Identity_PastMax_Throws()
    {
        // UInt32 max is 4_294_967_295. Seed at max-1 → two inserts
        // succeed, third must throw with an UInt32-overflow message
        // (not silently wrap).
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id UInt32 IDENTITY(4294967294, 1), name String)");
        catalog.Plan("INSERT INTO t (name) VALUES ('a'), ('b')");

        Exception ex = Assert.ThrowsAny<Exception>(() =>
            catalog.Plan("INSERT INTO t (name) VALUES ('c')"));
        Assert.Contains("UInt32", ex.Message);
    }

    [Fact]
    public void Insert_Int8NegativeStep_PastMin_Throws()
    {
        // Int8 min is -128. Seed -127, step -1 → first INSERT fills -127,
        // second fills -128, third would advance to -129 → must throw.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int8 IDENTITY(-127, -1), name String)");
        catalog.Plan("INSERT INTO t (name) VALUES ('a'), ('b')");

        Exception ex = Assert.ThrowsAny<Exception>(() =>
            catalog.Plan("INSERT INTO t (name) VALUES ('c')"));
        Assert.Contains("Int8", ex.Message);
    }

    // ──────────────────── ALTER TABLE ADD COLUMN with IDENTITY ────────────────────

    [Fact]
    public async Task AlterTable_AddColumn_GeneratedAlwaysAsIdentity_BackfillsHistoricalRows()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (name String)");
        catalog.Plan("INSERT INTO t (name) VALUES ('alice'), ('bob'), ('carol')");

        catalog.Plan("ALTER TABLE t ADD COLUMN id Int64 GENERATED ALWAYS AS IDENTITY");

        // Historical rows get sequential identity values from the seed.
        Dictionary<string, long> ids = new();
        await foreach (RowBatch batch in catalog["t"].ScanAsync(
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

        // Subsequent INSERT continues the counter past the backfill.
        catalog.Plan("INSERT INTO t (name) VALUES ('dave')");
        long daveId = -1;
        await foreach (RowBatch batch in catalog["t"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                if (batch[r][0].AsString(batch.Arena) == "dave") daveId = batch[r][1].AsInt64();
            }
            batch.Dispose();
        }
        Assert.Equal(4L, daveId);
    }

    [Fact]
    public void AlterTable_AddColumn_Identity_WhenTableAlreadyHasIdentity_Throws()
    {
        // At most one IDENTITY column per table — adding a second via
        // ALTER must be rejected with a clear error.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int64 GENERATED ALWAYS AS IDENTITY, name String)");

        Exception ex = Assert.ThrowsAny<Exception>(() =>
            catalog.Plan("ALTER TABLE t ADD COLUMN seq Int64 GENERATED ALWAYS AS IDENTITY"));
        Assert.Contains("IDENTITY", ex.Message);
    }

    // ──────────────────── ALTER TABLE … ALTER COLUMN c DROP IDENTITY ────────────────────
    //
    // Clears the IDENTITY attribute from a column. Existing rows keep their
    // stored identity values; future INSERTs that omit the column write
    // NULL instead of the next counter value. The column itself stays on
    // the table.

    [Fact]
    public async Task AlterTable_DropIdentity_RemovesIdentitySpec()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE users (id Int64 GENERATED BY DEFAULT AS IDENTITY, name String)");
        catalog.Plan("INSERT INTO users (name) VALUES ('alice'), ('bob')");

        catalog.Plan("ALTER TABLE users ALTER COLUMN id DROP IDENTITY");

        Schema schema = catalog["users"].GetSchema();
        Assert.Null(schema.Columns[0].Identity);

        // Existing rows kept their values (1, 2). New INSERT that omits id
        // would have continued the counter previously; now it stores NULL.
        catalog.Plan("INSERT INTO users (name) VALUES ('carol')");
        long nullCount = 0;
        await foreach (RowBatch batch in catalog["users"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                if (batch[r][0].IsNull) nullCount++;
            }
            batch.Dispose();
        }
        Assert.Equal(1, nullCount);
    }

    [Fact]
    public void AlterTable_DropIdentity_OnNonIdentityColumn_Throws()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE users (id Int32, name String)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("ALTER TABLE users ALTER COLUMN id DROP IDENTITY"));
        Assert.Contains("IDENTITY", ex.Message);
    }

    [Fact]
    public void AlterTable_DropIdentity_OnNonIdentityColumn_IfExists_NoThrow()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE users (id Int32, name String)");

        catalog.Plan("ALTER TABLE users ALTER COLUMN id DROP IDENTITY IF EXISTS");

        Schema schema = catalog["users"].GetSchema();
        Assert.Null(schema.Columns[0].Identity);
    }

    [Fact]
    public void AlterTable_DropIdentity_UnknownColumn_Throws()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE users (id Int32, name String)");

        Exception ex = Assert.ThrowsAny<Exception>(() =>
            catalog.Plan("ALTER TABLE users ALTER COLUMN missing DROP IDENTITY"));
        Assert.Contains("missing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AlterTable_DropIdentity_SurvivesReopen()
    {
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE users (id Int64 GENERATED BY DEFAULT AS IDENTITY, name String)");
            catalog.Plan("ALTER TABLE users ALTER COLUMN id DROP IDENTITY");
        }

        using TableCatalog reopened = CreateCatalog(CatalogPath);
        Schema schema = reopened["users"].GetSchema();
        Assert.Null(schema.Columns[0].Identity);
    }
}
