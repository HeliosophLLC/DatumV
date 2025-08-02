using DatumIngest.Model;

namespace DatumIngest.Functions.Aggregates;

/// <summary>
/// Implements <c>PERCENTILE_DISC(expression, fraction)</c>. Returns the value at
/// the nearest rank for the requested percentile — no interpolation is performed,
/// so the result is always an actually observed value.
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
public sealed class PercentileDiscreteFunction : IAggregateFunction
{
    /// <inheritdoc/>
    public string Name => "PERCENTILE_DISC";

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("PERCENTILE_DISC() requires exactly two arguments: expression and fraction.");
        }

        if (argumentKinds[0] is not (DataKind.Scalar or DataKind.UInt8))
        {
            throw new ArgumentException(
                $"PERCENTILE_DISC() first argument must be numeric, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] is not DataKind.Scalar)
        {
            throw new ArgumentException(
                $"PERCENTILE_DISC() second argument (fraction) must be Scalar, got {argumentKinds[1]}.");
        }

        return DataKind.Scalar;
    }

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new PercentileDiscreteAccumulator();

    /// <summary>
    /// Collects all non-null values and returns the nearest-rank value at finalization.
    /// Uses ceiling-based nearest rank: <c>index = ceil(fraction * count) - 1</c>,
    /// clamped to valid bounds.
    /// </summary>
    private sealed class PercentileDiscreteAccumulator : IAggregateAccumulator
    {
        private readonly List<float> _values = [];
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
                        $"PERCENTILE_DISC() fraction must be between 0 and 1, got {_fraction}.");
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

                // Nearest-rank method: index = ceil(fraction * count) - 1, clamped.
                int index = (int)System.Math.Ceiling(_fraction * _values.Count) - 1;
                index = System.Math.Clamp(index, 0, _values.Count - 1);

                return DataValue.FromScalar(_values[index]);
            }
        }
    }
}
