using System.Buffers.Binary;
using System.Numerics;

using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Model.Spatial;

using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Spatial;

/// <summary>
/// <c>point_cloud_from_points(color Image, points Float32[]) → PointCloud</c>.
/// Bridges a per-pixel 3D point-map (the dense [..., H, W, 3] tensor that
/// MoGe-2 / VGGT / DUSt3R-family models emit) directly into an organized
/// <see cref="DataKind.PointCloud"/>, skipping the depth-then-unproject
/// dance the older <c>point_cloud_from_depth_*</c> family requires.
/// </summary>
/// <remarks>
/// <para>
/// <strong>When to reach for this.</strong> Point-map models already
/// solved the camera geometry internally and emit world-/camera-space
/// xyz per pixel; re-deriving xyz via <c>point_cloud_from_depth_pinhole</c>
/// would throw away the model's better intrinsics estimate (MoGe-2's
/// scale-recovery, DUSt3R's pair-wise alignment) and replace it with a
/// caller-guessed FOV. Pipe their <c>points</c> output through here
/// instead.
/// </para>
/// <para>
/// <strong>Shape.</strong> Accepts rank-3 <c>(H, W, 3)</c> or any leading
/// 1-dim wrapping (<c>(1, H, W, 3)</c>, <c>(1, 1, H, W, 3)</c>) — matches
/// the auto-squeeze convention used by <c>array_resize_2d</c> and the
/// shape-aware depth constructors. The trailing 3 must be the xyz axis;
/// any other trailing dim is a clear error.
/// </para>
/// <para>
/// <strong>Color alignment.</strong> Point-map models typically run at
/// an inference resolution different from the source image (MoGe-2
/// resizes to ~588×588 internally). Rather than asking the caller to
/// pre-resize, this function bilinearly downsamples the color image to
/// match the points grid. The output cloud's <c>Width</c>/<c>Height</c>
/// header fields are the points-grid dims, not the source-image dims —
/// downstream <c>mesh_from_organized</c> meshes at the inference
/// resolution.
/// </para>
/// <para>
/// <strong>Coordinate frame.</strong> MoGe-2 / DUSt3R follow OpenCV
/// camera convention (+x right, +y down, +z forward). To stay consistent
/// with every other PointCloud producer in the catalog we pre-flip to
/// OpenGL frame (negate y, negate z) and tag the header as
/// <see cref="PointCloudCoordinateFrame.CameraOpenGl"/>. Renderers can
/// upload positions without a basis swap.
/// </para>
/// </remarks>
public sealed class PointCloudFromPointsFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "point_cloud_from_points";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Builds an organized PointCloud directly from a per-pixel xyz point-map "
        + "(MoGe-2 / VGGT / DUSt3R 'points' output, shape [..., H, W, 3]) and a "
        + "color image. The color image is bilinearly downsampled to the points "
        + "grid so per-pixel rgb stays aligned. Output frame is CameraOpenGl. "
        + "Use this instead of point_cloud_from_depth_pinhole for point-map "
        + "models — those already solved the unprojection internally and emit "
        + "better geometry than a caller-guessed FOV can recover.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("color",  DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("points", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.PointCloud)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PointCloudFromPointsFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef colorArg = args[0];
        ValueRef pointsArg = args[1];

        if (colorArg.IsNull || pointsArg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.PointCloud));
        }

        (int h, int w) = PointsShapeOps.ReadHwFromTrailingThree(pointsArg, frame, Name);
        DataValue pointsValue = pointsArg.ToDataValue(frame.Source);
        ReadOnlySpan<float> points =
            pointsValue.AsArraySpan<float>(frame.Source, frame.SidecarRegistry);
        if (points.Length != h * w * 3)
        {
            throw new FunctionArgumentException(Name,
                $"points shape {h}×{w}×3 = {h * w * 3} elements doesn't match "
                + $"actual element count {points.Length}.");
        }

        SKBitmap colorSrc = colorArg.AsImage();
        if (colorSrc.Width <= 0 || colorSrc.Height <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"color image has non-positive dimensions ({colorSrc.Width}×{colorSrc.Height}).");
        }

        // Bilinearly resample color to the points grid. Identity resize when
        // the source already matches — Skia treats that as a copy.
        SKImageInfo rgbaInfo = new(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap colorRgba = new(rgbaInfo);
        if (!colorSrc.CopyTo(colorRgba, SKColorType.Rgba8888))
        {
            throw new FunctionArgumentException(Name,
                $"failed to convert color image to RGBA8888 (source colour type: {colorSrc.ColorType}).");
        }
        SKBitmap colorMatched;
        bool disposeMatched = false;
        if (colorRgba.Width == w && colorRgba.Height == h)
        {
            colorMatched = colorRgba;
        }
        else
        {
            colorMatched = colorRgba.Resize(rgbaInfo, SKSamplingOptions.Default)
                ?? throw new InvalidOperationException(
                    $"{Name}: SkiaSharp failed to resize color image to {w}×{h}.");
            disposeMatched = true;
        }

        try
        {
            long pointCount = (long)w * h;
            if (pointCount > uint.MaxValue)
            {
                throw new FunctionArgumentException(Name,
                    $"points grid {w}×{h} produces {pointCount} points, "
                    + "which exceeds the uint32 point-count limit.");
            }

            byte[] blob = new byte[PointCloudHeader.SizeBytes
                + pointCount * PointCloudHeader.PositionStrideBytes
                + pointCount * PointCloudHeader.ColorStrideBytes];
            Span<byte> blobSpan = blob;

            Vector3 bboxMin = new(float.PositiveInfinity);
            Vector3 bboxMax = new(float.NegativeInfinity);

            nint colorPtr = colorMatched.GetPixels();
            int offset = PointCloudHeader.SizeBytes;

            unsafe
            {
                byte* color = (byte*)colorPtr;
                for (int i = 0; i < pointCount; i++)
                {
                    int pi = (int)i * 3;
                    float xCv = points[pi + 0];
                    float yCv = points[pi + 1];
                    float zCv = points[pi + 2];

                    // CV → GL (consistent with point_cloud_from_depth_*).
                    float x = xCv;
                    float y = -yCv;
                    float z = -zCv;

                    Span<byte> slot = blobSpan.Slice(offset, PointCloudHeader.PositionStrideBytes + PointCloudHeader.ColorStrideBytes);
                    BinaryPrimitives.WriteSingleLittleEndian(slot[0..4], x);
                    BinaryPrimitives.WriteSingleLittleEndian(slot[4..8], y);
                    BinaryPrimitives.WriteSingleLittleEndian(slot[8..12], z);
                    int ci = (int)i * 4;
                    slot[12] = color[ci + 0]; // R
                    slot[13] = color[ci + 1]; // G
                    slot[14] = color[ci + 2]; // B
                    slot[15] = color[ci + 3]; // A

                    bboxMin = Vector3.Min(bboxMin, new Vector3(x, y, z));
                    bboxMax = Vector3.Max(bboxMax, new Vector3(x, y, z));

                    offset += PointCloudHeader.PositionStrideBytes + PointCloudHeader.ColorStrideBytes;
                }
            }

            PointCloudHeader header = new(
                Version: PointCloudHeader.CurrentVersion,
                Flags: PointCloudFlags.HasColor,
                CoordinateFrame: PointCloudCoordinateFrame.CameraOpenGl,
                PointCount: (uint)pointCount,
                BboxMin: bboxMin,
                BboxMax: bboxMax,
                Width: (uint)w,
                Height: (uint)h);
            header.Write(blobSpan[..PointCloudHeader.SizeBytes]);

            return new ValueTask<ValueRef>(ValueRef.FromPointCloud(blob));
        }
        finally
        {
            if (disposeMatched) colorMatched.Dispose();
        }
    }
}

