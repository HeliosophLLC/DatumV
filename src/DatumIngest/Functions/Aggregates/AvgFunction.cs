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

    private static bool IsNumericKind(DataKind kind) =>
        kind is DataKind.Int8 or DataKind.Int16 or DataKind.UInt8 or DataKind.UInt16
            or DataKind.Int32 or DataKind.UInt32 or DataKind.Int64 or DataKind.UInt64
            or DataKind.Float32 or DataKind.Float64;

    internal static double ExtractAsDouble(DataValue value) => value.Kind switch
    {
        DataKind.Int8 => value.AsInt8(),
        DataKind.Int16 => value.AsInt16(),
        DataKind.UInt8 => value.AsUInt8(),
        DataKind.UInt16 => value.AsUInt16(),
        DataKind.Int32 => value.AsInt32(),
        DataKind.UInt32 => value.AsUInt32(),
        DataKind.Int64 => (double)value.AsInt64(),
        DataKind.UInt64 => (double)value.AsUInt64(),
        DataKind.Float32 => value.AsFloat32(),
        DataKind.Float64 => value.AsFloat64(),
        _ => throw new InvalidOperationException($"Cannot extract double from {value.Kind}."),
    };

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
