using System.Buffers.Binary;
using System.Numerics;
using Heliosoph.DatumV.IO;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Model.Spatial;

namespace Heliosoph.DatumV.Tests.Model.Spatial;

/// <summary>
/// Slice α coverage for <see cref="DataKind.Mesh"/> — round-trips a
/// hand-built mesh blob through the arena, wire format, and DataValue
/// equality. Construction-side scalars (mesh_from_organized,
/// mesh_from_depth_*, accessors, exporters) ship in slices β and γ.
/// </summary>
public sealed class MeshRoundTripTests : ServiceTestBase
{
    [Fact]
    public void DataValue_FromMesh_AsMesh_RoundTripsBlob()
    {
        Arena store = CreateArena();
        byte[] blob = BuildSampleMeshBlob();

        DataValue value = DataValue.FromMesh(blob, store);

        Assert.Equal(DataKind.Mesh, value.Kind);
        Assert.True(value.IsBlobKind);
        Assert.False(value.IsInSidecar);

        byte[] restored = value.AsMesh(store);
        Assert.Equal(blob, restored);
    }

    [Fact]
    public void DataValue_Mesh_OffsetEqualityAndHash()
    {
        Arena store = CreateArena();
        byte[] blob = BuildSampleMeshBlob();

        DataValue a = DataValue.FromMesh(blob, store);
        DataValue b = a;  // same (_p0, _p1) in the same store — offset-equal

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        // A second blob stored separately ends up at a different arena offset →
        // offset-inequality, even though byte contents match. Matches the
        // documented offset-equality semantics for Image / Audio / Video / Json /
        // PointCloud.
        DataValue c = DataValue.FromMesh(blob, store);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void DataValue_AsByteSpan_ReadsMeshBlob()
    {
        Arena store = CreateArena();
        byte[] blob = BuildSampleMeshBlob();

        DataValue value = DataValue.FromMesh(blob, store);
        ReadOnlySpan<byte> span = value.AsByteSpan(store);

        Assert.Equal(blob.Length, span.Length);
        Assert.True(span.SequenceEqual(blob));
    }

    [Fact]
    public void DataValue_WireFormat_RoundTripsThroughDataValueWriterReader()
    {
        Arena writeStore = CreateArena();
        byte[] blob = BuildSampleMeshBlob();
        DataValue original = DataValue.FromMesh(blob, writeStore);

        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            DataValueWriter.WriteDataValue(writer, original, writeStore);
        }

        stream.Position = 0;
        Arena readStore = CreateArena();
        using BinaryReader reader = new(stream);
        DataValue restored = DataValueReader.ReadDataValue(reader, readStore);

        Assert.Equal(DataKind.Mesh, restored.Kind);
        Assert.Equal(blob, restored.AsMesh(readStore));

        // Header survives the round-trip with semantically identical fields.
        MeshHeader expectedHeader = MeshHeader.Read(blob);
        MeshHeader actualHeader = MeshHeader.Read(restored.AsMesh(readStore));
        Assert.Equal(expectedHeader, actualHeader);
    }

    // ─────────────────────── Test helpers ───────────────────────

    /// <summary>
    /// Builds a 4-vertex, 2-triangle colored mesh blob — a single quad split
    /// into two triangles, with per-vertex colors at the corners and unit
    /// +Y normals. Exercises the full Phase 1 stride (pos + color + normal
    /// = 28 bytes per vertex) without depending on any constructor function.
    /// </summary>
    private static byte[] BuildSampleMeshBlob()
    {
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

        byte[] blob = new byte[header.TotalSizeBytes];
        Span<byte> span = blob;
        header.Write(span[..MeshHeader.SizeBytes]);

        Vector3 normal = new(0, 1, 0);
        (Vector3 pos, byte r, byte g, byte b, byte a)[] vertices =
        [
            (new(0, 0, 0), 255,   0,   0, 255),
            (new(1, 0, 0),   0, 255,   0, 255),
            (new(0, 0, 1),   0,   0, 255, 255),
            (new(1, 0, 1), 255, 255, 255, 255),
        ];

        int offset = MeshHeader.SizeBytes;
        const int vertexStride = 28;
        foreach (var (pos, r, g, b, a) in vertices)
        {
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 0, 4), pos.X);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 4, 4), pos.Y);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 8, 4), pos.Z);
            span[offset + 12] = r;
            span[offset + 13] = g;
            span[offset + 14] = b;
            span[offset + 15] = a;
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 16, 4), normal.X);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 20, 4), normal.Y);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 24, 4), normal.Z);
            offset += vertexStride;
        }

        // Two triangles (CCW winding) forming the quad: (0,1,2) and (2,1,3).
        uint[] indices = [0, 1, 2, 2, 1, 3];
        for (int i = 0; i < indices.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset, 4), indices[i]);
            offset += 4;
        }

        return blob;
    }
}
