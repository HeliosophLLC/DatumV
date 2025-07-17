using DatumIngest.Compute.Services;

namespace DatumIngest.Tests.Compute;

/// <summary>
/// Tests for <see cref="LocalFileDatasetStore"/> filesystem-based dataset operations.
/// </summary>
public sealed class LocalFileDatasetStoreTests : IDisposable
{
    private readonly string _basePath;

    /// <summary>
    /// Creates an isolated temporary directory for each test run.
    /// </summary>
    public LocalFileDatasetStoreTests()
    {
        _basePath = Path.Combine(Path.GetTempPath(), $"DatumIngestTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_basePath);
    }

    // ─────────────────── ExistsLocallyAsync ───────────────────

    /// <summary>
    /// ExistsLocallyAsync returns true when the dataset directory exists.
    /// </summary>
    [Fact]
    public async Task ExistsLocallyAsync_DirectoryExists_ReturnsTrue()
    {
        Directory.CreateDirectory(Path.Combine(_basePath, "my-dataset"));
        LocalFileDatasetStore store = new(_basePath);

        bool exists = await store.ExistsLocallyAsync("my-dataset", CancellationToken.None);

        Assert.True(exists);
    }

    /// <summary>
    /// ExistsLocallyAsync returns false when the dataset directory does not exist.
    /// </summary>
    [Fact]
    public async Task ExistsLocallyAsync_DirectoryMissing_ReturnsFalse()
    {
        LocalFileDatasetStore store = new(_basePath);

        bool exists = await store.ExistsLocallyAsync("nonexistent", CancellationToken.None);

        Assert.False(exists);
    }

    // ─────────────────── PullAsync ───────────────────

    /// <summary>
    /// PullAsync returns the resolved path when the dataset directory exists.
    /// </summary>
    [Fact]
    public async Task PullAsync_DirectoryExists_ReturnsPath()
    {
        string expectedPath = Path.Combine(_basePath, "my-dataset");
        Directory.CreateDirectory(expectedPath);
        LocalFileDatasetStore store = new(_basePath);

        string resultPath = await store.PullAsync("my-dataset", CancellationToken.None);

        Assert.Equal(expectedPath, resultPath);
    }

    /// <summary>
    /// PullAsync throws when the dataset directory does not exist.
    /// </summary>
    [Fact]
    public async Task PullAsync_DirectoryMissing_ThrowsDirectoryNotFoundException()
    {
        LocalFileDatasetStore store = new(_basePath);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => store.PullAsync("missing-dataset", CancellationToken.None));
    }

    // ─────────────────── EvictAsync ───────────────────

    /// <summary>
    /// EvictAsync completes without error (no-op implementation).
    /// </summary>
    [Fact]
    public async Task EvictAsync_CompletesSuccessfully()
    {
        string datasetPath = Path.Combine(_basePath, "to-evict");
        Directory.CreateDirectory(datasetPath);
        LocalFileDatasetStore store = new(_basePath);

        await store.EvictAsync("to-evict", CancellationToken.None);

        // Local store does not delete on eviction — data stays on disk.
        Assert.True(Directory.Exists(datasetPath));
    }

    // ─────────────────── Path traversal protection ───────────────────

    /// <summary>
    /// Dataset identifiers with path traversal sequences are sanitized.
    /// </summary>
    [Fact]
    public async Task ExistsLocallyAsync_PathTraversalAttempt_IsSanitized()
    {
        LocalFileDatasetStore store = new(_basePath);

        // The traversal attempt should be sanitized to just the filename portion.
        bool exists = await store.ExistsLocallyAsync("../../../etc/passwd", CancellationToken.None);

        Assert.False(exists);
    }

    /// <summary>
    /// PullAsync with traversal attempt resolves to sanitized path.
    /// </summary>
    [Fact]
    public async Task PullAsync_PathTraversalAttempt_ThrowsForSanitizedPath()
    {
        LocalFileDatasetStore store = new(_basePath);

        // Should resolve "../../secret" to just "secret" via Path.GetFileName,
        // which won't exist under the base path.
        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => store.PullAsync("../../secret", CancellationToken.None));
    }

    /// <summary>
    /// Empty dataset identifier throws ArgumentException.
    /// </summary>
    [Fact]
    public async Task ExistsLocallyAsync_EmptyIdentifier_ThrowsArgumentException()
    {
        LocalFileDatasetStore store = new(_basePath);

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.ExistsLocallyAsync("", CancellationToken.None));
    }

    /// <summary>
    /// Backslash path traversal is also sanitized.
    /// </summary>
    [Fact]
    public async Task ExistsLocallyAsync_BackslashTraversal_IsSanitized()
    {
        LocalFileDatasetStore store = new(_basePath);

        bool exists = await store.ExistsLocallyAsync("..\\..\\Windows\\System32", CancellationToken.None);

        Assert.False(exists);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_basePath))
            {
                Directory.Delete(_basePath, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
    }
}
