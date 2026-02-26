using DatumIngest.Execution;
using DatumIngest.Functions.Scalar.Activation;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Vector;

/// <summary>
/// <c>argmax(values FLOAT32[]) → INT32</c>. Returns the index of the
/// largest element. The classic classifier read: <c>argmax(logits)</c>
/// gives you the predicted class id.
/// </summary>
/// <remarks>
/// Ties resolve to the lowest index — same convention as
/// <c>numpy.argmax</c> / PyTorch's <c>torch.argmax</c>. <c>NaN</c>
/// behaviour matches the IEEE-754 default (NaN doesn't compare greater).
/// Empty input throws — there is no defensible "argmax of nothing."
/// </remarks>
public sealed class ArgmaxFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "argmax";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;

    /// <inheritdoc />
    public static string Description =>
        "Returns the index of the largest element in a Float32 vector: " +
        "argmax(values FLOAT32[]) → INT32. " +
        "Ties resolve to the lowest index; empty input throws.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("values", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Int32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ArgmaxFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull)
        {
            return new(ValueRef.Null(DataKind.Int32));
        }

        float[] values = ActivationOps.ReadFloat32Array(arg);
        if (values.Length == 0)
        {
            throw new FunctionArgumentException(Name, "argmax requires at least one element; got an empty array.");
        }

        int best = 0;
        float bestValue = values[0];
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i] > bestValue)
            {
                bestValue = values[i];
                best = i;
            }
        }
        return new(ValueRef.FromInt32(best));
    }
}

/// <summary>
/// <c>topk(values FLOAT32[], k INT) → INT32[]</c>. Returns the indices of
/// the top-k elements sorted by value, descending. Users index back into
/// the original array for the values; keeping the function's return type
/// flat (just indices) makes it SQL-composable without needing
/// struct-shaped returns.
/// </summary>
/// <remarks>
/// <para>
/// When <c>k</c> exceeds the input length, returns all <c>length</c>
/// indices (no padding, no error). When <c>k = 0</c>, returns an empty
/// array. Negative <c>k</c> throws.
/// </para>
/// <para>
/// Sort is stable: equal values resolve to ascending index order. For
/// the dominant use case (classification top-k) values rarely tie.
/// </para>
/// </remarks>
public sealed class TopkFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "topk";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;

    /// <inheritdoc />
    public static string Description =>
        "Returns indices of the top-k elements in descending order of value: " +
        "topk(values FLOAT32[], k INT) → INT32[]. " +
        "When k > length, returns all indices; ties resolve to ascending index.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("values", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("k",      DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Int32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<TopkFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull)
        {
            return new(ValueRef.NullArray(DataKind.Int32));
        }

        int k = args[1].ToInt32();
        if (k < 0)
        {
            throw new FunctionArgumentException(Name, $"k must be non-negative, got {k}.");
        }

        float[] values = ActivationOps.ReadFloat32Array(args[0]);
        int len = values.Length;
        int kClamped = System.Math.Min(k, len);
        if (kClamped == 0)
        {
            return new(ValueRef.FromPrimitiveArray(Array.Empty<int>(), DataKind.Int32));
        }

        // Index-array sort: cheap for the typical top-k-of-a-classifier-
        // output case (length 100-1000). For very large inputs we'd want
        // a partial-sort or heap; revisit when a real consumer pulls on it.
        int[] indices = new int[len];
        for (int i = 0; i < len; i++) indices[i] = i;
        Array.Sort(indices, (a, b) =>
        {
            int cmp = values[b].CompareTo(values[a]);   // descending by value
            return cmp != 0 ? cmp : a.CompareTo(b);     // tie → ascending by index
        });

        int[] output = new int[kClamped];
        Array.Copy(indices, output, kClamped);
        return new(ValueRef.FromPrimitiveArray(output, DataKind.Int32));
    }
}
