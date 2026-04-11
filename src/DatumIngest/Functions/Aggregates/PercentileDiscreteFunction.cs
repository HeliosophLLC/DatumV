using DatumIngest.Model;

namespace DatumIngest.Functions.Aggregates;

/// <summary>
/// Implements <c>PERCENTILE_DISC(expression, fraction)</c> and the ordered-set form
/// <c>PERCENTILE_DISC(fraction) WITHIN GROUP (ORDER BY expression)</c>.
/// Returns the nearest-rank observed value for the requested percentile — no
/// interpolation is performed.
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
public sealed class PercentileDiscreteFunction : IAggregateFunction
{
    /// <inheritdoc/>
    public string Name => "PERCENTILE_DISC";

    /// <inheritdoc/>
    public WithinGroupSemantics WithinGroupSemantics => WithinGroupSemantics.OrderedSet;

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("PERCENTILE_DISC() requires exactly two arguments: expression and fraction.");
        }

        if (!IsNumericKind(argumentKinds[0]))
        {
            throw new ArgumentException(
                $"PERCENTILE_DISC() first argument must be numeric, got {argumentKinds[0]}.");
        }

        if (argumentKinds[1] is not (DataKind.Float32 or DataKind.Float64))
        {
            throw new ArgumentException(
                $"PERCENTILE_DISC() fraction must be Float32 or Float64, got {argumentKinds[1]}.");
        }

        return DataKind.Float64;
    }

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new PercentileDiscreteAccumulator();

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="kind"/> is any fixed-width numeric kind.
    /// </summary>
    internal static bool IsNumericKind(DataKind kind) => kind is
        DataKind.Float32 or DataKind.Float64 or
        DataKind.UInt8 or DataKind.Int8 or
        DataKind.Int16 or DataKind.UInt16 or
        DataKind.Int32 or DataKind.UInt32 or
        DataKind.Int64 or DataKind.UInt64;

    internal static double ToDouble(DataValue value) => value.ToDouble();

    /// <summary>
    /// Collects all non-null values and returns the nearest-rank value at finalization.
    /// Uses ceiling-based nearest rank: <c>index = ceil(fraction * count) - 1</c>,
    /// clamped to valid bounds.
    /// </summary>
    private sealed class PercentileDiscreteAccumulator : IAggregateAccumulator
    {
        private readonly List<double> _values = [];
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
                        $"PERCENTILE_DISC() fraction must be between 0 and 1, got {_fraction}.");
                }

                _fractionCaptured = true;
            }

            if (arguments[0].IsNull) return;

            _values.Add(ToDouble(arguments[0]));
        }

        /// <inheritdoc/>
        public ValueTask MergeAsync(IAggregateAccumulator other, InvocationFrame frame)
        {
            PercentileDiscreteAccumulator otherAccumulator = (PercentileDiscreteAccumulator)other;
            _values.AddRange(otherAccumulator._values);

            if (!_fractionCaptured && otherAccumulator._fractionCaptured)
            {
                _fraction = otherAccumulator._fraction;
                _fractionCaptured = true;
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask<DataValue> ResultAsync(InvocationFrame frame)
        {
            if (_values.Count == 0)
            {
                return new(DataValue.Null(DataKind.Float64));
            }

            _values.Sort();

            // Nearest-rank method: index = ceil(fraction * count) - 1, clamped.
            int index = (int)System.Math.Ceiling(_fraction * _values.Count) - 1;
            index = System.Math.Clamp(index, 0, _values.Count - 1);

            return new(DataValue.FromFloat64(_values[index]));
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
