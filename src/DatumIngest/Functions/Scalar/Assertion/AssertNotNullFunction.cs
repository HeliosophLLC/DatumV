using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Assertion;

/// <summary>
/// Returns the first argument verbatim when it is non-null; throws an
/// <see cref="InvalidOperationException"/> when it is null. The optional
/// second argument supplies the failure message; when omitted, a default
/// message is used. <see cref="UdfInliner"/> always supplies an explicit
/// message naming the parameter or return slot it's guarding.
/// </summary>
public sealed class AssertNotNullFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "assert_not_null";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Assertion;

    /// <inheritdoc />
    public static string Description =>
        "Returns the input value when non-null; throws when null. " +
        "Used by the UDF inliner to enforce IS NOT NULL annotations on " +
        "parameters and return values.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Any),
                new ParameterSpec("message", DataKindMatcher.Exact(DataKind.String), IsOptional: true),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.SameAs(0)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<AssertNotNullFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef value = args[0];
        if (!value.IsNull) return new ValueTask<ValueRef>(value);

        string message = AssertHelpers.UserMessage(args, 1) ?? "value is null";
        throw new InvalidOperationException(message);
    }

}
