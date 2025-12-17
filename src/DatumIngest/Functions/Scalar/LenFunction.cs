using System.Text;
using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the length of a string in UTF-8 bytes (matching the engine's
/// canonical string encoding). Null input propagates to a null result.
/// </summary>
public sealed class LenFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "len";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Returns the length of a string in UTF-8 bytes.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Int32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<LenFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueRef Execute(ReadOnlySpan<ValueRef> arguments, in EvaluationFrame frame)
    {
        ValueRef input = arguments[0];
        if (input.IsNull)
            return ValueRef.Null(DataKind.Int32);

        return ValueRef.FromInt32(Encoding.UTF8.GetByteCount(input.AsString()));
    }
}
