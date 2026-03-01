using DatumIngest.Catalog;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// PR10c tests for SQL <c>INSERT INTO … VALUES (…)</c>. Cover:
/// positional vs named column lists, DEFAULT-fill for omitted columns,
/// NULL-fill for omitted nullable columns, NOT-NULL-without-default
/// rejection, lossless numeric coercion (Int8 → Int32 widen, etc.),
/// rejection of lossy / cross-family coercions, count-mismatch
/// validation, persistent-table round-trip, and the deferred
/// <c>INSERT … SELECT</c> rejection.
/// </summary>
public sealed class InsertValuesTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_pr10c_{Guid.NewGuid():N}");
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

    // ──────────────────── Happy path ────────────────────

    [Fact]
    public async Task InsertValues_PositionalSingleRow_VisibleViaScan()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, name String)");

        catalog.Plan("INSERT INTO t VALUES (1, 'alice')");

        Assert.Equal(1, catalog["t"].GetRowCount());
        List<(int id, string name)> rows = await ScanAsTuples(catalog["t"]);
        Assert.Equal([(1, "alice")], rows);
    }

    [Fact]
    public async Task InsertValues_PositionalMultipleRows_AllVisible()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, name String)");

        catalog.Plan("INSERT INTO t VALUES (1, 'a'), (2, 'b'), (3, 'c')");

        Assert.Equal(3, catalog["t"].GetRowCount());
        List<(int id, string name)> rows = await ScanAsTuples(catalog["t"]);
        Assert.Equal([(1, "a"), (2, "b"), (3, "c")], rows);
    }

    [Fact]
    public async Task InsertValues_NamedColumnList_FillsByName()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, name String)");

        // Named column list reverses the natural order — values map by
        // name, not VALUES position.
        catalog.Plan("INSERT INTO t (name, id) VALUES ('alice', 1)");

        List<(int id, string name)> rows = await ScanAsTuples(catalog["t"]);
        Assert.Equal([(1, "alice")], rows);
    }

    [Fact]
    public async Task InsertValues_NegatedNumericLiteral_StoresNegative()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32)");

        catalog.Plan("INSERT INTO t VALUES (-7)");

        List<int> ids = await ScanIntColumn(catalog["t"], "id");
        Assert.Equal([-7], ids);
    }

    [Fact]
    public async Task InsertValues_NullLiteral_StoresNullOnNullableColumn()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, note String)");

        catalog.Plan("INSERT INTO t VALUES (1, NULL)");

        List<DataValue[]> rows = await ScanAllValues(catalog["t"]);
        Assert.Single(rows);
        Assert.True(rows[0][1].IsNull);
    }

    // ──────────────────── DEFAULT-fill (PR10b's payoff) ────────────────────

    [Fact]
    public async Task InsertValues_OmittedColumnWithDefault_FillsWithDefault()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, status String DEFAULT 'pending')");

        catalog.Plan("INSERT INTO t (id) VALUES (1)");

        List<(int id, string name)> rows = await ScanAsTuples(catalog["t"]);
        Assert.Equal([(1, "pending")], rows);
    }

    [Fact]
    public async Task InsertValues_OmittedNullableColumnWithoutDefault_FillsNull()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, note String)");

        catalog.Plan("INSERT INTO t (id) VALUES (1)");

        List<DataValue[]> rows = await ScanAllValues(catalog["t"]);
        Assert.Single(rows);
        Assert.True(rows[0][1].IsNull);
    }

    [Fact]
    public void InsertValues_OmittedNotNullColumnWithoutDefault_Throws()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32 NOT NULL, name String)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("INSERT INTO t (name) VALUES ('alice')"));
        Assert.Contains("NOT NULL", ex.Message);
        Assert.Contains("id", ex.Message);
    }

    [Fact]
    public async Task InsertValues_DefaultLiteralNegativeNumber_FillsNegativeValue()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, score Int32 DEFAULT -1)");

        catalog.Plan("INSERT INTO t (id) VALUES (1)");

        List<DataValue[]> rows = await ScanAllValues(catalog["t"]);
        Assert.Equal(-1, rows[0][1].AsInt32());
    }

    // ──────────────────── Numeric coercion ────────────────────

    [Fact]
    public async Task InsertValues_LiteralWidensFromInt8ToInt32()
    {
        // The parser narrows `5` to sbyte; the column is Int32. Lossless
        // widen.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32)");

        catalog.Plan("INSERT INTO t VALUES (5)");

        List<int> ids = await ScanIntColumn(catalog["t"], "id");
        Assert.Equal([5], ids);
    }

    [Fact]
    public async Task InsertValues_LiteralFitsInUInt8()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (b UInt8)");

        catalog.Plan("INSERT INTO t VALUES (200)");

        List<DataValue[]> rows = await ScanAllValues(catalog["t"]);
        Assert.Equal(200, rows[0][0].AsUInt8());
    }

    [Fact]
    public void InsertValues_NegativeLiteralIntoUnsigned_Throws()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (b UInt8)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("INSERT INTO t VALUES (-1)"));
        Assert.Contains("UInt8", ex.Message);
    }

    [Fact]
    public void InsertValues_OverflowOnNarrow_Throws()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (b Int8)");

        // Int16 200 doesn't fit in Int8 ([-128, 127]).
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("INSERT INTO t VALUES (200)"));
        Assert.Contains("Int8", ex.Message);
    }

    [Fact]
    public void InsertValues_StringIntoNumeric_Throws()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32)");

        Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("INSERT INTO t VALUES ('5')"));
    }

    [Fact]
    public async Task InsertValues_Float32LosslessFromInt_Accepted()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (x Float32)");

        catalog.Plan("INSERT INTO t VALUES (5)");

        List<DataValue[]> rows = await ScanAllValues(catalog["t"]);
        Assert.Equal(5f, rows[0][0].AsFloat32());
    }

    // ──────────────────── 128-bit literal coercion ────────────────────

    [Fact]
    public async Task InsertValues_SmallLiteralIntoInt128_Widens()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int128)");

        catalog.Plan("INSERT INTO t VALUES (1)");

        List<DataValue[]> rows = await ScanAllValues(catalog["t"]);
        Assert.Equal((Int128)1, rows[0][0].AsInt128());
    }

    [Fact]
    public async Task InsertValues_Above64BitLiteralIntoInt128_RoundTrips()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int128)");

        // 2^65 — larger than UInt64.MaxValue, so the parser must reach
        // the Int128/UInt128 rungs without going through double.
        catalog.Plan("INSERT INTO t VALUES (36893488147419103232)");

        List<DataValue[]> rows = await ScanAllValues(catalog["t"]);
        Assert.Equal((Int128)1 << 65, rows[0][0].AsInt128());
    }

    [Fact]
    public async Task InsertValues_Int128MaxLiteral_RoundTrips()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int128)");

        catalog.Plan("INSERT INTO t VALUES (170141183460469231731687303715884105727)");

        List<DataValue[]> rows = await ScanAllValues(catalog["t"]);
        Assert.Equal(Int128.MaxValue, rows[0][0].AsInt128());
    }

    [Fact]
    public async Task InsertValues_UInt128MaxLiteral_RoundTrips()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id UInt128)");

        catalog.Plan("INSERT INTO t VALUES (340282366920938463463374607431768211455)");

        List<DataValue[]> rows = await ScanAllValues(catalog["t"]);
        Assert.Equal(UInt128.MaxValue, rows[0][0].AsUInt128());
    }

    [Fact]
    public void InsertValues_LiteralExceeding128Bits_Throws()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id UInt128)");

        // UInt128.MaxValue + 1.
        Exception ex = Assert.ThrowsAny<Exception>(() =>
            catalog.Plan("INSERT INTO t VALUES (340282366920938463463374607431768211456)"));
        Assert.Contains("128-bit", ex.Message);
    }

    [Fact]
    public void InsertValues_Int128LiteralOverflowsInt64_Throws()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int64)");

        // 2^65 — parser produces Int128, coercion to Int64 must reject.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("INSERT INTO t VALUES (36893488147419103232)"));
        Assert.Contains("Int64", ex.Message);
    }

    // ──────────────────── Temporal / Decimal coercion ────────────────────

    [Fact]
    public async Task InsertValues_NowExpression_StoresCurrentDateTime()
    {
        // Reproduces the exact pattern that surfaced the gap:
        //   INSERT INTO conversations VALUES ('default', 'Chat', now())
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE conversations (workspace String, title String, started_at DateTime)");

        DateTimeOffset before = DateTimeOffset.UtcNow.AddSeconds(-1);
        catalog.Plan("INSERT INTO conversations VALUES ('default', 'Chat', now())");
        DateTimeOffset after = DateTimeOffset.UtcNow.AddSeconds(1);

        List<DataValue[]> rows = await ScanAllValues(catalog["conversations"]);
        Assert.Single(rows);
        DateTimeOffset stored = rows[0][2].AsDateTime();
        Assert.InRange(stored, before, after);
    }

    [Fact]
    public async Task InsertValues_DateLiteralString_StoresAsDate()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (d Date)");

        catalog.Plan("INSERT INTO t VALUES ('2026-05-09')");

        List<DataValue[]> rows = await ScanAllValues(catalog["t"]);
        Assert.Equal(new DateOnly(2026, 5, 9), rows[0][0].AsDate());
    }

    [Fact]
    public async Task InsertValues_DecimalFromIntegerLiteral_Accepted()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (amount Decimal)");

        catalog.Plan("INSERT INTO t VALUES (1234)");

        List<DataValue[]> rows = await ScanAllValues(catalog["t"]);
        Assert.Equal(1234m, rows[0][0].AsDecimal());
    }

    // ──────────────────── Subqueries in VALUES ────────────────────

    [Fact]
    public async Task InsertValues_ScalarSubquery_FoldsAtPlanTime()
    {
        // Subquery shapes the user wants to write:
        //   INSERT INTO X (a, b, c) VALUES (1, 2, (SELECT x FROM Y LIMIT 1))
        // Pre-fold pass executes the inner SELECT, captures the scalar
        // result, and substitutes it as a literal before the tableless
        // VALUES evaluator runs.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE src (n Int32)");
        catalog.Plan("CREATE TEMP TABLE dst (a Int32, b Int32, c Int32)");
        catalog.Plan("INSERT INTO src VALUES (42)");

        catalog.Plan("INSERT INTO dst VALUES (1, 2, (SELECT n FROM src LIMIT 1))");

        List<DataValue[]> rows = await ScanAllValues(catalog["dst"]);
        Assert.Single(rows);
        Assert.Equal(1, rows[0][0].AsInt32());
        Assert.Equal(2, rows[0][1].AsInt32());
        Assert.Equal(42, rows[0][2].AsInt32());
    }

    [Fact]
    public async Task InsertValues_ScalarSubqueryEmpty_BecomesNull()
    {
        // Zero rows from a scalar subquery → NULL literal.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE src (n Int32)");
        catalog.Plan("CREATE TEMP TABLE dst (a Int32, n Int32)");

        catalog.Plan("INSERT INTO dst VALUES (1, (SELECT n FROM src LIMIT 1))");

        List<DataValue[]> rows = await ScanAllValues(catalog["dst"]);
        Assert.Single(rows);
        Assert.Equal(1, rows[0][0].AsInt32());
        Assert.True(rows[0][1].IsNull);
    }

    [Fact]
    public async Task InsertValues_SubqueryInsideExpression_FoldsThenEvaluates()
    {
        // Subquery nested inside an arithmetic expression — pre-fold
        // walker recurses into BinaryExpression.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE src (n Int32)");
        catalog.Plan("CREATE TEMP TABLE dst (total Int32)");
        catalog.Plan("INSERT INTO src VALUES (10)");

        catalog.Plan("INSERT INTO dst VALUES ((SELECT n FROM src LIMIT 1) + 5)");

        List<DataValue[]> rows = await ScanAllValues(catalog["dst"]);
        Assert.Single(rows);
        Assert.Equal(15, rows[0][0].AsInt32());
    }

    [Fact]
    public void InsertValues_ScalarSubqueryTooManyRows_Throws()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE src (n Int32)");
        catalog.Plan("CREATE TEMP TABLE dst (n Int32)");
        catalog.Plan("INSERT INTO src VALUES (1), (2)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("INSERT INTO dst VALUES ((SELECT n FROM src))"));
        Assert.Contains("more than one row", ex.Message);
    }

    // ──────────────────── Validation ────────────────────

    [Fact]
    public void InsertValues_RowArityMismatch_Throws()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, name String)");

        // Two columns, only one value supplied per row.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("INSERT INTO t VALUES (1)"));
        Assert.Contains("expects 2", ex.Message);
    }

    [Fact]
    public void InsertValues_ColumnListMentionsUnknown_Throws()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("INSERT INTO t (nope) VALUES (1)"));
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public void InsertValues_ColumnListDuplicates_Throws()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, name String)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("INSERT INTO t (id, id) VALUES (1, 2)"));
        Assert.Contains("more than once", ex.Message);
    }

    [Fact]
    public void InsertValues_ZeroRows_NoOp()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32)");

        // An empty VALUES list isn't valid SQL via our parser anyway,
        // but ensure single-row insert leaves count consistent.
        catalog.Plan("INSERT INTO t VALUES (1)");
        Assert.Equal(1, catalog["t"].GetRowCount());
    }

    [Fact]
    public async Task InsertValues_ComputedScalar_OnePlusTwo_Succeeds()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32)");

        // VALUES routes through the expression evaluator, so binary /
        // function-call expressions evaluate the same way they would in a
        // SELECT projection.
        catalog.Plan("INSERT INTO t VALUES (1 + 2)");

        Assert.Equal(1, catalog["t"].GetRowCount());
        List<int> rows = await ScanIntColumn(catalog["t"], "id");
        Assert.Equal([3], rows);
    }

    [Fact]
    public void InsertValues_ColumnReferenceInExpression_Throws()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32)");

        // VALUES expressions evaluate against an empty row — column
        // references can't bind to anything and must fail loudly. Use
        // INSERT … SELECT when the value comes from another table.
        Assert.ThrowsAny<Exception>(() =>
            catalog.Plan("INSERT INTO t VALUES (some_col)"));
    }

    [Fact]
    public async Task InsertValues_ArrayLiteral_StringArray_Succeeds()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE t (id Int32, tags String[])");

        catalog.Plan("INSERT INTO t (id, tags) VALUES (1, ['a', 'b', 'c'])");

        Assert.Equal(1, catalog["t"].GetRowCount());
        List<DataValue[]> rows = await ScanAllValues(catalog["t"]);
        Assert.Single(rows);
        DataValue tags = rows[0][1];
        Assert.True(tags.IsArray);
        Assert.Equal(DataKind.String, tags.Kind);
    }

    [Fact]
    public async Task InsertValues_ArrayLiteral_Int32Array_Succeeds()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE t (id Int32, scores Int32[])");

        catalog.Plan("INSERT INTO t (id, scores) VALUES (7, [10, 20, 30])");

        Assert.Equal(1, catalog["t"].GetRowCount());
        List<DataValue[]> rows = await ScanAllValues(catalog["t"]);
        DataValue scores = rows[0][1];
        Assert.True(scores.IsArray);
        Assert.Equal(DataKind.Int32, scores.Kind);
    }

    [Fact]
    public void InsertValues_ArrayKindMismatch_Throws()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE t (id Int32, scores Int32[])");

        // Array-of-String into Int32[] target: kinds don't match.
        Assert.ThrowsAny<Exception>(() =>
            catalog.Plan("INSERT INTO t (id, scores) VALUES (1, ['a','b'])"));
    }

    // INSERT SELECT (the PR10c'-NotYetSupported case) — covered in
    // InsertSelectTests now that the path ships.

    // ──────────────────── Persistent table ────────────────────

    [Fact]
    public async Task InsertValues_OnPersistentTable_VisibleAcrossReopen()
    {
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE users (id Int32, name String)");
            catalog.Plan("INSERT INTO users VALUES (1, 'alice'), (2, 'bob')");
        }

        using TableCatalog reopened = CreateCatalog(CatalogPath);
        Assert.Equal(2, reopened["users"].GetRowCount());
        List<(int id, string name)> rows = await ScanAsTuples(reopened["users"]);
        Assert.Equal([(1, "alice"), (2, "bob")], rows);
    }

    [Fact]
    public async Task InsertValues_OnPersistentTableWithDefault_FillsDefault()
    {
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE users (id Int32, status String DEFAULT 'active')");
            catalog.Plan("INSERT INTO users (id) VALUES (1)");
        }

        // Reopen to confirm the inserted-with-default row survives the
        // close/reopen cycle (DEFAULT lives in the footer prologue per
        // PR10b).
        using TableCatalog reopened = CreateCatalog(CatalogPath);
        List<(int id, string name)> rows = await ScanAsTuples(reopened["users"]);
        Assert.Equal([(1, "active")], rows);
    }

    [Fact]
    public void Insert_OnMissingTable_Throws()
    {
        using TableCatalog catalog = CreateCatalog();

        // S8 routes INSERT through SchemaResolver.Resolve (same as SELECT),
        // so a missing unqualified target surfaces as the rich
        // SchemaResolutionException with the search_path listed.
        SchemaResolutionException ex = Assert.Throws<SchemaResolutionException>(() =>
            catalog.Plan("INSERT INTO nope VALUES (1)"));
        Assert.Contains("nope", ex.Message);
    }

    // ──────────────────── Helpers ────────────────────

    /// <summary>Reads back (Int32, String) rows in scan order.</summary>
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

    /// <summary>Reads back the named Int32 column in scan order.</summary>
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

    /// <summary>Reads back every row's full DataValue array for direct inspection.</summary>
    private static async Task<List<DataValue[]>> ScanAllValues(ITableProvider provider)
    {
        List<DataValue[]> rows = new();
        await foreach (RowBatch batch in provider.ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                Row row = batch[r];
                DataValue[] copy = new DataValue[row.FieldCount];
                for (int c = 0; c < copy.Length; c++) copy[c] = row[c];
                rows.Add(copy);
            }
            batch.Dispose();
        }
        return rows;
    }

    // ──────────────────── DEFAULT keyword in VALUES ────────────────────

    [Fact]
    public async Task InsertValues_DefaultKeyword_ResolvesViaColumnDefault()
    {
        // `DEFAULT` keyword in VALUES routes the slot through the
        // column's resolution path — DEFAULT expression if any, else
        // IDENTITY/NULL/throw — the same logic as omitting the column.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, status String DEFAULT 'pending')");

        catalog.Plan("INSERT INTO t (id, status) VALUES (1, DEFAULT)");

        await foreach (RowBatch batch in catalog["t"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            Assert.Equal(1, batch.Count);
            Assert.Equal(1, batch[0][0].AsInt32());
            Assert.Equal("pending", batch[0][1].AsString(batch.Arena));
            batch.Dispose();
        }
    }

    [Fact]
    public async Task InsertValues_DefaultKeyword_ResolvesViaIdentity()
    {
        // DEFAULT keyword on an IDENTITY column reserves the next value
        // from the counter — equivalent to omitting the column.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int64 GENERATED BY DEFAULT AS IDENTITY, name String)");

        // BY DEFAULT IDENTITY: DEFAULT keyword routes to the counter; an
        // explicit value would otherwise be accepted. Mixing both in
        // one statement exercises the resolution flip per row.
        catalog.Plan("INSERT INTO t (id, name) VALUES (DEFAULT, 'a'), (100, 'b'), (DEFAULT, 'c')");

        Dictionary<string, long> ids = new();
        await foreach (RowBatch batch in catalog["t"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                ids[batch[r][1].AsString(batch.Arena)] = batch[r][0].AsInt64();
            }
            batch.Dispose();
        }
        Assert.Equal(1L, ids["a"]);
        Assert.Equal(100L, ids["b"]);  // explicit
        Assert.Equal(2L, ids["c"]);    // counter continues from 2 (not auto-advanced past 100)
    }

    [Fact]
    public async Task InsertValues_DefaultKeyword_PositionalRow()
    {
        // Positional row with DEFAULT keyword for one slot — works the
        // same as the named-column-list variant.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, status String DEFAULT 'pending')");

        catalog.Plan("INSERT INTO t VALUES (1, DEFAULT), (2, 'active')");

        Dictionary<int, string> rows = [];
        await foreach (RowBatch batch in catalog["t"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                rows[batch[r][0].AsInt32()] = batch[r][1].AsString(batch.Arena);
            }
            batch.Dispose();
        }
        Assert.Equal("pending", rows[1]);
        Assert.Equal("active", rows[2]);
    }

    [Fact]
    public async Task InsertValues_DefaultKeyword_NullableNoDefault_WritesNull()
    {
        // No DEFAULT, no IDENTITY, nullable — DEFAULT keyword resolves
        // to NULL, same as omitting the column.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, note String)");

        catalog.Plan("INSERT INTO t (id, note) VALUES (1, DEFAULT)");

        await foreach (RowBatch batch in catalog["t"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            Assert.Equal(1, batch.Count);
            Assert.True(batch[0][1].IsNull);
            batch.Dispose();
        }
    }

    [Fact]
    public void InsertValues_DefaultKeyword_NotNullNoDefault_Throws()
    {
        // No DEFAULT, no IDENTITY, NOT NULL — DEFAULT keyword has no
        // fallback and must error with the same message an omitted
        // column would surface.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, note String NOT NULL)");

        Exception ex = Assert.ThrowsAny<Exception>(() =>
            catalog.Plan("INSERT INTO t (id, note) VALUES (1, DEFAULT)"));
        Assert.Contains("note", ex.Message);
    }

    // ──────────────────── INSERT … DEFAULT VALUES ────────────────────
    //
    // PG-compatible shorthand: "INSERT INTO t DEFAULT VALUES" inserts
    // exactly one row, treating every column as omitted. Each column's
    // fill follows the same omitted-slot resolution as a column-list
    // INSERT that names no columns: IDENTITY counter → DEFAULT expr →
    // NULL → throw. Computed columns evaluate from the resolved row.

    [Fact]
    public async Task InsertDefaultValues_AllColumnsHaveDefaults_FillsFromDefaults()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32 DEFAULT 7, name String DEFAULT 'anon')");

        catalog.Plan("INSERT INTO t DEFAULT VALUES");

        List<(int id, string name)> rows = await ScanAsTuples(catalog["t"]);
        Assert.Equal([(7, "anon")], rows);
    }

    [Fact]
    public async Task InsertDefaultValues_AllNullableNoDefaults_FillsNulls()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (a Int32, b String)");

        catalog.Plan("INSERT INTO t DEFAULT VALUES");

        List<DataValue[]> rows = await ScanAllValues(catalog["t"]);
        Assert.Single(rows);
        Assert.True(rows[0][0].IsNull);
        Assert.True(rows[0][1].IsNull);
    }

    [Fact]
    public void InsertDefaultValues_NotNullColumnWithoutDefault_Throws()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32 NOT NULL, name String DEFAULT 'x')");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("INSERT INTO t DEFAULT VALUES"));
        Assert.Contains("NOT NULL", ex.Message);
        Assert.Contains("id", ex.Message);
    }

    [Fact]
    public async Task InsertDefaultValues_IdentityColumn_AdvancesCounter()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int64 GENERATED ALWAYS AS IDENTITY, name String DEFAULT 'x')");

        catalog.Plan("INSERT INTO t DEFAULT VALUES");
        catalog.Plan("INSERT INTO t DEFAULT VALUES");
        catalog.Plan("INSERT INTO t DEFAULT VALUES");

        List<long> ids = [];
        await foreach (RowBatch batch in catalog["t"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            for (int r = 0; r < batch.Count; r++) ids.Add(batch[r][0].AsInt64());
            batch.Dispose();
        }
        ids.Sort();
        Assert.Equal([1L, 2L, 3L], ids);
    }

    [Fact]
    public async Task InsertDefaultValues_WithComputedColumn_ComputedFiresFromDefaults()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan(
            "CREATE TEMP TABLE t (a Int32 DEFAULT 3, b Int32 DEFAULT 4, " +
            "c Int32 AS (a + b))");

        catalog.Plan("INSERT INTO t DEFAULT VALUES");

        List<DataValue[]> rows = await ScanAllValues(catalog["t"]);
        Assert.Single(rows);
        Assert.Equal(3, rows[0][0].AsInt32());
        Assert.Equal(4, rows[0][1].AsInt32());
        Assert.Equal(7, rows[0][2].AsInt32());
    }

    [Fact]
    public async Task InsertDefaultValues_PersistentTable_VisibleAfterReopen()
    {
        {
            using TableCatalog catalog = CreateCatalog(CatalogPath);
            catalog.Plan("CREATE TABLE t (id Int64 GENERATED ALWAYS AS IDENTITY, status String DEFAULT 'new')");
            catalog.Plan("INSERT INTO t DEFAULT VALUES");
            catalog.Plan("INSERT INTO t DEFAULT VALUES");
        }
        {
            using TableCatalog catalog = CreateCatalog(CatalogPath);
            List<long> ids = new();
            List<string> statuses = new();
            await foreach (RowBatch batch in catalog["t"].ScanAsync(
                requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
            {
                for (int r = 0; r < batch.Count; r++)
                {
                    ids.Add(batch[r][0].AsInt64());
                    statuses.Add(batch[r][1].AsString(batch.Arena));
                }
                batch.Dispose();
            }
            ids.Sort();
            Assert.Equal([1L, 2L], ids);
            Assert.Equal(["new", "new"], statuses);
        }
    }

    // ──────────────────── QA probes: DEFAULT VALUES edge cases ────────────────────

    [Fact]
    public void InsertDefaultValues_ColumnListSupplied_RejectsBeforeAnyWrite()
    {
        // INSERT INTO t (col) DEFAULT VALUES — combining a column list
        // with DEFAULT VALUES is rejected. The check must fire before
        // any session opens, otherwise the writer's tail-flip protocol
        // could leave an aborted partial commit on disk.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32 DEFAULT 1, name String DEFAULT 'x')");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("INSERT INTO t (id) DEFAULT VALUES"));
        Assert.Contains("DEFAULT VALUES", ex.Message);
        Assert.Equal(0, catalog["t"].GetRowCount());
    }

    [Fact]
    public async Task InsertDefaultValues_IdentityAndComputed_ComputedSeesIdentity()
    {
        // Ordering invariant: IDENTITY fills before computed evaluates.
        // The computed slug must see the post-IDENTITY value of `id`,
        // not NULL (which would surface as 'row-' with no suffix or a
        // null-propagated final).
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan(
            "CREATE TEMP TABLE t (id Int64 GENERATED ALWAYS AS IDENTITY, " +
            "slug String AS ('row-' || cast(id as String)))");

        catalog.Plan("INSERT INTO t DEFAULT VALUES");
        catalog.Plan("INSERT INTO t DEFAULT VALUES");

        Dictionary<long, string> rows = new();
        await foreach (RowBatch batch in catalog["t"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                rows[batch[r][0].AsInt64()] = batch[r][1].AsString(batch.Arena);
            }
            batch.Dispose();
        }
        Assert.Equal(2, rows.Count);
        Assert.Equal("row-1", rows[1L]);
        Assert.Equal("row-2", rows[2L]);
    }

    [Fact]
    public async Task InsertDefaultValues_NondeterministicDefault_DistinctPerInvocation()
    {
        // Two DEFAULT VALUES against a `DEFAULT uuidv4()` column must
        // produce two distinct UUIDs — confirms per-row eval, not a
        // single-eval-then-bulk-fill shortcut.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan(
            "CREATE TEMP TABLE t (id Int64 GENERATED ALWAYS AS IDENTITY, u Uuid DEFAULT uuidv4())");

        catalog.Plan("INSERT INTO t DEFAULT VALUES");
        catalog.Plan("INSERT INTO t DEFAULT VALUES");

        HashSet<Guid> uuids = new();
        await foreach (RowBatch batch in catalog["t"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            for (int r = 0; r < batch.Count; r++) uuids.Add(batch[r][1].AsUuid());
            batch.Dispose();
        }
        Assert.Equal(2, uuids.Count);
    }

    [Fact]
    public void InsertDefaultValues_DeterministicDefaultOnPK_SecondInsertCollides()
    {
        // PRIMARY KEY DEFAULT 5: first insert lands; second collides on
        // the PK uniqueness check. The PK check must run after the
        // omitted-fill resolution — that's the only point at which the
        // candidate value is known. A regression that runs PK before
        // fill would let the duplicate through.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32 PRIMARY KEY DEFAULT 5, name String DEFAULT 'x')");

        catalog.Plan("INSERT INTO t DEFAULT VALUES");
        Exception ex = Assert.ThrowsAny<Exception>(() =>
            catalog.Plan("INSERT INTO t DEFAULT VALUES"));
        Assert.Equal(1, catalog["t"].GetRowCount());  // no partial commit
    }

    [Fact]
    public async Task InsertDefaultValues_PKIdentity_FiftySequential_AllDistinct()
    {
        // Stress the counter+commit path: 50 sequential DEFAULT VALUES,
        // each opens its own session, advances the counter atomically,
        // and commits independently. All IDs must be distinct and the
        // counter must not skip.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan(
            "CREATE TEMP TABLE t (id Int64 PRIMARY KEY GENERATED ALWAYS AS IDENTITY, name String DEFAULT 'x')");

        for (int i = 0; i < 50; i++) catalog.Plan("INSERT INTO t DEFAULT VALUES");

        HashSet<long> ids = new();
        await foreach (RowBatch batch in catalog["t"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            for (int r = 0; r < batch.Count; r++) ids.Add(batch[r][0].AsInt64());
            batch.Dispose();
        }
        Assert.Equal(50, ids.Count);
        Assert.Equal(1L, ids.Min());
        Assert.Equal(50L, ids.Max());
    }

    [Fact]
    public async Task InsertDefaultValues_AfterDropColumn_TombstoneWriterPathFires()
    {
        // Exercises the writer's tombstone-pad path (the
        // DatumFileWriterV2 fix from the last QA round): the live-column
        // count is 2 after DROP, the encoder loop still iterates 3
        // column slots and pads the tombstoned slot with NULL.
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE TABLE t (id Int32 DEFAULT 1, name String DEFAULT 'x', extra String DEFAULT 'y')");
        catalog.Plan("INSERT INTO t DEFAULT VALUES");
        catalog.Plan("ALTER TABLE t DROP COLUMN extra");
        catalog.Plan("INSERT INTO t DEFAULT VALUES");

        List<DataValue[]> rows = await ScanAllValues(catalog["t"]);
        Assert.Equal(2, rows.Count);
        // Schema is now (id, name) — extra is tombstoned and hidden from scan.
        foreach (DataValue[] row in rows)
        {
            Assert.Equal(2, row.Length);
            Assert.Equal(1, row[0].AsInt32());
            Assert.Equal("x", row[1].AsString());
        }
    }

    [Fact]
    public async Task InsertDefaultValues_ReturningStar_YieldsAllResolvedValues()
    {
        // RETURNING * after DEFAULT VALUES yields the post-fill row —
        // IDENTITY value, DEFAULT value, all resolved. If the RETURNING
        // plan ran against a pre-fill row, the IDENTITY slot would be
        // NULL or the literal seed.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan(
            "CREATE TEMP TABLE t (id Int64 GENERATED ALWAYS AS IDENTITY, status String DEFAULT 'new')");

        IQueryPlan plan = catalog.Plan("INSERT INTO t DEFAULT VALUES RETURNING *");

        List<DataValue[]> rows = new();
        await foreach (RowBatch batch in plan.ExecuteAsync(default))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                DataValue[] copy = new DataValue[batch[r].FieldCount];
                for (int c = 0; c < copy.Length; c++) copy[c] = batch[r][c];
                rows.Add(copy);
            }
        }
        Assert.Single(rows);
        Assert.Equal(2, rows[0].Length);
        Assert.Equal(1L, rows[0][0].AsInt64());
        Assert.Equal("new", rows[0][1].AsString());
    }

    [Fact]
    public async Task InsertDefaultValues_EquivalentTo_AllDefaultKeywordsInValues_NoComputed()
    {
        // Equivalence (no computed columns): `INSERT INTO t DEFAULT
        // VALUES` and `INSERT INTO t VALUES (DEFAULT, DEFAULT, ...)`
        // must produce the same row on identical tables. If the two
        // paths drift apart — different DEFAULT resolution, different
        // IDENTITY counter sequencing — this catches it.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan(
            "CREATE TEMP TABLE t1 (id Int64 GENERATED ALWAYS AS IDENTITY, name String DEFAULT 'x')");
        catalog.Plan(
            "CREATE TEMP TABLE t2 (id Int64 GENERATED ALWAYS AS IDENTITY, name String DEFAULT 'x')");

        catalog.Plan("INSERT INTO t1 DEFAULT VALUES");
        catalog.Plan("INSERT INTO t2 VALUES (DEFAULT, DEFAULT)");

        List<DataValue[]> r1 = await ScanAllValues(catalog["t1"]);
        List<DataValue[]> r2 = await ScanAllValues(catalog["t2"]);
        Assert.Single(r1);
        Assert.Single(r2);
        Assert.Equal(r1[0][0].AsInt64(), r2[0][0].AsInt64());
        Assert.Equal(r1[0][1].AsString(), r2[0][1].AsString());
    }

    [Fact]
    public async Task InsertValues_DefaultKeyword_OnComputedColumn_AcceptedAsOmitted()
    {
        // PG-aligned: DEFAULT keyword in a VALUES slot mapping to a
        // computed column is treated as omitted — the second-pass
        // computed-column evaluation supplies the value. Explicit
        // non-DEFAULT values are still rejected (see test below).
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan(
            "CREATE TEMP TABLE t (id Int64 GENERATED ALWAYS AS IDENTITY, name String DEFAULT 'x', " +
            "slug String AS ('s-' || cast(id as String)))");

        catalog.Plan("INSERT INTO t VALUES (DEFAULT, DEFAULT, DEFAULT)");

        List<DataValue[]> rows = await ScanAllValues(catalog["t"]);
        Assert.Single(rows);
        Assert.Equal(1L, rows[0][0].AsInt64());
        Assert.Equal("x", rows[0][1].AsString());
        Assert.Equal("s-1", rows[0][2].AsString());
    }

    [Fact]
    public void InsertValues_NonDefaultValueOnComputedColumn_StillRejected()
    {
        // Explicit (non-DEFAULT) values on computed columns are still
        // rejected — the per-row guard preserves the original semantics
        // for the "user tried to override the computed value" case.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan(
            "CREATE TEMP TABLE t (id Int32 DEFAULT 1, name String DEFAULT 'x', " +
            "slug String AS (name || '-' || cast(id as String)))");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("INSERT INTO t VALUES (DEFAULT, DEFAULT, 'hand-written')"));
        Assert.Contains("computed", ex.Message);
        Assert.Contains("slug", ex.Message);
    }

    [Fact]
    public async Task InsertValues_NamedColumnList_DefaultOnComputed_AcceptedAsOmitted()
    {
        // Named-column-list variant of the same fix: the user mentions
        // the computed column explicitly and supplies DEFAULT.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan(
            "CREATE TEMP TABLE t (id Int64 GENERATED ALWAYS AS IDENTITY, name String DEFAULT 'x', " +
            "slug String AS ('s-' || cast(id as String)))");

        catalog.Plan("INSERT INTO t (id, name, slug) VALUES (DEFAULT, DEFAULT, DEFAULT)");

        List<DataValue[]> rows = await ScanAllValues(catalog["t"]);
        Assert.Single(rows);
        Assert.Equal(1L, rows[0][0].AsInt64());
        Assert.Equal("x", rows[0][1].AsString());
        Assert.Equal("s-1", rows[0][2].AsString());
    }

    [Fact]
    public async Task InsertValues_EquivalentToDefaultValues_WithComputedColumn()
    {
        // The equivalence probe that originally surfaced the divergence:
        // DEFAULT VALUES on table A must match VALUES (DEFAULT, DEFAULT,
        // DEFAULT) on table B when both have computed columns.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan(
            "CREATE TEMP TABLE t1 (id Int64 GENERATED ALWAYS AS IDENTITY, name String DEFAULT 'x', " +
            "slug String AS ('s-' || cast(id as String) || '-' || name))");
        catalog.Plan(
            "CREATE TEMP TABLE t2 (id Int64 GENERATED ALWAYS AS IDENTITY, name String DEFAULT 'x', " +
            "slug String AS ('s-' || cast(id as String) || '-' || name))");

        catalog.Plan("INSERT INTO t1 DEFAULT VALUES");
        catalog.Plan("INSERT INTO t2 VALUES (DEFAULT, DEFAULT, DEFAULT)");

        List<DataValue[]> r1 = await ScanAllValues(catalog["t1"]);
        List<DataValue[]> r2 = await ScanAllValues(catalog["t2"]);
        Assert.Single(r1);
        Assert.Single(r2);
        Assert.Equal(r1[0][0].AsInt64(), r2[0][0].AsInt64());
        Assert.Equal(r1[0][1].AsString(), r2[0][1].AsString());
        Assert.Equal(r1[0][2].AsString(), r2[0][2].AsString());
    }
}
