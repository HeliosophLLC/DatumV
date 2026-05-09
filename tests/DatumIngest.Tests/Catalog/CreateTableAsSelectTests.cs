using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.DatumFile.V2;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Catalog;

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

        catalog.Plan("CREATE TABLE test AS SELECT Value FROM RANGE(1, 10)");

        Assert.True(catalog.HasTable("test"));
        ITableProvider provider = catalog["test"];
        Schema schema = provider.GetSchema();
        Assert.Equal(["Value"], schema.Columns.Select(c => c.Name));
        Assert.Equal(DataKind.Int32, schema.Columns[0].Kind);

        // RANGE(1, 10) emits inclusive endpoints — 10 rows.
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

        catalog.Plan("CREATE TEMP TABLE t AS SELECT Value FROM RANGE(1, 3)");

        ITableProvider provider = catalog["t"];
        Assert.IsType<InMemoryTableProvider>(provider);
        Assert.Equal(3, provider.GetRowCount());
    }

    [Fact]
    public void Ctas_PersistentTarget_MaterialisesDatumFile()
    {
        Pool pool = CreatePool();
        using TableCatalog catalog = CreateCatalog(CatalogPath);

        catalog.Plan("CREATE TABLE numbers AS SELECT Value FROM RANGE(1, 5)");

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

        catalog.Plan("CREATE TABLE reports.numbers AS SELECT Value FROM RANGE(1, 5)");

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
            catalog.Plan("CREATE TEMP TABLE myapp.t AS SELECT Value FROM RANGE(1, 3)"));
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
            catalog.Plan("CREATE TEMP TABLE t AS SELECT Value FROM RANGE(1, 3)"));
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
