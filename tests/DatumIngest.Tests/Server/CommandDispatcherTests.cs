using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Functions;
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

    /// <summary>
    /// Sets up a dispatcher with admin and user sessions backed by a CSV catalog.
    /// </summary>
    public CommandDispatcherTests()
    {
        _sessionManager = new SessionManager(_functionRegistry);

        TableCatalog adminCatalog = new();
        adminCatalog.RegisterProvider("csv", () => new CsvTableProvider());
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
    /// .functions returns scalar and table-valued function names.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_Functions_ReturnsListResult()
    {
        CommandResult result = await _dispatcher.DispatchAsync(
            _adminSession, ".functions", CancellationToken.None);
        Assert.Equal(CommandResultKind.ListResult, result.Kind);
        Assert.True(result.Items!.Count > 0);
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

    /// <inheritdoc/>
    public void Dispose()
    {
        _adminSession.Dispose();
        _userSession.Dispose();
    }
}
