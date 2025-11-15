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
        if (!DataValueComparer.IsComparable(keyKind))
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
        public void Accumulate(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame)
        {
            DataValue value = arguments[0];
            DataValue key = arguments[1];

            // Skip rows where the key is null — cannot compare.
            if (key.IsNull) return;

            _valueKind = value.Kind;

            // _bestKey was stabilised into frame.Target by a previous Accumulate (or is
            // default on the first call). New candidate's payload still lives in
            // frame.Source. Compare across the two stores, then stabilise both the new
            // winning value and key into Target so their offsets stay valid after the
            // input batch's arena is recycled.
            if (!_hasValue || IsBetter(key, frame.Source, _bestKey, frame.Target))
            {
                _bestValue = DataValueRetention.Stabilize(value, frame.Source, frame.Target);
                _bestKey = DataValueRetention.Stabilize(key, frame.Source, frame.Target);
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
        public DataValue Result(in InvocationFrame frame)
        {
            if (!_hasValue) return DataValue.Null(_valueKind);
            // _bestValue lives in the Target arena passed during Accumulate (typically
            // context.Store). Restabilise into the emit Target so result-batch readers
            // resolve against the right arena.
            return DataValueRetention.Stabilize(_bestValue, frame.Source, frame.Target);
        }

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
            int comparison = DataValueComparer.Compare(candidate, current);
            return _findMaximum ? comparison > 0 : comparison < 0;
        }

        private bool IsBetter(
            DataValue candidate, IValueStore candidateStore,
            DataValue current, IValueStore currentStore)
        {
            int comparison = DataValueComparer.Compare(candidate, candidateStore, current, currentStore);
            return _findMaximum ? comparison > 0 : comparison < 0;
        }
    }
}
