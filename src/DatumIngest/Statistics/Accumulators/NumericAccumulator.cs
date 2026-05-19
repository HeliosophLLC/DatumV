namespace Heliosoph.DatumV.Statistics.Accumulators;

using Heliosoph.DatumV.Model;

/// <summary>
/// Accumulates min, max, mean, and variance for numeric columns using Welford's online algorithm.
/// Welford's algorithm provides numerically stable mean and variance computation in a single pass.
/// </summary>
public sealed class NumericAccumulator : IStatisticAccumulator
{
    private long _count;
    private long _zeroCount;
    private long _outlierCount;
    private double _min = double.PositiveInfinity;
    private double _max = double.NegativeInfinity;
    private double _mean;
    private double _m2; // Sum of squared deviations from the mean (for Welford's)
    private double _m3; // Third central moment accumulator (for skewness)
    private double _m4; // Fourth central moment accumulator (for kurtosis)

    // Parallel Welford accumulators for nonzero values only.
    // Populated alongside the main accumulators with negligible overhead.
    private long _nonzeroCount;
    private double _nonzeroMean;
    private double _nonzeroM2;

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

    /// <summary>Gets the skewness (third standardized moment). Zero when count &lt; 3 or variance is zero.</summary>
    public double Skewness => _count > 2 && _m2 > 0 ? Math.Sqrt((double)_count) * _m3 / Math.Pow(_m2, 1.5) : 0.0;

    /// <summary>Gets the kurtosis (fourth standardized moment). Zero when count &lt; 4 or variance is zero.</summary>
    public double Kurtosis => _count > 3 && _m2 > 0 ? (double)_count * _m4 / (_m2 * _m2) : 0.0;

    /// <inheritdoc />
    public void Add(DataValue value, IValueStore store)
    {
        if (value.IsNull)
        {
            return;
        }

        double numericValue = value.TryToDouble(out double d) ? d
            : value.Kind == DataKind.Duration ? value.AsDuration().TotalSeconds
            : double.NaN;

        if (double.IsNaN(numericValue))
        {
            return;
        }

        _count++;

        if (numericValue == 0.0)
        {
            _zeroCount++;
        }
        else
        {
            // Welford's for the nonzero subset
            _nonzeroCount++;
            double nonzeroDelta = numericValue - _nonzeroMean;
            double nonzeroDeltaN = nonzeroDelta / _nonzeroCount;
            _nonzeroM2 += nonzeroDelta * nonzeroDeltaN * (_nonzeroCount - 1);
            _nonzeroMean += nonzeroDeltaN;
        }

        if (numericValue < _min)
        {
            _min = numericValue;
        }

        if (numericValue > _max)
        {
            _max = numericValue;
        }

        // Welford's online algorithm extended to higher moments (Terriberry 2008)
        double delta = numericValue - _mean;
        double deltaN = delta / _count;
        double deltaN2 = deltaN * deltaN;
        double term1 = delta * deltaN * (_count - 1);

        _m4 += term1 * deltaN2 * ((double)_count * _count - 3.0 * _count + 3.0)
             + 6.0 * deltaN2 * _m2 - 4.0 * deltaN * _m3;
        _m3 += term1 * deltaN * (_count - 2) - 3.0 * deltaN * _m2;
        _m2 += term1;
        _mean += deltaN;

        // Z-score outlier detection: |x - mean| / stddev > 3
        if (_count >= 2)
        {
            double variance = _m2 / _count;
            if (variance > 0)
            {
                double stdDev = Math.Sqrt(variance);
                if (Math.Abs(numericValue - _mean) / stdDev > 3.0)
                {
                    _outlierCount++;
                }
            }
        }
    }

    /// <inheritdoc />
    public IEnumerable<StatisticResult> GetResults()
    {
        double zeroRatio = _count > 0 ? (double)_zeroCount / _count : 0.0;
        double outlierRatio = _count > 0 ? (double)_outlierCount / _count : 0.0;
        double nonzeroVariance = _nonzeroCount > 1 ? _nonzeroM2 / _nonzeroCount :
            _nonzeroCount == 1 ? 0.0 : double.NaN;
        yield return new StatisticResult("numeric", new NumericResult(
            _count, Min, Max, Mean, Variance, StandardDeviation, Skewness, Kurtosis,
            _zeroCount, zeroRatio, _outlierCount, outlierRatio,
            _nonzeroCount,
            _nonzeroCount > 0 ? _nonzeroMean : double.NaN,
            nonzeroVariance,
            Math.Sqrt(nonzeroVariance)));
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
/// <param name="Skewness">Skewness (third standardized moment). Positive indicates right-skewed distribution.</param>
/// <param name="Kurtosis">Kurtosis (fourth standardized moment). Normal distribution yields approximately 3.</param>
/// <param name="ZeroCount">Number of values exactly equal to zero.</param>
/// <param name="ZeroRatio">Ratio of zero values to total count.</param>
/// <param name="OutlierCount">Number of values with Z-score greater than 3.</param>
/// <param name="OutlierRatio">Ratio of outlier values to total count.</param>
/// <param name="NonzeroCount">Number of nonzero values observed.</param>
/// <param name="NonzeroMean">Mean of nonzero values, or NaN if none.</param>
/// <param name="NonzeroVariance">Population variance of nonzero values.</param>
/// <param name="NonzeroStandardDeviation">Population standard deviation of nonzero values.</param>
public sealed record NumericResult(
    long Count, double Min, double Max, double Mean,
    double Variance, double StandardDeviation, double Skewness, double Kurtosis,
    long ZeroCount, double ZeroRatio, long OutlierCount, double OutlierRatio,
    long NonzeroCount, double NonzeroMean, double NonzeroVariance, double NonzeroStandardDeviation)
{
    /// <summary>An empty result with zero counts and NaN for all numeric fields.</summary>
    public static NumericResult Empty { get; } = new(
        0, double.NaN, double.NaN, double.NaN, 0, 0, 0, 0, 0, 0, 0, 0,
        0, double.NaN, 0, 0);
}
