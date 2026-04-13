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
/// Tests for <see cref="MeshSwapAxesFunction"/>. Covers metadata, the
/// canonical TripoSR-frame → Three.js-frame rotation, identity, reflections
/// (winding must flip), and argument validation. Builds tiny hand-rolled
/// meshes so the per-vertex math is checkable to the byte.
/// </summary>
public sealed class MeshSwapAxesFunctionTests : ServiceTestBase
{
    [Fact]
    public void Metadata_ExposesNameCategoryAndDescription()
    {
        Assert.Equal("mesh_swap_axes", MeshSwapAxesFunction.Name);
        Assert.Equal(FunctionCategory.Image, MeshSwapAxesFunction.Category);
        Assert.False(string.IsNullOrWhiteSpace(MeshSwapAxesFunction.Description));
    }

    [Fact]
    public async Task Execute_Identity_LeavesPositionsAndIndicesUnchanged()
    {
        byte[] mesh = BuildPositionOnlyMesh(
            positions: [(1f, 2f, 3f), (4f, 5f, 6f), (7f, 8f, 9f)],
            indices: [(0u, 1u, 2u)]);

        ValueRef result = await Swap(mesh, [1, 2, 3]);
        byte[] outBlob = result.AsMesh();

        Assert.Equal(new Vector3(1f, 2f, 3f), ReadPosition(outBlob, 0));
        Assert.Equal(new Vector3(4f, 5f, 6f), ReadPosition(outBlob, 1));
        Assert.Equal(new Vector3(7f, 8f, 9f), ReadPosition(outBlob, 2));
        Assert.Equal((0u, 1u, 2u), ReadTriangle(outBlob, 0));
    }

    [Fact]
    public async Task Execute_TripoSrToThreeJs_CyclicPermutation()
    {
        // TripoSR frame: +X back, +Y right, +Z up
        // Three.js frame: +X right, +Y up, +Z toward viewer
        // Mapping: out.X = in.Y, out.Y = in.Z, out.Z = in.X  →  [2, 3, 1]
        byte[] mesh = BuildPositionOnlyMesh(
            positions: [(0f, 0f, 0f), (1f, 2f, 3f), (10f, 20f, 30f)],
            indices: [(0u, 1u, 2u)]);

        ValueRef result = await Swap(mesh, [2, 3, 1]);
        byte[] outBlob = result.AsMesh();

        Assert.Equal(new Vector3(0f, 0f, 0f), ReadPosition(outBlob, 0));
        Assert.Equal(new Vector3(2f, 3f, 1f), ReadPosition(outBlob, 1));  // (1,2,3) → (Y,Z,X)
        Assert.Equal(new Vector3(20f, 30f, 10f), ReadPosition(outBlob, 2));

        // Det = +1 (cyclic = even permutation, no flips); winding preserved.
        Assert.Equal((0u, 1u, 2u), ReadTriangle(outBlob, 0));

        // Bbox should be recomputed in the new frame.
        MeshHeader outHeader = MeshHeader.Read(outBlob);
        Assert.Equal(new Vector3(0f, 0f, 0f), outHeader.BboxMin);
        Assert.Equal(new Vector3(20f, 30f, 10f), outHeader.BboxMax);
    }

    [Fact]
    public async Task Execute_SingleAxisFlip_ReversesTriangleWinding()
    {
        // [1, 2, -3] flips Z. det = +1 * -1 = -1 → winding must reverse.
        byte[] mesh = BuildPositionOnlyMesh(
            positions: [(1f, 0f, 5f), (2f, 0f, 5f), (3f, 0f, 5f)],
            indices: [(0u, 1u, 2u)]);

        ValueRef result = await Swap(mesh, [1, 2, -3]);
        byte[] outBlob = result.AsMesh();

        Assert.Equal(new Vector3(1f, 0f, -5f), ReadPosition(outBlob, 0));
        Assert.Equal(new Vector3(2f, 0f, -5f), ReadPosition(outBlob, 1));
        Assert.Equal(new Vector3(3f, 0f, -5f), ReadPosition(outBlob, 2));
        // (0, 1, 2) → (2, 1, 0) after winding reversal
        Assert.Equal((2u, 1u, 0u), ReadTriangle(outBlob, 0));
    }

