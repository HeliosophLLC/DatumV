using System.Buffers.Binary;
using System.Numerics;

using DatumIngest.Execution;
using DatumIngest.Functions.Scalar.Activation;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Model.Spatial;

namespace DatumIngest.Functions.Scalar.Spatial;

/// <summary>
/// <c>mesh_from_density_grid(density Float32[], resolution Int32, isolevel Float32, radius Float32) → Mesh</c>.
/// Extracts an iso-surface triangle Mesh from a volumetric scalar field via
/// the Marching Cubes algorithm.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Density layout</strong> — flat <c>Float32[resolution³]</c>, indexed
/// as <c>density[x + y * resolution + z * resolution²]</c>. That is, X varies
/// fastest, then Y, then Z. This matches the convention TripoSR-style
/// triplane-query producers use when they generate a grid of (x, y, z)
/// samples in row-major XYZ order and feed the densities back as a flat
/// array.
/// </para>
/// <para>
/// <strong>Spatial mapping</strong> — grid corner <c>(i, j, k)</c> maps to
/// world position <c>(-radius + 2·radius·i/(resolution-1), …)</c> on each
/// axis, so the grid spans the cube <c>[-radius, +radius]³</c>. Mesh
/// vertices land inside that cube via linear interpolation along grid edges
/// crossing the iso-surface.
/// </para>
/// <para>
/// <strong>Iso-surface convention</strong> — <c>density &gt; isolevel</c> is
/// "inside" the surface; <c>density ≤ isolevel</c> is "outside". For
/// activated density fields (TripoSR's <c>density_act</c>, signed-distance
/// fields, occupancy probabilities) higher = more "stuff there", so this
/// matches the model's natural sign. Pick <c>isolevel</c> based on the
/// field's calibration; TripoSR's typical default is ~25.0 for
/// <c>density_act</c>.
/// </para>
/// <para>
/// <strong>Output mesh</strong> — position-only triangles in OpenGL/Three.js
/// camera-space convention (right-handed, +y up). No per-vertex normals or
/// color; compose with <c>mesh_compute_normals</c> for smooth shading.
/// Vertices are deduplicated across cube edges so shared edges produce one
/// vertex referenced by multiple triangles (correct topology for normal
/// averaging and mesh editing).
/// </para>
/// </remarks>
public sealed class MeshFromDensityGridFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "mesh_from_density_grid";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Extracts an iso-surface triangle Mesh from a volumetric density field via "
        + "Marching Cubes. density is a flat Float32 array of length resolution³ "
        + "(X varies fastest, then Y, then Z); the grid spans [-radius, +radius]³ "
        + "in world space; the iso-surface is density > isolevel. Returns a "
        + "position-only mesh in OpenGL convention; compose with mesh_compute_normals "
        + "for smooth shading. Designed for triplane / NeRF density fields (TripoSR, "
        + "SF3D, InstantMesh).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("density",    DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("resolution", DataKindMatcher.Exact(DataKind.Int32)),
                new ParameterSpec("isolevel",   DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("radius",     DataKindMatcher.Family(DataKindFamily.NumericScalar)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Mesh)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<MeshFromDensityGridFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Mesh));
        }

        float[] density = ActivationOps.ReadFloat32Array(args[0]);
        int resolution  = args[1].AsInt32();
        float isolevel  = args[2].AsFloat32();
        float radius    = args[3].AsFloat32();

        if (resolution < 2)
        {
            throw new FunctionArgumentException(Name,
                "resolution must be >= 2 (need at least a 2×2×2 grid to form one Marching Cubes cube); "
                + $"got {resolution}.");
        }
        long expected = (long)resolution * resolution * resolution;
        if (density.Length != expected)
        {
            throw new FunctionArgumentException(Name,
                $"density array length must equal resolution³ ({expected} for resolution={resolution}); "
                + $"got {density.Length}. Layout is density[x + y*resolution + z*resolution²].");
        }
        if (!float.IsFinite(isolevel))
        {
            throw new FunctionArgumentException(Name,
                $"isolevel must be finite; got {isolevel}.");
        }
        if (!float.IsFinite(radius) || radius <= 0f)
        {
            throw new FunctionArgumentException(Name,
                $"radius must be a positive finite value; got {radius}.");
        }

        MarchingCubesResult mc = MarchingCubesExtractor.Extract(density, resolution, isolevel, radius);
        byte[] blob = MeshBlobBuilder.PositionOnly(mc, PointCloudCoordinateFrame.CameraOpenGl);
        return new ValueTask<ValueRef>(ValueRef.FromMesh(blob));
    }
}

