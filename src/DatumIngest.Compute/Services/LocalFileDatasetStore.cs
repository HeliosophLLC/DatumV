using DatumIngest.Server;

namespace DatumIngest.Compute.Services;

/// <summary>
/// A local filesystem <see cref="IDatasetStore"/> that treats dataset
/// identifiers as directory paths on disk. Suitable for development
/// and single-node deployments where datasets are already present locally.
/// </summary>
public sealed class LocalFileDatasetStore : IDatasetStore
{
    private readonly string _basePath;

    /// <summary>
    /// Initializes the store with a base directory under which datasets are stored.
    /// </summary>
    /// <param name="basePath">Root directory containing dataset subdirectories.</param>
    public LocalFileDatasetStore(string basePath)
    {
        _basePath = basePath;
    }

    /// <inheritdoc />
    public Task<bool> ExistsLocallyAsync(string datasetId, CancellationToken cancellationToken)
    {
        string path = ResolvePath(datasetId);
        return Task.FromResult(Directory.Exists(path));
    }

    /// <inheritdoc />
    public Task<string> PullAsync(string datasetId, CancellationToken cancellationToken)
    {
        string path = ResolvePath(datasetId);

        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException(
                $"Dataset '{datasetId}' not found at '{path}'.");
        }

        return Task.FromResult(path);
    }

    /// <inheritdoc />
    public Task EvictAsync(string datasetId, CancellationToken cancellationToken)
    {
        // Local file store does not delete on eviction — the data
        // stays on disk. Override in a blob-backed implementation.
        return Task.CompletedTask;
    }

    /// <summary>
    /// Resolves a dataset identifier to a full directory path, sanitizing
    /// the identifier to prevent directory traversal.
    /// </summary>
    private string ResolvePath(string datasetId)
    {
        // Sanitize the dataset ID to prevent path traversal attacks.
        string sanitized = Path.GetFileName(datasetId);

        if (string.IsNullOrWhiteSpace(sanitized))
        {
            throw new ArgumentException("Dataset identifier must not be empty.", nameof(datasetId));
        }

        return Path.Combine(_basePath, sanitized);
    }
}
