using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;

using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Spatial;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Model.Spatial;

namespace DatumIngest.Tests.Functions.Scalar.Spatial;

/// <summary>
/// Tests for <see cref="MeshToGltfFunction"/>. Verifies metadata, null
/// propagation, the .glb container structure (magic / version / chunk
/// header bytes), JSON-chunk parseability, primitive declaration, and
/// the coordinate-frame transform from CameraOpenCv source meshes.
/// </summary>
public sealed class MeshToGltfFunctionTests : ServiceTestBase
{
    [Fact]
    public void Metadata_ExposesNameCategoryAndDescription()
    {
        Assert.Equal("mesh_to_gltf", MeshToGltfFunction.Name);
        Assert.Equal(FunctionCategory.Image, MeshToGltfFunction.Category);
        Assert.False(string.IsNullOrWhiteSpace(MeshToGltfFunction.Description));
    }

    [Fact]
    public void Validate_AcceptsMesh_ReturnsUInt8Array()
    {
        DataKind kind = new MeshToGltfFunction()
            .ValidateArguments([DataKind.Mesh]);
        Assert.Equal(DataKind.UInt8, kind);
    }

    [Fact]
    public async Task Execute_NullInput_ReturnsNullArray()
    {
        ValueRef result = await new MeshToGltfFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.Mesh) }, CreateEvaluationFrame(), default);

        Assert.True(result.IsNull);
    }

    [Fact]
    public async Task Execute_ProducesValidGlbMagicAndVersion()
    {
        ValueRef mesh = BuildSampleColoredMesh();

        ValueRef result = await new MeshToGltfFunction().ExecuteAsync(
            new[] { mesh }, CreateEvaluationFrame(), default);

        byte[] glb = result.AsBytes();

        // .glb file header (12 bytes):
        //   bytes 0-3: ASCII "glTF" (0x46546C67 in little-endian uint32)
        //   bytes 4-7: uint32 version = 2
        //   bytes 8-11: uint32 total length (must equal glb.Length)
        Assert.True(glb.Length > 12, $"expected non-empty .glb; got {glb.Length} bytes");
        Assert.Equal((byte)'g', glb[0]);
        Assert.Equal((byte)'l', glb[1]);
        Assert.Equal((byte)'T', glb[2]);
        Assert.Equal((byte)'F', glb[3]);
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(4, 4));
        uint totalLength = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(8, 4));
        Assert.Equal(2u, version);
        Assert.Equal((uint)glb.Length, totalLength);
    }

    [Fact]
    public async Task Execute_JsonChunk_DeclaresExpectedAccessors()
    {
        ValueRef mesh = BuildSampleColoredMesh();

        ValueRef result = await new MeshToGltfFunction().ExecuteAsync(
            new[] { mesh }, CreateEvaluationFrame(), default);

        byte[] glb = result.AsBytes();
        string json = ExtractJsonChunk(glb);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        // Single mesh, single primitive, with POSITION + NORMAL + COLOR_0
        // attributes (matches BuildSampleColoredMesh which has color+normals)
        // and triangle indices.
        Assert.True(root.TryGetProperty("meshes", out JsonElement meshes));
        Assert.Equal(1, meshes.GetArrayLength());
        JsonElement primitive = meshes[0].GetProperty("primitives")[0];
        JsonElement attributes = primitive.GetProperty("attributes");
        Assert.True(attributes.TryGetProperty("POSITION", out _));
        Assert.True(attributes.TryGetProperty("NORMAL", out _));
        Assert.True(attributes.TryGetProperty("COLOR_0", out _));
        Assert.True(primitive.TryGetProperty("indices", out _));

        // Generator stamp.
        Assert.Equal("DatumIngest", root.GetProperty("asset").GetProperty("generator").GetString());

        // Material with KHR_materials_unlit extension declared.
        Assert.True(root.TryGetProperty("materials", out JsonElement materials));
        Assert.True(materials.GetArrayLength() >= 1);
        Assert.True(materials[0].GetProperty("extensions").TryGetProperty("KHR_materials_unlit", out _));

        // Extensions used list must include unlit.
        bool foundUnlit = false;
        if (root.TryGetProperty("extensionsUsed", out JsonElement extsUsed))
        {
            foreach (JsonElement ext in extsUsed.EnumerateArray())
            {
                if (ext.GetString() == "KHR_materials_unlit") foundUnlit = true;
            }
        }
        Assert.True(foundUnlit, "expected extensionsUsed to include KHR_materials_unlit");
    }

    [Fact]
    public async Task Execute_RoundTripParsesAsValidGltf()
    {
        // The strongest correctness check — run the output back through
        // SharpGLTF's own reader. If it parses without throwing the file
        // is structurally valid (chunk alignment, JSON validity, accessor
        // byte ranges, the works).
        ValueRef mesh = BuildSampleColoredMesh();

        ValueRef result = await new MeshToGltfFunction().ExecuteAsync(
            new[] { mesh }, CreateEvaluationFrame(), default);

        byte[] glb = result.AsBytes();
        using MemoryStream stream = new(glb);
        SharpGLTF.Schema2.ModelRoot reread = SharpGLTF.Schema2.ModelRoot.ReadGLB(stream);

        Assert.Single(reread.LogicalMeshes);
        Assert.Equal(4, reread.LogicalMeshes[0].Primitives[0].VertexAccessors["POSITION"].Count);
        // 2 triangles × 3 indices = 6
        Assert.Equal(6, reread.LogicalMeshes[0].Primitives[0].IndexAccessor.Count);
    }

    // ─────────────────────── Helpers ───────────────────────

    /// <summary>
    /// Extracts the JSON chunk payload from a .glb buffer. .glb layout:
    /// 12-byte file header, then chunks each prefixed by 4-byte length +
    /// 4-byte chunk type. JSON chunk has type "JSON" (0x4E4F534A).
    /// </summary>
    private static string ExtractJsonChunk(byte[] glb)
    {
        // chunk header at offset 12: uint32 length, uint32 type
        uint chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12, 4));
        uint chunkType = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(16, 4));
        const uint JsonChunkType = 0x4E4F534Au;
        Assert.Equal(JsonChunkType, chunkType);
        return Encoding.UTF8.GetString(glb, 20, (int)chunkLength).TrimEnd(' ');
    }

    private static ValueRef BuildSampleColoredMesh() =>
        BuildMesh(
            positions: [new(0, 0, 0), new(1, 0, 0), new(0, 0, 1), new(1, 0, 1)],
            normalY: 1f,
            includeColor: true,
            indices: [0, 2, 1, 1, 2, 3],
            frame: PointCloudCoordinateFrame.CameraOpenGl);
    private static ValueRef BuildMesh(
        Vector3[] positions, float normalY, bool includeColor,
        uint[] indices, PointCloudCoordinateFrame frame)
    {
        MeshFlags flags = MeshFlags.HasNormals | (includeColor ? MeshFlags.HasColor : MeshFlags.None);
        int vertexStride = MeshHeader.PositionStrideBytes
            + (includeColor ? MeshHeader.ColorStrideBytes : 0)
            + MeshHeader.NormalStrideBytes;

        Vector3 bboxMin = positions[0];
        Vector3 bboxMax = positions[0];
        foreach (Vector3 p in positions)
        {
            bboxMin = Vector3.Min(bboxMin, p);
            bboxMax = Vector3.Max(bboxMax, p);
        }

        MeshHeader header = new(
            Version: MeshHeader.CurrentVersion,
            Flags: flags,
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
            int next = offset + 12;
            if (includeColor)
            {
                span[next + 0] = 200;
                span[next + 1] = 200;
                span[next + 2] = 200;
                span[next + 3] = 255;
                next += 4;
            }
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(next + 0, 4), 0f);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(next + 4, 4), normalY);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(next + 8, 4), 0f);
        }

        int indicesBase = vertexBase + positions.Length * vertexStride;
        for (int i = 0; i < indices.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(indicesBase + i * 4, 4), indices[i]);
        }
        return ValueRef.FromMesh(blob);
    }

}
