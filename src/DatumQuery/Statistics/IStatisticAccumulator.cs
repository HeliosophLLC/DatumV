namespace Axon.QueryEngine.Statistics;

using Axon.QueryEngine.Model;

/// <summary>
/// Interface for pluggable statistic accumulators that process data values incrementally.
/// </summary>
public interface IStatisticAccumulator
{
    /// <summary>
    /// Adds a data value to the accumulator.
    /// </summary>
    void Add(DataValue value);

    /// <summary>
    /// Merges another accumulator's state into this one. Used for parallel accumulation.
    /// </summary>
    void Merge(IStatisticAccumulator other);

    /// <summary>
    /// Returns the accumulated statistic result.
    /// </summary>
    StatisticResult GetResult();
}
