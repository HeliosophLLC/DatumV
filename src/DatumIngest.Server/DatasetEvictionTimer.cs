namespace DatumIngest.Server;

/// <summary>
/// Background timer that periodically checks for datasets with no active
/// sessions and evicts them from local storage after a configurable
/// cooldown period.
/// </summary>
public sealed class DatasetEvictionTimer : IAsyncDisposable
{
    private readonly SessionManager _sessionManager;
    private readonly IDatasetStore _datasetStore;
    private readonly TimeSpan _cooldownPeriod;
    private readonly Action<string>? _onEviction;
    private readonly Task _timerTask;
    private readonly CancellationTokenSource _stopSource = new();

    /// <summary>
    /// Initializes and starts the eviction timer.
    /// </summary>
    /// <param name="sessionManager">Session manager to query for active sessions and dataset access times.</param>
    /// <param name="datasetStore">Dataset store used to evict cached datasets.</param>
    /// <param name="cooldownPeriod">Minimum idle time before a dataset is eligible for eviction.</param>
    /// <param name="checkInterval">How often the timer checks for evictable datasets.</param>
    /// <param name="onEviction">Optional callback invoked for each evicted dataset identifier.</param>
    public DatasetEvictionTimer(
        SessionManager sessionManager,
        IDatasetStore datasetStore,
        TimeSpan cooldownPeriod,
        TimeSpan checkInterval,
        Action<string>? onEviction = null)
    {
        _sessionManager = sessionManager;
        _datasetStore = datasetStore;
        _cooldownPeriod = cooldownPeriod;
        _onEviction = onEviction;
        _timerTask = RunAsync(checkInterval, _stopSource.Token);
    }

    private async Task RunAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await EvictExpiredDatasetsAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — timer was disposed.
        }
    }

    private async Task EvictExpiredDatasetsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> evictable = _sessionManager.GetEvictableDatasets(_cooldownPeriod);

        foreach (string datasetId in evictable)
        {
            await _datasetStore.EvictAsync(datasetId, cancellationToken).ConfigureAwait(false);
            _sessionManager.ClearDatasetTracking(datasetId);
            _onEviction?.Invoke(datasetId);
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
