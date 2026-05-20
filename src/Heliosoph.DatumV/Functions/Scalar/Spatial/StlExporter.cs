using System.Buffers.Binary;
using System.Numerics;

using Heliosoph.DatumV.Model.Spatial;

namespace Heliosoph.DatumV.Functions.Scalar.Spatial;

/// <summary>
/// Serializes a <see cref="Heliosoph.DatumV.Model.DataKind.Mesh"/> blob to the
/// binary STL format — the universal 3D-printing exchange. STL carries no
/// color, no per-vertex normals, no UVs; just triangle positions and a
/// per-face normal. Every slicer (Bambu Studio, PrusaSlicer, Cura,
/// Lychee, ChiTuBox) reads STL natively. Always emits in OpenGL
/// convention (right-handed, +Y up, −Z forward) — converts from
/// <see cref="PointCloudCoordinateFrame.CameraOpenCv"/> by rotating
/// 180° around the X axis.
/// </summary>
internal static class StlExporter
{
    /// <summary>
    /// Binary STL file layout (little-endian throughout):
    /// <code>
    /// offset  size   field
    ///    0     80    header (zero-filled; conventionally an ASCII comment but slicers ignore it)
    ///   80      4    uint32 triangleCount
    ///   84   N*50    triangles, each:
    ///                  12 bytes: float32×3 face normal
    ///                  12 bytes: float32×3 vertex 1
    ///                  12 bytes: float32×3 vertex 2
    ///                  12 bytes: float32×3 vertex 3
    ///                   2 bytes: uint16 attribute byte count (always 0)
    /// </code>
    /// </summary>
    public static byte[] Export(byte[] meshBlob, string generator)
    {
        MeshHeader header = MeshHeader.Read(meshBlob);

        int vertexCount = checked((int)header.VertexCount);
        int triangleCount = checked((int)header.TriangleCount);
        int vertexStride = header.VertexStrideBytes;
        ReadOnlySpan<byte> blobSpan = meshBlob;
        int vertexBase = MeshHeader.SizeBytes;
        int trianglesBase = vertexBase + vertexCount * vertexStride;
        bool flipYZ = header.CoordinateFrame == PointCloudCoordinateFrame.CameraOpenCv;

        // Decode positions once into a managed array — every triangle reads
        // three vertices and most clouds have shared vertices across many
        // triangles, so a one-time materialization is cheaper than re-
        // reading the blob per-face.
        Vector3[] positions = new Vector3[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            int slotOffset = vertexBase + i * vertexStride;
            float x = BinaryPrimitives.ReadSingleLittleEndian(blobSpan.Slice(slotOffset + 0, 4));
            float y = BinaryPrimitives.ReadSingleLittleEndian(blobSpan.Slice(slotOffset + 4, 4));
            float z = BinaryPrimitives.ReadSingleLittleEndian(blobSpan.Slice(slotOffset + 8, 4));
            positions[i] = flipYZ ? new Vector3(x, -y, -z) : new Vector3(x, y, z);
        }

        const int HeaderSize = 80;
        const int CountSize = 4;
        const int TriangleSize = 50;
        long totalSize = HeaderSize + CountSize + (long)triangleCount * TriangleSize;
        byte[] stl = new byte[totalSize];
        Span<byte> stlSpan = stl;

        // 80-byte header. Conventional wisdom says don't start with "solid"
        // (that prefix is the ASCII-STL magic, and some slicers refuse to
        // parse binary STL whose header starts that way). Embed a short
        // generator tag instead.
        string headerText = $"Heliosoph.DatumV STL ({generator})";
        int headerBytes = System.Math.Min(headerText.Length, HeaderSize);
        System.Text.Encoding.ASCII.GetBytes(headerText, 0, headerBytes, stl, 0);
        // Remaining bytes already zero from the array allocation.

        BinaryPrimitives.WriteUInt32LittleEndian(stlSpan.Slice(HeaderSize, CountSize), (uint)triangleCount);

        int triOffset = HeaderSize + CountSize;
        for (int t = 0; t < triangleCount; t++)
        {
            uint ia = BinaryPrimitives.ReadUInt32LittleEndian(blobSpan.Slice(trianglesBase + t * 12 + 0, 4));
            uint ib = BinaryPrimitives.ReadUInt32LittleEndian(blobSpan.Slice(trianglesBase + t * 12 + 4, 4));
            uint ic = BinaryPrimitives.ReadUInt32LittleEndian(blobSpan.Slice(trianglesBase + t * 12 + 8, 4));

            Vector3 a = positions[ia];
            Vector3 b = positions[ib];
            Vector3 c = positions[ic];

            // Face normal from cross product, normalized. Degenerate
            // triangles (collinear vertices) fall back to (0, 0, 1) rather
            // than producing NaN — STL slicers tolerate any unit normal.
            Vector3 faceNormal = Vector3.Cross(b - a, c - a);
            float lenSq = faceNormal.LengthSquared();
            faceNormal = lenSq > 1e-20f
                ? faceNormal / MathF.Sqrt(lenSq)
                : new Vector3(0, 0, 1);

            WriteVector3(stlSpan.Slice(triOffset + 0, 12), faceNormal);
            WriteVector3(stlSpan.Slice(triOffset + 12, 12), a);
            WriteVector3(stlSpan.Slice(triOffset + 24, 12), b);
            WriteVector3(stlSpan.Slice(triOffset + 36, 12), c);
            // attribute byte count: always 0 (slicers ignore it).
            BinaryPrimitives.WriteUInt16LittleEndian(stlSpan.Slice(triOffset + 48, 2), 0);
            triOffset += TriangleSize;
        }

        return stl;
    }

    private static void WriteVector3(Span<byte> dst, Vector3 v)
    {
        BinaryPrimitives.WriteSingleLittleEndian(dst[0..4], v.X);
        BinaryPrimitives.WriteSingleLittleEndian(dst[4..8], v.Y);
        BinaryPrimitives.WriteSingleLittleEndian(dst[8..12], v.Z);
    }
}
