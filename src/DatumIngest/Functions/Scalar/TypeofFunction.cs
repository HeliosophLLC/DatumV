using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the runtime <see cref="DataKind"/> of a value as a
/// <see cref="DataKind.Type"/> tag. Enables type-oriented comparisons
/// like <c>typeof(x) == Int32</c> instead of string-based checks.
/// </summary>
public sealed class TypeofFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "typeof";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Conversion;

    /// <inheritdoc />
    public static string Description =>
        "Returns the runtime DataKind of a value as a Type tag. "
        + "Enables type-oriented comparisons like `typeof(x) == Int32`.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Any),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Type)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<TypeofFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueRef Execute(ReadOnlySpan<ValueRef> arguments, in EvaluationFrame frame) =>
        ValueRef.FromType(arguments[0].Kind);

    /// <inheritdoc />
    public int QueryUnitCost => 0;
}
