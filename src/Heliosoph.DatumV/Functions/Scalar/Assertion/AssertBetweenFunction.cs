using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Assertion;

/// <summary>
/// Returns the first argument verbatim when it lies within
/// <c>[low, high]</c> inclusive; throws otherwise. Null in any operand
/// passes through unchecked.
/// </summary>
public sealed class AssertBetweenFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "assert_between";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Assertion;

    /// <inheritdoc />
    public static string Description =>
        "Returns the input value when it lies within [low, high] inclusive; throws otherwise. " +
        "Null inputs pass through unchecked.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Any),
                new ParameterSpec("low", DataKindMatcher.Any),
                new ParameterSpec("high", DataKindMatcher.Any),
                new ParameterSpec("message", DataKindMatcher.Exact(DataKind.String), IsOptional: true),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.SameAs(0)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<AssertBetweenFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef value = args[0];
        ValueRef low = args[1];
        ValueRef high = args[2];
        if (value.IsNull || low.IsNull || high.IsNull) return new ValueTask<ValueRef>(value);

        if (ExpressionEvaluator.CompareValueRefs(value, low) >= 0
            && ExpressionEvaluator.CompareValueRefs(value, high) <= 0)
        {
            return new ValueTask<ValueRef>(value);
        }
        AssertHelpers.Throw(
            AssertHelpers.UserMessage(args, 3),
            $"value {AssertHelpers.Display(value)} was not between "
            + $"{AssertHelpers.Display(low)} and {AssertHelpers.Display(high)}");
        return default;
    }

}
