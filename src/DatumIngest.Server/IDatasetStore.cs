namespace DatumIngest.Server;

/// <summary>
/// Abstraction for the dataset storage lifecycle: pulling datasets from
/// remote storage to a local cache, checking local availability, and
/// evicting cached datasets when they are no longer needed.
/// </summary>
public interface IDatasetStore
{
    /// <summary>
    /// Checks whether the dataset is already available in local storage.
    /// </summary>
    /// <param name="datasetId">Unique identifier for the dataset.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> if the dataset exists locally; otherwise <see langword="false"/>.</returns>
    Task<bool> ExistsLocallyAsync(string datasetId, CancellationToken cancellationToken);

    /// <summary>
    /// Pulls the dataset from remote storage to local disk, returning the
    /// local directory path. If the dataset is already cached locally, this
    /// should return the cached path without re-downloading.
    /// </summary>
    /// <param name="datasetId">Unique identifier for the dataset.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The local directory path containing the dataset files.</returns>
    Task<string> PullAsync(string datasetId, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the dataset from local storage, freeing disk space.
    /// </summary>
    /// <param name="datasetId">Unique identifier for the dataset.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task EvictAsync(string datasetId, CancellationToken cancellationToken);
}
