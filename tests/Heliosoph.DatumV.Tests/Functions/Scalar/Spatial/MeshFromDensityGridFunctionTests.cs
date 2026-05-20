using System.Buffers.Binary;
using System.Numerics;

using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Spatial;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Model.Spatial;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Spatial;

/// <summary>
/// Tests for <see cref="MeshFromDensityGridFunction"/> — Marching Cubes
/// iso-surface extraction. Validates metadata, argument validation,
/// degenerate cases (all-inside / all-outside fields), and geometric
/// correctness against analytical density fields (sphere, two-spheres-union)
/// where the expected surface is known.
/// </summary>
public sealed class MeshFromDensityGridFunctionTests : ServiceTestBase
{
    [Fact]
    public void Metadata_ExposesNameCategoryAndDescription()
    {
        Assert.Equal("mesh_from_density_grid", MeshFromDensityGridFunction.Name);
        Assert.Equal(FunctionCategory.Image, MeshFromDensityGridFunction.Category);
        Assert.False(string.IsNullOrWhiteSpace(MeshFromDensityGridFunction.Description));
    }

    [Fact]
    public void Validate_AcceptsFloat32ArrayInt32Float32Float32_ReturnsMesh()
    {
        DataKind kind = new MeshFromDensityGridFunction()
            .ValidateArguments([DataKind.Float32, DataKind.Int32, DataKind.Float32, DataKind.Float32]);
        Assert.Equal(DataKind.Mesh, kind);
    }

