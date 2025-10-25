namespace DatumIngest.Model;

/// <summary>
/// Provider-agnostic representation of column-level statistics for a single
/// partition (e.g. a Parquet row group). Used by the query engine to determine
/// whether an entire partition can be skipped based on WHERE predicates.
/// </summary>
/// <param name="Minimum">The minimum value in this partition, or <c>null</c> if statistics are unavailable.</param>
/// <param name="Maximum">The maximum value in this partition, or <c>null</c> if statistics are unavailable.</param>
/// <param name="NullCount">The number of null values in this partition, or <c>null</c> if unknown.</param>
/// <param name="RowCount">The total number of rows in this partition.</param>
public sealed record ColumnStatisticsRange(
    DataValue? Minimum,
    DataValue? Maximum,
    long? NullCount,
    long RowCount);