using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Strings;

/// <summary>
/// PostgreSQL-compatible <c>rtrim(string [, characters])</c>. Removes the longest
/// trailing substring composed entirely of characters drawn from
/// <c>characters</c> (default: a single space). Null input propagates to null
/// output.
/// </summary>
public sealed class RtrimFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "rtrim";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Removes the longest substring of characters in `characters` (default: space) "
        + "from the end of `value`.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("characters", DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<RtrimFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef input = args[0];
        if (input.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.String));
        }
        if (args.Length >= 2 && args[1].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.String));
        }

        ReadOnlySpan<char> chars = args.Length >= 2 ? args[1].AsString().AsSpan() : " ".AsSpan();
        ReadOnlySpan<char> trimmed = input.AsString().AsSpan().TrimEnd(chars);
        return new ValueTask<ValueRef>(ValueRef.FromString(trimmed.ToString()));
    }
}
