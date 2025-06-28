namespace Axon.QueryEngine.Statistics.Accumulators;

using Axon.QueryEngine.Model;

/// <summary>
/// Computes approximate percentiles (P1, P5, P25, P50, P75, P95, P99) for numeric columns
/// using reservoir sampling. On <see cref="GetResult"/> the retained samples are sorted and
/// percentiles are computed via linear interpolation (matching NumPy's default method).
/// </summary>
public sealed class QuantileAccumulator : IStatisticAccumulator
{
    /// <summary>Maximum samples retained in the reservoir.</summary>
    public const int MaxSamples = 100_000;

    private readonly List<float> _samples = new();
    private readonly Random _random = new(42);
    private long _totalCount;

    /// <summary>Gets the total number of values observed (may exceed <see cref="MaxSamples"/>).</summary>
    public long TotalCount => _totalCount;

    /// <summary>Gets the current number of samples retained.</summary>
    public int SampleCount => _samples.Count;

    /// <inheritdoc />
    public void Add(DataValue value)
    {
        if (value.IsNull)
        {
            return;
        }

        float numericValue = value.Kind switch
        {
            DataKind.Scalar => value.AsScalar(),
            DataKind.UInt8 => value.AsUInt8(),
            _ => float.NaN
        };

        if (float.IsNaN(numericValue))
        {
            return;
        }

        _totalCount++;

        if (_samples.Count < MaxSamples)
        {
            _samples.Add(numericValue);
        }
        else
        {
            // Algorithm R reservoir sampling
            long j = _random.NextInt64(_totalCount);

            if (j < MaxSamples)
            {
                _samples[(int)j] = numericValue;
            }
        }
    }

    /// <inheritdoc />
    public void Merge(IStatisticAccumulator other)
    {
        if (other is not QuantileAccumulator otherQuantile || otherQuantile._totalCount == 0)
        {
            return;
        }

        _totalCount += otherQuantile._totalCount;
        _samples.AddRange(otherQuantile._samples);

        if (_samples.Count > MaxSamples)
        {
            // Shuffle and truncate for approximate fairness
            for (int i = _samples.Count - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1);
                (_samples[i], _samples[j]) = (_samples[j], _samples[i]);
            }

            _samples.RemoveRange(MaxSamples, _samples.Count - MaxSamples);
        }
    }

    /// <inheritdoc />
    public StatisticResult GetResult()
    {
        if (_samples.Count == 0)
        {
            return new StatisticResult("quantile", new QuantileResult(
                double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN));
        }

        _samples.Sort();

        return new StatisticResult("quantile", new QuantileResult(
            P01: Percentile(0.01),
            P05: Percentile(0.05),
            P25: Percentile(0.25),
            P50: Percentile(0.50),
            P75: Percentile(0.75),
            P95: Percentile(0.95),
            P99: Percentile(0.99)));
    }

    /// <summary>
    /// Computes the percentile value using linear interpolation.
    /// For a fraction p in [0,1], the index is p * (n - 1). The result
    /// is linearly interpolated between the two surrounding samples.
    /// </summary>
    private double Percentile(double p)
    {
        double index = p * (_samples.Count - 1);
        int lower = (int)Math.Floor(index);
        int upper = (int)Math.Ceiling(index);

        if (lower == upper)
        {
            return _samples[lower];
        }

        double fraction = index - lower;
        return _samples[lower] * (1.0 - fraction) + _samples[upper] * fraction;
    }
}

/// <summary>
/// Contains computed percentile values from the quantile accumulator.
/// </summary>
/// <param name="P01">1st percentile.</param>
/// <param name="P05">5th percentile.</param>
/// <param name="P25">25th percentile (first quartile).</param>
/// <param name="P50">50th percentile (median).</param>
/// <param name="P75">75th percentile (third quartile).</param>
/// <param name="P95">95th percentile.</param>
/// <param name="P99">99th percentile.</param>
public sealed record QuantileResult(
    double P01, double P05, double P25, double P50, double P75, double P95, double P99)
{
    /// <inheritdoc />
    public override string ToString() =>
        $"P1={P01:F2}, P5={P05:F2}, P25={P25:F2}, P50={P50:F2}, P75={P75:F2}, P95={P95:F2}, P99={P99:F2}";
}
