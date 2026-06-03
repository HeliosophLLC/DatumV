using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Activation;

/// <summary>
/// <c>softmax(values FLOAT32[]) → FLOAT32[]</c> / <c>softmax(x FLOAT32) → FLOAT32</c>.
/// Numerically-stable softmax over a Float32 vector; scalar form is the
/// degenerate identity returning <c>1.0</c>. The canonical normalization
/// users apply to classifier logits before reading off probabilities.
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
        "Output sums to 1.0; canonical normalization for classifier logits. " +
        "Scalar form returns 1.0 (degenerate identity).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
        ActivationOps.ScalarOrArraySignatures("values");

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
            return new(arg.IsArray ? ValueRef.NullArray(DataKind.Float32) : ValueRef.Null(DataKind.Float32));
        }
        if (!arg.IsArray)
        {
            // softmax of a single element is 1.0 (e^x / e^x).
            return new(ValueRef.FromFloat32(1f));
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

        return new(ActivationOps.WrapFloat32Array(output, arg, frame));
    }
}

/// <summary>
/// <c>sigmoid(values FLOAT32[]) → FLOAT32[]</c> / <c>sigmoid(x FLOAT32) → FLOAT32</c>.
/// Element-wise logistic sigmoid <c>1 / (1 + exp(-x))</c>. The classifier-head
/// activation for binary / multi-label outputs.
/// </summary>
public sealed class SigmoidFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "sigmoid";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Activation;

    /// <inheritdoc />
    public static string Description =>
        "Logistic sigmoid σ(x) = 1/(1+exp(-x)). Accepts a Float32 scalar or Float32[] vector. " +
        "Maps each input to (0, 1); used for binary / multi-label classifier heads.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
        ActivationOps.ScalarOrArraySignatures("values");

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<SigmoidFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken) =>
        new(ActivationOps.Apply(arguments.Span[0], frame,Sigmoid));

    private static float Sigmoid(float x)
    {
        // Branch-free numerically-stable form: for x >= 0 compute
        // 1/(1+e^-x); for x < 0 compute e^x/(1+e^x). Avoids overflow
        // from exp(-very_negative).
        double d = x;
        return d >= 0
            ? (float)(1.0 / (1.0 + System.Math.Exp(-d)))
            : (float)(System.Math.Exp(d) / (1.0 + System.Math.Exp(d)));
    }
}

/// <summary>
/// <c>relu(x)</c>. Rectified Linear Unit <c>max(0, x)</c>. The default
/// hidden-layer activation for most feed-forward and convolutional nets.
/// </summary>
public sealed class ReluFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "relu";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Activation;

    /// <inheritdoc />
    public static string Description =>
        "Rectified Linear Unit max(0, x). Accepts a Float32 scalar or Float32[] vector.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
        ActivationOps.ScalarOrArraySignatures("values");

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ReluFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken) =>
        new(ActivationOps.Apply(arguments.Span[0], frame,static x => x > 0f ? x : 0f));
}

/// <summary>
/// <c>selu(x)</c>. Scaled Exponential Linear Unit: <c>λ·x</c> for <c>x &gt; 0</c>,
/// <c>λ·α·(exp(x)-1)</c> otherwise, with the self-normalizing constants
/// <c>α ≈ 1.6732632</c> and <c>λ ≈ 1.0507010</c> from Klambauer et al. (2017).
/// </summary>
public sealed class SeluFunction : IFunction, IScalarFunction
{
    private const double Alpha = 1.6732632423543772848170429916717;
    private const double Lambda = 1.0507009873554804934193349852946;

    /// <inheritdoc />
    public static string Name => "selu";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Activation;

    /// <inheritdoc />
    public static string Description =>
        "Scaled Exponential Linear Unit with self-normalizing constants. " +
        "Accepts a Float32 scalar or Float32[] vector.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
        ActivationOps.ScalarOrArraySignatures("values");

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<SeluFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken) =>
        new(ActivationOps.Apply(arguments.Span[0], frame,Selu));

    private static float Selu(float x) =>
        x > 0f
            ? (float)(Lambda * x)
            : (float)(Lambda * Alpha * (System.Math.Exp(x) - 1.0));
}

