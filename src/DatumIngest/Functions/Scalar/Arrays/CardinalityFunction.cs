using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Arrays;

/// <summary>
/// Returns the total number of elements in an array (the product of all
/// dimensions for a multi-dim array; equivalent to the flat element span
/// length). Mirrors PostgreSQL's <c>cardinality()</c>. Null array yields a
/// null result. Throws if the argument is not an array.
/// </summary>
public sealed class CardinalityFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "cardinality";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Array;

    /// <inheritdoc />
    public static string Description =>
        "Returns the total number of elements in an array (product of all "
        + "dimensions for multi-dim arrays). PostgreSQL-compatible.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("array", DataKindMatcher.Any, IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Int32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<CardinalityFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arrayArg = arguments.Span[0];

        if (arrayArg.IsNull)
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Int32));

        // Multi-dim values arrive as DataValue-wrapper ValueRefs (no _materialized
        // payload) so the shape prefix survives. Route through the DataValue's
        // ElementCount, which is shape-prefix aware. Flat 1-D arrays still go
        // through GetArrayLength so the typed-payload fast paths (float[], int[])
        // stay free.
        if (arrayArg.IsMultiDim)
        {
            DataValue source = arrayArg.ToDataValue(frame.Source);
            return new ValueTask<ValueRef>(ValueRef.FromInt32(source.ElementCount));
        }
        return new ValueTask<ValueRef>(ValueRef.FromInt32(arrayArg.GetArrayLength()));
    }
}
