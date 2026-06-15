using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Arrays;

/// <summary>
/// Tests whether a typed array contains a given value by element-wise
/// equality. Multi-dim arrays scan the whole flat element span (matching
/// the shape-agnostic row in the array-function table). Returns null when
/// the array is null; the search-value side null returns false (SQL NULL
/// is not equal to anything, including itself). Supports the integer,
/// float, Boolean, and String element kinds — see
/// <see cref="ArraySearchCore"/> for the per-kind dispatch.
/// </summary>
public sealed class ArrayContainsFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "array_contains";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Array;

    /// <inheritdoc />
    public static string Description =>
        "Returns whether the array contains the value (by equality). "
        + "Multi-dim arrays scan the whole tensor. Null arrays return null; "
        + "null search values return false.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("array", DataKindMatcher.Any, IsArray: ArrayMatch.Array),
                new ParameterSpec("value", DataKindMatcher.Any, IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Boolean)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ArrayContainsFunction>(argumentKinds);

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
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Boolean));
        }

        int oneBasedIndex = ArraySearchCore.IndexOf(arrayArg, args[1], frame, Name);
        return new ValueTask<ValueRef>(ValueRef.FromBoolean(oneBasedIndex != ArraySearchCore.NotFound));
    }
}