    [Fact]
    public async Task Execute_NullMesh_ReturnsNullMesh()
    {
        ValueRef result = await new MeshSwapAxesFunction().ExecuteAsync(
            new[]
            {
                ValueRef.Null(DataKind.Mesh),
                ValueRef.FromPrimitiveArray(new int[] { 1, 2, 3 }, DataKind.Int32),
            },
            CreateEvaluationFrame(), default);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Mesh, result.Kind);
    }

    [Fact]
    public async Task Execute_WrongLength_Throws()
    {
        byte[] mesh = BuildPositionOnlyMesh([(0f, 0f, 0f)], []);

        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await Swap(mesh, [1, 2]));
        Assert.Contains("length 3", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_DuplicateAxis_Throws()
    {
        byte[] mesh = BuildPositionOnlyMesh([(0f, 0f, 0f)], []);

        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await Swap(mesh, [1, 1, 3]));
        Assert.Contains("exactly once", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_ZeroEntry_Throws()
    {
        byte[] mesh = BuildPositionOnlyMesh([(0f, 0f, 0f)], []);

        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await Swap(mesh, [1, 0, 3]));
        Assert.Contains("±1, ±2, ±3", ex.Message);
    }

    // ─────────────────────── Helpers ───────────────────────

    private Task<ValueRef> Swap(byte[] mesh, int[] axes)
    {
        return new MeshSwapAxesFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromMesh(mesh),
                ValueRef.FromPrimitiveArray(axes, DataKind.Int32),
            },
            CreateEvaluationFrame(), default).AsTask();
    }

    /// <summary>
    /// Builds a position-only Mesh blob (no color, no normals) with the
    /// given vertex positions and triangle index triples. Used to keep the
    /// per-byte expectations in the rotation tests trivially checkable.
    /// </summary>
    private static byte[] BuildPositionOnlyMesh(
        (float X, float Y, float Z)[] positions,
        (uint A, uint B, uint C)[] indices)
    {
        int vertexCount = positions.Length;
        int triangleCount = indices.Length;
        int stride = MeshHeader.PositionStrideBytes;

        long size = MeshHeader.SizeBytes
            + (long)vertexCount * stride
            + (long)triangleCount * MeshHeader.IndexStrideBytes;
        byte[] blob = new byte[size];
        Span<byte> span = blob;

        Vector3 bboxMin = vertexCount == 0 ? Vector3.Zero : new(float.PositiveInfinity);
        Vector3 bboxMax = vertexCount == 0 ? Vector3.Zero : new(float.NegativeInfinity);
        for (int i = 0; i < vertexCount; i++)
        {
            Vector3 p = new(positions[i].X, positions[i].Y, positions[i].Z);
            bboxMin = Vector3.Min(bboxMin, p);
            bboxMax = Vector3.Max(bboxMax, p);
        }

        MeshHeader header = new(
            Version: MeshHeader.CurrentVersion,
            Flags: MeshFlags.None,
            CoordinateFrame: PointCloudCoordinateFrame.CameraOpenGl,
            VertexCount: (uint)vertexCount,
            TriangleCount: (uint)triangleCount,
            BboxMin: bboxMin,
            BboxMax: bboxMax,
            TextureOffset: 0,
            TextureLength: 0);
        header.Write(span[..MeshHeader.SizeBytes]);

        int vBase = MeshHeader.SizeBytes;
        for (int i = 0; i < vertexCount; i++)
        {
            int off = vBase + i * stride;
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(off + 0, 4), positions[i].X);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(off + 4, 4), positions[i].Y);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(off + 8, 4), positions[i].Z);
        }
        int tBase = vBase + vertexCount * stride;
        for (int i = 0; i < triangleCount; i++)
        {
            int off = tBase + i * MeshHeader.IndexStrideBytes;
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(off + 0, 4), indices[i].A);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(off + 4, 4), indices[i].B);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(off + 8, 4), indices[i].C);
        }
        return blob;
    }

    private static Vector3 ReadPosition(byte[] blob, int vertexIndex)
    {
        int off = MeshHeader.SizeBytes + vertexIndex * MeshHeader.PositionStrideBytes;
        return new Vector3(
            BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(off + 0, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(off + 4, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(off + 8, 4)));
    }

    private static (uint A, uint B, uint C) ReadTriangle(byte[] blob, int triangleIndex)
    {
        MeshHeader h = MeshHeader.Read(blob);
        int tBase = MeshHeader.SizeBytes + (int)h.VertexCount * h.VertexStrideBytes;
        int off = tBase + triangleIndex * MeshHeader.IndexStrideBytes;
        return (
            BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off + 0, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off + 4, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off + 8, 4)));
    }
}
