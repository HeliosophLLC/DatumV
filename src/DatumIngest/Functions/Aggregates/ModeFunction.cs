using DatumIngest.Model;

namespace DatumIngest.Functions.Aggregates;

/// <summary>
/// Implements <c>MODE(expression)</c>. Returns the most frequently occurring value
/// in the group. If multiple values share the highest frequency, the first one
/// encountered is returned (deterministic tie-breaking by insertion order).
/// <para>
/// Accepts any comparable type: Scalar, UInt8, String, Date, DateTime, Time.
/// Returns the same <see cref="DataKind"/> as the input expression.
/// Returns null if all values are null.
/// </para>
/// <para>
/// Memory: O(D) per group where D is the number of distinct non-null values,
/// due to the frequency map.
/// </para>
/// </summary>
public sealed class ModeFunction : IAggregateFunction
{
    /// <inheritdoc/>
    public string Name => "MODE";

    /// <inheritdoc/>
    // O(D) frequency map per group where D = distinct values — Tier 2.
    public int QueryUnitCost => 2;

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("MODE() requires exactly one argument.");
        }

        if (argumentKinds[0] is DataKind.Vector)
        {
            throw new ArgumentException($"MODE() does not support Vector arguments.");
        }

        return argumentKinds[0];
    }

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new ModeAccumulator();

    /// <summary>
    /// Tracks value frequencies using a dictionary and remembers insertion order
    /// for deterministic tie-breaking.
    /// </summary>
    private sealed class ModeAccumulator : IAggregateAccumulator
    {
        private readonly Dictionary<DataValue, long> _frequencies = [];
        private readonly List<DataValue> _insertionOrder = [];
        private DataKind _kind = DataKind.Float32;

        public void Accumulate(ReadOnlySpan<DataValue> arguments)
        {
            if (arguments[0].IsNull) return;

            _kind = arguments[0].Kind;
            DataValue value = arguments[0];

            if (_frequencies.TryGetValue(value, out long count))
            {
                _frequencies[value] = count + 1;
            }
            else
            {
                _frequencies[value] = 1;
                _insertionOrder.Add(value);
            }
        }

        /// <inheritdoc/>
        public void Merge(IAggregateAccumulator other)
        {
            ModeAccumulator otherAccumulator = (ModeAccumulator)other;

            foreach (DataValue value in otherAccumulator._insertionOrder)
            {
                long otherFrequency = otherAccumulator._frequencies[value];

                if (_frequencies.TryGetValue(value, out long existingFrequency))
                {
                    _frequencies[value] = existingFrequency + otherFrequency;
                }
                else
                {
                    _frequencies[value] = otherFrequency;
                    _insertionOrder.Add(value);
                }
            }

            if (_frequencies.Count > 0)
            {
                _kind = otherAccumulator._kind;
            }
        }

        public DataValue Result
        {
            get
            {
                if (_frequencies.Count == 0)
                {
                    return DataValue.Null(_kind);
                }

                DataValue modeValue = _insertionOrder[0];
                long maxFrequency = _frequencies[modeValue];

                for (int index = 1; index < _insertionOrder.Count; index++)
                {
                    DataValue candidate = _insertionOrder[index];
                    long candidateFrequency = _frequencies[candidate];

                    if (candidateFrequency > maxFrequency)
                    {
                        modeValue = candidate;
                        maxFrequency = candidateFrequency;
                    }
                }

                return modeValue;
            }
        }

        /// <inheritdoc />
        public void Reset()
        {
            _frequencies.Clear();
            _insertionOrder.Clear();
            _kind = DataKind.Float32;
        }
    }
}
