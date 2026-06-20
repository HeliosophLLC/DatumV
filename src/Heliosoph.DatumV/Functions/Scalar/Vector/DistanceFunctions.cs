using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions.Scalar.Activation;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Vector;

/// <summary>
/// <c>euclidean_distance(a FLOAT32[], b FLOAT32[]) → FLOAT32</c>. L2
/// distance: <c>sqrt(Σ (aᵢ − bᵢ)²)</c>. The standard "straight-line"
/// distance between two embedding vectors.
/// </summary>
/// <remarks>
/// Throws on length mismatch (matching <c>cosine_similarity</c> /
/// <c>dot_product</c>). Accumulates in <c>double</c> to keep precision on
/// long vectors, then narrows to <c>Float32</c>.
/// </remarks>
public sealed class EuclideanDistanceFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "euclidean_distance";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;

    /// <inheritdoc />
    public static string Description =>
        "Euclidean (L2) distance between two Float32 vectors of equal length: " +
        "euclidean_distance(a FLOAT32[], b FLOAT32[]) → FLOAT32.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("a", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("b", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<EuclideanDistanceFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull)
        {
            return new(ValueRef.Null(DataKind.Float32));
        }

        float[] a = ActivationOps.ReadFloat32Array(args[0]);
        float[] b = ActivationOps.ReadFloat32Array(args[1]);
        if (a.Length != b.Length)
        {
            throw new FunctionArgumentException(Name,
                $"vectors must have the same length, got {a.Length} and {b.Length}.");
        }

        double sumSq = 0.0;
        for (int i = 0; i < a.Length; i++)
        {
            double diff = (double)a[i] - b[i];
            sumSq += diff * diff;
        }
        return new(ValueRef.FromFloat32((float)System.Math.Sqrt(sumSq)));
    }
}

/// <summary>
/// <c>manhattan_distance(a FLOAT32[], b FLOAT32[]) → FLOAT32</c>. L1
/// distance: <c>Σ |aᵢ − bᵢ|</c>. The taxicab metric — sum of axis-aligned
/// step lengths between the two points.
/// </summary>
/// <remarks>
/// Throws on length mismatch. Accumulates in <c>double</c> for precision.
/// </remarks>
public sealed class ManhattanDistanceFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "manhattan_distance";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;

    /// <inheritdoc />
    public static string Description =>
        "Manhattan (L1) distance between two Float32 vectors of equal length: " +
        "manhattan_distance(a FLOAT32[], b FLOAT32[]) → FLOAT32.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("a", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("b", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ManhattanDistanceFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull)
        {
            return new(ValueRef.Null(DataKind.Float32));
        }

        float[] a = ActivationOps.ReadFloat32Array(args[0]);
        float[] b = ActivationOps.ReadFloat32Array(args[1]);
        if (a.Length != b.Length)
        {
            throw new FunctionArgumentException(Name,
                $"vectors must have the same length, got {a.Length} and {b.Length}.");
        }

        double sum = 0.0;
        for (int i = 0; i < a.Length; i++)
        {
            sum += System.Math.Abs((double)a[i] - b[i]);
        }
        return new(ValueRef.FromFloat32((float)sum));
    }
}

/// <summary>
/// <c>hamming_distance(a STRING, b STRING) → FLOAT32</c>. Counts the number
/// of positions at which the two strings differ. Standard definition
/// requires equal lengths.
/// </summary>
/// <remarks>
/// Comparison is over UTF-16 code units, so a single non-BMP character
/// (surrogate pair) counts as two positions. Throws on length mismatch.
/// Returned as <c>Float32</c> to stay in the same numeric class as the
/// vector distance metrics.
/// </remarks>
public sealed class HammingDistanceFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "hamming_distance";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;

    /// <inheritdoc />
    public static string Description =>
        "Hamming distance between two equal-length strings: " +
        "hamming_distance(a STRING, b STRING) → FLOAT32. " +
        "Counts positions where characters differ; throws on length mismatch.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("a", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("b", DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<HammingDistanceFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull)
        {
            return new(ValueRef.Null(DataKind.Float32));
        }

        string a = args[0].AsString();
        string b = args[1].AsString();
        if (a.Length != b.Length)
        {
            throw new FunctionArgumentException(Name,
                $"strings must have the same length, got {a.Length} and {b.Length}.");
        }

        int distance = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i]) distance++;
        }
        return new(ValueRef.FromFloat32(distance));
    }
}
