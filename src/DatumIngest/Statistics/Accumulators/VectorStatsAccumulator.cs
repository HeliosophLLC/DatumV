namespace DatumIngest.Statistics.Accumulators;

using DatumIngest.Model;

/// <summary>
/// Accumulates aggregate element-wise statistics for vector, matrix, and tensor columns.
/// Tracks element count ranges (min/max length) and runs Welford's algorithm across
/// all scalar elements of every value, producing an overall <see cref="NumericSummary"/>.
/// Also tracks shape range (min/max rank and per-dimension extents).
/// </summary>
public sealed class VectorStatsAccumulator : IStatisticAccumulator
{
    private long _count;
    private int _minElementCount = int.MaxValue;
    private int _maxElementCount = int.MinValue;
    private int _minRank = int.MaxValue;
    private int _maxRank = int.MinValue;

    // Welford accumulators for element-level stats
    private long _elementCount;
    private long _zeroElementCount;
    private long _zeroVectorCount;
    private double _elementMin = double.PositiveInfinity;
    private double _elementMax = double.NegativeInfinity;
    private double _elementMean;
    private double _elementM2;

    // L2 norm tracking across vectors
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
        int rank = 0;
        int elementCount = 0;

        // Float32 + IsArray (formerly DataKind.Vector). The accumulator is opted
        // in by StatisticsCollector only for this kind/flag combination.
        if (value.Kind == DataKind.Float32 && value.IsArray)
        {
            elements = value.AsArraySpan<float>(store);
            rank = 1;
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

        if (rank < _minRank)
        {
            _minRank = rank;
        }

        if (rank > _maxRank)
        {
            _maxRank = rank;
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

        // L2 norm for this vector/matrix/tensor
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
            _zeroVectorCount++;
        }
    }

    /// <inheritdoc />
    public IEnumerable<StatisticResult> GetResults()
    {
        double variance = _elementCount > 1 ? _elementM2 / _elementCount : 0.0;
        double zeroElementRatio = _elementCount > 0 ? (double)_zeroElementCount / _elementCount : 0.0;

        yield return new StatisticResult("vector_stats", new VectorStatsResult(
            _count,
            _count > 0 ? _minElementCount : 0,
            _count > 0 ? _maxElementCount : 0,
            _count > 0 ? _minRank : 0,
            _count > 0 ? _maxRank : 0,
            new NumericSummary(
                _elementCount,
                _elementCount > 0 ? _elementMin : double.NaN,
                _elementCount > 0 ? _elementMax : double.NaN,
                _elementCount > 0 ? _elementMean : double.NaN,
                variance,
                Math.Sqrt(variance)),
            _zeroElementCount,
            zeroElementRatio,
            _zeroVectorCount,
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
/// Contains vector/matrix/tensor statistics.
/// </summary>
/// <param name="ValueCount">Number of vector/matrix/tensor values observed.</param>
/// <param name="MinElementCount">Fewest elements in any single value.</param>
/// <param name="MaxElementCount">Most elements in any single value.</param>
/// <param name="MinRank">Minimum rank (dimensionality) observed.</param>
/// <param name="MaxRank">Maximum rank (dimensionality) observed.</param>
/// <param name="ElementStats">Aggregate numeric statistics across all elements of all values.</param>
/// <param name="ZeroElementCount">Total number of elements exactly equal to zero.</param>
/// <param name="ZeroElementRatio">Ratio of zero elements to total element count.</param>
/// <param name="ZeroVectorCount">Number of values where every element is zero.</param>
/// <param name="NormMin">Minimum L2 norm across all values.</param>
/// <param name="NormMax">Maximum L2 norm across all values.</param>
/// <param name="NormMean">Mean L2 norm across all values.</param>
public sealed record VectorStatsResult(
    long ValueCount,
    int MinElementCount,
    int MaxElementCount,
    int MinRank,
    int MaxRank,
    NumericSummary ElementStats,
    long ZeroElementCount,
    double ZeroElementRatio,
    long ZeroVectorCount,
    double NormMin,
    double NormMax,
    double NormMean)
{
    /// <summary>An empty result with zero counts and NaN for all numeric summaries.</summary>
    public static VectorStatsResult Empty { get; } = new(
        0, 0, 0, 0, 0,
        NumericSummary.Empty,
        0, 0, 0, double.NaN, double.NaN, double.NaN);
}
