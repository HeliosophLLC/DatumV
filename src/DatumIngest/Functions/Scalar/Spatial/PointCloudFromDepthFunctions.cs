using System.Buffers.Binary;
using System.Numerics;

using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Model.Spatial;

using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Spatial;

/// <summary>
/// Selects how (X, Y) pixel positions scale with depth during depth-map
/// unprojection. Each <see cref="DataKind.PointCloud"/> constructor pins
/// itself to one mode at registration time so the SQL surface stays explicit
/// about which geometry it produces.
/// </summary>
internal enum DepthProjectionMode
{
    /// <summary>
    /// Pinhole camera model — angular position scales with depth. Close
    /// pixels (small forward distance) sit near the optical axis; far
    /// pixels (large forward distance) spread to the edges of the visible
    /// frustum. Physically correct when depth values represent true world
    /// distances (e.g. metric depth from ZoeDepth).
    /// </summary>
    Pinhole,

    /// <summary>
    /// Orthographic projection — each pixel's (X, Y) is fixed by its
    /// (u, v) image position, independent of depth. Depth only pushes the
    /// point forward or back along Z. The right interpretation when depth
    /// values are a relative ordering (normalized inverse depth from
    /// MiDaS / DPT) rather than real distances.
    /// </summary>
    Orthographic,
}

/// <summary>
/// <c>point_cloud_from_depth_pinhole(color Image, depth Image, fov_deg Float32) → PointCloud</c>.
/// Unprojects a per-pixel depth Image into a 3D point cloud using a pinhole
/// camera model — angular position scales with depth, so close pixels cluster
/// near the optical axis and far pixels spread to the visible-frustum edges.
/// </summary>
/// <remarks>
/// <para>
/// Reach for this variant when depth values represent real-world distances:
/// metric depth from <c>models.zoedepth_nyu_kitti</c>, calibrated RGB-D
/// sensors (RealSense, Kinect), or LiDAR-derived depth maps where the
/// resulting cloud is the ground truth for 3D reconstruction.
/// </para>
/// <para>
/// For normalized inverse-depth outputs (the standard MiDaS / DPT shape),
/// pinhole projection imposes a perspective effect that isn't really in the
/// data — the per-image min-max normalization in <c>depth_map_to_image</c>
/// throws away the absolute scale that makes pinhole math meaningful. Use
/// <c>point_cloud_from_depth_orthographic</c> for those models.
/// </para>
/// </remarks>
public sealed class PointCloudFromDepthPinholeFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "point_cloud_from_depth_pinhole";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Unprojects a per-pixel depth Image into a 3D PointCloud using a pinhole "
        + "camera model — angular position scales with depth. Use when depth values "
        + "represent real-world distances (metric depth, RGB-D sensors, LiDAR). For "
        + "normalized inverse depth from MiDaS / DPT, prefer "
        + "point_cloud_from_depth_orthographic instead.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
        BuildDepthUnprojectionSignatures();

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PointCloudFromDepthPinholeFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken) =>
        PointCloudFromDepthOps.ExecuteAsync(arguments, Name, DepthProjectionMode.Pinhole);

    internal static IReadOnlyList<FunctionSignatureVariant> BuildDepthUnprojectionSignatures() =>
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("color",   DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("depth",   DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("fov_deg", DataKindMatcher.Exact(DataKind.Float32)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.PointCloud)),
    ];
}

