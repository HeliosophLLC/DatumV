using DatumIngest.Catalog;
using DatumIngest.Functions;
using DatumIngest.Server;

namespace DatumIngest.Tests.Server;

/// <summary>
/// Tests for <see cref="SessionManager"/> session lifecycle management.
/// </summary>
public sealed class SessionManagerTests
{
    private readonly FunctionRegistry _functionRegistry = FunctionRegistry.CreateDefault();

    /// <summary>
    /// CreateLocalSession creates a tracked session.
    /// </summary>
    [Fact]
    public void CreateLocalSession_ReturnsTrackedSession()
    {
        SessionManager manager = new(_functionRegistry);
        Session session = manager.CreateLocalSession(SessionRole.Admin, new TableCatalog());

        Assert.NotNull(session);
        Assert.Equal(SessionRole.Admin, session.Role);
        Assert.Null(session.DatasetId);

        Session? retrieved = manager.GetSession(session.SessionId);
        Assert.Same(session, retrieved);

        session.Dispose();
    }

    /// <summary>
    /// GetSession returns null for unknown session identifiers.
    /// </summary>
    [Fact]
    public void GetSession_UnknownId_ReturnsNull()
    {
        SessionManager manager = new(_functionRegistry);
        Assert.Null(manager.GetSession(Guid.NewGuid()));
    }

    /// <summary>
    /// GetAllSessions returns all tracked sessions.
    /// </summary>
    [Fact]
    public void GetAllSessions_ReturnsAllTracked()
    {
        SessionManager manager = new(_functionRegistry);
        Session session1 = manager.CreateLocalSession(SessionRole.Admin, new TableCatalog());
        Session session2 = manager.CreateLocalSession(SessionRole.User, new TableCatalog());

        IReadOnlyList<Session> all = manager.GetAllSessions();
        Assert.Equal(2, all.Count);

        session1.Dispose();
        session2.Dispose();
    }

    /// <summary>
    /// RemoveSession disposes and removes the session.
    /// </summary>
    [Fact]
    public void RemoveSession_DisposesAndRemoves()
    {
        SessionManager manager = new(_functionRegistry);
        Session session = manager.CreateLocalSession(SessionRole.Admin, new TableCatalog());
        Guid id = session.SessionId;

        Assert.True(manager.RemoveSession(id));
        Assert.Null(manager.GetSession(id));
    }

    /// <summary>
    /// RemoveSession returns false for unknown identifiers.
    /// </summary>
    [Fact]
    public void RemoveSession_UnknownId_ReturnsFalse()
    {
        SessionManager manager = new(_functionRegistry);
        Assert.False(manager.RemoveSession(Guid.NewGuid()));
    }

    /// <summary>
    /// CreateSessionAsync throws when no dataset store is configured.
    /// </summary>
    [Fact]
    public async Task CreateSessionAsync_NoDatasetStore_Throws()
    {
        SessionManager manager = new(_functionRegistry);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.CreateSessionAsync(
                SessionRole.User,
                "dataset-1",
                _ => new TableCatalog(),
                CancellationToken.None));
    }

    /// <summary>
    /// CreateSessionAsync pulls the dataset and creates a session.
    /// </summary>
    [Fact]
    public async Task CreateSessionAsync_WithStore_PullsAndCreatesSession()
    {
        InMemoryDatasetStore store = new();
        store.AddDataset("ds-1", "/tmp/ds1");

        SessionManager manager = new(_functionRegistry, store);
        Session session = await manager.CreateSessionAsync(
            SessionRole.User,
            "ds-1",
            _ => new TableCatalog(),
            CancellationToken.None);

        Assert.Equal("ds-1", session.DatasetId);
        Assert.Equal(SessionRole.User, session.Role);

        session.Dispose();
    }

    /// <summary>
    /// GetEvictableDatasets returns datasets past cooldown with no active sessions.
    /// </summary>
    [Fact]
    public void GetEvictableDatasets_ExpiredWithNoSessions_ReturnsDataset()
    {
        InMemoryDatasetStore store = new();
        SessionManager manager = new(_functionRegistry, store);

        // Simulate a dataset that was accessed then its session was removed.
        Session session = manager.CreateLocalSession(SessionRole.User, new TableCatalog());
        // Directly touch the dataset tracking (internal access).
        manager.TouchDataset("old-dataset");
        manager.RemoveSession(session.SessionId);

        // With zero cooldown, everything is evictable.
        IReadOnlyList<string> evictable = manager.GetEvictableDatasets(TimeSpan.Zero);
        Assert.Contains("old-dataset", evictable);
    }

    /// <summary>
    /// ClearDatasetTracking removes the dataset from tracking.
    /// </summary>
    [Fact]
    public void ClearDatasetTracking_RemovesEntry()
    {
        SessionManager manager = new(_functionRegistry);
        manager.TouchDataset("test-dataset");

        IReadOnlyList<string> beforeClear = manager.GetEvictableDatasets(TimeSpan.Zero);
        Assert.Contains("test-dataset", beforeClear);

        manager.ClearDatasetTracking("test-dataset");

        IReadOnlyList<string> afterClear = manager.GetEvictableDatasets(TimeSpan.Zero);
        Assert.DoesNotContain("test-dataset", afterClear);
    }

    /// <summary>
    /// Minimal in-memory IDatasetStore for testing.
    /// </summary>
    private sealed class InMemoryDatasetStore : IDatasetStore
    {
        private readonly Dictionary<string, string> _datasets = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Adds a dataset to the in-memory store.</summary>
        public void AddDataset(string datasetId, string localPath) => _datasets[datasetId] = localPath;

        /// <inheritdoc/>
        public Task<bool> ExistsLocallyAsync(string datasetId, CancellationToken cancellationToken)
            => Task.FromResult(_datasets.ContainsKey(datasetId));

        /// <inheritdoc/>
        public Task<string> PullAsync(string datasetId, CancellationToken cancellationToken)
        {
            if (!_datasets.TryGetValue(datasetId, out string? path))
            {
                throw new KeyNotFoundException($"Dataset '{datasetId}' not found.");
            }

            return Task.FromResult(path);
        }

        /// <inheritdoc/>
        public Task EvictAsync(string datasetId, CancellationToken cancellationToken)
        {
            _datasets.Remove(datasetId);
            return Task.CompletedTask;
        }
    }
}
