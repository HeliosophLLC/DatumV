using DatumIngest.Model;

namespace DatumIngest.Functions.Aggregates;

/// <summary>
/// Implements <c>APPROX_PERCENTILE(expression, fraction)</c>. Computes an
/// approximate percentile using Algorithm R reservoir sampling with a
/// configurable maximum sample size. For groups smaller than the reservoir
/// cap, the result is exact; for larger groups, the error is typically 1–5%.
/// <para>
/// Arguments: first is the column expression (any numeric kind), second is the
/// percentile fraction (Float32 or Float64, in [0, 1]). The fraction must be
/// constant across all rows in a group — the value from the first accumulated
/// row is used.
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
    // Reservoir sampling up to 100K samples with O(N log N) sort at finalization — Tier 2.
    public int QueryUnitCost => 2;

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException(
                "APPROX_PERCENTILE() requires exactly two arguments: expression and fraction.");
        }

        if (!PercentileDiscreteFunction.IsNumericKind(argumentKinds[0]))
        {
            throw new ArgumentException(
                $"APPROX_PERCENTILE() first argument must be numeric, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] is not (DataKind.Float32 or DataKind.Float64))
        {
            throw new ArgumentException(
                $"APPROX_PERCENTILE() second argument (fraction) must be Float32 or Float64, got {argumentKinds[1]}.");
        }

        return DataKind.Float64;
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
        private readonly List<double> _samples = [];
        private readonly Random _random = new(42);
        private long _totalCount;
        private double _fraction;
        private bool _fractionCaptured;

        public void Accumulate(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame)
        {
            if (!_fractionCaptured && !arguments[1].IsNull)
            {
                _fraction = arguments[1].Kind == DataKind.Float64
                    ? arguments[1].AsFloat64()
                    : arguments[1].AsFloat32();

                if (_fraction < 0.0 || _fraction > 1.0)
                {
                    throw new ArgumentException(
                        $"APPROX_PERCENTILE() fraction must be between 0 and 1, got {_fraction}.");
                }

                _fractionCaptured = true;
            }

            if (arguments[0].IsNull) return;

            double value = PercentileDiscreteFunction.ToDouble(arguments[0]);
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

        /// <inheritdoc/>
        public void Merge(IAggregateAccumulator other, in InvocationFrame frame)
        {
            ReservoirPercentileAccumulator otherAccumulator = (ReservoirPercentileAccumulator)other;
            _totalCount += otherAccumulator._totalCount;
            _samples.AddRange(otherAccumulator._samples);

            if (!_fractionCaptured && otherAccumulator._fractionCaptured)
            {
                _fraction = otherAccumulator._fraction;
                _fractionCaptured = true;
            }

            if (_samples.Count > MaxSamples)
            {
                // Shuffle and truncate to maintain reservoir invariant.
                for (int i = _samples.Count - 1; i > 0; i--)
                {
                    int j = _random.Next(i + 1);
                    (_samples[i], _samples[j]) = (_samples[j], _samples[i]);
                }

                _samples.RemoveRange(MaxSamples, _samples.Count - MaxSamples);
            }
        }

        public DataValue Result(in InvocationFrame frame)
        {
            if (_samples.Count == 0)
            {
                return DataValue.Null(DataKind.Float64);
            }

            _samples.Sort();

            double row = _fraction * (_samples.Count - 1);
            int lower = (int)System.Math.Floor(row);
            int upper = (int)System.Math.Ceiling(row);

            if (lower == upper)
            {
                return DataValue.FromFloat64(_samples[lower]);
            }

            double interpolated = _samples[lower] + (_samples[upper] - _samples[lower]) * (row - lower);
            return DataValue.FromFloat64(interpolated);
        }

        /// <inheritdoc />
        public void Reset()
        {
            _samples.Clear();
            _totalCount = 0;
            _fraction = 0;
            _fractionCaptured = false;
        }
    }
}
