using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Arrays;

/// <summary>
/// Builds an array of length <c>count</c> with every element equal to
/// <c>value</c>. Returns an array whose element kind matches the value's
/// scalar kind. Used to construct fixed-content tensors that model bodies
/// need at runtime — e.g. all-1s attention masks of length matching the
/// concatenated visual + prompt sequence in Florence-2's encoder.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Element kinds.</strong> v1 supports the primitive scalar kinds
/// that show up in model bodies: <c>Int64</c>, <c>Int32</c>, <c>Float32</c>,
/// <c>Boolean</c>. Other kinds throw — extending is one switch arm per kind.
/// </para>
/// <para>
/// <strong>Zero / negative count.</strong> A count of zero yields an empty
/// array; a negative count is a runtime error (no SQL interpretation exists
/// for "negative-length array").
/// </para>
/// </remarks>
public sealed class ArrayRepeatFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "array_repeat";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Array;

    /// <inheritdoc />
    public static string Description =>
        "Builds an array of `count` copies of `value`. Result is an array "
        + "of the same element kind as `value`. v1 supports Int64, Int32, "
        + "Float32, Boolean — the kinds model bodies typically need for "
        + "attention masks and fixed-content tensors.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Any, IsArray: ArrayMatch.Scalar),
                // count is `IntegerFamily` rather than `Exact(Int32)` so SQL
                // literals that narrow to Int8/Int16 ("array_repeat(x, 256)")
                // and arithmetic expressions whose operand kinds never widen
                // to Int32 ("array_repeat(x, 2 * 128)") work without the
                // caller having to wrap the count in CAST(... AS Int32).
                new ParameterSpec("count", DataKindMatcher.Family(DataKindFamily.IntegerFamily), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            // Result is an array of the same element kind as the value arg.
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.SameAs(0))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ArrayRepeatFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull)
        {
            // Null value or null count → null array of the value's kind.
            return new ValueTask<ValueRef>(ValueRef.NullArray(args[0].Kind));
        }

        if (!args[1].TryToInt32(out int count))
        {
            throw new FunctionArgumentException(Name,
                $"count of kind {args[1].Kind} could not be widened to Int32.");
        }
        if (count < 0)
        {
            throw new FunctionArgumentException(Name,
                $"count must be non-negative; got {count}.");
        }

        return new ValueTask<ValueRef>(args[0].Kind switch
        {
            DataKind.Int64 => MakeArray<long>(args[0].AsInt64(), count, DataKind.Int64),
            DataKind.Int32 => MakeArray<int>(args[0].AsInt32(), count, DataKind.Int32),
            DataKind.Float32 => MakeArray<float>(args[0].AsFloat32(), count, DataKind.Float32),
            DataKind.Boolean => MakeArray<byte>(
                args[0].AsBoolean() ? (byte)1 : (byte)0, count, DataKind.Boolean),
            _ => throw new FunctionArgumentException(Name,
                $"array_repeat v1 supports value kinds Int64, Int32, Float32, Boolean; "
                + $"got {args[0].Kind}."),
        });
    }

    /// <summary>
    /// Allocates a fresh <c>T[count]</c>, fills it with <paramref name="value"/>,
    /// and wraps it as a primitive ValueRef array of the supplied
    /// <paramref name="kind"/>. <see cref="Array.Fill{T}(T[], T)"/> compiles
    /// to a tight memset-style loop for primitive value types.
    /// </summary>
    private static ValueRef MakeArray<T>(T value, int count, DataKind kind) where T : unmanaged
    {
        T[] buffer = new T[count];
        if (count > 0)
        {
            Array.Fill(buffer, value);
        }
        return ValueRef.FromPrimitiveArray(buffer, kind);
    }
}
