using System.Buffers.Binary;
using System.Numerics;

using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Model.Spatial;

namespace Heliosoph.DatumV.Functions.Scalar.Spatial;

/// <summary>
/// <c>mesh_from_organized(pc PointCloud) → Mesh</c>. Promotes an organized
/// point cloud (one point per pixel in row-major (u, v) order) to an explicit
/// triangle mesh. Each grid cell (u, v)→(u+1, v+1) becomes two triangles
/// (CCW winding for the OpenGL frame); cells whose corner depths span more
/// than 5% of the cloud's bbox Z range are skipped so depth-edges produce
/// topology breaks rather than rubber-sheet skirts.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Inherits color from the source cloud</strong> — if the cloud has
/// per-point RGBA, the mesh emits per-vertex RGBA at the same colors.
/// Coordinate frame is inherited unchanged.
/// </para>
/// <para>
/// <strong>Always emits per-vertex normals</strong> computed from the
/// triangle topology (normalized sum of adjacent face normals). Gives smooth
/// shading across continuous surfaces and sharp edges at the discontinuity
/// breaks.
/// </para>
/// <para>
/// <strong>Orphan vertices are tolerated.</strong> When the discontinuity
/// threshold rejects a cell, its 4 corner vertices are still emitted —
/// they're valid 3D points, just unreferenced by any triangle. Keeps the
/// construction algorithm to a single linear pass; a future
/// <c>mesh_prune_orphans</c> can compact when storage savings matter.
/// </para>
/// </remarks>
public sealed class MeshFromOrganizedFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "mesh_from_organized";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Promotes an organized PointCloud to an explicit triangle Mesh. Each grid cell "
        + "becomes two triangles; depth discontinuities (>5% of bbox Z range) produce "
        + "topology breaks rather than rubber-sheet skirts. Inherits color; computes "
        + "per-vertex normals. Throws for unorganized clouds.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("pc", DataKindMatcher.Exact(DataKind.PointCloud))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Mesh)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<MeshFromOrganizedFunction>(argumentKinds);

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
        ValueRef mesh = MeshConstructionOps.PromoteOrganizedCloud(arg, Name);
        return new ValueTask<ValueRef>(mesh);
    }
}

/// <summary>
/// <c>mesh_from_depth_orthographic(color Image, depth Image, fov_deg Float32) → Mesh</c>.
/// Composes <c>point_cloud_from_depth_orthographic</c> with
/// <c>mesh_from_organized</c> — unprojects a depth-map Image into an
/// organized cloud, then triangulates. The "honest" mesh shape for
/// normalized inverse depth (MiDaS, DPT, ZoeDepth visualization output).
/// </summary>
public sealed class MeshFromDepthOrthographicFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "mesh_from_depth_orthographic";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Unprojects a depth Image into a 3D Mesh using orthographic projection: each "
        + "pixel's (X, Y) is fixed by its image position, depth pushes points along Z. "
        + "Triangulates the resulting organized cloud with depth-discontinuity edge "
        + "handling. Use for normalized inverse depth (MiDaS, DPT). For real-world "
        + "distances, use mesh_from_depth_pinhole.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
        BuildDepthMeshSignatures();

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<MeshFromDepthOrthographicFunction>(argumentKinds);

    /// <inheritdoc />
    public async ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef cloud = await PointCloudFromDepthOps.ExecuteAsync(
            arguments, frame, Name, DepthProjectionMode.Orthographic);
        if (cloud.IsNull) return ValueRef.Null(DataKind.Mesh);
        return MeshConstructionOps.PromoteOrganizedCloud(cloud, Name);
    }

    internal static IReadOnlyList<FunctionSignatureVariant> BuildDepthMeshSignatures() =>
    [
        // Image-based depth: the standard MiDaS / DPT / Depth-Anything
        // visualization path. Same shape as point_cloud_from_depth_*'s
        // first variant.
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("color",   DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("depth",   DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("fov_deg", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Mesh)),
        // Array-based depth: raw Float32 metric depth (meters), shape-aware.
        // Pair with the metric model variants (ZoeDepth_meters / GLPN-NYU_meters)
        // to preserve real-world distances through the unprojection. Depth
        // array's (h, w) must match the color image dims — call array_resize_2d
        // in the body if they differ.
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("color",   DataKindMatcher.Exact(DataKind.Image)),
                new ParameterSpec("depth",   DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("fov_deg", DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Mesh)),
    ];
}