/// <summary>
/// <c>points_to_depth_image(points Float32[], target_h Int, target_w Int [, invert Bool, mask Float32[]]) → Image</c>.
/// Visualizes a per-pixel point-map (the [..., H, W, 3] tensor MoGe-2 /
/// VGGT / DUSt3R-family models emit) as a grayscale depth image by
/// extracting the z component, applying per-image min-max normalisation,
/// and bilinearly resizing to the requested output dimensions.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Optional mask.</strong> MoGe-2's <c>mask</c> output (rank-3
/// <c>[..., H, W]</c>, sigmoid scores) flags per-pixel reliability.
/// Without it, sky / background pixels that the model couldn't depth-fit
/// land at huge z and compress every valid foreground pixel into the
/// bottom few percent of the brightness range — the picture looks
/// washed-out flat. Passing the mask filters the min/max computation to
/// reliable pixels (mask &gt; 0.5) and renders invalid pixels black, so
/// the foreground depth signal occupies the full visible range.
/// </para>
/// </remarks>
public sealed class PointsToDepthImageFunction : IFunction, IScalarFunction
{
    private const float MaskValidThreshold = 0.5f;

    /// <inheritdoc />
    public static string Name => "points_to_depth_image";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Visualizes a per-pixel point-map as grayscale depth: extracts z from "
        + "each xyz triplet, per-image min-max normalises, and bilinearly resizes "
        + "to (target_h, target_w). Optional mask (MoGe-2's validity sigmoid) "
        + "filters outliers from the normalisation so foreground depth keeps full "
        + "contrast. Use on point-map models (MoGe-2 / VGGT / DUSt3R) where the "
        + "ONNX output is shaped [..., H, W, 3]; the [H, W] grid comes from the "
        + "trailing axes of the array itself.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("points",   DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("target_h", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("target_w", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("invert",   DataKindMatcher.Exact(DataKind.Boolean), IsOptional: true),
                new ParameterSpec("mask",     DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array, IsOptional: true),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PointsToDepthImageFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Image));
        }

        (int h, int w) = PointsShapeOps.ReadHwFromTrailingThree(args[0], frame, Name);
        DataValue pointsValue = args[0].ToDataValue(frame.Source);
        ReadOnlySpan<float> points =
            pointsValue.AsArraySpan<float>(frame.Source, frame.SidecarRegistry);
        if (points.Length != h * w * 3)
        {
            throw new FunctionArgumentException(Name,
                $"points shape {h}×{w}×3 = {h * w * 3} elements doesn't match "
                + $"actual element count {points.Length}.");
        }

        int targetH = ReadPositiveInt(args[1], "target_h");
        int targetW = ReadPositiveInt(args[2], "target_w");
        bool invert = args.Length >= 4 && !args[3].IsNull && args[3].AsBoolean();

        // Optional validity mask: rank-2 (H, W) or rank-3 (1, H, W) Float32
        // sigmoid scores. Pixels with score > 0.5 contribute to the min/max
        // normalisation; pixels at or below the threshold render black.
        ReadOnlySpan<float> mask = ReadOnlySpan<float>.Empty;
        bool hasMask = args.Length >= 5 && !args[4].IsNull;
        if (hasMask)
        {
            (int mh, int mw) = PointsShapeOps.ReadHwFromTrailingTwoOrSqueeze(args[4], frame, Name);
            if (mh != h || mw != w)
            {
                throw new FunctionArgumentException(Name,
                    $"mask dimensions ({mh}×{mw}) don't match points grid ({h}×{w}).");
            }
            DataValue maskValue = args[4].ToDataValue(frame.Source);
            mask = maskValue.AsArraySpan<float>(frame.Source, frame.SidecarRegistry);
        }

        int planeSize = h * w;
        float[] depth = new float[planeSize];
        bool[] valid = hasMask ? new bool[planeSize] : Array.Empty<bool>();
        float min = float.PositiveInfinity;
        float max = float.NegativeInfinity;
        for (int i = 0; i < planeSize; i++)
        {
            float z = points[i * 3 + 2];
            depth[i] = z;
            bool include;
            if (hasMask)
            {
                include = mask[i] > MaskValidThreshold;
                valid[i] = include;
            }
            else
            {
                include = true;
            }
            if (include)
            {
                if (z < min) min = z;
                if (z > max) max = z;
            }
        }
        // If the mask rejected every pixel, fall back to the unmasked range
        // so the user sees something instead of a black square. Treat the
        // resulting image as a degenerate visualisation; downstream code
        // doesn't need a separate "all-masked" signal.
        if (float.IsPositiveInfinity(min))
        {
            min = 0f;
            max = 1f;
        }
        float range = (max - min) > 1e-6f ? (max - min) : 1f;

        SKImageInfo smallInfo = new(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using SKBitmap small = new(smallInfo);
        nint smallPtr = small.GetPixels();
        unsafe
        {
            byte* dst = (byte*)smallPtr;
            for (int i = 0; i < planeSize; i++)
            {
                int o = i * 4;
                if (hasMask && !valid[i])
                {
                    dst[o + 0] = 0;
                    dst[o + 1] = 0;
                    dst[o + 2] = 0;
                    dst[o + 3] = 255;
                    continue;
                }
                float v = (depth[i] - min) / range;
                if (invert) v = 1f - v;
                byte g = ToByte(v);
                dst[o + 0] = g;
                dst[o + 1] = g;
                dst[o + 2] = g;
                dst[o + 3] = 255;
            }
        }

        SKImageInfo finalInfo = new(targetW, targetH, SKColorType.Rgba8888, SKAlphaType.Opaque);
        SKBitmap final = small.Resize(finalInfo, SKSamplingOptions.Default)
            ?? throw new InvalidOperationException(
                $"{Name}: SkiaSharp failed to resize to {targetW}×{targetH}.");

        return new ValueTask<ValueRef>(ValueRef.FromImage(final));
    }

    private static byte ToByte(float v)
    {
        float scaled = v * 255f;
        if (scaled < 0f) return 0;
        if (scaled > 255f) return 255;
        return (byte)MathF.Round(scaled);
    }

    private static int ReadPositiveInt(ValueRef arg, string name)
    {
        int value = arg.Kind switch
        {
            DataKind.Int8 => arg.AsInt8(),
            DataKind.Int16 => arg.AsInt16(),
            DataKind.Int32 => arg.AsInt32(),
            DataKind.Int64 => checked((int)arg.AsInt64()),
            _ => throw new FunctionArgumentException(Name,
                $"{name} must be an integer kind, got {arg.Kind}."),
        };
        if (value <= 0)
        {
            throw new FunctionArgumentException(Name, $"{name} must be > 0, got {value}.");
        }
        return value;
    }
}

