using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Aggregates;

/// <summary>
/// Implements <c>AVG(expression)</c>. Computes the arithmetic mean of all
/// non-null numeric values. Returns null if all values are null.
/// Always returns <c>Float64</c>, matching PostgreSQL semantics.
/// </summary>
public sealed class AvgFunction : IAggregateFunction, IAggregateFunctionMetadata
{
    /// <inheritdoc cref="IAggregateFunctionMetadata.Name"/>
    public static string Name => "AVG";

    /// <inheritdoc/>
    string IAggregateFunction.Name => Name;

    /// <inheritdoc/>
    public static FunctionCategory Category => FunctionCategory.Aggregate;

    /// <inheritdoc/>
    public static string Description =>
        "Arithmetic mean of non-null numeric values in a group; always returns Float64.";

    /// <inheritdoc/>
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("expression", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float64)),
    ];

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("AVG() requires exactly one argument.");
        }

        if (!DataValueComparer.IsNumericScalar(argumentKinds[0]))
        {
            throw new ArgumentException($"AVG() requires a numeric argument, got {argumentKinds[0]}.");
        }

        return DataKind.Float64;
    }

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new AvgAccumulator();

    private sealed class AvgAccumulator : IAggregateAccumulator
    {
        private double _sum;
        private long _count;

        public void Accumulate(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame)
        {
            if (arguments[0].IsNull) return;

            _sum += arguments[0].ToDouble();
            _count++;
        }

        /// <inheritdoc/>
        public ValueTask MergeAsync(IAggregateAccumulator other, InvocationFrame frame)
        {
            AvgAccumulator otherAccumulator = (AvgAccumulator)other;
            _sum += otherAccumulator._sum;
            _count += otherAccumulator._count;
            return ValueTask.CompletedTask;
        }

        public ValueTask<DataValue> ResultAsync(InvocationFrame frame) => new(_count > 0
            ? DataValue.FromFloat64(_sum / _count)
            : DataValue.Null(DataKind.Float64));

        /// <inheritdoc />
        public void Reset()
        {
            _sum = 0;
            _count = 0;
        }
    }
}