/// <summary>
/// <c>gelu(x)</c>. Gaussian Error Linear Unit using the tanh-based fast
/// approximation <c>0.5·x·(1 + tanh(√(2/π)·(x + 0.044715·x³)))</c>. The
/// standard activation in modern transformer FFN blocks (BERT, GPT-2, etc.).
/// </summary>
public sealed class GeluFunction : IFunction, IScalarFunction
{
    private const double Sqrt2OverPi = 0.7978845608028653558798921198687;
    private const double Coeff = 0.044715;

    /// <inheritdoc />
    public static string Name => "gelu";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Activation;

    /// <inheritdoc />
    public static string Description =>
        "Gaussian Error Linear Unit (tanh fast approximation). " +
        "Accepts a Float32 scalar or Float32[] vector.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
        ActivationOps.ScalarOrArraySignatures("values");

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<GeluFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken) =>
        new(ActivationOps.Apply(arguments.Span[0], frame,Gelu));

    private static float Gelu(float x)
    {
        double d = x;
        double inner = Sqrt2OverPi * (d + Coeff * d * d * d);
        return (float)(0.5 * d * (1.0 + System.Math.Tanh(inner)));
    }
}

/// <summary>
/// <c>swish(x)</c>. Swish activation <c>x·σ(x)</c> (a.k.a. SiLU). Used in
/// EfficientNet and several modern vision/audio architectures.
/// </summary>
public sealed class SwishFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "swish";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Activation;

    /// <inheritdoc />
    public static string Description =>
        "Swish activation x·σ(x), also known as SiLU. " +
        "Accepts a Float32 scalar or Float32[] vector.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
        ActivationOps.ScalarOrArraySignatures("values");

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<SwishFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken) =>
        new(ActivationOps.Apply(arguments.Span[0], frame,Swish));

    private static float Swish(float x)
    {
        double d = x;
        double sig = d >= 0
            ? 1.0 / (1.0 + System.Math.Exp(-d))
            : System.Math.Exp(d) / (1.0 + System.Math.Exp(d));
        return (float)(d * sig);
    }
}

/// <summary>
/// <c>softplus(x)</c>. Softplus <c>ln(1 + exp(x))</c>, a smooth approximation
/// of ReLU. Always positive.
/// </summary>
public sealed class SoftplusFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "softplus";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Activation;

    /// <inheritdoc />
    public static string Description =>
        "Softplus ln(1 + exp(x)), a smooth ReLU approximation. " +
        "Accepts a Float32 scalar or Float32[] vector.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
        ActivationOps.ScalarOrArraySignatures("values");

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<SoftplusFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken) =>
        new(ActivationOps.Apply(arguments.Span[0], frame,Softplus));

    internal static float Softplus(float x)
    {
        // Stable: max(x, 0) + log(1 + exp(-|x|)). Avoids exp(big) overflow.
        double d = x;
        double absX = d < 0 ? -d : d;
        double maxX = d > 0 ? d : 0;
        return (float)(maxX + System.Math.Log(1.0 + System.Math.Exp(-absX)));
    }
}

/// <summary>
/// <c>softsign(x)</c>. Softsign <c>x / (1 + |x|)</c>. Smooth, bounded in
/// (-1, 1); slower-saturating alternative to tanh.
/// </summary>
public sealed class SoftsignFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "softsign";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Activation;

    /// <inheritdoc />
    public static string Description =>
        "Softsign x / (1 + |x|), a smoother bounded alternative to tanh. " +
        "Accepts a Float32 scalar or Float32[] vector.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
        ActivationOps.ScalarOrArraySignatures("values");

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<SoftsignFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken) =>
        new(ActivationOps.Apply(arguments.Span[0], frame,static x => x / (1f + System.Math.Abs(x))));
}

