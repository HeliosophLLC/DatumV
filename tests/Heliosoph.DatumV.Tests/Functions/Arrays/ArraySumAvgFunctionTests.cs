using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Arrays;

/// <summary>
/// End-to-end SQL tests for <c>array_sum</c> / <c>array_avg</c>. Mirrors the
/// element-kind coverage of <see cref="ArrayMinMaxFunctionTests"/> across the
/// numeric kinds, multi-dim, and the rejection path for non-numeric arrays.
/// </summary>
public sealed class ArraySumAvgFunctionTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_arrsumavg_{Guid.NewGuid():N}");
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
    public async Task ArraySum_Float32_ReturnsTotal()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Float32>(3))");
        catalog.Plan("INSERT INTO t VALUES ([1, 2, 3])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_sum(v) AS s, array_avg(v) AS a FROM t", catalog);

        Assert.Equal(6.0f, rows[0]["s"].AsFloat32());
        Assert.Equal(2.0f, rows[0]["a"].AsFloat32());
    }

    [Fact]
    public async Task ArraySum_UInt8_BytePayload()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<UInt8>(4))");
        catalog.Plan("INSERT INTO t VALUES ([10, 20, 30, 40])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_sum(v) AS s, array_avg(v) AS a FROM t", catalog);

        Assert.Equal(100.0f, rows[0]["s"].AsFloat32());
        Assert.Equal(25.0f, rows[0]["a"].AsFloat32());
    }

    [Fact]
    public async Task ArraySum_Int32_NegativeValues()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Int32>(4))");
        catalog.Plan("INSERT INTO t VALUES ([" +
            "cast(-3 as Int32), cast(7 as Int32), cast(-10 as Int32), cast(6 as Int32)])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_sum(v) AS s, array_avg(v) AS a FROM t", catalog);

        Assert.Equal(0.0f, rows[0]["s"].AsFloat32());
        Assert.Equal(0.0f, rows[0]["a"].AsFloat32());
    }

    [Fact]
    public async Task ArraySum_Float64_PreservesPrecisionThenNarrowsToFloat32()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Float64>(3))");
        catalog.Plan("INSERT INTO t VALUES ([1, 2, 4])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_sum(v) AS s FROM t", catalog);

        Assert.Equal(7.0f, rows[0]["s"].AsFloat32());
    }

    [Fact]
    public async Task ArraySum_MultiDim_ReducesAcrossWholeTensor()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (m Array<Float32>(2, 3))");
        catalog.Plan("INSERT INTO t VALUES ([1, 2, 3, 4, 5, 6])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_sum(m) AS s, array_avg(m) AS a FROM t", catalog);

        Assert.Equal(21.0f, rows[0]["s"].AsFloat32());
        Assert.Equal(3.5f, rows[0]["a"].AsFloat32());
    }

    [Fact]
    public async Task ArraySum_SingleElement_ReturnsThatElement()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Float32>(1))");
        catalog.Plan("INSERT INTO t VALUES ([cast(42 as Float32)])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_sum(v) AS s, array_avg(v) AS a FROM t", catalog);

        Assert.Equal(42.0f, rows[0]["s"].AsFloat32());
        Assert.Equal(42.0f, rows[0]["a"].AsFloat32());
    }

    [Fact]
    public async Task ArraySum_StringElements_Rejected()
    {
        // String arrays use a reference-carrier payload that array_sum does
        // not accept — the signature gates on the numeric family at plan time
        // and the FunctionArgumentException is surfaced through the call site
        // as an ExpressionEvaluationException.
        ExpressionEvaluationException ex = await Assert.ThrowsAsync<ExpressionEvaluationException>(
            () => ExecuteQueryAsync("SELECT array_sum(array('a', 'b'))", CreateCatalog()));
        Assert.IsType<FunctionArgumentException>(ex.InnerException);
    }
}
