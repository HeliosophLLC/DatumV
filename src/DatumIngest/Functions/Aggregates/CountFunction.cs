using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Aggregates;

/// <summary>
/// Implements <c>COUNT(*)</c> (counts all rows) and <c>COUNT(expression)</c>
/// (counts non-null values). The <c>*</c> form receives zero arguments because
/// the parser treats the star as a sentinel that the planner strips.
/// </summary>
public sealed class CountFunction : IAggregateFunction, IAggregateFunctionMetadata
{
    /// <inheritdoc cref="IAggregateFunctionMetadata.Name"/>
    public static string Name => "COUNT";

    /// <inheritdoc/>
    string IAggregateFunction.Name => Name;

    /// <inheritdoc/>
    public static FunctionCategory Category => FunctionCategory.Aggregate;

    /// <inheritdoc/>
    public static string Description =>
        "Counts rows (COUNT(*)) or non-null values of an expression (COUNT(expr)).";

    /// <inheritdoc/>
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Int64)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("expression", DataKindMatcher.Any),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Int64)),
    ];

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        // COUNT(*) → 0 args, COUNT(expr) → 1 arg.
        if (argumentKinds.Length > 1)
        {
            throw new ArgumentException("COUNT() accepts zero or one argument.");
        }

        return DataKind.Int64;
    }

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new CountAccumulator();

    private sealed class CountAccumulator : IAggregateAccumulator
    {
        private long _count;

        public void Accumulate(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame)
        {
            if (arguments.Length == 0)
            {
                // COUNT(*) — count every row.
                _count++;
            }
            else if (!arguments[0].IsNull)
            {
                // COUNT(expr) — count non-null values.
                _count++;
            }
        }

        /// <inheritdoc/>
        public ValueTask MergeAsync(IAggregateAccumulator other, InvocationFrame frame)
        {
            CountAccumulator otherAccumulator = (CountAccumulator)other;
            _count += otherAccumulator._count;
            return ValueTask.CompletedTask;
        }

        public ValueTask<DataValue> ResultAsync(InvocationFrame frame) => new(DataValue.FromInt64(_count));

        /// <inheritdoc />
        public void Reset()
        {
            _count = 0;
        }
    }
}
