using System.Buffers.Binary;
using System.Numerics;

using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Spatial;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Model.Spatial;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Functions.Scalar.Spatial;

/// <summary>
/// Tests for the 8 Mesh accessor scalars
/// (<see cref="MeshVertexCountFunction"/>, <see cref="MeshTriangleCountFunction"/>,
/// <see cref="MeshBboxMinFunction"/>, <see cref="MeshBboxMaxFunction"/>,
/// <see cref="MeshHasColorFunction"/>, <see cref="MeshHasNormalsFunction"/>,
/// <see cref="MeshHasUVsFunction"/>, <see cref="MeshHasTextureFunction"/>).
/// </summary>
public sealed class MeshAccessorFunctionsTests : ServiceTestBase
{
    // ─────────────────────── Accessors over a hand-built mesh ───────────────────────

    [Fact]
    public async Task VertexCount_ReturnsHeaderVertexCount()
    {
        ValueRef mesh = BuildSampleMesh();
        ValueRef result = await new MeshVertexCountFunction().ExecuteAsync(
            new[] { mesh }, MakeFrame(), default);
        Assert.Equal(DataKind.Int32, result.Kind);
        Assert.Equal(4, result.AsInt32());
    }

    [Fact]
    public async Task TriangleCount_ReturnsHeaderTriangleCount()
    {
        ValueRef mesh = BuildSampleMesh();
        ValueRef result = await new MeshTriangleCountFunction().ExecuteAsync(
            new[] { mesh }, MakeFrame(), default);
        Assert.Equal(2, result.AsInt32());
    }

    [Fact]
    public async Task BboxMin_AndBboxMax_ReturnHeaderCorners()
    {
        Vector3 min = new(-1.5f, -2.25f, -3.75f);
        Vector3 max = new(1.5f, 2.25f, 3.75f);
        ValueRef mesh = BuildMeshWithBbox(min, max);

        ValueRef minResult = await new MeshBboxMinFunction().ExecuteAsync(
            new[] { mesh }, MakeFrame(), default);
        ValueRef maxResult = await new MeshBboxMaxFunction().ExecuteAsync(
            new[] { mesh }, MakeFrame(), default);

        Assert.Equal(DataKind.Point3D, minResult.Kind);
        Assert.Equal(DataKind.Point3D, maxResult.Kind);
        Assert.Equal(min, minResult.AsPoint3D());
        Assert.Equal(max, maxResult.AsPoint3D());
    }

    [Fact]
    public async Task HasColor_TrueForColoredMesh()
    {
        ValueRef mesh = BuildSampleMesh();
        ValueRef result = await new MeshHasColorFunction().ExecuteAsync(
            new[] { mesh }, MakeFrame(), default);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public async Task HasNormals_TrueForMeshWithNormals()
    {
        ValueRef mesh = BuildSampleMesh();
        ValueRef result = await new MeshHasNormalsFunction().ExecuteAsync(
            new[] { mesh }, MakeFrame(), default);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public async Task HasUVs_FalseForPhase1Meshes()
    {
        // Phase 1 emits no UVs; the accessor exists for surface stability
        // when Phase 2 adds UV-carrying meshes.
        ValueRef mesh = BuildSampleMesh();
        ValueRef result = await new MeshHasUVsFunction().ExecuteAsync(
            new[] { mesh }, MakeFrame(), default);
        Assert.False(result.AsBoolean());
    }

    [Fact]
    public async Task HasTexture_FalseForPhase1Meshes()
    {
        ValueRef mesh = BuildSampleMesh();
        ValueRef result = await new MeshHasTextureFunction().ExecuteAsync(
            new[] { mesh }, MakeFrame(), default);
        Assert.False(result.AsBoolean());
    }

    // ─────────────────────── Null propagation ───────────────────────

    [Fact]
    public async Task VertexCount_NullInput_ReturnsNullInt32()
    {
        ValueRef result = await new MeshVertexCountFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.Mesh) }, MakeFrame(), default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Int32, result.Kind);
    }

    [Fact]
    public async Task BboxMin_NullInput_ReturnsNullPoint3D()
    {
        ValueRef result = await new MeshBboxMinFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.Mesh) }, MakeFrame(), default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Point3D, result.Kind);
    }

    [Fact]
    public async Task HasColor_NullInput_ReturnsNullBoolean()
    {
        ValueRef result = await new MeshHasColorFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.Mesh) }, MakeFrame(), default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Boolean, result.Kind);
    }

    // ─────────────────────── Mesh builders ───────────────────────

    /// <summary>
    /// Hand-built 4-vertex / 2-triangle colored quad with normals — exercises
    /// the full Phase 1 stride (pos + color + normal = 28 bytes per vertex).
    /// </summary>
    private static ValueRef BuildSampleMesh() =>
        BuildMeshWithBbox(new Vector3(0, 0, 0), new Vector3(1, 0, 1));

    private static ValueRef BuildMeshWithBbox(Vector3 bboxMin, Vector3 bboxMax)
    {
        MeshHeader header = new(
            Version: MeshHeader.CurrentVersion,
            Flags: MeshFlags.HasColor | MeshFlags.HasNormals,
            CoordinateFrame: PointCloudCoordinateFrame.CameraOpenGl,
            VertexCount: 4,
            TriangleCount: 2,
            BboxMin: bboxMin,
            BboxMax: bboxMax,
            TextureOffset: 0,
            TextureLength: 0);

        byte[] blob = new byte[header.TotalSizeBytes];
        Span<byte> span = blob;
        header.Write(span[..MeshHeader.SizeBytes]);

        Vector3[] positions =
        [
            bboxMin,
            new(bboxMax.X, bboxMin.Y, bboxMin.Z),
            new(bboxMin.X, bboxMin.Y, bboxMax.Z),
            bboxMax,
        ];
        const int vertexStride = 28;
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
            // Normal = (0, 1, 0) for all
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 16, 4), 0f);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 20, 4), 1f);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 24, 4), 0f);
        }

        int indicesBase = vertexBase + 4 * vertexStride;
        uint[] indices = [0, 2, 1, 1, 2, 3];
        for (int i = 0; i < 6; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(indicesBase + i * 4, 4), indices[i]);
        }
        return ValueRef.FromMesh(blob);
    }

    private EvaluationFrame MakeFrame()
    {
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        return new EvaluationFrame(Row.Empty, arena, arena, new MemoryAccountant(), types: new TypeRegistry());
    }
}
