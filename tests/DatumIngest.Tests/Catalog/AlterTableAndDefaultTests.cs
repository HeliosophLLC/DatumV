using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
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
        catalog.Plan("CREATE TEMP TABLE t (id Int32, ts DateTime DEFAULT now())");
        catalog.Plan("INSERT INTO t (id) VALUES (1), (2)");

        List<DateTimeOffset> stamps = new();
        await foreach (RowBatch batch in catalog["t"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                stamps.Add(batch[r][1].AsDateTime());
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

        catalog.Plan("ALTER TABLE t ADD COLUMN created_at DateTime DEFAULT now()");
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
                    row2Stamp = batch[r][1].AsDateTime();
                }
            }
            batch.Dispose();
        }
        Assert.NotNull(row2Stamp);
        Assert.True(DateTimeOffset.UtcNow - row2Stamp!.Value < TimeSpan.FromMinutes(1));
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
            catalog.Plan("CREATE TABLE events (id Int32, ts DateTime DEFAULT now())");
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
                rows.Add((batch[r][0].AsInt32(), batch[r][1].AsDateTime()));
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
        catalog.Plan("CREATE TEMP TABLE t (id Int32, ts DateTime DEFAULT now())");

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
}
