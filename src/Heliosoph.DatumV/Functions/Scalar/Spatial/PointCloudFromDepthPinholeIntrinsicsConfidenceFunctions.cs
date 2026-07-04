using System.Buffers.Binary;
using System.Numerics;

using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Model.Spatial;

using SkiaSharp;

namespace Heliosoph.DatumV.Functions.Scalar.Spatial;

/// <summary>
/// <c>point_cloud_from_depth_pinhole_intrinsics_with_confidence(color Image,
/// depth Float32[], confidence Float32[], intrinsics Float32[], min_confidence Float32) → PointCloud</c>.
/// Completes the pinhole constructor family: K-matrix unprojection
/// (<see cref="PointCloudFromDepthPinholeIntrinsicsFunction"/>) combined with
/// confidence-gated emission (<see cref="PointCloudFromDepthPinholeWithConfidenceFunction"/>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this variant matters.</strong> A reconstruction pipeline that
/// estimates frame-to-frame poses with <c>pose_from_rgbd(…, K)</c> must
/// unproject its clouds with the <em>same</em> K — mixing K-based poses with
/// FOV-based unprojection puts the two in different camera geometries, and
/// the mismatch surfaces as world warp under camera rotation (floors tilt
/// when the video pans). This is the constructor that keeps the geometry
/// consistent when the K comes from a model's intrinsics head (e.g. the
/// <c>da3_base_full</c> struct) and low-confidence pixels
/// still need to be culled.
/// </para>
/// <para>
/// <strong>Intrinsics layout.</strong> Same as the other <c>_intrinsics</c>
/// constructors and <c>pose_from_rgbd</c>: row-major 3×3 K
/// (<c>[fx, 0, cx, 0, fy, cy, 0, 0, 1]</c>), trailing 9 elements read so
/// batched <c>(1, 1, 3, 3)</c> shapes pass through. K must be expressed at
/// the depth grid's resolution.
/// </para>
/// <para>
/// <strong>Everything else</strong> matches
/// <c>point_cloud_from_depth_pinhole_with_confidence</c>: shape-aware 2-D
/// depth/confidence matching the color dimensions, pixels dropped when
/// <c>confidence &lt; min_confidence</c> or depth is non-positive/non-finite,
/// CameraOpenGl output, always unorganized.
/// </para>
/// </remarks>
public sealed class PointCloudFromDepthPinholeIntrinsicsWithConfidenceFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "point_cloud_from_depth_pinhole_intrinsics_with_confidence";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Pinhole depth unprojection using a 3x3 K matrix (9-element row-major "
        + "Float32[], batched shapes accepted) with confidence-gated pixel "
        + "emission. Use when poses come from pose_from_rgbd with the same K — "
        + "keeps the pose and cloud geometries consistent. K must match the "
        + "depth grid's resolution.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("color",          DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("depth",          DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("confidence",     DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("intrinsics",     DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("min_confidence", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.PointCloud)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PointCloudFromDepthPinholeIntrinsicsWithConfidenceFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull || args[2].IsNull || args[3].IsNull || args[4].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.PointCloud));
        }

        DataValue intrinsicsValue = args[3].ToDataValue(frame.Source);
        ReadOnlySpan<float> K = intrinsicsValue.AsArraySpan<float>(frame.Source, frame.SidecarRegistry);
        if (K.Length < 9)
        {
            throw new FunctionArgumentException(
                Name,
                $"intrinsics must contain at least 9 Float32 values (3x3 K matrix); got {K.Length}.");
        }
        int kBase = K.Length - 9;
        float fx = K[kBase + 0];
        float fy = K[kBase + 4];
        float cx = K[kBase + 2];
        float cy = K[kBase + 5];
        if (!(fx > 0f) || !(fy > 0f) || !float.IsFinite(fx) || !float.IsFinite(fy)
            || !float.IsFinite(cx) || !float.IsFinite(cy))
        {
            throw new FunctionArgumentException(
                Name,
                $"intrinsics has non-positive or non-finite focal/principal-point: "
                + $"fx={fx}, fy={fy}, cx={cx}, cy={cy}.");
        }

        if (!args[4].TryToFloat(out float minConfidence))
        {
            throw new FunctionArgumentException(
                Name, $"min_confidence of kind {args[4].Kind} could not be widened to Float32.");
        }

        SKBitmap colorSrc = args[0].AsImage();
        int width = colorSrc.Width;
        int height = colorSrc.Height;
        if (width <= 0 || height <= 0)
        {
            throw new FunctionArgumentException(
                Name, $"color image has non-positive dimensions ({width}×{height}).");
        }

        // Materialize both into managed arrays BEFORE any further arena work —
        // a second ToDataValue can grow the arena and invalidate earlier spans.
        float[] depthMeters = ReadShapedFloat2D(args[1], frame, "depth", height, width).ToArray();
        float[] confidence = ReadShapedFloat2D(args[2], frame, "confidence", height, width).ToArray();

        SKImageInfo rgbaInfo = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap colorRgba = new(rgbaInfo);
        if (!colorSrc.CopyTo(colorRgba, SKColorType.Rgba8888))
        {
            throw new FunctionArgumentException(
                Name, $"failed to convert color image to RGBA8888 (source: {colorSrc.ColorType}).");
        }

        int keepCount = 0;
        for (int v = 0; v < height; v++)
        {
            int rowBase = v * width;
            for (int u = 0; u < width; u++)
            {
                float c = confidence[rowBase + u];
                if (c >= minConfidence && !float.IsNaN(c)) keepCount++;
            }
        }

        int outStride = PointCloudHeader.PositionStrideBytes + PointCloudHeader.ColorStrideBytes;
        byte[] outBlob = new byte[PointCloudHeader.SizeBytes + (long)keepCount * outStride];
        Span<byte> outSpan = outBlob;
        Vector3 bboxMin = new(float.PositiveInfinity);
        Vector3 bboxMax = new(float.NegativeInfinity);

        nint colorPtr = colorRgba.GetPixels();
        int writeOffset = PointCloudHeader.SizeBytes;

        unsafe
        {
            byte* color = (byte*)colorPtr;
            for (int v = 0; v < height; v++)
            {
                int rowBase = v * width;
                int colorRowBase = rowBase * 4;
                for (int u = 0; u < width; u++)
                {
                    float c = confidence[rowBase + u];
                    if (!(c >= minConfidence) || float.IsNaN(c)) continue;

                    float forward = depthMeters[rowBase + u];
                    if (!(forward > 0f) || float.IsNaN(forward) || float.IsInfinity(forward)) continue;

                    // Pinhole with per-axis focal + model principal point.
                    float xCv = (u + 0.5f - cx) * forward / fx;
                    float yCv = (v + 0.5f - cy) * forward / fy;
                    float zCv = forward;

                    float x = xCv;
                    float y = -yCv;
                    float z = -zCv;

                    int colorOffset = colorRowBase + u * 4;
                    Span<byte> outSlot = outSpan.Slice(writeOffset, outStride);
                    BinaryPrimitives.WriteSingleLittleEndian(outSlot[0..4], x);
                    BinaryPrimitives.WriteSingleLittleEndian(outSlot[4..8], y);
                    BinaryPrimitives.WriteSingleLittleEndian(outSlot[8..12], z);
                    outSlot[12] = color[colorOffset + 0];
                    outSlot[13] = color[colorOffset + 1];
                    outSlot[14] = color[colorOffset + 2];
                    outSlot[15] = color[colorOffset + 3];

                    bboxMin = Vector3.Min(bboxMin, new Vector3(x, y, z));
                    bboxMax = Vector3.Max(bboxMax, new Vector3(x, y, z));
                    writeOffset += outStride;
                }
            }
        }

        int actualWritten = (writeOffset - PointCloudHeader.SizeBytes) / outStride;
        if (actualWritten != keepCount)
        {
            byte[] trimmed = new byte[PointCloudHeader.SizeBytes + (long)actualWritten * outStride];
            outSpan[..writeOffset].CopyTo(trimmed);
            outBlob = trimmed;
            outSpan = outBlob;
            keepCount = actualWritten;
        }

        if (keepCount == 0)
        {
            bboxMin = Vector3.Zero;
            bboxMax = Vector3.Zero;
        }

        PointCloudHeader outHeader = new(
            Version: PointCloudHeader.CurrentVersion,
            Flags: PointCloudFlags.HasColor,
            CoordinateFrame: PointCloudCoordinateFrame.CameraOpenGl,
            PointCount: (uint)keepCount,
            BboxMin: bboxMin,
            BboxMax: bboxMax,
            Width: 0,
            Height: 0);
        outHeader.Write(outSpan[..PointCloudHeader.SizeBytes]);

        return new ValueTask<ValueRef>(ValueRef.FromPointCloud(outBlob));
    }

    private static ReadOnlySpan<float> ReadShapedFloat2D(
        ValueRef arg, EvaluationFrame frame, string paramName, int expectedH, int expectedW)
    {
        DataValue value = arg.ToDataValue(frame.Source);
        if (!value.IsMultiDim)
        {
            throw new FunctionArgumentException(
                Name,
                $"{paramName} must be a shape-aware Float32 array (2-D or batched); got flat 1-D.");
        }

        ReadOnlySpan<int> shape = value.GetShape(frame.Source, frame.SidecarRegistry);
        int dh, dw;
        if (shape.Length >= 2 && AllLeadingOnes(shape))
        {
            dh = shape[^2];
            dw = shape[^1];
        }
        else
        {
            throw new FunctionArgumentException(
                Name,
                $"{paramName} array must be 2-D (h, w) or batched (..., h, w); got shape "
                + $"[{string.Join(", ", shape.ToArray())}].");
        }

        if (dh != expectedH || dw != expectedW)
        {
            throw new FunctionArgumentException(
                Name,
                $"{paramName} dimensions ({dh}×{dw}) don't match color image "
                + $"({expectedH}×{expectedW}). Call array_resize_2d({paramName}, "
                + "image_height(color), image_width(color)) first.");
        }

        return value.AsArraySpan<float>(frame.Source, frame.SidecarRegistry);
    }

    private static bool AllLeadingOnes(ReadOnlySpan<int> shape)
    {
        for (int i = 0; i < shape.Length - 2; i++)
        {
            if (shape[i] != 1) return false;
        }
        return true;
    }
}
