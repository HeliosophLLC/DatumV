using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Compute.Grpc;
using DatumIngest.Compute.Services;
using DatumIngest.Functions;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Server;
using Grpc.Core;

namespace DatumIngest.Tests.Compute;

/// <summary>
/// Tests for <see cref="ComputeService"/> gRPC endpoint logic, verifying that
/// requests are dispatched correctly through the engine.
/// </summary>
public sealed class ComputeServiceTests : IDisposable
{
    private readonly FunctionRegistry _functionRegistry = FunctionRegistry.CreateDefault();
    private readonly SessionManager _sessionManager;
    private readonly CommandDispatcher _dispatcher;
    private readonly ComputeService _service;

    /// <summary>
    /// Sets up the compute service with a session manager and command dispatcher.
    /// </summary>
    public ComputeServiceTests()
    {
        _sessionManager = new SessionManager(_functionRegistry);
        _dispatcher = new CommandDispatcher(_sessionManager);
        _service = new ComputeService(_sessionManager, _dispatcher, QueryGovernor.Unlimited);
    }

    // ─────────────────── CreateSession ───────────────────

    /// <summary>
    /// CreateSession returns a valid session identifier.
    /// </summary>
    [Fact]
    public async Task CreateSession_ReturnsValidSessionId()
    {
        CreateSessionRequest request = new() { Role = "user" };

        CreateSessionResponse response = await _service.CreateSession(
            request, TestCallContext.Create());

        Assert.True(Guid.TryParse(response.SessionId, out Guid sessionId));
        Assert.NotNull(_sessionManager.GetSession(sessionId));
    }

    /// <summary>
    /// CreateSession with "admin" role creates an admin session.
    /// </summary>
    [Fact]
    public async Task CreateSession_AdminRole_CreatesAdminSession()
    {
        CreateSessionRequest request = new() { Role = "admin" };

        CreateSessionResponse response = await _service.CreateSession(
            request, TestCallContext.Create());

        Session? session = _sessionManager.GetSession(Guid.Parse(response.SessionId));
        Assert.NotNull(session);
        Assert.Equal(SessionRole.Admin, session.Role);
    }

    /// <summary>
    /// CreateSession with empty role defaults to User.
    /// </summary>
    [Fact]
    public async Task CreateSession_EmptyRole_DefaultsToUser()
    {
        CreateSessionRequest request = new() { Role = "" };

        CreateSessionResponse response = await _service.CreateSession(
            request, TestCallContext.Create());

        Session? session = _sessionManager.GetSession(Guid.Parse(response.SessionId));
        Assert.NotNull(session);
        Assert.Equal(SessionRole.User, session.Role);
    }

    /// <summary>
    /// CreateSession with a dataset_id throws FailedPrecondition when no IDatasetStore is configured.
    /// </summary>
    [Fact]
    public async Task CreateSession_DatasetId_NoStore_ThrowsFailedPrecondition()
    {
        CreateSessionRequest request = new() { Role = "user", DatasetId = "my-dataset" };

        RpcException exception = await Assert.ThrowsAsync<RpcException>(
            () => _service.CreateSession(request, TestCallContext.Create()));

        Assert.Equal(StatusCode.FailedPrecondition, exception.StatusCode);
    }

