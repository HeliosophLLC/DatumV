namespace DatumIngest.Statistics;

using System.Collections.Concurrent;
using DatumIngest.Model;
using DatumIngest.Statistics.Accumulators;

/// <summary>
/// Manages per-column statistic accumulators and collects statistics from rows.
/// Thread-safe: uses concurrent dictionaries for parallel accumulation.
/// </summary>
public sealed class StatisticsCollector
{
    private readonly ConcurrentDictionary<string, List<IStatisticAccumulator>> _columnAccumulators = new();
    private readonly int _topK;

    /// <summary>
    /// Cached accumulator lists indexed by column ordinal. Populated on the first row
    /// and reused for all subsequent rows, bypassing the <see cref="ConcurrentDictionary{TKey,TValue}"/>
    /// lookup overhead on the hot path.
    /// </summary>
    private List<IStatisticAccumulator>[]? _ordinalAccumulators;

    /// <summary>
    /// Initializes a new instance of the <see cref="StatisticsCollector"/> class.
    /// </summary>
    /// <param name="topK">Number of top-K values to track per column. Defaults to 10.</param>
    public StatisticsCollector(int topK = 10)
    {
        _topK = topK;
    }

    /// <summary>
    /// Adds a row to all column accumulators. Creates accumulators for new columns on first encounter.
    /// </summary>
    public void AddRow(Row row)
    {
        List<IStatisticAccumulator>[]? cached = _ordinalAccumulators;

        if (cached is not null && cached.Length == row.FieldCount)
        {
            for (int ordinal = 0; ordinal < cached.Length; ordinal++)
            {
                DataValue value = row[ordinal];
                List<IStatisticAccumulator> accumulators = cached[ordinal];

                foreach (IStatisticAccumulator accumulator in accumulators)
                {
                    accumulator.Add(value);
                }
            }

            return;
        }

        // First row or schema change: populate via the dictionary path and cache the result.
        List<IStatisticAccumulator>[] ordinalCache = new List<IStatisticAccumulator>[row.FieldCount];
        int index = 0;

        foreach (string columnName in row.ColumnNames)
        {
            DataValue value = row[columnName];
            List<IStatisticAccumulator> accumulators = _columnAccumulators.GetOrAdd(
                columnName,
                _ => CreateAccumulators(value.Kind));

            foreach (IStatisticAccumulator accumulator in accumulators)
            {
                accumulator.Add(value);
            }

            ordinalCache[index] = accumulators;
            index++;
        }

        _ordinalAccumulators = ordinalCache;
    }

    /// <summary>
    /// Merges another collector's state into this one. Used for combining results from parallel processing.
    /// </summary>
    public void Merge(StatisticsCollector other)
    {
        foreach (KeyValuePair<string, List<IStatisticAccumulator>> entry in other._columnAccumulators)
        {
            List<IStatisticAccumulator> thisAccumulators = _columnAccumulators.GetOrAdd(
                entry.Key,
                _ => CreateAccumulators(DataKind.String));

            if (thisAccumulators.Count != entry.Value.Count)
            {
                continue;
            }

            for (int i = 0; i < thisAccumulators.Count; i++)
            {
                thisAccumulators[i].Merge(entry.Value[i]);
            }
        }
    }

    /// <summary>
    /// Gets the aggregated statistics for all columns.
    /// </summary>
    public IReadOnlyDictionary<string, ColumnStatistics> GetStatistics()
    {
        Dictionary<string, ColumnStatistics> result = new();

        foreach (KeyValuePair<string, List<IStatisticAccumulator>> entry in _columnAccumulators)
        {
            Dictionary<string, StatisticResult> results = new();

            foreach (IStatisticAccumulator accumulator in entry.Value)
            {
                StatisticResult statisticResult = accumulator.GetResult();
                results[statisticResult.Name] = statisticResult;
            }

            result[entry.Key] = new ColumnStatistics(entry.Key, results);
        }

        return result;
    }

    private List<IStatisticAccumulator> CreateAccumulators(DataKind kind)
    {
        List<IStatisticAccumulator> accumulators =
        [
            new CountAccumulator(),
            new CardinalityAccumulator(),
            new MissingRunsAccumulator()
        ];

        // TopK is only meaningful for discrete, representable values.
        // Binary blobs (Image, UInt8Array) and multi-dimensional data (Vector, Matrix, Tensor)
        // have no useful string representation for frequency counting.
        if (kind is not (DataKind.Image or DataKind.UInt8Array or DataKind.Vector or DataKind.Matrix or DataKind.Tensor or DataKind.Array))
        {
            accumulators.Add(new TopKAccumulator(_topK, kind));
        }

        if (kind is DataKind.Float32 or DataKind.UInt8)
        {
            accumulators.Add(new NumericAccumulator());
            accumulators.Add(new HistogramAccumulator());
            accumulators.Add(new QuantileAccumulator());
        }

        if (kind is DataKind.String or DataKind.JsonValue)
        {
            accumulators.Add(new StringLengthAccumulator());
        }

        if (kind is DataKind.Vector or DataKind.Matrix or DataKind.Tensor)
        {
            accumulators.Add(new VectorStatsAccumulator());
        }

        if (kind is DataKind.Image)
        {
            accumulators.Add(new ImageStatsAccumulator());
        }

        if (kind is DataKind.UInt8Array)
        {
            accumulators.Add(new BinarySizeAccumulator());
        }

        if (kind is DataKind.Date or DataKind.DateTime or DataKind.Time)
        {
            accumulators.Add(new TemporalRangeAccumulator());
        }

        if (kind is DataKind.Duration)
        {
            accumulators.Add(new NumericAccumulator());
            accumulators.Add(new HistogramAccumulator());
            accumulators.Add(new QuantileAccumulator());
        }

        if (kind is DataKind.Float32 or DataKind.UInt8 or DataKind.String or DataKind.JsonValue or DataKind.Date or DataKind.DateTime or DataKind.Uuid or DataKind.Boolean or DataKind.Time or DataKind.Duration)
        {
            accumulators.Add(new EntropyAccumulator(kind));
            accumulators.Add(new CategoricalDiagnosticsAccumulator(_topK, kind));
        }

        return accumulators;
    }
}