/// <summary>
/// Output of <see cref="MarchingCubesExtractor.Extract"/>: deduplicated
/// vertex positions, triangle index list, and the bounding box of the
/// emitted geometry. Kept as raw managed arrays so downstream code can
/// attach per-vertex attributes (color sampled at vertex positions, etc.)
/// before assembling the final Mesh blob.
/// </summary>
internal readonly record struct MarchingCubesResult(
    float[] Positions,
    uint[] Indices,
    Vector3 BboxMin,
    Vector3 BboxMax)
{
    /// <summary>Number of unique vertices in <see cref="Positions"/>.</summary>
    public int VertexCount => Positions.Length / 3;

    /// <summary>Number of triangles in <see cref="Indices"/>.</summary>
    public int TriangleCount => Indices.Length / 3;
}

/// <summary>
/// Marching Cubes implementation that walks a resolution³ grid of scalar
/// density samples and emits a deduplicated triangle list whose vertices lie
/// on the <c>density == isolevel</c> iso-surface. Single static entry point
/// so the surface is callable from <see cref="MeshFromDensityGridFunction"/>
/// and from any future orchestrator (e.g. <c>mesh_from_triplane</c>) without
/// going through the scalar-function boundary.
/// </summary>
internal static class MarchingCubesExtractor
{
    /// <summary>Per-cube corner offsets in (Δx, Δy, Δz) for corners 0..7.</summary>
    private static readonly (int dx, int dy, int dz)[] CornerOffsets =
    [
        (0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0),   // bottom face
        (0, 0, 1), (1, 0, 1), (1, 1, 1), (0, 1, 1),   // top face
    ];

    /// <summary>For each of the 12 edges, the two corners it connects.</summary>
    private static readonly (int a, int b)[] EdgeCorners =
    [
        (0, 1), (1, 2), (2, 3), (3, 0),               // bottom face
        (4, 5), (5, 6), (6, 7), (7, 4),               // top face
        (0, 4), (1, 5), (2, 6), (3, 7),               // vertical edges
    ];

    /// <summary>
    /// For each edge, the canonical "owning" grid position + axis used for
    /// deduplication. An edge is owned by its lower-coordinate endpoint cube,
    /// so two cubes sharing an edge produce the same dedup key. Axis: 0=x, 1=y, 2=z.
    /// </summary>
    private static readonly (int dx, int dy, int dz, int axis)[] EdgeOwners =
    [
        (0, 0, 0, 0), (1, 0, 0, 1), (0, 1, 0, 0), (0, 0, 0, 1),
        (0, 0, 1, 0), (1, 0, 1, 1), (0, 1, 1, 0), (0, 0, 1, 1),
        (0, 0, 0, 2), (1, 0, 0, 2), (1, 1, 0, 2), (0, 1, 0, 2),
    ];

