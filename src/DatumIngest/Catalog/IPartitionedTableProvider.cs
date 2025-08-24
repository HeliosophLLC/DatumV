using DatumIngest.Model;

namespace DatumIngest.Catalog;

/// <summary>
/// Extended interface for table providers that support splitting a table into
/// disjoint row partitions that can be scanned concurrently.
/// </summary>
/// <remarks>
/// <para>
/// The union of all partitions must equal the full table. Row order across
/// partitions is not guaranteed — callers that require a specific order must
/// sort the merged output themselves.
/// </para>
/// <para>
/// Providers should return <see langword="null"/> when partitioning is not feasible
/// for the given descriptor (e.g. compressed files, empty files, files smaller than
/// the per-partition minimum). The caller falls back to the single-stream path.
/// </para>
/// </remarks>
public interface IPartitionedTableProvider
{
    /// <summary>
    /// Opens the table as a set of independent partition streams, each covering a
    /// disjoint contiguous byte range aligned to row boundaries. Returns
    /// <see langword="null"/> when the provider cannot partition the source
    /// (e.g. the file is too small, compressed, or unavailable).
    /// </summary>
    /// <param name="descriptor">The table to open.</param>
    /// <param name="requiredColumns">Columns to include; <see langword="null"/> means all columns.</param>
    /// <param name="maxPartitions">
    /// The maximum number of partitions to produce. The provider may return fewer when
    /// the file is too small to justify the requested partition count.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for the partitioning setup phase.</param>
    /// <returns>
    /// A list of independent enumerables (one per partition), or <see langword="null"/>
    /// if partitioning is not available.
    /// </returns>
    Task<IReadOnlyList<IAsyncEnumerable<Row>>?> OpenPartitionsAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        int maxPartitions,
        CancellationToken cancellationToken);
}
