using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Activation;

/// <summary>
/// <c>softmax(values FLOAT32[]) → FLOAT32[]</c>. Numerically-stable softmax
/// over a flat Float32 vector. The canonical normalization users apply to
/// classifier logits before reading off probabilities.
/// </summary>
/// <remarks>
/// Subtracts the max element before exponentiating, which is the standard
/// trick to avoid <c>exp(big_logit)</c> overflowing to <c>+inf</c>. Sum
/// always equals 1.0 (modulo IEEE-754 round-off).
/// </remarks>
public sealed class SoftmaxFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "softmax";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Activation;

    /// <inheritdoc />
    public static string Description =>
        "Numerically-stable softmax over a Float32 vector: softmax(values FLOAT32[]) → FLOAT32[]. " +
        "Output sums to 1.0; canonical normalization for classifier logits.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("values", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<SoftmaxFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull)
        {
            return new(ValueRef.NullArray(DataKind.Float32));
        }

        float[] input = ActivationOps.ReadFloat32Array(arg);
        if (input.Length == 0)
        {
            return new(ValueRef.FromPrimitiveArray(Array.Empty<float>(), DataKind.Float32));
        }

        float max = input[0];
        for (int i = 1; i < input.Length; i++)
        {
            if (input[i] > max) max = input[i];
        }

        float[] output = new float[input.Length];
        double sum = 0.0;
        for (int i = 0; i < input.Length; i++)
        {
            double e = System.Math.Exp(input[i] - max);
            output[i] = (float)e;
            sum += e;
        }

        if (sum == 0.0)
        {
            // All inputs were -inf relative to max; uniform fallback.
            float uniform = 1f / input.Length;
            for (int i = 0; i < input.Length; i++) output[i] = uniform;
        }
        else
        {
            float invSum = (float)(1.0 / sum);
            for (int i = 0; i < input.Length; i++) output[i] *= invSum;
        }

        return new(ValueRef.FromPrimitiveArray(output, DataKind.Float32));
    }
}

/// <summary>
/// <c>sigmoid(values FLOAT32[]) → FLOAT32[]</c>. Element-wise logistic
/// sigmoid <c>1 / (1 + exp(-x))</c>. The classifier-head activation for
/// binary / multi-label outputs.
/// </summary>
public sealed class SigmoidFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "sigmoid";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Activation;

    /// <inheritdoc />
    public static string Description =>
        "Element-wise logistic sigmoid over a Float32 vector: sigmoid(values FLOAT32[]) → FLOAT32[]. " +
        "Maps each input to (0, 1); used for binary / multi-label classifier heads.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("values", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<SigmoidFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull)
        {
            return new(ValueRef.NullArray(DataKind.Float32));
        }

        float[] input = ActivationOps.ReadFloat32Array(arg);
        float[] output = new float[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            // Branch-free numerically-stable form: for x >= 0 compute
            // 1/(1+e^-x); for x < 0 compute e^x/(1+e^x). Avoids overflow
            // from exp(-very_negative).
            double x = input[i];
            output[i] = x >= 0
                ? (float)(1.0 / (1.0 + System.Math.Exp(-x)))
                : (float)(System.Math.Exp(x) / (1.0 + System.Math.Exp(x)));
        }

        return new(ValueRef.FromPrimitiveArray(output, DataKind.Float32));
    }
}

/// <summary>
/// Shared helpers for activation / vector functions that consume a
/// <c>FLOAT32[]</c> input.
/// </summary>
internal static class ActivationOps
{
    /// <summary>
    /// Reads a <c>FLOAT32[]</c> argument as a managed <c>float[]</c>.
    /// Accepts both the <c>FromPrimitiveArray</c> typed-buffer payload
    /// and the <c>ValueRef[]</c> inline-array form so the function works
    /// regardless of whether the caller built the array via SQL literal,
    /// model output, or programmatic construction.
    /// </summary>
    internal static float[] ReadFloat32Array(ValueRef arg)
    {
        if (arg.Materialized is float[] direct)
        {
            return direct;
        }
        ReadOnlySpan<ValueRef> elements = arg.GetArrayElements();
        float[] copy = new float[elements.Length];
        for (int i = 0; i < elements.Length; i++)
        {
            if (!elements[i].TryToFloat(out float f))
            {
                throw new FunctionArgumentException("",
                    $"could not coerce array element [{i}] of kind {elements[i].Kind} to Float32.");
            }
            copy[i] = f;
        }
        return copy;
    }
}
