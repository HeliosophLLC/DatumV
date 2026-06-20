using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions.Scalar.Activation;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Vector;

/// <summary>
/// Shared helpers for the <c>vec_*</c> scalar reductions over
/// <c>FLOAT32[]</c>. Multi-dim Float32 arrays reduce across the whole
/// tensor — <see cref="ActivationOps.ReadFloat32Array"/> returns the
/// flat backing buffer regardless of declared shape.
/// </summary>
internal static class VecReductionOps
{
    internal static IReadOnlyList<FunctionSignatureVariant> UnaryFloat32Reduction { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("x", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
    ];
}

/// <summary>
/// <c>vec_sum(x FLOAT32[]) → FLOAT32</c>. Sum of all elements; flattens
/// across multi-dim arrays. Accumulates in <c>double</c> and narrows.
/// Empty input returns <c>0</c> (the additive identity).
/// </summary>
public sealed class VecSumFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "vec_sum";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;
    /// <inheritdoc />
    public static string Description =>
        "Sum of all elements in a Float32 vector (or flattened tensor): " +
        "vec_sum(x FLOAT32[]) → FLOAT32. Empty input returns 0.";
    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures => VecReductionOps.UnaryFloat32Reduction;
    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<VecSumFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull) return new(ValueRef.Null(DataKind.Float32));

        float[] x = ActivationOps.ReadFloat32Array(arg);
        double sum = 0.0;
        for (int i = 0; i < x.Length; i++) sum += x[i];
        return new(ValueRef.FromFloat32((float)sum));
    }
}

/// <summary>
/// <c>vec_mean(x FLOAT32[]) → FLOAT32</c>. Arithmetic mean. Empty input
/// returns null — there is no meaningful mean of nothing.
/// </summary>
public sealed class VecMeanFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "vec_mean";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;
    /// <inheritdoc />
    public static string Description =>
        "Arithmetic mean of a Float32 vector: vec_mean(x FLOAT32[]) → FLOAT32. " +
        "Empty input returns null.";
    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures => VecReductionOps.UnaryFloat32Reduction;
    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<VecMeanFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull) return new(ValueRef.Null(DataKind.Float32));

        float[] x = ActivationOps.ReadFloat32Array(arg);
        if (x.Length == 0) return new(ValueRef.Null(DataKind.Float32));

        double sum = 0.0;
        for (int i = 0; i < x.Length; i++) sum += x[i];
        return new(ValueRef.FromFloat32((float)(sum / x.Length)));
    }
}

/// <summary>
/// <c>vec_min(x FLOAT32[]) → FLOAT32</c>. Smallest element. Empty input
/// returns null. NaN comparison follows IEEE-754: NaN values do not
/// replace the current best, so they are effectively skipped unless every
/// element is NaN.
/// </summary>
public sealed class VecMinFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "vec_min";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;
    /// <inheritdoc />
    public static string Description =>
        "Minimum element of a Float32 vector: vec_min(x FLOAT32[]) → FLOAT32. " +
        "Empty input returns null.";
    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures => VecReductionOps.UnaryFloat32Reduction;
    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<VecMinFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull) return new(ValueRef.Null(DataKind.Float32));

        float[] x = ActivationOps.ReadFloat32Array(arg);
        if (x.Length == 0) return new(ValueRef.Null(DataKind.Float32));

        float best = x[0];
        for (int i = 1; i < x.Length; i++) if (x[i] < best) best = x[i];
        return new(ValueRef.FromFloat32(best));
    }
}

/// <summary>
/// <c>vec_max(x FLOAT32[]) → FLOAT32</c>. Largest element. Empty input
/// returns null. NaN-handling mirrors <see cref="VecMinFunction"/>.
/// </summary>
public sealed class VecMaxFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "vec_max";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;
    /// <inheritdoc />
    public static string Description =>
        "Maximum element of a Float32 vector: vec_max(x FLOAT32[]) → FLOAT32. " +
        "Empty input returns null.";
    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures => VecReductionOps.UnaryFloat32Reduction;
    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<VecMaxFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull) return new(ValueRef.Null(DataKind.Float32));

        float[] x = ActivationOps.ReadFloat32Array(arg);
        if (x.Length == 0) return new(ValueRef.Null(DataKind.Float32));

        float best = x[0];
        for (int i = 1; i < x.Length; i++) if (x[i] > best) best = x[i];
        return new(ValueRef.FromFloat32(best));
    }
}

