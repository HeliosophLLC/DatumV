using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Spatial;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Model.Spatial;

using SkiaSharp;

namespace DatumIngest.Tests.Functions.Scalar.Spatial;

/// <summary>
/// Tests for the shortcut depth-to-mesh constructors —
/// <see cref="MeshFromDepthOrthographicFunction"/> and
/// <see cref="MeshFromDepthPinholeFunction"/>. Verifies end-to-end pipeline
/// (color + depth → mesh) for both projection modes; the unprojection
/// math itself is covered by the PointCloud tests.
/// </summary>
public sealed class MeshFromDepthFunctionsTests : ServiceTestBase
{
    [Fact]
    public void OrthographicMetadata_Exposes()
    {
        Assert.Equal("mesh_from_depth_orthographic", MeshFromDepthOrthographicFunction.Name);
        Assert.Equal(FunctionCategory.Image, MeshFromDepthOrthographicFunction.Category);
        Assert.False(string.IsNullOrWhiteSpace(MeshFromDepthOrthographicFunction.Description));
    }

    [Fact]
    public void PinholeMetadata_Exposes()
    {
        Assert.Equal("mesh_from_depth_pinhole", MeshFromDepthPinholeFunction.Name);
    }

    [Theory]
    [MemberData(nameof(ProjectionVariants))]
    public void Validate_AcceptsImageImageFloat32_ReturnsMesh(IScalarFunction fn)
    {
        DataKind kind = fn.ValidateArguments([DataKind.Image, DataKind.Image, DataKind.Float32]);
        Assert.Equal(DataKind.Mesh, kind);
    }

    [Theory]
    [MemberData(nameof(ProjectionVariants))]
    public async Task Execute_NullColor_ReturnsNullMesh(IScalarFunction fn)
    {
        using SKBitmap depth = MakeConstantDepth(4, 4, intensity: 128);

        ValueRef result = await fn.ExecuteAsync(
            new ValueRef[] { ValueRef.Null(DataKind.Image), ValueRef.FromImage(depth), ValueRef.FromFloat32(60f) },
            CreateEvaluationFrame(), default);

        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Mesh, result.Kind);
    }

    [Theory]
    [MemberData(nameof(ProjectionVariants))]
    public async Task Execute_RealDepthMap_ProducesMeshWithMatchingVertexCount(IScalarFunction fn)
    {
        // 8×8 inputs → unprojected cloud has 64 points → mesh has 64 vertices
        // and (for a flat-depth cloud with no discontinuities) 2×7×7 = 98 triangles.
        const int w = 8;
        const int h = 8;
        using SKBitmap color = MakeSolidColor(w, h, new SKColor(200, 100, 50, 255));
        using SKBitmap depth = MakeConstantDepth(w, h, intensity: 128);

        ValueRef result = await fn.ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(color), ValueRef.FromImage(depth), ValueRef.FromFloat32(60f) },
            CreateEvaluationFrame(), default);

        MeshHeader header = MeshHeader.Read(result.AsMesh());
        Assert.Equal((uint)(w * h), header.VertexCount);
        // Flat depth → no discontinuities → full grid triangulation.
        Assert.Equal((uint)(2 * (w - 1) * (h - 1)), header.TriangleCount);
        Assert.True(header.HasColor);
        Assert.True(header.HasNormals);
        Assert.Equal(PointCloudCoordinateFrame.CameraOpenGl, header.CoordinateFrame);
    }

    [Theory]
    [MemberData(nameof(ProjectionVariants))]
    public async Task Execute_AcceptsIntegerFov(IScalarFunction fn)
    {
        // fov_deg matcher is NumericScalar — Int32 widens to Float32 without
        // an explicit cast.
        using SKBitmap color = MakeSolidColor(4, 4, new SKColor(0, 0, 0, 255));
        using SKBitmap depth = MakeConstantDepth(4, 4, intensity: 128);

        ValueRef result = await fn.ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(color), ValueRef.FromImage(depth), ValueRef.FromInt32(60) },
            CreateEvaluationFrame(),
            default);

        Assert.False(result.IsNull);
        Assert.Equal(DataKind.Mesh, result.Kind);
    }

    [Theory]
    [MemberData(nameof(ProjectionVariants))]
    public async Task Execute_AcceptsFloat32ArrayDepth(IScalarFunction fn)
    {
        // The second signature variant takes raw Float32[] depth (metric meters
        // path, paired with the *_meters depth-model variants). The mesh path
        // delegates to PointCloudFromDepthOps which already routes on depth
        // kind, so the mesh constructors inherit the variant for free — this
        // test verifies the signature actually accepts it.
        const int w = 4;
        const int h = 4;
        using SKBitmap color = MakeSolidColor(w, h, new SKColor(0, 0, 0, 255));

        // Build a (h, w) shape-aware Float32 depth array — matches what an
        // Array<Float32>(*, *) model output would produce. The metric-depth
        // path requires the 2-D shape so it can map flat indices back to
        // (row, col) for unprojection.
        float[] depthValues = new float[w * h];
        for (int i = 0; i < depthValues.Length; i++)
        {
            depthValues[i] = 0.5f;  // arbitrary metric depth, constant for simplicity
        }
        ValueRef depthArr = ValueRef.FromPrimitiveMultiDimArray(depthValues, [h, w], DataKind.Float32);

        ValueRef result = await fn.ExecuteAsync(
            new ValueRef[] { ValueRef.FromImage(color), depthArr, ValueRef.FromFloat32(60f) },
            CreateEvaluationFrame(),
            default);

        Assert.False(result.IsNull);
        Assert.Equal(DataKind.Mesh, result.Kind);
        MeshHeader header = MeshHeader.Read(result.AsMesh());
        Assert.Equal((uint)(w * h), header.VertexCount);
    }

    public static IEnumerable<object[]> ProjectionVariants() => new[]
    {
        new object[] { (IScalarFunction)new MeshFromDepthPinholeFunction() },
        new object[] { (IScalarFunction)new MeshFromDepthOrthographicFunction() },
    };

    // ─────────────────────── Helpers ───────────────────────

    private static SKBitmap MakeSolidColor(int width, int height, SKColor color)
    {
        SKBitmap bmp = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bmp.SetPixel(x, y, color);
            }
        }
        return bmp;
    }

    private static SKBitmap MakeConstantDepth(int width, int height, byte intensity)
    {
        SKBitmap bmp = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        SKColor px = new(intensity, intensity, intensity, 255);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bmp.SetPixel(x, y, px);
            }
        }
        return bmp;
    }
}
