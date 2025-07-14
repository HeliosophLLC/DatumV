using DatumIngest.Catalog;
using DatumIngest.Functions;

namespace DatumIngest.Server;

/// <summary>
/// Represents an authenticated connection to the server engine. Each
/// session has its own <see cref="TableCatalog"/> for tenant isolation,
/// a shared <see cref="FunctionRegistry"/>, and a per-session
/// cancellation scope for query termination.
/// </summary>
public sealed class Session : IDisposable
{
    private CancellationTokenSource _cancellationTokenSource = new();
    private readonly List<string> _queryHistory = new();
    private readonly object _lock = new();

    /// <summary>
    /// Initializes a new session with the specified role, dataset, catalog, and function registry.
    /// </summary>
    /// <param name="role">Authorization level for this session.</param>
    /// <param name="datasetId">Identifier of the dataset this session is serving, or <see langword="null"/> for local/shell sessions.</param>
    /// <param name="catalog">Isolated table catalog for this session's data.</param>
    /// <param name="functionRegistry">Shared function registry (immutable, safe to share across sessions).</param>
    internal Session(SessionRole role, string? datasetId, TableCatalog catalog, FunctionRegistry functionRegistry)
    {
        SessionId = Guid.NewGuid();
        Role = role;
        DatasetId = datasetId;
        Catalog = catalog;
        FunctionRegistry = functionRegistry;
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

    /// <summary>Gets the timestamp when this session was created.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Gets the timestamp of the last command executed in this session.</summary>
    public DateTimeOffset LastActivityAt { get; private set; }

    /// <summary>Gets a cancellation token tied to this session's lifecycle.</summary>
    public CancellationToken CancellationToken => _cancellationTokenSource.Token;

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
    /// Cancels the current operation and resets the cancellation scope
    /// so the session can be reused for subsequent commands.
    /// </summary>
    public void CancelAndReset()
    {
        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();
        _cancellationTokenSource = new CancellationTokenSource();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _cancellationTokenSource.Dispose();
    }
}
