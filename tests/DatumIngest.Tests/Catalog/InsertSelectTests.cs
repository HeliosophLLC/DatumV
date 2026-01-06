using DatumIngest.Catalog;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// PR10c' tests for SQL <c>INSERT INTO … SELECT …</c>. Cover:
/// tableless SELECT projecting literals, SELECT from another table
/// (positional + named column list), default-fill for omitted
/// columns, cross-kind coercion through the shared
/// <see cref="LiteralCoercion"/> path, projection arity mismatches,
/// streaming through an <see cref="IAppendSession"/>, abort-on-error
/// semantics, and persistent-table round-trip.
/// </summary>
public sealed class InsertSelectTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_pr10c_select_{Guid.NewGuid():N}");
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

    // ──────────────────── Tableless SELECT (literals) ────────────────────

    [Fact]
    public async Task InsertSelect_TablelessLiterals_VisibleViaScan()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32, name String)");

        // No source table — SELECT projects literals only.
        catalog.Plan("INSERT INTO t SELECT 1, 'alice'");

        Assert.Equal(1, catalog["t"].GetRowCount());
        List<(int id, string name)> rows = await ScanAsTuples(catalog["t"]);
        Assert.Equal([(1, "alice")], rows);
    }

    [Fact]
    public async Task InsertSelect_TablelessExpressions_AreEvaluatedThenStored()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE t (sum Int32)");

        // 1 + 2 isn't a literal — so this is rejected by VALUES but
        // accepted by SELECT (full evaluator runs the +).
        catalog.Plan("INSERT INTO t SELECT 1 + 2");

        List<int> ids = await ScanIntColumn(catalog["t"], "sum");
        Assert.Equal([3], ids);
    }

    // ──────────────────── SELECT from a populated table ────────────────────

    [Fact]
    public async Task InsertSelect_FromTable_PositionalCopy()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE src (id Int32, name String)");
        catalog.Plan("CREATE TEMP TABLE dst (id Int32, name String)");
        catalog.Plan("INSERT INTO src VALUES (1, 'alice'), (2, 'bob')");

        catalog.Plan("INSERT INTO dst SELECT id, name FROM src");

        Assert.Equal(2, catalog["dst"].GetRowCount());
        List<(int id, string name)> rows = await ScanAsTuples(catalog["dst"]);
        Assert.Equal([(1, "alice"), (2, "bob")], rows);
    }

    [Fact]
    public async Task InsertSelect_NamedColumnList_ReordersTargetColumns()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE src (a Int32, b String)");
        catalog.Plan("CREATE TEMP TABLE dst (id Int32, name String)");
        catalog.Plan("INSERT INTO src VALUES (1, 'alice')");

        // Named list maps SELECT projection-position → target column.
        // SELECT projects (b, a); column list is (name, id) — so the
        // string lands in `name` and the int in `id`.
        catalog.Plan("INSERT INTO dst (name, id) SELECT b, a FROM src");

        List<(int id, string name)> rows = await ScanAsTuples(catalog["dst"]);
        Assert.Equal([(1, "alice")], rows);
    }

    [Fact]
    public async Task InsertSelect_PartialColumnList_FillsOmittedWithDefault()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE src (id Int32)");
        catalog.Plan("CREATE TEMP TABLE dst (id Int32, status String DEFAULT 'imported')");
        catalog.Plan("INSERT INTO src VALUES (1), (2)");

        catalog.Plan("INSERT INTO dst (id) SELECT id FROM src");

        List<(int id, string name)> rows = await ScanAsTuples(catalog["dst"]);
        Assert.Equal([(1, "imported"), (2, "imported")], rows);
    }

    [Fact]
    public async Task InsertSelect_PartialColumnList_FillsOmittedNullableWithNull()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE src (id Int32)");
        catalog.Plan("CREATE TEMP TABLE dst (id Int32, note String)");
        catalog.Plan("INSERT INTO src VALUES (1)");

        catalog.Plan("INSERT INTO dst (id) SELECT id FROM src");

        List<DataValue[]> rows = await ScanAllValues(catalog["dst"]);
        Assert.Single(rows);
        Assert.True(rows[0][1].IsNull);
    }

    [Fact]
    public void InsertSelect_OmittedNotNullWithoutDefault_Throws()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE src (id Int32)");
        catalog.Plan("CREATE TEMP TABLE dst (id Int32 NOT NULL, name String NOT NULL)");
        catalog.Plan("INSERT INTO src VALUES (1)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("INSERT INTO dst (id) SELECT id FROM src"));
        Assert.Contains("name", ex.Message);
        Assert.Contains("NOT NULL", ex.Message);
    }

    // ──────────────────── Cross-kind coercion ────────────────────

    [Fact]
    public async Task InsertSelect_WidensInt8ProjectionToInt32Column()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE src (n Int8)");
        catalog.Plan("CREATE TEMP TABLE dst (n Int32)");
        catalog.Plan("INSERT INTO src VALUES (5), (-7)");

        catalog.Plan("INSERT INTO dst SELECT n FROM src");

        List<int> values = await ScanIntColumn(catalog["dst"], "n");
        Assert.Equal([5, -7], values);
    }

    [Fact]
    public void InsertSelect_OverflowOnNarrow_AbortsBeforeCommit()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE src (n Int16)");
        catalog.Plan("CREATE TEMP TABLE dst (n Int8)");

        // 200 fits in Int16 but overflows Int8.
        catalog.Plan("INSERT INTO src VALUES (5), (200)");

        Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("INSERT INTO dst SELECT n FROM src"));

        // Abort semantics: dst is untouched (no rows committed).
        Assert.Equal(0, catalog["dst"].GetRowCount());
    }

    // ──────────────────── Validation ────────────────────

    [Fact]
    public void InsertSelect_ProjectionArityMismatch_Throws()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE src (a Int32, b String)");
        catalog.Plan("CREATE TEMP TABLE dst (id Int32)");
        catalog.Plan("INSERT INTO src VALUES (1, 'x')");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("INSERT INTO dst SELECT a, b FROM src"));
        // Source produces 2 cols, dst has 1 — error mentions arities.
        Assert.Contains("1", ex.Message);
        Assert.Contains("2", ex.Message);
    }

    [Fact]
    public void InsertSelect_ColumnListArityMismatch_Throws()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE src (a Int32)");
        catalog.Plan("CREATE TEMP TABLE dst (id Int32, name String)");
        catalog.Plan("INSERT INTO src VALUES (1)");

        // SELECT projects 1 column; column list says 2.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("INSERT INTO dst (id, name) SELECT a FROM src"));
        Assert.Contains("must match", ex.Message);
    }

    [Fact]
    public async Task InsertSelect_FromEmptySource_NoOp()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE src (a Int32)");
        catalog.Plan("CREATE TEMP TABLE dst (id Int32)");
        // src has no rows.

        catalog.Plan("INSERT INTO dst SELECT a FROM src");

        Assert.Equal(0, catalog["dst"].GetRowCount());
        Assert.Empty(await ScanIntColumn(catalog["dst"], "id"));
    }

    [Fact]
    public async Task InsertSelect_WithFilter_RespectsWhereClause()
    {
        Pool pool = new(new PoolBacking());
        using TableCatalog catalog = new(pool);
        catalog.Plan("CREATE TEMP TABLE src (id Int32, name String)");
        catalog.Plan("CREATE TEMP TABLE dst (id Int32, name String)");
        catalog.Plan("INSERT INTO src VALUES (1, 'a'), (2, 'b'), (3, 'c')");

        catalog.Plan("INSERT INTO dst SELECT id, name FROM src WHERE id >= 2");

        Assert.Equal(2, catalog["dst"].GetRowCount());
        List<(int id, string name)> rows = await ScanAsTuples(catalog["dst"]);
        Assert.Equal([(2, "b"), (3, "c")], rows);
    }

    // ──────────────────── Persistent target ────────────────────

    [Fact]
    public async Task InsertSelect_OnPersistentTable_VisibleAcrossReopen()
    {
        Pool pool = new(new PoolBacking());
        using (TableCatalog catalog = new(pool, CatalogPath))
        {
            catalog.Plan("CREATE TABLE users (id Int32, name String)");
            catalog.Plan("INSERT INTO users VALUES (1, 'alice')");
            // Persistent → persistent via an in-flight SELECT projection
            // that crosses the streaming-append session boundary.
            catalog.Plan("INSERT INTO users SELECT id + 1, 'bob' FROM users");
        }

        using TableCatalog reopened = new(pool, CatalogPath);
        Assert.Equal(2, reopened["users"].GetRowCount());
        List<(int id, string name)> rows = await ScanAsTuples(reopened["users"]);
        Assert.Equal([(1, "alice"), (2, "bob")], rows);
    }

    // ──────────────────── Helpers ────────────────────

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