/// <summary>
/// <c>normal_to_image(normal Float32[], target_h Int, target_w Int) → Image</c>.
/// Encodes a per-pixel surface-normal field (the [..., H, W, 3] tensor
/// MoGe-2 / DSINE-family models emit) as a standard RGB normal map —
/// R = (nx + 1) × 127.5, G = (ny + 1) × 127.5, B = (nz + 1) × 127.5 —
/// then bilinearly resizes to the requested output dimensions.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> Surface normals are unit vectors
/// per pixel — they carry geometric detail (edges, curvature, micro-
/// relief) that depth visualisation tends to wash out. Encoding them as
/// RGB produces the canonical "ambient-light" shaded look that game
/// engines, materials editors, and 3D-print preview tools all share.
/// </para>
/// <para>
/// <strong>Sign convention.</strong> MoGe-2 emits normals in OpenCV
/// camera frame (+x right, +y down, +z forward). We map directly through
/// without flipping, matching the convention used by DSINE, the
/// omnidata-tools normal viz, and HuggingFace model cards. If you need
/// OpenGL-frame normals (+y up, −z forward) for a downstream shader,
/// pre-flip the channels in SQL with explicit array math; this primitive
/// stays opinion-free.
/// </para>
/// </remarks>
public sealed class NormalToImageFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "normal_to_image";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Encodes a per-pixel surface-normal field as an RGB image: maps each "
        + "(nx, ny, nz) ∈ [-1, 1] to (R, G, B) ∈ [0, 255] and bilinearly resizes "
        + "to (target_h, target_w). Use on MoGe-2 / DSINE-family normal outputs "
        + "where the ONNX tensor is shaped [..., H, W, 3]; the [H, W] grid comes "
        + "from the trailing axes of the array itself.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("normal",   DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("target_h", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("target_w", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<NormalToImageFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Image));
        }

        (int h, int w) = PointsShapeOps.ReadHwFromTrailingThree(args[0], frame, Name);
        DataValue normalValue = args[0].ToDataValue(frame.Source);
        ReadOnlySpan<float> normal =
            normalValue.AsArraySpan<float>(frame.Source, frame.SidecarRegistry);
        if (normal.Length != h * w * 3)
        {
            throw new FunctionArgumentException(Name,
                $"normal shape {h}×{w}×3 = {h * w * 3} elements doesn't match "
                + $"actual element count {normal.Length}.");
        }

        int targetH = ReadPositiveInt(args[1], "target_h");
        int targetW = ReadPositiveInt(args[2], "target_w");

        int planeSize = h * w;
        SKImageInfo smallInfo = new(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using SKBitmap small = new(smallInfo);
        nint smallPtr = small.GetPixels();
        unsafe
        {
            byte* dst = (byte*)smallPtr;
            for (int i = 0; i < planeSize; i++)
            {
                int ni = i * 3;
                float nx = normal[ni + 0];
                float ny = normal[ni + 1];
                float nz = normal[ni + 2];
                int o = i * 4;
                dst[o + 0] = ToByteNormal(nx);
                dst[o + 1] = ToByteNormal(ny);
                dst[o + 2] = ToByteNormal(nz);
                dst[o + 3] = 255;
            }
        }

        SKImageInfo finalInfo = new(targetW, targetH, SKColorType.Rgba8888, SKAlphaType.Opaque);
        SKBitmap final = small.Resize(finalInfo, SKSamplingOptions.Default)
            ?? throw new InvalidOperationException(
                $"{Name}: SkiaSharp failed to resize to {targetW}×{targetH}.");

        return new ValueTask<ValueRef>(ValueRef.FromImage(final));
    }

    private static byte ToByteNormal(float v)
    {
        // Map [-1, 1] → [0, 255]; tolerate slight out-of-range emissions
        // (cosine outputs from some exports drift a hair past the unit
        // sphere) by clamping.
        float scaled = (v + 1f) * 127.5f;
        if (scaled < 0f) return 0;
        if (scaled > 255f) return 255;
        return (byte)MathF.Round(scaled);
    }

    private static int ReadPositiveInt(ValueRef arg, string name)
    {
        int value = arg.Kind switch
        {
            DataKind.Int8 => arg.AsInt8(),
            DataKind.Int16 => arg.AsInt16(),
            DataKind.Int32 => arg.AsInt32(),
            DataKind.Int64 => checked((int)arg.AsInt64()),
            _ => throw new FunctionArgumentException(Name,
                $"{name} must be an integer kind, got {arg.Kind}."),
        };
        if (value <= 0)
        {
            throw new FunctionArgumentException(Name, $"{name} must be > 0, got {value}.");
        }
        return value;
    }
}