/// <summary>
/// <c>vec_var(x FLOAT32[]) → FLOAT32</c>. Population variance
/// <c>Σ(xᵢ − μ)² / n</c> (divisor <c>n</c>, not <c>n − 1</c>). Empty input
/// returns null.
/// </summary>
public sealed class VecVarFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "vec_var";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;
    /// <inheritdoc />
    public static string Description =>
        "Population variance of a Float32 vector (divisor n): " +
        "vec_var(x FLOAT32[]) → FLOAT32. Empty input returns null.";
    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures => VecReductionOps.UnaryFloat32Reduction;
    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<VecVarFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull) return new(ValueRef.Null(DataKind.Float32));

        float[] x = ActivationOps.ReadFloat32Array(arg);
        if (x.Length == 0) return new(ValueRef.Null(DataKind.Float32));

        return new(ValueRef.FromFloat32((float)PopulationVariance(x)));
    }

    internal static double PopulationVariance(float[] x)
    {
        double sum = 0.0;
        for (int i = 0; i < x.Length; i++) sum += x[i];
        double mean = sum / x.Length;
        double sumSq = 0.0;
        for (int i = 0; i < x.Length; i++)
        {
            double d = x[i] - mean;
            sumSq += d * d;
        }
        return sumSq / x.Length;
    }
}

/// <summary>
/// <c>vec_std(x FLOAT32[]) → FLOAT32</c>. Population standard deviation —
/// square root of <see cref="VecVarFunction"/>. Empty input returns null.
/// </summary>
public sealed class VecStdFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "vec_std";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;
    /// <inheritdoc />
    public static string Description =>
        "Population standard deviation of a Float32 vector (divisor n): " +
        "vec_std(x FLOAT32[]) → FLOAT32. Empty input returns null.";
    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures => VecReductionOps.UnaryFloat32Reduction;
    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<VecStdFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull) return new(ValueRef.Null(DataKind.Float32));

        float[] x = ActivationOps.ReadFloat32Array(arg);
        if (x.Length == 0) return new(ValueRef.Null(DataKind.Float32));

        return new(ValueRef.FromFloat32((float)System.Math.Sqrt(VecVarFunction.PopulationVariance(x))));
    }
}

/// <summary>
/// <c>vec_median(x FLOAT32[]) → FLOAT32</c>. Median element. For even-
/// length input returns the average of the two centre values (matching
/// NumPy). Empty input returns null.
/// </summary>
/// <remarks>
/// Allocates and sorts a copy of the input — O(n log n). Sufficient for the
/// embedding-sized vectors this is built for; revisit with quickselect if a
/// hot path emerges.
/// </remarks>
public sealed class VecMedianFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "vec_median";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;
    /// <inheritdoc />
    public static string Description =>
        "Median of a Float32 vector (average of the two centre values for even length): " +
        "vec_median(x FLOAT32[]) → FLOAT32. Empty input returns null.";
    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures => VecReductionOps.UnaryFloat32Reduction;
    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<VecMedianFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull) return new(ValueRef.Null(DataKind.Float32));

        float[] src = ActivationOps.ReadFloat32Array(arg);
        if (src.Length == 0) return new(ValueRef.Null(DataKind.Float32));

        float[] sorted = (float[])src.Clone();
        Array.Sort(sorted);
        int n = sorted.Length;
        float median = (n & 1) == 1
            ? sorted[n / 2]
            : (float)(((double)sorted[n / 2 - 1] + sorted[n / 2]) * 0.5);
        return new(ValueRef.FromFloat32(median));
    }
}

/// <summary>
/// <c>vec_norm(x FLOAT32[]) → FLOAT32</c> / <c>vec_norm(x FLOAT32[], p
/// FLOAT32) → FLOAT32</c>. <c>p</c>-norm: <c>(Σ |xᵢ|^p)^(1/p)</c>. Default
/// <c>p = 2</c> (Euclidean). Pass <c>'infinity'::float4</c> for the
/// max-norm (<c>max |xᵢ|</c>). Empty input returns <c>0</c>.
/// </summary>
public sealed class VecNormFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "vec_norm";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;
    /// <inheritdoc />
    public static string Description =>
        "Lp norm of a Float32 vector (default p=2; pass 'infinity'::float4 for max-norm): " +
        "vec_norm(x FLOAT32[], [p FLOAT32]) → FLOAT32. Empty input returns 0.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("x", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("x", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("p", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<VecNormFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull) return new(ValueRef.Null(DataKind.Float32));

        float[] x = ActivationOps.ReadFloat32Array(args[0]);

        float p = 2f;
        if (args.Length == 2)
        {
            if (args[1].IsNull) return new(ValueRef.Null(DataKind.Float32));
            p = args[1].AsFloat32();
        }
        if (p <= 0f && !float.IsPositiveInfinity(p))
        {
            throw new FunctionArgumentException(Name,
                $"p must be positive (or +infinity for max-norm), got {p}.");
        }

        if (x.Length == 0) return new(ValueRef.FromFloat32(0f));

        if (float.IsPositiveInfinity(p))
        {
            float maxAbs = 0f;
            for (int i = 0; i < x.Length; i++)
            {
                float a = System.Math.Abs(x[i]);
                if (a > maxAbs) maxAbs = a;
            }
            return new(ValueRef.FromFloat32(maxAbs));
        }

        if (p == 2f)
        {
            double sumSq = 0.0;
            for (int i = 0; i < x.Length; i++) sumSq += (double)x[i] * x[i];
            return new(ValueRef.FromFloat32((float)System.Math.Sqrt(sumSq)));
        }

        if (p == 1f)
        {
            double sumAbs = 0.0;
            for (int i = 0; i < x.Length; i++) sumAbs += System.Math.Abs((double)x[i]);
            return new(ValueRef.FromFloat32((float)sumAbs));
        }

        double sum = 0.0;
        for (int i = 0; i < x.Length; i++)
        {
            sum += System.Math.Pow(System.Math.Abs((double)x[i]), p);
        }
        return new(ValueRef.FromFloat32((float)System.Math.Pow(sum, 1.0 / p)));
    }
}

