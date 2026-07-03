using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions.Scalar.Activation;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Vector;

/// <summary>
/// <c>nearest_centroid(centroids FLOAT32[k,d], vec FLOAT32[]) → INT32</c>. Returns
/// the 1-based index of the centroid nearest to <c>vec</c> by Euclidean distance.
/// Row-stream counterpart to <c>kmeans_fit_agg</c> — label every row with its
/// cluster id, or classify against any hand-picked anchor set.
/// </summary>
/// <remarks>
/// The centroid argument is a bare k×d matrix (multi-dim or flat row-major), not
/// the k-means model struct, so it works with centroids from any source: a
/// <c>kmeans_fit_agg</c> model's <c>centroids</c> field, a table of stored
/// anchors, or a literal. Indices are 1-based to compose with SQL array
/// subscripting (<c>centroids[nearest_centroid(...)]</c>); ties break to the
/// lowest index.
/// </remarks>
public sealed class NearestCentroidFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "nearest_centroid";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;

    /// <inheritdoc />
    public static string Description =>
        "1-based index of the Euclidean-nearest centroid row: "
        + "nearest_centroid(centroids FLOAT32[k,d], vec FLOAT32[]) → INT32. Ties break to the lowest index.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("centroids", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("vec", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Int32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<NearestCentroidFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull)
        {
            return new(ValueRef.Null(DataKind.Int32));
        }

        float[] centroids = ActivationOps.ReadFloat32Array(args[0]);
        float[] vec = ActivationOps.ReadFloat32Array(args[1]);

        int d = vec.Length;
        if (d < 1 || centroids.Length < 1)
        {
            throw new FunctionArgumentException(Name,
                "centroids and vector must both be non-empty.");
        }
        if (centroids.Length % d != 0)
        {
            throw new FunctionArgumentException(Name,
                $"centroid matrix carries {centroids.Length} elements, which is not a multiple of the "
                + $"vector dimensionality {d}; expected a k×{d} matrix.");
        }
        int k = centroids.Length / d;

        int best = 0;
        double bestDist = double.MaxValue;
        for (int c = 0; c < k; c++)
        {
            int row = c * d;
            double dist = 0;
            for (int i = 0; i < d; i++)
            {
                double delta = (double)centroids[row + i] - vec[i];
                dist += delta * delta;
            }
            if (dist < bestDist)
            {
                bestDist = dist;
                best = c;
            }
        }

        return new(ValueRef.FromInt32(best + 1));
    }
}
