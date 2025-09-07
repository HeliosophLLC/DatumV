using DatumIngest.Server;
using Microsoft.Extensions.Hosting;

namespace DatumIngest.Compute;

/// <summary>
/// ASP.NET Core hosted service adapter that wraps <see cref="SessionExpiryTimer"/>
/// so it participates in the application lifetime. Register via
/// <see cref="DatumComputeServiceExtensions.AddSessionExpiryTimer"/>.
/// </summary>
internal sealed class SessionExpiryTimerHostedService : IHostedService, IAsyncDisposable
{
    private readonly SessionManager _sessionManager;
    private readonly TimeSpan _checkInterval;
    private SessionExpiryTimer? _timer;

    /// <summary>
    /// Initializes the hosted service with the dependencies needed to construct
    /// <see cref="SessionExpiryTimer"/> on startup.
    /// </summary>
    /// <param name="sessionManager">Session manager used by the sweep timer.</param>
    /// <param name="checkInterval">How often expired sessions are swept.</param>
    internal SessionExpiryTimerHostedService(SessionManager sessionManager, TimeSpan checkInterval)
    {
        _sessionManager = sessionManager;
        _checkInterval = checkInterval;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new SessionExpiryTimer(_sessionManager, _checkInterval);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_timer is not null)
        {
            await _timer.DisposeAsync().ConfigureAwait(false);
            _timer = null;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_timer is not null)
        {
            await _timer.DisposeAsync().ConfigureAwait(false);
            _timer = null;
        }
    }
}