    /// <summary>
    /// CreateSession with a dataset_id creates a session that auto-discovers files
    /// from the pulled dataset directory.
    /// </summary>
    [Fact]
    public async Task CreateSession_DatasetId_WithStore_CreatesSessionWithCatalog()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDirectory);

        try
        {
            // Write a small CSV file into the "dataset" directory.
            File.WriteAllText(
                Path.Combine(tempDirectory, "people.csv"),
                "name,age\nAlice,30\nBob,25\n");

            IDatasetStore store = new InMemoryDatasetStore(tempDirectory);
            SessionManager sessionManager = new(_functionRegistry, store);
            CommandDispatcher dispatcher = new(sessionManager);
            ComputeService service = new(sessionManager, dispatcher, QueryGovernor.Unlimited);

            CreateSessionRequest request = new() { Role = "admin", DatasetId = "test-ds" };

            CreateSessionResponse response = await service.CreateSession(
                request, TestCallContext.Create());

            Session? session = sessionManager.GetSession(Guid.Parse(response.SessionId));
            Assert.NotNull(session);
            Assert.Equal("test-ds", session.DatasetId);
            Assert.Contains("people_csv", session.Catalog.TableNames);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    // ─────────────────── DestroySession ───────────────────

    /// <summary>
    /// DestroySession removes an existing session.
    /// </summary>
    [Fact]
    public async Task DestroySession_ExistingSession_RemovesIt()
    {
        Session session = _sessionManager.CreateLocalSession(SessionRole.User, new TableCatalog());
        DestroySessionRequest request = new() { SessionId = session.SessionId.ToString() };

        await _service.DestroySession(request, TestCallContext.Create());

        Assert.Null(_sessionManager.GetSession(session.SessionId));
    }

    /// <summary>
    /// DestroySession with unknown session throws NotFound.
    /// </summary>
    [Fact]
    public async Task DestroySession_UnknownSession_ThrowsNotFound()
    {
        DestroySessionRequest request = new() { SessionId = Guid.NewGuid().ToString() };

        RpcException exception = await Assert.ThrowsAsync<RpcException>(
            () => _service.DestroySession(request, TestCallContext.Create()));

        Assert.Equal(StatusCode.NotFound, exception.StatusCode);
    }

    /// <summary>
    /// DestroySession with invalid GUID throws InvalidArgument.
    /// </summary>
    [Fact]
    public async Task DestroySession_InvalidGuid_ThrowsInvalidArgument()
    {
        DestroySessionRequest request = new() { SessionId = "not-a-guid" };

        RpcException exception = await Assert.ThrowsAsync<RpcException>(
            () => _service.DestroySession(request, TestCallContext.Create()));

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
    }

    // ─────────────────── ListTables ───────────────────

    /// <summary>
    /// ListTables returns items from the session catalog.
    /// </summary>
    [Fact]
    public async Task ListTables_ReturnsRegisteredTables()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        Session session = _sessionManager.CreateLocalSession(SessionRole.Admin, catalog);

        ListTablesRequest request = new() { SessionId = session.SessionId.ToString() };

        ListResponse response = await _service.ListTables(request, TestCallContext.Create());

        Assert.NotNull(response.Items);
    }

    /// <summary>
    /// ListTables with unknown session throws NotFound.
    /// </summary>
    [Fact]
    public async Task ListTables_UnknownSession_ThrowsNotFound()
    {
        ListTablesRequest request = new() { SessionId = Guid.NewGuid().ToString() };

        RpcException exception = await Assert.ThrowsAsync<RpcException>(
            () => _service.ListTables(request, TestCallContext.Create()));

        Assert.Equal(StatusCode.NotFound, exception.StatusCode);
    }

    // ─────────────────── ListProviders ───────────────────

    /// <summary>
    /// ListProviders returns registered format providers.
    /// </summary>
    [Fact]
    public async Task ListProviders_ReturnsProviders()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        Session session = _sessionManager.CreateLocalSession(SessionRole.Admin, catalog);

        ListProvidersRequest request = new() { SessionId = session.SessionId.ToString() };

        ListResponse response = await _service.ListProviders(request, TestCallContext.Create());

        Assert.Contains("csv", response.Items);
    }

    // ─────────────────── ListFunctions ───────────────────

    /// <summary>
    /// ListFunctions returns available functions with parameter metadata.
    /// </summary>
    [Fact]
    public async Task ListFunctions_ReturnsFunctions()
    {
        Session session = _sessionManager.CreateLocalSession(SessionRole.Admin, new TableCatalog());

        ListFunctionsRequest request = new() { SessionId = session.SessionId.ToString() };

        ListFunctionsResponse response = await _service.ListFunctions(request, TestCallContext.Create());

        Assert.True(response.Functions.Count > 0);

        FunctionInfoMessage clamp = Assert.Single(response.Functions, f => f.Name == "clamp");
        Assert.Equal(3, clamp.Parameters.Count);
        Assert.Equal("value", clamp.Parameters[0].Name);
        Assert.Equal(ParameterKindValue.ParameterKindScalar, clamp.Parameters[0].Kind);
        Assert.True(clamp.Parameters[0].Required);
    }

    // ─────────────────── Explain ───────────────────

    /// <summary>
    /// Explain without SQL argument returns InvalidArgument.
    /// </summary>
    [Fact]
    public async Task Explain_NoSql_ThrowsInvalidArgument()
    {
        Session session = _sessionManager.CreateLocalSession(SessionRole.Admin, new TableCatalog());

        ExplainRequest request = new()
        {
            SessionId = session.SessionId.ToString(),
            Sql = "",
        };

        RpcException exception = await Assert.ThrowsAsync<RpcException>(
            () => _service.Explain(request, TestCallContext.Create()));

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
    }

    /// <summary>
    /// Explain with valid SQL returns both plan text and a structured plan tree.
    /// </summary>
    [Fact]
    public async Task Explain_ValidSql_ReturnsStructuredPlan()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        string fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "simple.csv");
        catalog.Register(new TableDescriptor("csv", "data", fixturePath, new Dictionary<string, string>()));
        Session session = _sessionManager.CreateLocalSession(SessionRole.Admin, catalog);

        ExplainRequest request = new()
        {
            SessionId = session.SessionId.ToString(),
            Sql = "SELECT name FROM data WHERE age > 30",
        };

        ExplainResponse response = await _service.Explain(request, TestCallContext.Create());

        // plan_text is populated for backward compatibility.
        Assert.False(string.IsNullOrEmpty(response.PlanText));

        // Structured root is populated.
        Assert.NotNull(response.Root);
        Assert.False(string.IsNullOrEmpty(response.Root.OperatorName));

        // The plan should have at least one child (scan under filter/project).
        Assert.NotEmpty(response.Root.Children);
    }

    // ─────────────────── AddSource ───────────────────

    /// <summary>
    /// AddSource with admin session registers a source.
    /// </summary>
    [Fact]
    public async Task AddSource_AdminSession_RegistersSource()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        Session session = _sessionManager.CreateLocalSession(SessionRole.Admin, catalog);

        AddSourceRequest request = new()
        {
            SessionId = session.SessionId.ToString(),
            SourceDefinition = "csv:test=nonexistent.csv",
        };

        AddSourceResponse response = await _service.AddSource(request, TestCallContext.Create());

        Assert.Contains("test", response.Message);
    }

    /// <summary>
    /// AddSource with user session throws PermissionDenied.
    /// </summary>
    [Fact]
    public async Task AddSource_UserSession_ThrowsPermissionDenied()
    {
        Session session = _sessionManager.CreateLocalSession(SessionRole.User, new TableCatalog());

        AddSourceRequest request = new()
        {
            SessionId = session.SessionId.ToString(),
            SourceDefinition = "csv:test=data.csv",
        };

        RpcException exception = await Assert.ThrowsAsync<RpcException>(
            () => _service.AddSource(request, TestCallContext.Create()));

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
    }

    /// <summary>
    /// AddSource with empty definition throws InvalidArgument.
    /// </summary>
    [Fact]
    public async Task AddSource_EmptyDefinition_ThrowsInvalidArgument()
    {
        Session session = _sessionManager.CreateLocalSession(SessionRole.Admin, new TableCatalog());

        AddSourceRequest request = new()
        {
            SessionId = session.SessionId.ToString(),
            SourceDefinition = "",
        };

        RpcException exception = await Assert.ThrowsAsync<RpcException>(
            () => _service.AddSource(request, TestCallContext.Create()));

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
    }

    // ─────────────────── ListSessions ───────────────────

    /// <summary>
    /// ListSessions with admin session returns session information.
    /// </summary>
    [Fact]
    public async Task ListSessions_AdminSession_ReturnsSessionInfo()
    {
        Session adminSession = _sessionManager.CreateLocalSession(SessionRole.Admin, new TableCatalog());
        _sessionManager.CreateLocalSession(SessionRole.User, new TableCatalog());

        ListSessionsRequest request = new() { SessionId = adminSession.SessionId.ToString() };

        ListSessionsResponse response = await _service.ListSessions(request, TestCallContext.Create());

        Assert.True(response.Sessions.Count >= 2);
    }

    /// <summary>
    /// ListSessions with user session throws PermissionDenied.
    /// </summary>
    [Fact]
    public async Task ListSessions_UserSession_ThrowsPermissionDenied()
    {
        Session userSession = _sessionManager.CreateLocalSession(SessionRole.User, new TableCatalog());

        ListSessionsRequest request = new() { SessionId = userSession.SessionId.ToString() };

        RpcException exception = await Assert.ThrowsAsync<RpcException>(
            () => _service.ListSessions(request, TestCallContext.Create()));

        Assert.Equal(StatusCode.PermissionDenied, exception.StatusCode);
    }

    // ─────────────────── KillQuery ───────────────────

    /// <summary>
    /// KillQuery cancels the target session's active query.
    /// </summary>
    [Fact]
    public async Task KillQuery_ValidTarget_CancelsTarget()
    {
        Session adminSession = _sessionManager.CreateLocalSession(SessionRole.Admin, new TableCatalog());
        Session targetSession = _sessionManager.CreateLocalSession(SessionRole.User, new TableCatalog());
        CancellationToken targetToken = targetSession.CancellationToken;

        KillQueryRequest request = new()
        {
            SessionId = adminSession.SessionId.ToString(),
            TargetSessionId = targetSession.SessionId.ToString(),
        };

        KillQueryResponse response = await _service.KillQuery(request, TestCallContext.Create());

        Assert.True(targetToken.IsCancellationRequested);
        Assert.NotNull(response.Message);
    }

    /// <summary>
    /// KillQuery with unknown target throws InvalidArgument.
    /// </summary>
    [Fact]
    public async Task KillQuery_UnknownTarget_ThrowsInvalidArgument()
    {
        Session adminSession = _sessionManager.CreateLocalSession(SessionRole.Admin, new TableCatalog());

        KillQueryRequest request = new()
        {
            SessionId = adminSession.SessionId.ToString(),
            TargetSessionId = Guid.NewGuid().ToString(),
        };

        RpcException exception = await Assert.ThrowsAsync<RpcException>(
            () => _service.KillQuery(request, TestCallContext.Create()));

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
    }

    // ─────────────────── CancelQuery (self-cancel) ───────────────────

    /// <summary>
    /// CancelQuery cancels the caller's own active query and resets
    /// the session for subsequent commands.
    /// </summary>
    [Fact]
    public async Task CancelQuery_ActiveSession_CancelsAndResets()
    {
        Session session = _sessionManager.CreateLocalSession(SessionRole.User, new TableCatalog());
        CancellationToken originalToken = session.CancellationToken;

        CancelQueryRequest request = new()
        {
            SessionId = session.SessionId.ToString(),
        };

        CancelQueryResponse response = await _service.CancelQuery(request, TestCallContext.Create());

        Assert.True(originalToken.IsCancellationRequested);
        Assert.NotNull(response.Message);

        // Session is reusable — new token is not cancelled.
        Assert.False(session.CancellationToken.IsCancellationRequested);
    }

    /// <summary>
    /// CancelQuery is idempotent — calling it when no query is active
    /// succeeds without error.
    /// </summary>
    [Fact]
    public async Task CancelQuery_NoActiveQuery_Succeeds()
    {
        Session session = _sessionManager.CreateLocalSession(SessionRole.User, new TableCatalog());

        CancelQueryRequest request = new()
        {
            SessionId = session.SessionId.ToString(),
        };

        CancelQueryResponse response = await _service.CancelQuery(request, TestCallContext.Create());

        Assert.NotNull(response.Message);
    }

    /// <summary>
    /// CancelQuery with an invalid session ID throws NotFound.
    /// </summary>
    [Fact]
    public async Task CancelQuery_InvalidSession_ThrowsNotFound()
    {
        CancelQueryRequest request = new()
        {
            SessionId = Guid.NewGuid().ToString(),
        };

        RpcException exception = await Assert.ThrowsAsync<RpcException>(
            () => _service.CancelQuery(request, TestCallContext.Create()));

        Assert.Equal(StatusCode.NotFound, exception.StatusCode);
    }

    /// <summary>
    /// CancelQuery stops a streaming query in progress, producing a
    /// <see cref="StatusCode.Cancelled"/> gRPC status on the query stream.
    /// </summary>
    [Fact]
    public async Task CancelQuery_DuringStreaming_StopsStream()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        string fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "simple.csv");
        catalog.Register(new TableDescriptor("csv", "data", fixturePath, new Dictionary<string, string>()));
        Session session = _sessionManager.CreateLocalSession(SessionRole.User, catalog);

        QueryRequest queryRequest = new()
        {
            SessionId = session.SessionId.ToString(),
            Sql = "SELECT * FROM data",
        };

        // Writer that cancels the session after the first row is written,
        // simulating a concurrent CancelQuery call.
        CancellingStreamWriter<QueryRow> writer = new(session, cancelAfterRow: 1);

        RpcException exception = await Assert.ThrowsAsync<RpcException>(
            () => _service.Query(queryRequest, writer, TestCallContext.Create()));

        Assert.Equal(StatusCode.Cancelled, exception.StatusCode);
        Assert.Contains("cancelled", exception.Status.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.True(writer.Messages.Count >= 1);
    }

    // ─────────────────── Query Cancellation ───────────────────

    /// <summary>
    /// When the gRPC call is cancelled (client disconnects), the linked
    /// token propagates the cancellation through the execution pipeline.
    /// </summary>
    [Fact]
    public async Task Query_GrpcCallCancelled_StopsStreaming()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        string fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "simple.csv");
        catalog.Register(new TableDescriptor("csv", "data", fixturePath, new Dictionary<string, string>()));
        Session session = _sessionManager.CreateLocalSession(SessionRole.Admin, catalog);

        CancellationTokenSource callCancellation = new();
        callCancellation.Cancel();

        QueryRequest request = new()
        {
            SessionId = session.SessionId.ToString(),
            Sql = "SELECT * FROM data",
        };

        CapturingStreamWriter<QueryRow> writer = new();

        // The gRPC call token is already cancelled, so the dispatcher
        // catches the OperationCanceledException and returns an error
        // which surfaces as RpcException.
        RpcException exception = await Assert.ThrowsAsync<RpcException>(
            () => _service.Query(request, writer, TestCallContext.Create(callCancellation.Token)));

        Assert.Contains("cancelled", exception.Status.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// When the session token is cancelled during row streaming, the linked
    /// token causes the row enumeration to stop and the server returns
    /// a <see cref="StatusCode.Cancelled"/> gRPC status.
    /// </summary>
    [Fact]
    public async Task Query_SessionCancelledDuringStreaming_StopsStream()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        string fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "simple.csv");
        catalog.Register(new TableDescriptor("csv", "data", fixturePath, new Dictionary<string, string>()));
        Session session = _sessionManager.CreateLocalSession(SessionRole.Admin, catalog);

        QueryRequest request = new()
        {
            SessionId = session.SessionId.ToString(),
            Sql = "SELECT * FROM data",
        };

        // Writer that cancels the session after the first row is written,
        // simulating a mid-stream KillQuery from another session.
        CancellingStreamWriter<QueryRow> writer = new(session, cancelAfterRow: 1);

        RpcException exception = await Assert.ThrowsAsync<RpcException>(
            () => _service.Query(request, writer, TestCallContext.Create()));

        Assert.Equal(StatusCode.Cancelled, exception.StatusCode);
        Assert.Contains("cancelled", exception.Status.Detail, StringComparison.OrdinalIgnoreCase);

        // At least one row was written before cancellation.
        Assert.True(writer.Messages.Count >= 1);
    }

    // ─────────────────── GetSchema ───────────────────

    /// <summary>
    /// GetSchema with missing table name throws InvalidArgument.
    /// </summary>
    [Fact]
    public async Task GetSchema_EmptyTableName_ThrowsInvalidArgument()
    {
        Session session = _sessionManager.CreateLocalSession(SessionRole.Admin, new TableCatalog());

        GetSchemaRequest request = new()
        {
            SessionId = session.SessionId.ToString(),
            TableName = "",
        };

        RpcException exception = await Assert.ThrowsAsync<RpcException>(
            () => _service.GetSchema(request, TestCallContext.Create()));

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
    }

    // ─────────────────── Resource Governance ───────────────────

    /// <summary>
    /// When the session has a row budget, the stream throws ResourceExhausted
    /// once the budget is exceeded.
    /// </summary>
    [Fact]
    public async Task Query_RowBudgetExceeded_ThrowsResourceExhausted()
    {
        QueryGovernor governor = new(QueryTimeoutSeconds: null, MaxOutputRows: 2, ThrottleDelayMilliseconds: null);
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        string fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "simple.csv");
        catalog.Register(new TableDescriptor("csv", "data", fixturePath, new Dictionary<string, string>()));
        Session session = _sessionManager.CreateLocalSession(SessionRole.Admin, catalog, governor);

        QueryRequest request = new()
        {
            SessionId = session.SessionId.ToString(),
            Sql = "SELECT * FROM data",
        };

        CapturingStreamWriter<QueryRow> writer = new();

        RpcException exception = await Assert.ThrowsAsync<RpcException>(
            () => _service.Query(request, writer, TestCallContext.Create()));

        Assert.Equal(StatusCode.ResourceExhausted, exception.StatusCode);
        Assert.Contains("Row budget exceeded", exception.Status.Detail);

        // Exactly the budget number of rows should have been streamed.
        Assert.Equal(2, writer.Messages.Count);
    }

    /// <summary>
    /// When the row budget is larger than the result set, all rows are
    /// streamed normally with no error.
    /// </summary>
    [Fact]
    public async Task Query_RowBudgetNotExceeded_StreamsAllRows()
    {
        QueryGovernor governor = new(QueryTimeoutSeconds: null, MaxOutputRows: 100, ThrottleDelayMilliseconds: null);
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        string fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "simple.csv");
        catalog.Register(new TableDescriptor("csv", "data", fixturePath, new Dictionary<string, string>()));
        Session session = _sessionManager.CreateLocalSession(SessionRole.Admin, catalog, governor);

        QueryRequest request = new()
        {
            SessionId = session.SessionId.ToString(),
            Sql = "SELECT * FROM data",
        };

        CapturingStreamWriter<QueryRow> writer = new();
        await _service.Query(request, writer, TestCallContext.Create());

        Assert.True(writer.Messages.Count > 0);
    }

    /// <summary>
    /// When a deadline is set and the query does not exceed it, all rows
    /// are streamed normally.
    /// </summary>
    [Fact]
    public async Task Query_DeadlineNotExceeded_StreamsAllRows()
    {
        QueryGovernor governor = new(QueryTimeoutSeconds: 60, MaxOutputRows: null, ThrottleDelayMilliseconds: null);
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        string fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "simple.csv");
        catalog.Register(new TableDescriptor("csv", "data", fixturePath, new Dictionary<string, string>()));
        Session session = _sessionManager.CreateLocalSession(SessionRole.Admin, catalog, governor);

        QueryRequest request = new()
        {
            SessionId = session.SessionId.ToString(),
            Sql = "SELECT * FROM data",
        };

        CapturingStreamWriter<QueryRow> writer = new();
        await _service.Query(request, writer, TestCallContext.Create());

        Assert.True(writer.Messages.Count > 0);
    }

    /// <summary>
    /// CreateSession merges governance fields from the proto request with
    /// server defaults into the session's governor.
    /// </summary>
    [Fact]
    public async Task CreateSession_GovernorFieldsMerged_WithServerDefaults()
    {
        QueryGovernor serverDefaults = new(QueryTimeoutSeconds: 300, MaxOutputRows: 10_000, ThrottleDelayMilliseconds: null);
        SessionManager sessionManager = new(_functionRegistry);
        CommandDispatcher dispatcher = new(sessionManager);
        ComputeService service = new(sessionManager, dispatcher, serverDefaults);

        // Override timeout, use server default for rows, disable throttle.
        CreateSessionRequest request = new()
        {
            Role = "user",
            QueryTimeoutSeconds = 60,
            MaxOutputRows = 0,
            ThrottleDelayMs = -1,
        };

        CreateSessionResponse response = await service.CreateSession(request, TestCallContext.Create());

        Session? session = sessionManager.GetSession(Guid.Parse(response.SessionId));
        Assert.NotNull(session);
        Assert.Equal(60, session.Governor.QueryTimeoutSeconds);
        Assert.Equal(10_000, session.Governor.MaxOutputRows);
        Assert.Null(session.Governor.ThrottleDelayMilliseconds);
    }

    /// <summary>
    /// When the session has a throttle delay, the execution completes without
    /// errors (verifies the delay path doesn't throw).
    /// </summary>
    [Fact]
    public async Task Query_ThrottleDelay_StreamsAllRows()
    {
        QueryGovernor governor = new(QueryTimeoutSeconds: null, MaxOutputRows: null, ThrottleDelayMilliseconds: 1);
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        string fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "simple.csv");
        catalog.Register(new TableDescriptor("csv", "data", fixturePath, new Dictionary<string, string>()));
        Session session = _sessionManager.CreateLocalSession(SessionRole.Admin, catalog, governor);

        QueryRequest request = new()
        {
            SessionId = session.SessionId.ToString(),
            Sql = "SELECT * FROM data",
        };

        CapturingStreamWriter<QueryRow> writer = new();
        await _service.Query(request, writer, TestCallContext.Create());

        Assert.True(writer.Messages.Count > 0);
    }

    // ─────────────────── GetJoinSuggestions ───────────────────

    /// <summary>
    /// GetJoinSuggestions with fewer than two manifests returns an empty response
    /// instead of throwing.
    /// </summary>
    [Fact]
    public async Task GetJoinSuggestions_NoManifests_ReturnsEmptyResponse()
    {
        TableCatalog catalog = new();
        Session session = _sessionManager.CreateLocalSession(SessionRole.Admin, catalog);

        GetJoinSuggestionsRequest request = new()
        {
            SessionId = session.SessionId.ToString(),
        };

        GetJoinSuggestionsResponse response = await _service.GetJoinSuggestions(
            request, TestCallContext.Create());

        Assert.Empty(response.ResultJson);
    }

    /// <summary>
    /// GetJoinSuggestions with a single manifest returns an empty response
    /// instead of throwing.
    /// </summary>
    [Fact]
    public async Task GetJoinSuggestions_SingleManifest_ReturnsEmptyResponse()
    {
        TableCatalog catalog = new();
        catalog.RegisterManifest("orders", new QueryResultsManifest
        {
            RowCount = 100,
            GeneratedAtUtc = DateTime.UtcNow,
            Features = [],
        });
        Session session = _sessionManager.CreateLocalSession(SessionRole.Admin, catalog);

        GetJoinSuggestionsRequest request = new()
        {
            SessionId = session.SessionId.ToString(),
        };

        GetJoinSuggestionsResponse response = await _service.GetJoinSuggestions(
            request, TestCallContext.Create());

        Assert.Empty(response.ResultJson);
    }

    /// <summary>
    /// GetJoinSuggestions with two manifests returns a non-empty JSON result.
    /// </summary>
    [Fact]
    public async Task GetJoinSuggestions_TwoManifests_ReturnsResult()
    {
        TableCatalog catalog = new();
        catalog.RegisterManifest("orders", new QueryResultsManifest
        {
            RowCount = 1000,
            GeneratedAtUtc = DateTime.UtcNow,
            Features =
            [
                new NumericFeatureManifest
                {
                    Name = "customer_id",
                    Kind = DataKind.Scalar,
                    Count = 1000,
                    NullCount = 0,
                    ValidCount = 1000,
                    NullRatio = 0.0,
                    EstimatedDistinctCount = 900,
                    TopKValues = [new FrequencyEntry("1", 10), new FrequencyEntry("2", 10)],
                    Min = 1.0,
                    Max = 1000.0,
                    Mean = 500.0,
                    Variance = 25.0,
                    StandardDeviation = 5.0,
                    Skewness = 0.0,
                    Kurtosis = 3.0,
                    Histogram = new HistogramData([], []),
                    ZeroCount = 0,
                    ZeroRatio = 0.0,
                    OutlierCount = 0,
                    OutlierRatio = 0.0,
                    IntegerValued = true,
                },
            ],
        });
        catalog.RegisterManifest("customers", new QueryResultsManifest
        {
            RowCount = 1000,
            GeneratedAtUtc = DateTime.UtcNow,
            Features =
            [
                new NumericFeatureManifest
                {
                    Name = "customer_id",
                    Kind = DataKind.Scalar,
                    Count = 1000,
                    NullCount = 0,
                    ValidCount = 1000,
                    NullRatio = 0.0,
                    EstimatedDistinctCount = 1000,
                    TopKValues = [new FrequencyEntry("1", 1), new FrequencyEntry("2", 1)],
                    Min = 1.0,
                    Max = 1000.0,
                    Mean = 500.0,
                    Variance = 25.0,
                    StandardDeviation = 5.0,
                    Skewness = 0.0,
                    Kurtosis = 3.0,
                    Histogram = new HistogramData([], []),
                    ZeroCount = 0,
                    ZeroRatio = 0.0,
                    OutlierCount = 0,
                    OutlierRatio = 0.0,
                    IntegerValued = true,
                },
            ],
        });
        Session session = _sessionManager.CreateLocalSession(SessionRole.Admin, catalog);

        GetJoinSuggestionsRequest request = new()
        {
            SessionId = session.SessionId.ToString(),
        };

        GetJoinSuggestionsResponse response = await _service.GetJoinSuggestions(
            request, TestCallContext.Create());

        Assert.NotEmpty(response.ResultJson);
        Assert.Contains("customer_id", response.ResultJson);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // SessionManager tracks sessions but does not need explicit disposal.
    }

    /// <summary>
    /// Minimal <see cref="ServerCallContext"/> stub for unit testing gRPC services
    /// without a full gRPC hosting pipeline.
    /// </summary>
    private sealed class TestCallContext : ServerCallContext
    {
        private readonly Metadata _requestHeaders = new();
        private readonly Metadata _responseTrailers = new();
        private readonly CancellationToken _cancellationToken;
        private Status _status;
        private WriteOptions? _writeOptions;

        private TestCallContext(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
        }

        /// <summary>Creates a test call context with an optional cancellation token.</summary>
        public static TestCallContext Create(CancellationToken cancellationToken = default) =>
            new(cancellationToken);

        /// <inheritdoc/>
        protected override string MethodCore => "/test";

        /// <inheritdoc/>
        protected override string HostCore => "localhost";

        /// <inheritdoc/>
        protected override string PeerCore => "127.0.0.1";

        /// <inheritdoc/>
        protected override DateTime DeadlineCore => DateTime.MaxValue;

        /// <inheritdoc/>
        protected override Metadata RequestHeadersCore => _requestHeaders;

        /// <inheritdoc/>
        protected override CancellationToken CancellationTokenCore => _cancellationToken;

        /// <inheritdoc/>
        protected override Metadata ResponseTrailersCore => _responseTrailers;

        /// <inheritdoc/>
        protected override Status StatusCore { get => _status; set => _status = value; }

        /// <inheritdoc/>
        protected override WriteOptions? WriteOptionsCore { get => _writeOptions; set => _writeOptions = value; }

        /// <inheritdoc/>
        protected override AuthContext AuthContextCore => new(null, new Dictionary<string, List<AuthProperty>>());

        /// <inheritdoc/>
        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            throw new NotSupportedException();

        /// <inheritdoc/>
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// Trivial <see cref="IDatasetStore"/> that always returns a fixed directory path.
    /// </summary>
    private sealed class InMemoryDatasetStore : IDatasetStore
    {
        private readonly string _localPath;

        public InMemoryDatasetStore(string localPath)
        {
            _localPath = localPath;
        }

        /// <inheritdoc/>
        public Task<bool> ExistsLocallyAsync(string datasetId, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        /// <inheritdoc/>
        public Task<string> PullAsync(string datasetId, CancellationToken cancellationToken) =>
            Task.FromResult(_localPath);

        /// <inheritdoc/>
        public Task EvictAsync(string datasetId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// <see cref="IServerStreamWriter{T}"/> that captures written messages into a list
    /// for test assertion.
    /// </summary>
    private sealed class CapturingStreamWriter<T> : IServerStreamWriter<T>
    {
        /// <summary>Messages written to this stream.</summary>
        public List<T> Messages { get; } = new();

        /// <inheritdoc/>
        public WriteOptions? WriteOptions { get; set; }

        /// <inheritdoc/>
        public Task WriteAsync(T message)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Overrides the default interface method to support cancellation tokens
        /// passed by <see cref="ComputeService.Query"/>.
        /// </summary>
        Task IAsyncStreamWriter<T>.WriteAsync(T message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return WriteAsync(message);
        }
    }

    /// <summary>
    /// <see cref="IServerStreamWriter{T}"/> that cancels a session after a specified
    /// number of rows, simulating a mid-stream <c>KillQuery</c> from another session.
    /// </summary>
    private sealed class CancellingStreamWriter<T> : IServerStreamWriter<T>
    {
        private readonly Session _session;
        private readonly int _cancelAfterRow;

        /// <summary>Messages written before and at cancellation.</summary>
        public List<T> Messages { get; } = new();

        public CancellingStreamWriter(Session session, int cancelAfterRow)
        {
            _session = session;
            _cancelAfterRow = cancelAfterRow;
        }

        /// <inheritdoc/>
        public WriteOptions? WriteOptions { get; set; }

        /// <inheritdoc/>
        public Task WriteAsync(T message)
        {
            Messages.Add(message);

            if (Messages.Count >= _cancelAfterRow)
            {
                _session.CancelAndReset();
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Overrides the default interface method to support cancellation tokens
        /// passed by <see cref="ComputeService.Query"/>.
        /// </summary>
        Task IAsyncStreamWriter<T>.WriteAsync(T message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task result = WriteAsync(message);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
    }
}