    [Fact]
    public async Task Execute_NullDensity_ReturnsNullMesh()
    {
        ValueRef result = await new MeshFromDensityGridFunction().ExecuteAsync(
            new[]
            {
                ValueRef.Null(DataKind.Float32),
                ValueRef.FromInt32(32),
                ValueRef.FromFloat32(0f),
                ValueRef.FromFloat32(1f),
            },
            CreateEvaluationFrame(), default);

        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Mesh, result.Kind);
    }

    [Fact]
    public async Task Execute_ResolutionBelowTwo_Throws()
    {
        ValueRef density = ValueRef.FromPrimitiveArray(new float[1] { 0f }, DataKind.Float32);

        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new MeshFromDensityGridFunction().ExecuteAsync(
                new[]
                {
                    density,
                    ValueRef.FromInt32(1),
                    ValueRef.FromFloat32(0f),
                    ValueRef.FromFloat32(1f),
                },
                CreateEvaluationFrame(), default));
        Assert.Contains("resolution", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_DensityLengthMismatch_Throws()
    {
        // resolution=4 expects 4³ = 64 samples; pass 50 to trigger the error.
        ValueRef density = ValueRef.FromPrimitiveArray(new float[50], DataKind.Float32);

        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new MeshFromDensityGridFunction().ExecuteAsync(
                new[]
                {
                    density,
                    ValueRef.FromInt32(4),
                    ValueRef.FromFloat32(0f),
                    ValueRef.FromFloat32(1f),
                },
                CreateEvaluationFrame(), default));
        Assert.Contains("density array length", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_RadiusNonPositive_Throws()
    {
        ValueRef density = ValueRef.FromPrimitiveArray(new float[8], DataKind.Float32); // 2³

        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new MeshFromDensityGridFunction().ExecuteAsync(
                new[]
                {
                    density,
                    ValueRef.FromInt32(2),
                    ValueRef.FromFloat32(0f),
                    ValueRef.FromFloat32(-1f),
                },
                CreateEvaluationFrame(), default));
        Assert.Contains("radius", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_AllInsideField_ProducesZeroTriangles()
    {
        // density = +1 everywhere, isolevel = 0 → every corner > iso →
        // case index = 255 for every cube → no crossings → no triangles.
        int res = 8;
        float[] density = new float[res * res * res];
        Array.Fill(density, 1f);

        MeshHeader header = await ExtractHeader(density, res, isolevel: 0f, radius: 1f);
        Assert.Equal(0u, header.VertexCount);
        Assert.Equal(0u, header.TriangleCount);
    }

    [Fact]
    public async Task Execute_AllOutsideField_ProducesZeroTriangles()
    {
        // density = -1 everywhere, isolevel = 0 → every corner ≤ iso →
        // case index = 0 for every cube → no crossings → no triangles.
        int res = 8;
        float[] density = new float[res * res * res];
        Array.Fill(density, -1f);

        MeshHeader header = await ExtractHeader(density, res, isolevel: 0f, radius: 1f);
        Assert.Equal(0u, header.VertexCount);
        Assert.Equal(0u, header.TriangleCount);
    }

    [Fact]
    public async Task Execute_SphereField_ProducesApproximateSphereMesh()
    {
        // Sphere of radius 0.5: density(p) = 0.5² − ‖p‖²; iso = 0 →
        // surface is ‖p‖ = 0.5. Sample on a 33³ grid spanning [-1, +1]³ so
        // the sphere is well inside the bbox.
        const int res = 33;
        const float gridRadius = 1f;
        const float sphereRadius = 0.5f;
        float[] density = BuildSphereField(res, gridRadius, sphereRadius);

        (MeshHeader header, byte[] blob) = await ExtractMesh(density, res, isolevel: 0f, radius: gridRadius);

        // Sanity-check sizes -- a 33³ sphere mesh lands in the thousands of
        // triangles. Tight bands would tie the test to ε-sensitive table
        // outputs; just confirm it's non-trivial and within reason.
        Assert.InRange(header.TriangleCount, 1000u, 20000u);
        Assert.InRange(header.VertexCount, 500u, 10000u);

        // Every vertex should sit on the iso-surface at radius ≈ sphereRadius.
        // Allow ½ grid-cell tolerance: cell size = 2·gridRadius / (res-1).
        float cellSize = 2f * gridRadius / (res - 1);
        float tolerance = cellSize * 0.6f; // a touch above ½ cell for the worst-case interpolation drift
        ReadOnlySpan<byte> span = blob;
        int vertexBase = MeshHeader.SizeBytes;
        int stride = MeshHeader.PositionStrideBytes;
        for (uint v = 0; v < header.VertexCount; v++)
        {
            Vector3 p = new(
                BinaryPrimitives.ReadSingleLittleEndian(span.Slice(vertexBase + (int)v * stride + 0, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(span.Slice(vertexBase + (int)v * stride + 4, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(span.Slice(vertexBase + (int)v * stride + 8, 4)));
            float r = p.Length();
            Assert.True(MathF.Abs(r - sphereRadius) <= tolerance,
                $"vertex {v} at {p} has radius {r}; expected ≈{sphereRadius} (±{tolerance:F4}).");
        }
    }

    [Fact]
    public async Task Execute_SphereField_OutputsCameraOpenGlFrameAndNoFlags()
    {
        const int res = 17;
        float[] density = BuildSphereField(res, gridRadius: 1f, sphereRadius: 0.5f);

        MeshHeader header = await ExtractHeader(density, res, isolevel: 0f, radius: 1f);
        Assert.Equal(PointCloudCoordinateFrame.CameraOpenGl, header.CoordinateFrame);
        Assert.Equal(MeshFlags.None, header.Flags);
        Assert.False(header.HasColor);
        Assert.False(header.HasNormals);
    }

    [Fact]
    public async Task Execute_DeduplicatesEdgeVertices_VertexCountFarBelowTriangleVertexProduct()
    {
        // Closed iso-surfaces share most edges across adjacent cubes. A
        // correctly deduplicated mesh has ~½ as many vertices as triangles
        // (Euler ≈ V - E + F = 2 for a closed manifold). Without dedup,
        // every triangle would have 3 unique vertices → vertex count = 3 ×
        // triangle count. Assert we're closer to the manifold bound than
        // the naïve upper bound.
        const int res = 33;
        float[] density = BuildSphereField(res, gridRadius: 1f, sphereRadius: 0.5f);

        MeshHeader header = await ExtractHeader(density, res, isolevel: 0f, radius: 1f);
        Assert.True(header.VertexCount * 2u < (uint)(header.TriangleCount * 3u),
            $"expected dedup → vertices ≈ ½ triangle-vertex-count; got {header.VertexCount} vertices "
            + $"for {header.TriangleCount} triangles (naïve would be {header.TriangleCount * 3u}).");
    }

    [Fact]
    public async Task Execute_SphereMesh_BboxApproximatesSphereExtent()
    {
        const int res = 33;
        const float sphereRadius = 0.5f;
        float[] density = BuildSphereField(res, gridRadius: 1f, sphereRadius: sphereRadius);

        MeshHeader header = await ExtractHeader(density, res, isolevel: 0f, radius: 1f);

        // Bbox should sit inside [-sphereRadius, +sphereRadius]³ + one-cell
        // slack on each side. Same cell-size tolerance as the per-vertex
        // radius test.
        float cellSize = 2f * 1f / (res - 1);
        float tolerance = cellSize * 0.6f;
        Assert.InRange(header.BboxMin.X, -sphereRadius - tolerance, -sphereRadius + tolerance);
        Assert.InRange(header.BboxMax.X, sphereRadius - tolerance, sphereRadius + tolerance);
        Assert.InRange(header.BboxMin.Y, -sphereRadius - tolerance, -sphereRadius + tolerance);
        Assert.InRange(header.BboxMax.Y, sphereRadius - tolerance, sphereRadius + tolerance);
        Assert.InRange(header.BboxMin.Z, -sphereRadius - tolerance, -sphereRadius + tolerance);
        Assert.InRange(header.BboxMax.Z, sphereRadius - tolerance, sphereRadius + tolerance);
    }

    [Fact]
    public async Task Execute_ResultRoundTripsThroughMeshComputeNormals()
    {
        // The intended composition pattern: mesh_from_density_grid emits
        // position-only triangles, then mesh_compute_normals adds smooth
        // shading. Verify the two compose -- if the index list or vertex
        // layout were malformed, mesh_compute_normals would throw or emit
        // garbage normals.
        const int res = 17;
        float[] density = BuildSphereField(res, gridRadius: 1f, sphereRadius: 0.5f);

        ValueRef mesh = await new MeshFromDensityGridFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromPrimitiveArray(density, DataKind.Float32),
                ValueRef.FromInt32(res),
                ValueRef.FromFloat32(0f),
                ValueRef.FromFloat32(1f),
            },
            CreateEvaluationFrame(), default);

        ValueRef withNormals = await new MeshComputeNormalsFunction().ExecuteAsync(
            new[] { mesh }, CreateEvaluationFrame(), default);

        MeshHeader header = MeshHeader.Read(withNormals.AsMesh());
        Assert.True(header.HasNormals);
        Assert.Equal(MeshHeader.Read(mesh.AsMesh()).VertexCount, header.VertexCount);
        Assert.Equal(MeshHeader.Read(mesh.AsMesh()).TriangleCount, header.TriangleCount);
    }

    // ─────────────────────── Helpers ───────────────────────

    /// <summary>
    /// Builds the analytical density field <c>r² − ‖p‖²</c> for a sphere of
    /// radius <paramref name="sphereRadius"/> sampled on a <c>res³</c> grid
    /// that spans <c>[-gridRadius, +gridRadius]³</c>. The iso-surface at
    /// level 0 is exactly the sphere; density is positive inside the sphere
    /// and negative outside.
    /// </summary>
    private static float[] BuildSphereField(int res, float gridRadius, float sphereRadius)
    {
        float[] density = new float[res * res * res];
        float step = (2f * gridRadius) / (res - 1);
        float r2 = sphereRadius * sphereRadius;
        for (int k = 0; k < res; k++)
        {
            float pz = -gridRadius + step * k;
            for (int j = 0; j < res; j++)
            {
                float py = -gridRadius + step * j;
                for (int i = 0; i < res; i++)
                {
                    float px = -gridRadius + step * i;
                    density[i + j * res + k * res * res] = r2 - (px * px + py * py + pz * pz);
                }
            }
        }
        return density;
    }

    private async Task<MeshHeader> ExtractHeader(float[] density, int res, float isolevel, float radius)
    {
        ValueRef result = await new MeshFromDensityGridFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromPrimitiveArray(density, DataKind.Float32),
                ValueRef.FromInt32(res),
                ValueRef.FromFloat32(isolevel),
                ValueRef.FromFloat32(radius),
            },
            CreateEvaluationFrame(), default);
        return MeshHeader.Read(result.AsMesh());
    }

    private async Task<(MeshHeader header, byte[] blob)> ExtractMesh(
        float[] density, int res, float isolevel, float radius)
    {
        ValueRef result = await new MeshFromDensityGridFunction().ExecuteAsync(
            new[]
            {
                ValueRef.FromPrimitiveArray(density, DataKind.Float32),
                ValueRef.FromInt32(res),
                ValueRef.FromFloat32(isolevel),
                ValueRef.FromFloat32(radius),
            },
            CreateEvaluationFrame(), default);
        byte[] blob = result.AsMesh();
        return (MeshHeader.Read(blob), blob);
    }
}
