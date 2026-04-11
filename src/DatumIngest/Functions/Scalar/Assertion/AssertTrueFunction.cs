using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Assertion;

/// <summary>
/// Returns the input verbatim when it is <c>true</c>; throws otherwise.
/// Null input passes through unchecked.
/// </summary>
public sealed class AssertTrueFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "assert_true";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Assertion;

    /// <inheritdoc />
    public static string Description =>
        "Returns the boolean input when it is true; throws when it is false. " +
        "Null inputs pass through unchecked.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("condition", DataKindMatcher.Exact(DataKind.Boolean)),
                new ParameterSpec("message", DataKindMatcher.Exact(DataKind.String), IsOptional: true),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.SameAs(0)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<AssertTrueFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef value = args[0];
        if (value.IsNull) return new ValueTask<ValueRef>(value);
        if (value.AsBoolean()) return new ValueTask<ValueRef>(value);

        AssertHelpers.Throw(
            AssertHelpers.UserMessage(args, 1),
            "expected true but got false");
        return default;
    }

}
