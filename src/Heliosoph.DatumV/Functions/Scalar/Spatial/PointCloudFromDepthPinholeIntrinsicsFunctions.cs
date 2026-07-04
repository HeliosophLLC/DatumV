using System.Buffers.Binary;
using System.Numerics;

using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Model.Spatial;

using SkiaSharp;

namespace Heliosoph.DatumV.Functions.Scalar.Spatial;

/// <summary>
/// <c>point_cloud_from_depth_pinhole_intrinsics(color Image, depth Image|Float32[], intrinsics Float32[]) → PointCloud</c>.
/// Pinhole sibling of <see cref="PointCloudFromDepthOrthographicIntrinsicsFunction"/>.
/// Takes a 3×3 K matrix and uses per-axis focal lengths + principal point for
/// geometrically-correct unprojection — pair with metric depth (e.g.
/// <c>da3metric_large_meters</c>) for results where flat planes look
/// flat instead of curving into hills.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Pinhole vs orthographic.</strong> Pinhole scales (u, v) by the
/// forward distance — close pixels cluster near the optical axis, far pixels
/// spread to the frustum edges. This is the *physically correct* unprojection
/// when depth values represent real distances. Orthographic puts (u, v) at
/// fixed image-plane positions regardless of depth, which produces the
/// distinctive "planes-as-hills" artifact for monocular depth output.
/// </para>
/// <para>
/// <strong>Intrinsics layout.</strong> Identical to the orthographic variant —
/// 9-element row-major K matrix:
/// <c>[fx, 0, cx, 0, fy, cy, 0, 0, 1]</c>. Trailing 9 elements read so
/// batched <c>(1, 1, 3, 3)</c> shapes pass through without slicing.
/// </para>
/// <para>
/// <strong>Resolution alignment.</strong> Intrinsics must be at the same
/// resolution as the depth array. Scale fx/fy/cx/cy before passing if depth
/// was resized from the model's native input.
/// </para>
/// </remarks>
public sealed class PointCloudFromDepthPinholeIntrinsicsFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "point_cloud_from_depth_pinhole_intrinsics";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Unprojects a per-pixel depth Image into a 3D PointCloud using pinhole "
        + "projection with a 3x3 K matrix (9-element Float32[] row-major). "
        + "Sibling of point_cloud_from_depth_orthographic_intrinsics — same "
        + "arg shape, geometrically correct math. Pair with metric depth for "
        + "results where flat planes look flat. Intrinsics must match the "
        + "depth grid's resolution.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("color", DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("depth", DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("intrinsics", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.PointCloud)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("color", DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("depth", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("intrinsics", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.PointCloud)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PointCloudFromDepthPinholeIntrinsicsFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef colorArg = args[0];
        ValueRef depthArg = args[1];
        ValueRef intrinsicsArg = args[2];

        if (colorArg.IsNull || depthArg.IsNull || intrinsicsArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.PointCloud));
        }

        DataValue intrinsicsValue = intrinsicsArg.ToDataValue(frame.Source);
        ReadOnlySpan<float> K = intrinsicsValue.AsArraySpan<float>(frame.Source, frame.SidecarRegistry);
        if (K.Length < 9)
        {
            throw new FunctionArgumentException(
                Name,
                $"intrinsics must contain at least 9 Float32 values (3x3 K matrix); got {K.Length}.");
        }
        int baseIdx = K.Length - 9;
        float fx = K[baseIdx + 0];
        float fy = K[baseIdx + 4];
        float cx = K[baseIdx + 2];
        float cy = K[baseIdx + 5];

        if (!(fx > 0f) || !(fy > 0f) || !float.IsFinite(fx) || !float.IsFinite(fy)
            || !float.IsFinite(cx) || !float.IsFinite(cy))
        {
            throw new FunctionArgumentException(
                Name,
                $"intrinsics has non-positive or non-finite values: fx={fx}, fy={fy}, cx={cx}, cy={cy}.");
        }

        SKBitmap colorSrc = colorArg.AsImage();
        int width = colorSrc.Width;
        int height = colorSrc.Height;
        if (width <= 0 || height <= 0)
        {
            throw new FunctionArgumentException(
                Name, $"color image has non-positive dimensions ({width}×{height}).");
        }

        if (depthArg.IsArray && depthArg.Kind == DataKind.Float32)
        {
            return new ValueTask<ValueRef>(ExecuteShapedMetric(
                colorSrc, depthArg, frame, width, height, fx, fy, cx, cy));
        }

        return new ValueTask<ValueRef>(ExecuteImageDepth(
            colorSrc, depthArg.AsImage(), width, height, fx, fy, cx, cy));
    }

    private static ValueRef ExecuteImageDepth(
        SKBitmap colorSrc, SKBitmap depthSrc,
        int width, int height,
        float fx, float fy, float cx, float cy)
    {
        if (depthSrc.Width != width || depthSrc.Height != height)
        {
            throw new FunctionArgumentException(
                Name,
                $"color and depth dimensions must match: color={width}×{height}, "
                + $"depth={depthSrc.Width}×{depthSrc.Height}.");
        }

        SKImageInfo rgbaInfo = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap colorRgba = new(rgbaInfo);
        using SKBitmap depthRgba = new(rgbaInfo);
        if (!colorSrc.CopyTo(colorRgba, SKColorType.Rgba8888))
        {
            throw new FunctionArgumentException(
                Name, $"failed to convert color image to RGBA8888 (source: {colorSrc.ColorType}).");
        }
        if (!depthSrc.CopyTo(depthRgba, SKColorType.Rgba8888))
        {
            throw new FunctionArgumentException(
                Name, $"failed to convert depth image to RGBA8888 (source: {depthSrc.ColorType}).");
        }

        long pointCount = (long)width * height;
        if (pointCount > uint.MaxValue)
        {
            throw new FunctionArgumentException(
                Name, $"image dimensions {width}×{height} exceed uint32 point-count limit.");
        }

        // Same NearForward/FarForward range as the FOV-based path so callers
        // swapping between functions see comparable Z extents.
        const float NearForward = 0.1f;
        const float FarForward = 1.0f;
        const float ForwardRange = FarForward - NearForward;

        byte[] blob = new byte[PointCloudHeader.SizeBytes
            + pointCount * PointCloudHeader.PositionStrideBytes
            + pointCount * PointCloudHeader.ColorStrideBytes];
        Span<byte> blobSpan = blob;
        Vector3 bboxMin = new(float.PositiveInfinity);
        Vector3 bboxMax = new(float.NegativeInfinity);

        nint colorPtr = colorRgba.GetPixels();
        nint depthPtr = depthRgba.GetPixels();
        int pointOffset = PointCloudHeader.SizeBytes;

        unsafe
        {
            byte* color = (byte*)colorPtr;
            byte* depth = (byte*)depthPtr;
            for (int v = 0; v < height; v++)
            {
                for (int u = 0; u < width; u++)
                {
                    int pixelIndex = (v * width + u) * 4;
                    byte intensity = depth[pixelIndex + 0];
                    float forward = NearForward + (1f - (intensity / 255f)) * ForwardRange;

                    // Pinhole: (X, Y) scale with depth — angular position
                    // times forward distance.
                    float xCv = (u + 0.5f - cx) * forward / fx;
                    float yCv = (v + 0.5f - cy) * forward / fy;
                    float zCv = forward;

                    float x = xCv;
                    float y = -yCv;
                    float z = -zCv;

                    Span<byte> pointSlot = blobSpan.Slice(pointOffset,
                        PointCloudHeader.PositionStrideBytes + PointCloudHeader.ColorStrideBytes);
                    BinaryPrimitives.WriteSingleLittleEndian(pointSlot[0..4], x);
                    BinaryPrimitives.WriteSingleLittleEndian(pointSlot[4..8], y);
                    BinaryPrimitives.WriteSingleLittleEndian(pointSlot[8..12], z);
                    pointSlot[12] = color[pixelIndex + 0];
                    pointSlot[13] = color[pixelIndex + 1];
                    pointSlot[14] = color[pixelIndex + 2];
                    pointSlot[15] = color[pixelIndex + 3];

                    bboxMin = Vector3.Min(bboxMin, new Vector3(x, y, z));
                    bboxMax = Vector3.Max(bboxMax, new Vector3(x, y, z));
                    pointOffset += PointCloudHeader.PositionStrideBytes
                                 + PointCloudHeader.ColorStrideBytes;
                }
            }
        }

        PointCloudHeader header = new(
            Version: PointCloudHeader.CurrentVersion,
            Flags: PointCloudFlags.HasColor,
            CoordinateFrame: PointCloudCoordinateFrame.CameraOpenGl,
            PointCount: (uint)pointCount,
            BboxMin: bboxMin,
            BboxMax: bboxMax,
            Width: (uint)width,
            Height: (uint)height);
        header.Write(blobSpan[..PointCloudHeader.SizeBytes]);

        return ValueRef.FromPointCloud(blob);
    }

    private static ValueRef ExecuteShapedMetric(
        SKBitmap colorSrc, ValueRef depthArg, EvaluationFrame frame,
        int width, int height,
        float fx, float fy, float cx, float cy)
    {
        DataValue depthValue = depthArg.ToDataValue(frame.Source);
        ReadOnlySpan<int> shape = depthValue.GetShape(frame.Source, frame.SidecarRegistry);
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
                "depth array must be 2-D (h, w) or batched (..., h, w); got shape "
                + $"[{string.Join(", ", shape.ToArray())}].");
        }

        if (dh != height || dw != width)
        {
            throw new FunctionArgumentException(
                Name,
                $"depth ({dh}×{dw}) doesn't match color ({height}×{width}). "
                + "Call array_resize_2d(depth, image_height(color), image_width(color)) first.");
        }

        ReadOnlySpan<float> depthMeters =
            depthValue.AsArraySpan<float>(frame.Source, frame.SidecarRegistry);

        long pointCount = (long)width * height;
        if (pointCount > uint.MaxValue)
        {
            throw new FunctionArgumentException(
                Name, $"image dimensions {width}×{height} exceed uint32 limit.");
        }

        SKImageInfo rgbaInfo = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap colorRgba = new(rgbaInfo);
        if (!colorSrc.CopyTo(colorRgba, SKColorType.Rgba8888))
        {
            throw new FunctionArgumentException(
                Name, $"failed to convert color image to RGBA8888 (source: {colorSrc.ColorType}).");
        }

        byte[] blob = new byte[PointCloudHeader.SizeBytes
            + pointCount * PointCloudHeader.PositionStrideBytes
            + pointCount * PointCloudHeader.ColorStrideBytes];
        Span<byte> blobSpan = blob;
        Vector3 bboxMin = new(float.PositiveInfinity);
        Vector3 bboxMax = new(float.NegativeInfinity);

        nint colorPtr = colorRgba.GetPixels();
        int pointOffset = PointCloudHeader.SizeBytes;

        unsafe
        {
            byte* color = (byte*)colorPtr;
            for (int v = 0; v < height; v++)
            {
                int rowBase = v * width;
                int colorRowBase = rowBase * 4;
                for (int u = 0; u < width; u++)
                {
                    float forward = depthMeters[rowBase + u];
                    if (!(forward > 0f) || float.IsNaN(forward) || float.IsInfinity(forward))
                    {
                        forward = 0f;
                    }

                    // Pinhole: (X, Y) scale by forward.
                    float xCv = (u + 0.5f - cx) * forward / fx;
                    float yCv = (v + 0.5f - cy) * forward / fy;
                    float zCv = forward;

                    float x = xCv;
                    float y = -yCv;
                    float z = -zCv;

                    int colorOffset = colorRowBase + u * 4;
                    Span<byte> pointSlot = blobSpan.Slice(pointOffset,
                        PointCloudHeader.PositionStrideBytes + PointCloudHeader.ColorStrideBytes);
                    BinaryPrimitives.WriteSingleLittleEndian(pointSlot[0..4], x);
                    BinaryPrimitives.WriteSingleLittleEndian(pointSlot[4..8], y);
                    BinaryPrimitives.WriteSingleLittleEndian(pointSlot[8..12], z);
                    pointSlot[12] = color[colorOffset + 0];
                    pointSlot[13] = color[colorOffset + 1];
                    pointSlot[14] = color[colorOffset + 2];
                    pointSlot[15] = color[colorOffset + 3];

                    bboxMin = Vector3.Min(bboxMin, new Vector3(x, y, z));
                    bboxMax = Vector3.Max(bboxMax, new Vector3(x, y, z));
                    pointOffset += PointCloudHeader.PositionStrideBytes
                                 + PointCloudHeader.ColorStrideBytes;
                }
            }
        }

        PointCloudHeader header = new(
            Version: PointCloudHeader.CurrentVersion,
            Flags: PointCloudFlags.HasColor,
            CoordinateFrame: PointCloudCoordinateFrame.CameraOpenGl,
            PointCount: (uint)pointCount,
            BboxMin: bboxMin,
            BboxMax: bboxMax,
            Width: (uint)width,
            Height: (uint)height);
        header.Write(blobSpan[..PointCloudHeader.SizeBytes]);

        return ValueRef.FromPointCloud(blob);
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
