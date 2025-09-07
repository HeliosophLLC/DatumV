using DatumIngest.Model;

namespace DatumIngest.Functions.Aggregates;

/// <summary>
/// Implements <c>PERCENTILE_CONT(expression, fraction)</c> and the ordered-set form
/// <c>PERCENTILE_CONT(fraction) WITHIN GROUP (ORDER BY expression)</c>.
/// Computes an arbitrary percentile using SQL-standard linear interpolation.
/// <para>
/// Arguments: first is the column expression (any numeric kind), second is the
/// percentile fraction (Float32 or Float64, in [0, 1]). The fraction must be
/// constant across all rows in a group — the value from the first accumulated
/// row is used.
/// </para>
/// <para>
/// Memory: O(N) per group — all non-null values are collected before the percentile
/// is computed. Returns <see cref="DataKind.Float64"/>.
/// </para>
/// </summary>
public sealed class PercentileContinuousFunction : IAggregateFunction
{
    /// <inheritdoc/>
    public string Name => "PERCENTILE_CONT";

    /// <inheritdoc/>
    // O(N) memory accumulation and O(N log N) sort at finalization — Tier 2.
    public int QueryUnitCost => 2;

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("PERCENTILE_CONT() requires exactly two arguments: expression and fraction.");
        }

        if (!PercentileDiscreteFunction.IsNumericKind(argumentKinds[0]))
        {
            throw new ArgumentException(
                $"PERCENTILE_CONT() first argument must be numeric, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] is not (DataKind.Float32 or DataKind.Float64))
        {
            throw new ArgumentException(
                $"PERCENTILE_CONT() fraction must be Float32 or Float64, got {argumentKinds[1]}.");
        }

        return DataKind.Float64;
    }

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new PercentileContinuousAccumulator();

    /// <summary>
    /// Collects all non-null values and computes the percentile at finalization
    /// using SQL-standard linear interpolation between adjacent values.
    /// </summary>
    private sealed class PercentileContinuousAccumulator : IAggregateAccumulator
    {
        private readonly List<double> _values = [];
        private double _fraction;
        private bool _fractionCaptured;

        public void Accumulate(ReadOnlySpan<DataValue> arguments)
        {
            // Capture the fraction from the first non-null invocation.
            if (!_fractionCaptured && !arguments[1].IsNull)
            {
                _fraction = arguments[1].Kind == DataKind.Float64
                    ? arguments[1].AsFloat64()
                    : arguments[1].AsFloat32();

                if (_fraction < 0.0 || _fraction > 1.0)
                {
                    throw new ArgumentException(
                        $"PERCENTILE_CONT() fraction must be between 0 and 1, got {_fraction}.");
                }

                _fractionCaptured = true;
            }

            if (arguments[0].IsNull) return;

            _values.Add(PercentileDiscreteFunction.ToDouble(arguments[0]));
        }

        /// <inheritdoc/>
        public void Merge(IAggregateAccumulator other)
        {
            PercentileContinuousAccumulator otherAccumulator = (PercentileContinuousAccumulator)other;
            _values.AddRange(otherAccumulator._values);

            if (!_fractionCaptured && otherAccumulator._fractionCaptured)
            {
                _fraction = otherAccumulator._fraction;
                _fractionCaptured = true;
            }
        }

        public DataValue Result
        {
            get
            {
                if (_values.Count == 0)
                {
                    return DataValue.Null(DataKind.Float64);
                }

                _values.Sort();

                // SQL-standard continuous percentile interpolation.
                double row = _fraction * (_values.Count - 1);
                int lower = (int)System.Math.Floor(row);
                int upper = (int)System.Math.Ceiling(row);

                if (lower == upper)
                {
                    return DataValue.FromFloat64(_values[lower]);
                }

                double interpolated = _values[lower] + (_values[upper] - _values[lower]) * (row - lower);
                return DataValue.FromFloat64(interpolated);
            }
        }

        /// <inheritdoc />
        public void Reset()
        {
            _values.Clear();
            _fraction = 0.0;
            _fractionCaptured = false;
        }
    }
}