/// <summary>
/// <c>point_cloud_from_depth_orthographic(color Image, depth Image, fov_deg Float32) → PointCloud</c>.
/// Unprojects a per-pixel depth Image into a 3D point cloud using orthographic
/// projection — each pixel's (X, Y) position is fixed by its (u, v) image
/// coordinate, independent of depth. Depth only pushes the point forward or
/// back along Z, producing a heightfield-like reading of the depth map.
/// </summary>
/// <remarks>
/// <para>
/// Reach for this variant for normalized inverse-depth outputs (MiDaS, DPT,
/// any depth model whose values are a relative ordering rather than real
/// distances). The cloud reads as a "tilted picture" — pixel positions
/// preserved, depth as a relief — which is the honest interpretation when
/// the underlying depth values have been per-image min-max normalized.
/// </para>
/// <para>
/// <c>fov_deg</c> still controls the X/Y scale via the focal-length math
/// (it sets the size of the projected image plane), so swapping FOVs
/// uniformly resizes the cloud without changing the per-pixel relative
/// positions.
/// </para>
/// <para>
/// For real-world-distance depth (metric depth, RGB-D, LiDAR) where pinhole
/// geometry is meaningful, use <c>point_cloud_from_depth_pinhole</c>.
/// </para>
/// </remarks>
public sealed class PointCloudFromDepthOrthographicFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "point_cloud_from_depth_orthographic";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Unprojects a per-pixel depth Image into a 3D PointCloud using orthographic "
        + "projection — each pixel's (X, Y) is fixed by its (u, v) image coordinate; "
        + "depth only pushes points forward or back along Z. The honest interpretation "
        + "for normalized inverse depth (MiDaS, DPT). For real-world-distance depth, "
        + "use point_cloud_from_depth_pinhole.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
        PointCloudFromDepthPinholeFunction.BuildDepthUnprojectionSignatures();

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PointCloudFromDepthOrthographicFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken) =>
        PointCloudFromDepthOps.ExecuteAsync(arguments, Name, DepthProjectionMode.Orthographic);
}

/// <summary>
/// Shared implementation for the two depth-unprojection scalars. The only
/// difference between pinhole and orthographic is two lines in the per-pixel
/// inner loop; everything else (arg validation, image decode, header write,
/// bbox tracking, color sampling) is identical.
/// </summary>
internal static class PointCloudFromDepthOps
{
    /// <summary>
    /// Maps normalized inverse depth to a non-zero forward range. Without
    /// a non-zero NEAR plane the pinhole variant would collapse all
    /// closest-intensity pixels to the camera origin; both variants share
    /// the same range so cloud Z spans are comparable across projections.
    /// </summary>
    private const float NearForward = 0.1f;
    private const float FarForward = 1.0f;
    private const float ForwardRange = FarForward - NearForward;

