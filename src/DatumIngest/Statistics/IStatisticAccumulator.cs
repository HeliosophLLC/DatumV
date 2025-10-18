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
    /// Hook called by <see cref="StatisticsCollector.FlushRowGroup"/> just before the
    /// writer's arena is reset. Accumulators that hold arena-relative references into
    /// the writer's arena (e.g. local Space-Saving sketches) must materialize any
    /// retained data here — after the call returns, <paramref name="writerArenaStore"/>
    /// is no longer valid. Default is no-op; pure-numeric or self-contained
    /// accumulators need not override.
    /// </summary>
    /// <param name="writerArenaStore">
    /// Read-only view over the current row group's page of the writer's arena.
    /// Used to resolve any arena offsets the accumulator has retained since the
    /// last flush.
    /// </param>
    void BeforeRowGroupFlush(IValueStore writerArenaStore) { }

    /// <summary>
    /// Returns the accumulated statistic result(s). Most accumulators yield one
    /// result; <see cref="Accumulators.SpaceSavingAccumulator"/> yields three
    /// (<c>top_k</c>, <c>entropy</c>, <c>categorical_diagnostics</c>) from a
    /// single shared sketch.
    /// </summary>
    IEnumerable<StatisticResult> GetResults();
}
