using System.Buffers.Binary;

using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Model.Spatial;

using SkiaSharp;

namespace DatumIngest.Functions.Scalar.Spatial;

/// <summary>
/// <c>point_cloud_count(pc PointCloud) → Int32</c>. Number of points in the cloud.
/// </summary>
public sealed class PointCloudCountFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "point_cloud_count";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Returns the number of points in a PointCloud as Int32.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("pc", DataKindMatcher.Exact(DataKind.PointCloud))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Int32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PointCloudCountFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Int32));
        }
        PointCloudHeader header = PointCloudHeader.Read(arg.AsPointCloud());
        return new ValueTask<ValueRef>(ValueRef.FromInt32(checked((int)header.PointCount)));
    }
}

/// <summary>
/// <c>point_cloud_width(pc PointCloud) → Int32</c>. Grid width for organized
/// clouds; <c>0</c> for unorganized clouds.
/// </summary>
public sealed class PointCloudWidthFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "point_cloud_width";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Returns the grid width of an organized PointCloud as Int32, or 0 when unorganized. "
        + "Pair with point_cloud_height() to recover the source-image dimensions.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("pc", DataKindMatcher.Exact(DataKind.PointCloud))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Int32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PointCloudWidthFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Int32));
        }
        PointCloudHeader header = PointCloudHeader.Read(arg.AsPointCloud());
        return new ValueTask<ValueRef>(ValueRef.FromInt32(checked((int)header.Width)));
    }
}

/// <summary>
/// <c>point_cloud_height(pc PointCloud) → Int32</c>. Grid height for organized
/// clouds; <c>0</c> for unorganized clouds.
/// </summary>
public sealed class PointCloudHeightFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "point_cloud_height";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Returns the grid height of an organized PointCloud as Int32, or 0 when unorganized.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("pc", DataKindMatcher.Exact(DataKind.PointCloud))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Int32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PointCloudHeightFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Int32));
        }
        PointCloudHeader header = PointCloudHeader.Read(arg.AsPointCloud());
        return new ValueTask<ValueRef>(ValueRef.FromInt32(checked((int)header.Height)));
    }
}

/// <summary>
/// <c>point_cloud_is_organized(pc PointCloud) → Boolean</c>. True when the cloud
/// has a non-zero (width × height) grid matching its point count, so callers may
/// derive implicit topology from the (u, v) layout.
/// </summary>
public sealed class PointCloudIsOrganizedFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "point_cloud_is_organized";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Returns true when a PointCloud has a (width × height) grid matching its point count "
        + "(produced by point_cloud_from_depth and other per-pixel constructors). "
        + "Unorganized clouds (LiDAR, decimated, photogrammetry) return false.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("pc", DataKindMatcher.Exact(DataKind.PointCloud))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Boolean)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PointCloudIsOrganizedFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Boolean));
        }
        PointCloudHeader header = PointCloudHeader.Read(arg.AsPointCloud());
        return new ValueTask<ValueRef>(ValueRef.FromBoolean(header.IsOrganized));
    }
}

/// <summary>
/// <c>point_cloud_has_color(pc PointCloud) → Boolean</c>. True when the cloud
/// carries per-point RGBA color in addition to position.
/// </summary>
public sealed class PointCloudHasColorFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "point_cloud_has_color";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Returns true when a PointCloud has per-point RGBA color. Position-only "
        + "clouds (geometry-only LiDAR, future 2-arg point_cloud_from_depth) return false.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("pc", DataKindMatcher.Exact(DataKind.PointCloud))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Boolean)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PointCloudHasColorFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Boolean));
        }
        PointCloudHeader header = PointCloudHeader.Read(arg.AsPointCloud());
        return new ValueTask<ValueRef>(ValueRef.FromBoolean(header.HasColor));
    }
}

/// <summary>
/// <c>point_cloud_bbox_min(pc PointCloud) → Point3D</c>. Component-wise minimum
/// of all positions in the cloud, in the cloud's coordinate frame.
/// </summary>
public sealed class PointCloudBboxMinFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "point_cloud_bbox_min";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Returns the component-wise minimum corner of a PointCloud's axis-aligned bounding box, "
        + "in the cloud's declared coordinate frame.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("pc", DataKindMatcher.Exact(DataKind.PointCloud))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Point3D)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PointCloudBboxMinFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Point3D));
        }
        PointCloudHeader header = PointCloudHeader.Read(arg.AsPointCloud());
        return new ValueTask<ValueRef>(ValueRef.FromPoint3D(header.BboxMin));
    }
}

