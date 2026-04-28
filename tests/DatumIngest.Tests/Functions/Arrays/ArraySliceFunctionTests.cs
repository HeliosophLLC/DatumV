using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Arrays;

/// <summary>
/// End-to-end SQL tests for <c>array_slice(arr, start, length)</c>.
/// Verifies PG-style 1-based indexing, right-edge clamp, hard errors
/// on negative inputs, multi-dim rejection, and round-tripping of
/// the common primitive element kinds.
/// </summary>
public sealed class ArraySliceFunctionTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_arrslice_{Guid.NewGuid():N}");
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
    public async Task Slice_Middle_ReturnsRequestedWindow()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Float32>)");
        catalog.Plan("INSERT INTO t VALUES ([10.0, 20.0, 30.0, 40.0, 50.0])");

        using Arena arena = new();
        arena.AddReference();
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_slice(v, 2, 3) AS s FROM t", catalog, store: arena);

        DataValue s = rows[0]["s"];
        Assert.True(s.IsArray);
        Assert.Equal(DataKind.Float32, s.Kind);
        Assert.Equal(new float[] { 20f, 30f, 40f },
            s.AsArraySpan<float>(arena, catalog.SidecarRegistry).ToArray());
    }

    [Fact]
    public async Task Slice_FromStart_FirstNElements()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Int64>)");
        catalog.Plan("INSERT INTO t VALUES ([1, 2, 3, 4, 5])");

        using Arena arena = new();
        arena.AddReference();
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_slice(v, 1, 2) AS s FROM t", catalog, store: arena);

        Assert.Equal(new long[] { 1, 2 },
            rows[0]["s"].AsArraySpan<long>(arena, catalog.SidecarRegistry).ToArray());
    }

    [Fact]
    public async Task Slice_OverflowsLength_ClampsToEnd()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Int32>)");
        catalog.Plan("INSERT INTO t VALUES ([1, 2, 3])");

        using Arena arena = new();
        arena.AddReference();
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_slice(v, 2, 100) AS s FROM t", catalog, store: arena);

        // PG-style clamp: requested 100 starting from position 2 of a length-3
        // array → returns positions 2 and 3.
        Assert.Equal(new int[] { 2, 3 },
            rows[0]["s"].AsArraySpan<int>(arena, catalog.SidecarRegistry).ToArray());
    }

    [Fact]
    public async Task Slice_StartPastEnd_ReturnsEmpty()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Float32>)");
        catalog.Plan("INSERT INTO t VALUES ([1.0, 2.0, 3.0])");

        using Arena arena = new();
        arena.AddReference();
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_slice(v, 10, 5) AS s FROM t", catalog, store: arena);

        Assert.Equal(Array.Empty<float>(),
            rows[0]["s"].AsArraySpan<float>(arena, catalog.SidecarRegistry).ToArray());
    }

    [Fact]
    public async Task Slice_LengthZero_ReturnsEmpty()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Float32>)");
        catalog.Plan("INSERT INTO t VALUES ([1.0, 2.0, 3.0])");

        using Arena arena = new();
        arena.AddReference();
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_slice(v, 1, 0) AS s FROM t", catalog, store: arena);

        Assert.Equal(Array.Empty<float>(),
            rows[0]["s"].AsArraySpan<float>(arena, catalog.SidecarRegistry).ToArray());
    }

    [Fact]
    public async Task Slice_StartZero_Throws()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Float32>)");
        catalog.Plan("INSERT INTO t VALUES ([1.0, 2.0, 3.0])");

        ExecutionException ex = await Assert.ThrowsAnyAsync<ExecutionException>(() =>
            ExecuteQueryAsync("SELECT array_slice(v, 0, 1) AS s FROM t", catalog));
        Assert.Contains("start", ex.Message);
    }

    [Fact]
    public async Task Slice_NegativeLength_Throws()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Float32>)");
        catalog.Plan("INSERT INTO t VALUES ([1.0, 2.0, 3.0])");

        ExecutionException ex = await Assert.ThrowsAnyAsync<ExecutionException>(() =>
            ExecuteQueryAsync("SELECT array_slice(v, 1, -1) AS s FROM t", catalog));
        Assert.Contains("length", ex.Message);
    }

    [Fact]
    public async Task Slice_MultiDim_Rejected()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (m Array<Float32>(2, 3))");
        catalog.Plan("INSERT INTO t VALUES ([1.0, 2.0, 3.0, 4.0, 5.0, 6.0])");

        ExecutionException ex = await Assert.ThrowsAnyAsync<ExecutionException>(() =>
            ExecuteQueryAsync("SELECT array_slice(m, 1, 2) AS s FROM t", catalog));
        Assert.Contains("1-D", ex.Message);
    }

    [Fact]
    public async Task Slice_NullArray_ReturnsNullArray()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Float32>)");
        catalog.Plan("INSERT INTO t VALUES (NULL)");

        using Arena arena = new();
        arena.AddReference();
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_slice(v, 1, 2) AS s FROM t", catalog, store: arena);

        Assert.True(rows[0]["s"].IsNull);
    }

    [Fact]
    public async Task Slice_BooleanArray_RoundTrips()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Boolean>)");
        catalog.Plan("INSERT INTO t VALUES ([true, false, true, true, false])");

        using Arena arena = new();
        arena.AddReference();
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_slice(v, 2, 3) AS s FROM t", catalog, store: arena);

        DataValue s = rows[0]["s"];
        Assert.True(s.IsArray);
        Assert.Equal(DataKind.Boolean, s.Kind);
        byte[] bytes = s.AsArraySpan<byte>(arena, catalog.SidecarRegistry).ToArray();
        Assert.Equal(new byte[] { 0, 1, 1 }, bytes);
    }
}