/// <summary>
/// <c>vec_count_nonzero(x FLOAT32[]) → FLOAT32</c>. Number of non-zero
/// elements. Returns <c>0</c> on empty input. NaN is non-zero (it is
/// neither equal to nor comparable with zero).
/// </summary>
public sealed class VecCountNonzeroFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "vec_count_nonzero";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;
    /// <inheritdoc />
    public static string Description =>
        "Count of non-zero elements in a Float32 vector: " +
        "vec_count_nonzero(x FLOAT32[]) → FLOAT32. Empty input returns 0; NaN counts as non-zero.";
    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures => VecReductionOps.UnaryFloat32Reduction;
    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<VecCountNonzeroFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull) return new(ValueRef.Null(DataKind.Float32));

        float[] x = ActivationOps.ReadFloat32Array(arg);
        int count = 0;
        for (int i = 0; i < x.Length; i++) if (x[i] != 0f) count++;
        return new(ValueRef.FromFloat32(count));
    }
}

/// <summary>
/// <c>vec_any(x FLOAT32[]) → FLOAT32</c>. Returns <c>1</c> if any element
/// is non-zero, else <c>0</c>. Empty input returns <c>0</c> (vacuous
/// false).
/// </summary>
public sealed class VecAnyFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "vec_any";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;
    /// <inheritdoc />
    public static string Description =>
        "Returns 1 if any element of a Float32 vector is non-zero, else 0: " +
        "vec_any(x FLOAT32[]) → FLOAT32. Empty input returns 0.";
    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures => VecReductionOps.UnaryFloat32Reduction;
    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<VecAnyFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull) return new(ValueRef.Null(DataKind.Float32));

        float[] x = ActivationOps.ReadFloat32Array(arg);
        for (int i = 0; i < x.Length; i++)
        {
            if (x[i] != 0f) return new(ValueRef.FromFloat32(1f));
        }
        return new(ValueRef.FromFloat32(0f));
    }
}

/// <summary>
/// <c>vec_all(x FLOAT32[]) → FLOAT32</c>. Returns <c>1</c> if every
/// element is non-zero, else <c>0</c>. Empty input returns <c>1</c>
/// (vacuous truth — matches NumPy <c>np.all([])</c>).
/// </summary>
public sealed class VecAllFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "vec_all";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;
    /// <inheritdoc />
    public static string Description =>
        "Returns 1 if every element of a Float32 vector is non-zero, else 0: " +
        "vec_all(x FLOAT32[]) → FLOAT32. Empty input returns 1 (vacuous truth).";
    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures => VecReductionOps.UnaryFloat32Reduction;
    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<VecAllFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull) return new(ValueRef.Null(DataKind.Float32));

        float[] x = ActivationOps.ReadFloat32Array(arg);
        for (int i = 0; i < x.Length; i++)
        {
            if (x[i] == 0f) return new(ValueRef.FromFloat32(0f));
        }
        return new(ValueRef.FromFloat32(1f));
    }
}

/// <summary>
/// <c>vec_product(x FLOAT32[]) → FLOAT32</c>. Product of all elements;
/// accumulates in <c>double</c>. Empty input returns <c>1</c> (the
/// multiplicative identity).
/// </summary>
public sealed class VecProductFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "vec_product";
    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;
    /// <inheritdoc />
    public static string Description =>
        "Product of all elements in a Float32 vector: " +
        "vec_product(x FLOAT32[]) → FLOAT32. Empty input returns 1.";
    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures => VecReductionOps.UnaryFloat32Reduction;
    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<VecProductFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull) return new(ValueRef.Null(DataKind.Float32));

        float[] x = ActivationOps.ReadFloat32Array(arg);
        double product = 1.0;
        for (int i = 0; i < x.Length; i++) product *= x[i];
        return new(ValueRef.FromFloat32((float)product));
    }
}
