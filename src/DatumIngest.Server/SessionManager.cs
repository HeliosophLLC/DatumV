using System.Collections.Concurrent;
using DatumIngest.Catalog;
using DatumIngest.Functions;

namespace DatumIngest.Server;

/// <summary>
/// Creates, tracks, and manages the lifecycle of <see cref="Session"/> instances.
/// Supports both remote sessions (backed by <see cref="IDatasetStore"/>) and
/// local sessions for the CLI shell.
/// </summary>
public sealed class SessionManager
{
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _datasetLastAccess = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Tracks sessions that are pending expiry, mapping session ID to the
    /// absolute deadline after which the session may be destroyed.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _pendingExpiry = new();

    private readonly FunctionRegistry _functionRegistry;
    private readonly IDatasetStore? _datasetStore;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new session manager.
    /// </summary>
    /// <param name="functionRegistry">Shared function registry for all sessions.</param>
    /// <param name="datasetStore">
    /// Optional dataset store for pulling remote datasets. Pass <see langword="null"/>
    /// for local/shell mode where catalogs are built directly.
    /// </param>
    /// <param name="timeProvider">
    /// Clock abstraction used to evaluate expiry deadlines. Defaults to
    /// <see cref="TimeProvider.System"/> when <see langword="null"/>.
    /// </param>
    public SessionManager(
        FunctionRegistry functionRegistry,
        IDatasetStore? datasetStore = null,
        TimeProvider? timeProvider = null)
    {
        _functionRegistry = functionRegistry;
        _datasetStore = datasetStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Creates a session backed by a remote dataset. The dataset is pulled
    /// to local storage via the <see cref="IDatasetStore"/> if not already cached.
    /// </summary>
    /// <param name="role">Authorization level for the new session.</param>
    /// <param name="datasetId">Unique identifier for the dataset to load.</param>
    /// <param name="catalogFactory">
    /// Factory that builds a <see cref="TableCatalog"/> from the local dataset path.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="governor">Resource governance limits, or <see langword="null"/> for unlimited.</param>
    /// <returns>The newly created session.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no dataset store is configured.</exception>
    public async Task<Session> CreateSessionAsync(
        SessionRole role,
        string datasetId,
        Func<string, Task<TableCatalog>> catalogFactory,
        CancellationToken cancellationToken,
        QueryGovernor? governor = null)
    {
        if (_datasetStore is null)
        {
            throw new InvalidOperationException(
                "Cannot create a dataset-backed session without an IDatasetStore. " +
                "Use CreateLocalSession for shell mode.");
        }

        string localPath = await _datasetStore.PullAsync(datasetId, cancellationToken).ConfigureAwait(false);
        TableCatalog catalog = await catalogFactory(localPath).ConfigureAwait(false);

        Session session = new(role, datasetId, catalog, _functionRegistry, governor);
        _sessions[session.SessionId] = session;
        _datasetLastAccess[datasetId] = _timeProvider.GetUtcNow();

        return session;
    }

    /// <summary>
    /// Creates a local session with a pre-built catalog. Used by the CLI
    /// shell where the catalog is constructed from command-line arguments.
    /// </summary>
    /// <param name="role">Authorization level for the new session.</param>
    /// <param name="catalog">Pre-built table catalog.</param>
    /// <param name="governor">Resource governance limits, or <see langword="null"/> for unlimited.</param>
    /// <returns>The newly created session.</returns>
    public Session CreateLocalSession(SessionRole role, TableCatalog catalog, QueryGovernor? governor = null)
    {
        Session session = new(role, datasetId: null, catalog, _functionRegistry, governor);
        _sessions[session.SessionId] = session;
        return session;
    }

    /// <summary>
    /// Retrieves a session by its unique identifier.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <returns>The session, or <see langword="null"/> if not found.</returns>
    public Session? GetSession(Guid sessionId)
    {
        _sessions.TryGetValue(sessionId, out Session? session);
        return session;
    }

    /// <summary>
    /// Returns all active sessions.
    /// </summary>
    /// <returns>A snapshot of all tracked sessions.</returns>
    public IReadOnlyList<Session> GetAllSessions()
    {
        return _sessions.Values.ToList();
    }

    /// <summary>
    /// Removes and disposes a session.
    /// </summary>
    /// <param name="sessionId">The session identifier to remove.</param>
    /// <returns><see langword="true"/> if the session was found and removed; otherwise <see langword="false"/>.</returns>
    public bool RemoveSession(Guid sessionId)
    {
        if (_sessions.TryRemove(sessionId, out Session? session))
        {
            session.Dispose();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Updates the last-access timestamp for a dataset. Called when any
    /// session using the dataset executes a command.
    /// </summary>
    /// <param name="datasetId">The dataset identifier.</param>
    internal void TouchDataset(string datasetId)
    {
        _datasetLastAccess[datasetId] = _timeProvider.GetUtcNow();
    }

    /// <summary>
    /// Returns dataset identifiers that have no active sessions and whose
    /// last access time is older than the specified cooldown period.
    /// </summary>
    /// <param name="cooldown">Minimum time since last access before a dataset is eligible for eviction.</param>
    /// <returns>Dataset identifiers eligible for eviction.</returns>
    internal IReadOnlyList<string> GetEvictableDatasets(TimeSpan cooldown)
    {
        DateTimeOffset cutoff = _timeProvider.GetUtcNow() - cooldown;

        // Build the set of datasets that still have active sessions.
        HashSet<string> activeDatasets = new(StringComparer.OrdinalIgnoreCase);
        foreach (Session session in _sessions.Values)
        {
            if (session.DatasetId is not null)
            {
                activeDatasets.Add(session.DatasetId);
            }
        }

        List<string> evictable = new();
        foreach (KeyValuePair<string, DateTimeOffset> entry in _datasetLastAccess)
        {
            if (!activeDatasets.Contains(entry.Key) && entry.Value < cutoff)
            {
                evictable.Add(entry.Key);
            }
        }

        return evictable;
    }

    /// <summary>
    /// Removes the last-access tracking entry for an evicted dataset.
    /// </summary>
    /// <param name="datasetId">The dataset identifier that was evicted.</param>
    internal void ClearDatasetTracking(string datasetId)
    {
        _datasetLastAccess.TryRemove(datasetId, out _);
    }

    /// <summary>
    /// Marks a session as pending expiry. If the session is not reclaimed before
    /// <paramref name="grace"/> has elapsed, <see cref="GetExpiredSessions"/> will
    /// return it and the background sweep can destroy it.
    /// Calling this again before the deadline resets the deadline.
    /// </summary>
    /// <param name="sessionId">The session to schedule for expiry.</param>
    /// <param name="grace">How long to wait before the session is eligible for destruction.</param>
    /// <exception cref="InvalidOperationException">Thrown when the session does not exist.</exception>
    public void BeginSessionExpiry(Guid sessionId, TimeSpan grace)
    {
        if (!_sessions.ContainsKey(sessionId))
        {
            throw new InvalidOperationException($"Session '{sessionId}' not found.");
        }

        _pendingExpiry[sessionId] = _timeProvider.GetUtcNow() + grace;
    }

    /// <summary>
    /// Cancels a pending expiry so the session is not destroyed by the sweep.
    /// </summary>
    /// <param name="sessionId">The session whose expiry should be cancelled.</param>
    /// <returns>
    /// <see langword="true"/> if the expiry was pending and has been cancelled;
    /// <see langword="false"/> if no expiry was scheduled.
    /// </returns>
    public bool CancelSessionExpiry(Guid sessionId)
    {
        return _pendingExpiry.TryRemove(sessionId, out _);
    }

    /// <summary>
    /// Returns all sessions whose expiry deadline has passed. The sessions remain
    /// in the active dictionary — the caller is responsible for removing them via
    /// <see cref="RemoveSession"/>.
    /// </summary>
    /// <returns>Sessions eligible for destruction.</returns>
    internal IReadOnlyList<Session> GetExpiredSessions()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        List<Session> expired = new();

        foreach (KeyValuePair<Guid, DateTimeOffset> entry in _pendingExpiry)
        {
            if (entry.Value <= now && _sessions.TryGetValue(entry.Key, out Session? session))
            {
                expired.Add(session);
            }
        }

        return expired;
    }
}
