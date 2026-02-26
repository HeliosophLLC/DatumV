using DatumIngest.Execution;
using DatumIngest.Functions.Scalar.Activation;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Vector;

/// <summary>
/// <c>l2_normalize(values FLOAT32[]) → FLOAT32[]</c>. Divides each element
/// by the vector's L2 norm so the result is a unit vector. Used to project
/// embeddings onto the unit sphere before cosine-similarity comparison.
/// </summary>
/// <remarks>
/// All-zero input returns all-zero output (rather than dividing by zero and
/// emitting NaN). Same convention as scikit-learn's <c>normalize</c> with
/// <c>norm='l2'</c>.
/// </remarks>
public sealed class L2NormalizeFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "l2_normalize";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;

    /// <inheritdoc />
    public static string Description =>
        "Divides a Float32 vector by its L2 norm, returning a unit vector: " +
        "l2_normalize(values FLOAT32[]) → FLOAT32[]. " +
        "All-zero input returns all-zero (no divide-by-zero NaN).";

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
        FunctionMetadata.Validate<L2NormalizeFunction>(argumentKinds);

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

        double sumSq = 0.0;
        for (int i = 0; i < input.Length; i++) sumSq += (double)input[i] * input[i];

        float[] output = new float[input.Length];
        if (sumSq == 0.0)
        {
            // Output is already zero-initialised; nothing to do.
            return new(ValueRef.FromPrimitiveArray(output, DataKind.Float32));
        }

        float invNorm = (float)(1.0 / System.Math.Sqrt(sumSq));
        for (int i = 0; i < input.Length; i++) output[i] = input[i] * invNorm;
        return new(ValueRef.FromPrimitiveArray(output, DataKind.Float32));
    }
}

/// <summary>
/// <c>cosine_similarity(a FLOAT32[], b FLOAT32[]) → FLOAT32</c>. Standard
/// cosine similarity: <c>(a · b) / (||a|| * ||b||)</c>. Range is
/// [-1, 1]; 1 means identical direction.
/// </summary>
/// <remarks>
/// Throws when the two arrays have different lengths. Returns <c>0</c> if
/// either vector is all-zero (matches scikit-learn's behaviour and
/// sidesteps the NaN that pure mathematics would emit).
/// </remarks>
public sealed class CosineSimilarityFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "cosine_similarity";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;

    /// <inheritdoc />
    public static string Description =>
        "Cosine similarity between two Float32 vectors of equal length: " +
        "cosine_similarity(a FLOAT32[], b FLOAT32[]) → FLOAT32. " +
        "Range [-1, 1]; either-vector-zero returns 0 (no NaN).";

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
        FunctionMetadata.Validate<CosineSimilarityFunction>(argumentKinds);

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

        double dot = 0.0, sumA = 0.0, sumB = 0.0;
        for (int i = 0; i < a.Length; i++)
        {
            double ai = a[i];
            double bi = b[i];
            dot += ai * bi;
            sumA += ai * ai;
            sumB += bi * bi;
        }

        if (sumA == 0.0 || sumB == 0.0)
        {
            return new(ValueRef.FromFloat32(0f));
        }

        double sim = dot / (System.Math.Sqrt(sumA) * System.Math.Sqrt(sumB));
        return new(ValueRef.FromFloat32((float)sim));
    }
}
