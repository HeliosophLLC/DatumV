using System.Buffers.Binary;
using System.Numerics;

using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Model.Spatial;

using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Spatial;

/// <summary>
/// <c>point_cloud_from_depth_orthographic_intrinsics(color Image, depth Image|Float32[], intrinsics Float32[]) → PointCloud</c>.
/// Sibling to <see cref="PointCloudFromDepthOrthographicFunction"/> that
/// takes a model-predicted 3×3 camera intrinsics matrix directly instead of
/// computing focal length from a hard-coded vertical FOV. Use when the
/// depth model also emits an intrinsics head (e.g.
/// <c>depth_anything_v3_large_full.intrinsics</c>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Intrinsics layout (row-major).</strong> The argument is a
/// 9-element <c>Float32[]</c> interpreted as the 3×3 K matrix:
/// </para>
/// <code>
/// K = [ fx   0  cx ]    intrinsics[0..3] = [ fx,  0, cx ]
///     [  0  fy  cy ]    intrinsics[3..6] = [  0, fy, cy ]
///     [  0   0   1 ]    intrinsics[6..9] = [  0,  0,  1 ]
/// </code>
/// <para>
/// Only positions <c>[0]=fx</c>, <c>[2]=cx</c>, <c>[4]=fy</c>, <c>[5]=cy</c>
/// are read. Element [8] (homogeneous 1) and the zero placeholders are
/// ignored — accepting the full 9-element matrix matches the shape ONNX
/// intrinsics heads emit, so callers can pass the array straight through
/// without slicing.
/// </para>
/// <para>
/// <strong>Resolution alignment.</strong> The intrinsics must be expressed
/// at the SAME resolution as the depth grid the function samples. If your
/// model emits intrinsics at the model's input resolution (e.g. 518×518)
/// but you've resized depth back to source resolution, scale the matrix
/// first: <c>fx *= source_width / 518</c>, <c>fy *= source_height / 518</c>,
/// <c>cx *= source_width / 518</c>, <c>cy *= source_height / 518</c>.
/// Mismatched resolutions produce a cloud at the wrong scale.
/// </para>
/// </remarks>
public sealed class PointCloudFromDepthOrthographicIntrinsicsFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "point_cloud_from_depth_orthographic_intrinsics";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Unprojects a per-pixel depth Image into a 3D PointCloud using orthographic "
        + "projection with a model-predicted 3x3 intrinsics matrix (K). Same shape "
        + "as point_cloud_from_depth_orthographic but uses fx, fy, cx, cy from K[0,0], "
        + "K[1,1], K[0,2], K[1,2] instead of computing focal length from a hardcoded "
        + "fov_deg. Pass the model's intrinsics output as a 9-element Float32[] (3x3 "
        + "K row-major). Caller is responsible for scaling intrinsics if depth is at "
        + "a different resolution than the model's input.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        // Image-based depth (normalized inverse depth — MiDaS / DPT shape).
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("color", DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("depth", DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("intrinsics", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.PointCloud)),
        // Array-based depth (shape-aware metric Float32[h,w]).
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
        FunctionMetadata.Validate<PointCloudFromDepthOrthographicIntrinsicsFunction>(argumentKinds);

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

        // Read intrinsics — accept any shape that has at least 9 elements in
        // a row-major 3x3 K layout. ONNX intrinsics heads often emit shape
        // (1, 1, 3, 3) for [batch=1, views=1, 3, 3]; that flattens to 9 in
        // row-major order, so a length check (>=9) covers both squeezed and
        // batched inputs.
        DataValue intrinsicsValue = intrinsicsArg.ToDataValue(frame.Source);
        ReadOnlySpan<float> K = intrinsicsValue.AsArraySpan<float>(frame.Source, frame.SidecarRegistry);
        if (K.Length < 9)
        {
            throw new FunctionArgumentException(
                Name,
                $"intrinsics must contain at least 9 Float32 values (3x3 K matrix); got {K.Length}.");
        }

        // The last 9 elements are the K matrix — supports batched shapes
        // like (1, 1, 3, 3) without forcing the caller to squeeze. Trailing
        // matrix is the canonical one for batch=1/views=1.
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
                $"intrinsics has non-positive or non-finite focal/principal-point: "
                + $"fx={fx}, fy={fy}, cx={cx}, cy={cy}.");
        }

        SKBitmap colorSrc = colorArg.AsImage();
        int width = colorSrc.Width;
        int height = colorSrc.Height;
        if (width <= 0 || height <= 0)
        {
            throw new FunctionArgumentException(
                Name,
                $"color image has non-positive dimensions ({width}×{height}).");
        }

        if (depthArg.IsArray && depthArg.Kind == DataKind.Float32)
        {
            return new ValueTask<ValueRef>(ExecuteShapedMetric(
                colorSrc, depthArg, frame, width, height, fx, fy, cx, cy));
        }

        return new ValueTask<ValueRef>(ExecuteImageDepth(
            colorSrc, depthArg.AsImage(), width, height, fx, fy, cx, cy));
    }

    /// <summary>
    /// Normalized-inverse-depth path. R channel of the depth image encodes
    /// the inverse depth byte → forward range, exactly as the FOV-based
    /// orthographic constructor does. Only the projection math differs.
    /// </summary>
    private static ValueRef ExecuteImageDepth(
        SKBitmap colorSrc,
        SKBitmap depthSrc,
        int width,
        int height,
        float fx,
        float fy,
        float cx,
        float cy)
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
                Name,
                $"failed to convert color image to RGBA8888 (source colour type: {colorSrc.ColorType}).");
        }
        if (!depthSrc.CopyTo(depthRgba, SKColorType.Rgba8888))
        {
            throw new FunctionArgumentException(
                Name,
                $"failed to convert depth image to RGBA8888 (source colour type: {depthSrc.ColorType}).");
        }

        long pointCount = (long)width * height;
        if (pointCount > uint.MaxValue)
        {
            throw new FunctionArgumentException(
                Name,
                $"image dimensions {width}×{height} produce {pointCount} points, "
                + $"which exceeds the uint32 point-count limit.");
        }

        // Same NearForward/FarForward range as the FOV-based ortho constructor —
        // we hold this constant so callers can swap between the two functions
        // and get clouds at comparable Z extents.
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

                    // Orthographic with per-axis intrinsics: X/Y scale by (u-cx)/fx
                    // and (v-cy)/fy, independent of depth.
                    float xCv = (u + 0.5f - cx) / fx;
                    float yCv = (v + 0.5f - cy) / fy;
                    float zCv = forward;

                    // CV → GL: y down → y up, +z forward → -z forward.
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

    /// <summary>
    /// Metric-depth path. The depth argument is a shape-aware Float32 array
    /// whose values are real-world forward distances; pair with the
    /// model's `_meters` variant when the model also emits intrinsics.
    /// </summary>
    private static ValueRef ExecuteShapedMetric(
        SKBitmap colorSrc,
        ValueRef depthArg,
        EvaluationFrame frame,
        int width,
        int height,
        float fx,
        float fy,
        float cx,
        float cy)
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
                "depth array must be 2-D (h, w) or have only 1-dims before the "
                + "trailing (h, w); got shape "
                + $"[{string.Join(", ", shape.ToArray())}].");
        }

        if (dh != height || dw != width)
        {
            throw new FunctionArgumentException(
                Name,
                $"depth array dimensions ({dh}×{dw}) don't match color image "
                + $"({height}×{width}). Call array_resize_2d(depth, "
                + "image_height(color), image_width(color)) first to align.");
        }

        ReadOnlySpan<float> depthMeters =
            depthValue.AsArraySpan<float>(frame.Source, frame.SidecarRegistry);

        long pointCount = (long)width * height;
        if (pointCount > uint.MaxValue)
        {
            throw new FunctionArgumentException(
                Name,
                $"image dimensions {width}×{height} produce {pointCount} points, "
                + "which exceeds the uint32 point-count limit.");
        }

        SKImageInfo rgbaInfo = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap colorRgba = new(rgbaInfo);
        if (!colorSrc.CopyTo(colorRgba, SKColorType.Rgba8888))
        {
            throw new FunctionArgumentException(
                Name,
                $"failed to convert color image to RGBA8888 (source colour type: {colorSrc.ColorType}).");
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

                    float xCv = (u + 0.5f - cx) / fx;
                    float yCv = (v + 0.5f - cy) / fy;
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
