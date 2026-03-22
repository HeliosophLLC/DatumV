using System.Buffers.Binary;
using System.Numerics;

using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Model.Spatial;

using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Spatial;

/// <summary>
/// <c>point_cloud_from_depth(color Image, depth Image, fov_deg Float32) → PointCloud</c>.
/// Unprojects a per-pixel depth-map Image into a colored 3D point cloud using a
/// pinhole camera model with vertical field-of-view in degrees.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this is for.</strong> Turn a depth-estimator output (e.g.
/// <c>models.midas_small</c>, <c>models.dpt_large</c>) plus the original RGB
/// frame into a renderable 3D structure. The output <see cref="DataKind.PointCloud"/>
/// is organized (one point per pixel, row-major), so a downstream consumer can
/// render it as splats or derive implicit triangles from the (u, v) grid.
/// </para>
/// <para>
/// <strong>Inputs.</strong>
/// <list type="bullet">
/// <item><c>color</c> — RGBA source image. Each pixel becomes the corresponding
///   point's color (rgba8).</item>
/// <item><c>depth</c> — single-channel-as-RGBA depth map (R = G = B = normalized
///   inverse depth, A = 255), the standard output of <c>depth_map_to_image</c>
///   on MiDaS/DPT-family models. Must have the same dimensions as <c>color</c>.</item>
/// <item><c>fov_deg</c> — vertical field-of-view in degrees, in (0, 180). Matches
///   the Three.js <c>PerspectiveCamera</c> default convention; pick a value that
///   roughly matches the source camera (phone wide ~70°, DSLR portrait ~40°,
///   depth cameras ~55–65°). The exact number rarely matters for visualization
///   — it scales X/Y proportionally.</item>
/// </list>
/// </para>
/// <para>
/// <strong>Unprojection math.</strong> Per pixel <c>(u, v)</c> with normalized
/// inverse depth <c>d ∈ [0, 1]</c> (R-channel / 255):
/// <code>
/// forward  = 1 - d                  // 0 = at camera, 1 = far horizon
/// focal_px = (H / 2) / tan(fov / 2) // pixels per radian (vertical)
/// X_cv = (u + 0.5 - W/2) * forward / focal_px
/// Y_cv = (v + 0.5 - H/2) * forward / focal_px
/// Z_cv = forward
/// </code>
/// Output is then converted to <see cref="PointCloudCoordinateFrame.CameraOpenGl"/>
/// (right-handed, +y up, −z forward) so downstream renderers can upload positions
/// without a basis swap: <c>X = X_cv, Y = -Y_cv, Z = -Z_cv</c>.
/// </para>
/// <para>
/// <strong>Scale.</strong> Because <c>depth_map_to_image</c> per-image min-max
/// normalizes, the output cloud lives in a normalized [-1, 0] Z range — there
/// is no real-world scale. Metric depth models (e.g. <c>zoedepth-nyu-kitti</c>)
/// emit meters before normalization; a future metric variant of this function
/// will accept the raw Float32 depth and preserve units.
/// </para>
/// </remarks>
public sealed class PointCloudFromDepthFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "point_cloud_from_depth";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Unprojects a per-pixel depth Image into a colored 3D PointCloud using a "
        + "pinhole camera model: point_cloud_from_depth(color, depth, fov_deg) → PointCloud. "
        + "Depth must be a grayscale-as-RGBA inverse-depth map matching color's dimensions "
        + "(output of depth_map_to_image on MiDaS/DPT-family models). fov_deg is the "
        + "vertical field-of-view in (0, 180). Output is organized, OpenGL-frame (-z forward).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
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

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PointCloudFromDepthFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef colorArg = args[0];
        ValueRef depthArg = args[1];
        ValueRef fovArg   = args[2];

        if (colorArg.IsNull || depthArg.IsNull || fovArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.PointCloud));
        }

        float fovDeg = fovArg.AsFloat32();
        if (!(fovDeg > 0f && fovDeg < 180f))
        {
            throw new FunctionArgumentException(
                Name,
                $"fov_deg must be in the open interval (0, 180); got {fovDeg}.");
        }

        SKBitmap colorSrc = colorArg.AsImage();
        SKBitmap depthSrc = depthArg.AsImage();

        int width = colorSrc.Width;
        int height = colorSrc.Height;
        if (width <= 0 || height <= 0)
        {
            throw new FunctionArgumentException(
                Name,
                $"color image has non-positive dimensions ({width}×{height}).");
        }
        if (depthSrc.Width != width || depthSrc.Height != height)
        {
            throw new FunctionArgumentException(
                Name,
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

        // Pinhole projection constants.
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
                    float forward = 1f - (intensity / 255f);

                    float xCv = (u + 0.5f - cx) * forward / focalPx;
                    float yCv = (v + 0.5f - cy) * forward / focalPx;
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
