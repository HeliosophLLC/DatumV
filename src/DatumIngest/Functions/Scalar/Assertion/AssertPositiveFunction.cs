using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Assertion;

/// <summary>
/// Returns the numeric input verbatim when it is strictly greater than zero;
/// throws otherwise. Null input passes through unchecked. For unsigned
/// kinds, only zero fails.
/// </summary>
public sealed class AssertPositiveFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "assert_positive";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Assertion;

    /// <inheritdoc />
    public static string Description =>
        "Returns the numeric input when it is strictly greater than zero; throws otherwise. " +
        "Null inputs pass through unchecked.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("message", DataKindMatcher.Exact(DataKind.String), IsOptional: true),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.SameAs(0)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<AssertPositiveFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef value = args[0];
        if (value.IsNull) return new ValueTask<ValueRef>(value);

        if (NumericSign.IsPositive(value)) return new ValueTask<ValueRef>(value);

        AssertHelpers.Throw(
            AssertHelpers.UserMessage(args, 1),
            $"value {AssertHelpers.Display(value)} was not positive");
        return default;
    }

}