/// <summary>
/// <c>mesh_from_depth_pinhole(color Image, depth Image, fov_deg Float32) → Mesh</c>.
/// Composes <c>point_cloud_from_depth_pinhole</c> with <c>mesh_from_organized</c> —
/// unprojects a depth-map Image into an organized cloud using pinhole-camera
/// projection (angular position scales with depth), then triangulates.
/// Physically correct when depth values are real-world distances.
/// </summary>
public sealed class MeshFromDepthPinholeFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "mesh_from_depth_pinhole";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Unprojects a depth Image into a 3D Mesh using pinhole camera projection: "
        + "angular position scales with depth. Triangulates the resulting organized "
        + "cloud with depth-discontinuity edge handling. Use for real-world-distance "
        + "depth (metric depth, RGB-D sensors). For normalized inverse depth, use "
        + "mesh_from_depth_orthographic.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
        MeshFromDepthOrthographicFunction.BuildDepthMeshSignatures();

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<MeshFromDepthPinholeFunction>(argumentKinds);

    /// <inheritdoc />
    public async ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef cloud = await PointCloudFromDepthOps.ExecuteAsync(
            arguments, frame, Name, DepthProjectionMode.Pinhole);
        if (cloud.IsNull) return ValueRef.Null(DataKind.Mesh);
        return MeshConstructionOps.PromoteOrganizedCloud(cloud, Name);
    }
}

/// <summary>
/// <c>mesh_compute_normals(mesh Mesh) → Mesh</c>. Recomputes per-vertex
/// normals from triangle topology — normalized sum of adjacent face normals.
/// Useful for meshes ingested without normals (e.g. ONNX models that emit
/// vertices + indices but no normal data), or to refresh normals after a
/// topology-changing operation.
/// </summary>
public sealed class MeshComputeNormalsFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "mesh_compute_normals";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Recomputes per-vertex normals on a Mesh from its triangle topology "
        + "(normalized sum of adjacent face normals). Returns a new Mesh with "
        + "HasNormals set; preserves position, color, coordinate frame, and topology.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("mesh", DataKindMatcher.Exact(DataKind.Mesh))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Mesh)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<MeshComputeNormalsFunction>(argumentKinds);

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
        ValueRef result = MeshConstructionOps.RecomputeNormals(arg, Name);
        return new ValueTask<ValueRef>(result);
    }
}

/// <summary>
/// Shared implementation for the mesh-producing scalars. Centralizes the
/// triangulation, normal computation, and per-vertex / per-triangle blob
/// layout so the four producer functions share a single source of truth.
/// </summary>
internal static class MeshConstructionOps
{
    /// <summary>
    /// Fraction of bbox Z range above which a grid cell's corner-depth span
    /// counts as a discontinuity and the cell's triangles are skipped.
    /// Matches the front-end MeshRenderer's threshold so engine-emitted
    /// meshes look identical to the viewer's implicit-mesh-from-cloud mode.
    /// </summary>
    private const float DiscontinuityThresholdFraction = 0.05f;

