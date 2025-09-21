using DatumIngest.Model;

namespace DatumIngest.Functions.Aggregates;

/// <summary>
/// Implements <c>AVG(expression)</c>. Computes the arithmetic mean of all
/// non-null numeric values. Returns null if all values are null.
/// Always returns <c>Float64</c>, matching PostgreSQL semantics.
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

        if (!IsNumericKind(argumentKinds[0]))
        {
            throw new ArgumentException($"AVG() requires a numeric argument, got {argumentKinds[0]}.");
        }

        return DataKind.Float64;
    }

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new AvgAccumulator();

    private static bool IsNumericKind(DataKind kind) => DataValueComparer.IsNumericScalar(kind);

    internal static double ExtractAsDouble(DataValue value) => value.ToDouble();

    private sealed class AvgAccumulator : IAggregateAccumulator
    {
        private double _sum;
        private long _count;

        public void Accumulate(ReadOnlySpan<DataValue> arguments)
        {
            if (arguments[0].IsNull) return;

            _sum += ExtractAsDouble(arguments[0]);
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
            ? DataValue.FromFloat64(_sum / _count)
            : DataValue.Null(DataKind.Float64);

        /// <inheritdoc />
        public void Reset()
        {
            _sum = 0;
            _count = 0;
        }
    }
}
