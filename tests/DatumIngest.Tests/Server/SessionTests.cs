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

    // ─────────────────── Per-Query Tracking ───────────────────

    /// <summary>
    /// RegisterQuery creates an active query tracked by the session.
    /// </summary>
    [Fact]
    public void RegisterQuery_CreatesTrackedActiveQuery()
    {
        using Session session = _sessionManager.CreateLocalSession(SessionRole.Admin, new TableCatalog());

        ActiveQuery query = session.RegisterQuery("SELECT 1");

        Assert.NotEqual(Guid.Empty, query.QueryId);
        Assert.Equal("SELECT 1", query.Sql);
        Assert.Single(session.GetActiveQueries());
        Assert.Equal(query.QueryId, session.GetActiveQueries()[0].QueryId);
    }

    /// <summary>
    /// UnregisterQuery removes and disposes the active query.
    /// </summary>
    [Fact]
    public void UnregisterQuery_RemovesAndDisposesQuery()
    {
        using Session session = _sessionManager.CreateLocalSession(SessionRole.Admin, new TableCatalog());
        ActiveQuery query = session.RegisterQuery("SELECT 1");
        CancellationToken token = query.CancellationToken;

        session.UnregisterQuery(query.QueryId);

        Assert.Empty(session.GetActiveQueries());
        Assert.Throws<ObjectDisposedException>(() => token.WaitHandle);
    }

    /// <summary>
    /// UnregisterQuery with unknown ID is a safe no-op.
    /// </summary>
    [Fact]
    public void UnregisterQuery_UnknownId_NoOp()
    {
        using Session session = _sessionManager.CreateLocalSession(SessionRole.Admin, new TableCatalog());

        session.UnregisterQuery(Guid.NewGuid());

        Assert.Empty(session.GetActiveQueries());
    }

    /// <summary>
    /// CancelQuery cancels only the targeted query, leaving others unaffected.
    /// </summary>
    [Fact]
    public void CancelQuery_CancelsOnlyTargetedQuery()
    {
        using Session session = _sessionManager.CreateLocalSession(SessionRole.Admin, new TableCatalog());
        ActiveQuery first = session.RegisterQuery("SELECT 1");
        ActiveQuery second = session.RegisterQuery("SELECT 2");

        bool found = session.CancelQuery(first.QueryId);

        Assert.True(found);
        Assert.True(first.CancellationToken.IsCancellationRequested);
        Assert.False(second.CancellationToken.IsCancellationRequested);

        session.UnregisterQuery(first.QueryId);
        session.UnregisterQuery(second.QueryId);
    }

    /// <summary>
    /// CancelQuery with unknown ID returns false.
    /// </summary>
    [Fact]
    public void CancelQuery_UnknownId_ReturnsFalse()
    {
        using Session session = _sessionManager.CreateLocalSession(SessionRole.Admin, new TableCatalog());

        Assert.False(session.CancelQuery(Guid.NewGuid()));
    }

    /// <summary>
    /// CancelAllAndReset cancels all active queries and resets the session token.
    /// </summary>
    [Fact]
    public void CancelAllAndReset_CancelsAllActiveQueries()
    {
        using Session session = _sessionManager.CreateLocalSession(SessionRole.Admin, new TableCatalog());
        ActiveQuery first = session.RegisterQuery("SELECT 1");
        ActiveQuery second = session.RegisterQuery("SELECT 2");
        CancellationToken sessionToken = session.CancellationToken;

        int count = session.CancelAllAndReset();

        Assert.Equal(2, count);
        Assert.True(first.CancellationToken.IsCancellationRequested);
        Assert.True(second.CancellationToken.IsCancellationRequested);
        Assert.True(sessionToken.IsCancellationRequested);
        Assert.False(session.CancellationToken.IsCancellationRequested);

        session.UnregisterQuery(first.QueryId);
        session.UnregisterQuery(second.QueryId);
    }

    /// <summary>
    /// GetActiveQueries returns a snapshot that is not affected by subsequent changes.
    /// </summary>
    [Fact]
    public void GetActiveQueries_ReturnsSnapshot()
    {
        using Session session = _sessionManager.CreateLocalSession(SessionRole.Admin, new TableCatalog());
        ActiveQuery query = session.RegisterQuery("SELECT 1");

        IReadOnlyList<ActiveQuery> snapshot = session.GetActiveQueries();

        session.UnregisterQuery(query.QueryId);

        Assert.Single(snapshot);
        Assert.Empty(session.GetActiveQueries());
    }

    /// <summary>
    /// Multiple concurrent register/unregister calls do not corrupt session state.
    /// </summary>
    [Fact]
    public void RegisterAndUnregister_ConcurrentAccess_IsThreadSafe()
    {
        using Session session = _sessionManager.CreateLocalSession(SessionRole.Admin, new TableCatalog());

        Parallel.For(0, 100, _ =>
        {
            ActiveQuery query = session.RegisterQuery("SELECT 1");
            session.UnregisterQuery(query.QueryId);
        });

        Assert.Empty(session.GetActiveQueries());
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
