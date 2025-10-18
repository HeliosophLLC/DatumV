namespace DatumIngest.Statistics.Accumulators;

using DatumIngest.Model;

/// <summary>
/// Computes approximate percentiles (P1, P5, P25, P50, P75, P95, P99) for numeric columns
/// using reservoir sampling. On <see cref="GetResults"/> the retained samples are sorted and
/// percentiles are computed via linear interpolation (matching NumPy's default method).
/// </summary>
public sealed class QuantileAccumulator : IStatisticAccumulator
{
    /// <summary>Maximum samples retained in the reservoir.</summary>
    public const int MaxSamples = 100_000;

    // Pre-sized to MaxSamples so the reservoir never triggers List<T>.AddWithResize
    // on the hot Add path. The ~800 KB backing array lives for the column's lifetime.
    private readonly List<double> _samples = new(MaxSamples);
    private readonly Random _random = new(42);
    private long _totalCount;

    /// <summary>Gets the total number of values observed (may exceed <see cref="MaxSamples"/>).</summary>
    public long TotalCount => _totalCount;

    /// <summary>Gets the current number of samples retained.</summary>
    public int SampleCount => _samples.Count;

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
    public IEnumerable<StatisticResult> GetResults()
    {
        if (_samples.Count == 0)
        {
            yield return new StatisticResult("quantile", new QuantileResult(
                double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN,
                double.NaN, double.NaN, double.NaN, 0, 0.0));
            yield break;
        }

        _samples.Sort();

        double p25 = Percentile(0.25);
        double p75 = Percentile(0.75);
        double iqr = p75 - p25;
        double lowerFence = p25 - 1.5 * iqr;
        double upperFence = p75 + 1.5 * iqr;

        // Count outliers using binary search on the sorted reservoir
        int belowCount = LowerBound(lowerFence);
        int aboveCount = _samples.Count - UpperBound(upperFence);
        long outlierCount = belowCount + aboveCount;
        double outlierRatio = (double)outlierCount / _samples.Count;

        yield return new StatisticResult("quantile", new QuantileResult(
            P01: Percentile(0.01),
            P05: Percentile(0.05),
            P25: p25,
            P50: Percentile(0.50),
            P75: p75,
            P95: Percentile(0.95),
            P99: Percentile(0.99),
            Iqr: iqr,
            LowerFence: lowerFence,
            UpperFence: upperFence,
            OutlierCount: outlierCount,
            OutlierRatio: outlierRatio));
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

    /// <summary>
    /// Returns the index of the first element that is not less than <paramref name="value"/>.
    /// </summary>
    private int LowerBound(double value)
    {
        int lo = 0, hi = _samples.Count;

        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;

            if (_samples[mid] < value)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    /// <summary>
    /// Returns the index of the first element that is greater than <paramref name="value"/>.
    /// </summary>
    private int UpperBound(double value)
    {
        int lo = 0, hi = _samples.Count;

        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;

            if (_samples[mid] <= value)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}

/// <summary>
/// Contains computed percentile values and IQR outlier statistics from the quantile accumulator.
/// </summary>
/// <param name="P01">1st percentile.</param>
/// <param name="P05">5th percentile.</param>
/// <param name="P25">25th percentile (first quartile).</param>
/// <param name="P50">50th percentile (median).</param>
/// <param name="P75">75th percentile (third quartile).</param>
/// <param name="P95">95th percentile.</param>
/// <param name="P99">99th percentile.</param>
/// <param name="Iqr">Interquartile range (P75 − P25).</param>
/// <param name="LowerFence">Lower Tukey fence (P25 − 1.5 × IQR). Values below this are outliers.</param>
/// <param name="UpperFence">Upper Tukey fence (P75 + 1.5 × IQR). Values above this are outliers.</param>
/// <param name="OutlierCount">Number of sampled values outside the fences.</param>
/// <param name="OutlierRatio">Ratio of outlier values to total sampled values.</param>
public sealed record QuantileResult(
    double P01, double P05, double P25, double P50, double P75, double P95, double P99,
    double Iqr, double LowerFence, double UpperFence, long OutlierCount, double OutlierRatio)
{
    /// <inheritdoc />
    public override string ToString() =>
        $"P1={P01:F2}, P5={P05:F2}, P25={P25:F2}, P50={P50:F2}, P75={P75:F2}, P95={P95:F2}, P99={P99:F2}, " +
        $"IQR={Iqr:F2}, Fences=[{LowerFence:F2}, {UpperFence:F2}], Outliers={OutlierCount} ({OutlierRatio:P1})";
}
