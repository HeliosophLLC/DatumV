using System.Buffers.Binary;
using System.Numerics;
using System.Text;

using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Spatial;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Model.Spatial;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Functions.Scalar.Spatial;

/// <summary>
/// Tests for <see cref="MeshToStlFunction"/>. Verifies metadata, null
/// propagation, the binary STL file layout (header + count + 50-byte
/// triangle records), and the face-normal computation from triangle
/// vertices.
/// </summary>
public sealed class MeshToStlFunctionTests : ServiceTestBase
{
    [Fact]
    public void Metadata_ExposesNameCategoryAndDescription()
    {
        Assert.Equal("mesh_to_stl", MeshToStlFunction.Name);
        Assert.Equal(FunctionCategory.Image, MeshToStlFunction.Category);
    }

    [Fact]
    public async Task Execute_NullInput_ReturnsNullArray()
    {
        ValueRef result = await new MeshToStlFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.Mesh) }, CreateEvaluationFrame(), default);
        Assert.True(result.IsNull);
    }

    [Fact]
    public async Task Execute_ProducesExpectedFileLayout()
    {
        ValueRef mesh = BuildSampleQuadMesh();

        ValueRef result = await new MeshToStlFunction().ExecuteAsync(
            new[] { mesh }, CreateEvaluationFrame(), default);

        byte[] stl = result.AsBytes();
        // Binary STL: 80 bytes header + 4 bytes count + N × 50 bytes.
        // Quad mesh has 2 triangles → 80 + 4 + 2 × 50 = 184 bytes.
        Assert.Equal(184, stl.Length);

        // Count at offset 80.
        uint triangleCount = BinaryPrimitives.ReadUInt32LittleEndian(stl.AsSpan(80, 4));
        Assert.Equal(2u, triangleCount);
    }

    [Fact]
    public async Task Execute_HeaderDoesNotStartWithSolidPrefix()
    {
        // Some slicers refuse to parse binary STL whose 80-byte header
        // starts with "solid" — that's the ASCII-STL magic. Our exporter
        // embeds a "DatumIngest STL ..." prefix to avoid the ambiguity.
        ValueRef mesh = BuildSampleQuadMesh();

        ValueRef result = await new MeshToStlFunction().ExecuteAsync(
            new[] { mesh }, CreateEvaluationFrame(), default);

        byte[] stl = result.AsBytes();
        string headerStart = Encoding.ASCII.GetString(stl, 0, 5);
        Assert.NotEqual("solid", headerStart);
    }

    [Fact]
    public async Task Execute_FaceNormals_ComputedFromTriangleVertices()
    {
        // Triangle in the XZ plane with CCW winding viewed from +Y gives a
        // face normal of (0, 1, 0). With three vertices a=(0,0,0),
        // b=(1,0,0), c=(0,0,1) and winding (a,c,b), cross product
        // (c-a) × (b-a) = (0,0,1) × (1,0,0) = (0, 1, 0).
        ValueRef mesh = BuildSampleQuadMesh();

        ValueRef result = await new MeshToStlFunction().ExecuteAsync(
            new[] { mesh }, CreateEvaluationFrame(), default);

        byte[] stl = result.AsBytes();
        // First triangle begins at offset 84. First 12 bytes are the face
        // normal (3 × float32). Should be (0, 1, 0).
        float nx = BinaryPrimitives.ReadSingleLittleEndian(stl.AsSpan(84 + 0, 4));
        float ny = BinaryPrimitives.ReadSingleLittleEndian(stl.AsSpan(84 + 4, 4));
        float nz = BinaryPrimitives.ReadSingleLittleEndian(stl.AsSpan(84 + 8, 4));

        Assert.True(MathF.Abs(nx) < 1e-4f, $"expected nx≈0, got {nx}");
        Assert.True(MathF.Abs(ny - 1f) < 1e-4f, $"expected ny≈1, got {ny}");
        Assert.True(MathF.Abs(nz) < 1e-4f, $"expected nz≈0, got {nz}");
    }

    [Fact]
    public async Task Execute_AttributeByteCountIsZeroPerTriangle()
    {
        // STL spec: last 2 bytes of each 50-byte triangle record are the
        // "attribute byte count" — always 0 in standard binary STL.
        ValueRef mesh = BuildSampleQuadMesh();

        ValueRef result = await new MeshToStlFunction().ExecuteAsync(
            new[] { mesh }, CreateEvaluationFrame(), default);

        byte[] stl = result.AsBytes();
        // First triangle's attribute bytes at offset 84+48=132.
        Assert.Equal(0, stl[132]);
        Assert.Equal(0, stl[133]);
        // Second triangle's at 84+50+48=182.
        Assert.Equal(0, stl[182]);
        Assert.Equal(0, stl[183]);
    }

    // ─────────────────────── Helpers ───────────────────────

    private static ValueRef BuildSampleQuadMesh()
    {
        // CCW from +Y: triangles (0, 2, 1) and (1, 2, 3) produce +Y face
        // normals via the right-hand rule.
        Vector3[] positions = [new(0, 0, 0), new(1, 0, 0), new(0, 0, 1), new(1, 0, 1)];
        uint[] indices = [0, 2, 1, 1, 2, 3];

        const int vertexStride = MeshHeader.PositionStrideBytes
            + MeshHeader.ColorStrideBytes
            + MeshHeader.NormalStrideBytes;

        MeshHeader header = new(
            Version: MeshHeader.CurrentVersion,
            Flags: MeshFlags.HasColor | MeshFlags.HasNormals,
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
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 16, 4), 0f);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 20, 4), 1f);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 24, 4), 0f);
        }

        int indicesBase = vertexBase + 4 * vertexStride;
        for (int i = 0; i < 6; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(indicesBase + i * 4, 4), indices[i]);
        }
        return ValueRef.FromMesh(blob);
    }
}
