namespace Heliosoph.DatumV.Statistics;

/// <summary>
/// Represents the result of a statistic accumulation with a name and typed value.
/// </summary>
/// <param name="Name">The name of the statistic (e.g. "count", "mean", "min").</param>
/// <param name="Value">The computed statistic value.</param>
public sealed record StatisticResult(string Name, object? Value);
