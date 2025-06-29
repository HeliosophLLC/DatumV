namespace Axon.QueryEngine.Statistics.Accumulators;

using Axon.QueryEngine.Model;

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

    /// <inheritdoc />
    public void Add(DataValue value)
    {
        if (value.IsNull)
        {
            return;
        }

        float[]? elements = null;
        int rank = 0;
        int elementCount = 0;

        switch (value.Kind)
        {
            case DataKind.Vector:
                elements = value.AsVector();
                rank = 1;
                elementCount = elements.Length;
                break;

            case DataKind.Matrix:
                elements = value.AsMatrix(out int rows, out int cols);
                rank = 2;
                elementCount = rows * cols;
                break;

            case DataKind.Tensor:
                elements = value.AsTensor(out int[] shape);
                rank = shape.Length;
                elementCount = elements.Length;
                break;

            default:
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

        // Welford's across all elements
        bool allZero = true;

        for (int i = 0; i < elements.Length; i++)
        {
            double v = elements[i];
            _elementCount++;

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

        if (allZero)
        {
            _zeroVectorCount++;
        }
    }

    /// <inheritdoc />
    public void Merge(IStatisticAccumulator other)
    {
        if (other is not VectorStatsAccumulator otherVector || otherVector._count == 0)
        {
            return;
        }

        if (_count == 0)
        {
            _count = otherVector._count;
            _minElementCount = otherVector._minElementCount;
            _maxElementCount = otherVector._maxElementCount;
            _minRank = otherVector._minRank;
            _maxRank = otherVector._maxRank;
            _elementCount = otherVector._elementCount;
            _zeroElementCount = otherVector._zeroElementCount;
            _zeroVectorCount = otherVector._zeroVectorCount;
            _elementMin = otherVector._elementMin;
            _elementMax = otherVector._elementMax;
            _elementMean = otherVector._elementMean;
            _elementM2 = otherVector._elementM2;
            return;
        }

        _count += otherVector._count;
        _zeroElementCount += otherVector._zeroElementCount;
        _zeroVectorCount += otherVector._zeroVectorCount;
        _minElementCount = Math.Min(_minElementCount, otherVector._minElementCount);
        _maxElementCount = Math.Max(_maxElementCount, otherVector._maxElementCount);
        _minRank = Math.Min(_minRank, otherVector._minRank);
        _maxRank = Math.Max(_maxRank, otherVector._maxRank);
        _elementMin = Math.Min(_elementMin, otherVector._elementMin);
        _elementMax = Math.Max(_elementMax, otherVector._elementMax);

        // Parallel Welford merge (Chan et al.)
        long combinedCount = _elementCount + otherVector._elementCount;

        if (combinedCount > 0)
        {
            double delta = otherVector._elementMean - _elementMean;
            double combinedMean = _elementMean + delta * otherVector._elementCount / combinedCount;
            double combinedM2 = _elementM2 + otherVector._elementM2 +
                                delta * delta * _elementCount * otherVector._elementCount / combinedCount;

            _elementCount = combinedCount;
            _elementMean = combinedMean;
            _elementM2 = combinedM2;
        }
    }

    /// <inheritdoc />
    public StatisticResult GetResult()
    {
        double variance = _elementCount > 1 ? _elementM2 / _elementCount : 0.0;
        double zeroElementRatio = _elementCount > 0 ? (double)_zeroElementCount / _elementCount : 0.0;

        return new StatisticResult("vector_stats", new VectorStatsResult(
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
            _zeroVectorCount));
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
    long Count, double Min, double Max, double Mean, double Variance, double StandardDeviation);

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
public sealed record VectorStatsResult(
    long ValueCount,
    int MinElementCount,
    int MaxElementCount,
    int MinRank,
    int MaxRank,
    NumericSummary ElementStats,
    long ZeroElementCount,
    double ZeroElementRatio,
    long ZeroVectorCount);
