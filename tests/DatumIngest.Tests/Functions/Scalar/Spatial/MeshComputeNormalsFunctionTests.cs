using System.Buffers.Binary;
using System.Numerics;

using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Spatial;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Model.Spatial;

namespace DatumIngest.Tests.Functions.Scalar.Spatial;

/// <summary>
/// Tests for <see cref="MeshComputeNormalsFunction"/>. Builds a mesh
/// without normals (or with stale normals), runs the function, verifies
/// the output mesh has HasNormals set with correct per-vertex unit normals
/// for a known geometry.
/// </summary>
public sealed class MeshComputeNormalsFunctionTests : ServiceTestBase
{
    [Fact]
    public void Metadata_ExposesNameCategoryAndDescription()
    {
        Assert.Equal("mesh_compute_normals", MeshComputeNormalsFunction.Name);
        Assert.Equal(FunctionCategory.Image, MeshComputeNormalsFunction.Category);
    }

    [Fact]
    public async Task Execute_NullInput_ReturnsNullMesh()
    {
        ValueRef result = await new MeshComputeNormalsFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.Mesh) }, CreateEvaluationFrame(), default);

        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Mesh, result.Kind);
    }

    [Fact]
    public async Task Execute_MeshWithoutNormals_ProducesMeshWithNormals()
    {
        ValueRef input = BuildFlatQuadMesh(includeNormals: false);
        MeshHeader inputHeader = MeshHeader.Read(input.AsMesh());
        Assert.False(inputHeader.HasNormals);

        ValueRef result = await new MeshComputeNormalsFunction().ExecuteAsync(
            new[] { input }, CreateEvaluationFrame(), default);

        byte[] outBlob = result.AsMesh();
        MeshHeader outHeader = MeshHeader.Read(outBlob);
        Assert.True(outHeader.HasNormals);
        Assert.Equal(inputHeader.HasColor, outHeader.HasColor);
        Assert.Equal(inputHeader.VertexCount, outHeader.VertexCount);
        Assert.Equal(inputHeader.TriangleCount, outHeader.TriangleCount);
    }

    [Fact]
    public async Task Execute_FlatQuad_ProducesUnitYNormals()
    {
        // A flat quad in the XZ plane (all Y = 0). Each triangle's face
        // normal is +Y (cross product of edges along +X and +Z gives +Y in
        // CCW winding). Every vertex normal should be (0, 1, 0).
        ValueRef input = BuildFlatQuadMesh(includeNormals: false);

        ValueRef result = await new MeshComputeNormalsFunction().ExecuteAsync(
            new[] { input }, CreateEvaluationFrame(), default);

        byte[] outBlob = result.AsMesh();
        MeshHeader outHeader = MeshHeader.Read(outBlob);

        for (int i = 0; i < outHeader.VertexCount; i++)
        {
            Vector3 n = ReadVertexNormal(outBlob, outHeader, vertexIndex: i);
            Assert.True(MathF.Abs(n.LengthSquared() - 1f) < 1e-3f,
                $"vertex {i}: expected unit-length normal, got length²={n.LengthSquared()}");
            Assert.True(MathF.Abs(n.Y - 1f) < 1e-3f && MathF.Abs(n.X) < 1e-3f && MathF.Abs(n.Z) < 1e-3f,
                $"vertex {i}: expected (0,1,0), got {n}");
        }
    }

    // ─────────────────────── Helpers ───────────────────────

    /// <summary>
    /// Hand-build a 4-vertex / 2-triangle quad in the XZ plane (Y=0).
    /// When <paramref name="includeNormals"/> is false, the blob omits the
    /// normal group entirely (Flags = HasColor; stride 16 per vertex).
    /// </summary>
    private static ValueRef BuildFlatQuadMesh(bool includeNormals)
    {
        MeshFlags flags = MeshFlags.HasColor | (includeNormals ? MeshFlags.HasNormals : MeshFlags.None);
        int vertexStride = MeshHeader.PositionStrideBytes
            + MeshHeader.ColorStrideBytes
            + (includeNormals ? MeshHeader.NormalStrideBytes : 0);

        MeshHeader header = new(
            Version: MeshHeader.CurrentVersion,
            Flags: flags,
            CoordinateFrame: PointCloudCoordinateFrame.CameraOpenGl,
            VertexCount: 4,
            TriangleCount: 2,
            BboxMin: new Vector3(0, 0, 0),
            BboxMax: new Vector3(1, 0, 1),
            TextureOffset: 0,
            TextureLength: 0);

        long totalSize = MeshHeader.SizeBytes
            + 4L * vertexStride
            + 2L * MeshHeader.IndexStrideBytes;
        byte[] blob = new byte[totalSize];
        Span<byte> span = blob;
        header.Write(span[..MeshHeader.SizeBytes]);

        Vector3[] positions =
        [
            new(0, 0, 0),
            new(1, 0, 0),
            new(0, 0, 1),
            new(1, 0, 1),
        ];

        int vertexBase = MeshHeader.SizeBytes;
        for (int i = 0; i < 4; i++)
        {
            int offset = vertexBase + i * vertexStride;
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 0, 4), positions[i].X);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 4, 4), positions[i].Y);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 8, 4), positions[i].Z);
            span[offset + 12] = 200;
            span[offset + 13] = 200;
            span[offset + 14] = 200;
            span[offset + 15] = 255;
            // No normal write when includeNormals=false; when true, leave zero
            // (the test that needs them computes via the function under test).
        }

        // CCW winding viewed from +Y: (0, 2, 1) and (1, 2, 3) — produces +Y
        // face normals via right-hand cross product.
        int indicesBase = vertexBase + 4 * vertexStride;
        uint[] indices = [0, 2, 1, 1, 2, 3];
        for (int i = 0; i < 6; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(indicesBase + i * 4, 4), indices[i]);
        }

        return ValueRef.FromMesh(blob);
    }

    private static Vector3 ReadVertexNormal(byte[] meshBlob, MeshHeader header, int vertexIndex)
    {
        ReadOnlySpan<byte> span = meshBlob;
        int offset = MeshHeader.SizeBytes
            + vertexIndex * header.VertexStrideBytes
            + MeshHeader.PositionStrideBytes
            + (header.HasColor ? MeshHeader.ColorStrideBytes : 0);
        return new Vector3(
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(offset + 0, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(offset + 4, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(offset + 8, 4)));
    }
}
