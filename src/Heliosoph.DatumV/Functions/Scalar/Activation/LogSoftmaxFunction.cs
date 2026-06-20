using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Activation;

/// <summary>
/// <c>log_softmax(values FLOAT32[]) → FLOAT32[]</c> /
/// <c>log_softmax(x FLOAT32) → FLOAT32</c>. Element-wise log of the
/// numerically-stable softmax: <c>x_i - max - log(Σⱼ exp(x_j - max))</c>.
/// Scalar form is the degenerate <c>log(1) = 0</c>.
/// </summary>
/// <remarks>
/// Uses the log-sum-exp trick: subtracting the max before exponentiating
/// avoids overflow on big logits while letting the subtracted offset cancel
/// out of the final result.
/// </remarks>
public sealed class LogSoftmaxFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "log_softmax";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Activation;

    /// <inheritdoc />
    public static string Description =>
        "Numerically-stable log-softmax over a Float32 vector: " +
        "log_softmax(values FLOAT32[]) → FLOAT32[]. " +
        "Scalar form returns 0.0 (log of degenerate softmax).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
        ActivationOps.ScalarOrArraySignatures("values");

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<LogSoftmaxFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull)
        {
            return new(arg.IsArray ? ValueRef.NullArray(DataKind.Float32) : ValueRef.Null(DataKind.Float32));
        }
        if (!arg.IsArray)
        {
            return new(ValueRef.FromFloat32(0f));
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

        double sumExp = 0.0;
        for (int i = 0; i < input.Length; i++)
        {
            sumExp += System.Math.Exp(input[i] - max);
        }
        float logSumExp = (float)(max + System.Math.Log(sumExp));

        float[] output = new float[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            output[i] = input[i] - logSumExp;
        }

        return new(ActivationOps.WrapFloat32Array(output, arg, frame));
    }
}
