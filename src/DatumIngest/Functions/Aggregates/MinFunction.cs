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
        if (kind is not (DataKind.Float32 or DataKind.UInt8 or DataKind.String
            or DataKind.Date or DataKind.DateTime or DataKind.Time))
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

        public void Accumulate(ReadOnlySpan<DataValue> arguments)
        {
            DataValue value = arguments[0];
            if (value.IsNull) return;

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

            if (_minimum is null || CompareValues(otherAccumulator._minimum.Value, _minimum.Value) < 0)
            {
                _minimum = otherAccumulator._minimum;
            }
        }

        public DataValue Result => _minimum ?? DataValue.Null(DataKind.Float32);

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
