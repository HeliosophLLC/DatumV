using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Arrays;

/// <summary>
/// <c>array_resize_2d(arr Array&lt;Float32&gt;, dst_h Int32, dst_w Int32)
/// → Array&lt;Float32&gt;(dst_h, dst_w)</c>. Bilinear-resamples a 2D scalar
/// field onto a new pixel grid, preserving the source value's units
/// (linear interpolation is unit-preserving). Output is a shape-aware
/// multi-dim Float32 array so downstream consumers can read its dimensions
/// via <c>array_shape</c> / <c>array_get(arr, y, x)</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Rank handling.</strong> The source array must resolve to a 2-D
/// <c>(h, w)</c> grid:
/// <list type="bullet">
///   <item><description>Rank-2 inputs (<c>Array&lt;Float32&gt;(h, w)</c>)
///   are consumed directly.</description></item>
///   <item><description>Rank-3 inputs with a leading <c>1</c> dim
///   (<c>Array&lt;Float32&gt;(1, h, w)</c> — the typical ONNX
///   <c>[batch=1, h, w]</c> shape for depth / segmentation outputs) are
///   auto-squeezed; the leading 1 is treated as a batch dim and dropped.</description></item>
///   <item><description>Anything else (rank 1, rank 3 without leading 1,
///   rank ≥ 4) raises a <see cref="FunctionArgumentException"/> pointing
///   at the actual observed shape.</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Algorithm.</strong> Bilinear interpolation with the same
/// half-pixel-corner convention as PIL / OpenCV / SkiaSharp's default
/// resamplers — sample positions are <c>(src_y + 0.5) * (src_h / dst_h)
/// - 0.5</c> in source coordinates. Boundary samples clamp to the source
/// extents. Linear in the source values, so the units of the input
/// (meters for ZoeDepth / GLPN, anything else for relative depth /
/// segmentation masks / saliency maps) are preserved end-to-end.
/// </para>
/// <para>
/// <strong>Typical use.</strong> Resize a model-native depth output
/// (e.g. ZoeDepth's <c>[1, 384, 384]</c>) onto the source image's pixel
/// grid so downstream per-pixel sampling stays aligned with the color
/// image:
/// <code>
/// DECLARE depth_native Array&lt;Float32&gt; = models.zoedepth_nyu_kitti_meters(img);
/// DECLARE depth_full   Array&lt;Float32&gt; = array_resize_2d(
///     depth_native, image_height(img), image_width(img));
/// </code>
/// </para>
/// </remarks>
public sealed class ArrayResize2DFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "array_resize_2d";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Array;

    /// <inheritdoc />
    public static string Description =>
        "Bilinear-resamples a 2D Float32 array onto a new (dst_h, dst_w) grid. "
        + "Accepts rank-2 (h, w) inputs directly and rank-3 (1, h, w) inputs "
        + "with auto-squeeze of the leading batch dim. Returns a shape-aware "
        + "Array<Float32>(dst_h, dst_w). Linear interpolation preserves the "
        + "source value's units — use for metric depth, segmentation masks, "
        + "saliency maps, any single-channel field that needs resampling to "
        + "a different resolution.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("array", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("dst_h", DataKindMatcher.Family(DataKindFamily.IntegerFamily), IsArray: ArrayMatch.Scalar,
                    Metadata: new ParameterMetadata(
                        Check: new GreaterThanCheck(0m),
                        Unit: "pixels",
                        Description: "Target output height. Must be > 0.")),
                new ParameterSpec("dst_w", DataKindMatcher.Family(DataKindFamily.IntegerFamily), IsArray: ArrayMatch.Scalar,
                    Metadata: new ParameterMetadata(
                        Check: new GreaterThanCheck(0m),
                        Unit: "pixels",
                        Description: "Target output width. Must be > 0.")),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ArrayResize2DFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef arrayArg = args[0];
        if (arrayArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.Float32));
        }

        int dstH = args[1].ToInt32();
        int dstW = args[2].ToInt32();
        if (dstH <= 0 || dstW <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"dst_h and dst_w must be > 0; got dst_h={dstH}, dst_w={dstW}.");
        }

        // Resolve source dims. ONNX depth outputs typically arrive with
        // one or more leading 1-dims wrapping the (h, w) plane:
        //   (h, w)               — rank 2, direct
        //   (1, h, w)             — typical batch=1 export (DAv2, ZoeDepth)
        //   (1, 1, h, w)          — multi-view-capable export with views=1
        //                           (DAv3 large emits this shape)
        //   (1, 1, …, 1, h, w)    — any depth of leading 1-dims is squeezed
        // Anything where the leading dims aren't all 1 is ambiguous (we'd
        // have to pick a slice) and gets rejected with a clear error.
        DataValue source = arrayArg.ToDataValue(frame.Source);
        int srcH, srcW;
        if (source.IsMultiDim)
        {
            ReadOnlySpan<int> shape = source.GetShape(frame.Source, frame.SidecarRegistry);
            if (shape.Length >= 2 && AllLeadingOnes(shape))
            {
                srcH = shape[^2];
                srcW = shape[^1];
            }
            else
            {
                throw new FunctionArgumentException(Name,
                    $"input array must be 2-D (h, w) or have only 1-dims before the "
                    + $"trailing (h, w); got shape [{string.Join(", ", shape.ToArray())}]. "
                    + "Use array_get or an explicit reshape to extract the 2-D slice first.");
            }
        }
        else
        {
            throw new FunctionArgumentException(Name,
                "input array must be a shape-aware multi-dim Float32 array. "
                + "1-D Float32[] inputs have no implicit (h, w) split — declare "
                + "the source as Array<Float32>(h, w) or build it via a model "
                + "whose ONNX output shape ≥ rank 2.");
        }

        ReadOnlySpan<float> srcElements = source.AsArraySpan<float>(frame.Source, frame.SidecarRegistry);
        if (srcElements.Length != srcH * srcW)
        {
            throw new FunctionArgumentException(Name,
                $"declared shape {srcH}×{srcW} = {srcH * srcW} elements doesn't match "
                + $"the array's actual element count {srcElements.Length}.");
        }

        // Identity resize — copy + reshape, no interpolation. Common when
        // the caller's source dims happen to match the target dims (e.g.
        // a 384×384 model output projected onto a 384×384 source image).
        if (srcH == dstH && srcW == dstW)
        {
            float[] copy = srcElements.ToArray();
            return new ValueTask<ValueRef>(
                ValueRef.FromPrimitiveMultiDimArray(copy, [dstH, dstW], DataKind.Float32));
        }

        float[] dst = BilinearResample(srcElements, srcH, srcW, dstH, dstW);
        return new ValueTask<ValueRef>(
            ValueRef.FromPrimitiveMultiDimArray(dst, [dstH, dstW], DataKind.Float32));
    }

    /// <summary>
    /// True when every dimension except the trailing two is 1 — i.e. the
    /// shape is <c>(1, ..., 1, h, w)</c> for some non-negative number of
    /// leading 1-dims. Used to auto-squeeze multi-view ONNX exports that
    /// wrap a 2-D plane in batch / view / channel-1 axes.
    /// </summary>
    private static bool AllLeadingOnes(ReadOnlySpan<int> shape)
    {
        for (int i = 0; i < shape.Length - 2; i++)
        {
            if (shape[i] != 1) return false;
        }
        return true;
    }

    /// <summary>
    /// Bilinear resample with the half-pixel-corner sampling convention
    /// (PIL / OpenCV / Skia default). For each destination pixel
    /// <c>(dy, dx)</c> the source position is
    /// <c>((dy + 0.5) * sh/dh - 0.5, (dx + 0.5) * sw/dw - 0.5)</c>;
    /// boundary samples clamp to the source extents.
    /// </summary>
    private static float[] BilinearResample(
        ReadOnlySpan<float> src, int srcH, int srcW, int dstH, int dstW)
    {
        float[] dst = new float[dstH * dstW];
        float scaleY = (float)srcH / dstH;
        float scaleX = (float)srcW / dstW;
        for (int dy = 0; dy < dstH; dy++)
        {
            float sy = (dy + 0.5f) * scaleY - 0.5f;
            int y0 = (int)MathF.Floor(sy);
            int y1 = y0 + 1;
            float fy = sy - y0;
            // Clamp to valid source rows. Negative or past-end positions
            // collapse onto the boundary so the corner samples extend.
            if (y0 < 0) { y0 = 0; fy = 0f; }
            if (y1 < 0) y1 = 0;
            if (y0 >= srcH) y0 = srcH - 1;
            if (y1 >= srcH) y1 = srcH - 1;

            int row0 = y0 * srcW;
            int row1 = y1 * srcW;
            int dstRow = dy * dstW;

            for (int dx = 0; dx < dstW; dx++)
            {
                float sx = (dx + 0.5f) * scaleX - 0.5f;
                int x0 = (int)MathF.Floor(sx);
                int x1 = x0 + 1;
                float fx = sx - x0;
                if (x0 < 0) { x0 = 0; fx = 0f; }
                if (x1 < 0) x1 = 0;
                if (x0 >= srcW) x0 = srcW - 1;
                if (x1 >= srcW) x1 = srcW - 1;

                float v00 = src[row0 + x0];
                float v01 = src[row0 + x1];
                float v10 = src[row1 + x0];
                float v11 = src[row1 + x1];

                float top = v00 + (v01 - v00) * fx;
                float bot = v10 + (v11 - v10) * fx;
                dst[dstRow + dx] = top + (bot - top) * fy;
            }
        }
        return dst;
    }
}