    public static MarchingCubesResult Extract(float[] density, int resolution, float isolevel, float radius)
    {
        int res = resolution;
        int cubes = res - 1;
        float step = (2f * radius) / (res - 1);

        // Vertex / index accumulators. List<float> for vertices stores XYZ
        // triplets flat; a real workload may want a pooled buffer, but the
        // List grow-doubling cost is small relative to MC table lookups +
        // dictionary probes.
        List<float> vertices = [];
        List<uint> indices = [];

        // Edge → vertex-index cache. Key combines the owning grid position
        // and the edge axis. (res³ × 3) potential keys total; populated
        // sparsely for only the edges the iso-surface actually crosses,
        // which is typically a few percent of the total. Dictionary scales
        // well at this density.
        Dictionary<long, uint> edgeCache = new(capacity: 1024);

        // Per-cube scratch carrying interpolated vertex indices for each of
        // the 12 edges, indexed by edge number 0..11. Reused across cubes.
        Span<uint> edgeVerts = stackalloc uint[12];
        Span<float> cornerD = stackalloc float[8];

        for (int k = 0; k < cubes; k++)
        {
            int kStride = k * res * res;
            int kStrideNext = (k + 1) * res * res;
            for (int j = 0; j < cubes; j++)
            {
                int jStride = j * res;
                int jStrideNext = (j + 1) * res;
                for (int i = 0; i < cubes; i++)
                {
                    // Read the 8 corner densities. Indexing matches the
                    // CornerOffsets layout: bottom face z=k, top face z=k+1;
                    // bottom face order around z=k is (0,0)→(1,0)→(1,1)→(0,1).
                    cornerD[0] = density[i     + jStride     + kStride];
                    cornerD[1] = density[i + 1 + jStride     + kStride];
                    cornerD[2] = density[i + 1 + jStrideNext + kStride];
                    cornerD[3] = density[i     + jStrideNext + kStride];
                    cornerD[4] = density[i     + jStride     + kStrideNext];
                    cornerD[5] = density[i + 1 + jStride     + kStrideNext];
                    cornerD[6] = density[i + 1 + jStrideNext + kStrideNext];
                    cornerD[7] = density[i     + jStrideNext + kStrideNext];

                    // Build the 8-bit case index from the corner sign pattern.
                    int caseIdx = 0;
                    for (int c = 0; c < 8; c++)
                    {
                        if (cornerD[c] > isolevel) caseIdx |= (1 << c);
                    }

                    int edgeMask = MarchingCubesTables.EdgeTable[caseIdx];
                    if (edgeMask == 0)
                    {
                        // Entirely inside or entirely outside -- nothing to emit.
                        continue;
                    }

                    // Place / reuse a vertex on each crossed edge.
                    for (int e = 0; e < 12; e++)
                    {
                        if ((edgeMask & (1 << e)) == 0) continue;

                        var owner = EdgeOwners[e];
                        int oi = i + owner.dx;
                        int oj = j + owner.dy;
                        int ok = k + owner.dz;
                        long key = (((long)ok * res + oj) * res + oi) * 3 + owner.axis;

                        if (edgeCache.TryGetValue(key, out uint cachedIdx))
                        {
                            edgeVerts[e] = cachedIdx;
                        }
                        else
                        {
                            var (ca, cb) = EdgeCorners[e];
                            float da = cornerD[ca];
                            float db = cornerD[cb];
                            // Linear interpolation along the edge to the
                            // iso-surface crossing. Clamp t into [0,1] for
                            // robustness against numerical edge cases when
                            // |db - da| approaches zero (corners straddle
                            // the iso almost exactly).
                            float denom = db - da;
                            float t = MathF.Abs(denom) > 1e-12f
                                ? (isolevel - da) / denom
                                : 0.5f;
                            if (t < 0f) t = 0f;
                            else if (t > 1f) t = 1f;

                            var (aOff, bOff) = (CornerOffsets[ca], CornerOffsets[cb]);
                            int ax = i + aOff.dx, ay = j + aOff.dy, az = k + aOff.dz;
                            int bx = i + bOff.dx, by = j + bOff.dy, bz = k + bOff.dz;

                            float vx = (-radius + step * ax) + t * (step * (bx - ax));
                            float vy = (-radius + step * ay) + t * (step * (by - ay));
                            float vz = (-radius + step * az) + t * (step * (bz - az));

                            uint newIdx = (uint)(vertices.Count / 3);
                            vertices.Add(vx);
                            vertices.Add(vy);
                            vertices.Add(vz);
                            edgeCache[key] = newIdx;
                            edgeVerts[e] = newIdx;
                        }
                    }

                    // Emit triangles from the triangle table. Each row
                    // contains up to 5 triangles encoded as triplets of edge
                    // indices, terminated by -1.
                    for (int t = 0; t < 16; t += 3)
                    {
                        int e0 = MarchingCubesTables.TriTable[caseIdx, t];
                        if (e0 == -1) break;
                        int e1 = MarchingCubesTables.TriTable[caseIdx, t + 1];
                        int e2 = MarchingCubesTables.TriTable[caseIdx, t + 2];
                        indices.Add(edgeVerts[e0]);
                        indices.Add(edgeVerts[e1]);
                        indices.Add(edgeVerts[e2]);
                    }
                }
            }
        }

        int vertexCount = vertices.Count / 3;

        // Materialize positions + indices into flat arrays; compute the bbox
        // from the same data so the result is self-consistent regardless of
        // how a downstream caller assembles the final blob.
        float[] positions = new float[vertices.Count];
        for (int v = 0; v < vertices.Count; v++) positions[v] = vertices[v];

        uint[] indexArray = new uint[indices.Count];
        for (int i = 0; i < indices.Count; i++) indexArray[i] = indices[i];

        Vector3 bboxMin, bboxMax;
        if (vertexCount == 0)
        {
            bboxMin = bboxMax = Vector3.Zero;
        }
        else
        {
            bboxMin = new Vector3(float.PositiveInfinity);
            bboxMax = new Vector3(float.NegativeInfinity);
            for (int v = 0; v < vertexCount; v++)
            {
                Vector3 p = new(positions[v * 3], positions[v * 3 + 1], positions[v * 3 + 2]);
                bboxMin = Vector3.Min(bboxMin, p);
                bboxMax = Vector3.Max(bboxMax, p);
            }
        }

        return new MarchingCubesResult(positions, indexArray, bboxMin, bboxMax);
    }
}

