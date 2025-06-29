namespace Axon.QueryEngine.Statistics.Accumulators;

using Axon.QueryEngine.Model;

/// <summary>
/// Accumulates min, max, mean, and variance for numeric columns using Welford's online algorithm.
/// Welford's algorithm provides numerically stable mean and variance computation in a single pass.
/// </summary>
public sealed class NumericAccumulator : IStatisticAccumulator
{
    private long _count;
    private long _zeroCount;
    private double _min = double.PositiveInfinity;
    private double _max = double.NegativeInfinity;
    private double _mean;
    private double _m2; // Sum of squared deviations from the mean (for Welford's)

    /// <summary>Gets the number of numeric values observed.</summary>
    public long Count => _count;

    /// <summary>Gets the minimum value observed.</summary>
    public double Min => _count > 0 ? _min : double.NaN;

    /// <summary>Gets the maximum value observed.</summary>
    public double Max => _count > 0 ? _max : double.NaN;

    /// <summary>Gets the running mean.</summary>
    public double Mean => _count > 0 ? _mean : double.NaN;

    /// <summary>Gets the population variance.</summary>
    public double Variance => _count > 1 ? _m2 / _count : 0.0;

    /// <summary>Gets the population standard deviation.</summary>
    public double StandardDeviation => Math.Sqrt(Variance);

    /// <inheritdoc />
    public void Add(DataValue value)
    {
        if (value.IsNull)
        {
            return;
        }

        double numericValue = value.Kind switch
        {
            DataKind.Scalar => value.AsScalar(),
            DataKind.UInt8 => value.AsUInt8(),
            _ => double.NaN
        };

        if (double.IsNaN(numericValue))
        {
            return;
        }

        _count++;

        if (numericValue == 0.0)
        {
            _zeroCount++;
        }

        if (numericValue < _min)
        {
            _min = numericValue;
        }

        if (numericValue > _max)
        {
            _max = numericValue;
        }

        // Welford's online algorithm for mean and variance
        double delta = numericValue - _mean;
        _mean += delta / _count;
        double delta2 = numericValue - _mean;
        _m2 += delta * delta2;
    }

    /// <inheritdoc />
    public void Merge(IStatisticAccumulator other)
    {
        if (other is not NumericAccumulator otherNumeric || otherNumeric._count == 0)
        {
            return;
        }

        if (_count == 0)
        {
            _count = otherNumeric._count;
            _zeroCount = otherNumeric._zeroCount;
            _min = otherNumeric._min;
            _max = otherNumeric._max;
            _mean = otherNumeric._mean;
            _m2 = otherNumeric._m2;
            return;
        }

        // Parallel Welford merge (Chan et al.)
        long combinedCount = _count + otherNumeric._count;
        double delta = otherNumeric._mean - _mean;
        double combinedMean = _mean + delta * otherNumeric._count / combinedCount;
        double combinedM2 = _m2 + otherNumeric._m2 + delta * delta * _count * otherNumeric._count / combinedCount;

        _count = combinedCount;
        _mean = combinedMean;
        _m2 = combinedM2;
        _zeroCount += otherNumeric._zeroCount;
        _min = Math.Min(_min, otherNumeric._min);
        _max = Math.Max(_max, otherNumeric._max);
    }

    /// <inheritdoc />
    public StatisticResult GetResult()
    {
        double zeroRatio = _count > 0 ? (double)_zeroCount / _count : 0.0;
        return new StatisticResult("numeric", new NumericResult(
            _count, Min, Max, Mean, Variance, StandardDeviation, _zeroCount, zeroRatio));
    }
}

/// <summary>
/// Contains the numeric accumulation results.
/// </summary>
/// <param name="Count">Number of numeric values processed.</param>
/// <param name="Min">Minimum value.</param>
/// <param name="Max">Maximum value.</param>
/// <param name="Mean">Arithmetic mean.</param>
/// <param name="Variance">Population variance.</param>
/// <param name="StandardDeviation">Population standard deviation.</param>
/// <param name="ZeroCount">Number of values exactly equal to zero.</param>
/// <param name="ZeroRatio">Ratio of zero values to total count.</param>
public sealed record NumericResult(long Count, double Min, double Max, double Mean, double Variance, double StandardDeviation, long ZeroCount, double ZeroRatio);
