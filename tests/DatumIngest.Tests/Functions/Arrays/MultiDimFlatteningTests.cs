using DatumIngest.Catalog;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Arrays;

/// <summary>
/// Documents the shape-agnostic contract for array functions that consume
/// multi-dim arrays by silently flattening: the output is always a flat 1-D
/// array of the same total element count. These tests pin that behavior so
/// it doesn't drift — Florence-2 and similar model bodies rely on it.
/// </summary>
public sealed class MultiDimFlatteningTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_flat_{Guid.NewGuid():N}");
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

    // ───────────────────── array_concat over multi-dim ─────────────────────

    [Fact]
    public async Task ArrayConcat_TwoMultiDim_FlattensToOneDim()
    {
        // Both inputs are 2×2 matrices; concat produces a flat 8-element array.
        // The Florence-2 stitching pattern (visual_features || prompt_embeds)
        // depends on this behavior.
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (a Array<Float32>(2,2), b Array<Float32>(2,2))");
        catalog.Plan(
            "INSERT INTO t VALUES ([1.0, 2.0, 3.0, 4.0], [10.0, 20.0, 30.0, 40.0])");

        // Probe the flat result via cardinality and a couple of bracket reads.
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT cardinality(array_concat(a, b)) AS total," +
            "       array_ndims(array_concat(a, b))  AS nd," +
            "       array_get(array_concat(a, b), 1) AS first," +
            "       array_get(array_concat(a, b), 8) AS last " +
            "FROM t", catalog);

        Assert.Equal(8, rows[0]["total"].AsInt32());  // 4 + 4 flat
        Assert.Equal(1, rows[0]["nd"].AsInt32());     // flat 1-D, no shape
        Assert.Equal(1f, rows[0]["first"].AsFloat32());
        Assert.Equal(40f, rows[0]["last"].AsFloat32());
    }

    [Fact]
    public async Task ArrayConcat_MultiDimWithFlat_FlattensBothSides()
    {
        // Mixed-rank concat — the multi-dim side flattens into the same
        // typed-array stream as the flat side.
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (m Array<Float32>(2,3), v Array<Float32>(2))");
        catalog.Plan(
            "INSERT INTO t VALUES ([1.0, 2.0, 3.0, 4.0, 5.0, 6.0], [99.0, 100.0])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT cardinality(array_concat(m, v))   AS total," +
            "       array_get(array_concat(m, v), 1)  AS first_from_m," +
            "       array_get(array_concat(m, v), 6)  AS last_from_m," +
            "       array_get(array_concat(m, v), 7)  AS first_from_v," +
            "       array_get(array_concat(m, v), 8)  AS last_from_v " +
            "FROM t", catalog);

        Assert.Equal(8, rows[0]["total"].AsInt32());
        Assert.Equal(1f, rows[0]["first_from_m"].AsFloat32());
        Assert.Equal(6f, rows[0]["last_from_m"].AsFloat32());
        Assert.Equal(99f, rows[0]["first_from_v"].AsFloat32());
        Assert.Equal(100f, rows[0]["last_from_v"].AsFloat32());
    }

    [Fact]
    public async Task Cardinality_OverConcat_EqualsSumOfInputCardinalities()
    {
        // Algebraic sanity: |a ⨁ b| = |a| + |b| regardless of input shape.
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (m Array<Float32>(2,3))");
        catalog.Plan("INSERT INTO t VALUES ([1.0, 2.0, 3.0, 4.0, 5.0, 6.0])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT cardinality(array_concat(m, m)) AS total," +
            "       cardinality(m) + cardinality(m) AS sum_of_parts " +
            "FROM t", catalog);

        Assert.Equal(rows[0]["sum_of_parts"].AsInt32(), rows[0]["total"].AsInt32());
        Assert.Equal(12, rows[0]["total"].AsInt32());
    }

    // ───────────────────── Element iteration is shape-blind by design ─────────────────────

    [Fact]
    public async Task MultiDim_FlatElementOrder_IsRowMajor()
    {
        // The flattening is row-major (matches FromArenaMultiDimArray's storage
        // order). Locks the convention so downstream uses know what to expect.
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (m Array<Int32>(2,3))");
        catalog.Plan("INSERT INTO t VALUES ([1, 2, 3, 4, 5, 6])");

        // m as a flat span via array_concat with an empty-equivalent (m itself)
        // and then read positions 0..5 to confirm row-major order.
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_get(m, 1, 1) AS r0c0," +
            "       array_get(m, 1, 2) AS r0c1," +
            "       array_get(m, 1, 3) AS r0c2," +
            "       array_get(m, 2, 1) AS r1c0," +
            "       array_get(m, 2, 2) AS r1c1," +
            "       array_get(m, 2, 3) AS r1c2 " +
            "FROM t", catalog);

        Assert.Equal(1, rows[0]["r0c0"].AsInt32());
        Assert.Equal(2, rows[0]["r0c1"].AsInt32());
        Assert.Equal(3, rows[0]["r0c2"].AsInt32());
        Assert.Equal(4, rows[0]["r1c0"].AsInt32());
        Assert.Equal(5, rows[0]["r1c1"].AsInt32());
        Assert.Equal(6, rows[0]["r1c2"].AsInt32());
    }
}
