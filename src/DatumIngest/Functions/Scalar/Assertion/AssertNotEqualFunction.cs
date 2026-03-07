using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Assertion;

/// <summary>
/// Returns the first argument verbatim when it differs from the second;
/// throws otherwise. Null in either operand passes through unchecked
/// (SQL three-valued logic).
/// </summary>
public sealed class AssertNotEqualFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "assert_not_equal";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Assertion;

    /// <inheritdoc />
    public static string Description =>
        "Returns the input value when it differs from the comparand; throws otherwise. " +
        "Null inputs pass through unchecked.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Any),
                new ParameterSpec("other", DataKindMatcher.Any),
                new ParameterSpec("message", DataKindMatcher.Exact(DataKind.String), IsOptional: true),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.SameAs(0)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<AssertNotEqualFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef value = args[0];
        ValueRef other = args[1];
        if (value.IsNull || other.IsNull) return new ValueTask<ValueRef>(value);

        if (ExpressionEvaluator.CompareValueRefs(value, other) != 0)
        {
            return new ValueTask<ValueRef>(value);
        }
        AssertHelpers.Throw(
            AssertHelpers.UserMessage(args, 2),
            $"value {AssertHelpers.Display(value)} was equal to {AssertHelpers.Display(other)}");
        return default;
    }

    /// <inheritdoc />
    public int QueryUnitCost => 0;
}
