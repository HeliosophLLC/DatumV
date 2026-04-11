using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Assertion;

/// <summary>
/// Returns the input verbatim when it is a finite floating-point value;
/// throws when it is NaN or ±infinity. Restricted to float kinds — integer
/// values are always finite, so the assertion is meaningless there. Null
/// input passes through unchecked.
/// </summary>
public sealed class AssertFiniteFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "assert_finite";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Assertion;

    /// <inheritdoc />
    public static string Description =>
        "Returns the floating-point input when it is finite (not NaN, not ±infinity); throws otherwise. " +
        "Null inputs pass through unchecked.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec(
                    "value",
                    DataKindMatcher.OneOf(DataKind.Float16, DataKind.Float32, DataKind.Float64)),
                new ParameterSpec("message", DataKindMatcher.Exact(DataKind.String), IsOptional: true),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.SameAs(0)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<AssertFiniteFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef value = args[0];
        if (value.IsNull) return new ValueTask<ValueRef>(value);

        bool isFinite = value.Kind switch
        {
            DataKind.Float16 => Half.IsFinite(value.AsFloat16()),
            DataKind.Float32 => float.IsFinite(value.AsFloat32()),
            DataKind.Float64 => double.IsFinite(value.AsFloat64()),
            _ => true,
        };
        if (isFinite) return new ValueTask<ValueRef>(value);

        AssertHelpers.Throw(
            AssertHelpers.UserMessage(args, 1),
            $"value {AssertHelpers.Display(value)} was not finite");
        return default;
    }

}
