using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Assertion;

/// <summary>
/// Returns the first argument verbatim when it equals the second; throws
/// otherwise. Null in either operand passes through unchecked
/// (SQL three-valued logic) — pair with <c>assert_not_null</c> when null
/// is itself a violation.
/// </summary>
public sealed class AssertEqualFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "assert_equal";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Assertion;

    /// <inheritdoc />
    public static string Description =>
        "Returns the input value when it equals the expected value; throws otherwise. " +
        "Null inputs pass through unchecked.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Any),
                new ParameterSpec("expected", DataKindMatcher.Any),
                new ParameterSpec("message", DataKindMatcher.Exact(DataKind.String), IsOptional: true),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.SameAs(0)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<AssertEqualFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef value = args[0];
        ValueRef expected = args[1];
        if (value.IsNull || expected.IsNull) return new ValueTask<ValueRef>(value);

        if (ExpressionEvaluator.CompareValueRefs(value, expected) == 0)
        {
            return new ValueTask<ValueRef>(value);
        }
        AssertHelpers.Throw(
            AssertHelpers.UserMessage(args, 2),
            $"value {AssertHelpers.Display(value)} was not equal to {AssertHelpers.Display(expected)}");
        return default;
    }

    /// <inheritdoc />
    public int QueryUnitCost => 0;
}
