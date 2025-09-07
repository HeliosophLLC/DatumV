namespace DatumIngest.Server;

/// <summary>
/// Background timer that periodically checks for sessions whose expiry grace
/// period has elapsed and destroys them. Sessions are only eligible once
/// <see cref="SessionManager.BeginSessionExpiry"/> has been called and the
/// configured deadline has passed without a corresponding
/// <see cref="SessionManager.CancelSessionExpiry"/> call.
/// </summary>
public sealed class SessionExpiryTimer : IAsyncDisposable
{
    private readonly SessionManager _sessionManager;
    private readonly Action<Guid>? _onExpiry;
    private readonly Task _timerTask;
    private readonly CancellationTokenSource _stopSource = new();

    /// <summary>
    /// Initializes and starts the expiry sweep timer.
    /// </summary>
    /// <param name="sessionManager">Session manager to query for expired sessions.</param>
    /// <param name="checkInterval">How often the timer scans for expired sessions.</param>
    /// <param name="onExpiry">Optional callback invoked with the session ID each time a session is destroyed.</param>
    public SessionExpiryTimer(
        SessionManager sessionManager,
        TimeSpan checkInterval,
        Action<Guid>? onExpiry = null)
    {
        _sessionManager = sessionManager;
        _onExpiry = onExpiry;
        _timerTask = RunAsync(checkInterval, _stopSource.Token);
    }

    private async Task RunAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                SweepExpiredSessions();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — timer was disposed.
        }
    }

    private void SweepExpiredSessions()
    {
        IReadOnlyList<Session> expired = _sessionManager.GetExpiredSessions();

        foreach (Session session in expired)
        {
            // Cancel the expiry tracking entry before removing so that if
            // RemoveSession is called concurrently, the sweep won't double-fire.
            _sessionManager.CancelSessionExpiry(session.SessionId);
            _sessionManager.RemoveSession(session.SessionId);
            _onExpiry?.Invoke(session.SessionId);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _stopSource.CancelAsync().ConfigureAwait(false);

        try
        {
            await _timerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }

        _stopSource.Dispose();
    }
}
