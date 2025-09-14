using DatumIngest.Model;

namespace DatumIngest.Functions.Aggregates;

/// <summary>
/// Implements <c>ARG_MAX(value, key)</c> and <c>ARG_MIN(value, key)</c>.
/// Returns the <c>value</c> from the row where <c>key</c> reaches its
/// maximum (or minimum). Ties are broken by first-encountered order.
/// <para>
/// The key argument must be a comparable type (numeric, string, date, datetime, time).
/// The value argument may be any type — it is stored, not compared.
/// </para>
/// <para>
/// Returns null if all key values are null.
/// </para>
/// </summary>
public sealed class ArgMaxFunction : IAggregateFunction
{
    private readonly bool _findMaximum;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArgMaxFunction"/> class.
    /// </summary>
    /// <param name="findMaximum">
    /// <see langword="true"/> for ARG_MAX (track the maximum key);
    /// <see langword="false"/> for ARG_MIN (track the minimum key).
    /// </param>
    /// <param name="name">The SQL function name.</param>
    public ArgMaxFunction(bool findMaximum, string name)
    {
        _findMaximum = findMaximum;
        Name = name;
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException($"{Name}() requires exactly two arguments: value and key.");
        }

        DataKind keyKind = argumentKinds[1];
        bool isComparable = keyKind is DataKind.Int8 or DataKind.Int16 or DataKind.UInt8 or DataKind.UInt16
            or DataKind.Int32 or DataKind.UInt32 or DataKind.Int64 or DataKind.UInt64
            or DataKind.Float32 or DataKind.Float64
            or DataKind.String or DataKind.Date or DataKind.DateTime or DataKind.Time;
        if (!isComparable)
        {
            throw new ArgumentException(
                $"{Name}() second argument (key) must be a comparable type, got {keyKind}.");
        }

        // The result type matches the first argument (the value to return).
        return argumentKinds[0];
    }

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new ArgMaxAccumulator(_findMaximum);

    /// <summary>
    /// Accumulator that tracks the key extremum and the associated value.
    /// </summary>
    private sealed class ArgMaxAccumulator : IAggregateAccumulator
    {
        private readonly bool _findMaximum;
        private DataValue _bestValue;
        private DataValue _bestKey;
        private bool _hasValue;
        private DataKind _valueKind = DataKind.Float64;

        internal ArgMaxAccumulator(bool findMaximum)
        {
            _findMaximum = findMaximum;
        }

        /// <inheritdoc/>
        public void Accumulate(ReadOnlySpan<DataValue> arguments)
        {
            DataValue value = arguments[0];
            DataValue key = arguments[1];

            // Skip rows where the key is null — cannot compare.
            if (key.IsNull) return;

            _valueKind = value.Kind;

            if (!_hasValue || IsBetter(key, _bestKey))
            {
                _bestValue = value;
                _bestKey = key;
                _hasValue = true;
            }
        }

        /// <inheritdoc/>
        public void Merge(IAggregateAccumulator other)
        {
            ArgMaxAccumulator otherAccumulator = (ArgMaxAccumulator)other;

            if (!otherAccumulator._hasValue)
                return;

            if (!_hasValue || IsBetter(otherAccumulator._bestKey, _bestKey))
            {
                _bestValue = otherAccumulator._bestValue;
                _bestKey = otherAccumulator._bestKey;
                _valueKind = otherAccumulator._valueKind;
                _hasValue = true;
            }
        }

        /// <inheritdoc/>
        public DataValue Result => _hasValue ? _bestValue : DataValue.Null(_valueKind);

        /// <inheritdoc/>
        public void Reset()
        {
            _bestValue = default;
            _bestKey = default;
            _hasValue = false;
            _valueKind = DataKind.Float64;
        }

        /// <summary>
        /// Returns <see langword="true"/> when <paramref name="candidate"/> is strictly
        /// better than <paramref name="current"/> (greater for ARG_MAX, less for ARG_MIN).
        /// </summary>
        private bool IsBetter(DataValue candidate, DataValue current)
        {
            int comparison = CompareKeys(candidate, current);
            return _findMaximum ? comparison > 0 : comparison < 0;
        }

        private static int CompareKeys(DataValue left, DataValue right)
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
