using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Spatial;

/// <summary>
/// <c>mesh_to_gltf(m Mesh) → UInt8[]</c>. Serializes a Mesh to binary
/// glTF 2.0 (.glb) bytes — a single-file artifact opened by Blender,
/// Unity, Three.js, web browsers' built-in 3D viewer, and any other
/// modern 3D consumer.
/// </summary>
public sealed class MeshToGltfFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "mesh_to_gltf";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Serializes a Mesh to binary glTF 2.0 (.glb) bytes — single-file 3D asset "
        + "for Blender / Unity / Three.js / web viewers. Vertex colors via "
        + "KHR_materials_unlit. Always emits in glTF-standard right-handed +Y-up "
        + "frame; auto-converts from CameraOpenCv source meshes.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("m", DataKindMatcher.Exact(DataKind.Mesh))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.UInt8))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<MeshToGltfFunction>(argumentKinds);

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
        byte[] glb = GltfExporter.Export(arg.AsMesh(), generator: "Heliosoph.DatumV");
        return new ValueTask<ValueRef>(ValueRef.FromBytes(DataKind.UInt8, glb, isArray: true));
    }
}

/// <summary>
/// <c>mesh_to_obj(m Mesh) → UInt8[]</c>. Serializes a Mesh to Wavefront
/// OBJ (UTF-8 text bytes). Per-vertex colors emitted via the
/// <c>v X Y Z R G B</c> extension recognized by MeshLab, CloudCompare,
/// Open3D, and Blender.
/// </summary>
public sealed class MeshToObjFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "mesh_to_obj";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Serializes a Mesh to Wavefront OBJ as UTF-8 text bytes — interchange format "
        + "for MeshLab / CloudCompare / Open3D / Blender. Vertex colors via the "
        + "'v X Y Z R G B' extension. Always emits in OpenGL right-handed +Y-up frame; "
        + "auto-converts from CameraOpenCv source meshes.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("m", DataKindMatcher.Exact(DataKind.Mesh))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.UInt8))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<MeshToObjFunction>(argumentKinds);

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
        byte[] obj = ObjExporter.Export(arg.AsMesh(), generator: "Heliosoph.DatumV");
        return new ValueTask<ValueRef>(ValueRef.FromBytes(DataKind.UInt8, obj, isArray: true));
    }
}

/// <summary>
/// <c>mesh_to_stl(m Mesh) → UInt8[]</c>. Serializes a Mesh to binary
/// STL (STereoLithography), the universal 3D-printing interchange.
/// Every slicer (Bambu Studio, PrusaSlicer, Cura, Lychee, ChiTuBox)
/// reads STL natively. Loses color / normals / UVs by format design.
/// </summary>
public sealed class MeshToStlFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "mesh_to_stl";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Serializes a Mesh to binary STL bytes — the universal 3D-printing format "
        + "for Bambu Studio / PrusaSlicer / Cura / Lychee / ChiTuBox. Loses color "
        + "and per-vertex normals (STL stores only triangle positions + face normals). "
        + "Always emits in OpenGL right-handed +Y-up frame; auto-converts from "
        + "CameraOpenCv source meshes.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("m", DataKindMatcher.Exact(DataKind.Mesh))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.UInt8))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<MeshToStlFunction>(argumentKinds);

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
        byte[] stl = StlExporter.Export(arg.AsMesh(), generator: "Heliosoph.DatumV");
        return new ValueTask<ValueRef>(ValueRef.FromBytes(DataKind.UInt8, stl, isArray: true));
    }
}