    /// <summary>
    /// Promotes an organized PointCloud value to an explicit triangle Mesh.
    /// Per-vertex pos/color is inherited from the cloud; per-vertex normals
    /// are computed from the resulting triangle topology.
    /// </summary>
    public static ValueRef PromoteOrganizedCloud(ValueRef cloudArg, string functionName)
    {
        byte[] cloudBlob = cloudArg.AsPointCloud();
        PointCloudHeader cloudHeader = PointCloudHeader.Read(cloudBlob);

        if (!cloudHeader.IsOrganized)
        {
            throw new FunctionArgumentException(
                functionName,
                "requires an organized PointCloud (width × height = point count); "
                + $"got width={cloudHeader.Width}, height={cloudHeader.Height}, points={cloudHeader.PointCount}. "
                + "Unorganized clouds (LiDAR scans, decimated, photogrammetry) have no grid "
                + "topology to triangulate from.");
        }

        int width = checked((int)cloudHeader.Width);
        int height = checked((int)cloudHeader.Height);
        int pointCount = checked((int)cloudHeader.PointCount);
        int cloudStride = cloudHeader.PointStrideBytes;
        bool sourceHasColor = cloudHeader.HasColor;

        MeshFlags meshFlags = MeshFlags.HasNormals | (sourceHasColor ? MeshFlags.HasColor : MeshFlags.None);
        int vertexStride = MeshHeader.PositionStrideBytes
            + (sourceHasColor ? MeshHeader.ColorStrideBytes : 0)
            + MeshHeader.NormalStrideBytes;

        // Triangle emission: walk grid cells, skip those whose corner Z values
        // span more than the discontinuity threshold. Build the index list as
        // we go; the orphan-vertex policy means we don't have to compact
        // vertices, so the vertex count equals pointCount unconditionally.
        float zRange = MathF.Max(cloudHeader.BboxMax.Z - cloudHeader.BboxMin.Z, 1e-6f);
        float threshold = zRange * DiscontinuityThresholdFraction;

        ReadOnlySpan<byte> cloudSpan = cloudBlob;
        int cloudPointsStart = PointCloudHeader.SizeBytes;

        // First pass: collect triangle indices.
        List<uint> indices = new(capacity: (width - 1) * (height - 1) * 6);
        for (int v = 0; v < height - 1; v++)
        {
            for (int u = 0; u < width - 1; u++)
            {
                int a = v * width + u;
                int b = v * width + u + 1;
                int c = (v + 1) * width + u;
                int d = (v + 1) * width + u + 1;

                float za = ReadPointZ(cloudSpan, cloudPointsStart, a, cloudStride);
                float zb = ReadPointZ(cloudSpan, cloudPointsStart, b, cloudStride);
                float zc = ReadPointZ(cloudSpan, cloudPointsStart, c, cloudStride);
                float zd = ReadPointZ(cloudSpan, cloudPointsStart, d, cloudStride);

                float zMin = MathF.Min(MathF.Min(za, zb), MathF.Min(zc, zd));
                float zMax = MathF.Max(MathF.Max(za, zb), MathF.Max(zc, zd));
                if (zMax - zMin > threshold) continue;

                // CCW winding in OpenGL frame: (a, c, b) and (b, c, d).
                indices.Add((uint)a);
                indices.Add((uint)c);
                indices.Add((uint)b);
                indices.Add((uint)b);
                indices.Add((uint)c);
                indices.Add((uint)d);
            }
        }
        int triangleCount = indices.Count / 3;

        // Allocate the mesh blob and start writing.
        long totalSize = (long)MeshHeader.SizeBytes
            + (long)pointCount * vertexStride
            + (long)triangleCount * MeshHeader.IndexStrideBytes;
        byte[] meshBlob = new byte[totalSize];
        Span<byte> meshSpan = meshBlob;

        // Second pass: copy positions + colors, allocate zero-initialized
        // normal slots (filled in a third pass).
        int vertexBase = MeshHeader.SizeBytes;
        for (int i = 0; i < pointCount; i++)
        {
            int srcOffset = cloudPointsStart + i * cloudStride;
            int dstOffset = vertexBase + i * vertexStride;
            // Position (always)
            cloudSpan.Slice(srcOffset, MeshHeader.PositionStrideBytes)
                .CopyTo(meshSpan.Slice(dstOffset, MeshHeader.PositionStrideBytes));
            if (sourceHasColor)
            {
                // Color follows position.
                cloudSpan.Slice(srcOffset + MeshHeader.PositionStrideBytes, MeshHeader.ColorStrideBytes)
                    .CopyTo(meshSpan.Slice(dstOffset + MeshHeader.PositionStrideBytes, MeshHeader.ColorStrideBytes));
            }
            // Normal slot (after pos + optional color) is left zero; the
            // accumulation pass below fills it.
        }

        // Write triangle indices after the vertex payload.
        int trianglesBase = vertexBase + pointCount * vertexStride;
        Span<byte> indicesSpan = meshSpan.Slice(trianglesBase, triangleCount * MeshHeader.IndexStrideBytes);
        for (int i = 0; i < indices.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(indicesSpan.Slice(i * 4, 4), indices[i]);
        }

        // Third pass: accumulate face normals into per-vertex normal slots,
        // then normalize. Writes are in the same vertex slot the position
        // pass wrote to, just at an offset further into the slot.
        int normalSlotOffset = MeshHeader.PositionStrideBytes
            + (sourceHasColor ? MeshHeader.ColorStrideBytes : 0);
        AccumulateAndNormalizeNormals(meshSpan, vertexBase, vertexStride, pointCount,
            normalSlotOffset, indices);

        // Compute the mesh bbox from the same per-vertex positions we just
        // wrote. Differs from the cloud's bbox only when the cloud had
        // disconnected outlier points the discontinuity threshold dropped,
        // which doesn't change positions, just which triangles reference
        // them — so the bbox is identical in practice. Recompute anyway so
        // the mesh header is self-consistent rather than inherited.
        (Vector3 bboxMin, Vector3 bboxMax) = ComputeBbox(meshSpan, vertexBase, vertexStride, pointCount);

        MeshHeader header = new(
            Version: MeshHeader.CurrentVersion,
            Flags: meshFlags,
            CoordinateFrame: cloudHeader.CoordinateFrame,
            VertexCount: (uint)pointCount,
            TriangleCount: (uint)triangleCount,
            BboxMin: bboxMin,
            BboxMax: bboxMax,
            TextureOffset: 0,
            TextureLength: 0);
        header.Write(meshSpan[..MeshHeader.SizeBytes]);

        return ValueRef.FromMesh(meshBlob);
    }

