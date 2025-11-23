namespace DatumIngest.Statistics.Accumulators;

using DatumIngest.Model;

/// <summary>
/// Accumulates aggregate element-wise statistics for typed-array columns. Today
/// dispatches only on <see cref="DataKind.Float32"/> + <see cref="DataValue.IsArray"/>
/// (the former Vector kind); the Welford / L2-norm machinery is element-kind-generic
/// so other numeric arrays (Float64, Int*) can plug in as their dispatch lands.
/// Tracks per-array element-count ranges (min/max length) and runs Welford's
/// algorithm across all scalar elements of every value, producing an overall
/// <see cref="NumericSummary"/>.
/// </summary>
public sealed class ArrayStatsAccumulator : IStatisticAccumulator
{
    private long _count;
    private int _minElementCount = int.MaxValue;
    private int _maxElementCount = int.MinValue;

    // Welford accumulators for element-level stats
    private long _elementCount;
    private long _zeroElementCount;
    private long _zeroArrayCount;
    private double _elementMin = double.PositiveInfinity;
    private double _elementMax = double.NegativeInfinity;
    private double _elementMean;
    private double _elementM2;

    // L2 norm tracking across arrays
    private double _normMin = double.PositiveInfinity;
    private double _normMax = double.NegativeInfinity;
    private double _normMean;

    /// <inheritdoc />
    public void Add(DataValue value, IValueStore store)
    {
        if (value.IsNull)
        {
            return;
        }

        ReadOnlySpan<float> elements;
        int elementCount;

        // Float32 + IsArray (formerly DataKind.Vector). Other element kinds
        // are demand-pulled — StatisticsCollector only opts this accumulator
        // in for the Float32+IsArray combination today.
        if (value.Kind == DataKind.Float32 && value.IsArray)
        {
            elements = value.AsArraySpan<float>(store);
            elementCount = elements.Length;
        }
        else
        {
            return;
        }

        _count++;

        if (elementCount < _minElementCount)
        {
            _minElementCount = elementCount;
        }

        if (elementCount > _maxElementCount)
        {
            _maxElementCount = elementCount;
        }

        // Welford's across all elements + sum-of-squares for L2 norm
        bool allZero = true;
        double sumOfSquares = 0.0;

        for (int i = 0; i < elements.Length; i++)
        {
            double v = elements[i];
            _elementCount++;
            sumOfSquares += v * v;

            if (v == 0.0)
            {
                _zeroElementCount++;
            }
            else
            {
                allZero = false;
            }

            if (v < _elementMin)
            {
                _elementMin = v;
            }

            if (v > _elementMax)
            {
                _elementMax = v;
            }

            double delta = v - _elementMean;
            _elementMean += delta / _elementCount;
            double delta2 = v - _elementMean;
            _elementM2 += delta * delta2;
        }

        // L2 norm for this array
        double norm = Math.Sqrt(sumOfSquares);

        if (norm < _normMin)
        {
            _normMin = norm;
        }

        if (norm > _normMax)
        {
            _normMax = norm;
        }

        // Incremental mean of norms
        _normMean += (norm - _normMean) / _count;

        if (allZero)
        {
            _zeroArrayCount++;
        }
    }

    /// <inheritdoc />
    public IEnumerable<StatisticResult> GetResults()
    {
        double variance = _elementCount > 1 ? _elementM2 / _elementCount : 0.0;
        double zeroElementRatio = _elementCount > 0 ? (double)_zeroElementCount / _elementCount : 0.0;

        yield return new StatisticResult("array_stats", new ArrayStatsResult(
            _count,
            _count > 0 ? _minElementCount : 0,
            _count > 0 ? _maxElementCount : 0,
            new NumericSummary(
                _elementCount,
                _elementCount > 0 ? _elementMin : double.NaN,
                _elementCount > 0 ? _elementMax : double.NaN,
                _elementCount > 0 ? _elementMean : double.NaN,
                variance,
                Math.Sqrt(variance)),
            _zeroElementCount,
            zeroElementRatio,
            _zeroArrayCount,
            _count > 0 ? _normMin : double.NaN,
            _count > 0 ? _normMax : double.NaN,
            _count > 0 ? _normMean : double.NaN));
    }
}

/// <summary>
/// Aggregate element-wise statistics for a numeric collection.
/// </summary>
/// <param name="Count">Number of scalar elements processed.</param>
/// <param name="Min">Minimum element value.</param>
/// <param name="Max">Maximum element value.</param>
/// <param name="Mean">Arithmetic mean of all elements.</param>
/// <param name="Variance">Population variance.</param>
/// <param name="StandardDeviation">Population standard deviation.</param>
public sealed record NumericSummary(
    long Count, double Min, double Max, double Mean, double Variance, double StandardDeviation)
{
    /// <summary>An empty summary with zero count and NaN for all numeric fields.</summary>
    public static NumericSummary Empty { get; } = new(0, double.NaN, double.NaN, double.NaN, 0, 0);
}

/// <summary>
/// Contains typed-array (Float32 + IsArray, formerly Vector) statistics.
/// </summary>
/// <param name="ValueCount">Number of array values observed.</param>
/// <param name="MinElementCount">Fewest elements in any single array.</param>
/// <param name="MaxElementCount">Most elements in any single array.</param>
/// <param name="ElementStats">Aggregate numeric statistics across all elements of all arrays.</param>
/// <param name="ZeroElementCount">Total number of elements exactly equal to zero.</param>
/// <param name="ZeroElementRatio">Ratio of zero elements to total element count.</param>
/// <param name="ZeroArrayCount">Number of arrays where every element is zero.</param>
/// <param name="NormMin">Minimum L2 norm across all arrays.</param>
/// <param name="NormMax">Maximum L2 norm across all arrays.</param>
/// <param name="NormMean">Mean L2 norm across all arrays.</param>
public sealed record ArrayStatsResult(
    long ValueCount,
    int MinElementCount,
    int MaxElementCount,
    NumericSummary ElementStats,
    long ZeroElementCount,
    double ZeroElementRatio,
    long ZeroArrayCount,
    double NormMin,
    double NormMax,
    double NormMean)
{
    /// <summary>An empty result with zero counts and NaN for all numeric summaries.</summary>
    public static ArrayStatsResult Empty { get; } = new(
        0, 0, 0,
        NumericSummary.Empty,
        0, 0, 0, double.NaN, double.NaN, double.NaN);
}
