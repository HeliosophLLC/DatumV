using DatumIngest.Execution;
using DatumIngest.Functions.Scalar.Activation;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Sam;

/// <summary>
/// <c>sam_stability_score(plane Float32[], h Int32, w Int32, delta Float32) → Float32</c>.
/// Computes SAM's stability metric on a single mask-logit plane: the IoU
/// between the mask thresholded at <c>+delta</c> versus at <c>-delta</c>.
/// Crisp boundaries push this near 1.0; fuzzy edges drag it toward 0.
/// The canonical threshold (≥ 0.95 with delta = 1.0) drops candidates
/// whose foreground/background distinction is unstable to small logit
/// perturbations.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a builtin.</strong> The metric is small but runs once per
/// candidate inside the everything-mode prompt sweep (≈ 4 × gridSize²
/// calls per row, so up to ≈ 4096 at gridSize=32). Expressing it in SQL
/// would require either a per-element loop with arithmetic or a fused
/// "threshold-and-count" helper that doesn't generalize beyond stability.
/// One builtin keeps the body's filter step readable.
/// </para>
/// <para>
/// <strong>Plane size.</strong> <c>cardinality(plane)</c> must equal
/// <c>h * w</c>; mismatches throw rather than silently producing the wrong
/// metric. SAM bodies derive h, w from <c>image_height(img)</c> /
/// <c>image_width(img)</c>.
/// </para>
/// </remarks>
public sealed class SamStabilityScoreFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "sam_stability_score";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "SAM stability metric for one mask-logit plane: IoU between the binary mask thresholded " +
        "at +delta versus at -delta. Higher = more stable boundary. Canonical filter: keep when >= 0.95 " +
        "with delta = 1.0. Plane is Float32[h*w]; throws on length mismatch.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("plane", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("h",     DataKindMatcher.Family(DataKindFamily.IntegerFamily), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("w",     DataKindMatcher.Family(DataKindFamily.IntegerFamily), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("delta", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<SamStabilityScoreFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull || args[3].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.FromFloat32(0f));
        }
        float[] plane = ActivationOps.ReadFloat32Array(args[0]);
        if (!args[1].TryToInt32(out int h) || !args[2].TryToInt32(out int w))
        {
            throw new FunctionArgumentException(Name, "h and w must be Int32-coercible.");
        }
        if (!args[3].TryToFloat(out float delta))
        {
            throw new FunctionArgumentException(Name, "delta must be Float32-coercible.");
        }
        if (h <= 0 || w <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"h and w must be positive; got [{h}, {w}].");
        }
        int expected = h * w;
        if (plane.Length != expected)
        {
            throw new FunctionArgumentException(Name,
                $"plane length {plane.Length} != h * w = {expected}.");
        }

        int intersection = 0;
        int union = 0;
        float lo = -delta;
        float hi = +delta;
        for (int p = 0; p < expected; p++)
        {
            float v = plane[p];
            bool high = v > hi;
            bool low = v > lo;
            if (high && low) intersection++;
            if (high || low) union++;
        }
        float score = union == 0 ? 0f : (float)intersection / union;
        return new ValueTask<ValueRef>(ValueRef.FromFloat32(score));
    }
}
