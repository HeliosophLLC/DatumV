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
        bool isComparable = kind is DataKind.Int8 or DataKind.Int16 or DataKind.UInt8 or DataKind.UInt16
            or DataKind.Int32 or DataKind.UInt32 or DataKind.Int64 or DataKind.UInt64
            or DataKind.Float32 or DataKind.Float64
            or DataKind.String or DataKind.Date or DataKind.DateTime or DataKind.Time;
        if (!isComparable)
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

        private static int CompareValues(DataValue left, DataValue right)
        {
            return left.Kind switch
            {
                DataKind.Int8 => left.AsInt8().CompareTo(right.AsInt8()),
                DataKind.Int16 => left.AsInt16().CompareTo(right.AsInt16()),
                DataKind.UInt8 => left.AsUInt8().CompareTo(right.AsUInt8()),
                DataKind.UInt16 => left.AsUInt16().CompareTo(right.AsUInt16()),
                DataKind.Int32 => left.AsInt32().CompareTo(right.AsInt32()),
                DataKind.UInt32 => left.AsUInt32().CompareTo(right.AsUInt32()),
                DataKind.Int64 => left.AsInt64().CompareTo(right.AsInt64()),
                DataKind.UInt64 => left.AsUInt64().CompareTo(right.AsUInt64()),
                DataKind.Float32 => left.AsFloat32().CompareTo(right.AsFloat32()),
                DataKind.Float64 => left.AsFloat64().CompareTo(right.AsFloat64()),
                DataKind.String => string.Compare(left.AsString(), right.AsString(), StringComparison.Ordinal),
                DataKind.Date => left.AsDate().CompareTo(right.AsDate()),
                DataKind.DateTime => left.AsDateTime().CompareTo(right.AsDateTime()),
                DataKind.Time => left.AsTime().CompareTo(right.AsTime()),
                _ => 0,
            };
        }
    }
}
