using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Assertion;

/// <summary>
/// Returns the first argument verbatim when it equals none of the array's
/// elements; throws otherwise. Null in either operand passes through
/// unchecked. See <see cref="AssertInFunction"/> for the array-shape
/// caveat.
/// </summary>
public sealed class AssertNotInFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "assert_not_in";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Assertion;

    /// <inheritdoc />
    public static string Description =>
        "Returns the input when it does not appear in the supplied array; throws otherwise. " +
        "Null inputs pass through unchecked.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Any, IsArray: ArrayMatch.Scalar),
                new ParameterSpec("forbidden", DataKindMatcher.Any, IsArray: ArrayMatch.Array),
                new ParameterSpec("message", DataKindMatcher.Exact(DataKind.String), IsOptional: true, IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.SameAs(0)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<AssertNotInFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef value = args[0];
        ValueRef forbidden = args[1];
        if (value.IsNull || forbidden.IsNull) return new ValueTask<ValueRef>(value);

        ReadOnlySpan<ValueRef> elements = forbidden.GetArrayElements();
        for (int i = 0; i < elements.Length; i++)
        {
            ValueRef element = elements[i];
            if (element.IsNull) continue;
            if (ExpressionEvaluator.CompareValueRefs(value, element) == 0)
            {
                AssertHelpers.Throw(
                    AssertHelpers.UserMessage(args, 2),
                    $"value {AssertHelpers.Display(value)} was in the forbidden set");
                return default;
            }
        }
        return new ValueTask<ValueRef>(value);
    }

}
