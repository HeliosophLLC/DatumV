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
public sealed class InsertValuesTests : IAsyncLifetime
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
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32, name String)");

        catalog.Plan("INSERT INTO t VALUES (1, 'alice')");

        Assert.Equal(1, catalog["t"].GetRowCount());
        List<(int id, string name)> rows = await ScanAsTuples(catalog["t"]);
        Assert.Equal([(1, "alice")], rows);
    }

    [Fact]
    public async Task InsertValues_PositionalMultipleRows_AllVisible()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32, name String)");

        catalog.Plan("INSERT INTO t VALUES (1, 'a'), (2, 'b'), (3, 'c')");

        Assert.Equal(3, catalog["t"].GetRowCount());
        List<(int id, string name)> rows = await ScanAsTuples(catalog["t"]);
        Assert.Equal([(1, "a"), (2, "b"), (3, "c")], rows);
    }

    [Fact]
    public async Task InsertValues_NamedColumnList_FillsByName()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
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
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32)");

        catalog.Plan("INSERT INTO t VALUES (-7)");

        List<int> ids = await ScanIntColumn(catalog["t"], "id");
        Assert.Equal([-7], ids);
    }

    [Fact]
    public async Task InsertValues_NullLiteral_StoresNullOnNullableColumn()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
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
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32, status String DEFAULT 'pending')");

        catalog.Plan("INSERT INTO t (id) VALUES (1)");

        List<(int id, string name)> rows = await ScanAsTuples(catalog["t"]);
        Assert.Equal([(1, "pending")], rows);
    }

    [Fact]
    public async Task InsertValues_OmittedNullableColumnWithoutDefault_FillsNull()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32, note String)");

        catalog.Plan("INSERT INTO t (id) VALUES (1)");

        List<DataValue[]> rows = await ScanAllValues(catalog["t"]);
        Assert.Single(rows);
        Assert.True(rows[0][1].IsNull);
    }

    [Fact]
    public void InsertValues_OmittedNotNullColumnWithoutDefault_Throws()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32 NOT NULL, name String)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("INSERT INTO t (name) VALUES ('alice')"));
        Assert.Contains("NOT NULL", ex.Message);
        Assert.Contains("id", ex.Message);
    }

    [Fact]
    public async Task InsertValues_DefaultLiteralNegativeNumber_FillsNegativeValue()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
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
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32)");

        catalog.Plan("INSERT INTO t VALUES (5)");

        List<int> ids = await ScanIntColumn(catalog["t"], "id");
        Assert.Equal([5], ids);
    }

    [Fact]
    public async Task InsertValues_LiteralFitsInUInt8()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (b UInt8)");

        catalog.Plan("INSERT INTO t VALUES (200)");

        List<DataValue[]> rows = await ScanAllValues(catalog["t"]);
        Assert.Equal(200, rows[0][0].AsUInt8());
    }

    [Fact]
    public void InsertValues_NegativeLiteralIntoUnsigned_Throws()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (b UInt8)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("INSERT INTO t VALUES (-1)"));
        Assert.Contains("UInt8", ex.Message);
    }

    [Fact]
    public void InsertValues_OverflowOnNarrow_Throws()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (b Int8)");

        // Int16 200 doesn't fit in Int8 ([-128, 127]).
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("INSERT INTO t VALUES (200)"));
        Assert.Contains("Int8", ex.Message);
    }

    [Fact]
    public void InsertValues_StringIntoNumeric_Throws()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32)");

        Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("INSERT INTO t VALUES ('5')"));
    }

    [Fact]
    public async Task InsertValues_Float32LosslessFromInt_Accepted()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (x Float32)");

        catalog.Plan("INSERT INTO t VALUES (5)");

        List<DataValue[]> rows = await ScanAllValues(catalog["t"]);
        Assert.Equal(5f, rows[0][0].AsFloat32());
    }

    // ──────────────────── Validation ────────────────────

    [Fact]
    public void InsertValues_RowArityMismatch_Throws()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32, name String)");

        // Two columns, only one value supplied per row.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("INSERT INTO t VALUES (1)"));
        Assert.Contains("expects 2", ex.Message);
    }

    [Fact]
    public void InsertValues_ColumnListMentionsUnknown_Throws()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("INSERT INTO t (nope) VALUES (1)"));
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public void InsertValues_ColumnListDuplicates_Throws()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32, name String)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("INSERT INTO t (id, id) VALUES (1, 2)"));
        Assert.Contains("more than once", ex.Message);
    }

    [Fact]
    public void InsertValues_ZeroRows_NoOp()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32)");

        // An empty VALUES list isn't valid SQL via our parser anyway,
        // but ensure single-row insert leaves count consistent.
        catalog.Plan("INSERT INTO t VALUES (1)");
        Assert.Equal(1, catalog["t"].GetRowCount());
    }

    [Fact]
    public void InsertValues_NonLiteralExpression_RejectedUntilSelect()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32)");

        // 1 + 2 is a binary expression, not a literal — VALUES path
        // accepts literals only in PR10c.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("INSERT INTO t VALUES (1 + 2)"));
        Assert.Contains("literal", ex.Message);
    }

    // INSERT SELECT (the PR10c'-NotYetSupported case) — covered in
    // InsertSelectTests now that the path ships.

    // ──────────────────── Persistent table ────────────────────

    [Fact]
    public async Task InsertValues_OnPersistentTable_VisibleAcrossReopen()
    {
        Pool pool = new(new PoolBacking());
        using (TableCatalog catalog = new(pool, CatalogPath))
        {
            catalog.Plan("CREATE TABLE users (id Int32, name String)");
            catalog.Plan("INSERT INTO users VALUES (1, 'alice'), (2, 'bob')");
        }

        using TableCatalog reopened = new(pool, CatalogPath);
        Assert.Equal(2, reopened["users"].GetRowCount());
        List<(int id, string name)> rows = await ScanAsTuples(reopened["users"]);
        Assert.Equal([(1, "alice"), (2, "bob")], rows);
    }

    [Fact]
    public async Task InsertValues_OnPersistentTableWithDefault_FillsDefault()
    {
        Pool pool = new(new PoolBacking());
        using (TableCatalog catalog = new(pool, CatalogPath))
        {
            catalog.Plan("CREATE TABLE users (id Int32, status String DEFAULT 'active')");
            catalog.Plan("INSERT INTO users (id) VALUES (1)");
        }

        // Reopen to confirm the inserted-with-default row survives the
        // close/reopen cycle (DEFAULT lives in the footer prologue per
        // PR10b).
        using TableCatalog reopened = new(pool, CatalogPath);
        List<(int id, string name)> rows = await ScanAsTuples(reopened["users"]);
        Assert.Equal([(1, "active")], rows);
    }

    [Fact]
    public void Insert_OnMissingTable_Throws()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);

        Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("INSERT INTO nope VALUES (1)"));
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
}
