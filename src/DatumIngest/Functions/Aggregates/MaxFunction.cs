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

        public void Accumulate(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame)
        {
            DataValue value = arguments[0];
            if (value.IsNull) return;

            _inputKind = value.Kind;

            // _maximum was stabilised into frame.Target by a previous Accumulate (or is
            // null on the first call). New candidate's payload still lives in frame.Source.
            // Compare across the two stores, then stabilise the new winner into Target so
            // the captured DataValue's offsets stay valid after the input batch's arena
            // is recycled.
            if (_maximum is null
                || DataValueComparer.Compare(value, frame.Source, _maximum.Value, frame.Target) > 0)
            {
                _maximum = DataValueRetention.Stabilize(value, frame.Source, frame.Target);
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

        public DataValue Result(in InvocationFrame frame)
        {
            if (_maximum is null) return DataValue.Null(_inputKind);
            // _maximum lives in the Target arena passed during Accumulate (typically
            // context.Store). Restabilise into the emit Target so result-batch readers
            // resolve against the right arena.
            return DataValueRetention.Stabilize(_maximum.Value, frame.Source, frame.Target);
        }

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
