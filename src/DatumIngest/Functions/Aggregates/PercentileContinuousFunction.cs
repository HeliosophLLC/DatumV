using DatumIngest.Model;

namespace DatumIngest.Functions.Aggregates;

/// <summary>
/// Implements <c>PERCENTILE_CONT(expression, fraction)</c>. Computes an arbitrary
/// percentile of all non-null numeric values using SQL-standard linear interpolation.
/// <para>
/// Arguments: first is the column expression (Scalar/UInt8), second is the percentile
/// fraction (Scalar in [0, 1]). The fraction must be constant across all rows in a
/// group — the value from the first accumulated row is used.
/// </para>
/// <para>
/// Memory: O(N) per group — all non-null values are collected before the percentile
/// is computed.
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

        if (argumentKinds[0] is not (DataKind.Scalar or DataKind.UInt8))
        {
            throw new ArgumentException(
                $"PERCENTILE_CONT() first argument must be numeric, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] is not DataKind.Scalar)
        {
            throw new ArgumentException(
                $"PERCENTILE_CONT() second argument (fraction) must be Scalar, got {argumentKinds[1]}.");
        }

        return DataKind.Scalar;
    }

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new PercentileContinuousAccumulator();

    /// <summary>
    /// Collects all non-null values and computes the percentile at finalization
    /// using SQL-standard linear interpolation between adjacent values.
    /// </summary>
    private sealed class PercentileContinuousAccumulator : IAggregateAccumulator
    {
        private readonly List<float> _values = [];
        private float _fraction;
        private bool _fractionCaptured;

        public void Accumulate(ReadOnlySpan<DataValue> arguments)
        {
            // Capture the fraction from the first non-null invocation.
            if (!_fractionCaptured && !arguments[1].IsNull)
            {
                _fraction = arguments[1].AsScalar();

                if (_fraction < 0f || _fraction > 1f)
                {
                    throw new ArgumentException(
                        $"PERCENTILE_CONT() fraction must be between 0 and 1, got {_fraction}.");
                }

                _fractionCaptured = true;
            }

            if (arguments[0].IsNull) return;

            _values.Add(arguments[0].AsScalar());
        }

        public DataValue Result
        {
            get
            {
                if (_values.Count == 0)
                {
                    return DataValue.Null(DataKind.Scalar);
                }

                _values.Sort();

                // SQL-standard continuous percentile interpolation.
                double row = _fraction * (_values.Count - 1);
                int lower = (int)System.Math.Floor(row);
                int upper = (int)System.Math.Ceiling(row);

                if (lower == upper)
                {
                    return DataValue.FromScalar(_values[lower]);
                }

                double interpolated = _values[lower] + (_values[upper] - _values[lower]) * (row - lower);
                return DataValue.FromScalar((float)interpolated);
            }
        }
    }
}
