namespace Axon.QueryEngine.Statistics.Accumulators;

using Axon.QueryEngine.Model;

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
    public void Add(DataValue value)
    {
        if (value.IsNull)
        {
            return;
        }

        if (value.Kind is not DataKind.UInt8Array)
        {
            return;
        }

        byte[] bytes = value.AsUInt8Array();
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
    public void Merge(IStatisticAccumulator other)
    {
        if (other is not BinarySizeAccumulator otherBinary || otherBinary._count == 0)
        {
            return;
        }

        if (_count == 0)
        {
            _count = otherBinary._count;
            _min = otherBinary._min;
            _max = otherBinary._max;
            _mean = otherBinary._mean;
            _m2 = otherBinary._m2;
            return;
        }

        long combinedCount = _count + otherBinary._count;
        double delta = otherBinary._mean - _mean;
        _mean += delta * otherBinary._count / combinedCount;
        _m2 += otherBinary._m2 + delta * delta * _count * otherBinary._count / combinedCount;
        _min = Math.Min(_min, otherBinary._min);
        _max = Math.Max(_max, otherBinary._max);
        _count = combinedCount;
    }

    /// <inheritdoc />
    public StatisticResult GetResult()
    {
        double variance = _count > 1 ? _m2 / _count : 0.0;

        return new StatisticResult("binary_size", new BinarySizeResult(
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
public sealed record BinarySizeResult(NumericSummary SizeStats);
