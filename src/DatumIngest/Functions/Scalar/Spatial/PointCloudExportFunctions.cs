using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Spatial;

/// <summary>
/// <c>point_cloud_to_ply(pc PointCloud) → UInt8[]</c>. Serializes a
/// PointCloud to binary PLY bytes — the universal interchange for
/// MeshLab, CloudCompare, Open3D, PCL, and Blender's PLY importer.
/// Always emits in OpenGL right-handed +Y-up frame; auto-converts from
/// <c>CameraOpenCv</c> source clouds.
/// </summary>
public sealed class PointCloudToPlyFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "point_cloud_to_ply";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Spatial;

    /// <inheritdoc />
    public static string Description =>
        "Serializes a PointCloud to binary PLY bytes — universal interchange "
        + "for MeshLab / CloudCompare / Open3D / PCL / Blender. Emits "
        + "binary_little_endian PLY with x/y/z floats and (when present) "
        + "red/green/blue uchar per point. Always emits in OpenGL +Y-up "
        + "frame; auto-converts from CameraOpenCv source clouds.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("pc", DataKindMatcher.Exact(DataKind.PointCloud))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.UInt8))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PointCloudToPlyFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.UInt8));
        }
        byte[] ply = PlyExporter.Export(arg.AsPointCloud(), generator: "DatumIngest");
        return new ValueTask<ValueRef>(ValueRef.FromBytes(DataKind.UInt8, ply, isArray: true));
    }
}
