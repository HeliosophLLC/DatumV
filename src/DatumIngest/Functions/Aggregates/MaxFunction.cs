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
        if (kind is not (DataKind.Float32 or DataKind.UInt8 or DataKind.String
            or DataKind.Date or DataKind.DateTime or DataKind.Time))
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

        public void Accumulate(ReadOnlySpan<DataValue> arguments)
        {
            DataValue value = arguments[0];
            if (value.IsNull) return;

            if (_maximum is null || CompareValues(value, _maximum) > 0)
            {
                _maximum = value;
            }
        }

        public DataValue Result => _maximum ?? DataValue.Null(DataKind.Float32);

        private static int CompareValues(DataValue left, DataValue right)
        {
            return left.Kind switch
            {
                DataKind.Float32 or DataKind.UInt8 => left.AsFloat32().CompareTo(right.AsFloat32()),
                DataKind.String => string.Compare(left.AsString(), right.AsString(), StringComparison.Ordinal),
                DataKind.Date => left.AsDate().CompareTo(right.AsDate()),
                DataKind.DateTime => left.AsDateTime().CompareTo(right.AsDateTime()),
                DataKind.Time => left.AsTime().CompareTo(right.AsTime()),
                _ => 0,
            };
        }
    }
}
