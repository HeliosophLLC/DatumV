using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Catalog.Providers;
using Heliosoph.DatumV.DatumFile.V2;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Tests.Catalog;

/// <summary>
/// Tests for SQL <c>CREATE TABLE … AS SELECT</c> (CTAS): TEMP vs
/// persistent target dispatch, schema inferred from the source
/// projection, <c>IF NOT EXISTS</c> short-circuit, duplicate-column
/// rejection, and drop-on-failure cleanup.
/// </summary>
public sealed class CreateTableAsSelectTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_ctas_{Guid.NewGuid():N}");
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

    [Fact]
    public async Task Ctas_FromRange_CreatesTableAndStreamsRows()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE test AS SELECT Value FROM GENERATE_SERIES(1, 10)");

        Assert.True(catalog.HasTable("test"));
        ITableProvider provider = catalog["test"];
        Schema schema = provider.GetSchema();
        Assert.Equal(["Value"], schema.Columns.Select(c => c.Name));
        Assert.Equal(DataKind.Int32, schema.Columns[0].Kind);

        // GENERATE_SERIES(1, 10) emits inclusive endpoints — 10 rows.
        List<int> values = await ScanIntColumn(provider, "Value");
        Assert.Equal(Enumerable.Range(1, 10).ToList(), values);
    }

    [Fact]
    public async Task Ctas_FromSourceTable_CopiesAllRows()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE src (id Int32, name String)");
        catalog.Plan("INSERT INTO src VALUES (1, 'alice'), (2, 'bob'), (3, 'carol')");

        catalog.Plan("CREATE TEMP TABLE dst AS SELECT id, name FROM src");

        Assert.True(catalog.HasTable("dst"));
        ITableProvider provider = catalog["dst"];
        Assert.Equal(3, provider.GetRowCount());

        Schema schema = provider.GetSchema();
        Assert.Equal(["id", "name"], schema.Columns.Select(c => c.Name));
        Assert.Equal(DataKind.Int32, schema.Columns[0].Kind);
        Assert.Equal(DataKind.String, schema.Columns[1].Kind);
    }

    [Fact]
    public void Ctas_TempTarget_RegistersInMemoryProvider()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);

        catalog.Plan("CREATE TEMP TABLE t AS SELECT Value FROM GENERATE_SERIES(1, 3)");

        ITableProvider provider = catalog["t"];
        Assert.IsType<InMemoryTableProvider>(provider);
        Assert.Equal(3, provider.GetRowCount());
    }

    [Fact]
    public void Ctas_PersistentTarget_MaterialisesDatumFile()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE numbers AS SELECT Value FROM GENERATE_SERIES(1, 5)");

        string expectedPath = Path.Combine(_tempDir, "data", "public", "numbers.datum");
        Assert.True(File.Exists(expectedPath));

        ITableProvider provider = catalog["numbers"];
        Assert.IsType<DatumFileTableProviderV2>(provider);
        Assert.Equal(5, provider.GetRowCount());
    }

    [Fact]
    public async Task Ctas_TablelessProjection_CreatesTableWithLiteralColumns()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);

        catalog.Plan("CREATE TEMP TABLE t AS SELECT 1 AS a, 'hello' AS b");

        Schema schema = catalog["t"].GetSchema();
        Assert.Equal(["a", "b"], schema.Columns.Select(c => c.Name));
        Assert.Equal(DataKind.Int8, schema.Columns[0].Kind);
        Assert.Equal(DataKind.String, schema.Columns[1].Kind);
        Assert.Equal(1, catalog["t"].GetRowCount());
    }

    [Fact]
    public void Ctas_TablelessSingleLiteral_CreatesSingleColumnTable()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);

        catalog.Plan("CREATE TEMP TABLE t AS SELECT 42 AS answer");

        Schema schema = catalog["t"].GetSchema();
        Assert.Single(schema.Columns);
        Assert.Equal("answer", schema.Columns[0].Name);
        Assert.Equal(DataKind.Int8, schema.Columns[0].Kind);
        Assert.Equal(1, catalog["t"].GetRowCount());
    }

    [Fact]
    public void Ctas_SchemaQualifiedTarget_RoutesToNamedSchema()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);
        catalog.Plan("CREATE SCHEMA reports");

        catalog.Plan("CREATE TABLE reports.numbers AS SELECT Value FROM GENERATE_SERIES(1, 5)");

        Assert.True(catalog.HasTable("reports.numbers"));
        // Unqualified `numbers` does not resolve — search_path doesn't
        // include `reports` by default.
        Assert.False(catalog.HasTable("numbers"));
        Assert.Equal("reports", catalog["reports.numbers"].QualifiedName.Schema);
        Assert.Equal(5, catalog["reports.numbers"].GetRowCount());
    }

    [Fact]
    public void Ctas_TempWithExplicitSchema_Rejected()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE TEMP TABLE myapp.t AS SELECT Value FROM GENERATE_SERIES(1, 3)"));
        Assert.Contains("TEMP", ex.Message);
        Assert.Contains("schema", ex.Message);
    }

    [Fact]
    public void Ctas_TableAlreadyExists_Throws()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32)");

        Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE TEMP TABLE t AS SELECT Value FROM GENERATE_SERIES(1, 3)"));
    }

    [Fact]
    public void Ctas_IfNotExists_ShortCircuits_AndDoesNotEvaluateQuery()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE t (id Int32)");
        catalog.Plan("INSERT INTO t VALUES (42)");

        // The source query references a non-existent table — if the
        // IF NOT EXISTS short-circuit fires before query planning (PG
        // behaviour) this is a no-op. If we instead evaluated and
        // discarded, planning the SELECT would throw.
        catalog.Plan("CREATE TEMP TABLE IF NOT EXISTS t AS SELECT id FROM nope");

        // Original table is intact: same provider, same row.
        Assert.Equal(1, catalog["t"].GetRowCount());
        Assert.Equal(DataKind.Int32, catalog["t"].GetSchema().Columns[0].Kind);
    }

    [Fact]
    public void Ctas_DuplicateAutoNames_AreDeduplicated()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE src (id Int32)");
        catalog.Plan("INSERT INTO src VALUES (1)");

        // Two unaliased references to the same column produce two
        // columns with the same raw name. The shared projection
        // resolver de-duplicates auto-generated names with a numeric
        // suffix — same behaviour SELECT itself surfaces in the shell,
        // so CTAS materialises the same shape.
        catalog.Plan("CREATE TEMP TABLE dst AS SELECT id, id FROM src");

        Schema schema = catalog["dst"].GetSchema();
        Assert.Equal(["id_1", "id_2"], schema.Columns.Select(c => c.Name));
    }

    [Fact]
    public void Ctas_DuplicateAliases_Rejected()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE src (id Int32, name String)");
        catalog.Plan("INSERT INTO src VALUES (1, 'x')");

        // Explicit duplicate aliases bypass auto-dedup (the resolver
        // doesn't rename user-supplied names) so they reach the CTAS
        // executor's schema build and trip the duplicate-name guard.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE TEMP TABLE t AS SELECT id AS dup, name AS dup FROM src"));
        Assert.Contains("specified more than once", ex.Message);
    }

    // ───────────────────── Compound source queries ─────────────────────

    [Fact]
    public async Task Ctas_FromUnionAll_StreamsBothBranches()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE a (id Int32, name String)");
        catalog.Plan("CREATE TEMP TABLE b (id Int32, name String)");
        catalog.Plan("INSERT INTO a VALUES (1, 'alice')");
        catalog.Plan("INSERT INTO b VALUES (2, 'bob')");

        catalog.Plan(
            "CREATE TEMP TABLE combined AS " +
            "SELECT id, name FROM a UNION ALL SELECT id, name FROM b");

        Schema schema = catalog["combined"].GetSchema();
        Assert.Equal(["id", "name"], schema.Columns.Select(c => c.Name));
        Assert.Equal(DataKind.Int32, schema.Columns[0].Kind);
        Assert.Equal(DataKind.String, schema.Columns[1].Kind);
        Assert.Equal(2, catalog["combined"].GetRowCount());
    }

    [Fact]
    public void Ctas_FromUnion_WidensColumnKindAcrossBranches()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE narrow (id Int16)");
        catalog.Plan("CREATE TEMP TABLE wide   (id Int64)");
        catalog.Plan("INSERT INTO narrow VALUES (1)");
        catalog.Plan("INSERT INTO wide   VALUES (2)");

        catalog.Plan(
            "CREATE TEMP TABLE combined AS " +
            "SELECT id FROM narrow UNION ALL SELECT id FROM wide");

        // Int16 and Int64 unify to Int64 per the type-coercion chain.
        Schema schema = catalog["combined"].GetSchema();
        Assert.Equal(DataKind.Int64, schema.Columns[0].Kind);
    }

    [Fact]
    public void Ctas_FromUnion_ColumnNamesComeFromLeftBranch()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE left_src  (a Int32)");
        catalog.Plan("CREATE TEMP TABLE right_src (b Int32)");

        catalog.Plan(
            "CREATE TEMP TABLE combined AS " +
            "SELECT a FROM left_src UNION ALL SELECT b FROM right_src");

        // PG semantics: the combined column is named after the leftmost
        // branch's column (`a`), not the right's (`b`).
        Schema schema = catalog["combined"].GetSchema();
        Assert.Equal(["a"], schema.Columns.Select(c => c.Name));
    }

    [Fact]
    public void Ctas_FromUnion_ArityMismatch_Rejected()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE one (a Int32)");
        catalog.Plan("CREATE TEMP TABLE two (a Int32, b Int32)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE TEMP TABLE t AS SELECT a FROM one UNION ALL SELECT a, b FROM two"));
        Assert.Contains("same number of columns", ex.Message);
    }

    [Fact]
    public void Ctas_FromUnion_ArrayPlusScalarColumn_Rejected()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE arr (xs Float32[])");
        catalog.Plan("CREATE TEMP TABLE sca (xs Float32)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE TEMP TABLE t AS SELECT xs FROM arr UNION ALL SELECT xs FROM sca"));
        Assert.Contains("cannot be matched", ex.Message);
    }

    [Fact]
    public void Ctas_FromUnion_IncompatibleKinds_Rejected()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE strs (x String)");
        catalog.Plan("CREATE TEMP TABLE ints (x Int32)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE TEMP TABLE t AS SELECT x FROM strs UNION ALL SELECT x FROM ints"));
        Assert.Contains("cannot be matched", ex.Message);
    }

    [Fact]
    public async Task Ctas_FromIntersect_StreamsCommonRows()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);
        catalog.Plan("CREATE TEMP TABLE a (id Int32)");
        catalog.Plan("CREATE TEMP TABLE b (id Int32)");
        catalog.Plan("INSERT INTO a VALUES (1), (2), (3)");
        catalog.Plan("INSERT INTO b VALUES (2), (3), (4)");

        catalog.Plan(
            "CREATE TEMP TABLE shared AS " +
            "SELECT id FROM a INTERSECT SELECT id FROM b");

        List<int> values = await ScanIntColumn(catalog["shared"], "id");
        values.Sort();
        Assert.Equal([2, 3], values);
    }

    [Fact]
    public async Task Ctas_PersistentSource_SidecarStrings_CopiesBytesIntoTargetSidecar()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        // Register another sidecar-backed table FIRST so registry slot 0
        // belongs to a table other than the CTAS source. Writing the source
        // table's sidecar offsets verbatim into the target then resolves
        // against the wrong blob file instead of silently landing on the
        // right bytes by registration-order luck.
        catalog.Plan("CREATE TABLE decoy (junk String)");
        catalog.Plan($"INSERT INTO decoy VALUES ('{new string('x', 500)}')");

        // Strings longer than the 16-byte on-disk slot are sidecar-resident.
        string first = "alpha " + new string('a', 60);
        string second = "bravo " + new string('b', 90);
        string third = "charlie " + new string('c', 120);
        catalog.Plan("CREATE TABLE src (text String)");
        catalog.Plan($"INSERT INTO src VALUES ('{first}'), ('{second}'), ('{third}')");

        catalog.Plan("CREATE TABLE dst AS SELECT text FROM src");

        List<string> values = await CollectStrings(catalog, "SELECT text FROM dst");
        Assert.Equal([first, second, third], values);

        // The target must be self-contained: its payload bytes live in its
        // own sidecar, not referenced out of the source table's blob file.
        catalog.Plan("DROP TABLE src");
        List<string> afterDrop = await CollectStrings(catalog, "SELECT text FROM dst");
        Assert.Equal([first, second, third], afterDrop);
    }

    [Fact]
    public async Task InsertSelect_PersistentSource_SidecarStrings_CopiesBytesIntoTargetSidecar()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE decoy (junk String)");
        catalog.Plan($"INSERT INTO decoy VALUES ('{new string('x', 500)}')");

        string payload = "delta " + new string('d', 80);
        catalog.Plan("CREATE TABLE src (text String)");
        catalog.Plan($"INSERT INTO src VALUES ('{payload}')");

        catalog.Plan("CREATE TABLE dst (text String)");
        catalog.Plan("INSERT INTO dst SELECT text FROM src");

        catalog.Plan("DROP TABLE src");
        List<string> values = await CollectStrings(catalog, "SELECT text FROM dst");
        Assert.Equal([payload], values);
    }

    [Fact]
    public async Task Ctas_TempTarget_FromPersistentSidecarStrings_MaterialisesValues()
    {
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        // Strings longer than the 16-byte on-disk slot are sidecar-resident,
        // so the TEMP append session receives sidecar-backed DataValues that
        // need the catalog's SidecarRegistry to materialise.
        string first = "alpha " + new string('a', 60);
        string second = "bravo " + new string('b', 90);
        catalog.Plan("CREATE TABLE src (text String)");
        catalog.Plan($"INSERT INTO src VALUES ('{first}'), ('{second}')");

        catalog.Plan("CREATE TEMP TABLE tmp AS SELECT text FROM src");

        List<string> values = await CollectStrings(catalog, "SELECT text FROM tmp");
        Assert.Equal([first, second], values);

        // TEMP cells are materialised managed values — dropping the source
        // table must not affect them.
        catalog.Plan("DROP TABLE src");
        List<string> afterDrop = await CollectStrings(catalog, "SELECT text FROM tmp");
        Assert.Equal([first, second], afterDrop);
    }

    [Fact]
    public async Task Ctas_TempTarget_FromNonByteArrayColumn_MaterialisesArrays()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(pool);

        // A Float32[] column exercises the forward DataValue→CLR-cell path
        // (ConvertDataValueToCell) for a non-UInt8 array element kind — the
        // asymmetry where only UInt8 arrays were extracted and every other
        // element kind fell through to its scalar accessor. A 6-element array
        // is wider than the 16-byte inline budget, so the TEMP append session
        // receives arena-backed array DataValues that must be materialised into
        // stable CLR arrays decoupled from the source scan arena.
        catalog.Plan("CREATE TEMP TABLE src (id Int32, xs Float32[])");
        catalog.Plan(
            "INSERT INTO src VALUES " +
            "(1, [1.5, 2.5, 3.5, 4.5, 5.5, 6.5]), " +
            "(2, [10.0, 20.0, 30.0, 40.0, 50.0, 60.0])");

        catalog.Plan("CREATE TEMP TABLE dst AS SELECT id, xs FROM src");

        Schema schema = catalog["dst"].GetSchema();
        Assert.Equal(DataKind.Float32, schema.Columns[1].Kind);
        Assert.True(schema.Columns[1].IsArray);

        List<float[]> arrays = await ScanFloatArrayColumn(catalog, "dst", "xs");
        Assert.Equal(2, arrays.Count);
        Assert.Equal([1.5f, 2.5f, 3.5f, 4.5f, 5.5f, 6.5f], arrays[0]);
        Assert.Equal([10f, 20f, 30f, 40f, 50f, 60f], arrays[1]);
    }

    private static async Task<List<float[]>> ScanFloatArrayColumn(
        TableCatalog catalog, string tableName, string columnName)
    {
        ITableProvider provider = catalog[tableName];
        Schema schema = provider.GetSchema();
        int columnIndex = -1;
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            if (schema.Columns[i].Name == columnName) { columnIndex = i; break; }
        }
        if (columnIndex < 0) throw new InvalidOperationException($"column {columnName} not in schema");

        List<float[]> arrays = new();
        await foreach (RowBatch batch in provider.ScanAsync(
            requiredColumns: null, filterHint: null, targetArena: null, cancellationToken: default))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                // Materialise inline — the batch arena recycles across the enumerator.
                arrays.Add(batch[r][columnIndex].AsArraySpan<float>(batch.Arena, catalog.SidecarRegistry).ToArray());
            }
            batch.Dispose();
        }
        return arrays;
    }

    private static async Task<List<string>> CollectStrings(TableCatalog catalog, string sql)
    {
        StatementPlan plan = catalog.Plan(sql);
        List<string> values = new();
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            for (int r = 0; r < batch.Count; r++)
            {
                values.Add(batch[r][0].AsString(batch.Arena, catalog.SidecarRegistry));
            }
        }
        return values;
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
}
