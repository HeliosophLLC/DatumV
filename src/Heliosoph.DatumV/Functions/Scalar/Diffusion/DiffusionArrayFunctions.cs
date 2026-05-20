using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions.Scalar.Activation;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Diffusion;

/// <summary>
/// <c>array_axpy(y Float32[], a Float32, x Float32[]) → Float32[]</c>. Computes
/// the element-wise update <c>y[i] + a · x[i]</c> and returns a fresh array.
/// Named after the BLAS Level-1 routine of the same role. Used in the
/// diffusion Euler step: <c>latents += (sigma_next - sigma) · noise_pred</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a builtin.</strong> The Euler update runs once per denoising
/// step over the entire latent tensor (16384 elements for SD, 65536 for SDXL).
/// Expressing it via per-element SQL would require a loop over array indices
/// with arithmetic on Float32 — possible but ~5 orders of magnitude slower
/// than a tight C# loop. One scalar primitive collapses the cost.
/// </para>
/// </remarks>
public sealed class ArrayAxpyFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "array_axpy";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Array;

    /// <inheritdoc />
    public static string Description =>
        "Element-wise `y + a*x` over two equal-length Float32 arrays with a Float32 scalar `a`. " +
        "Returns a fresh Float32[] of the same length. The BLAS axpy primitive — used in " +
        "diffusion bodies for the Euler update step.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("y", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("a", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("x", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ArrayAxpyFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.Float32));
        }
        float[] y = ActivationOps.ReadFloat32Array(args[0]);
        if (!args[1].TryToFloat(out float a))
        {
            throw new FunctionArgumentException(Name,
                $"scalar `a` of kind {args[1].Kind} could not be widened to Float32.");
        }
        float[] x = ActivationOps.ReadFloat32Array(args[2]);
        if (y.Length != x.Length)
        {
            throw new FunctionArgumentException(Name,
                $"y and x must have equal length; got y.length={y.Length}, x.length={x.Length}.");
        }
        float[] result = new float[y.Length];
        for (int i = 0; i < y.Length; i++)
        {
            result[i] = y[i] + a * x[i];
        }
        return new ValueTask<ValueRef>(ValueRef.FromPrimitiveArray(result, DataKind.Float32));
    }
}

/// <summary>
/// <c>array_scale(a Float32[], s Float32) → Float32[]</c>. Returns a fresh
/// array with every element multiplied by <c>s</c>. Used by diffusion bodies
/// to scale the initial latent buffer by sigma_max, to apply the
/// <c>c_in</c> precondition before each UNet call, and to divide by the
/// VAE scale factor before decoding.
/// </summary>
public sealed class ArrayScaleFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "array_scale";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Array;

    /// <inheritdoc />
    public static string Description =>
        "Multiplies every element of a Float32 array by a Float32 scalar; returns a fresh array. " +
        "Used in diffusion bodies for c_in preconditioning, sigma scaling, and the VAE scale-factor " +
        "divide.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("a", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("s", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ArrayScaleFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.Float32));
        }
        float[] a = ActivationOps.ReadFloat32Array(args[0]);
        if (!args[1].TryToFloat(out float s))
        {
            throw new FunctionArgumentException(Name,
                $"scalar `s` of kind {args[1].Kind} could not be widened to Float32.");
        }
        float[] result = new float[a.Length];
        for (int i = 0; i < a.Length; i++)
        {
            result[i] = a[i] * s;
        }
        return new ValueTask<ValueRef>(ValueRef.FromPrimitiveArray(result, DataKind.Float32));
    }
}

/// <summary>
/// <c>array_clamp(a Float32[], min Float32, max Float32) → Float32[]</c>.
/// Returns a fresh array with every element clamped to <c>[min, max]</c>.
/// Diffusion use case: the SDXL UNet is fp16; intermediate Float32 values
/// outside ±65504 become ±Inf on cast and produce NaN inside the next
/// session's attention softmax. Clamping the text-encoder output to the
/// fp16 range pre-feed keeps the UNet finite.
/// </summary>
public sealed class ArrayClampFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "array_clamp";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Array;

    /// <inheritdoc />
    public static string Description =>
        "Element-wise clamp of a Float32 array to [min, max]. Returns a fresh Float32[]. " +
        "Used in fp16-UNet diffusion paths to keep upstream Float32 activations inside " +
        "the half-precision representable range before the cast.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("a",   DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("min", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("max", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ArrayClampFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.Float32));
        }
        float[] a = ActivationOps.ReadFloat32Array(args[0]);
        if (!args[1].TryToFloat(out float min) || !args[2].TryToFloat(out float max))
        {
            throw new FunctionArgumentException(Name,
                "min and max must be Float32-coercible.");
        }
        if (min > max)
        {
            throw new FunctionArgumentException(Name,
                $"min ({min}) must be <= max ({max}).");
        }
        float[] result = new float[a.Length];
        for (int i = 0; i < a.Length; i++)
        {
            float v = a[i];
            if (v < min) v = min;
            else if (v > max) v = max;
            result[i] = v;
        }
        return new ValueTask<ValueRef>(ValueRef.FromPrimitiveArray(result, DataKind.Float32));
    }
}

