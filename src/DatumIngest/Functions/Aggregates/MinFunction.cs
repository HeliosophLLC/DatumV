using DatumIngest.Model;

namespace DatumIngest.Functions.Aggregates;

/// <summary>
/// Implements <c>MIN(expression)</c>. Returns the minimum non-null value.
/// Works on numeric, string, date, datetime, and time types using their
/// natural ordering. Returns null if all values are null.
/// </summary>
public sealed class MinFunction : IAggregateFunction
{
    /// <inheritdoc/>
    public string Name => "MIN";

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("MIN() requires exactly one argument.");
        }

        DataKind kind = argumentKinds[0];
        if (!DataValueComparer.IsComparable(kind))
        {
            throw new ArgumentException($"MIN() requires a comparable argument, got {kind}.");
        }

        return kind;
    }

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new MinAccumulator();

    private sealed class MinAccumulator : IAggregateAccumulator
    {
        private DataValue? _minimum;
        // Captured from the first non-null value so that the empty-group null
        // carries the correct input kind rather than an arbitrary default.
        private DataKind _inputKind = DataKind.Float64;

        public void Accumulate(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame)
        {
            DataValue value = arguments[0];
            if (value.IsNull) return;

            _inputKind = value.Kind;
            if (_minimum is null || CompareValues(value, _minimum.Value) < 0)
            {
                _minimum = value;
            }
        }

        /// <inheritdoc/>
        public void Merge(IAggregateAccumulator other)
        {
            MinAccumulator otherAccumulator = (MinAccumulator)other;

            if (otherAccumulator._minimum is null)
            {
                return;
            }

            _inputKind = otherAccumulator._inputKind;
            if (_minimum is null || CompareValues(otherAccumulator._minimum.Value, _minimum.Value) < 0)
            {
                _minimum = otherAccumulator._minimum;
            }
        }

        public DataValue Result(in InvocationFrame frame) => _minimum ?? DataValue.Null(_inputKind);

        /// <inheritdoc />
        public void Reset()
        {
            _minimum = null;
            _inputKind = DataKind.Float64;
        }

        private static int CompareValues(DataValue left, DataValue right) =>
            DataValueComparer.Compare(left, right);
    }
}