    public static ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        string functionName,
        DepthProjectionMode mode)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef colorArg = args[0];
        ValueRef depthArg = args[1];
        ValueRef fovArg = args[2];

        if (colorArg.IsNull || depthArg.IsNull || fovArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.PointCloud));
        }

        float fovDeg = fovArg.AsFloat32();
        if (!(fovDeg > 0f && fovDeg < 180f))
        {
            throw new FunctionArgumentException(
                functionName,
                $"fov_deg must be in the open interval (0, 180); got {fovDeg}.");
        }

        SKBitmap colorSrc = colorArg.AsImage();
        SKBitmap depthSrc = depthArg.AsImage();

        int width = colorSrc.Width;
        int height = colorSrc.Height;
        if (width <= 0 || height <= 0)
        {
            throw new FunctionArgumentException(
                functionName,
                $"color image has non-positive dimensions ({width}×{height}).");
        }
        if (depthSrc.Width != width || depthSrc.Height != height)
        {
            throw new FunctionArgumentException(
                functionName,
                $"color and depth dimensions must match: color={width}×{height}, "
                + $"depth={depthSrc.Width}×{depthSrc.Height}.");
        }

        // Stabilize byte order across host platforms — AsImage() returns native
        // (BGRA on Windows, RGBA elsewhere). RGBA8888 is the universal indexer.
        SKImageInfo rgbaInfo = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap colorRgba = new(rgbaInfo);
        using SKBitmap depthRgba = new(rgbaInfo);
        if (!colorSrc.CopyTo(colorRgba, SKColorType.Rgba8888))
        {
            throw new FunctionArgumentException(
                functionName,
                $"failed to convert color image to RGBA8888 (source colour type: {colorSrc.ColorType}).");
        }
        if (!depthSrc.CopyTo(depthRgba, SKColorType.Rgba8888))
        {
            throw new FunctionArgumentException(
                functionName,
                $"failed to convert depth image to RGBA8888 (source colour type: {depthSrc.ColorType}).");
        }

        long pointCount = (long)width * height;
        if (pointCount > uint.MaxValue)
        {
            throw new FunctionArgumentException(
                functionName,
                $"image dimensions {width}×{height} produce {pointCount} points, "
                + $"which exceeds the uint32 point-count limit.");
        }

        // Pinhole projection constants. fov_deg is vertical FOV; the resulting
        // focal length is in pixels (rays per pixel). Orthographic mode reuses
        // the same focal value as a scale factor for (u, v) → (X, Y) so the
        // two projections produce clouds at comparable bbox extents.
        float fovRad = fovDeg * MathF.PI / 180f;
        float focalPx = (height / 2f) / MathF.Tan(fovRad / 2f);
        float cx = width / 2f;
        float cy = height / 2f;

        byte[] blob = new byte[PointCloudHeader.SizeBytes + pointCount * PointCloudHeader.PositionStrideBytes + pointCount * PointCloudHeader.ColorStrideBytes];
        Span<byte> blobSpan = blob;

        // Track the bbox in the OpenGL frame (post y/z flip).
        Vector3 bboxMin = new(float.PositiveInfinity);
        Vector3 bboxMax = new(float.NegativeInfinity);

        nint colorPtr = colorRgba.GetPixels();
        nint depthPtr = depthRgba.GetPixels();
        int pointOffset = PointCloudHeader.SizeBytes;
        bool isPinhole = mode == DepthProjectionMode.Pinhole;

        unsafe
        {
            byte* color = (byte*)colorPtr;
            byte* depth = (byte*)depthPtr;

            for (int v = 0; v < height; v++)
            {
                for (int u = 0; u < width; u++)
                {
                    int pixelIndex = (v * width + u) * 4;
                    byte intensity = depth[pixelIndex + 0]; // R channel encodes normalized inverse depth
                    float forward = NearForward + (1f - (intensity / 255f)) * ForwardRange;

                    // Pinhole: (X, Y) scale with depth so close pixels cluster
                    // near the optical axis. Orthographic: (X, Y) are fixed by
                    // pixel position so the depth map reads as a heightfield.
                    // The branch is loop-invariant — predictor handles it free.
                    float xCv, yCv;
                    if (isPinhole)
                    {
                        xCv = (u + 0.5f - cx) * forward / focalPx;
                        yCv = (v + 0.5f - cy) * forward / focalPx;
                    }
                    else
                    {
                        xCv = (u + 0.5f - cx) / focalPx;
                        yCv = (v + 0.5f - cy) / focalPx;
                    }
                    float zCv = forward;

                    // CV → GL: y down → y up, +z forward → -z forward.
                    float x = xCv;
                    float y = -yCv;
                    float z = -zCv;

                    Span<byte> pointSlot = blobSpan.Slice(pointOffset, PointCloudHeader.PositionStrideBytes + PointCloudHeader.ColorStrideBytes);
                    BinaryPrimitives.WriteSingleLittleEndian(pointSlot[0..4], x);
                    BinaryPrimitives.WriteSingleLittleEndian(pointSlot[4..8], y);
                    BinaryPrimitives.WriteSingleLittleEndian(pointSlot[8..12], z);
                    pointSlot[12] = color[pixelIndex + 0]; // R
                    pointSlot[13] = color[pixelIndex + 1]; // G
                    pointSlot[14] = color[pixelIndex + 2]; // B
                    pointSlot[15] = color[pixelIndex + 3]; // A

                    bboxMin = Vector3.Min(bboxMin, new Vector3(x, y, z));
                    bboxMax = Vector3.Max(bboxMax, new Vector3(x, y, z));

                    pointOffset += PointCloudHeader.PositionStrideBytes + PointCloudHeader.ColorStrideBytes;
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

        return new ValueTask<ValueRef>(ValueRef.FromPointCloud(blob));
    }
}
