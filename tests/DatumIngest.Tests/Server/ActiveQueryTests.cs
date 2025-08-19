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
        using ActiveQuery query = new("SELECT 1");

        Assert.NotEqual(Guid.Empty, query.QueryId);
        Assert.Equal("SELECT 1", query.Sql);
        Assert.False(query.CancellationToken.IsCancellationRequested);
        Assert.True(query.StartedAt <= DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Each active query gets a distinct identifier.
    /// </summary>
    [Fact]
    public void Constructor_GeneratesDistinctIds()
    {
        using ActiveQuery first = new("SELECT 1");
        using ActiveQuery second = new("SELECT 2");

        Assert.NotEqual(first.QueryId, second.QueryId);
    }

    /// <summary>
    /// Cancel signals the cancellation token.
    /// </summary>
    [Fact]
    public void Cancel_SignalsCancellationToken()
    {
        using ActiveQuery query = new("SELECT 1");

        query.Cancel();

        Assert.True(query.CancellationToken.IsCancellationRequested);
    }

    /// <summary>
    /// Dispose releases the underlying cancellation token source without throwing.
    /// </summary>
    [Fact]
    public void Dispose_ReleasesResources()
    {
        ActiveQuery query = new("SELECT 1");
        CancellationToken token = query.CancellationToken;

        query.Dispose();

        // Accessing the token after disposal should throw ObjectDisposedException.
        Assert.Throws<ObjectDisposedException>(() => token.WaitHandle);
    }
}
