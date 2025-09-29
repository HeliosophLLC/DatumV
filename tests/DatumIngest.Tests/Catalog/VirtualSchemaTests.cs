using System.Runtime.CompilerServices;
using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// Tests for the virtual schema infrastructure: registry resolution, parser support
/// for schema-qualified table references, and end-to-end SQL queries against
/// <c>information_schema</c> and <c>datum_catalog</c>.
/// </summary>
public sealed class VirtualSchemaTests
{
    private static readonly FunctionRegistry DefaultFunctions = FunctionRegistry.CreateDefault();
    private static readonly VirtualSchemaRegistry DefaultRegistry = VirtualSchemaRegistry.CreateDefault();

    // ─────────────────── Parser: schema-qualified table references ───────────────────

    [Fact]
    public void Parser_SchemaQualifiedTableReference_ParsesSchemaAndTable()
    {
        SelectStatement result = ParseStatement("SELECT * FROM information_schema.tables");

        Assert.NotNull(result.From);
        TableReference table = Assert.IsType<TableReference>(result.From.Source);
        Assert.Equal("information_schema", table.SchemaName);
        Assert.Equal("tables", table.Name);
        Assert.Null(table.Alias);
    }

    [Fact]
    public void Parser_SchemaQualifiedWithAlias_PreservesAlias()
    {
        SelectStatement result = ParseStatement("SELECT t.table_name FROM information_schema.tables t");

        Assert.NotNull(result.From);
        TableReference table = Assert.IsType<TableReference>(result.From.Source);
        Assert.Equal("information_schema", table.SchemaName);
        Assert.Equal("tables", table.Name);
        Assert.Equal("t", table.Alias);
    }

    [Fact]
    public void Parser_UnqualifiedTableReference_HasNullSchemaName()
    {
        SelectStatement result = ParseStatement("SELECT * FROM users");

        Assert.NotNull(result.From);
        TableReference table = Assert.IsType<TableReference>(result.From.Source);
        Assert.Null(table.SchemaName);
        Assert.Equal("users", table.Name);
    }

    [Fact]
    public void Parser_DatumCatalogSchemaQualified_ParsesCorrectly()
    {
        SelectStatement result = ParseStatement("SELECT * FROM datum_catalog.functions");

        Assert.NotNull(result.From);
        TableReference table = Assert.IsType<TableReference>(result.From.Source);
        Assert.Equal("datum_catalog", table.SchemaName);
        Assert.Equal("functions", table.Name);
    }

    // ─────────────────── Registry resolution ───────────────────

    [Fact]
    public void Registry_CreateDefault_RegistersInformationSchemaAndDatumCatalog()
    {
        VirtualSchemaRegistry registry = VirtualSchemaRegistry.CreateDefault();

        Assert.NotNull(registry.TryResolve("information_schema"));
        Assert.NotNull(registry.TryResolve("datum_catalog"));
    }

    [Fact]
    public void Registry_TryResolve_IsCaseInsensitive()
    {
        VirtualSchemaRegistry registry = VirtualSchemaRegistry.CreateDefault();

        Assert.NotNull(registry.TryResolve("INFORMATION_SCHEMA"));
        Assert.NotNull(registry.TryResolve("Datum_Catalog"));
    }

    [Fact]
    public void Registry_TryResolve_ReturnsNullForUnknownSchema()
    {
        VirtualSchemaRegistry registry = VirtualSchemaRegistry.CreateDefault();

        Assert.Null(registry.TryResolve("nonexistent"));
    }

    [Fact]
    public void InformationSchema_ExposesTablesColumnsAndSchemata()
    {
        IVirtualSchema? schema = DefaultRegistry.TryResolve("information_schema");
        Assert.NotNull(schema);

        Assert.NotNull(schema!.TryResolve("tables"));
        Assert.NotNull(schema.TryResolve("columns"));
        Assert.NotNull(schema.TryResolve("schemata"));
        Assert.Null(schema.TryResolve("nonexistent"));
    }

    [Fact]
    public void DatumCatalog_ExposesAllSixTables()
    {
        IVirtualSchema? schema = DefaultRegistry.TryResolve("datum_catalog");
        Assert.NotNull(schema);

        Assert.NotNull(schema!.TryResolve("providers"));
        Assert.NotNull(schema.TryResolve("functions"));
        Assert.NotNull(schema.TryResolve("function_parameters"));
        Assert.NotNull(schema.TryResolve("statistics"));
        Assert.NotNull(schema.TryResolve("indexes"));
        Assert.NotNull(schema.TryResolve("interactions"));
        Assert.Null(schema.TryResolve("nonexistent"));
    }

    // ─────────────────── End-to-end: information_schema ───────────────────