/// <summary>
/// Shape-decode helper shared by the point-map constructors above. Walks
/// past leading 1-dims and reads (H, W) off the two axes preceding a
/// trailing-3 xyz axis.
/// </summary>
internal static class PointsShapeOps
{
    public static (int H, int W) ReadHwFromTrailingThree(
        ValueRef pointsArg, EvaluationFrame frame, string functionName)
    {
        DataValue value = pointsArg.ToDataValue(frame.Source);
        if (!value.IsMultiDim)
        {
            throw new FunctionArgumentException(functionName,
                "points array must carry a shape — pass a shape-aware Float32 "
                + "array with trailing (H, W, 3), not a flat 1-D Float32[].");
        }
        ReadOnlySpan<int> shape = value.GetShape(frame.Source, frame.SidecarRegistry);
        if (shape.Length < 3 || shape[^1] != 3)
        {
            throw new FunctionArgumentException(functionName,
                "points array must have trailing axis of size 3 (xyz triplets); "
                + $"got shape [{string.Join(", ", shape.ToArray())}].");
        }
        for (int i = 0; i < shape.Length - 3; i++)
        {
            if (shape[i] != 1)
            {
                throw new FunctionArgumentException(functionName,
                    "points array must be 3-D (H, W, 3) or have only 1-dims before "
                    + "the trailing (H, W, 3); got shape "
                    + $"[{string.Join(", ", shape.ToArray())}].");
            }
        }
        return (shape[^3], shape[^2]);
    }

