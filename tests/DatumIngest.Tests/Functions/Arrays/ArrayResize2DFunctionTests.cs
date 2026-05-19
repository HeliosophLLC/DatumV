using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Arrays;

/// <summary>
/// End-to-end SQL tests for <c>array_resize_2d</c>. Covers the rank-2 and
/// rank-3-with-leading-1 input shapes, identity resize, and bilinear
/// behavior on a known field.
/// </summary>
public sealed class ArrayResize2DFunctionTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_resize2d_{Guid.NewGuid():N}");
    private string CatalogPath => Path.Combine(_tempDir, ".datum-catalog.json");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
        }
        return Task.CompletedTask;
    }

    private TableCatalog NewFileCatalog() => CreateCatalog(CatalogPath);

    [Fact]
    public async Task Resize2D_IdentityShape_PreservesValues()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (m Array<Float32>(2,2))");
        catalog.Plan("INSERT INTO t VALUES ([1.0, 2.0, 3.0, 4.0])");

        using Arena arena = CreateArena();
        arena.AddReference();
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_resize_2d(m, 2, 2) AS r FROM t", catalog, store: arena);

        DataValue r = rows[0]["r"];
        Assert.True(r.IsMultiDim);
        Assert.Equal([2, 2], r.GetShape(arena, catalog.SidecarRegistry).ToArray());
        Assert.Equal(
            [1.0f, 2.0f, 3.0f, 4.0f],
            r.AsArraySpan<float>(arena, catalog.SidecarRegistry).ToArray());
    }

    [Fact]
    public async Task Resize2D_UpsampleConstantField_StaysConstant()
    {
        // Uniform input → uniform output, regardless of resample geometry.
        // Tightest sanity check on the bilinear math: any deviation would
        // surface as drift in the constant.
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (m Array<Float32>(2,2))");
        catalog.Plan("INSERT INTO t VALUES ([7.5, 7.5, 7.5, 7.5])");

        using Arena arena = CreateArena();
        arena.AddReference();
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_resize_2d(m, 8, 8) AS r FROM t", catalog, store: arena);

        DataValue r = rows[0]["r"];
        Assert.Equal([8, 8], r.GetShape(arena, catalog.SidecarRegistry).ToArray());
        ReadOnlySpan<float> values = r.AsArraySpan<float>(arena, catalog.SidecarRegistry);
        Assert.Equal(64, values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            Assert.Equal(7.5f, values[i]);
        }
    }

    [Fact]
    public async Task Resize2D_AcceptsRank3LeadingOne_AutoSqueezes()
    {
        // (1, h, w) — the shape ONNX depth / segmentation models typically
        // emit. The function should auto-squeeze the leading batch dim.
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (m Array<Float32>(1,2,2))");
        catalog.Plan("INSERT INTO t VALUES ([1.0, 2.0, 3.0, 4.0])");

        using Arena arena = CreateArena();
        arena.AddReference();
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_resize_2d(m, 4, 4) AS r FROM t", catalog, store: arena);

        DataValue r = rows[0]["r"];
        Assert.Equal([4, 4], r.GetShape(arena, catalog.SidecarRegistry).ToArray());
    }

    [Fact]
    public async Task Resize2D_RejectsBadRank()
    {
        // Rank 3 with leading dim ≠ 1 isn't a (1, h, w) batch — refuse rather
        // than silently picking a slice.
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (m Array<Float32>(2,2,2))");
        catalog.Plan("INSERT INTO t VALUES ([0.0, 1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0])");

        Exception ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using Arena arena = CreateArena();
            arena.AddReference();
            _ = await ExecuteQueryAsync(
                "SELECT array_resize_2d(m, 4, 4) AS r FROM t", catalog, store: arena);
        });
        Assert.Contains("2-D", ex.Message);
    }
}