/// <summary>
/// <c>mish(x)</c>. Mish activation <c>x · tanh(softplus(x))</c>. Self-gated,
/// smooth; popularized by YOLOv4 and similar detection backbones.
/// </summary>
public sealed class MishFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "mish";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Activation;

    /// <inheritdoc />
    public static string Description =>
        "Mish activation x · tanh(softplus(x)). " +
        "Accepts a Float32 scalar or Float32[] vector.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
        ActivationOps.ScalarOrArraySignatures("values");

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<MishFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken) =>
        new(ActivationOps.Apply(arguments.Span[0], frame,Mish));

    private static float Mish(float x) => (float)(x * System.Math.Tanh(SoftplusFunction.Softplus(x)));
}

/// <summary>
/// <c>hard_sigmoid(x)</c>. Piecewise-linear sigmoid approximation
/// <c>max(0, min(1, 0.2·x + 0.5))</c>, matching the ONNX default. Cheaper
/// than the exponential sigmoid; used on quantized mobile models.
/// </summary>
public sealed class HardSigmoidFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "hard_sigmoid";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Activation;

    /// <inheritdoc />
    public static string Description =>
        "Piecewise-linear sigmoid approximation max(0, min(1, 0.2·x + 0.5)). " +
        "Accepts a Float32 scalar or Float32[] vector.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
        ActivationOps.ScalarOrArraySignatures("values");

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<HardSigmoidFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken) =>
        new(ActivationOps.Apply(arguments.Span[0], frame,HardSigmoid));

    internal static float HardSigmoid(float x)
    {
        float y = 0.2f * x + 0.5f;
        if (y < 0f) return 0f;
        if (y > 1f) return 1f;
        return y;
    }
}

/// <summary>
/// <c>hard_swish(x)</c>. Hard Swish <c>x · max(0, min(1, (x + 3) / 6))</c>
/// (MobileNetV3 / ONNX definition). A piecewise-linear swish approximation
/// cheap enough for mobile inference.
/// </summary>
public sealed class HardSwishFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "hard_swish";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Activation;

    /// <inheritdoc />
    public static string Description =>
        "Hard Swish x · max(0, min(1, (x + 3) / 6)) (MobileNetV3 definition). " +
        "Accepts a Float32 scalar or Float32[] vector.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
        ActivationOps.ScalarOrArraySignatures("values");

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<HardSwishFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken) =>
        new(ActivationOps.Apply(arguments.Span[0], frame,HardSwish));

    private static float HardSwish(float x)
    {
        float gate = (x + 3f) / 6f;
        if (gate < 0f) gate = 0f;
        else if (gate > 1f) gate = 1f;
        return x * gate;
    }
}

/// <summary>
/// <c>leaky_relu(x [, alpha])</c>. Leaky ReLU: <c>x</c> for <c>x &gt; 0</c>,
/// <c>α·x</c> otherwise. Default slope <c>α = 0.01</c>.
/// </summary>
public sealed class LeakyReluFunction : IFunction, IScalarFunction
{
    private const float DefaultAlpha = 0.01f;

    /// <inheritdoc />
    public static string Name => "leaky_relu";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Activation;

    /// <inheritdoc />
    public static string Description =>
        "Leaky ReLU with configurable negative slope α (default 0.01). " +
        "Accepts a Float32 scalar or Float32[] vector.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
        ActivationOps.ScalarOrArrayWithAlphaSignatures("values");

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<LeakyReluFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        float alpha = args.Length > 1 && !args[1].IsNull ? args[1].AsFloat32() : DefaultAlpha;
        return new(ActivationOps.Apply(args[0], frame,x => x > 0f ? x : alpha * x));
    }
}

