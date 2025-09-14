namespace DatumIngest.Server;

/// <summary>
/// Represents a single in-flight query within a <see cref="Session"/>.
/// Each active query has its own cancellation scope so that individual
/// queries can be cancelled without affecting other concurrent queries
/// on the same session.
/// </summary>
public sealed class ActiveQuery : IDisposable
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    /// <summary>
    /// Initializes a new active query with a server-assigned identifier.
    /// </summary>
    /// <param name="sql">The SQL text of the query being executed.</param>
    /// <param name="contextId">The identifier of the query context this query belongs to.</param>
    internal ActiveQuery(string sql, Guid contextId)
    {
        QueryId = Guid.NewGuid();
        Sql = sql;
        ContextId = contextId;
        StartedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Gets the server-assigned unique identifier for this query.</summary>
    public Guid QueryId { get; }

    /// <summary>Gets the SQL text of the query.</summary>
    public string Sql { get; }

    /// <summary>Gets the identifier of the query context this query executes on.</summary>
    public Guid ContextId { get; }

    /// <summary>Gets the timestamp when the query started.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>Gets a cancellation token scoped to this individual query.</summary>
    public CancellationToken CancellationToken => _cancellationTokenSource.Token;

    /// <summary>
    /// Cancels this query by signalling its cancellation token.
    /// </summary>
    public void Cancel()
    {
        _cancellationTokenSource.Cancel();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _cancellationTokenSource.Dispose();
    }
}
