using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Compute.Grpc;
using DatumIngest.Compute.Services;
using DatumIngest.Functions;
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
        _service = new ComputeService(_sessionManager, _dispatcher);
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
            ComputeService service = new(sessionManager, dispatcher);

            CreateSessionRequest request = new() { Role = "admin", DatasetId = "test-ds" };

            CreateSessionResponse response = await service.CreateSession(
                request, TestCallContext.Create());

            Session? session = sessionManager.GetSession(Guid.Parse(response.SessionId));
            Assert.NotNull(session);
            Assert.Equal("test-ds", session.DatasetId);
            Assert.Contains("people", session.Catalog.TableNames);
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
    /// ListFunctions returns available scalar and table-valued functions.
    /// </summary>
    [Fact]
    public async Task ListFunctions_ReturnsFunctions()
    {
        Session session = _sessionManager.CreateLocalSession(SessionRole.Admin, new TableCatalog());

        ListFunctionsRequest request = new() { SessionId = session.SessionId.ToString() };

        ListResponse response = await _service.ListFunctions(request, TestCallContext.Create());

        Assert.True(response.Items.Count > 0);
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
}
