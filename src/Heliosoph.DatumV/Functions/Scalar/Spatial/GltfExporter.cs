using System.Buffers.Binary;
using System.Numerics;

using Heliosoph.DatumV.Model.Spatial;

using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

namespace Heliosoph.DatumV.Functions.Scalar.Spatial;

/// <summary>
/// Serializes a <see cref="Heliosoph.DatumV.Model.DataKind.Mesh"/> blob to the
/// binary glTF 2.0 (.glb) wire format using the SharpGLTF library. Always
/// emits in glTF's right-handed +Y-up, −Z-forward convention regardless of
/// the source mesh's <c>CoordinateFrame</c> — converts from
/// <see cref="PointCloudCoordinateFrame.CameraOpenCv"/> by rotating 180°
/// around the X axis (y→−y, z→−z) so the output renders correctly in any
/// glTF consumer (Blender, Three.js, Unity, the browser's built-in viewer).
/// </summary>
internal static class GltfExporter
{
    /// <summary>
    /// Builds a .glb byte buffer for the given mesh blob. Phase 1 emits a
    /// single scene, single node, single mesh primitive with POSITION,
    /// optional NORMAL, optional COLOR_0, and triangle indices. The material
    /// uses the <c>KHR_materials_unlit</c> extension so vertex colors render
    /// as-is — depth-derived meshes don't have real materials, and unlit
    /// shading is what every consumer's default lighting expects for a
    /// screen-captured photo's colors.
    /// </summary>
    public static byte[] Export(byte[] meshBlob, string generator)
    {
        MeshHeader header = MeshHeader.Read(meshBlob);

        // Decode per-vertex attributes into managed arrays. SharpGLTF's
        // builder API takes typed structs directly; the marshalling cost
        // is a one-time linear scan, negligible compared to mesh sizes
        // that would otherwise matter for export latency.
        DecodeVertexData(meshBlob, header,
            out Vector3[] positions, out Vector3[]? normals, out Vector4[]? colors);

        int[] indices = DecodeIndices(meshBlob, header);

        // CV→GL is a proper rotation (det = +1) so winding order is
        // preserved — no need to swap two indices per triangle.

        MaterialBuilder material = new MaterialBuilder("VertexColor")
            .WithUnlitShader();

        // Four flag combinations → four explicit branches. Inlining the
        // generic VertexBuilder<TvG, TvM, TvS> call site at each branch
        // avoids the (TvG, TvM) vs (TvG, TvS) constructor ambiguity that
        // hits when TvM == TvS == VertexEmpty.
        SceneBuilder scene = (header.HasColor, header.HasNormals) switch
        {
            (true, true) => BuildSceneColorNormal(positions, normals!, colors!, indices, material),
            (true, false) => BuildSceneColor(positions, colors!, indices, material),
            (false, true) => BuildSceneNormal(positions, normals!, indices, material),
            (false, false) => BuildScenePositionOnly(positions, indices, material),
        };

        SharpGLTF.Schema2.ModelRoot model = scene.ToGltf2();
        model.Asset.Generator = generator;

        using MemoryStream stream = new();
        model.WriteGLB(stream);
        return stream.ToArray();
    }

    // ──────────────── Per-flag-combination scene builders ────────────────

    private static SceneBuilder BuildSceneColorNormal(
        Vector3[] positions, Vector3[] normals, Vector4[] colors,
        int[] indices, MaterialBuilder material)
    {
        var mb = new MeshBuilder<VertexPositionNormal, VertexColor1, VertexEmpty>("Mesh");
        var prim = mb.UsePrimitive(material);
        for (int i = 0; i < indices.Length; i += 3)
        {
            int ia = indices[i], ib = indices[i + 1], ic = indices[i + 2];
            prim.AddTriangle(
                new VertexBuilder<VertexPositionNormal, VertexColor1, VertexEmpty>(
                    new VertexPositionNormal(positions[ia], normals[ia]),
                    new VertexColor1(colors[ia])),
                new VertexBuilder<VertexPositionNormal, VertexColor1, VertexEmpty>(
                    new VertexPositionNormal(positions[ib], normals[ib]),
                    new VertexColor1(colors[ib])),
                new VertexBuilder<VertexPositionNormal, VertexColor1, VertexEmpty>(
                    new VertexPositionNormal(positions[ic], normals[ic]),
                    new VertexColor1(colors[ic])));
        }
        SceneBuilder scene = new();
        scene.AddRigidMesh(mb, Matrix4x4.Identity);
        return scene;
    }

    private static SceneBuilder BuildSceneColor(
        Vector3[] positions, Vector4[] colors,
        int[] indices, MaterialBuilder material)
    {
        var mb = new MeshBuilder<VertexPosition, VertexColor1, VertexEmpty>("Mesh");
        var prim = mb.UsePrimitive(material);
        for (int i = 0; i < indices.Length; i += 3)
        {
            int ia = indices[i], ib = indices[i + 1], ic = indices[i + 2];
            prim.AddTriangle(
                new VertexBuilder<VertexPosition, VertexColor1, VertexEmpty>(
                    new VertexPosition(positions[ia]),
                    new VertexColor1(colors[ia])),
                new VertexBuilder<VertexPosition, VertexColor1, VertexEmpty>(
                    new VertexPosition(positions[ib]),
                    new VertexColor1(colors[ib])),
                new VertexBuilder<VertexPosition, VertexColor1, VertexEmpty>(
                    new VertexPosition(positions[ic]),
                    new VertexColor1(colors[ic])));
        }
        SceneBuilder scene = new();
        scene.AddRigidMesh(mb, Matrix4x4.Identity);
        return scene;
    }

