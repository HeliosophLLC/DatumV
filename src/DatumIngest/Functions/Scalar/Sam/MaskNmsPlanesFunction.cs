using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions.Scalar.Activation;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Sam;

/// <summary>
/// <c>mask_nms_planes(planes Float32[], scores Float32[], h Int32, w Int32, iou_threshold Float32) → Array&lt;Image&gt;</c>.
/// Takes the accumulated logit planes from a SAM-style sweep
/// (one Float32 plane of length <c>h*w</c> per candidate, all concatenated)
/// plus the parallel per-candidate score array, thresholds each plane at
/// zero to a binary mask, runs IoU non-maximum-suppression sorted by score
/// descending, and emits the surviving masks as binary grayscale RGBA
/// <see cref="DataKind.Image"/> values.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a builtin.</strong> NMS over a variable-length list of masks
/// is awkward in SQL — pairwise mask-IoU requires either a 2D loop with
/// indexed access into packed byte buffers, or a nested-array carrier that
/// the engine doesn't expose cleanly. One builtin handles the threshold,
/// the IoU bookkeeping, the suppression order, and the Image materialization
/// (which we only want to pay for survivors, not every candidate).
/// </para>
/// <para>
/// <strong>Layout.</strong> <c>planes</c> is the per-row accumulator —
/// candidates are appended via <c>array_concat</c> inside the SAM body's
/// grid loop. <c>cardinality(planes)</c> must equal <c>cardinality(scores)
/// * h * w</c>; the builtin derives the candidate count from
/// <c>scores</c> and validates the plane buffer's length matches.
/// Per-plane threshold is <c>logit &gt; 0</c> (SAM convention; the decoder
/// produces signed logits at original-image dims).
/// </para>
/// <para>
/// <strong>Output.</strong> One PNG-able bitmap per survivor,
/// sized <c>w × h</c>, RGBA opaque, equal channels (white = foreground,
/// black = background). Same shape as U²-Net masks so downstream
/// <c>image_cutout(img, mask)</c> consumes the result uniformly.
/// </para>
/// </remarks>
public sealed class MaskNmsPlanesFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "mask_nms_planes";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Per-plane threshold + IoU NMS over an accumulator of mask-logit planes. " +
        "planes is Float32[N*h*w] (one h*w plane per candidate, concatenated); scores is Float32[N] " +
        "of per-candidate ranking scores (e.g. predicted IoU from a SAM decoder). " +
        "Returns Array<Image> of binary grayscale RGBA masks (foreground = white) sized w*h. " +
        "iou_threshold is the suppression cutoff over mask-IoU between pairs; SAM canonical = 0.7.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("planes",        DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("scores",        DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("h",             DataKindMatcher.Family(DataKindFamily.IntegerFamily), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("w",             DataKindMatcher.Family(DataKindFamily.IntegerFamily), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("iou_threshold", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Image))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<MaskNmsPlanesFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull || args[3].IsNull || args[4].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.FromArray(DataKind.Image, []));
        }
        float[] planes = ActivationOps.ReadFloat32Array(args[0]);
        float[] scores = ActivationOps.ReadFloat32Array(args[1]);
        if (!args[2].TryToInt32(out int h) || !args[3].TryToInt32(out int w))
        {
            throw new FunctionArgumentException(Name, "h and w must be Int32-coercible.");
        }
        if (!args[4].TryToFloat(out float iouThreshold))
        {
            throw new FunctionArgumentException(Name, "iou_threshold must be Float32-coercible.");
        }
        if (h <= 0 || w <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"h and w must be positive; got [{h}, {w}].");
        }

        int planeSize = h * w;
        int n = scores.Length;
        if (n == 0)
        {
            return new ValueTask<ValueRef>(ValueRef.FromArray(DataKind.Image, []));
        }
        if (planes.Length != (long)n * planeSize)
        {
            throw new FunctionArgumentException(Name,
                $"planes length {planes.Length} != scores.length * h * w = {(long)n * planeSize}.");
        }

        // Threshold every plane to a packed byte mask + record area. Empty
        // masks are dropped here so NMS doesn't waste pairs comparing them.
        byte[][] masks = new byte[n][];
        int[] areas = new int[n];
        bool[] kept = new bool[n];
        int liveCount = 0;
        for (int i = 0; i < n; i++)
        {
            byte[] m = new byte[planeSize];
            int offset = i * planeSize;
            int area = 0;
            for (int p = 0; p < planeSize; p++)
            {
                if (planes[offset + p] > 0f)
                {
                    m[p] = 1;
                    area++;
                }
            }
            masks[i] = m;
            areas[i] = area;
            if (area > 0)
            {
                kept[i] = true;
                liveCount++;
            }
        }
        if (liveCount == 0)
        {
            return new ValueTask<ValueRef>(ValueRef.FromArray(DataKind.Image, []));
        }

        // Sort live indices by score descending. Suppress later entries
        // overlapping a kept one above iou_threshold. Standard SAM NMS.
        int[] order = new int[liveCount];
        int oi = 0;
        for (int i = 0; i < n; i++)
        {
            if (kept[i]) order[oi++] = i;
        }
        Array.Sort(order, (a, b) => scores[b].CompareTo(scores[a]));

        bool[] suppressed = new bool[liveCount];
        List<int> survivors = new();
        for (int i = 0; i < liveCount; i++)
        {
            if (suppressed[i]) continue;
            int idxA = order[i];
            survivors.Add(idxA);
            for (int j = i + 1; j < liveCount; j++)
            {
                if (suppressed[j]) continue;
                int idxB = order[j];
                if (MaskIou(masks[idxA], masks[idxB], areas[idxA], areas[idxB]) > iouThreshold)
                {
                    suppressed[j] = true;
                }
            }
        }

        ValueRef[] result = new ValueRef[survivors.Count];
        for (int s = 0; s < survivors.Count; s++)
        {
            result[s] = ValueRef.FromImage(
                SamMaskOps.BuildBinaryMaskBitmap(masks[survivors[s]], w, h));
        }
        return new ValueTask<ValueRef>(ValueRef.FromArray(DataKind.Image, result));
    }

    private static float MaskIou(byte[] a, byte[] b, int areaA, int areaB)
    {
        int intersection = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != 0 && b[i] != 0) intersection++;
        }
        int union = areaA + areaB - intersection;
        return union == 0 ? 0f : (float)intersection / union;
    }
}
