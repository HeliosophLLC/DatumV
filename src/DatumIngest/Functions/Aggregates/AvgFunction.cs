using DatumIngest.Model;

namespace DatumIngest.Functions.Aggregates;

/// <summary>
/// Implements <c>AVG(expression)</c>. Computes the arithmetic mean of all
/// non-null numeric values. Returns null if all values are null.
/// </summary>
public sealed class AvgFunction : IAggregateFunction
{
    /// <inheritdoc/>
    public string Name => "AVG";

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("AVG() requires exactly one argument.");
        }

        if (argumentKinds[0] is not (DataKind.Float32 or DataKind.UInt8))
        {
            throw new ArgumentException($"AVG() requires a numeric argument, got {argumentKinds[0]}.");
        }

        return DataKind.Float32;
    }

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new AvgAccumulator();

    private sealed class AvgAccumulator : IAggregateAccumulator
    {
        private double _sum;
        private long _count;

        public void Accumulate(ReadOnlySpan<DataValue> arguments)
        {
            if (arguments[0].IsNull) return;

            _sum += arguments[0].AsFloat32();
            _count++;
        }

        /// <inheritdoc/>
        public void Merge(IAggregateAccumulator other)
        {
            AvgAccumulator otherAccumulator = (AvgAccumulator)other;
            _sum += otherAccumulator._sum;
            _count += otherAccumulator._count;
        }

        public DataValue Result => _count > 0
            ? DataValue.FromFloat32((float)(_sum / _count))
            : DataValue.Null(DataKind.Float32);
    }
}
