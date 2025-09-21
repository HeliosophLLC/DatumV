using DatumIngest.Model;

namespace DatumIngest.Functions.Aggregates;

/// <summary>
/// Implements <c>MAX(expression)</c>. Returns the maximum non-null value.
/// Works on numeric, string, date, datetime, and time types using their
/// natural ordering. Returns null if all values are null.
/// </summary>
public sealed class MaxFunction : IAggregateFunction
{
    /// <inheritdoc/>
    public string Name => "MAX";

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("MAX() requires exactly one argument.");
        }

        DataKind kind = argumentKinds[0];
        if (!DataValueComparer.IsComparable(kind))
        {
            throw new ArgumentException($"MAX() requires a comparable argument, got {kind}.");
        }

        return kind;
    }

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new MaxAccumulator();

    private sealed class MaxAccumulator : IAggregateAccumulator
    {
        private DataValue? _maximum;
        // Captured from the first non-null value so that the empty-group null
        // carries the correct input kind rather than an arbitrary default.
        private DataKind _inputKind = DataKind.Float64;

        public void Accumulate(ReadOnlySpan<DataValue> arguments)
        {
            DataValue value = arguments[0];
            if (value.IsNull) return;

            _inputKind = value.Kind;
            if (_maximum is null || CompareValues(value, _maximum.Value) > 0)
            {
                _maximum = value;
            }
        }

        /// <inheritdoc/>
        public void Merge(IAggregateAccumulator other)
        {
            MaxAccumulator otherAccumulator = (MaxAccumulator)other;

            if (otherAccumulator._maximum is null)
            {
                return;
            }

            _inputKind = otherAccumulator._inputKind;
            if (_maximum is null || CompareValues(otherAccumulator._maximum.Value, _maximum.Value) > 0)
            {
                _maximum = otherAccumulator._maximum;
            }
        }

        public DataValue Result => _maximum ?? DataValue.Null(_inputKind);

        /// <inheritdoc />
        public void Reset()
        {
            _maximum = null;
            _inputKind = DataKind.Float64;
        }

        private static int CompareValues(DataValue left, DataValue right) =>
            DataValueComparer.Compare(left, right);
    }
}