/// <summary>
/// Helpers that pack raw vertex / index arrays into the on-wire Mesh blob
/// format. Used by mesh-producing scalar functions that compute geometry
/// independently of the blob layout (Marching Cubes, future ONNX-mesh
/// constructors). Kept distinct from <see cref="MeshConstructionOps"/>,
/// which builds meshes from PointCloud sources with a different attribute
/// flow (positions + colors come from the source cloud, normals from
/// topology) — sharing a helper there would tangle two different control
/// flows.
/// </summary>
internal static class MeshBlobBuilder
{
    /// <summary>
    /// Packs a position-only mesh (no color, no normals) from the
    /// <paramref name="mc"/> arrays. Caller composes with
    /// <c>mesh_compute_normals</c> for smooth shading if needed.
    /// </summary>
    public static byte[] PositionOnly(MarchingCubesResult mc, PointCloudCoordinateFrame frame)
    {
        int vertexCount = mc.VertexCount;
        int triangleCount = mc.TriangleCount;
        int vertexStride = MeshHeader.PositionStrideBytes;

        long totalSize = (long)MeshHeader.SizeBytes
            + (long)vertexCount * vertexStride
            + (long)triangleCount * MeshHeader.IndexStrideBytes;
        byte[] blob = new byte[totalSize];
        Span<byte> span = blob;

        MeshHeader header = new(
            Version: MeshHeader.CurrentVersion,
            Flags: MeshFlags.None,
            CoordinateFrame: frame,
            VertexCount: (uint)vertexCount,
            TriangleCount: (uint)triangleCount,
            BboxMin: mc.BboxMin,
            BboxMax: mc.BboxMax,
            TextureOffset: 0,
            TextureLength: 0);
        header.Write(span[..MeshHeader.SizeBytes]);

        int vertexBase = MeshHeader.SizeBytes;
        for (int v = 0; v < vertexCount; v++)
        {
            int off = vertexBase + v * vertexStride;
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(off + 0, 4), mc.Positions[v * 3 + 0]);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(off + 4, 4), mc.Positions[v * 3 + 1]);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(off + 8, 4), mc.Positions[v * 3 + 2]);
        }

        WriteIndices(span, vertexBase + vertexCount * vertexStride, mc.Indices);
        return blob;
    }

    /// <summary>
    /// Packs a position + per-vertex RGBA color mesh (no normals). Color
    /// channels arrive as Float32 in <c>[0, 1]</c> (the typical NeRF /
    /// implicit-field output range) and are clamped and quantized to
    /// uint8 here; alpha is set to 255 (opaque) since per-vertex implicit
    /// fields rarely model transparency. <paramref name="colorsRgb"/> must
    /// be <c>positions.Length</c> (i.e. 3 channels per vertex).
    /// </summary>
    public static byte[] PositionPlusColor(
        float[] positions,
        float[] colorsRgb,
        uint[] indices,
        Vector3 bboxMin,
        Vector3 bboxMax,
        PointCloudCoordinateFrame frame)
    {
        if (positions.Length % 3 != 0)
        {
            throw new ArgumentException(
                $"positions length must be a multiple of 3 (XYZ per vertex); got {positions.Length}.",
                nameof(positions));
        }
        if (colorsRgb.Length != positions.Length)
        {
            throw new ArgumentException(
                $"colorsRgb length {colorsRgb.Length} must equal positions length "
                + $"{positions.Length} (3 RGB channels per vertex).",
                nameof(colorsRgb));
        }
        if (indices.Length % 3 != 0)
        {
            throw new ArgumentException(
                $"indices length must be a multiple of 3 (3 indices per triangle); got {indices.Length}.",
                nameof(indices));
        }

        int vertexCount = positions.Length / 3;
        int triangleCount = indices.Length / 3;
        int vertexStride = MeshHeader.PositionStrideBytes + MeshHeader.ColorStrideBytes;

        long totalSize = (long)MeshHeader.SizeBytes
            + (long)vertexCount * vertexStride
            + (long)triangleCount * MeshHeader.IndexStrideBytes;
        byte[] blob = new byte[totalSize];
        Span<byte> span = blob;

        MeshHeader header = new(
            Version: MeshHeader.CurrentVersion,
            Flags: MeshFlags.HasColor,
            CoordinateFrame: frame,
            VertexCount: (uint)vertexCount,
            TriangleCount: (uint)triangleCount,
            BboxMin: bboxMin,
            BboxMax: bboxMax,
            TextureOffset: 0,
            TextureLength: 0);
        header.Write(span[..MeshHeader.SizeBytes]);

        int vertexBase = MeshHeader.SizeBytes;
        for (int v = 0; v < vertexCount; v++)
        {
            int off = vertexBase + v * vertexStride;
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(off + 0, 4), positions[v * 3 + 0]);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(off + 4, 4), positions[v * 3 + 1]);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(off + 8, 4), positions[v * 3 + 2]);
            span[off + 12] = QuantizeChannel(colorsRgb[v * 3 + 0]);
            span[off + 13] = QuantizeChannel(colorsRgb[v * 3 + 1]);
            span[off + 14] = QuantizeChannel(colorsRgb[v * 3 + 2]);
            span[off + 15] = 255;
        }

        WriteIndices(span, vertexBase + vertexCount * vertexStride, indices);
        return blob;
    }

    private static void WriteIndices(Span<byte> span, int trianglesBase, uint[] indices)
    {
        for (int i = 0; i < indices.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(trianglesBase + i * 4, 4), indices[i]);
        }
    }

    private static byte QuantizeChannel(float c)
    {
        // Clamp + 8-bit quantize. NaN passes through the < comparisons as
        // false, so we explicitly normalize it to 0 — the typical NeRF
        // output never produces NaN, but the input contract here is "any
        // float", so be defensive.
        if (float.IsNaN(c)) return 0;
        if (c <= 0f) return 0;
        if (c >= 1f) return 255;
        return (byte)MathF.Round(c * 255f);
    }
}