/// <summary>
/// <c>array_concat_last_dim(a Float32[], a_inner Int32, b Float32[], b_inner Int32) → Float32[]</c>.
/// Concatenates two <c>[outer, inner_a]</c> / <c>[outer, inner_b]</c> tensors
/// along the inner (last) dimension, producing
/// <c>[outer, inner_a + inner_b]</c> with rows interleaved: each block of
/// <c>inner_a + inner_b</c> output elements is <c>a</c>'s row followed by
/// <c>b</c>'s row for the same <c>outer</c> index.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Diffusion use case.</strong> SDXL concatenates the two text
/// encoders' per-token hidden states along the hidden dim — CLIP-L
/// <c>[1, 77, 768]</c> + OpenCLIP-G <c>[1, 77, 1280]</c> → <c>[1, 77, 2048]</c>.
/// In the flat-array representation this is per-token interleaving of the
/// 768- and 1280-wide chunks.
/// </para>
/// <para>
/// <strong>Outer derivation.</strong> The <c>outer</c> dimension is implied
/// by <c>cardinality(a) / inner_a</c>; both inputs must produce the same
/// outer count or the function throws.
/// </para>
/// </remarks>
public sealed class ArrayConcatLastDimFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "array_concat_last_dim";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Array;

    /// <inheritdoc />
    public static string Description =>
        "Concatenates two Float32 arrays along their inner (last) dimension. " +
        "Inputs are [outer, inner_a] and [outer, inner_b] flat tensors with `outer` derived from " +
        "cardinality/inner. Output is [outer, inner_a + inner_b] with per-row interleaving. " +
        "SDXL uses this to merge CLIP-L + OpenCLIP-G per-token hidden states.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("a",       DataKindMatcher.Exact(DataKind.Float32),       IsArray: ArrayMatch.Array),
                new ParameterSpec("a_inner", DataKindMatcher.Family(DataKindFamily.IntegerFamily), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("b",       DataKindMatcher.Exact(DataKind.Float32),       IsArray: ArrayMatch.Array),
                new ParameterSpec("b_inner", DataKindMatcher.Family(DataKindFamily.IntegerFamily), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ArrayConcatLastDimFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull || args[3].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.Float32));
        }
        float[] a = ActivationOps.ReadFloat32Array(args[0]);
        float[] b = ActivationOps.ReadFloat32Array(args[2]);
        if (!args[1].TryToInt32(out int aInner) || !args[3].TryToInt32(out int bInner))
        {
            throw new FunctionArgumentException(Name,
                "a_inner and b_inner must be Int32-coercible.");
        }
        if (aInner <= 0 || bInner <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"a_inner and b_inner must be positive; got [{aInner}, {bInner}].");
        }
        if (a.Length % aInner != 0)
        {
            throw new FunctionArgumentException(Name,
                $"cardinality(a)={a.Length} is not a multiple of a_inner={aInner}.");
        }
        if (b.Length % bInner != 0)
        {
            throw new FunctionArgumentException(Name,
                $"cardinality(b)={b.Length} is not a multiple of b_inner={bInner}.");
        }
        int outer = a.Length / aInner;
        if (outer != b.Length / bInner)
        {
            throw new FunctionArgumentException(Name,
                $"derived outer dims differ: a/a_inner={outer}, b/b_inner={b.Length / bInner}.");
        }
        int totalInner = aInner + bInner;
        float[] result = new float[outer * totalInner];
        for (int t = 0; t < outer; t++)
        {
            Array.Copy(a, t * aInner,         result, t * totalInner,          aInner);
            Array.Copy(b, t * bInner,         result, t * totalInner + aInner, bInner);
        }
        return new ValueTask<ValueRef>(ValueRef.FromPrimitiveArray(result, DataKind.Float32));
    }
}
