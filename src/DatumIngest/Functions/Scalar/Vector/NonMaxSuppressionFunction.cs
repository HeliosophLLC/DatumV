using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions.Scalar.Activation;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Vector;

/// <summary>
/// <c>nms(boxes FLOAT32[], scores FLOAT32[], iou_threshold FLOAT32) → INT32[]</c>.
/// Greedy Non-Maximum Suppression over axis-aligned bounding boxes. Sorts
/// boxes by score descending, walks them, and keeps each one that doesn't
/// overlap a higher-scoring kept box by more than <c>iou_threshold</c>.
/// Returns the indices of kept boxes in score-descending order.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Box layout.</strong> <c>boxes</c> is a flat <c>FLOAT32[]</c> of
/// length <c>4 × N</c>, with each box laid out as <c>x1, y1, x2, y2</c>
/// (top-left + bottom-right, axis-aligned). Both ordering conventions —
/// (x1≤x2, y1≤y2) for standard image coordinates — are honoured;
/// degenerate boxes (x1==x2 or y1==y2) have zero area and never overlap
/// anything.
/// </para>
/// <para>
/// <strong>Algorithm.</strong> Classic O(N²) greedy NMS. Adequate for the
/// few-hundred-detection regime that detection models produce per frame.
/// Soft-NMS / faster index structures are post-v1.
/// </para>
/// </remarks>
public sealed class NmsFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "nms";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Vector;

    /// <inheritdoc />
    public static string Description =>
        "Greedy Non-Maximum Suppression over axis-aligned boxes: " +
        "nms(boxes FLOAT32[], scores FLOAT32[], iou_threshold FLOAT32) → INT32[]. " +
        "boxes is a flat array of length 4*N laid out as (x1, y1, x2, y2, …). " +
        "Returns kept-box indices in score-descending order.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("boxes",         DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("scores",        DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("iou_threshold", DataKindMatcher.Exact(DataKind.Float32)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Int32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<NmsFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull)
        {
            return new(ValueRef.NullArray(DataKind.Int32));
        }

        float[] boxes = ActivationOps.ReadFloat32Array(args[0]);
        float[] scores = ActivationOps.ReadFloat32Array(args[1]);
        float iouThreshold = (float)args[2].ToDouble();

        if (boxes.Length % 4 != 0)
        {
            throw new FunctionArgumentException(Name,
                $"boxes array length must be a multiple of 4 (x1,y1,x2,y2 per box), got {boxes.Length}.");
        }
        int n = boxes.Length / 4;
        if (scores.Length != n)
        {
            throw new FunctionArgumentException(Name,
                $"scores length ({scores.Length}) must equal box count ({n}, derived from boxes length / 4).");
        }
        if (iouThreshold < 0f || iouThreshold > 1f || float.IsNaN(iouThreshold))
        {
            throw new FunctionArgumentException(Name,
                $"iou_threshold must be in [0, 1], got {iouThreshold}.");
        }

        if (n == 0)
        {
            return new(ValueRef.FromPrimitiveArray(Array.Empty<int>(), DataKind.Int32));
        }

        // Sort all box indices by descending score; stable tie-break by
        // ascending index so output is deterministic when scores collide.
        int[] order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        Array.Sort(order, (a, b) =>
        {
            int cmp = scores[b].CompareTo(scores[a]);
            return cmp != 0 ? cmp : a.CompareTo(b);
        });

        // Pre-compute box areas — used in IoU's union term every comparison.
        float[] area = new float[n];
        for (int i = 0; i < n; i++)
        {
            int b = i * 4;
            float w = boxes[b + 2] - boxes[b + 0];
            float h = boxes[b + 3] - boxes[b + 1];
            area[i] = w > 0 && h > 0 ? w * h : 0f;
        }

        bool[] suppressed = new bool[n];
        List<int> kept = new();
        for (int oi = 0; oi < order.Length; oi++)
        {
            int i = order[oi];
            if (suppressed[i]) continue;
            kept.Add(i);

            int ib = i * 4;
            float ix1 = boxes[ib + 0];
            float iy1 = boxes[ib + 1];
            float ix2 = boxes[ib + 2];
            float iy2 = boxes[ib + 3];
            float ia = area[i];

            for (int oj = oi + 1; oj < order.Length; oj++)
            {
                int j = order[oj];
                if (suppressed[j]) continue;

                int jb = j * 4;
                float ox1 = System.Math.Max(ix1, boxes[jb + 0]);
                float oy1 = System.Math.Max(iy1, boxes[jb + 1]);
                float ox2 = System.Math.Min(ix2, boxes[jb + 2]);
                float oy2 = System.Math.Min(iy2, boxes[jb + 3]);

                float ow = ox2 - ox1;
                float oh = oy2 - oy1;
                if (ow <= 0 || oh <= 0) continue;

                float inter = ow * oh;
                float union = ia + area[j] - inter;
                if (union <= 0) continue;

                float iou = inter / union;
                if (iou > iouThreshold)
                {
                    suppressed[j] = true;
                }
            }
        }

        return new(ValueRef.FromPrimitiveArray(kept.ToArray(), DataKind.Int32));
    }
}