    /// <summary>
    /// Recomputes per-vertex normals on an existing mesh from its triangle
    /// topology. Allocates a new blob (always has HasNormals set) and copies
    /// position + color over from the input.
    /// </summary>
    public static ValueRef RecomputeNormals(ValueRef meshArg, string functionName)
    {
        byte[] srcBlob = meshArg.AsMesh();
        MeshHeader srcHeader = MeshHeader.Read(srcBlob);

        int vertexCount = checked((int)srcHeader.VertexCount);
        int triangleCount = checked((int)srcHeader.TriangleCount);
        int srcVertexStride = srcHeader.VertexStrideBytes;
        bool hasColor = srcHeader.HasColor;

        MeshFlags dstFlags = MeshFlags.HasNormals | (hasColor ? MeshFlags.HasColor : MeshFlags.None);
        int dstVertexStride = MeshHeader.PositionStrideBytes
            + (hasColor ? MeshHeader.ColorStrideBytes : 0)
            + MeshHeader.NormalStrideBytes;

        long totalSize = (long)MeshHeader.SizeBytes
            + (long)vertexCount * dstVertexStride
            + (long)triangleCount * MeshHeader.IndexStrideBytes;
        byte[] dstBlob = new byte[totalSize];
        Span<byte> dstSpan = dstBlob;
        ReadOnlySpan<byte> srcSpan = srcBlob;

        // Copy position + color from source to destination.
        int srcVertexBase = MeshHeader.SizeBytes;
        int dstVertexBase = MeshHeader.SizeBytes;
        for (int i = 0; i < vertexCount; i++)
        {
            int srcOffset = srcVertexBase + i * srcVertexStride;
            int dstOffset = dstVertexBase + i * dstVertexStride;
            srcSpan.Slice(srcOffset, MeshHeader.PositionStrideBytes)
                .CopyTo(dstSpan.Slice(dstOffset, MeshHeader.PositionStrideBytes));
            if (hasColor)
            {
                srcSpan.Slice(srcOffset + MeshHeader.PositionStrideBytes, MeshHeader.ColorStrideBytes)
                    .CopyTo(dstSpan.Slice(dstOffset + MeshHeader.PositionStrideBytes, MeshHeader.ColorStrideBytes));
            }
        }

        // Copy triangle indices verbatim from source.
        int srcTrianglesBase = srcVertexBase + vertexCount * srcVertexStride;
        int dstTrianglesBase = dstVertexBase + vertexCount * dstVertexStride;
        srcSpan.Slice(srcTrianglesBase, triangleCount * MeshHeader.IndexStrideBytes)
            .CopyTo(dstSpan.Slice(dstTrianglesBase, triangleCount * MeshHeader.IndexStrideBytes));

        // Build an in-memory index list for the normal-accumulation helper.
        List<uint> indices = new(capacity: triangleCount * 3);
        for (int i = 0; i < triangleCount * 3; i++)
        {
            indices.Add(BinaryPrimitives.ReadUInt32LittleEndian(
                srcSpan.Slice(srcTrianglesBase + i * 4, 4)));
        }

        int normalSlotOffset = MeshHeader.PositionStrideBytes + (hasColor ? MeshHeader.ColorStrideBytes : 0);
        AccumulateAndNormalizeNormals(dstSpan, dstVertexBase, dstVertexStride, vertexCount,
            normalSlotOffset, indices);

        MeshHeader dstHeader = srcHeader with
        {
            Flags = dstFlags,
            TextureOffset = 0,
            TextureLength = 0,
        };
        dstHeader.Write(dstSpan[..MeshHeader.SizeBytes]);

        return ValueRef.FromMesh(dstBlob);
    }

