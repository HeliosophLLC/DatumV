using System.Collections.Concurrent;
using DatumIngest.Catalog;
using DatumIngest.Functions;

namespace DatumIngest.Server;

/// <summary>
/// Represents an authenticated connection to the server engine. Each
/// session has its own <see cref="TableCatalog"/> for tenant isolation,
/// a shared <see cref="FunctionRegistry"/>, and per-query cancellation
/// scopes that allow multiple concurrent queries to be cancelled
/// independently.
/// </summary>
public sealed class Session : IDisposable
{
    private CancellationTokenSource _sessionCancellationTokenSource = new();
    private readonly ConcurrentDictionary<Guid, ActiveQuery> _activeQueries = new();
    private readonly ConcurrentDictionary<Guid, QueryContext> _queryContexts = new();
    private readonly List<string> _queryHistory = new();
    private readonly object _lock = new();
    private long _totalQueryUnits;

    /// <summary>
    /// Initializes a new session with the specified role, dataset, catalog, function registry,
    /// and resource governance limits.
    /// </summary>
    /// <param name="role">Authorization level for this session.</param>
    /// <param name="datasetId">Identifier of the dataset this session is serving, or <see langword="null"/> for local/shell sessions.</param>
    /// <param name="catalog">Isolated table catalog for this session's data.</param>
    /// <param name="functionRegistry">Shared function registry (immutable, safe to share across sessions).</param>
    /// <param name="governor">Resource governance limits for this session, or <see langword="null"/> for unlimited.</param>
    internal Session(SessionRole role, string? datasetId, TableCatalog catalog, FunctionRegistry functionRegistry, QueryGovernor? governor = null)
    {
        SessionId = Guid.NewGuid();
        Role = role;
        DatasetId = datasetId;
        Catalog = catalog;
        FunctionRegistry = functionRegistry;
        Governor = governor ?? QueryGovernor.Unlimited;
        CreatedAt = DateTimeOffset.UtcNow;
        LastActivityAt = CreatedAt;
    }

    /// <summary>Gets the unique identifier for this session.</summary>
    public Guid SessionId { get; }

    /// <summary>Gets the authorization role of this session.</summary>
    public SessionRole Role { get; }

    /// <summary>Gets the dataset identifier this session is serving, or <see langword="null"/> for local sessions.</summary>
    public string? DatasetId { get; }

    /// <summary>Gets the isolated table catalog for this session.</summary>
    public TableCatalog Catalog { get; }

    /// <summary>Gets the shared function registry.</summary>
    public FunctionRegistry FunctionRegistry { get; }

    /// <summary>Gets the resource governance limits for this session.</summary>
    public QueryGovernor Governor { get; }

    /// <summary>
    /// Gets the total Query Units accumulated across all queries in this session.
    /// </summary>
    public long TotalQueryUnits => Interlocked.Read(ref _totalQueryUnits);

    /// <summary>Gets the timestamp when this session was created.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Gets the timestamp of the last command executed in this session.</summary>
    public DateTimeOffset LastActivityAt { get; private set; }

    /// <summary>Gets a cancellation token tied to this session's lifecycle.
    /// This token is signalled when the session is destroyed or when
    /// <see cref="CancelAllAndReset"/> is called. Individual query tokens
    /// are linked to this token so that session teardown cancels everything.</summary>
    public CancellationToken CancellationToken => _sessionCancellationTokenSource.Token;

    /// <summary>Gets the history of queries executed in this session.</summary>
    public IReadOnlyList<string> QueryHistory
    {
        get
        {
            lock (_lock)
            {
                return _queryHistory.ToList();
            }
        }
    }

    /// <summary>
    /// Checks whether this session is authorized to perform the given operation.
    /// </summary>
    /// <param name="operation">The operation to check.</param>
    /// <returns><see langword="true"/> if authorized; otherwise <see langword="false"/>.</returns>
    public bool IsAuthorized(ServerOperation operation)
    {
        return ServerCapability.IsAuthorized(Role, operation);
    }

