using System.Buffers.Binary;
using System.Numerics;

using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Spatial;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Model.Spatial;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Spatial;

/// <summary>
/// Tests for <see cref="MeshFromOrganizedFunction"/> — the core promotion
/// of organized PointClouds to triangle meshes. Verifies metadata, error
/// paths (unorganized → throw), triangulation correctness (flat-cloud →
/// full-grid topology, discontinuity → skipped cells), color inheritance,
/// per-vertex normal computation, coordinate-frame inheritance.
/// </summary>
public sealed class MeshFromOrganizedFunctionTests : ServiceTestBase
{
    [Fact]
    public void Metadata_ExposesNameCategoryAndDescription()
    {
        Assert.Equal("mesh_from_organized", MeshFromOrganizedFunction.Name);
        Assert.Equal(FunctionCategory.Image, MeshFromOrganizedFunction.Category);
        Assert.False(string.IsNullOrWhiteSpace(MeshFromOrganizedFunction.Description));
    }

    [Fact]
    public void Validate_AcceptsPointCloud_ReturnsMesh()
    {
        DataKind kind = new MeshFromOrganizedFunction()
            .ValidateArguments([DataKind.PointCloud]);
        Assert.Equal(DataKind.Mesh, kind);
    }

    [Fact]
    public async Task Execute_NullInput_ReturnsNullMesh()
    {
        ValueRef result = await new MeshFromOrganizedFunction().ExecuteAsync(
            new[] { ValueRef.Null(DataKind.PointCloud) }, CreateEvaluationFrame(), default);

        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Mesh, result.Kind);
    }

    [Fact]
    public async Task Execute_UnorganizedCloud_Throws()
    {
        ValueRef pc = BuildUnorganizedCloud(pointCount: 8);

        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new MeshFromOrganizedFunction().ExecuteAsync(
                new[] { pc }, CreateEvaluationFrame(), default));
        Assert.Contains("organized", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_FlatOrganizedCloud_ProducesFullGridTriangulation()
    {
        // 4×4 flat cloud (all Z = -0.5) → no discontinuities → every cell emits
        // 2 triangles → total = 2 × 3 × 3 = 18 triangles for a 4×4 grid.
        ValueRef pc = BuildOrganizedColoredCloud(width: 4, height: 4, flatZ: -0.5f);

        ValueRef result = await new MeshFromOrganizedFunction().ExecuteAsync(
            new[] { pc }, CreateEvaluationFrame(), default);

        byte[] meshBlob = result.AsMesh();
        MeshHeader header = MeshHeader.Read(meshBlob);

        Assert.Equal(16u, header.VertexCount);
        Assert.Equal(2u * 3 * 3, header.TriangleCount);
        Assert.Equal(MeshFlags.HasColor | MeshFlags.HasNormals, header.Flags);
        Assert.Equal(PointCloudCoordinateFrame.CameraOpenGl, header.CoordinateFrame);
    }

    [Fact]
    public async Task Execute_CloudWithDiscontinuity_SkipsDiscontinuousCells()
    {
        // 4×4 cloud where one corner is far recessed in Z → cells touching that
        // corner exceed the 5% bbox-range threshold and get skipped.
        ValueRef pc = BuildCloudWithCornerSpike(width: 4, height: 4);

        ValueRef result = await new MeshFromOrganizedFunction().ExecuteAsync(
            new[] { pc }, CreateEvaluationFrame(), default);

        MeshHeader header = MeshHeader.Read(result.AsMesh());
        // Full triangulation would be 2 × 3 × 3 = 18 triangles. The corner spike
        // touches up to 3 cells (corner cell + 2 edge cells) → at least 1 cell
        // skipped → at most 16 triangles.
        Assert.True(header.TriangleCount < 18u,
            $"expected discontinuity to skip at least one cell; got {header.TriangleCount} triangles");
        Assert.True(header.TriangleCount > 0u, "expected at least some triangles to be emitted");
    }

    [Fact]
    public async Task Execute_InheritsCoordinateFrameFromSource()
    {
        ValueRef pc = BuildOrganizedFlatCloudWithFrame(
            width: 3, height: 3, flatZ: -0.5f,
            frame: PointCloudCoordinateFrame.CameraOpenCv);

        ValueRef result = await new MeshFromOrganizedFunction().ExecuteAsync(
            new[] { pc }, CreateEvaluationFrame(), default);

        MeshHeader header = MeshHeader.Read(result.AsMesh());
        Assert.Equal(PointCloudCoordinateFrame.CameraOpenCv, header.CoordinateFrame);
    }

    [Fact]
    public async Task Execute_PerVertexColor_PreservedFromSourceCloud()
    {
        ValueRef pc = BuildOrganizedColoredCloud(width: 4, height: 4, flatZ: -0.5f);

        ValueRef result = await new MeshFromOrganizedFunction().ExecuteAsync(
            new[] { pc }, CreateEvaluationFrame(), default);

        byte[] meshBlob = result.AsMesh();
        MeshHeader header = MeshHeader.Read(meshBlob);
        Assert.True(header.HasColor);

        // Spot-check: the first vertex should match the first point's color
        // (R=0, G=0, B=100, A=255 per the builder pattern).
        (byte r, byte g, byte b, byte a) = ReadVertexColor(meshBlob, header, vertexIndex: 0);
        Assert.Equal(0, r);
        Assert.Equal(0, g);
        Assert.Equal(100, b);
        Assert.Equal(255, a);
    }

    [Fact]
    public async Task Execute_PerVertexNormals_ComputedAndUnitLength()
    {
        ValueRef pc = BuildOrganizedColoredCloud(width: 4, height: 4, flatZ: -0.5f);

        ValueRef result = await new MeshFromOrganizedFunction().ExecuteAsync(
            new[] { pc }, CreateEvaluationFrame(), default);

        byte[] meshBlob = result.AsMesh();
        MeshHeader header = MeshHeader.Read(meshBlob);
        Assert.True(header.HasNormals);

        // Flat cloud in XY plane (all Z constant) → every triangle's face
        // normal points along ±Z → every vertex normal collapses to that
        // direction and is unit length. Sample an interior vertex (one
        // surrounded by triangles).
        Vector3 n = ReadVertexNormal(meshBlob, header, vertexIndex: 1 * 4 + 1); // (1,1) interior
        Assert.True(MathF.Abs(n.LengthSquared() - 1f) < 1e-3f,
            $"expected unit-length normal, got length²={n.LengthSquared()}");
        Assert.True(MathF.Abs(n.Z) > 0.99f,
            $"expected near-pure-Z normal for flat XY cloud, got {n}");
    }

    // ─────────────────────── Cloud builders ───────────────────────

    /// <summary>
    /// Hand-build an organized colored cloud where every point has the
    /// specified Z and a per-pixel color (R/G ramp by position).
    /// </summary>
    private static ValueRef BuildOrganizedColoredCloud(int width, int height, float flatZ)
        => BuildOrganizedFlatCloudWithFrame(width, height, flatZ, PointCloudCoordinateFrame.CameraOpenGl);

    private static ValueRef BuildOrganizedFlatCloudWithFrame(
        int width, int height, float flatZ, PointCloudCoordinateFrame frame)
    {
        int pointCount = width * height;
        PointCloudHeader header = new(
            Version: PointCloudHeader.CurrentVersion,
            Flags: PointCloudFlags.HasColor,
            CoordinateFrame: frame,
            PointCount: (uint)pointCount,
            BboxMin: new Vector3(0, 0, flatZ),
            BboxMax: new Vector3(width - 1, height - 1, flatZ),
            Width: (uint)width,
            Height: (uint)height);

        byte[] blob = new byte[header.TotalSizeBytes];
        Span<byte> span = blob;
        header.Write(span[..PointCloudHeader.SizeBytes]);

        int offset = PointCloudHeader.SizeBytes;
        for (int v = 0; v < height; v++)
        {
            for (int u = 0; u < width; u++)
            {
                WritePoint(span.Slice(offset, 16),
                    new Vector3(u, v, flatZ),
                    (byte)(u * 30), (byte)(v * 30), 100, 255);
                offset += 16;
            }
        }
        return ValueRef.FromPointCloud(blob);
    }

    /// <summary>
    /// Hand-build a 4×4 organized cloud with one corner pixel pushed far in Z
    /// — large enough to exceed the 5% discontinuity threshold and cause
    /// cells touching that corner to be skipped during triangulation.
    /// </summary>
    private static ValueRef BuildCloudWithCornerSpike(int width, int height)
    {
        int pointCount = width * height;
        PointCloudHeader header = new(
            Version: PointCloudHeader.CurrentVersion,
            Flags: PointCloudFlags.HasColor,
            CoordinateFrame: PointCloudCoordinateFrame.CameraOpenGl,
            PointCount: (uint)pointCount,
            BboxMin: new Vector3(0, 0, -1.0f),
            BboxMax: new Vector3(width - 1, height - 1, 0f),
            Width: (uint)width,
            Height: (uint)height);

        byte[] blob = new byte[header.TotalSizeBytes];
        Span<byte> span = blob;
        header.Write(span[..PointCloudHeader.SizeBytes]);

        int offset = PointCloudHeader.SizeBytes;
        for (int v = 0; v < height; v++)
        {
            for (int u = 0; u < width; u++)
            {
                // All points at Z=0 except the bottom-right corner at Z=-1
                // (full bbox depth). Cells touching that corner will see a
                // Z span of 1.0, which is way over 5% of the bbox Z range.
                float z = (u == width - 1 && v == height - 1) ? -1f : 0f;
                WritePoint(span.Slice(offset, 16), new Vector3(u, v, z), 200, 200, 200, 255);
                offset += 16;
            }
        }
        return ValueRef.FromPointCloud(blob);
    }

    /// <summary>
    /// Hand-build an unorganized position-only cloud (width=height=0).
    /// </summary>
    private static ValueRef BuildUnorganizedCloud(uint pointCount)
    {
        PointCloudHeader header = new(
            Version: PointCloudHeader.CurrentVersion,
            Flags: PointCloudFlags.None,
            CoordinateFrame: PointCloudCoordinateFrame.Unspecified,
            PointCount: pointCount,
            BboxMin: new Vector3(0, 0, 0),
            BboxMax: new Vector3(1, 1, 1),
            Width: 0,
            Height: 0);
        byte[] blob = new byte[header.TotalSizeBytes];
        Span<byte> span = blob;
        header.Write(span[..PointCloudHeader.SizeBytes]);

        int offset = PointCloudHeader.SizeBytes;
        for (uint i = 0; i < pointCount; i++)
        {
            float v = i / (float)Math.Max(1, pointCount);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 0, 4), v);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 4, 4), v);
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 8, 4), v);
            offset += PointCloudHeader.PositionStrideBytes;
        }
        return ValueRef.FromPointCloud(blob);
    }

    private static void WritePoint(Span<byte> dst, Vector3 pos, byte r, byte g, byte b, byte a)
    {
        BinaryPrimitives.WriteSingleLittleEndian(dst[0..4], pos.X);
        BinaryPrimitives.WriteSingleLittleEndian(dst[4..8], pos.Y);
        BinaryPrimitives.WriteSingleLittleEndian(dst[8..12], pos.Z);
        dst[12] = r;
        dst[13] = g;
        dst[14] = b;
        dst[15] = a;
    }

    private static (byte r, byte g, byte b, byte a) ReadVertexColor(
        byte[] meshBlob, MeshHeader header, int vertexIndex)
    {
        ReadOnlySpan<byte> span = meshBlob;
        int offset = MeshHeader.SizeBytes
            + vertexIndex * header.VertexStrideBytes
            + MeshHeader.PositionStrideBytes;  // color follows position
        return (span[offset + 0], span[offset + 1], span[offset + 2], span[offset + 3]);
    }

    private static Vector3 ReadVertexNormal(byte[] meshBlob, MeshHeader header, int vertexIndex)
    {
        ReadOnlySpan<byte> span = meshBlob;
        int offset = MeshHeader.SizeBytes
            + vertexIndex * header.VertexStrideBytes
            + MeshHeader.PositionStrideBytes
            + (header.HasColor ? MeshHeader.ColorStrideBytes : 0);  // normal follows pos+color
        return new Vector3(
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(offset + 0, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(offset + 4, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(offset + 8, 4)));
    }
}
