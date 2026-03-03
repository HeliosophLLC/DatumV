using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the number of elements in an array. Null array yields a null result.
/// Throws if the argument is not an array. Element kind is unrestricted.
/// </summary>
public sealed class ArrayLengthFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "array_length";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Array;

    /// <inheritdoc />
    public static string Description =>
        "Returns the number of elements in an array.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("array", DataKindMatcher.Any),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Int32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ArrayLengthFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef arrayArg = args[0];

        if (!arrayArg.IsArray)
            throw new FunctionArgumentException(Name, "argument must be an array.");

        if (arrayArg.IsNull)
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Int32));

        return new ValueTask<ValueRef>(ValueRef.FromInt32(arrayArg.GetArrayLength()));
    }
}