    /// <summary>
    /// Walks the triangle list, accumulates each face's normal (cross product
    /// of two edges) into each of its three vertices' normal slots, then
    /// normalizes each per-vertex accumulated vector. The result is the
    /// "smooth shading" normal for each vertex — the right-hand average of
    /// the normals of all triangles meeting at it.
    /// </summary>
    private static void AccumulateAndNormalizeNormals(
        Span<byte> meshSpan, int vertexBase, int vertexStride, int vertexCount,
        int normalSlotOffset, List<uint> indices)
    {
        // Per-vertex accumulator. Lives in a managed array so we can do
        // structured math; copied into the blob at the end. Avoids the
        // bytes-then-floats-then-bytes ping-pong inside the inner loop.
        Vector3[] accum = new Vector3[vertexCount];

        for (int i = 0; i < indices.Count; i += 3)
        {
            int ia = (int)indices[i];
            int ib = (int)indices[i + 1];
            int ic = (int)indices[i + 2];

            Vector3 pa = ReadVertexPosition(meshSpan, vertexBase, vertexStride, ia);
            Vector3 pb = ReadVertexPosition(meshSpan, vertexBase, vertexStride, ib);
            Vector3 pc = ReadVertexPosition(meshSpan, vertexBase, vertexStride, ic);

            Vector3 faceNormal = Vector3.Cross(pb - pa, pc - pa);
            // Unnormalized cross-product magnitude weights by triangle area —
            // larger triangles contribute more to the vertex normal. Standard
            // approach (vs. normalizing per-face first); produces smoother
            // results on irregular meshes.
            accum[ia] += faceNormal;
            accum[ib] += faceNormal;
            accum[ic] += faceNormal;
        }

        for (int i = 0; i < vertexCount; i++)
        {
            Vector3 n = accum[i];
            float lenSq = n.LengthSquared();
            // Orphan vertices (referenced by no triangle) end up with a zero
            // accumulator. Pick a defensible default (+Y) rather than NaN.
            Vector3 normalized = lenSq > 1e-12f
                ? n / MathF.Sqrt(lenSq)
                : new Vector3(0, 1, 0);

            int dstOffset = vertexBase + i * vertexStride + normalSlotOffset;
            BinaryPrimitives.WriteSingleLittleEndian(meshSpan.Slice(dstOffset + 0, 4), normalized.X);
            BinaryPrimitives.WriteSingleLittleEndian(meshSpan.Slice(dstOffset + 4, 4), normalized.Y);
            BinaryPrimitives.WriteSingleLittleEndian(meshSpan.Slice(dstOffset + 8, 4), normalized.Z);
        }
    }

    private static (Vector3 min, Vector3 max) ComputeBbox(
        Span<byte> meshSpan, int vertexBase, int vertexStride, int vertexCount)
    {
        if (vertexCount == 0)
        {
            return (Vector3.Zero, Vector3.Zero);
        }
        Vector3 min = new(float.PositiveInfinity);
        Vector3 max = new(float.NegativeInfinity);
        for (int i = 0; i < vertexCount; i++)
        {
            Vector3 p = ReadVertexPosition(meshSpan, vertexBase, vertexStride, i);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return (min, max);
    }

    private static Vector3 ReadVertexPosition(
        ReadOnlySpan<byte> meshSpan, int vertexBase, int vertexStride, int vertexIndex)
    {
        int offset = vertexBase + vertexIndex * vertexStride;
        return new Vector3(
            BinaryPrimitives.ReadSingleLittleEndian(meshSpan.Slice(offset + 0, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(meshSpan.Slice(offset + 4, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(meshSpan.Slice(offset + 8, 4)));
    }

    private static float ReadPointZ(
        ReadOnlySpan<byte> cloudSpan, int pointsStart, int pointIndex, int stride)
    {
        int offset = pointsStart + pointIndex * stride + 8; // Z is at offset 8 in each point slot
        return BinaryPrimitives.ReadSingleLittleEndian(cloudSpan.Slice(offset, 4));
    }
}

