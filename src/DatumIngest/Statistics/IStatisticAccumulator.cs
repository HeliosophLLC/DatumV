namespace DatumIngest.Statistics;

using DatumIngest.Model;

/// <summary>
/// Interface for pluggable statistic accumulators that process data values incrementally.
/// </summary>
public interface IStatisticAccumulator
{
    /// <summary>
    /// Adds a data value to the accumulator.
    /// </summary>
    /// <param name="value">The value to accumulate.</param>
    /// <param name="store">Value store for resolving reference-type payloads (strings, vectors, etc.).</param>
    void Add(DataValue value, IValueStore store);

    /// <summary>
    /// Returns the accumulated statistic result.
    /// </summary>
    StatisticResult GetResult();
}
