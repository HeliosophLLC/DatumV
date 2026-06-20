using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Arrays;

/// <summary>
/// End-to-end SQL tests for <c>array_reverse</c>, <c>array_sort</c>, and
/// <c>array_distinct</c>. Each function covers a representative numeric,
/// Boolean, and String case plus the rejection paths (unsupported kinds).
/// Result arrays are read through the arena-aware
/// <see cref="DataValue.AsArraySpan{T}"/> overload, matching the
/// inspection style in <c>ArraySliceFunctionTests</c>.
/// </summary>
public sealed class ArrayReverseSortDistinctFunctionTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_arrrsd_{Guid.NewGuid():N}");
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

    // ---- array_reverse ----

    [Fact]
    public async Task ArrayReverse_Int32_PermutesElements()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Int32>)");
        catalog.Plan("INSERT INTO t VALUES ([" +
            "cast(1 as Int32), cast(2 as Int32), cast(3 as Int32), cast(4 as Int32)])");

        using Arena arena = CreateArena();
        arena.AddReference();
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_reverse(v) AS r FROM t", catalog, store: arena);

        Assert.Equal(new int[] { 4, 3, 2, 1 },
            rows[0]["r"].AsArraySpan<int>(arena, catalog.SidecarRegistry).ToArray());
    }

    [Fact]
    public async Task ArrayReverse_Float32_PreservesValues()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Float32>)");
        catalog.Plan("INSERT INTO t VALUES ([10, 20, 30])");

        using Arena arena = CreateArena();
        arena.AddReference();
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_reverse(v) AS r FROM t", catalog, store: arena);

        Assert.Equal(new float[] { 30f, 20f, 10f },
            rows[0]["r"].AsArraySpan<float>(arena, catalog.SidecarRegistry).ToArray());
    }

    [Fact]
    public async Task ArrayReverse_String_OrdersByPosition()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<String>)");
        catalog.Plan("INSERT INTO t VALUES (['alpha', 'beta', 'gamma'])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_to_string(array_reverse(v), ',') AS r FROM t", catalog);

        Assert.Equal("gamma,beta,alpha", rows[0]["r"].AsString());
    }

    // ---- array_sort ----

    [Fact]
    public async Task ArraySort_Int32_AscendingOrder()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Int32>)");
        catalog.Plan("INSERT INTO t VALUES ([" +
            "cast(3 as Int32), cast(1 as Int32), cast(4 as Int32), cast(1 as Int32), cast(5 as Int32)])");

        using Arena arena = CreateArena();
        arena.AddReference();
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_sort(v) AS r FROM t", catalog, store: arena);

        Assert.Equal(new int[] { 1, 1, 3, 4, 5 },
            rows[0]["r"].AsArraySpan<int>(arena, catalog.SidecarRegistry).ToArray());
    }

    [Fact]
    public async Task ArraySort_Float32_AscendingOrder()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Float32>)");
        catalog.Plan("INSERT INTO t VALUES ([7, 2, 9, 4])");

        using Arena arena = CreateArena();
        arena.AddReference();
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_sort(v) AS r FROM t", catalog, store: arena);

        Assert.Equal(new float[] { 2f, 4f, 7f, 9f },
            rows[0]["r"].AsArraySpan<float>(arena, catalog.SidecarRegistry).ToArray());
    }

    [Fact]
    public async Task ArraySort_Boolean_FalseBeforeTrue()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Boolean>)");
        catalog.Plan("INSERT INTO t VALUES ([true, false, true, false])");

        using Arena arena = CreateArena();
        arena.AddReference();
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_sort(v) AS r FROM t", catalog, store: arena);

        Assert.Equal(new byte[] { 0, 0, 1, 1 },
            rows[0]["r"].AsArraySpan<byte>(arena, catalog.SidecarRegistry).ToArray());
    }

    [Fact]
    public async Task ArraySort_String_OrdinalOrder()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<String>)");
        catalog.Plan("INSERT INTO t VALUES (['gamma', 'alpha', 'beta'])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_to_string(array_sort(v), ',') AS r FROM t", catalog);

        Assert.Equal("alpha,beta,gamma", rows[0]["r"].AsString());
    }

    [Fact]
    public async Task ArraySort_Point2D_Rejected()
    {
        using TableCatalog catalog = NewFileCatalog();
        ExpressionEvaluationException ex = await Assert.ThrowsAsync<ExpressionEvaluationException>(
            () => ExecuteQueryAsync(
                "SELECT array_sort(array(point2d(0, 0), point2d(1, 1)))", catalog));
        Assert.IsType<FunctionArgumentException>(ex.InnerException);
    }

    // ---- array_distinct ----

    [Fact]
    public async Task ArrayDistinct_Int32_PreservesFirstOccurrence()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Int32>)");
        catalog.Plan("INSERT INTO t VALUES ([" +
            "cast(1 as Int32), cast(2 as Int32), cast(2 as Int32), " +
            "cast(3 as Int32), cast(1 as Int32), cast(3 as Int32)])");

        using Arena arena = CreateArena();
        arena.AddReference();
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_distinct(v) AS r FROM t", catalog, store: arena);

        Assert.Equal(new int[] { 1, 2, 3 },
            rows[0]["r"].AsArraySpan<int>(arena, catalog.SidecarRegistry).ToArray());
    }

    [Fact]
    public async Task ArrayDistinct_Float32_FirstOccurrence()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Float32>)");
        catalog.Plan("INSERT INTO t VALUES ([1, 1, 2, 1, 2])");

        using Arena arena = CreateArena();
        arena.AddReference();
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_distinct(v) AS r FROM t", catalog, store: arena);

        Assert.Equal(new float[] { 1f, 2f },
            rows[0]["r"].AsArraySpan<float>(arena, catalog.SidecarRegistry).ToArray());
    }

    [Fact]
    public async Task ArrayDistinct_String_PreservesOrder()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<String>)");
        catalog.Plan("INSERT INTO t VALUES (['a', 'b', 'a', 'c', 'b'])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_to_string(array_distinct(v), ',') AS r FROM t", catalog);

        Assert.Equal("a,b,c", rows[0]["r"].AsString());
    }

    [Fact]
    public async Task ArrayDistinct_Boolean_AtMostTwoElements()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Boolean>)");
        catalog.Plan("INSERT INTO t VALUES ([true, false, true, false])");

        using Arena arena = CreateArena();
        arena.AddReference();
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_distinct(v) AS r FROM t", catalog, store: arena);

        Assert.Equal(new byte[] { 1, 0 },
            rows[0]["r"].AsArraySpan<byte>(arena, catalog.SidecarRegistry).ToArray());
    }
}
