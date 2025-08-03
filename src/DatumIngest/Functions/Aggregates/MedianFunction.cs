using DatumIngest.Model;

namespace DatumIngest.Functions.Aggregates;

/// <summary>
/// Implements <c>MEDIAN(expression)</c>. Computes the median (50th percentile)
/// of all non-null numeric values. For even-count groups, returns the average
/// of the two middle values (continuous interpolation).
/// Returns null if all values are null.
/// <para>
/// Memory: O(N) per group — all non-null values are collected before the median
/// is computed. This is acceptable for typical ML group sizes (categories,
/// classes, time buckets).
/// </para>
/// </summary>
public sealed class MedianFunction : IAggregateFunction
{
    /// <inheritdoc/>
    public string Name => "MEDIAN";

    /// <inheritdoc/>
    // O(N) memory accumulation and O(N log N) sort at finalization — Tier 2.
    public int QueryUnitCost => 2;

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("MEDIAN() requires exactly one argument.");
        }

        if (argumentKinds[0] is not (DataKind.Scalar or DataKind.UInt8))
        {
            throw new ArgumentException($"MEDIAN() requires a numeric argument, got {argumentKinds[0]}.");
        }

        return DataKind.Scalar;
    }

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new MedianAccumulator();

    private sealed class MedianAccumulator : IAggregateAccumulator
    {
        private readonly List<float> _values = [];

        public void Accumulate(ReadOnlySpan<DataValue> arguments)
        {
            if (arguments[0].IsNull) return;

            _values.Add(arguments[0].AsScalar());
        }

        public DataValue Result
        {
            get
            {
                if (_values.Count == 0)
                {
                    return DataValue.Null(DataKind.Scalar);
                }

                _values.Sort();
                int count = _values.Count;
                int mid = count / 2;

                if (count % 2 == 1)
                {
                    return DataValue.FromScalar(_values[mid]);
                }

                // Even count: average of the two middle values.
                float median = (_values[mid - 1] + _values[mid]) / 2f;
                return DataValue.FromScalar(median);
            }
        }
    }
}
