using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.DatumFile.Sidecar;
using DatumIngest.DatumFile.V2;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// PR10b tests for SQL <c>DEFAULT &lt;literal&gt;</c> on
/// <c>CREATE TABLE</c> columns and <c>ALTER TABLE ADD/DROP COLUMN</c>.
/// Cover: literal validation, defaults persisted via the v4 footer
/// prologue and surfaced through <see cref="ColumnInfo.DefaultExpression"/>,
/// catalog reopen, and the deliberate gaps (<c>ALTER ADD … DEFAULT</c>
/// and <c>ALTER ADD … NOT NULL</c> are rejected pending backfill).
/// </summary>
public sealed class AlterTableAndDefaultTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_pr10b_{Guid.NewGuid():N}");
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

    // ──────────────────── DEFAULT on CREATE TEMP TABLE ────────────────────

    [Fact]
    public void CreateTempTable_DefaultIntLiteral_StoredOnColumnInfo()
    {
        using TableCatalog catalog = CreateCatalog();

        catalog.Plan("CREATE TEMP TABLE t (a Int32 DEFAULT 5, b String)");

        Schema schema = catalog["t"].GetSchema();
        LiteralExpression literal = Assert.IsType<LiteralExpression>(schema.Columns[0].DefaultExpression);
        Assert.Equal(5L, Convert.ToInt64(literal.Value));
        Assert.Null(schema.Columns[1].DefaultExpression);
    }

    [Fact]
    public void CreateTempTable_DefaultStringLiteral_RoundTrips()
    {
        using TableCatalog catalog = CreateCatalog();

        catalog.Plan("CREATE TEMP TABLE t (status String DEFAULT 'pending')");

        Schema schema = catalog["t"].GetSchema();
        LiteralExpression literal = Assert.IsType<LiteralExpression>(schema.Columns[0].DefaultExpression);
        Assert.Equal("pending", literal.Value);
    }

    [Fact]
    public void CreateTempTable_DefaultNullLiteral_PreservedAsLiteral()
    {
        using TableCatalog catalog = CreateCatalog();

        catalog.Plan("CREATE TEMP TABLE t (a Int32 DEFAULT NULL)");

        Schema schema = catalog["t"].GetSchema();
        LiteralExpression literal = Assert.IsType<LiteralExpression>(schema.Columns[0].DefaultExpression);
        Assert.Null(literal.Value);
    }

    [Fact]
    public void CreateTempTable_DefaultNegativeNumber_AcceptedAsLiteral()
    {
        using TableCatalog catalog = CreateCatalog();

        catalog.Plan("CREATE TEMP TABLE t (a Int32 DEFAULT -7)");

        Expression? expr = catalog["t"].GetSchema().Columns[0].DefaultExpression;
        UnaryExpression unary = Assert.IsType<UnaryExpression>(expr);
        Assert.Equal(UnaryOperator.Negate, unary.Operator);
        LiteralExpression operand = Assert.IsType<LiteralExpression>(unary.Operand);
        Assert.Equal(7L, Convert.ToInt64(operand.Value));
    }

    [Fact]
    public void CreateTempTable_DefaultBoolLiteral_RoundTrips()
    {
        using TableCatalog catalog = CreateCatalog();

        catalog.Plan("CREATE TEMP TABLE t (flag Boolean DEFAULT true)");

        LiteralExpression literal = Assert.IsType<LiteralExpression>(
            catalog["t"].GetSchema().Columns[0].DefaultExpression);
        Assert.Equal(true, literal.Value);
    }

    [Fact]
    public async Task CreateTempTable_DefaultNow_EvaluatesPerRow()
    {
        // DEFAULT now() — each INSERTed row that omits the column gets a
        // fresh UtcNow read, so the two timestamps differ on any clock with
        // sub-tick resolution. The lift relaxes the old "must be a literal"
        // gate; per-row evaluation matches PostgreSQL semantics for
        // non-deterministic DEFAULTs.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, ts TimestampTz DEFAULT now())");
        catalog.Plan("INSERT INTO t (id) VALUES (1), (2)");

        List<DateTimeOffset> stamps = new();
        await foreach (RowBatch batch in catalog["t"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                stamps.Add(batch[r][1].AsTimestampTz());
            }
            batch.Dispose();
        }

        Assert.Equal(2, stamps.Count);
        // Both timestamps are recent (within the last minute) and ordered.
        Assert.True(stamps[0] <= stamps[1], $"Expected non-decreasing timestamps; got {stamps[0]:O} then {stamps[1]:O}.");
        Assert.True(DateTimeOffset.UtcNow - stamps[0] < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task CreateTempTable_DefaultArithmeticExpression_EvaluatesAtInsertTime()
    {
        // 1 + 2 — accepted as a tableless expression; folded at evaluation
        // time, not at parse time, so the row reads 3.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, n Int32 DEFAULT 1 + 2)");
        catalog.Plan("INSERT INTO t (id) VALUES (10)");

        await foreach (RowBatch batch in catalog["t"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            Assert.Equal(1, batch.Count);
            Assert.Equal(3, batch[0][1].AsInt32());
            batch.Dispose();
        }
    }

    [Fact]
    public async Task CreateTempTable_DefaultStringConcat_EvaluatesPerRow()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, label String DEFAULT 'row-' || 'x')");
        catalog.Plan("INSERT INTO t (id) VALUES (1)");

        await foreach (RowBatch batch in catalog["t"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            Assert.Equal("row-x", batch[0][1].AsString(batch.Arena));
            batch.Dispose();
        }
    }

    [Fact]
    public async Task CreateTempTable_DefaultUuidV4_DistinctPerRow()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, key Uuid DEFAULT uuidv4())");
        catalog.Plan("INSERT INTO t (id) VALUES (1), (2), (3)");

        HashSet<Guid> uuids = new();
        await foreach (RowBatch batch in catalog["t"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                uuids.Add(batch[r][1].AsUuid());
            }
            batch.Dispose();
        }

        Assert.Equal(3, uuids.Count);
    }

    [Fact]
    public void CreateTempTable_DefaultColumnReference_Rejected()
    {
        // DEFAULT expressions evaluate against an empty frame, so a column
        // reference would never resolve. Reject at CREATE TABLE.
        using TableCatalog catalog = CreateCatalog();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE TEMP TABLE t (a Int32, b Int32 DEFAULT a)"));
        Assert.Contains("column reference", ex.Message);
    }

    [Fact]
    public void CreateTempTable_DefaultSubquery_Rejected()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE src (n Int32)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE TEMP TABLE t (id Int32, n Int32 DEFAULT (SELECT n FROM src LIMIT 1))"));
        Assert.Contains("subquery", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────── DEFAULT on persistent CREATE TABLE ────────────────────

    [Fact]
    public void CreatePersistentTable_DefaultsPersistedInFooterPrologue()
    {
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE users (id Int32, status String DEFAULT 'pending', score Int32 DEFAULT -1)");
        }

        // Read the .datum file directly and inspect the prologue.
        string datumPath = Path.Combine(_tempDir, "users.datum");
        using DatumFileReaderV2 reader = DatumFileReaderV2.Open(datumPath);
        Assert.Equal(2, reader.Footer.Prologue.ColumnDefaults.Count);

        Dictionary<ushort, string> bySlot = reader.Footer.Prologue.ColumnDefaults
            .ToDictionary(d => d.ColumnIndex, d => d.SqlFragment);
        Assert.True(bySlot.ContainsKey(1));
        Assert.True(bySlot.ContainsKey(2));
        // Fragments are SQL — exact format depends on QueryExplainer but
        // both should round-trip back to the parsed expressions.
        Assert.Contains("pending", bySlot[1]);
        Assert.Contains("1", bySlot[2]);
    }

    [Fact]
    public void CreatePersistentTable_ReopenedCatalog_RestoresDefaultsOnSchema()
    {
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE users (id Int32, status String DEFAULT 'pending')");
        }

        using TableCatalog reopened = CreateCatalog(CatalogPath);
        Schema schema = reopened["users"].GetSchema();

        Assert.Null(schema.Columns[0].DefaultExpression);
        LiteralExpression literal = Assert.IsType<LiteralExpression>(schema.Columns[1].DefaultExpression);
        Assert.Equal("pending", literal.Value);
    }

    // ──────────────────── ALTER TABLE ADD COLUMN ────────────────────

    [Fact]
    public void AlterTable_AddColumn_OnTempTable_AppendsNullableColumn()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32)");

        catalog.Plan("ALTER TABLE t ADD COLUMN name String");

        Schema schema = catalog["t"].GetSchema();
        Assert.Equal(2, schema.Columns.Count);
        Assert.Equal("name", schema.Columns[1].Name);
        Assert.Equal(DataKind.String, schema.Columns[1].Kind);
        Assert.True(schema.Columns[1].Nullable);
    }

    [Fact]
    public void AlterTable_AddColumn_OptionalColumnKeyword_Accepted()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32)");

        // COLUMN keyword is optional.
        catalog.Plan("ALTER TABLE t ADD score Float64");

        Schema schema = catalog["t"].GetSchema();
        Assert.Equal("score", schema.Columns[1].Name);
        Assert.Equal(DataKind.Float64, schema.Columns[1].Kind);
    }

    [Fact]
    public async Task AlterTable_AddColumn_WithDefault_NewInsertsAutoFill()
    {
        // ALTER ADD COLUMN now accepts DEFAULT. Pre-existing rows read
        // NULL (column wasn't present); INSERTs after the ALTER that
        // omit the new column pick up the DEFAULT.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32)");
        catalog.Plan("INSERT INTO t VALUES (1), (2)");

        catalog.Plan("ALTER TABLE t ADD COLUMN status String DEFAULT 'pending'");

        // Insert a row without supplying the new column — should DEFAULT-fill.
        catalog.Plan("INSERT INTO t (id) VALUES (3)");

        await foreach (RowBatch batch in catalog["t"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            Assert.Equal(3, batch.Count);
            // Existing rows backfilled NULL.
            Assert.True(batch[0][1].IsNull);
            Assert.True(batch[1][1].IsNull);
            // New row picks up the DEFAULT.
            Assert.Equal("pending", batch[2][1].AsString(batch.Arena));
            batch.Dispose();
        }
    }

    [Fact]
    public async Task AlterTable_AddColumn_WithDefaultNow_EvaluatesOnNewInserts()
    {
        // ALTER ADD COLUMN ... DEFAULT now() — accepted under the lifted
        // validator; existing rows still read NULL (no historical backfill
        // yet, per V2-F2), new INSERTs that omit the column get a fresh
        // UtcNow timestamp.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32)");
        catalog.Plan("INSERT INTO t (id) VALUES (1)");

        catalog.Plan("ALTER TABLE t ADD COLUMN created_at TimestampTz DEFAULT now()");
        catalog.Plan("INSERT INTO t (id) VALUES (2)");

        DateTimeOffset? row2Stamp = null;
        await foreach (RowBatch batch in catalog["t"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                if (batch[r][0].AsInt32() == 1)
                {
                    Assert.True(batch[r][1].IsNull);
                }
                else if (batch[r][0].AsInt32() == 2)
                {
                    row2Stamp = batch[r][1].AsTimestampTz();
                }
            }
            batch.Dispose();
        }
        Assert.NotNull(row2Stamp);
        Assert.True(DateTimeOffset.UtcNow - row2Stamp!.Value < TimeSpan.FromMinutes(1));
    }

    // ──────────────────── Sidecar registry survives DROP COLUMN ────────────────────

    [Fact]
    public async Task AlterTable_BackfillRollback_KeepsLaterScansOfSidecarBackedColumnsWorking()
    {
        // Regression for the user-reported QA scenario: after a failed
        // ALTER ADD COLUMN whose Path C rollback drops the half-added
        // column, a subsequent SELECT that reads a previously-existing
        // sidecar-backed column used to throw
        // `Cannot access a disposed object. Object name: 'SidecarReadStore'`.
        //
        // Root cause: RebuildSnapshotAfterMutation only updated the
        // sidecar registry when sidecarMayHaveGrown=true, but
        // SwapSnapshot disposes the old sidecar unconditionally. After
        // DropColumn (which passes sidecarMayHaveGrown=false), the
        // registry's pointer was left dangling at a disposed sidecar.
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE y (id Int32, content String)");
        catalog.Plan("INSERT INTO y (id, content) VALUES (1, 'alice'), (2, 'bob')");

        // First ALTER: sidecar grows during V2-F2 backfill (UInt8[]
        // values from sha256 go through VariableSlot which spills).
        catalog.Plan("ALTER TABLE y ADD COLUMN hash UInt8[] AS (sha256(content))");

        // Second ALTER: backfill fails (cast String → Int32 on
        // non-numeric data). Path C rollback drops the half-added
        // column. The DropColumn rebuild used to disconnect the
        // registry from the live sidecar.
        Assert.ThrowsAny<Exception>(() =>
            catalog.Plan("ALTER TABLE y ADD COLUMN bad Int32 AS (content)"));

        // After rollback, reading the still-live sidecar-backed `hash`
        // column's bytes must succeed. The original repro routed
        // through WebCellFormatter.Format → value.AsByteSpan(arena,
        // registry), which is the path that hit the disposed sidecar
        // via the catalog's SidecarRegistry. Reproduce that exact path.
        SidecarRegistry registry = catalog.SidecarRegistry;
        int rowsRead = 0;
        await foreach (RowBatch batch in catalog["y"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                DataValue hash = batch[r][2];
                Assert.False(hash.IsNull);
                // sha256 yields a 32-byte digest. AsByteSpan goes through
                // the registry for sidecar-backed values — this is the
                // assertion that fails when the registry's pointer is
                // stale.
                ReadOnlySpan<byte> bytes = hash.AsByteSpan(batch.Arena, registry);
                Assert.Equal(32, bytes.Length);
                rowsRead++;
            }
            batch.Dispose();
        }
        Assert.Equal(2, rowsRead);
    }

    // ──────────────────── DROP + ADD: name reuse after tombstone ────────────────────

    [Fact]
    public async Task AlterTable_DropThenAddSameName_TempTable_Works()
    {
        // In-memory provider: DropColumn removes the column outright,
        // so re-adding under the same name has always worked. Pin the
        // happy path so any future tombstoning change in the in-memory
        // provider stays compatible.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, hash String)");
        catalog.Plan("INSERT INTO t (id, hash) VALUES (1, 'abc')");

        catalog.Plan("ALTER TABLE t DROP COLUMN hash");
        catalog.Plan("ALTER TABLE t ADD COLUMN hash UInt8[]");

        Schema schema = catalog["t"].GetSchema();
        Assert.Equal(["id", "hash"], schema.Columns.Select(c => c.Name));
        Assert.Equal(DataKind.UInt8, schema.Columns[1].Kind);
        Assert.True(schema.Columns[1].IsArray);

        // Pre-existing row's hash is NULL (the original `hash` column was
        // dropped; the new one is unrelated).
        await foreach (RowBatch batch in catalog["t"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            Assert.Equal(1, batch.Count);
            Assert.True(batch[0][1].IsNull);
            batch.Dispose();
        }
    }

    [Fact]
    public async Task AlterTable_DropThenAddSameName_PersistentTable_Works()
    {
        // Persistent provider: DropColumn soft-tombstones the footer
        // entry. Adding a new column with the same name must succeed —
        // the tombstoned entry stays in the footer for compaction-time
        // reclamation, but the live column slot is fresh.
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE y (id Int32, hash String)");
        catalog.Plan("INSERT INTO y (id, hash) VALUES (1, 'first'), (2, 'second')");

        catalog.Plan("ALTER TABLE y DROP COLUMN hash");
        catalog.Plan("ALTER TABLE y ADD COLUMN hash UInt8[]");

        Schema schema = catalog["y"].GetSchema();
        Assert.Equal(["id", "hash"], schema.Columns.Select(c => c.Name));
        Assert.Equal(DataKind.UInt8, schema.Columns[1].Kind);
        Assert.True(schema.Columns[1].IsArray);

        // Two existing rows + new column → both rows have NULL for the
        // fresh `hash` column.
        int rowsSeen = 0;
        await foreach (RowBatch batch in catalog["y"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                Assert.True(batch[r][1].IsNull);
                rowsSeen++;
            }
            batch.Dispose();
        }
        Assert.Equal(2, rowsSeen);
    }

    [Fact]
    public async Task AlterTable_DropThenAddSameName_NewColumnAcceptsInserts()
    {
        // After DROP + ADD, the new column should behave like any other
        // column — INSERTs that supply a value land in the new slot, not
        // the tombstoned one.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, hash String)");
        catalog.Plan("INSERT INTO t (id, hash) VALUES (1, 'old')");

        catalog.Plan("ALTER TABLE t DROP COLUMN hash");
        catalog.Plan("ALTER TABLE t ADD COLUMN hash Int32");
        catalog.Plan("INSERT INTO t (id, hash) VALUES (2, 42)");

        Dictionary<int, int?> rows = new();
        await foreach (RowBatch batch in catalog["t"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                int id = batch[r][0].AsInt32();
                rows[id] = batch[r][1].IsNull ? null : batch[r][1].AsInt32();
            }
            batch.Dispose();
        }
        Assert.Null(rows[1]);
        Assert.Equal(42, rows[2]);
    }

    // ──────────────────── Probe #2: DROP+ADD chain reuse ────────────────────

    [Fact]
    public async Task AlterTable_DropAddDropAddChain_LiveColumnIsLatestAdd()
    {
        // Each ADD after a DROP introduces a fresh footer entry while
        // the prior tombstoned ones linger. The live column should
        // always be the most recent ADD; reads should see ONLY that
        // column under the shared name; the second DROP should target
        // the live column (not silently no-op on a tombstoned one).
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE t (id Int32, hash Int32)");

        catalog.Plan("ALTER TABLE t DROP COLUMN hash");
        catalog.Plan("ALTER TABLE t ADD COLUMN hash String");

        catalog.Plan("ALTER TABLE t DROP COLUMN hash");
        catalog.Plan("ALTER TABLE t ADD COLUMN hash UInt8[]");

        Schema schema = catalog["t"].GetSchema();
        Assert.Equal(["id", "hash"], schema.Columns.Select(c => c.Name));
        Assert.Equal(DataKind.UInt8, schema.Columns[1].Kind);
        Assert.True(schema.Columns[1].IsArray);

        // INSERT against the live (UInt8[]) hash works.
        catalog.Plan("INSERT INTO t (id) VALUES (1)");
        int rows = 0;
        await foreach (RowBatch batch in catalog["t"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                Assert.Equal(1, batch[r][0].AsInt32());
                Assert.True(batch[r][1].IsNull);  // newly-added column reads NULL for INSERTs that omitted it
                rows++;
            }
            batch.Dispose();
        }
        Assert.Equal(1, rows);
    }

    // ──────────────────── Probe #3: persistent reopen after DROP+ADD ────────────────────

    [Fact]
    public async Task AlterTable_DropAddDifferentKind_PersistentReopen_SeesLiveKind()
    {
        // After DROP+ADD with a different kind, closing and reopening
        // the catalog must surface the LIVE column (latest ADD) — not
        // the tombstoned one. The reader's tombstone filter must
        // resolve correctly when the footer has two entries sharing a
        // name with different kinds.
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE t (id Int32, hash Int32)");
            catalog.Plan("INSERT INTO t (id, hash) VALUES (1, 42)");
            catalog.Plan("ALTER TABLE t DROP COLUMN hash");
            catalog.Plan("ALTER TABLE t ADD COLUMN hash String");
            catalog.Plan("INSERT INTO t (id, hash) VALUES (2, 'after')");
        }

        // Reopen — schema must show the live String hash, not the
        // tombstoned Int32.
        using TableCatalog reopened = CreateCatalog(CatalogPath);
        Schema schema = reopened["t"].GetSchema();
        Assert.Equal(["id", "hash"], schema.Columns.Select(c => c.Name));
        Assert.Equal(DataKind.String, schema.Columns[1].Kind);
        Assert.False(schema.Columns[1].IsArray);

        // Existing rows: row 1 (pre-drop) reads NULL for the new
        // String hash; row 2 (post-drop+add) reads 'after'.
        Dictionary<int, string?> hashes = new();
        await foreach (RowBatch batch in reopened["t"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                hashes[batch[r][0].AsInt32()] = batch[r][1].IsNull
                    ? null
                    : batch[r][1].AsString(batch.Arena);
            }
            batch.Dispose();
        }
        Assert.Null(hashes[1]);
        Assert.Equal("after", hashes[2]);
    }

    [Fact]
    public void AlterTable_DropThenAddSameName_RejectsDoubleAdd()
    {
        // After a DROP+ADD, the live column is `hash`. A second ADD
        // without a DROP between should still be rejected against the
        // live column (not against the tombstoned one).
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, hash String)");
        catalog.Plan("ALTER TABLE t DROP COLUMN hash");
        catalog.Plan("ALTER TABLE t ADD COLUMN hash Int32");

        Exception ex = Assert.ThrowsAny<Exception>(() =>
            catalog.Plan("ALTER TABLE t ADD COLUMN hash Int64"));
        Assert.Contains("hash", ex.Message);
    }

    [Fact]
    public void AlterTable_AddColumn_WithDefaultColumnReference_Rejected()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("ALTER TABLE t ADD COLUMN n Int32 DEFAULT id"));
        Assert.Contains("column reference", ex.Message);
    }

    [Fact]
    public void AlterTable_AddColumn_WithNotNull_Throws_PendingBackfill()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("ALTER TABLE t ADD COLUMN score Int32 NOT NULL"));
        Assert.Contains("NOT NULL", ex.Message);
    }

    [Fact]
    public void AlterTable_AddColumn_OnPersistentTable_PersistsAcrossReopen()
    {
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE users (id Int32)");
            catalog.Plan("ALTER TABLE users ADD COLUMN name String");
        }

        using TableCatalog reopened = CreateCatalog(CatalogPath);
        Schema schema = reopened["users"].GetSchema();
        Assert.Equal(["id", "name"], schema.Columns.Select(c => c.Name));
    }

    // ──────────────────── ALTER TABLE DROP COLUMN ────────────────────

    [Fact]
    public void AlterTable_DropColumn_OnTempTable_RemovesColumn()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, name String, score Int32)");

        catalog.Plan("ALTER TABLE t DROP COLUMN name");

        Schema schema = catalog["t"].GetSchema();
        Assert.Equal(["id", "score"], schema.Columns.Select(c => c.Name));
    }

    [Fact]
    public void AlterTable_DropColumn_OptionalColumnKeyword_Accepted()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, name String)");

        catalog.Plan("ALTER TABLE t DROP name");

        Assert.Equal(["id"], catalog["t"].GetSchema().Columns.Select(c => c.Name));
    }

    [Fact]
    public void AlterTable_DropColumn_Missing_Throws()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("ALTER TABLE t DROP COLUMN nope"));
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public void AlterTable_DropColumn_IfExists_NoOpWhenMissing()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32)");

        // Should not throw.
        catalog.Plan("ALTER TABLE t DROP COLUMN IF EXISTS nope");

        Assert.Equal(["id"], catalog["t"].GetSchema().Columns.Select(c => c.Name));
    }

    [Fact]
    public void AlterTable_DropColumn_OnPersistentTable_PersistsAcrossReopen()
    {
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE users (id Int32, name String)");
            catalog.Plan("ALTER TABLE users DROP COLUMN name");
        }

        using TableCatalog reopened = CreateCatalog(CatalogPath);
        Schema schema = reopened["users"].GetSchema();
        Assert.Equal(["id"], schema.Columns.Select(c => c.Name));
    }

    [Fact]
    public void AlterTable_OnMissingTable_Throws()
    {
        using TableCatalog catalog = CreateCatalog();

        Assert.ThrowsAny<Exception>(() =>
            catalog.Plan("ALTER TABLE nope ADD COLUMN x Int32"));
    }

    // ──────────────────── DEFAULT expression persistence ────────────────────

    [Fact]
    public async Task PersistentTable_DefaultNow_SurvivesReopen_AndStillEvaluatesPerRow()
    {
        // The DEFAULT expression persists as a SQL fragment via the v4
        // footer prologue. After reopen, INSERTs that omit the column
        // should re-evaluate the same expression — picking up a fresh
        // UtcNow on every INSERT.
        using (TableCatalog catalog = CreateCatalog( CatalogPath))
        {
            catalog.Plan("CREATE TABLE events (id Int32, ts TimestampTz DEFAULT now())");
            catalog.Plan("INSERT INTO events (id) VALUES (1)");
        }

        using TableCatalog reopened = CreateCatalog(CatalogPath);
        Schema schema = reopened["events"].GetSchema();
        Assert.NotNull(schema.Columns[1].DefaultExpression);
        Assert.IsType<FunctionCallExpression>(schema.Columns[1].DefaultExpression);

        // INSERT after reopen still evaluates the expression freshly.
        reopened.Plan("INSERT INTO events (id) VALUES (2)");

        List<(int Id, DateTimeOffset Ts)> rows = new();
        await foreach (RowBatch batch in reopened["events"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                rows.Add((batch[r][0].AsInt32(), batch[r][1].AsTimestampTz()));
            }
            batch.Dispose();
        }
        Assert.Equal(2, rows.Count);
        // Row 2 was inserted second; both timestamps should be recent.
        Assert.True(DateTimeOffset.UtcNow - rows[0].Ts < TimeSpan.FromMinutes(1));
        Assert.True(DateTimeOffset.UtcNow - rows[1].Ts < TimeSpan.FromMinutes(1));
    }

    // ──────────────────── DEFAULT type validation at CREATE/ALTER time ────────────────────

    [Fact]
    public void CreateTempTable_DefaultFloatOnIntColumn_RejectsAtCreateTime()
    {
        // `DEFAULT 3.14` on an Int32 column is type-incompatible — the
        // catalog should reject at CREATE TABLE, not let the failure
        // surface at INSERT time when an omitted-column resolution kicks
        // off the runtime coercion.
        using TableCatalog catalog = CreateCatalog();

        Exception ex = Assert.ThrowsAny<Exception>(() =>
            catalog.Plan("CREATE TEMP TABLE t (n Int32 DEFAULT 3.14)"));
        Assert.Contains("Int32", ex.Message);
    }

    [Fact]
    public void CreateTempTable_DefaultStringOnIntColumn_RejectsAtCreateTime()
    {
        using TableCatalog catalog = CreateCatalog();

        Exception ex = Assert.ThrowsAny<Exception>(() =>
            catalog.Plan("CREATE TEMP TABLE t (n Int32 DEFAULT 'oops')"));
        Assert.Contains("Int32", ex.Message);
    }

    [Fact]
    public void CreateTempTable_DefaultIntOnStringColumn_RejectsAtCreateTime()
    {
        using TableCatalog catalog = CreateCatalog();

        Exception ex = Assert.ThrowsAny<Exception>(() =>
            catalog.Plan("CREATE TEMP TABLE t (s String DEFAULT 5)"));
        Assert.Contains("String", ex.Message);
    }

    [Fact]
    public void CreateTempTable_DefaultNowOnDateTime_StillAccepted()
    {
        // Sanity: eager validation must not reject deterministic +
        // non-deterministic function-call DEFAULTs that DO coerce
        // correctly. Only the explicit type mismatches should fail.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, ts TimestampTz DEFAULT now())");

        Schema schema = catalog["t"].GetSchema();
        Assert.NotNull(schema.Columns[1].DefaultExpression);
    }

    [Fact]
    public void AlterTable_AddColumn_DefaultFloatOnIntColumn_RejectsAtAlterTime()
    {
        // Same eager type-validation at ALTER ADD COLUMN time.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32)");

        Exception ex = Assert.ThrowsAny<Exception>(() =>
            catalog.Plan("ALTER TABLE t ADD COLUMN n Int32 DEFAULT 3.14"));
        Assert.Contains("Int32", ex.Message);
    }

    // ──────────────────── ALTER TABLE … ALTER COLUMN c DROP DEFAULT ────────────────────
    //
    // Clears a column's DEFAULT expression so future INSERTs that omit
    // the column write NULL rather than the previously-configured default.
    // Existing rows are unaffected (the default never backfilled them).

    [Fact]
    public void AlterTable_DropDefault_ClearsDefaultExpression()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE t (id Int32, label String DEFAULT 'unknown')");

        catalog.Plan("ALTER TABLE t ALTER COLUMN label DROP DEFAULT");

        Schema schema = catalog["t"].GetSchema();
        Assert.Null(schema.Columns[1].DefaultExpression);
    }

    [Fact]
    public async Task AlterTable_DropDefault_OmittedColumn_StoresNullAfterDrop()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE t (id Int32, label String DEFAULT 'unknown')");

        // Pre-drop INSERT omits label → default fills 'unknown'.
        catalog.Plan("INSERT INTO t (id) VALUES (1)");

        catalog.Plan("ALTER TABLE t ALTER COLUMN label DROP DEFAULT");

        // Post-drop INSERT omits label → NULL.
        catalog.Plan("INSERT INTO t (id) VALUES (2)");

        int beforeWithDefault = 0;
        int afterNull = 0;
        await foreach (RowBatch batch in catalog["t"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            Arena arena = batch.Arena;
            for (int r = 0; r < batch.Count; r++)
            {
                int id = batch[r][0].AsInt32();
                DataValue label = batch[r][1];
                if (id == 1 && !label.IsNull && label.AsString(arena) == "unknown") beforeWithDefault++;
                if (id == 2 && label.IsNull) afterNull++;
            }
            batch.Dispose();
        }
        Assert.Equal(1, beforeWithDefault);
        Assert.Equal(1, afterNull);
    }

    [Fact]
    public void AlterTable_DropDefault_IsIdempotent()
    {
        // PG treats DROP DEFAULT as idempotent (no error if no default);
        // we match that behavior regardless of IF EXISTS.
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE t (id Int32, label String)");

        // No default to begin with — must not throw.
        catalog.Plan("ALTER TABLE t ALTER COLUMN label DROP DEFAULT");

        Schema schema = catalog["t"].GetSchema();
        Assert.Null(schema.Columns[1].DefaultExpression);
    }

    [Fact]
    public void AlterTable_DropDefault_UnknownColumn_Throws()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE t (id Int32)");

        Exception ex = Assert.ThrowsAny<Exception>(() =>
            catalog.Plan("ALTER TABLE t ALTER COLUMN missing DROP DEFAULT"));
        Assert.Contains("missing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AlterTable_DropDefault_SurvivesReopen()
    {
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE t (id Int32, label String DEFAULT 'unknown')");
            catalog.Plan("ALTER TABLE t ALTER COLUMN label DROP DEFAULT");
        }

        using TableCatalog reopened = CreateCatalog(CatalogPath);
        Schema schema = reopened["t"].GetSchema();
        Assert.Null(schema.Columns[1].DefaultExpression);
    }

    // ──────────────────── ALTER TABLE … ALTER COLUMN c DROP NOT NULL ────────────────────
    //
    // Relaxes NOT NULL on an existing column. Existing rows are untouched
    // (the encoder already either wrote bitmaps or not — per-page flag on
    // PageDescriptorV2 records which); future INSERTs may write NULL.

    [Fact]
    public void AlterTable_DropNotNull_ClearsNullability()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE t (id Int32 NOT NULL, name String)");

        catalog.Plan("ALTER TABLE t ALTER COLUMN id DROP NOT NULL");

        Schema schema = catalog["t"].GetSchema();
        Assert.True(schema.Columns[0].Nullable);
    }

    [Fact]
    public async Task AlterTable_DropNotNull_PreExistingRowsRoundTrip_NewNullAccepted()
    {
        // Pre-drop the column was NOT NULL; existing rows have non-null values
        // stored in pages WITHOUT a null bitmap. After DROP NOT NULL, future
        // INSERTs can supply NULL — those rows land in new pages WITH a null
        // bitmap. Both kinds of page must round-trip correctly.
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE t (id Int32 NOT NULL, name String)");
        catalog.Plan("INSERT INTO t VALUES (1, 'alice'), (2, 'bob')");

        catalog.Plan("ALTER TABLE t ALTER COLUMN id DROP NOT NULL");
        catalog.Plan("INSERT INTO t (name) VALUES ('carol')");

        Dictionary<string, int?> seen = new();
        await foreach (RowBatch batch in catalog["t"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            Arena arena = batch.Arena;
            for (int r = 0; r < batch.Count; r++)
            {
                string name = batch[r][1].AsString(arena);
                seen[name] = batch[r][0].IsNull ? null : batch[r][0].AsInt32();
            }
            batch.Dispose();
        }
        Assert.Equal(1, seen["alice"]);
        Assert.Equal(2, seen["bob"]);
        Assert.Null(seen["carol"]);
    }

    [Fact]
    public void AlterTable_DropNotNull_OnAlreadyNullableColumn_Throws()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE t (id Int32, name String)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("ALTER TABLE t ALTER COLUMN id DROP NOT NULL"));
        Assert.Contains("NOT NULL", ex.Message);
    }

    [Fact]
    public void AlterTable_DropNotNull_OnAlreadyNullableColumn_IfExists_NoThrow()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE t (id Int32, name String)");

        catalog.Plan("ALTER TABLE t ALTER COLUMN id DROP NOT NULL IF EXISTS");

        Schema schema = catalog["t"].GetSchema();
        Assert.True(schema.Columns[0].Nullable);
    }

    [Fact]
    public void AlterTable_DropNotNull_UnknownColumn_Throws()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE t (id Int32 NOT NULL)");

        Exception ex = Assert.ThrowsAny<Exception>(() =>
            catalog.Plan("ALTER TABLE t ALTER COLUMN missing DROP NOT NULL"));
        Assert.Contains("missing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────── ALTER TABLE … ALTER COLUMN c SET NOT NULL ────────────────────
    //
    // Tightens a column back to NOT NULL. Unlike DROP, this requires a
    // scan: every existing row must be non-null or the SET is rejected.
    // Pre-existing pages keep their bitmap (now redundant, decodes
    // correctly anyway); future INSERTs reject NULL.

    [Fact]
    public void AlterTable_SetNotNull_OnCleanColumn_Succeeds()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE t (id Int32, name String)");
        catalog.Plan("INSERT INTO t VALUES (1, 'alice'), (2, 'bob')");

        catalog.Plan("ALTER TABLE t ALTER COLUMN id SET NOT NULL");

        Schema schema = catalog["t"].GetSchema();
        Assert.False(schema.Columns[0].Nullable);
    }

    [Fact]
    public void AlterTable_SetNotNull_OnColumnWithNulls_Rejects()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE t (id Int32, name String)");
        catalog.Plan("INSERT INTO t VALUES (1, 'alice'), (2, 'bob')");
        catalog.Plan("INSERT INTO t (name) VALUES ('carol')"); // id = NULL

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("ALTER TABLE t ALTER COLUMN id SET NOT NULL"));
        Assert.Contains("NULL", ex.Message);
        Assert.Contains("id", ex.Message);

        // The descriptor must NOT have been flipped — failed validation
        // shouldn't have any side effects.
        Schema schema = catalog["t"].GetSchema();
        Assert.True(schema.Columns[0].Nullable);
    }

    [Fact]
    public void AlterTable_SetNotNull_AfterDropThenSet_RejectsNewNullInserts()
    {
        // Round-trip: drop NOT NULL, then set it back. Subsequent INSERTs
        // must reject NULL again — i.e. the descriptor flip really took
        // and the runtime check fires.
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE t (id Int32 NOT NULL, name String)");
        catalog.Plan("INSERT INTO t VALUES (1, 'alice')");
        catalog.Plan("ALTER TABLE t ALTER COLUMN id DROP NOT NULL");
        catalog.Plan("INSERT INTO t (name) VALUES ('bob')"); // id = NULL — accepted while nullable
        catalog.Plan("DELETE FROM t WHERE name = 'bob'");    // clean out the NULL row

        catalog.Plan("ALTER TABLE t ALTER COLUMN id SET NOT NULL");

        // Pre-existing pages have heterogeneous bitmap state but no NULLs
        // among live rows. Future NULL insert must be rejected.
        Exception ex = Assert.ThrowsAny<Exception>(() =>
            catalog.Plan("INSERT INTO t (name) VALUES ('carol')"));
        Assert.Contains("id", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AlterTable_SetNotNull_OnAlreadyNotNullColumn_Idempotent()
    {
        // PG accepts SET NOT NULL on a column that already has the
        // constraint as a no-op. Match that.
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE t (id Int32 NOT NULL, name String)");

        catalog.Plan("ALTER TABLE t ALTER COLUMN id SET NOT NULL");

        Schema schema = catalog["t"].GetSchema();
        Assert.False(schema.Columns[0].Nullable);
    }

    [Fact]
    public void AlterTable_SetNotNull_UnknownColumn_Throws()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE t (id Int32, name String)");

        Exception ex = Assert.ThrowsAny<Exception>(() =>
            catalog.Plan("ALTER TABLE t ALTER COLUMN missing SET NOT NULL"));
        Assert.Contains("missing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AlterTable_SetNotNull_SurvivesReopen()
    {
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE t (id Int32, name String)");
            catalog.Plan("INSERT INTO t VALUES (1, 'alice')");
            catalog.Plan("ALTER TABLE t ALTER COLUMN id SET NOT NULL");
        }

        using TableCatalog reopened = CreateCatalog(CatalogPath);
        Schema schema = reopened["t"].GetSchema();
        Assert.False(schema.Columns[0].Nullable);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task AlterTable_DropNotNull_SurvivesReopen()
    {
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE t (id Int32 NOT NULL, name String)");
            catalog.Plan("INSERT INTO t VALUES (1, 'alice')");
            catalog.Plan("ALTER TABLE t ALTER COLUMN id DROP NOT NULL");
            catalog.Plan("INSERT INTO t (name) VALUES ('bob')");
        }

        using TableCatalog reopened = CreateCatalog(CatalogPath);
        Schema schema = reopened["t"].GetSchema();
        Assert.True(schema.Columns[0].Nullable);

        // Both the pre-drop NOT-NULL-encoded page and the post-drop
        // nullable-encoded page must decode correctly after reopen.
        List<(string Name, int? Id)> rows = new();
        await foreach (RowBatch batch in reopened["t"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            Arena arena = batch.Arena;
            for (int r = 0; r < batch.Count; r++)
            {
                rows.Add((batch[r][1].AsString(arena),
                    batch[r][0].IsNull ? null : batch[r][0].AsInt32()));
            }
            batch.Dispose();
        }
        Assert.Contains(("alice", (int?)1), rows);
        Assert.Contains(("bob", (int?)null), rows);
    }
}
