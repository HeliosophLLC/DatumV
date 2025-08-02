using DatumIngest.Model;

namespace DatumIngest.Functions.Aggregates;

/// <summary>
/// Implements <c>APPROX_PERCENTILE(expression, fraction)</c>. Computes an
/// approximate percentile using Algorithm R reservoir sampling with a
/// configurable maximum sample size. For groups smaller than the reservoir
/// cap, the result is exact; for larger groups, the error is typically 1–5%.
/// <para>
/// Arguments: first is the column expression (Scalar/UInt8), second is the
/// percentile fraction (Scalar in [0, 1]). The fraction must be constant
/// across all rows in a group — the value from the first accumulated row
/// is used.
/// </para>
/// <para>
/// This provides O(1) memory per group (bounded by <see cref="MaxSamples"/>)
/// regardless of group size, compared to the exact <c>PERCENTILE_CONT</c>
/// which is O(N).
/// </para>
/// </summary>
public sealed class ApproximatePercentileFunction : IAggregateFunction
{
    /// <summary>Maximum samples retained in the reservoir.</summary>
    internal const int MaxSamples = 100_000;

    /// <inheritdoc/>
    public string Name => "APPROX_PERCENTILE";

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException(
                "APPROX_PERCENTILE() requires exactly two arguments: expression and fraction.");
        }

        if (argumentKinds[0] is not (DataKind.Scalar or DataKind.UInt8))
        {
            throw new ArgumentException(
                $"APPROX_PERCENTILE() first argument must be numeric, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] is not DataKind.Scalar)
        {
            throw new ArgumentException(
                $"APPROX_PERCENTILE() second argument (fraction) must be Scalar, got {argumentKinds[1]}.");
        }

        return DataKind.Scalar;
    }

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new ReservoirPercentileAccumulator();

    /// <summary>
    /// Algorithm R reservoir sampling accumulator for approximate percentile
    /// computation. Retains up to <see cref="MaxSamples"/> values and computes
    /// the percentile via linear interpolation at finalization.
    /// </summary>
    private sealed class ReservoirPercentileAccumulator : IAggregateAccumulator
    {
        private readonly List<float> _samples = [];
        private readonly Random _random = new(42);
        private long _totalCount;
        private float _fraction;
        private bool _fractionCaptured;

        public void Accumulate(ReadOnlySpan<DataValue> arguments)
        {
            if (!_fractionCaptured && !arguments[1].IsNull)
            {
                _fraction = arguments[1].AsScalar();

                if (_fraction < 0f || _fraction > 1f)
                {
                    throw new ArgumentException(
                        $"APPROX_PERCENTILE() fraction must be between 0 and 1, got {_fraction}.");
                }

                _fractionCaptured = true;
            }

            if (arguments[0].IsNull) return;

            float value = arguments[0].AsScalar();
            _totalCount++;

            if (_samples.Count < MaxSamples)
            {
                _samples.Add(value);
            }
            else
            {
                long j = _random.NextInt64(_totalCount);

                if (j < MaxSamples)
                {
                    _samples[(int)j] = value;
                }
            }
        }

        public DataValue Result
        {
            get
            {
                if (_samples.Count == 0)
                {
                    return DataValue.Null(DataKind.Scalar);
                }

                _samples.Sort();

                double row = _fraction * (_samples.Count - 1);
                int lower = (int)System.Math.Floor(row);
                int upper = (int)System.Math.Ceiling(row);

                if (lower == upper)
                {
                    return DataValue.FromScalar(_samples[lower]);
                }

                double interpolated = _samples[lower] + (_samples[upper] - _samples[lower]) * (row - lower);
                return DataValue.FromScalar((float)interpolated);
            }
        }
    }
}
