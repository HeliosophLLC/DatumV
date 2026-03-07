using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Assertion;

/// <summary>
/// Returns the first argument verbatim when it is greater than or equal to
/// the threshold; throws otherwise. Null in either operand passes through
/// unchecked.
/// </summary>
public sealed class AssertGreaterOrEqualFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "assert_greater_or_equal";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Assertion;

    /// <inheritdoc />
    public static string Description =>
        "Returns the input value when it is greater than or equal to the threshold; throws otherwise. " +
        "Null inputs pass through unchecked.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Any),
                new ParameterSpec("threshold", DataKindMatcher.Any),
                new ParameterSpec("message", DataKindMatcher.Exact(DataKind.String), IsOptional: true),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.SameAs(0)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<AssertGreaterOrEqualFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef value = args[0];
        ValueRef threshold = args[1];
        if (value.IsNull || threshold.IsNull) return new ValueTask<ValueRef>(value);

        if (ExpressionEvaluator.CompareValueRefs(value, threshold) >= 0)
        {
            return new ValueTask<ValueRef>(value);
        }
        AssertHelpers.Throw(
            AssertHelpers.UserMessage(args, 2),
            $"value {AssertHelpers.Display(value)} was not greater than or equal to {AssertHelpers.Display(threshold)}");
        return default;
    }

    /// <inheritdoc />
    public int QueryUnitCost => 0;
}
