using DatumIngest.Model;

namespace DatumIngest.Functions.Aggregates;

/// <summary>
/// Implements <c>SUM(expression)</c>. Computes the sum of all non-null numeric
/// values. Returns null if all values are null.
/// </summary>
public sealed class SumFunction : IAggregateFunction
{
    /// <inheritdoc/>
    public string Name => "SUM";

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("SUM() requires exactly one argument.");
        }

        if (argumentKinds[0] is not (DataKind.Scalar or DataKind.UInt8))
        {
            throw new ArgumentException($"SUM() requires a numeric argument, got {argumentKinds[0]}.");
        }

        return DataKind.Scalar;
    }

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new SumAccumulator();

    private sealed class SumAccumulator : IAggregateAccumulator
    {
        private double _sum;
        private bool _hasValue;

        public void Accumulate(ReadOnlySpan<DataValue> arguments)
        {
            if (arguments[0].IsNull) return;

            _sum += arguments[0].AsScalar();
            _hasValue = true;
        }

        public DataValue Result => _hasValue
            ? DataValue.FromScalar((float)_sum)
            : DataValue.Null(DataKind.Scalar);
    }
}
