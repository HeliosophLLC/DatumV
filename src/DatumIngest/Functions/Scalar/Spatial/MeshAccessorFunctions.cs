using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Model.Spatial;

namespace DatumIngest.Functions.Scalar.Spatial;

/// <summary>
/// <c>mesh_vertex_count(m Mesh) → Int32</c>. Number of vertices in the mesh.
/// </summary>
public sealed class MeshVertexCountFunction : IFunction, IScalarFunction, IInlineMetadataAccessor
{
    /// <inheritdoc />
    public static string Name => "mesh_vertex_count";

    /// <inheritdoc />
    public InlineAccessorField Field => InlineAccessorField.MeshVertexCount;

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Returns the number of vertices in a Mesh as Int32.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("m", DataKindMatcher.Exact(DataKind.Mesh))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Int32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<MeshVertexCountFunction>(argumentKinds);

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
        uint inlineCount = arg.InlineDataValue.MeshVertexCount;
        if (inlineCount != 0)
        {
            return new ValueTask<ValueRef>(ValueRef.FromInt32(checked((int)inlineCount)));
        }
        MeshHeader header = MeshHeader.Read(arg.AsMesh());
        return new ValueTask<ValueRef>(ValueRef.FromInt32(checked((int)header.VertexCount)));
    }
}

/// <summary>
/// <c>mesh_triangle_count(m Mesh) → Int32</c>. Number of triangles in the mesh.
/// </summary>
public sealed class MeshTriangleCountFunction : IFunction, IScalarFunction, IInlineMetadataAccessor
{
    /// <inheritdoc />
    public static string Name => "mesh_triangle_count";

    /// <inheritdoc />
    public InlineAccessorField Field => InlineAccessorField.MeshTriangleCount;

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Returns the number of triangles in a Mesh as Int32.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("m", DataKindMatcher.Exact(DataKind.Mesh))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Int32)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<MeshTriangleCountFunction>(argumentKinds);

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
        uint inlineCount = arg.InlineDataValue.MeshTriangleCount;
        if (inlineCount != 0)
        {
            return new ValueTask<ValueRef>(ValueRef.FromInt32(checked((int)inlineCount)));
        }
        MeshHeader header = MeshHeader.Read(arg.AsMesh());
        return new ValueTask<ValueRef>(ValueRef.FromInt32(checked((int)header.TriangleCount)));
    }
}

/// <summary>
/// <c>mesh_bbox_min(m Mesh) → Point3D</c>. Component-wise minimum corner of
/// the mesh's axis-aligned bounding box, in the mesh's coordinate frame.
/// </summary>
public sealed class MeshBboxMinFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "mesh_bbox_min";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Returns the component-wise minimum corner of a Mesh's axis-aligned bounding box, "
        + "in the mesh's declared coordinate frame.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("m", DataKindMatcher.Exact(DataKind.Mesh))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Point3D)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<MeshBboxMinFunction>(argumentKinds);

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
        MeshHeader header = MeshHeader.Read(arg.AsMesh());
        return new ValueTask<ValueRef>(ValueRef.FromPoint3D(header.BboxMin));
    }
}

/// <summary>
/// <c>mesh_bbox_max(m Mesh) → Point3D</c>. Component-wise maximum corner of
/// the mesh's axis-aligned bounding box, in the mesh's coordinate frame.
/// </summary>
public sealed class MeshBboxMaxFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "mesh_bbox_max";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Returns the component-wise maximum corner of a Mesh's axis-aligned bounding box, "
        + "in the mesh's declared coordinate frame.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("m", DataKindMatcher.Exact(DataKind.Mesh))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Point3D)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<MeshBboxMaxFunction>(argumentKinds);

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
        MeshHeader header = MeshHeader.Read(arg.AsMesh());
        return new ValueTask<ValueRef>(ValueRef.FromPoint3D(header.BboxMax));
    }
}

/// <summary>
/// <c>mesh_has_color(m Mesh) → Boolean</c>. True when the mesh carries
/// per-vertex RGBA color in addition to position.
/// </summary>
public sealed class MeshHasColorFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "mesh_has_color";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Returns true when a Mesh has per-vertex RGBA color.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("m", DataKindMatcher.Exact(DataKind.Mesh))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Boolean)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<MeshHasColorFunction>(argumentKinds);

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
        MeshHeader header = MeshHeader.Read(arg.AsMesh());
        return new ValueTask<ValueRef>(ValueRef.FromBoolean(header.HasColor));
    }
}

/// <summary>
/// <c>mesh_has_normals(m Mesh) → Boolean</c>. True when the mesh carries
/// per-vertex unit normals (needed for shaded rendering).
/// </summary>
public sealed class MeshHasNormalsFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "mesh_has_normals";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Returns true when a Mesh has per-vertex unit normals.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("m", DataKindMatcher.Exact(DataKind.Mesh))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Boolean)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<MeshHasNormalsFunction>(argumentKinds);

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
        MeshHeader header = MeshHeader.Read(arg.AsMesh());
        return new ValueTask<ValueRef>(ValueRef.FromBoolean(header.HasNormals));
    }
}

/// <summary>
/// <c>mesh_has_uvs(m Mesh) → Boolean</c>. True when the mesh carries per-vertex
/// UV texture coordinates. Always false for Phase 1 meshes; the accessor ships
/// now so the surface is stable when Phase 2 adds UV-carrying meshes from ONNX
/// mesh-from-image models.
/// </summary>
public sealed class MeshHasUVsFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "mesh_has_uvs";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Returns true when a Mesh has per-vertex UV texture coordinates.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("m", DataKindMatcher.Exact(DataKind.Mesh))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Boolean)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<MeshHasUVsFunction>(argumentKinds);

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
        MeshHeader header = MeshHeader.Read(arg.AsMesh());
        return new ValueTask<ValueRef>(ValueRef.FromBoolean(header.HasUVs));
    }
}

/// <summary>
/// <c>mesh_has_texture(m Mesh) → Boolean</c>. True when the mesh carries an
/// embedded encoded texture image at the blob tail. Always false for Phase 1
/// meshes; the accessor ships now so the surface is stable when Phase 2 adds
/// textured meshes from ONNX models.
/// </summary>
public sealed class MeshHasTextureFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "mesh_has_texture";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Returns true when a Mesh has an embedded encoded texture image.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("m", DataKindMatcher.Exact(DataKind.Mesh))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Boolean)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<MeshHasTextureFunction>(argumentKinds);

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
        MeshHeader header = MeshHeader.Read(arg.AsMesh());
        return new ValueTask<ValueRef>(ValueRef.FromBoolean(header.HasTexture));
    }
}
