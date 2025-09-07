using DatumIngest.Catalog;
using DatumIngest.Functions;
using DatumIngest.Server;
using Microsoft.Extensions.Time.Testing;

namespace DatumIngest.Tests.Server;

/// <summary>
/// Tests for the session expiry grace period: <see cref="SessionManager.BeginSessionExpiry"/>,
/// <see cref="SessionManager.CancelSessionExpiry"/>, and <see cref="SessionManager.GetExpiredSessions"/>.
/// <see cref="SessionManager.BeginSessionExpiry"/> is called internally by the <c>DestroySession</c> RPC;
/// <see cref="SessionManager.CancelSessionExpiry"/> is called internally by the <c>CreateSession</c> RPC
/// when <c>reconnect_session_id</c> is provided.
/// </summary>
public sealed class SessionExpiryTests
{
    private readonly FunctionRegistry _functionRegistry = FunctionRegistry.CreateDefault();

    private SessionManager CreateManager(FakeTimeProvider clock) =>
        new(_functionRegistry, datasetStore: null, timeProvider: clock);

    /// <summary>
    /// A session with a pending expiry is still accessible before the deadline passes.
    /// </summary>
    [Fact]
    public void BeginExpiry_SessionStillAccessible_BeforeDeadline()
    {
        FakeTimeProvider clock = new();
        SessionManager manager = CreateManager(clock);
        Session session = manager.CreateLocalSession(SessionRole.User, new TableCatalog());

        manager.BeginSessionExpiry(session.SessionId, TimeSpan.FromSeconds(30));

        // Advance 29 seconds — not yet expired.
        clock.Advance(TimeSpan.FromSeconds(29));

        Assert.Empty(manager.GetExpiredSessions());
        Assert.NotNull(manager.GetSession(session.SessionId));

        session.Dispose();
    }

    /// <summary>
    /// A session is returned by GetExpiredSessions once the grace period elapses.
    /// </summary>
    [Fact]
    public void BeginExpiry_SessionReturnedByGetExpiredSessions_AfterDeadline()
    {
        FakeTimeProvider clock = new();
        SessionManager manager = CreateManager(clock);
        Session session = manager.CreateLocalSession(SessionRole.User, new TableCatalog());

        manager.BeginSessionExpiry(session.SessionId, TimeSpan.FromSeconds(30));

        // Advance 31 seconds — deadline has passed.
        clock.Advance(TimeSpan.FromSeconds(31));

        IReadOnlyList<Session> expired = manager.GetExpiredSessions();
        Assert.Single(expired);
        Assert.Equal(session.SessionId, expired[0].SessionId);

        // Simulate what the sweep timer does.
        manager.CancelSessionExpiry(session.SessionId);
        manager.RemoveSession(session.SessionId);

        Assert.Null(manager.GetSession(session.SessionId));
    }

    /// <summary>
    /// CancelSessionExpiry prevents the session from appearing in GetExpiredSessions.
    /// </summary>
    [Fact]
    public void CancelExpiry_SessionSurvivesSweep()
    {
        FakeTimeProvider clock = new();
        SessionManager manager = CreateManager(clock);
        Session session = manager.CreateLocalSession(SessionRole.User, new TableCatalog());

        manager.BeginSessionExpiry(session.SessionId, TimeSpan.FromSeconds(30));
        bool wasPending = manager.CancelSessionExpiry(session.SessionId);

        Assert.True(wasPending);

        // Advance past the original deadline.
        clock.Advance(TimeSpan.FromSeconds(31));

        // Session should not appear as expired because the expiry was cancelled.
        Assert.Empty(manager.GetExpiredSessions());
        Assert.NotNull(manager.GetSession(session.SessionId));

        session.Dispose();
    }

    /// <summary>
    /// Calling BeginSessionExpiry a second time resets the deadline. A sweep that
    /// runs between the first and second deadline must not destroy the session.
    /// </summary>
    [Fact]
    public void BeginExpiry_CalledTwice_ResetsDeadline()
    {
        FakeTimeProvider clock = new();
        SessionManager manager = CreateManager(clock);
        Session session = manager.CreateLocalSession(SessionRole.User, new TableCatalog());

        // First call: deadline at T+30s.
        manager.BeginSessionExpiry(session.SessionId, TimeSpan.FromSeconds(30));

        // Advance 10 seconds, then reset the deadline to T+10s+30s = T+40s.
        clock.Advance(TimeSpan.FromSeconds(10));
        manager.BeginSessionExpiry(session.SessionId, TimeSpan.FromSeconds(30));

        // Advance to T+35s — past the first deadline but before the second.
        clock.Advance(TimeSpan.FromSeconds(25));

        Assert.Empty(manager.GetExpiredSessions());
        Assert.NotNull(manager.GetSession(session.SessionId));

        session.Dispose();
    }

    /// <summary>
    /// BeginSessionExpiry throws for a session ID that does not exist.
    /// </summary>
    [Fact]
    public void BeginExpiry_UnknownSession_Throws()
    {
        FakeTimeProvider clock = new();
        SessionManager manager = CreateManager(clock);

        Assert.Throws<InvalidOperationException>(() =>
            manager.BeginSessionExpiry(Guid.NewGuid(), TimeSpan.FromSeconds(30)));
    }

    /// <summary>
    /// CancelSessionExpiry returns false when no expiry was scheduled.
    /// </summary>
    [Fact]
    public void CancelExpiry_NoPendingExpiry_ReturnsFalse()
    {
        FakeTimeProvider clock = new();
        SessionManager manager = CreateManager(clock);
        Session session = manager.CreateLocalSession(SessionRole.User, new TableCatalog());

        bool wasPending = manager.CancelSessionExpiry(session.SessionId);

        Assert.False(wasPending);

        session.Dispose();
    }

    /// <summary>
    /// Simulates the broker disconnect/reconnect flow: the <c>DestroySession</c> RPC calls
    /// <see cref="SessionManager.BeginSessionExpiry"/> on disconnect; the <c>CreateSession</c> RPC
    /// calls <see cref="SessionManager.CancelSessionExpiry"/> on reconnect.
    /// As long as the reconnect happens within the grace period the session survives intact.
    /// </summary>
    [Fact]
    public void DisconnectReconnect_WithinGrace_SessionSurvives()
    {
        FakeTimeProvider clock = new();
        SessionManager manager = CreateManager(clock);
        Session session = manager.CreateLocalSession(SessionRole.User, new TableCatalog());
        Guid id = session.SessionId;

        // DestroySession RPC — starts the 30-second grace period.
        manager.BeginSessionExpiry(id, TimeSpan.FromSeconds(30));

        // Client reconnects at T+15s — within the grace window.
        clock.Advance(TimeSpan.FromSeconds(15));

        // CreateSession RPC with reconnect_session_id — cancels the expiry.
        bool wasPending = manager.CancelSessionExpiry(id);

        Assert.True(wasPending);
        Assert.Empty(manager.GetExpiredSessions());
        Assert.NotNull(manager.GetSession(id));

        session.Dispose();
    }
}
