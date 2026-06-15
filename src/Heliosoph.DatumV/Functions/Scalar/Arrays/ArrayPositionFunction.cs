using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Arrays;

/// <summary>
/// Returns the 1-based index of the first occurrence of a value in a flat
/// typed array, or null if not found. Restricted to 1-D arrays — position
/// has no well-defined linearisation across multi-dim shapes. Returns null
/// when the array is null; a null search value returns null (SQL NULL is
/// not equal to anything). Element-kind support mirrors
/// <see cref="ArrayContainsFunction"/>: integer / float / Boolean / String.
/// </summary>
public sealed class ArrayPositionFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "array_position";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Array;

    /// <inheritdoc />
    public static string Description =>
        "Returns the 1-based index of the first occurrence of the value in "
        + "a flat array, or null if not found. Rejects multi-dim arrays.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("array", DataKindMatcher.Any, IsArray: ArrayMatch.FlatArray),
                new ParameterSpec("value", DataKindMatcher.Any, IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Int32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ArrayPositionFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef arrayArg = args[0];
        if (arrayArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Int32));
        }

        int oneBasedIndex = ArraySearchCore.IndexOf(arrayArg, args[1], frame, Name);
        return new ValueTask<ValueRef>(oneBasedIndex == ArraySearchCore.NotFound
            ? ValueRef.Null(DataKind.Int32)
            : ValueRef.FromInt32(oneBasedIndex));
    }
}
