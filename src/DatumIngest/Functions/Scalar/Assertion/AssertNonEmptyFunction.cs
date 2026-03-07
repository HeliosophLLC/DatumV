using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Assertion;

/// <summary>
/// Returns the input verbatim when it is a non-empty string or a non-empty
/// array; throws otherwise. Null input passes through unchecked. The two
/// signature variants share an implementation — string-length vs
/// array-length is decided at runtime from the value's array flag.
/// </summary>
public sealed class AssertNonEmptyFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "assert_non_empty";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Assertion;

    /// <inheritdoc />
    public static string Description =>
        "Returns the input when it is a non-empty string or array; throws otherwise. " +
        "Null inputs pass through unchecked.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Exact(DataKind.String), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("message", DataKindMatcher.Exact(DataKind.String), IsOptional: true, IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.SameAs(0)),

        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Any, IsArray: ArrayMatch.Array),
                new ParameterSpec("message", DataKindMatcher.Exact(DataKind.String), IsOptional: true, IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.SameAs(0)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<AssertNonEmptyFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef value = args[0];
        if (value.IsNull) return new ValueTask<ValueRef>(value);

        int length = value.IsArray ? value.GetArrayLength() : value.AsString().Length;
        if (length > 0) return new ValueTask<ValueRef>(value);

        AssertHelpers.Throw(
            AssertHelpers.UserMessage(args, 1),
            value.IsArray ? "value was an empty array" : "value was an empty string");
        return default;
    }

    /// <inheritdoc />
    public int QueryUnitCost => 0;
}
