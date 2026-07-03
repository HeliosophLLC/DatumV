using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

using SkiaSharp;

namespace Heliosoph.DatumV.Functions.Scalar.Spatial;

/// <summary>
/// <c>pc_render(pc PointCloud, view_pose Float32[], width Int, height Int,
/// fov_deg Float32 [, point_size Int]) → Image</c>. Renders a point cloud to
/// an RGBA image through a pinhole camera at <c>view_pose</c> using a
/// z-buffered point splat.
/// </summary>
/// <remarks>
/// <para>
/// <c>view_pose</c> is a camera-to-world 4×4 row-major matrix — the same
/// convention the reconstruction pipeline accumulates via
/// <c>pose_from_rgbd</c> / <c>pose_compose</c>, so a frame's cumulative pose
/// drops in directly and the render shows what a camera at that trajectory
/// point would see. Pass <c>pose_identity()</c> to render from the world
/// origin, or compose with <c>pose_translate</c> to pull the viewpoint back.
/// </para>
/// <para>
/// The camera model is the exact inverse of
/// <c>point_cloud_from_depth_pinhole</c>: vertical-FOV focal derivation,
/// centered principal point, half-integer pixel centers. Unprojecting a
/// depth image and re-rendering with the same size, fov, and identity pose
/// reproduces the source picture.
/// </para>
/// <para>
/// <c>point_size</c> (default 2) is the square splat edge in pixels —
/// raise it to close holes when rendering sparse or voxel-downsampled
/// clouds at high resolution. Colorless clouds render white-on-black;
/// the background is opaque black. Null <c>pc</c> or <c>view_pose</c>
/// returns a null Image; an empty cloud renders the bare background.
/// </para>
/// </remarks>
public sealed class PcRenderFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "pc_render";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Spatial;

    /// <inheritdoc />
    public static string Description =>
        "Renders a PointCloud to an RGBA Image through a pinhole camera at a "
        + "camera-to-world view_pose (4x4 row-major Float32[16]; Float64[] "
        + "accepted and narrowed) using a z-buffered point splat. Inverse of "
        + "point_cloud_from_depth_pinhole's camera model; point_size (default "
        + "2) sets the splat edge in pixels.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("pc",        DataKindMatcher.Exact(DataKind.PointCloud)),
                new ParameterSpec("view_pose", DataKindMatcher.Family(DataKindFamily.FloatFamily), IsArray: ArrayMatch.Array),
                new ParameterSpec("width",     DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("height",    DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("fov_deg",   DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("pc",         DataKindMatcher.Exact(DataKind.PointCloud)),
                new ParameterSpec("view_pose",  DataKindMatcher.Family(DataKindFamily.FloatFamily), IsArray: ArrayMatch.Array),
                new ParameterSpec("width",      DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("height",     DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("fov_deg",    DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("point_size", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Image)),
    ];

    /// <summary>Largest render dimension accepted on either axis.</summary>
    internal const int MaxDimension = 8192;

    /// <summary>Largest splat edge accepted; beyond this the "point" reads as a tile.</summary>
    internal const int MaxPointSize = 64;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PcRenderFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Image));
        }

        float[] pose = PoseMatrixArgument.Read16(args[1], frame, Name, "view_pose");

        int width = ReadInt(args[2], "width");
        int height = ReadInt(args[3], "height");
        ValidateDimension(width, "width");
        ValidateDimension(height, "height");

        float fovDeg = args[4].ToFloat();
        ValidateFov(fovDeg);

        int pointSize = args.Length >= 6 && !args[5].IsNull ? ReadInt(args[5], "point_size") : 2;
        ValidatePointSize(pointSize);

        byte[] blob = args[0].AsPointCloud();
        SKBitmap bitmap = PointCloudRasterizer.Render(
            [blob], pose, width, height, fovDeg, pointSize);
        return new ValueTask<ValueRef>(ValueRef.FromImage(bitmap));
    }

    internal static void ValidateDimension(int value, string paramName)
    {
        if (value <= 0 || value > MaxDimension)
        {
            throw new FunctionArgumentException(
                Name, $"{paramName} must be in [1, {MaxDimension}]; got {value}.");
        }
    }

    internal static void ValidateFov(float fovDeg)
    {
        if (!(fovDeg > 0f) || !(fovDeg < 180f) || float.IsNaN(fovDeg))
        {
            throw new FunctionArgumentException(
                Name, $"fov_deg must be in (0, 180); got {fovDeg}.");
        }
    }

    internal static void ValidatePointSize(int pointSize)
    {
        if (pointSize <= 0 || pointSize > MaxPointSize)
        {
            throw new FunctionArgumentException(
                Name, $"point_size must be in [1, {MaxPointSize}]; got {pointSize}.");
        }
    }

    private static int ReadInt(ValueRef arg, string paramName)
    {
        return arg.Kind switch
        {
            DataKind.Int8 => arg.AsInt8(),
            DataKind.Int16 => arg.AsInt16(),
            DataKind.Int32 => arg.AsInt32(),
            DataKind.Int64 => checked((int)arg.AsInt64()),
            _ => throw new FunctionArgumentException(
                Name, $"{paramName} must be an integer kind; got {arg.Kind}."),
        };
    }
}