    /// <summary>
    /// Shape-decode for 2-D companion arrays (validity mask, confidence
    /// map) — accepts <c>(H, W)</c> or any leading 1-dim wrapping. Mirrors
    /// the convention used by <c>array_resize_2d</c> and the depth-only
    /// constructors so callers can pass MoGe-2's <c>mask</c> output
    /// directly without squeezing batch dims externally.
    /// </summary>
    public static (int H, int W) ReadHwFromTrailingTwoOrSqueeze(
        ValueRef arg, EvaluationFrame frame, string functionName)
    {
        DataValue value = arg.ToDataValue(frame.Source);
        if (!value.IsMultiDim)
        {
            throw new FunctionArgumentException(functionName,
                "companion array must carry a shape — pass a shape-aware Float32 "
                + "array with trailing (H, W), not a flat 1-D Float32[].");
        }
        ReadOnlySpan<int> shape = value.GetShape(frame.Source, frame.SidecarRegistry);
        if (shape.Length < 2)
        {
            throw new FunctionArgumentException(functionName,
                $"companion array must be rank ≥ 2; got shape "
                + $"[{string.Join(", ", shape.ToArray())}].");
        }
        for (int i = 0; i < shape.Length - 2; i++)
        {
            if (shape[i] != 1)
            {
                throw new FunctionArgumentException(functionName,
                    "companion array must be 2-D (H, W) or have only 1-dims before "
                    + "the trailing (H, W); got shape "
                    + $"[{string.Join(", ", shape.ToArray())}].");
            }
        }
        return (shape[^2], shape[^1]);
    }
}
