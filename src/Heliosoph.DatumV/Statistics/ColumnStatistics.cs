namespace Heliosoph.DatumV.Statistics;

/// <summary>
/// Aggregated statistics for a single column, containing results from all applicable accumulators.
/// </summary>
/// <param name="ColumnName">The name of the column.</param>
/// <param name="Results">The statistic results keyed by accumulator name.</param>
public sealed record ColumnStatistics(string ColumnName, IReadOnlyDictionary<string, StatisticResult> Results);
