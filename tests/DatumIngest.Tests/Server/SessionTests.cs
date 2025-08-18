using DatumIngest.Catalog;
using DatumIngest.Functions;
using DatumIngest.Server;

namespace DatumIngest.Tests.Server;

/// <summary>
/// Tests for <see cref="Session"/> lifecycle, authorization, and state tracking.
/// </summary>
public sealed class SessionTests : IDisposable
{
    private readonly FunctionRegistry _functionRegistry = FunctionRegistry.CreateDefault();
    private readonly SessionManager _sessionManager;

    /// <summary>
    /// Initializes test infrastructure with a local-mode session manager.
    /// </summary>
    public SessionTests()
    {
        _sessionManager = new SessionManager(_functionRegistry);
    }

    /// <summary>
    /// Admin sessions are authorized for all operations.
    /// </summary>
    [Fact]
    public void IsAuthorized_AdminSession_AllOperationsAllowed()
    {
        using Session session = _sessionManager.CreateLocalSession(SessionRole.Admin, new TableCatalog());
        Assert.True(session.IsAuthorized(ServerOperation.Query));
        Assert.True(session.IsAuthorized(ServerOperation.AddSource));
        Assert.True(session.IsAuthorized(ServerOperation.KillQuery));
    }

    /// <summary>
    /// User sessions are denied administrative operations.
    /// </summary>
    [Fact]
    public void IsAuthorized_UserSession_AdminOperationsDenied()
    {
        using Session session = _sessionManager.CreateLocalSession(SessionRole.User, new TableCatalog());
        Assert.True(session.IsAuthorized(ServerOperation.Query));
        Assert.False(session.IsAuthorized(ServerOperation.AddSource));
        Assert.False(session.IsAuthorized(ServerOperation.KillQuery));
    }

    /// <summary>
    /// TouchActivity updates the last activity timestamp.
    /// </summary>
    [Fact]
    public void TouchActivity_UpdatesLastActivityTimestamp()
    {
        using Session session = _sessionManager.CreateLocalSession(SessionRole.Admin, new TableCatalog());
        DateTimeOffset before = session.LastActivityAt;
        Thread.Sleep(10);
        session.TouchActivity();
        Assert.True(session.LastActivityAt > before);
    }

    /// <summary>
    /// RecordQuery appends to the thread-safe query history.
    /// </summary>
    [Fact]
    public void RecordQuery_AddsToHistory()
    {
        using Session session = _sessionManager.CreateLocalSession(SessionRole.Admin, new TableCatalog());
        session.RecordQuery("SELECT 1");
        session.RecordQuery("SELECT 2");

        Assert.Equal(2, session.QueryHistory.Count);
        Assert.Equal("SELECT 1", session.QueryHistory[0]);
        Assert.Equal("SELECT 2", session.QueryHistory[1]);
    }

    /// <summary>
    /// CancelAllAndReset cancels the current token and provides a new one.
    /// </summary>
    [Fact]
    public void CancelAllAndReset_CancelsCurrentTokenAndResetsScope()
    {
        using Session session = _sessionManager.CreateLocalSession(SessionRole.Admin, new TableCatalog());
        CancellationToken firstToken = session.CancellationToken;
        Assert.False(firstToken.IsCancellationRequested);

        session.CancelAllAndReset();

        Assert.True(firstToken.IsCancellationRequested);
        Assert.False(session.CancellationToken.IsCancellationRequested);
    }

    /// <summary>
    /// Local sessions have a null DatasetId.
    /// </summary>
    [Fact]
    public void CreateLocalSession_HasNullDatasetId()
    {
        using Session session = _sessionManager.CreateLocalSession(SessionRole.Admin, new TableCatalog());
        Assert.Null(session.DatasetId);
    }

    /// <summary>
    /// Each session gets a unique identifier.
    /// </summary>
    [Fact]
    public void SessionId_IsUnique()
    {
        using Session session1 = _sessionManager.CreateLocalSession(SessionRole.Admin, new TableCatalog());
        using Session session2 = _sessionManager.CreateLocalSession(SessionRole.Admin, new TableCatalog());
        Assert.NotEqual(session1.SessionId, session2.SessionId);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // SessionManager does not need disposal.
    }
}
