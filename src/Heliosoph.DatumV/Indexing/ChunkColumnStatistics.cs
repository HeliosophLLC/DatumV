using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Indexing;

/// <summary>
/// Per-column statistics for a single index chunk. Captures the minimum information
/// needed for partition pruning via <see cref="Execution.StatisticsPredicateEvaluator"/>.
/// </summary>
/// <param name="Minimum">The smallest value in this chunk, or <c>null</c> if unavailable.</param>
/// <param name="Maximum">The largest value in this chunk, or <c>null</c> if unavailable.</param>
/// <param name="NullCount">Number of null values in this chunk.</param>
/// <param name="RowCount">Total number of rows in this chunk.</param>
/// <param name="EstimatedCardinality">Approximate number of distinct values (HyperLogLog estimate).</param>
public sealed record ChunkColumnStatistics(
    DataValue? Minimum,
    DataValue? Maximum,
    long NullCount,
    long RowCount,
    long EstimatedCardinality)
{
    /// <summary>
    /// Converts to the <see cref="ColumnStatisticsRange"/> type used by the
    /// query engine's statistics-based partition pruning.
    /// </summary>
    public ColumnStatisticsRange ToColumnStatisticsRange() =>
        new(Minimum, Maximum, NullCount, RowCount);
}