/// <summary>
/// <c>elu(x [, alpha])</c>. Exponential Linear Unit: <c>x</c> for <c>x &gt; 0</c>,
/// <c>α·(exp(x) - 1)</c> otherwise. Default <c>α = 1.0</c>.
/// </summary>
public sealed class EluFunction : IFunction, IScalarFunction
{
    private const float DefaultAlpha = 1.0f;

    /// <inheritdoc />
    public static string Name => "elu";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Activation;

    /// <inheritdoc />
    public static string Description =>
        "Exponential Linear Unit with configurable α (default 1.0). " +
        "Accepts a Float32 scalar or Float32[] vector.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
        ActivationOps.ScalarOrArrayWithAlphaSignatures("values");

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<EluFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        float alpha = args.Length > 1 && !args[1].IsNull ? args[1].AsFloat32() : DefaultAlpha;
        return new(ActivationOps.Apply(args[0], frame,x => x > 0f ? x : alpha * (float)(System.Math.Exp(x) - 1.0)));
    }
}

/// <summary>
/// Shared helpers for activation / vector functions that consume a Float32
/// scalar or Float32[] input.
/// </summary>
internal static class ActivationOps
{
    /// <summary>
    /// Signature pair for an element-wise Float32 activation accepting either
    /// a scalar (<c>FLOAT32 → FLOAT32</c>) or an array (<c>FLOAT32[] → FLOAT32[]</c>).
    /// </summary>
    internal static IReadOnlyList<FunctionSignatureVariant> ScalarOrArraySignatures(string paramName) =>
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec(paramName, DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec(paramName, DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <summary>
    /// Signature pair as <see cref="ScalarOrArraySignatures"/> plus an optional
    /// trailing <c>alpha FLOAT32</c> parameter, for parameterised activations
    /// like <c>leaky_relu</c> and <c>elu</c>.
    /// </summary>
    internal static IReadOnlyList<FunctionSignatureVariant> ScalarOrArrayWithAlphaSignatures(string paramName) =>
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec(paramName, DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("alpha",   DataKindMatcher.Exact(DataKind.Float32), IsOptional: true, IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec(paramName, DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("alpha",   DataKindMatcher.Exact(DataKind.Float32), IsOptional: true, IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <summary>
    /// Applies an element-wise Float32→Float32 transform to <paramref name="arg"/>,
    /// handling null/scalar/array uniformly. Scalar in → scalar out; flat array
    /// in → flat array out; multi-dim array in → multi-dim array out with the
    /// input's shape preserved.
    /// </summary>
    internal static ValueRef Apply(ValueRef arg, EvaluationFrame frame, Func<float, float> fn)
    {
        if (arg.IsNull)
        {
            return arg.IsArray ? ValueRef.NullArray(DataKind.Float32) : ValueRef.Null(DataKind.Float32);
        }
        if (!arg.IsArray)
        {
            return ValueRef.FromFloat32(fn(arg.AsFloat32()));
        }

        float[] input = ReadFloat32Array(arg);
        float[] output = new float[input.Length];
        for (int i = 0; i < input.Length; i++) output[i] = fn(input[i]);
        return WrapFloat32Array(output, arg, frame);
    }

    /// <summary>
    /// Wraps a freshly-computed flat <c>float[]</c> output as a 1-D
    /// <see cref="ValueRef"/> or — when <paramref name="shapeSource"/> carries
    /// a multi-dim shape — as a multi-dim <see cref="ValueRef"/> with the
    /// shape copied over.
    /// </summary>
    internal static ValueRef WrapFloat32Array(float[] output, ValueRef shapeSource, EvaluationFrame frame)
    {
        if (!shapeSource.IsMultiDim)
        {
            return ValueRef.FromPrimitiveArray(output, DataKind.Float32);
        }

        ReadOnlySpan<int> shape = shapeSource.ToDataValue(frame.Source).GetShape(frame.Source, frame.SidecarRegistry);
        return ValueRef.FromPrimitiveMultiDimArray(output, shape.ToArray(), DataKind.Float32);
    }

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