    [Fact]
    public async Task InformationSchema_Tables_ReturnsRegisteredTables()
    {
        TableCatalog catalog = CreateCatalog(("orders", MakeRows(1)), ("products", MakeRows(1)));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT table_name, table_schema, table_type FROM information_schema.tables ORDER BY table_name",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal("orders", results[0]["table_name"].AsString());
        Assert.Equal("public", results[0]["table_schema"].AsString());
        Assert.Equal("BASE TABLE", results[0]["table_type"].AsString());
        Assert.Equal("products", results[1]["table_name"].AsString());
    }

    [Fact]
    public async Task InformationSchema_Tables_WithAlias_SupportsQualifiedAccess()
    {
        TableCatalog catalog = CreateCatalog(("items", MakeRows(1)));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT t.table_name FROM information_schema.tables t",
            catalog);

        Assert.Single(results);
        Assert.Equal("items", results[0]["table_name"].AsString());
    }

    [Fact]
    public async Task InformationSchema_Columns_ReturnsColumnMetadata()
    {
        TableCatalog catalog = CreateCatalog(("users", new Row[]
        {
            MakeRow(("id", DataValue.FromInt32(1)), ("name", DataValue.FromString("Alice"))),
        }));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT column_name, data_type, ordinal_position FROM information_schema.columns WHERE table_name = 'users' ORDER BY ordinal_position",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal("id", results[0]["column_name"].AsString());
        Assert.Equal("Int32", results[0]["data_type"].AsString());
        Assert.Equal(1, results[0]["ordinal_position"].AsInt32());
        Assert.Equal("name", results[1]["column_name"].AsString());
        Assert.Equal("String", results[1]["data_type"].AsString());
        Assert.Equal(2, results[1]["ordinal_position"].AsInt32());
    }

    [Fact]
    public async Task InformationSchema_Schemata_ReturnsKnownSchemas()
    {
        TableCatalog catalog = CreateCatalog();

        List<Row> results = await ExecuteQueryAsync(
            "SELECT schema_name FROM information_schema.schemata ORDER BY schema_name",
            catalog);

        List<string> schemaNames = results.Select(row => row["schema_name"].AsString()).ToList();
        Assert.Contains("public", schemaNames);
        Assert.Contains("temp", schemaNames);
        Assert.Contains("information_schema", schemaNames);
        Assert.Contains("datum_catalog", schemaNames);
    }

    // ─────────────────── End-to-end: datum_catalog ───────────────────

    [Fact]
    public async Task DatumCatalog_Functions_ReturnsRegisteredFunctions()
    {
        TableCatalog catalog = CreateCatalog();

        List<Row> results = await ExecuteQueryAsync(
            "SELECT function_name, function_type FROM datum_catalog.functions WHERE function_type = 'SCALAR' LIMIT 5",
            catalog);

        Assert.NotEmpty(results);
        Assert.All(results, row => Assert.Equal("SCALAR", row["function_type"].AsString()));
    }

    [Fact]
    public async Task DatumCatalog_Functions_IncludesAggregatesAndTableValued()
    {
        TableCatalog catalog = CreateCatalog();

        List<Row> results = await ExecuteQueryAsync(
            "SELECT DISTINCT function_type FROM datum_catalog.functions ORDER BY function_type",
            catalog);

        List<string> types = results.Select(row => row["function_type"].AsString()).ToList();
        Assert.Contains("SCALAR", types);
        Assert.Contains("AGGREGATE", types);
    }

    [Fact]
    public async Task DatumCatalog_Functions_IncludesEnrichedColumns()
    {
        TableCatalog catalog = CreateCatalog();

        List<Row> results = await ExecuteQueryAsync(
            "SELECT function_name, function_type, category, return_type, parameter_count, query_unit_cost FROM datum_catalog.functions WHERE function_name = 'abs' LIMIT 1",
            catalog);

        Assert.Single(results);
        Assert.Equal("abs", results[0]["function_name"].AsString());
        Assert.Equal("SCALAR", results[0]["function_type"].AsString());
        Assert.False(results[0]["category"].IsNull);
        Assert.False(results[0]["parameter_count"].IsNull);
    }

    [Fact]
    public async Task DatumCatalog_FunctionParameters_ReturnsParameterDetails()
    {
        TableCatalog catalog = CreateCatalog();

        List<Row> results = await ExecuteQueryAsync(
            "SELECT function_name, ordinal_position, parameter_name, data_type, is_optional FROM datum_catalog.function_parameters WHERE function_name = 'substring' ORDER BY ordinal_position",
            catalog);

        Assert.NotEmpty(results);
        Assert.Equal("substring", results[0]["function_name"].AsString());
        Assert.Equal(1, results[0]["ordinal_position"].AsInt32());
        Assert.Contains("NO", results[0]["is_optional"].AsString());
    }

    [Fact]
    public async Task DatumCatalog_Interactions_ReturnsEmptyWhenNoManifest()
    {
        TableCatalog catalog = CreateCatalog(("items", MakeRows(1)));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT table_name, column_a, column_b FROM datum_catalog.interactions",
            catalog);

        Assert.Empty(results);
    }

    [Fact]
    public async Task DatumCatalog_Indexes_ReturnsEmptyWhenNoIndex()
    {
        TableCatalog catalog = CreateCatalog(("items", MakeRows(1)));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT table_name, column_name, index_type FROM datum_catalog.indexes",
            catalog);

        Assert.Empty(results);
    }

    // ─────────────────── Error cases ───────────────────

    [Fact]
    public async Task UnknownSchema_ThrowsInvalidOperationException()
    {
        TableCatalog catalog = CreateCatalog();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ExecuteQueryAsync("SELECT * FROM nonexistent_schema.tables", catalog));
    }

    [Fact]
    public async Task UnknownTableInKnownSchema_ThrowsInvalidOperationException()
    {
        TableCatalog catalog = CreateCatalog();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ExecuteQueryAsync("SELECT * FROM information_schema.nonexistent", catalog));
    }

    // ─────────────────── Helpers ───────────────────

    private static SelectStatement ParseStatement(string sql)
    {
        return ((SelectQueryExpression)SqlParser.Parse(sql)).Statement;
    }

    private static Row MakeRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = columns.Select(column => column.Name).ToArray();
        DataValue[] values = columns.Select(column => column.Value).ToArray();
        return new Row(names, values);
    }

    private static Row[] MakeRows(int count)
    {
        Row[] rows = new Row[count];
        for (int index = 0; index < count; index++)
        {
            rows[index] = MakeRow(("id", DataValue.FromInt32(index + 1)));
        }
        return rows;
    }

    private static TableCatalog CreateCatalog(params (string Name, Row[] Rows)[] tables)
    {
        TableCatalog catalog = new();

        foreach ((string name, Row[] rows) in tables)
        {
            InMemoryTableProvider provider = new(rows);
            catalog.RegisterProvider(name, () => provider);
            catalog.Register(new TableDescriptor(name, name, "", new Dictionary<string, string>()));
        }

        return catalog;
    }

    private static async Task<List<Row>> ExecuteQueryAsync(string sql, TableCatalog catalog)
    {
        QueryExpression query = SqlParser.Parse(sql);
        QueryPlanner planner = new(catalog, DefaultFunctions, DefaultRegistry);

        ExecutionContext context = new(
            CancellationToken.None,
            DefaultFunctions,
            catalog,
            new LocalBufferPool());

        IQueryOperator plan = await planner.PlanWithSubqueriesAsync(query, context, CancellationToken.None);

        return await plan.CollectRowsAsync(context);
    }

    /// <summary>
    /// Minimal in-memory table provider for test catalog registration.
    /// </summary>
    private sealed class InMemoryTableProvider : ITableProvider
    {
        private readonly Row[] _rows;

        public InMemoryTableProvider(Row[] rows)
        {
            _rows = rows;
        }

        public Task<Schema> GetSchemaAsync(TableDescriptor descriptor, CancellationToken cancellationToken)
        {
            if (_rows.Length == 0)
            {
                return Task.FromResult(new Schema([new ColumnInfo("empty", DataKind.String, nullable: true)]));
            }

            List<ColumnInfo> columns = [];
            foreach (string name in _rows[0].ColumnNames)
            {
                columns.Add(new ColumnInfo(name, _rows[0][name].Kind, nullable: true));
            }

            return Task.FromResult(new Schema(columns));
        }

        public Task<ProviderCapabilities> GetCapabilitiesAsync(
            TableDescriptor descriptor, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ProviderCapabilities(
                EstimatedRowCount: _rows.Length,
                EstimatedRowSizeBytes: null,
                SupportsSeek: false,
                ColumnCosts: new Dictionary<string, ColumnCost>()));
        }

        public async IAsyncEnumerable<RowBatch> OpenAsync(
            TableDescriptor descriptor,
            IReadOnlySet<string>? requiredColumns,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            RowBatch batch = RowBatch.Rent(64);

            foreach (Row row in _rows)
            {
                batch.Add(row);

                if (batch.IsFull)
                {
                    yield return batch;
                    batch = RowBatch.Rent(64);
                }
            }

            if (batch.Count > 0)
            {
                yield return batch;
            }

            await Task.CompletedTask;
        }
    }
}
