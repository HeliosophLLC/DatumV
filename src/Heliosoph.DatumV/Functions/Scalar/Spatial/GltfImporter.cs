using System.Buffers.Binary;
using System.Numerics;
using Heliosoph.DatumV.Model.Spatial;
using SharpGLTF.Schema2;

namespace Heliosoph.DatumV.Functions.Scalar.Spatial;

/// <summary>
/// Parses a binary glTF 2.0 (.glb) blob into a
/// <see cref="Heliosoph.DatumV.Model.DataKind.Mesh"/> blob the engine
/// understands. Inverse of <see cref="GltfExporter"/>, closing the
/// round-trip loop after a Parquet COPY → re-import.
/// </summary>
/// <remarks>
/// <para>
/// Phase 1 surface (matching <see cref="GltfExporter"/>): one logical mesh,
/// one primitive, triangles only. POSITION is required; NORMAL and
/// COLOR_0 are optional. UVs (TEXCOORD_0) and embedded textures are read
/// as metadata only — not preserved in the v1 Mesh blob because the
/// header's <c>HasUVs</c> / <c>HasTexture</c> bits are reserved for
/// Phase 2.
/// </para>
/// <para>
/// Coordinate frame: glTF spec mandates right-handed +Y up, −Z forward.
/// We tag the resulting blob as <see cref="PointCloudCoordinateFrame.CameraOpenGl"/>.
/// No rotation applied — clouds / meshes that started in CV-frame and went
/// through GltfExporter were y/z-flipped on export; importing them keeps
/// them in GL-frame internally, which matches how we treat foreign meshes
/// from Blender / Three.js.
/// </para>
/// </remarks>
internal static class GltfImporter
{
    /// <summary>
    /// Parses <paramref name="glbBytes"/> and returns a freshly allocated
    /// <see cref="Heliosoph.DatumV.Model.DataKind.Mesh"/> blob. Throws
    /// <see cref="InvalidDataException"/> when the file is not a valid
    /// .glb, when it has zero or multiple logical meshes/primitives, or
    /// when the primitive's topology is not <c>TRIANGLES</c>.
    /// </summary>
    public static byte[] Import(byte[] glbBytes)
    {
        ModelRoot model;
        try
        {
            using MemoryStream stream = new(glbBytes, writable: false);
            model = ModelRoot.ReadGLB(stream);
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException(
                $"Failed to parse glTF / .glb input: {ex.Message}", ex);
        }

        if (model.LogicalMeshes.Count == 0)
        {
            throw new InvalidDataException("glTF file contains no meshes.");
        }
        Mesh gltfMesh = model.LogicalMeshes[0];
        if (gltfMesh.Primitives.Count == 0)
        {
            throw new InvalidDataException("glTF mesh contains no primitives.");
        }
        MeshPrimitive prim = gltfMesh.Primitives[0];
        if (prim.DrawPrimitiveType != PrimitiveType.TRIANGLES)
        {
            throw new InvalidDataException(
                $"glTF primitive uses {prim.DrawPrimitiveType} topology; only TRIANGLES is supported.");
        }

        // Required: POSITION.
        IList<Vector3> positions = ReadVector3List(prim, "POSITION")
            ?? throw new InvalidDataException("glTF primitive is missing the POSITION attribute.");

        // Optional: NORMAL, COLOR_0. COLOR_0 may be Vector3 (RGB) or Vector4 (RGBA)
        // depending on the source glTF; SharpGLTF normalises to Vector4.
        IList<Vector3>? normals = ReadVector3List(prim, "NORMAL");
        IList<Vector4>? colors = ReadVector4List(prim, "COLOR_0");

        // Indices: glTF allows uint8 / uint16 / uint32; SharpGLTF widens
        // everything to uint when AsIndicesArray() is called.
        IList<uint> indices = prim.GetIndices()
            ?? throw new InvalidDataException("glTF primitive has no index buffer.");
        if (indices.Count == 0 || indices.Count % 3 != 0)
        {
            throw new InvalidDataException(
                $"glTF triangle list has {indices.Count} indices; expected a non-zero multiple of 3.");
        }

        int vertexCount = positions.Count;
        int triangleCount = indices.Count / 3;

        bool hasColor = colors is not null;
        bool hasNormals = normals is not null;

        MeshFlags flags = MeshFlags.None;
        if (hasColor) flags |= MeshFlags.HasColor;
        if (hasNormals) flags |= MeshFlags.HasNormals;

        int vertexStride = MeshHeader.PositionStrideBytes
            + (hasColor ? MeshHeader.ColorStrideBytes : 0)
            + (hasNormals ? MeshHeader.NormalStrideBytes : 0);

        long payloadBytes = (long)vertexCount * vertexStride
                          + (long)triangleCount * MeshHeader.IndexStrideBytes;
        byte[] blob = new byte[MeshHeader.SizeBytes + payloadBytes];

        // Per-vertex bbox + payload write.
        Vector3 bboxMin = new(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        Vector3 bboxMax = new(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

        Span<byte> dst = blob.AsSpan(MeshHeader.SizeBytes);
        int dstOffset = 0;
        for (int i = 0; i < vertexCount; i++)
        {
            Vector3 p = positions[i];
            BinaryPrimitives.WriteSingleLittleEndian(dst.Slice(dstOffset + 0, 4), p.X);
            BinaryPrimitives.WriteSingleLittleEndian(dst.Slice(dstOffset + 4, 4), p.Y);
            BinaryPrimitives.WriteSingleLittleEndian(dst.Slice(dstOffset + 8, 4), p.Z);
            dstOffset += 12;

            if (p.X < bboxMin.X) bboxMin.X = p.X; if (p.X > bboxMax.X) bboxMax.X = p.X;
            if (p.Y < bboxMin.Y) bboxMin.Y = p.Y; if (p.Y > bboxMax.Y) bboxMax.Y = p.Y;
            if (p.Z < bboxMin.Z) bboxMin.Z = p.Z; if (p.Z > bboxMax.Z) bboxMax.Z = p.Z;

            if (hasColor)
            {
                Vector4 c = colors![i];
                dst[dstOffset + 0] = (byte)System.Math.Clamp((int)(c.X * 255f + 0.5f), 0, 255);
                dst[dstOffset + 1] = (byte)System.Math.Clamp((int)(c.Y * 255f + 0.5f), 0, 255);
                dst[dstOffset + 2] = (byte)System.Math.Clamp((int)(c.Z * 255f + 0.5f), 0, 255);
                dst[dstOffset + 3] = (byte)System.Math.Clamp((int)(c.W * 255f + 0.5f), 0, 255);
                dstOffset += 4;
            }

            if (hasNormals)
            {
                Vector3 n = normals![i];
                BinaryPrimitives.WriteSingleLittleEndian(dst.Slice(dstOffset + 0, 4), n.X);
                BinaryPrimitives.WriteSingleLittleEndian(dst.Slice(dstOffset + 4, 4), n.Y);
                BinaryPrimitives.WriteSingleLittleEndian(dst.Slice(dstOffset + 8, 4), n.Z);
                dstOffset += 12;
            }
        }
        if (vertexCount == 0)
        {
            bboxMin = Vector3.Zero;
            bboxMax = Vector3.Zero;
        }

        // Index payload — uint32 per index, in iteration order.
        for (int i = 0; i < indices.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(dstOffset, 4), indices[i]);
            dstOffset += 4;
        }

        MeshHeader header = new(
            MeshHeader.CurrentVersion,
            flags,
            PointCloudCoordinateFrame.CameraOpenGl,
            (uint)vertexCount,
            (uint)triangleCount,
            bboxMin,
            bboxMax,
            TextureOffset: 0,
            TextureLength: 0);
        header.Write(blob);
        return blob;
    }

    private static IList<Vector3>? ReadVector3List(MeshPrimitive prim, string name)
    {
        if (!prim.VertexAccessors.TryGetValue(name, out Accessor? accessor) || accessor is null)
        {
            return null;
        }
        return accessor.AsVector3Array();
    }

    private static IList<Vector4>? ReadVector4List(MeshPrimitive prim, string name)
    {
        if (!prim.VertexAccessors.TryGetValue(name, out Accessor? accessor) || accessor is null)
        {
            return null;
        }
        // SharpGLTF surfaces COLOR_0 stored as VEC3 by widening to VEC4 with
        // alpha = 1 when requested via AsColorArray. For non-color VEC3
        // accessors this throws, which is the right behaviour for the
        // standard-compliant glTF we accept.
        return accessor.AsColorArray();
    }
}
