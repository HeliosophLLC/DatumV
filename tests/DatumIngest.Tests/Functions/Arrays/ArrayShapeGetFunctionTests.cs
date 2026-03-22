using DatumIngest.Catalog;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Arrays;

/// <summary>
/// End-to-end SQL tests for <c>array_shape</c> and <c>array_get</c> against
/// multi-dim columns. Until the <c>arr[y, x]</c> bracket syntax lands, these
/// functions are the SQL-visible surface for multi-dim arrays.
/// </summary>
public sealed class ArrayShapeGetFunctionTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_arrfn_{Guid.NewGuid():N}");
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

    // ───────────────────── array_shape ─────────────────────

    [Fact]
    public async Task ArrayShape_MultiDim_ReturnsDims()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (m Array<Float32>(2,3))");
        catalog.Plan("INSERT INTO t VALUES ([1.0, 2.0, 3.0, 4.0, 5.0, 6.0])");

        using Arena arena = new();
        arena.AddReference();   // baseline keeps the arena alive across mid-query batch returns
        List<Row> rows = await ExecuteQueryAsync("SELECT array_shape(m) AS s FROM t", catalog, store: arena);

        DataValue shape = rows[0]["s"];
        Assert.True(shape.IsArray);
        Assert.Equal(DataKind.Int32, shape.Kind);
        Assert.Equal([2, 3], shape.AsArraySpan<int>(arena, catalog.SidecarRegistry).ToArray());
    }

    [Fact]
    public async Task ArrayShape_ThreeDim_ReturnsAllDims()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (cube Array<Int32>(2,2,2))");
        catalog.Plan("INSERT INTO t VALUES ([0, 1, 2, 3, 4, 5, 6, 7])");

        using Arena arena = new();
        arena.AddReference();   // baseline keeps the arena alive across mid-query batch returns
        List<Row> rows = await ExecuteQueryAsync("SELECT array_shape(cube) AS s FROM t", catalog, store: arena);

        Assert.Equal([2, 2, 2],
            rows[0]["s"].AsArraySpan<int>(arena, catalog.SidecarRegistry).ToArray());
    }

    [Fact]
    public async Task ArrayShape_FlatArray_ReturnsLength()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Float32>(4))");
        catalog.Plan("INSERT INTO t VALUES ([10.0, 20.0, 30.0, 40.0])");

        using Arena arena = new();
        arena.AddReference();   // baseline keeps the arena alive across mid-query batch returns
        List<Row> rows = await ExecuteQueryAsync("SELECT array_shape(v) AS s FROM t", catalog, store: arena);

        Assert.Equal([4],
            rows[0]["s"].AsArraySpan<int>(arena, catalog.SidecarRegistry).ToArray());
    }

    // ───────────────────── array_get ─────────────────────

    [Fact]
    public async Task ArrayGet_MultiDim_ReadsScalarElement()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (m Array<Float32>(2,3))");
        // Row-major layout: m = [[1, 2, 3], [4, 5, 6]]
        catalog.Plan("INSERT INTO t VALUES ([1.0, 2.0, 3.0, 4.0, 5.0, 6.0])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_get(m, 0, 0) AS a, " +
            "       array_get(m, 0, 2) AS b, " +
            "       array_get(m, 1, 0) AS c, " +
            "       array_get(m, 1, 2) AS d " +
            "FROM t",
            catalog);

        Assert.Equal(1f, rows[0]["a"].AsFloat32());
        Assert.Equal(3f, rows[0]["b"].AsFloat32());
        Assert.Equal(4f, rows[0]["c"].AsFloat32());
        Assert.Equal(6f, rows[0]["d"].AsFloat32());
    }

    [Fact]
    public async Task ArrayGet_ThreeDim_ReadsScalarElement()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (cube Array<Int32>(2,2,2))");
        catalog.Plan("INSERT INTO t VALUES ([0, 1, 2, 3, 4, 5, 6, 7])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_get(cube, 0, 0, 0) AS a, " +
            "       array_get(cube, 1, 1, 1) AS b, " +
            "       array_get(cube, 0, 1, 0) AS c " +
            "FROM t",
            catalog);

        Assert.Equal(0, rows[0]["a"].AsInt32());
        Assert.Equal(7, rows[0]["b"].AsInt32());
        Assert.Equal(2, rows[0]["c"].AsInt32());
    }

    [Fact]
    public async Task ArrayGet_FlatArray_ReadsScalarElement()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Float32>(4))");
        catalog.Plan("INSERT INTO t VALUES ([10.0, 20.0, 30.0, 40.0])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_get(v, 0) AS a, array_get(v, 3) AS b FROM t",
            catalog);

        Assert.Equal(10f, rows[0]["a"].AsFloat32());
        Assert.Equal(40f, rows[0]["b"].AsFloat32());
    }

    [Fact]
    public async Task ArrayGet_OutOfRange_ReturnsNull()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Float32>(4))");
        catalog.Plan("INSERT INTO t VALUES ([10.0, 20.0, 30.0, 40.0])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_get(v, 99) AS oob FROM t",
            catalog);

        Assert.True(rows[0]["oob"].IsNull);
    }

    [Fact]
    public async Task ArrayGet_WrongNdim_Throws()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (m Array<Float32>(2,3))");
        catalog.Plan("INSERT INTO t VALUES ([1.0, 2.0, 3.0, 4.0, 5.0, 6.0])");

        // 2-D array, only 1 index supplied → expected to throw at execution.
        await Assert.ThrowsAsync<DatumIngest.Execution.ExpressionEvaluationException>(
            () => ExecuteQueryAsync("SELECT array_get(m, 0) FROM t", catalog));
    }

    [Fact]
    public async Task ArrayGet_MultiDimIndexOutOfRange_Throws()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (m Array<Float32>(2,3))");
        catalog.Plan("INSERT INTO t VALUES ([1.0, 2.0, 3.0, 4.0, 5.0, 6.0])");

        // Second dim is 3 (valid indices 0..2); 5 is out of range.
        await Assert.ThrowsAsync<DatumIngest.Execution.ExpressionEvaluationException>(
            () => ExecuteQueryAsync("SELECT array_get(m, 0, 5) FROM t", catalog));
    }
}
