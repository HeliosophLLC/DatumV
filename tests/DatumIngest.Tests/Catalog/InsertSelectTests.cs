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
public sealed class InsertSelectTests : ServiceTestBase, IAsyncLifetime
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
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
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
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
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
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
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
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
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
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
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
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
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
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
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
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
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
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
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
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
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
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
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
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
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
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE src (id Int32, name String)");
        catalog.Plan("CREATE TEMP TABLE dst (id Int32, name String)");
        catalog.Plan("INSERT INTO src VALUES (1, 'a'), (2, 'b'), (3, 'c')");

        catalog.Plan("INSERT INTO dst SELECT id, name FROM src WHERE id >= 2");

        Assert.Equal(2, catalog["dst"].GetRowCount());
        List<(int id, string name)> rows = await ScanAsTuples(catalog["dst"]);
        Assert.Equal([(2, "b"), (3, "c")], rows);
    }

    // ──────────────────── Blob columns (Image / Audio / Video / Json) ────────────────────

    [Fact]
    public async Task InsertSelect_ImageColumn_RoundTripsBytes()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE src (id Int32, img Image)");
        catalog.Plan("CREATE TEMP TABLE dst (id Int32, img Image)");

        // Populate src directly via the append session — there is no SQL
        // function today that produces an Image literal from inline bytes,
        // so the test bootstraps via the provider API.
        byte[] imageBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01, 0x02, 0x03];
        ITableProvider srcProvider = catalog["src"];
        await using (IAppendSession s = srcProvider.BeginAppend())
        {
            Arena srcArena = new();
            RowBatch srcBatch = pool.RentRowBatch(new ColumnLookup(["id", "img"]), capacity: 1, arena: srcArena);
            DataValue[] row = pool.RentDataValues(2);
            row[0] = DataValue.FromInt32(1);
            row[1] = DataValue.FromImage(imageBytes, srcArena);
            srcBatch.Add(row);
            await s.WriteAsync(srcBatch);
            await s.CommitAsync();
        }

        // INSERT SELECT — this is the use case: dst now holds a copy of the image.
        catalog.Plan("INSERT INTO dst SELECT id, img FROM src");

        Assert.Equal(1, catalog["dst"].GetRowCount());
        await foreach (RowBatch batch in catalog["dst"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            Assert.Equal(1, batch.Count);
            Row row = batch[0];
            Assert.Equal(1, row[0].AsInt32());
            Assert.Equal(DataKind.Image, row[1].Kind);
            ReadOnlySpan<byte> readBack = row[1].AsByteSpan(batch.Arena);
            Assert.True(readBack.SequenceEqual(imageBytes));
            batch.Dispose();
        }
    }

    [Fact]
    public async Task InsertValues_JsonExpression_StoresCanonicalCbor()
    {
        // Exercises the INSERT VALUES blob branch: json_parse('{"x":1}')
        // produces a DataKind.Json value at evaluation time which then
        // routes through ConvertValueRefToTarget's blob arm into the
        // target arena. Image/Audio/Video share the same arm — JSON is
        // the only one with a literal-producing scalar function in the
        // standard library.
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE j (id Int32, data Json)");

        catalog.Plan("INSERT INTO j VALUES (1, json_parse('{\"x\":1}'))");

        Assert.Equal(1, catalog["j"].GetRowCount());
        await foreach (RowBatch batch in catalog["j"].ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            Row row = batch[0];
            Assert.Equal(DataKind.Json, row[1].Kind);
            // Stored bytes are canonical CBOR; just confirm the value
            // is non-empty and well-formed enough to read.
            Assert.False(row[1].IsNull);
            Assert.True(row[1].AsByteSpan(batch.Arena).Length > 0);
            batch.Dispose();
        }
    }

    [Fact]
    public async Task InsertSelect_BlobIntoMismatchedKind_Throws()
    {
        // Image source → String target should error clearly; no implicit
        // blob coercion.
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE src (id Int32, img Image)");
        catalog.Plan("CREATE TEMP TABLE dst (id Int32, blob String)");

        ITableProvider srcProvider = catalog["src"];
        await using (IAppendSession s = srcProvider.BeginAppend())
        {
            Arena srcArena = new();
            RowBatch srcBatch = pool.RentRowBatch(new ColumnLookup(["id", "img"]), capacity: 1, arena: srcArena);
            DataValue[] row = pool.RentDataValues(2);
            row[0] = DataValue.FromInt32(1);
            row[1] = DataValue.FromImage([0x01, 0x02, 0x03], srcArena);
            srcBatch.Add(row);
            await s.WriteAsync(srcBatch);
            await s.CommitAsync();
        }

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("INSERT INTO dst SELECT id, img FROM src"));
        Assert.Contains("Image", ex.Message);
        Assert.Contains("do not coerce", ex.Message);
    }

    // ──────────────────── Persistent target ────────────────────

    [Fact]
    public async Task InsertSelect_OnPersistentTable_VisibleAcrossReopen()
    {
        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE users (id Int32, name String)");
            catalog.Plan("INSERT INTO users VALUES (1, 'alice')");
            // Persistent → persistent via an in-flight SELECT projection
            // that crosses the streaming-append session boundary.
            catalog.Plan("INSERT INTO users SELECT id + 1, 'bob' FROM users");
        }

        using TableCatalog reopened = CreateCatalog(CatalogPath);
        Assert.Equal(2, reopened["users"].GetRowCount());
        List<(int id, string name)> rows = await ScanAsTuples(reopened["users"]);
        Assert.Equal([(1, "alice"), (2, "bob")], rows);
    }

    [Fact]
    public async Task InsertSelect_FromSelf_WithSidecarBackedString_Roundtrips()
    {
        // Regression: long String column on a persistent table round-trips
        // through the .datum sidecar on read, so the scan yields
        // IsInSidecar=true DataValues. The INSERT…SELECT path must resolve
        // them through the SidecarRegistry rather than calling AsString(store)
        // (which throws with a "Use the AsString(store, registry) overload"
        // message).
        string longText = new('x', 200);

        using (TableCatalog catalog = CreateCatalog(CatalogPath))
        {
            catalog.Plan("CREATE TABLE test (id Int32 GENERATED ALWAYS AS IDENTITY, content String)");
            catalog.Plan($"INSERT INTO test (content) VALUES ('{longText}')");

            // Self-INSERT…SELECT: source rows scan back from the persistent
            // .datum file as sidecar-backed Strings.
            catalog.Plan("INSERT INTO test (content) SELECT content FROM test");

            Assert.Equal(2, catalog["test"].GetRowCount());

            // Scan with the registry-aware AsString overload — the no-store
            // helper can't resolve sidecar-backed values.
            List<string> readBack = new();
            await foreach (RowBatch batch in catalog["test"].ScanAsync(
                requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
            {
                for (int r = 0; r < batch.Count; r++)
                {
                    Row row = batch[r];
                    readBack.Add(row[1].AsString(batch.Arena, catalog.SidecarRegistry));
                }
                batch.Dispose();
            }
            Assert.Equal([longText, longText], readBack);
        }
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
