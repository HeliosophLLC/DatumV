namespace Heliosoph.DatumV.Statistics.Accumulators;

using Heliosoph.DatumV.Model;

/// <summary>
/// Accumulates min, max, mean, and variance for <see cref="DataKind.Decimal"/>
/// columns using Welford's online algorithm in <see cref="decimal"/>
/// arithmetic. Trades ~10× throughput vs <see cref="NumericAccumulator"/>'s
/// <see cref="double"/> path for full 28-digit precision — the whole point
/// of the Decimal kind. Manifests compute once at ingest / ANALYZE, not
/// in the per-row hot path, so the throughput cost is acceptable.
/// </summary>
/// <remarks>
/// v1 covers the planner's primary inputs: Count, Min, Max, Mean,
/// Variance, StandardDeviation, ZeroCount, IntegerValued. Skewness,
/// kurtosis, histogram, and quantiles are deferred — those higher-order
/// statistics are diagnostic-only and would require either additional
/// decimal arithmetic (cubic/quartic terms) or a hybrid double/decimal
/// path. Add them when a real workload demonstrates the need.
/// </remarks>
public sealed class DecimalAccumulator : IStatisticAccumulator
{
    private long _count;
    private long _zeroCount;
    private decimal _min = decimal.MaxValue;
    private decimal _max = decimal.MinValue;
    private decimal _mean;
    private decimal _m2;
    private bool _allInteger = true;

    /// <inheritdoc />
    public void Add(DataValue value, IValueStore store)
    {
        if (value.IsNull)
        {
            return;
        }

        if (value.Kind != DataKind.Decimal)
        {
            return;
        }

        decimal v = value.AsDecimal();

        _count++;

        if (v == 0m)
        {
            _zeroCount++;
        }

        if (v < _min) _min = v;
        if (v > _max) _max = v;

        // Welford's online algorithm in decimal — matches the double path
        // in NumericAccumulator but stays in decimal arithmetic so the
        // running mean / variance retain Decimal's precision. Skip the
        // M2 update when count == 1: the (count - 1) factor is zero, but
        // delta × deltaN = delta² can overflow decimal (~7.9e28 cap) for
        // single-value inputs near the kind's range — multiplying by zero
        // afterwards doesn't help if the intermediate already overflowed.
        decimal delta = v - _mean;
        decimal deltaN = delta / _count;
        if (_count > 1)
        {
            _m2 += delta * deltaN * (_count - 1);
        }
        _mean += deltaN;

        if (_allInteger && v != decimal.Truncate(v))
        {
            _allInteger = false;
        }
    }

    /// <inheritdoc />
    public IEnumerable<StatisticResult> GetResults()
    {
        decimal min = _count > 0 ? _min : 0m;
        decimal max = _count > 0 ? _max : 0m;
        decimal variance = _count > 1 ? _m2 / _count : 0m;
        decimal stdDev = SqrtDecimal(variance);
        decimal zeroRatio = _count > 0 ? (decimal)_zeroCount / _count : 0m;

        yield return new StatisticResult("decimal_numeric", new DecimalNumericResult(
            _count, min, max, _mean, variance, stdDev,
            _zeroCount, zeroRatio, _count > 0 && _allInteger));
    }

    /// <summary>
    /// Newton-Raphson square root for <see cref="decimal"/>. Decimal lacks a
    /// native sqrt; the iteration converges in ~10 steps for typical inputs
    /// and stays in decimal arithmetic to preserve precision. Negative inputs
    /// (impossible for variance but defensive) return zero.
    /// </summary>
    private static decimal SqrtDecimal(decimal value)
    {
        if (value <= 0m) return 0m;

        decimal x = value;
        decimal lastX = 0m;
        for (int i = 0; i < 50; i++)
        {
            x = (x + value / x) / 2m;
            if (x == lastX) break;
            lastX = x;
        }
        return x;
    }
}

/// <summary>
/// Decimal-precision numeric accumulator results.
/// </summary>
/// <param name="Count">Number of non-null values observed.</param>
/// <param name="Min">Minimum value, or 0 when no values observed.</param>
/// <param name="Max">Maximum value, or 0 when no values observed.</param>
/// <param name="Mean">Running mean.</param>
/// <param name="Variance">Population variance.</param>
/// <param name="StandardDeviation">Population standard deviation.</param>
/// <param name="ZeroCount">Number of values exactly equal to zero.</param>
/// <param name="ZeroRatio">Ratio of zero values to total count.</param>
/// <param name="IntegerValued">True when every observed value was a whole number (no fractional part).</param>
public sealed record DecimalNumericResult(
    long Count, decimal Min, decimal Max, decimal Mean,
    decimal Variance, decimal StandardDeviation,
    long ZeroCount, decimal ZeroRatio, bool IntegerValued)
{
    /// <summary>An empty result with zero counts.</summary>
    public static DecimalNumericResult Empty { get; } =
        new(0, 0m, 0m, 0m, 0m, 0m, 0, 0m, false);
}
