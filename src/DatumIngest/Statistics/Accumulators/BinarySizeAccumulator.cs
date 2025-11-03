namespace DatumIngest.Statistics.Accumulators;

using DatumIngest.Model;

/// <summary>
/// Accumulates size statistics for binary (UInt8Array) columns using Welford's algorithm.
/// Tracks min, max, mean, and standard deviation of byte array lengths.
/// </summary>
public sealed class BinarySizeAccumulator : IStatisticAccumulator
{
    private long _count;
    private double _min = double.PositiveInfinity;
    private double _max = double.NegativeInfinity;
    private double _mean;
    private double _m2;

    /// <inheritdoc />
    public void Add(DataValue value, IValueStore store)
    {
        if (value.IsNull)
        {
            return;
        }

        // Accept both legacy (Kind=UInt8Array) and new-model (Kind=UInt8 + IsArray)
        // byte arrays. PR3 will drop the legacy kind clause.
        bool isByteArray = value.Kind == DataKind.UInt8Array
            || (value.Kind == DataKind.UInt8 && value.IsArray);
        if (!isByteArray)
        {
            return;
        }

        byte[] bytes = value.AsUInt8Array(store);
        double size = bytes.Length;

        _count++;

        if (size < _min)
        {
            _min = size;
        }

        if (size > _max)
        {
            _max = size;
        }

        double delta = size - _mean;
        _mean += delta / _count;
        double delta2 = size - _mean;
        _m2 += delta * delta2;
    }

    /// <inheritdoc />
    public IEnumerable<StatisticResult> GetResults()
    {
        double variance = _count > 1 ? _m2 / _count : 0.0;

        yield return new StatisticResult("binary_size", new BinarySizeResult(
            new NumericSummary(
                _count,
                _count > 0 ? _min : double.NaN,
                _count > 0 ? _max : double.NaN,
                _count > 0 ? _mean : double.NaN,
                variance,
                Math.Sqrt(variance))));
    }
}

/// <summary>
/// Contains binary size accumulation results.
/// </summary>
/// <param name="SizeStats">Byte-length statistics for binary values.</param>
public sealed record BinarySizeResult(NumericSummary SizeStats)
{
    /// <summary>An empty result with no size data.</summary>
    public static BinarySizeResult Empty { get; } = new(NumericSummary.Empty);
}