    private static SceneBuilder BuildSceneNormal(
        Vector3[] positions, Vector3[] normals,
        int[] indices, MaterialBuilder material)
    {
        var mb = new MeshBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty>("Mesh");
        var prim = mb.UsePrimitive(material);
        for (int i = 0; i < indices.Length; i += 3)
        {
            int ia = indices[i], ib = indices[i + 1], ic = indices[i + 2];
            prim.AddTriangle(
                new VertexBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty>(
                    new VertexPositionNormal(positions[ia], normals[ia])),
                new VertexBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty>(
                    new VertexPositionNormal(positions[ib], normals[ib])),
                new VertexBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty>(
                    new VertexPositionNormal(positions[ic], normals[ic])));
        }
        SceneBuilder scene = new();
        scene.AddRigidMesh(mb, Matrix4x4.Identity);
        return scene;
    }

    private static SceneBuilder BuildScenePositionOnly(
        Vector3[] positions, int[] indices, MaterialBuilder material)
    {
        var mb = new MeshBuilder<VertexPosition, VertexEmpty, VertexEmpty>("Mesh");
        var prim = mb.UsePrimitive(material);
        for (int i = 0; i < indices.Length; i += 3)
        {
            int ia = indices[i], ib = indices[i + 1], ic = indices[i + 2];
            prim.AddTriangle(
                new VertexBuilder<VertexPosition, VertexEmpty, VertexEmpty>(
                    new VertexPosition(positions[ia])),
                new VertexBuilder<VertexPosition, VertexEmpty, VertexEmpty>(
                    new VertexPosition(positions[ib])),
                new VertexBuilder<VertexPosition, VertexEmpty, VertexEmpty>(
                    new VertexPosition(positions[ic])));
        }
        SceneBuilder scene = new();
        scene.AddRigidMesh(mb, Matrix4x4.Identity);
        return scene;
    }

    // ──────────────── Blob decoders ────────────────

    private static void DecodeVertexData(
        byte[] meshBlob, MeshHeader header,
        out Vector3[] positions, out Vector3[]? normals, out Vector4[]? colors)
    {
        int vertexCount = checked((int)header.VertexCount);
        int vertexStride = header.VertexStrideBytes;
        int vertexBase = MeshHeader.SizeBytes;
        ReadOnlySpan<byte> blobSpan = meshBlob;

        int colorOffset = MeshHeader.PositionStrideBytes;
        int normalOffset = colorOffset + (header.HasColor ? MeshHeader.ColorStrideBytes : 0);
        bool flipYZ = header.CoordinateFrame == PointCloudCoordinateFrame.CameraOpenCv;

        positions = new Vector3[vertexCount];
        normals = header.HasNormals ? new Vector3[vertexCount] : null;
        colors = header.HasColor ? new Vector4[vertexCount] : null;

        for (int i = 0; i < vertexCount; i++)
        {
            int slotOffset = vertexBase + i * vertexStride;
            float x = BinaryPrimitives.ReadSingleLittleEndian(blobSpan.Slice(slotOffset + 0, 4));
            float y = BinaryPrimitives.ReadSingleLittleEndian(blobSpan.Slice(slotOffset + 4, 4));
            float z = BinaryPrimitives.ReadSingleLittleEndian(blobSpan.Slice(slotOffset + 8, 4));
            positions[i] = flipYZ ? new Vector3(x, -y, -z) : new Vector3(x, y, z);

            if (colors is not null)
            {
                byte r = blobSpan[slotOffset + colorOffset + 0];
                byte g = blobSpan[slotOffset + colorOffset + 1];
                byte b = blobSpan[slotOffset + colorOffset + 2];
                byte a = blobSpan[slotOffset + colorOffset + 3];
                colors[i] = new Vector4(r / 255f, g / 255f, b / 255f, a / 255f);
            }

            if (normals is not null)
            {
                float nx = BinaryPrimitives.ReadSingleLittleEndian(blobSpan.Slice(slotOffset + normalOffset + 0, 4));
                float ny = BinaryPrimitives.ReadSingleLittleEndian(blobSpan.Slice(slotOffset + normalOffset + 4, 4));
                float nz = BinaryPrimitives.ReadSingleLittleEndian(blobSpan.Slice(slotOffset + normalOffset + 8, 4));
                normals[i] = flipYZ ? new Vector3(nx, -ny, -nz) : new Vector3(nx, ny, nz);
            }
        }
    }

    private static int[] DecodeIndices(byte[] meshBlob, MeshHeader header)
    {
        int vertexCount = checked((int)header.VertexCount);
        int triangleCount = checked((int)header.TriangleCount);
        int vertexStride = header.VertexStrideBytes;
        int trianglesBase = MeshHeader.SizeBytes + vertexCount * vertexStride;
        ReadOnlySpan<byte> blobSpan = meshBlob;

        int[] indices = new int[triangleCount * 3];
        for (int i = 0; i < triangleCount * 3; i++)
        {
            uint idx = BinaryPrimitives.ReadUInt32LittleEndian(blobSpan.Slice(trianglesBase + i * 4, 4));
            indices[i] = checked((int)idx);
        }
        return indices;
    }
}
