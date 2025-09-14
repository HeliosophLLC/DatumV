using DatumIngest.Server;

namespace DatumIngest.Tests.Server;

/// <summary>
/// Tests for <see cref="ActiveQuery"/> lifecycle, cancellation, and disposal.
/// </summary>
public sealed class ActiveQueryTests
{
    /// <summary>
    /// A newly created active query has a unique identifier and is not cancelled.
    /// </summary>
    [Fact]
    public void Constructor_AssignsUniqueIdAndNonCancelledToken()
    {
        Guid contextId = Guid.NewGuid();
        using ActiveQuery query = new("SELECT 1", contextId);

        Assert.NotEqual(Guid.Empty, query.QueryId);
        Assert.Equal("SELECT 1", query.Sql);
        Assert.Equal(contextId, query.ContextId);
        Assert.False(query.CancellationToken.IsCancellationRequested);
        Assert.True(query.StartedAt <= DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Each active query gets a distinct identifier.
    /// </summary>
    [Fact]
    public void Constructor_GeneratesDistinctIds()
    {
        using ActiveQuery first = new("SELECT 1", Guid.NewGuid());
        using ActiveQuery second = new("SELECT 2", Guid.NewGuid());

        Assert.NotEqual(first.QueryId, second.QueryId);
    }

    /// <summary>
    /// Cancel signals the cancellation token.
    /// </summary>
    [Fact]
    public void Cancel_SignalsCancellationToken()
    {
        using ActiveQuery query = new("SELECT 1", Guid.NewGuid());

        query.Cancel();

        Assert.True(query.CancellationToken.IsCancellationRequested);
    }

    /// <summary>
    /// Dispose releases the underlying cancellation token source without throwing.
    /// </summary>
    [Fact]
    public void Dispose_ReleasesResources()
    {
        ActiveQuery query = new("SELECT 1", Guid.NewGuid());
        CancellationToken token = query.CancellationToken;

        query.Dispose();

        // Accessing the token after disposal should throw ObjectDisposedException.
        Assert.Throws<ObjectDisposedException>(() => token.WaitHandle);
    }
}
