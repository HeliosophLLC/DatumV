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
    private readonly FunctionRegistry _functionRegistry;
    private readonly IDatasetStore? _datasetStore;

    /// <summary>
    /// Initializes a new session manager.
    /// </summary>
    /// <param name="functionRegistry">Shared function registry for all sessions.</param>
    /// <param name="datasetStore">
    /// Optional dataset store for pulling remote datasets. Pass <see langword="null"/>
    /// for local/shell mode where catalogs are built directly.
    /// </param>
    public SessionManager(FunctionRegistry functionRegistry, IDatasetStore? datasetStore = null)
    {
        _functionRegistry = functionRegistry;
        _datasetStore = datasetStore;
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
    /// <returns>The newly created session.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no dataset store is configured.</exception>
    public async Task<Session> CreateSessionAsync(
        SessionRole role,
        string datasetId,
        Func<string, TableCatalog> catalogFactory,
        CancellationToken cancellationToken)
    {
        if (_datasetStore is null)
        {
            throw new InvalidOperationException(
                "Cannot create a dataset-backed session without an IDatasetStore. " +
                "Use CreateLocalSession for shell mode.");
        }

        string localPath = await _datasetStore.PullAsync(datasetId, cancellationToken).ConfigureAwait(false);
        TableCatalog catalog = catalogFactory(localPath);

        Session session = new(role, datasetId, catalog, _functionRegistry);
        _sessions[session.SessionId] = session;
        _datasetLastAccess[datasetId] = DateTimeOffset.UtcNow;

        return session;
    }

    /// <summary>
    /// Creates a local session with a pre-built catalog. Used by the CLI
    /// shell where the catalog is constructed from command-line arguments.
    /// </summary>
    /// <param name="role">Authorization level for the new session.</param>
    /// <param name="catalog">Pre-built table catalog.</param>
    /// <returns>The newly created session.</returns>
    public Session CreateLocalSession(SessionRole role, TableCatalog catalog)
    {
        Session session = new(role, datasetId: null, catalog, _functionRegistry);
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
        _datasetLastAccess[datasetId] = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Returns dataset identifiers that have no active sessions and whose
    /// last access time is older than the specified cooldown period.
    /// </summary>
    /// <param name="cooldown">Minimum time since last access before a dataset is eligible for eviction.</param>
    /// <returns>Dataset identifiers eligible for eviction.</returns>
    internal IReadOnlyList<string> GetEvictableDatasets(TimeSpan cooldown)
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - cooldown;

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
}
