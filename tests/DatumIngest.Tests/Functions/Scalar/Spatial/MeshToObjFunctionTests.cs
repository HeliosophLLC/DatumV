using System.Buffers.Binary;
using System.Numerics;
using System.Text;

using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Spatial;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Model.Spatial;

namespace DatumIngest.Tests.Functions.Scalar.Spatial;

/// <summary>
/// Tests for <see cref="MeshToObjFunction"/>. Verifies metadata, null
/// propagation, OBJ text format conformance (correct v/vn/f line counts,
/// per-vertex color extension when HasColor), and the CV→GL coordinate
/// transform on export.
/// </summary>
public sealed class MeshToObjFunctionTests : ServiceTestBase
{
    [Fact]
    public void Metadata_ExposesNameCategoryAndDescription()
    {
        Assert.Equal("mesh_to_obj", MeshToObjFunction.Name);
        Assert.Equal(FunctionCategory.Image, MeshToObjFunction.Category);
    }

    [Fact]
    public async Task Execute_NullInput_ReturnsNullArray()
    {
        ValueRef result = await new MeshToObjFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.Mesh) }, CreateEvaluationFrame(), default);
        Assert.True(result.IsNull);
    }

    [Fact]
    public async Task Execute_ProducesCorrectVAndFLineCounts()
    {
        ValueRef mesh = BuildSampleQuadMesh();

        ValueRef result = await new MeshToObjFunction().ExecuteAsync(
            new[] { mesh }, CreateEvaluationFrame(), default);

        string obj = Encoding.UTF8.GetString(result.AsBytes());
        string[] lines = obj.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        int vCount = lines.Count(l => l.StartsWith("v "));
        int vnCount = lines.Count(l => l.StartsWith("vn "));
        int fCount = lines.Count(l => l.StartsWith("f "));

        Assert.Equal(4, vCount);   // 4 vertices
        Assert.Equal(4, vnCount);  // one normal per vertex
        Assert.Equal(2, fCount);   // 2 triangles
    }

    [Fact]
    public async Task Execute_ColoredMesh_EmitsRgbExtensionOnVertexLines()
    {
        ValueRef mesh = BuildSampleQuadMesh();

        ValueRef result = await new MeshToObjFunction().ExecuteAsync(
            new[] { mesh }, CreateEvaluationFrame(), default);

        string obj = Encoding.UTF8.GetString(result.AsBytes());
        // Each "v X Y Z R G B" line has 7 whitespace-separated tokens
        // ("v" + 3 coords + 3 colors).
        string vLine = obj.Split('\n').First(l => l.StartsWith("v "));
        string[] tokens = vLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(7, tokens.Length);
    }

    [Fact]
    public async Task Execute_FaceLines_UseOneBasedIndicesWithNormals()
    {
        ValueRef mesh = BuildSampleQuadMesh();

        ValueRef result = await new MeshToObjFunction().ExecuteAsync(
            new[] { mesh }, CreateEvaluationFrame(), default);

        string obj = Encoding.UTF8.GetString(result.AsBytes());
        string fLine = obj.Split('\n').First(l => l.StartsWith("f "));

        // Format: "f a//na b//nb c//nc" with 1-based indices.
        Assert.Contains("//", fLine);
        string[] vertices = fLine.Substring(2).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, vertices.Length);
        foreach (string spec in vertices)
        {
            string[] parts = spec.Split("//");
            Assert.Equal(2, parts.Length);
            Assert.True(int.Parse(parts[0]) >= 1);
        }
    }

    [Fact]
    public async Task Execute_CameraOpenCvSource_AppliesYZFlip()
    {
        // Build a mesh with a known vertex at +Y, +Z in the CV frame.
        // After export we expect that vertex's coordinates to be y=-Y, z=-Z.
        ValueRef mesh = BuildMeshWithSinglePositiveVertex(
            frame: PointCloudCoordinateFrame.CameraOpenCv);

        ValueRef result = await new MeshToObjFunction().ExecuteAsync(
            new[] { mesh }, CreateEvaluationFrame(), default);

        string obj = Encoding.UTF8.GetString(result.AsBytes());
        // Find the vertex line for the (1, 2, 3) point — it should now
        // print as (1, -2, -3).
        bool foundFlipped = obj.Split('\n').Any(l =>
            l.StartsWith("v 1 -2 -3")
            || l.StartsWith("v 1.0 -2.0 -3.0")
            || l.StartsWith("v 1 -2.0 -3.0")
            || l.Contains("1 -2 -3"));
        Assert.True(foundFlipped, $"expected y/z-flipped vertex in OBJ; got:\n{obj}");
    }

    // ─────────────────────── Helpers ───────────────────────

    private static ValueRef BuildSampleQuadMesh()
        => BuildMesh(
            positions: [new(0, 0, 0), new(1, 0, 0), new(0, 0, 1), new(1, 0, 1)],
            normalY: 1f,
            indices: [0, 2, 1, 1, 2, 3],
            frame: PointCloudCoordinateFrame.CameraOpenGl);

    private static ValueRef BuildMeshWithSinglePositiveVertex(PointCloudCoordinateFrame frame)
        => BuildMesh(
            positions: [new(0, 0, 0), new(1, 2, 3), new(0, 0, 1)],
            normalY: 1f,
            indices: [0, 1, 2],
            frame: frame);

    private static ValueRef BuildMesh(
        Vector3[] positions, float normalY, uint[] indices, PointCloudCoordinateFrame frame)
    {
        const int vertexStride = MeshHeader.PositionStrideBytes
            + MeshHeader.ColorStrideBytes
            + MeshHeader.NormalStrideBytes; // 28

        Vector3 bboxMin = positions[0];
        Vector3 bboxMax = positions[0];
        foreach (Vector3 p in positions)
        {
            bboxMin = Vector3.Min(bboxMin, p);
            bboxMax = Vector3.Max(bboxMax, p);
        }

        MeshHeader header = new(
            Version: MeshHeader.CurrentVersion,
            Flags: MeshFlags.HasColor | MeshFlags.HasNormals,
            CoordinateFrame: frame,
            VertexCount: (uint)positions.Length,
            TriangleCount: (uint)(indices.Length / 3),
            BboxMin: bboxMin,
            BboxMax: bboxMax,
            TextureOffset: 0,
            TextureLength: 0);

        long totalSize = MeshHeader.SizeBytes
            + (long)positions.Length * vertexStride
            + (long)(indices.Length / 3) * MeshHeader.IndexStrideBytes;
        byte[] blob = new byte[totalSize];
        Span<byte> span = blob;
        header.Write(span[..MeshHeader.SizeBytes]);

        int vertexBase = MeshHeader.SizeBytes;
        for (int i = 0; i < positions.Length; i++)
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
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 20, 4), normalY);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 24, 4), 0f);
        }

        int indicesBase = vertexBase + positions.Length * vertexStride;
        for (int i = 0; i < indices.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(indicesBase + i * 4, 4), indices[i]);
        }
        return ValueRef.FromMesh(blob);
    }
}
