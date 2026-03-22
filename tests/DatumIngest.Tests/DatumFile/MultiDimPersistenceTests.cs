using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Tests.DatumFile;

/// <summary>
/// PR3: round-trip persistence tests for multi-dim arrays. Verifies that an
/// INSERT into a fixed-shape (ndim ≥ 2) column flows through the
/// <c>VariableSlotPageEncoderV2</c>/<c>VariableSlotPageDecoderV2</c> pair
/// preserving both the declared shape and the element bytes. PR2's
/// <c>LiteralCoercion.EnforceFixedShape</c> always materializes multi-dim
/// values into the arena, so the on-disk representation is sidecar-backed —
/// the encoder's inline path for multi-dim values is exercised by direct
/// <c>DataValue</c> construction tests (PR1).
/// </summary>
public sealed class MultiDimPersistenceTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_multidim_{Guid.NewGuid():N}");
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

    // ───────────────────── Small payload (still routed to sidecar) ─────────────────────

    [Fact]
    public async Task Small_Int16_2x2_RoundTripsShapeAndElements()
    {
        // Even though 4 Int16s + a 2-dim shape prefix fit in 16 bytes, the
        // arena-materialization in LiteralCoercion lands the value in arena →
        // encoder routes it to the sidecar pointer slot. Shape and elements
        // must still round-trip intact.
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (m Array<Int16>(2,2))");
        catalog.Plan("INSERT INTO t VALUES ([10, 20, 30, 40])");

        List<Row> rows = await ExecuteQueryAsync("SELECT m FROM t", catalog);

        Assert.Single(rows);
        DataValue value = rows[0]["m"];
        Assert.True(value.IsArray);
        Assert.True(value.IsMultiDim);
        Assert.Equal(2, value.Ndim);
        Assert.Equal([2, 2], value.GetShape(registry: catalog.SidecarRegistry).ToArray());
        Assert.Equal([(short)10, 20, 30, 40],
            value.AsArraySpan<short>(registry: catalog.SidecarRegistry).ToArray());
    }

    // ───────────────────── Sidecar path (large payload) ─────────────────────

    [Fact]
    public async Task Sidecar_Float32_4x4_RoundTripsShapeAndElements()
    {
        // 2 dims × 4 = 8 shape bytes + 16 × 4 = 64 element bytes = 72 total → sidecar.
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (m Array<Float32>(4,4))");
        catalog.Plan("INSERT INTO t VALUES ([1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, " +
                                            "9.0, 10.0, 11.0, 12.0, 13.0, 14.0, 15.0, 16.0])");

        List<Row> rows = await ExecuteQueryAsync("SELECT m FROM t", catalog);

        Assert.Single(rows);
        DataValue value = rows[0]["m"];
        Assert.True(value.IsArray);
        Assert.True(value.IsMultiDim);
        Assert.Equal(2, value.Ndim);
        Assert.Equal([4, 4], value.GetShape(registry: catalog.SidecarRegistry).ToArray());

        ReadOnlySpan<float> elements = value.AsArraySpan<float>(registry: catalog.SidecarRegistry);
        Assert.Equal(16, elements.Length);
        for (int i = 0; i < 16; i++) Assert.Equal((float)(i + 1), elements[i]);
    }

    // ───────────────────── 3-D ─────────────────────

    [Fact]
    public async Task Sidecar_Int32_2x2x2_ThreeDimensional()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (cube Array<Int32>(2,2,2))");
        catalog.Plan("INSERT INTO t VALUES ([0, 1, 2, 3, 4, 5, 6, 7])");

        List<Row> rows = await ExecuteQueryAsync("SELECT cube FROM t", catalog);

        DataValue value = rows[0]["cube"];
        Assert.True(value.IsMultiDim);
        Assert.Equal(3, value.Ndim);
        Assert.Equal([2, 2, 2], value.GetShape(registry: catalog.SidecarRegistry).ToArray());
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7],
            value.AsArraySpan<int>(registry: catalog.SidecarRegistry).ToArray());
    }

    // ───────────────────── Multiple rows ─────────────────────

    [Fact]
    public async Task MultipleRows_PreserveShapeAndElements()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (m Array<Float32>(2,3))");
        catalog.Plan("INSERT INTO t VALUES ([1.0, 2.0, 3.0, 4.0, 5.0, 6.0])," +
                                          " ([7.0, 8.0, 9.0, 10.0, 11.0, 12.0])");

        List<Row> rows = await ExecuteQueryAsync("SELECT m FROM t", catalog);
        Assert.Equal(2, rows.Count);

        foreach (Row row in rows)
        {
            DataValue value = row["m"];
            Assert.True(value.IsMultiDim);
            Assert.Equal([2, 3], value.GetShape(registry: catalog.SidecarRegistry).ToArray());
        }
        Assert.Equal([1f, 2f, 3f, 4f, 5f, 6f],
            rows[0]["m"].AsArraySpan<float>(registry: catalog.SidecarRegistry).ToArray());
        Assert.Equal([7f, 8f, 9f, 10f, 11f, 12f],
            rows[1]["m"].AsArraySpan<float>(registry: catalog.SidecarRegistry).ToArray());
    }

    // ───────────────────── 1-D columns stay flat (no regression) ─────────────────────

    [Fact]
    public async Task OneDimColumn_StillFlat()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Float32>(4))");
        catalog.Plan("INSERT INTO t VALUES ([1.0, 2.0, 3.0, 4.0])");

        List<Row> rows = await ExecuteQueryAsync("SELECT v FROM t", catalog);

        DataValue value = rows[0]["v"];
        Assert.True(value.IsArray);
        Assert.False(value.IsMultiDim);
        Assert.Equal([1f, 2f, 3f, 4f],
            value.AsArraySpan<float>(registry: catalog.SidecarRegistry).ToArray());
    }
}
