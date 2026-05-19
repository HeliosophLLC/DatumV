using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Assertion;

/// <summary>
/// Returns the string input verbatim when it ends with the supplied suffix
/// (ordinal comparison); throws otherwise. Null in either operand passes
/// through unchecked.
/// </summary>
public sealed class AssertEndsWithFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "assert_ends_with";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Assertion;

    /// <inheritdoc />
    public static string Description =>
        "Returns the string input when it ends with the supplied suffix; throws otherwise. " +
        "Null inputs pass through unchecked.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("suffix", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("message", DataKindMatcher.Exact(DataKind.String), IsOptional: true),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.SameAs(0)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<AssertEndsWithFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef value = args[0];
        ValueRef suffix = args[1];
        if (value.IsNull || suffix.IsNull) return new ValueTask<ValueRef>(value);

        string suffixText = suffix.AsString();
        if (value.AsString().EndsWith(suffixText, StringComparison.Ordinal))
        {
            return new ValueTask<ValueRef>(value);
        }

        AssertHelpers.Throw(
            AssertHelpers.UserMessage(args, 2),
            $"value {AssertHelpers.Display(value)} did not end with '{suffixText}'");
        return default;
    }

}