    /// <summary>
    /// Updates the last activity timestamp to the current time.
    /// </summary>
    public void TouchActivity()
    {
        LastActivityAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Records a query in the session history.
    /// </summary>
    /// <param name="query">The query text to record.</param>
    public void RecordQuery(string query)
    {
        lock (_lock)
        {
            _queryHistory.Add(query);
        }
    }

    /// <summary>
    /// Adds the specified Query Units to the session's cumulative total.
    /// Thread-safe.
    /// </summary>
    /// <param name="units">The number of Query Units to add.</param>
    public void AddQueryUnits(long units)
    {
        Interlocked.Add(ref _totalQueryUnits, units);
    }

    /// <summary>
    /// Registers a new active query and returns it. The caller must call
    /// <see cref="UnregisterQuery"/> in a <c>finally</c> block when the
    /// query completes or faults.
    /// </summary>
    /// <param name="sql">The SQL text of the query being executed.</param>
    /// <param name="contextId">The identifier of the query context executing the query.</param>
    /// <returns>The newly registered <see cref="ActiveQuery"/> with a server-assigned identifier.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the session has reached its <see cref="QueryGovernor.MaxConcurrentQueries"/> limit.
    /// </exception>
    public ActiveQuery RegisterQuery(string sql, Guid contextId)
    {
        if (Governor.MaxConcurrentQueries.HasValue &&
            _activeQueries.Count >= Governor.MaxConcurrentQueries.Value)
        {
            throw new InvalidOperationException(
                $"Concurrent query limit reached (limit: {Governor.MaxConcurrentQueries.Value}). " +
                "Cancel or wait for an active query to complete before starting a new one.");
        }

        ActiveQuery activeQuery = new(sql, contextId);
        _activeQueries.TryAdd(activeQuery.QueryId, activeQuery);
        return activeQuery;
    }

    /// <summary>
    /// Removes and disposes an active query after it has completed or faulted.
    /// </summary>
    /// <param name="queryId">The identifier of the query to unregister.</param>
    public void UnregisterQuery(Guid queryId)
    {
        if (_activeQueries.TryRemove(queryId, out ActiveQuery? activeQuery))
        {
            activeQuery.Dispose();
        }
    }

    /// <summary>
    /// Cancels a specific active query by its identifier.
    /// </summary>
    /// <param name="queryId">The identifier of the query to cancel.</param>
    /// <returns><see langword="true"/> if the query was found and cancelled; <see langword="false"/> if no such query is active.</returns>
    public bool CancelQuery(Guid queryId)
    {
        if (_activeQueries.TryGetValue(queryId, out ActiveQuery? activeQuery))
        {
            activeQuery.Cancel();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Cancels all active queries belonging to a specific query context.
    /// </summary>
    /// <param name="contextId">The identifier of the query context whose queries should be cancelled.</param>
    /// <returns>The number of queries that were cancelled.</returns>
    public int CancelQueriesByContext(Guid contextId)
    {
        int count = 0;

        foreach (ActiveQuery activeQuery in _activeQueries.Values)
        {
            if (activeQuery.ContextId == contextId)
            {
                activeQuery.Cancel();
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Cancels all active queries and resets the session-level cancellation
    /// scope so the session can be reused for subsequent commands.
    /// </summary>
    /// <returns>The number of active queries that were cancelled.</returns>
    public int CancelAllAndReset()
    {
        int count = 0;

        foreach (ActiveQuery activeQuery in _activeQueries.Values)
        {
            activeQuery.Cancel();
            count++;
        }

        _sessionCancellationTokenSource.Cancel();
        _sessionCancellationTokenSource.Dispose();
        _sessionCancellationTokenSource = new CancellationTokenSource();

        return count;
    }

    /// <summary>
    /// Returns a snapshot of all currently active queries in this session.
    /// </summary>
    /// <returns>A read-only list of active queries.</returns>
    public IReadOnlyList<ActiveQuery> GetActiveQueries()
    {
        return _activeQueries.Values.ToList();
    }

    // ──────────────────── Query Contexts ────────────────────

    /// <summary>
    /// Creates a new query context with its own isolated temp table namespace.
    /// </summary>
    /// <param name="label">Human-readable label for debugging (e.g. "Tab 1", "SQL Assistant").</param>
    /// <returns>The newly created query context.</returns>
    public QueryContext CreateQueryContext(string label)
    {
        QueryContext queryContext = new(SessionId, Catalog, label);
        _queryContexts.TryAdd(queryContext.ContextId, queryContext);
        return queryContext;
    }

    /// <summary>
    /// Resolves a query context by its identifier.
    /// </summary>
    /// <param name="contextId">The context identifier.</param>
    /// <returns>The matching query context, or <see langword="null"/> if not found.</returns>
    public QueryContext? GetQueryContext(Guid contextId)
    {
        _queryContexts.TryGetValue(contextId, out QueryContext? queryContext);
        return queryContext;
    }

    /// <summary>
    /// Destroys a query context, dropping all its temp tables and cleaning up
    /// associated storage. Active queries on the context are not automatically
    /// cancelled — the caller must cancel them first if needed.
    /// </summary>
    /// <param name="contextId">The context identifier.</param>
    /// <returns><see langword="true"/> if the context was found and destroyed; otherwise <see langword="false"/>.</returns>
    public bool DestroyQueryContext(Guid contextId)
    {
        if (_queryContexts.TryRemove(contextId, out QueryContext? queryContext))
        {
            queryContext.Dispose();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns a snapshot of all query contexts in this session.
    /// </summary>
    /// <returns>A read-only list of query contexts.</returns>
    public IReadOnlyList<QueryContext> GetQueryContexts()
    {
        return _queryContexts.Values.ToList();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (ActiveQuery activeQuery in _activeQueries.Values)
        {
            activeQuery.Dispose();
        }

        _activeQueries.Clear();

        foreach (QueryContext queryContext in _queryContexts.Values)
        {
            queryContext.Dispose();
        }

        _queryContexts.Clear();

        _sessionCancellationTokenSource.Dispose();
        Catalog.Dispose();
    }
}
