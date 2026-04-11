using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Assertion;

/// <summary>
/// Returns the first argument verbatim when it equals one of the array's
/// elements; throws otherwise. Null in either operand passes through
/// unchecked. Membership uses the same cross-kind comparator as SQL
/// <c>=</c>.
/// </summary>
/// <remarks>
/// Backed by <see cref="ValueRef.GetArrayElements"/>, which is populated by
/// SQL array literals (<c>[1, 2, 3]</c>). Columns of typed primitive arrays
/// (built via <c>ValueRef.FromPrimitiveArray</c>) don't expose a boxed
/// element view yet — pass such values through <c>unnest</c> + a literal
/// array if needed.
/// </remarks>
public sealed class AssertInFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "assert_in";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Assertion;

    /// <inheritdoc />
    public static string Description =>
        "Returns the input when it is equal to one of the array's elements; throws otherwise. " +
        "Null inputs pass through unchecked.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Any, IsArray: ArrayMatch.Scalar),
                new ParameterSpec("choices", DataKindMatcher.Any, IsArray: ArrayMatch.Array),
                new ParameterSpec("message", DataKindMatcher.Exact(DataKind.String), IsOptional: true, IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.SameAs(0)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<AssertInFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef value = args[0];
        ValueRef choices = args[1];
        if (value.IsNull || choices.IsNull) return new ValueTask<ValueRef>(value);

        ReadOnlySpan<ValueRef> elements = choices.GetArrayElements();
        for (int i = 0; i < elements.Length; i++)
        {
            ValueRef element = elements[i];
            if (element.IsNull) continue;
            if (ExpressionEvaluator.CompareValueRefs(value, element) == 0)
            {
                return new ValueTask<ValueRef>(value);
            }
        }

        AssertHelpers.Throw(
            AssertHelpers.UserMessage(args, 2),
            $"value {AssertHelpers.Display(value)} was not in the supplied choices");
        return default;
    }

}
