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

            // _minimum was stabilised into frame.Target by a previous Accumulate (or is
            // null on the first call). New candidate's payload still lives in frame.Source.
            // Compare across the two stores, then stabilise the new winner into Target so
            // the captured DataValue's offsets stay valid after the input batch's arena
            // is recycled.
            if (_minimum is null
                || DataValueComparer.Compare(value, frame.Source, _minimum.Value, frame.Target) < 0)
            {
                _minimum = DataValueRetention.Stabilize(value, frame.Source, frame.Target);
            }
        }

        /// <inheritdoc/>
        public ValueTask MergeAsync(IAggregateAccumulator other, InvocationFrame frame)
        {
            MinAccumulator otherAccumulator = (MinAccumulator)other;

            if (otherAccumulator._minimum is null)
            {
                return ValueTask.CompletedTask;
            }

            _inputKind = otherAccumulator._inputKind;
            // Both sides' captured values were Stabilized into the same Target store
            // during their Accumulate calls (per the parallel-aggregate contract:
            // workers share context.Store). Compare against that shared store.
            if (_minimum is null
                || DataValueComparer.Compare(otherAccumulator._minimum.Value, _minimum.Value, frame.Target) < 0)
            {
                _minimum = otherAccumulator._minimum;
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask<DataValue> ResultAsync(InvocationFrame frame)
        {
            if (_minimum is null) return new(DataValue.Null(_inputKind));
            // _minimum lives in the Target arena passed during Accumulate (typically
            // context.Store). Restabilise into the emit Target so result-batch readers
            // resolve against the right arena.
            return new(DataValueRetention.Stabilize(_minimum.Value, frame.Source, frame.Target));
        }

        /// <inheritdoc />
        public void Reset()
        {
            _minimum = null;
            _inputKind = DataKind.Float64;
        }
    }
}
