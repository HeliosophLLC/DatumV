using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Functions;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Server;

namespace DatumIngest.Tests.Server;

/// <summary>
/// Tests for <see cref="CommandDispatcher"/> meta-commands and SQL routing.
/// </summary>
public sealed class CommandDispatcherTests : IDisposable
{
    private readonly FunctionRegistry _functionRegistry = FunctionRegistry.CreateDefault();
    private readonly SessionManager _sessionManager;
    private readonly CommandDispatcher _dispatcher;
    private readonly Session _adminSession;
    private readonly Session _userSession;

    private static string FixturePath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
    }

    /// <summary>
    /// Sets up a dispatcher with admin and user sessions backed by a CSV catalog.
    /// </summary>
    public CommandDispatcherTests()
    {
        _sessionManager = new SessionManager(_functionRegistry);

        TableCatalog adminCatalog = new();
        adminCatalog.RegisterProvider("csv", () => new CsvTableProvider());
        adminCatalog.Register(new TableDescriptor(
            "csv", "people", FixturePath("simple.csv"),
            new Dictionary<string, string>()));
        _adminSession = _sessionManager.CreateLocalSession(SessionRole.Admin, adminCatalog);

        TableCatalog userCatalog = new();
        userCatalog.RegisterProvider("csv", () => new CsvTableProvider());
        _userSession = _sessionManager.CreateLocalSession(SessionRole.User, userCatalog);

        _dispatcher = new CommandDispatcher(_sessionManager);
    }

    /// <summary>
    /// Empty input returns a success result with empty message.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_EmptyInput_ReturnsSuccess()
    {
        CommandResult result = await _dispatcher.DispatchAsync(
            _adminSession, "  ", CancellationToken.None);
        Assert.Equal(CommandResultKind.Success, result.Kind);
    }

    /// <summary>
    /// Unknown dot-command returns an error.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_UnknownDotCommand_ReturnsError()
    {
        CommandResult result = await _dispatcher.DispatchAsync(
            _adminSession, ".unknown", CancellationToken.None);
        Assert.Equal(CommandResultKind.Error, result.Kind);
        Assert.Contains("Unknown command", result.Message);
    }

    /// <summary>
    /// .help returns a success result listing available commands.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_Help_ReturnsCommandList()
    {
        CommandResult result = await _dispatcher.DispatchAsync(
            _userSession, ".help", CancellationToken.None);

        Assert.Equal(CommandResultKind.Success, result.Kind);
        Assert.Contains(".tables", result.Message);
        Assert.Contains(".schema", result.Message);
        Assert.Contains(".explain", result.Message);
        Assert.Contains(".source", result.Message);
        Assert.Contains(".sessions", result.Message);
        Assert.Contains(".kill", result.Message);
    }

    /// <summary>
    /// .tables returns a list result.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_Tables_ReturnsListResult()
    {
        CommandResult result = await _dispatcher.DispatchAsync(
            _adminSession, ".tables", CancellationToken.None);
        Assert.Equal(CommandResultKind.ListResult, result.Kind);
        Assert.NotNull(result.Items);
    }

    /// <summary>
    /// .providers returns a list of registered format providers.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_Providers_ReturnsListResult()
    {
        CommandResult result = await _dispatcher.DispatchAsync(
            _adminSession, ".providers", CancellationToken.None);
        Assert.Equal(CommandResultKind.ListResult, result.Kind);
        Assert.Contains("csv", result.Items!);
    }

    /// <summary>
    /// .functions returns function signatures with parameter metadata.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_Functions_ReturnsFunctionList()
    {
        CommandResult result = await _dispatcher.DispatchAsync(
            _adminSession, ".functions", CancellationToken.None);
        Assert.Equal(CommandResultKind.FunctionList, result.Kind);
        Assert.True(result.Functions!.Count > 0);

        FunctionSignature? abs = result.Functions!.FirstOrDefault(f => f.Name == "abs");
        Assert.NotNull(abs);
        Assert.Single(abs.Parameters);
        Assert.Equal("value", abs.Parameters[0].Name);
    }

    /// <summary>
    /// .schema without an argument returns an error.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_SchemaNoArgument_ReturnsError()
    {
        CommandResult result = await _dispatcher.DispatchAsync(
            _adminSession, ".schema", CancellationToken.None);
        Assert.Equal(CommandResultKind.Error, result.Kind);
        Assert.Contains("Usage", result.Message);
    }

    /// <summary>
    /// .sessions returns session information for admin users.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_Sessions_ReturnsSessionList()
    {
        CommandResult result = await _dispatcher.DispatchAsync(
            _adminSession, ".sessions", CancellationToken.None);
        Assert.Equal(CommandResultKind.SessionList, result.Kind);
        Assert.True(result.Sessions!.Count >= 2); // At least admin + user sessions.
    }

    /// <summary>
    /// User cannot execute .sessions (admin-only).
    /// </summary>
    [Fact]
    public async Task DispatchAsync_UserListSessions_PermissionDenied()
    {
        CommandResult result = await _dispatcher.DispatchAsync(
            _userSession, ".sessions", CancellationToken.None);
        Assert.Equal(CommandResultKind.Error, result.Kind);
        Assert.Contains("Permission denied", result.Message);
    }

    /// <summary>
    /// User cannot execute .source (admin-only).
    /// </summary>
    [Fact]
    public async Task DispatchAsync_UserAddSource_PermissionDenied()
    {
        CommandResult result = await _dispatcher.DispatchAsync(
            _userSession, ".source csv:test=data.csv", CancellationToken.None);
        Assert.Equal(CommandResultKind.Error, result.Kind);
        Assert.Contains("Permission denied", result.Message);
    }

    /// <summary>
    /// .kill with invalid GUID returns an error.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_KillInvalidGuid_ReturnsError()
    {
        CommandResult result = await _dispatcher.DispatchAsync(
            _adminSession, ".kill not-a-guid", CancellationToken.None);
        Assert.Equal(CommandResultKind.Error, result.Kind);
        Assert.Contains("Usage", result.Message);
    }

    /// <summary>
    /// .kill with unknown session returns an error.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_KillUnknownSession_ReturnsError()
    {
        CommandResult result = await _dispatcher.DispatchAsync(
            _adminSession, $".kill {Guid.NewGuid()}", CancellationToken.None);
        Assert.Equal(CommandResultKind.Error, result.Kind);
        Assert.Contains("not found", result.Message);
    }

    /// <summary>
    /// .kill with valid session cancels the target and returns success.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_KillValidSession_CancelsTarget()
    {
        CancellationToken targetToken = _userSession.CancellationToken;

        CommandResult result = await _dispatcher.DispatchAsync(
            _adminSession, $".kill {_userSession.SessionId}", CancellationToken.None);

        Assert.Equal(CommandResultKind.Success, result.Kind);
        Assert.True(targetToken.IsCancellationRequested);
    }

    /// <summary>
    /// Invalid SQL returns a syntax error.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_InvalidSql_ReturnsSyntaxError()
    {
        CommandResult result = await _dispatcher.DispatchAsync(
            _adminSession, "NOT VALID SQL", CancellationToken.None);
        Assert.Equal(CommandResultKind.Error, result.Kind);
    }

    /// <summary>
    /// .explain without SQL argument returns a usage error.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_ExplainNoSql_ReturnsError()
    {
        CommandResult result = await _dispatcher.DispatchAsync(
            _adminSession, ".explain", CancellationToken.None);
        Assert.Equal(CommandResultKind.Error, result.Kind);
        Assert.Contains("Usage", result.Message);
    }

    /// <summary>
    /// ParseSourceDefinition correctly handles explicit provider format.
    /// </summary>
    [Fact]
    public void ParseSourceDefinition_ExplicitProvider_ParsesCorrectly()
    {
        TableDescriptor descriptor = CommandDispatcher.ParseSourceDefinition("csv:sales=data/sales.csv");
        Assert.Equal("csv", descriptor.Provider);
        Assert.Equal("sales", descriptor.Name);
        Assert.Equal("data/sales.csv", descriptor.FilePath);
    }

    /// <summary>
    /// ParseSourceDefinition auto-detects provider from file extension.
    /// </summary>
    [Fact]
    public void ParseSourceDefinition_AutoDetect_InfersProvider()
    {
        TableDescriptor descriptor = CommandDispatcher.ParseSourceDefinition("sales=data/sales.parquet");
        Assert.Equal("parquet", descriptor.Provider);
        Assert.Equal("sales", descriptor.Name);
    }

    /// <summary>
    /// ParseSourceDefinition throws on missing equals sign.
    /// </summary>
    [Fact]
    public void ParseSourceDefinition_NoEquals_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => CommandDispatcher.ParseSourceDefinition("nosource"));
    }

    /// <summary>
    /// ParseSourceDefinition parses options from semicolon-separated pairs.
    /// </summary>
    [Fact]
    public void ParseSourceDefinition_WithOptions_ParsesKeyValuePairs()
    {
        TableDescriptor descriptor = CommandDispatcher.ParseSourceDefinition(
            "csv:data=file.csv;delimiter=|;header=true");
        Assert.Equal("csv", descriptor.Provider);
        Assert.Equal("data", descriptor.Name);
        Assert.Equal("file.csv", descriptor.FilePath);
        Assert.Equal("|", descriptor.Options["delimiter"]);
        Assert.Equal("true", descriptor.Options["header"]);
    }

    /// <summary>
    /// Selecting a subset of columns returns a schema matching only those columns,
    /// and rows can be consumed without ordinal errors.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_SelectSubsetOfColumns_SchemaMatchesProjection()
    {
        CommandResult result = await _dispatcher.DispatchAsync(
            _adminSession, "SELECT name FROM people", CancellationToken.None);

        Assert.Equal(CommandResultKind.StreamingRows, result.Kind);
        Assert.NotNull(result.Schema);
        Assert.Single(result.Schema!.Columns);
        Assert.Equal("name", result.Schema.Columns[0].Name);

        // Consuming rows must not throw an ordinal mismatch.
        List<Row> rows = new();
        await foreach (Row row in result.Rows!)
        {
            Assert.Equal(1, row.FieldCount);
            rows.Add(row);
        }

        Assert.Equal(3, rows.Count);
    }

    /// <summary>
    /// <c>.source</c> with a root-object JSON file automatically expands into sub-tables
    /// for each top-level array property, without requiring an explicit
    /// <see cref="TableCatalog.ExpandMultiTableSourcesAsync"/> call.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_AddSourceRootObjectJson_ExpandsIntoSubTables()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("json", () => new JsonTableProvider());
        Session session = _sessionManager.CreateLocalSession(SessionRole.Admin, catalog);

        string sourcePath = FixturePath("root_object.json");
        CommandResult result = await _dispatcher.DispatchAsync(
            session, $".source json:data={sourcePath}", CancellationToken.None);

        Assert.Equal(CommandResultKind.Success, result.Kind);
        Assert.Contains("expanded", result.Message);

        // The original "data" table should no longer exist.
        Assert.False(catalog.TryResolve("data", out _));

        // Sub-tables should be registered with qualified names.
        Assert.True(catalog.TryResolve("data.licenses", out _));
        Assert.True(catalog.TryResolve("data.captions", out _));

        // Each sub-table should be queryable without errors.
        Schema licensesSchema = await catalog.GetSchemaAsync("data.licenses", CancellationToken.None);
        Assert.NotNull(licensesSchema.FindColumn("id"));
        Assert.NotNull(licensesSchema.FindColumn("name"));

        session.Dispose();
    }

    /// <summary>
    /// When greedy join reordering swaps the probe and build sides, the schema
    /// column order (resolved from SQL text) must still match the row value order.
    /// Regression test: <c>SELECT *</c> from a join where the right table is larger
    /// would produce headers in SQL-text order (left first) but values in execution
    /// order (right first) because the reordering moved the larger table to the probe side.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_SelectStar_JoinReordering_SchemaMatchesRowOrder()
    {
        TableCatalog catalog = new();

        // "departments" — small table (2 rows).
        catalog.RegisterProvider("departments", () => new FixedRowProvider(
            new Schema([
                new ColumnInfo("department_id", DataKind.Scalar, false),
                new ColumnInfo("department", DataKind.String, false),
            ]),
            estimatedRowCount: 2,
            new Row(["department_id", "department"],
                [DataValue.FromScalar(17f), DataValue.FromString("household")])));

        catalog.Register(new TableDescriptor(
            "departments", "departments", "departments.csv", new Dictionary<string, string>()));

        // "products" — large table (1000 rows estimated, only 1 actual for test simplicity).
        catalog.RegisterProvider("products", () => new FixedRowProvider(
            new Schema([
                new ColumnInfo("product_id", DataKind.Scalar, false),
                new ColumnInfo("product_name", DataKind.String, false),
                new ColumnInfo("department_id", DataKind.Scalar, false),
            ]),
            estimatedRowCount: 1000,
            new Row(["product_id", "product_name", "department_id"],
                [DataValue.FromScalar(105f), DataValue.FromString("Bakeware"), DataValue.FromScalar(17f)])));

        catalog.Register(new TableDescriptor(
            "products", "products", "products.csv", new Dictionary<string, string>()));

        SessionManager sessionManager = new(FunctionRegistry.CreateDefault());
        Session session = sessionManager.CreateLocalSession(SessionRole.Admin, catalog);
        CommandDispatcher dispatcher = new(sessionManager);

        CommandResult result = await dispatcher.DispatchAsync(
            session,
            "SELECT * FROM departments INNER JOIN products ON departments.department_id = products.department_id",
            CancellationToken.None);

        Assert.Equal(CommandResultKind.StreamingRows, result.Kind);
        Assert.NotNull(result.Schema);

        // Schema should list departments columns first (SQL-text order).
        List<string> schemaNames = result.Schema!.Columns.Select(column => column.Name).ToList();

        List<Row> rows = new();
        await foreach (Row row in result.Rows!)
        {
            rows.Add(row);
        }

        Assert.Single(rows);
        Row joined = rows[0];

        // The critical check: for each column position, the schema name and the
        // row value must correspond. Schema[0] is "department_id" (or deduped variant)
        // from the departments table — its value must be 17, not 105.
        for (int index = 0; index < schemaNames.Count; index++)
        {
            string name = schemaNames[index];
            DataValue value = joined[index];

            if (name.Contains("department_id", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("product", StringComparison.OrdinalIgnoreCase))
            {
                // department_id columns should have value 17.
                Assert.Equal(17f, value.AsScalar());
            }

            if (name.Equals("product_id", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Equal(105f, value.AsScalar());
            }

            if (name.Equals("department", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Equal("household", value.AsString());
            }

            if (name.Equals("product_name", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Equal("Bakeware", value.AsString());
            }
        }

        session.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _adminSession.Dispose();
        _userSession.Dispose();
    }
}

/// <summary>
/// Minimal table provider with a fixed schema, estimated row count, and a set of rows.
/// Used to trigger greedy join reordering by controlling the estimated cardinality.
/// </summary>
internal sealed class FixedRowProvider : ITableProvider
{
    private readonly Schema _schema;
    private readonly long _estimatedRowCount;
    private readonly Row[] _rows;

    /// <summary>
    /// Creates a fixed-row provider with the specified schema, row count estimate, and rows.
    /// </summary>
    public FixedRowProvider(Schema schema, long estimatedRowCount, params Row[] rows)
    {
        _schema = schema;
        _estimatedRowCount = estimatedRowCount;
        _rows = rows;
    }

    /// <inheritdoc/>
    public Task<Schema> GetSchemaAsync(TableDescriptor descriptor, CancellationToken cancellationToken)
    {
        return Task.FromResult(_schema);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<Row> OpenAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (Row row in _rows)
        {
            yield return row;
        }

        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<ProviderCapabilities> GetCapabilitiesAsync(
        TableDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new ProviderCapabilities(
            EstimatedRowCount: _estimatedRowCount,
            EstimatedRowSizeBytes: null,
            SupportsSeek: false,
            ColumnCosts: new Dictionary<string, ColumnCost>()));
    }
}
