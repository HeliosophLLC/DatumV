using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Spatial;

/// <summary>
/// <c>pointcloud_from_ply(bytes Array&lt;UInt8&gt;) → PointCloud</c> /
/// <c>pointcloud_from_ply(path String) → PointCloud</c>. Inverse of
/// <c>pc_export_ply</c>: parses a binary little-endian PLY file (whether
/// emitted by Heliosoph.DatumV's own <see cref="PlyExporter"/>, MeshLab,
/// CloudCompare, Open3D, or any other standard producer) and lifts it to
/// a typed <see cref="DataKind.PointCloud"/>. Closes the COPY → Parquet →
/// re-import round trip — exporting a PointCloud column and then reading
/// it back via <c>pointcloud_from_ply(col)</c> restores the typed value.
/// </summary>
public sealed class PointCloudFromPlyFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "pointcloud_from_ply";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Parses a binary little-endian PLY file (bytes or path) and lifts it to a typed PointCloud: "
        + "pointcloud_from_ply(bytes Array<UInt8>) → PointCloud / "
        + "pointcloud_from_ply(path String) → PointCloud. Recognises position + RGB color; other "
        + "vertex properties (normals, alpha, intensity) are skipped at parse time. Inverse of "
        + "pc_export_ply for round-tripping a PointCloud column through a Parquet COPY export.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("bytes", DataKindMatcher.Exact(DataKind.UInt8),
                    IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.PointCloud)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("path", DataKindMatcher.Exact(DataKind.String),
                    IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.PointCloud)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PointCloudFromPlyFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.PointCloud));
        }

        if (arg.Kind == DataKind.String)
        {
            return ReadFromPathAsync(arg.AsString(), cancellationToken);
        }

        byte[] plyBytes = arg.AsBytes();
        byte[] blob = PlyImporter.Import(plyBytes);
        return new ValueTask<ValueRef>(ValueRef.FromPointCloud(blob));
    }

    private static async ValueTask<ValueRef> ReadFromPathAsync(
        string path,
        CancellationToken cancellationToken)
    {
        byte[] plyBytes = await System.IO.File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        byte[] blob = PlyImporter.Import(plyBytes);
        return ValueRef.FromPointCloud(blob);
    }
}