/// <summary>
/// <c>point_cloud_bbox_max(pc PointCloud) → Point3D</c>. Component-wise maximum
/// of all positions in the cloud, in the cloud's coordinate frame.
/// </summary>
public sealed class PointCloudBboxMaxFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "point_cloud_bbox_max";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Returns the component-wise maximum corner of a PointCloud's axis-aligned bounding box, "
        + "in the cloud's declared coordinate frame.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("pc", DataKindMatcher.Exact(DataKind.PointCloud))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Point3D)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PointCloudBboxMaxFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Point3D));
        }
        PointCloudHeader header = PointCloudHeader.Read(arg.AsPointCloud());
        return new ValueTask<ValueRef>(ValueRef.FromPoint3D(header.BboxMax));
    }
}

/// <summary>
/// <c>point_cloud_depth(pc PointCloud) → Image</c>. Reconstructs a grayscale-as-RGBA
/// depth-map Image from an organized cloud — inverse of
/// <c>point_cloud_from_depth</c>'s per-pixel unprojection. Throws
/// <see cref="FunctionArgumentException"/> for unorganized clouds (no grid → no depth map).
/// </summary>
/// <remarks>
/// <para>
/// Read the Z component of each point in row-major (u, v) order. Z values are
/// re-normalized to [0, 255] per-cloud (max-Z → 255 = close, min-Z → 0 = far,
/// matching the <c>depth_map_to_image</c> convention of brighter = closer in
/// the OpenGL frame where -z is forward).
/// </para>
/// </remarks>
public sealed class PointCloudDepthFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "point_cloud_depth";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Reconstructs a grayscale-as-RGBA depth Image from an organized PointCloud. "
        + "Each pixel's intensity is the corresponding point's Z, per-cloud min-max normalized "
        + "(brighter = closer in the OpenGL frame). Throws for unorganized clouds.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("pc", DataKindMatcher.Exact(DataKind.PointCloud))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PointCloudDepthFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Image));
        }

        byte[] blob = arg.AsPointCloud();
        PointCloudHeader header = PointCloudHeader.Read(blob);

        if (!header.IsOrganized)
        {
            throw new FunctionArgumentException(
                Name,
                "PointCloud is unorganized — no (width × height) grid to project back into "
                + "a depth map. point_cloud_depth requires an organized cloud (typically "
                + "produced by point_cloud_from_depth).");
        }

        int width = checked((int)header.Width);
        int height = checked((int)header.Height);
        int stride = header.PointStrideBytes;
        int headerSize = PointCloudHeader.SizeBytes;
        ReadOnlySpan<byte> blobSpan = blob;

        // Two-pass: compute min/max Z to normalize, then write pixels. One pass
        // would need to defer normalization until after reading all Z values
        // anyway, so two reads through the position bytes is the same cost.
        float zMin = float.PositiveInfinity;
        float zMax = float.NegativeInfinity;
        for (int i = 0; i < header.PointCount; i++)
        {
            float z = BinaryPrimitives.ReadSingleLittleEndian(
                blobSpan.Slice(headerSize + i * stride + 8, 4));
            if (z < zMin) zMin = z;
            if (z > zMax) zMax = z;
        }

        // Degenerate flat cloud → emit a mid-gray image; avoids divide-by-zero.
        float range = zMax - zMin;
        bool flat = range <= 0f || !float.IsFinite(range);

        SKImageInfo info = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        SKBitmap output = new(info);
        nint outPtr = output.GetPixels();

        unsafe
        {
            byte* d = (byte*)outPtr;
            for (int v = 0; v < height; v++)
            {
                for (int u = 0; u < width; u++)
                {
                    int pointIdx = v * width + u;
                    float z = BinaryPrimitives.ReadSingleLittleEndian(
                        blobSpan.Slice(headerSize + pointIdx * stride + 8, 4));

                    // OpenGL frame: -z is forward, so larger (more positive) z = closer.
                    // Map [zMin, zMax] → [0, 255] with zMax = brightest = closest.
                    byte intensity = flat
                        ? (byte)128
                        : (byte)System.Math.Clamp(MathF.Round((z - zMin) / range * 255f), 0f, 255f);

                    int pixelIdx = pointIdx * 4;
                    d[pixelIdx + 0] = intensity;
                    d[pixelIdx + 1] = intensity;
                    d[pixelIdx + 2] = intensity;
                    d[pixelIdx + 3] = 255;
                }
            }
        }

        return new ValueTask<ValueRef>(ValueRef.FromImage(output));
    }
}
