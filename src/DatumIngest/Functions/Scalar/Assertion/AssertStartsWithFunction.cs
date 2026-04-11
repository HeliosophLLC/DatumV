using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Assertion;

/// <summary>
/// Returns the string input verbatim when it starts with the supplied
/// prefix (ordinal comparison); throws otherwise. Null in either operand
/// passes through unchecked.
/// </summary>
public sealed class AssertStartsWithFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "assert_starts_with";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Assertion;

    /// <inheritdoc />
    public static string Description =>
        "Returns the string input when it starts with the supplied prefix; throws otherwise. " +
        "Null inputs pass through unchecked.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("prefix", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("message", DataKindMatcher.Exact(DataKind.String), IsOptional: true),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.SameAs(0)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<AssertStartsWithFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef value = args[0];
        ValueRef prefix = args[1];
        if (value.IsNull || prefix.IsNull) return new ValueTask<ValueRef>(value);

        string prefixText = prefix.AsString();
        if (value.AsString().StartsWith(prefixText, StringComparison.Ordinal))
        {
            return new ValueTask<ValueRef>(value);
        }

        AssertHelpers.Throw(
            AssertHelpers.UserMessage(args, 2),
            $"value {AssertHelpers.Display(value)} did not start with '{prefixText}'");
        return default;
    }

}
