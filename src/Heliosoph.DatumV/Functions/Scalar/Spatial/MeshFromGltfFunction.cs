using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Spatial;

/// <summary>
/// <c>mesh_from_gltf(bytes Array&lt;UInt8&gt;) → Mesh</c> /
/// <c>mesh_from_gltf(path String) → Mesh</c>. Inverse of
/// <c>mesh_to_gltf</c>: parses a binary glTF 2.0 (.glb) file (whether
/// emitted by Heliosoph.DatumV's own <see cref="GltfExporter"/>, Blender,
/// Three.js exporters, or any other standard producer) and lifts it to a
/// typed <see cref="DataKind.Mesh"/>. Closes the COPY → Parquet →
/// re-import round trip — exporting a Mesh column and then reading it
/// back via <c>mesh_from_gltf(col)</c> restores the typed value.
/// </summary>
public sealed class MeshFromGltfFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "mesh_from_gltf";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Parses a binary glTF 2.0 (.glb) file (bytes or path) and lifts it to a typed Mesh: "
        + "mesh_from_gltf(bytes Array<UInt8>) → Mesh / mesh_from_gltf(path String) → Mesh. "
        + "Reads the first primitive's POSITION, NORMAL (optional), COLOR_0 (optional), and "
        + "triangle indices. Inverse of mesh_to_gltf for round-tripping a Mesh column through "
        + "a Parquet COPY export.";

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
            ReturnType: ReturnTypeRule.Constant(DataKind.Mesh)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("path", DataKindMatcher.Exact(DataKind.String),
                    IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Mesh)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<MeshFromGltfFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Mesh));
        }

        if (arg.Kind == DataKind.String)
        {
            return ReadFromPathAsync(arg.AsString(), cancellationToken);
        }

        byte[] glbBytes = arg.AsBytes();
        byte[] blob = GltfImporter.Import(glbBytes);
        return new ValueTask<ValueRef>(ValueRef.FromMesh(blob));
    }

    private static async ValueTask<ValueRef> ReadFromPathAsync(
        string path,
        CancellationToken cancellationToken)
    {
        byte[] glbBytes = await System.IO.File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        byte[] blob = GltfImporter.Import(glbBytes);
        return ValueRef.FromMesh(blob);
    }
}
