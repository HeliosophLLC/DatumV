namespace Heliosoph.DatumV.Statistics.Accumulators;

using System.Numerics;
using Heliosoph.DatumV.Model;

/// <summary>
/// Accumulates aggregate element-wise statistics for typed-array columns. Dispatches
/// on the value's <see cref="DataKind"/> + <see cref="DataValue.IsArray"/> flag and
/// runs Welford's algorithm across all scalar elements via the generic
/// <see cref="INumber{TSelf}"/> path; element values are widened to <see cref="double"/>
/// for the running mean/variance and L2-norm so a single accumulator covers every
/// supported element kind.
/// </summary>
/// <remarks>
/// <para>
/// Supported element kinds: Float16 / Float32 / Float64 and the signed/unsigned
/// integer family (Int8 — Int64, UInt16 — UInt64). UInt8+IsArray is intentionally
/// excluded — that storage shape is the byte-blob path and is handled by
/// <see cref="BinarySizeAccumulator"/>; even though byte arrays are technically
/// numeric, treating them as binary blobs matches the SQL idiom and avoids the
/// nonsense of "the mean byte value of an image is 127.4."
/// </para>
/// <para>
/// Excluded element kinds: <see cref="DataKind.Decimal"/> (would lose precision
/// when widening to double — a dedicated decimal-array accumulator is a follow-up),
/// <see cref="DataKind.Int128"/> / <see cref="DataKind.UInt128"/> (no array
/// payload support today), and <see cref="DataKind.Boolean"/> (boolean arrays
/// are unusual; if a use case lands, count true/false in a dedicated path).
/// </para>
/// </remarks>
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
        if (value.IsNull || !value.IsArray)
        {
            return;
        }

        // Byte arrays (UInt8+IsArray) are routed to BinarySizeAccumulator —
        // the StatisticsCollector gate excludes them before this accumulator
        // ever sees them. Defensive guard here keeps the behaviour explicit
        // if the gating shifts in the future.
        if (value.IsByteArrayKind)
        {
            return;
        }

        // Dispatch reads the appropriate typed span and runs Welford + L2-norm
        // through the INumber<T> generic helper; double-widening happens once
        // per element via Convert.ToDouble (a no-op for Float64, a cheap
        // conversion for narrower kinds). Decimal/Int128/UInt128/Boolean are
        // not in the supported set — see class doc.
        switch (value.Kind)
        {
            case DataKind.Float16:
                AccumulateElements(value.AsArraySpan<Half>(store));
                break;
            case DataKind.Float32:
                AccumulateElements(value.AsArraySpan<float>(store));
                break;
            case DataKind.Float64:
                AccumulateElements(value.AsArraySpan<double>(store));
                break;
            case DataKind.Int8:
                AccumulateElements(value.AsArraySpan<sbyte>(store));
                break;
            case DataKind.Int16:
                AccumulateElements(value.AsArraySpan<short>(store));
                break;
            case DataKind.Int32:
                AccumulateElements(value.AsArraySpan<int>(store));
                break;
            case DataKind.Int64:
                AccumulateElements(value.AsArraySpan<long>(store));
                break;
            case DataKind.UInt16:
                AccumulateElements(value.AsArraySpan<ushort>(store));
                break;
            case DataKind.UInt32:
                AccumulateElements(value.AsArraySpan<uint>(store));
                break;
            case DataKind.UInt64:
                AccumulateElements(value.AsArraySpan<ulong>(store));
                break;
            default:
                return;
        }
    }

    private void AccumulateElements<T>(ReadOnlySpan<T> elements)
        where T : unmanaged, INumber<T>
    {
        _count++;

        int elementCount = elements.Length;
        if (elementCount < _minElementCount) _minElementCount = elementCount;
        if (elementCount > _maxElementCount) _maxElementCount = elementCount;

        bool allZero = true;
        double sumOfSquares = 0.0;

        for (int i = 0; i < elements.Length; i++)
        {
            // double.CreateChecked widens T → double for any INumber<T>; on
            // Float64 it's a no-op, on Float16/integers it's a fast conversion.
            // Throws on overflow, which can't happen here — every supported
            // element kind fits in double's range (Float16/32/64 directly,
            // integers up to UInt64 max ≈ 1.8e19 fits in double's 53-bit
            // mantissa range with rounding past 2^53 — same caveat the scalar
            // NumericAccumulator already accepts).
            double v = double.CreateChecked(elements[i]);
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

            if (v < _elementMin) _elementMin = v;
            if (v > _elementMax) _elementMax = v;

            double delta = v - _elementMean;
            _elementMean += delta / _elementCount;
            double delta2 = v - _elementMean;
            _elementM2 += delta * delta2;
        }

        double norm = Math.Sqrt(sumOfSquares);

        if (norm < _normMin) _normMin = norm;
        if (norm > _normMax) _normMax = norm;
        _normMean += (norm - _normMean) / _count;

        if (allZero) _zeroArrayCount++;
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
/// Contains typed-array statistics for any supported numeric element kind.
/// See <see cref="ArrayStatsAccumulator"/> for the element-kind support matrix.
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
