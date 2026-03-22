using DatumIngest.Catalog;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Arrays;

/// <summary>
/// PostgreSQL-compatibility tests for the array introspection trio:
/// <c>cardinality(arr)</c>, <c>array_length(arr, dim)</c>, <c>array_ndims(arr)</c>.
/// Mirrors PG semantics: array_length is 1-based and dim-required (no single-arg
/// form), out-of-range dim returns NULL, cardinality returns the flat element
/// count.
/// </summary>
public sealed class PgArrayCompatTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_pgcompat_{Guid.NewGuid():N}");
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

    // ───────────────────── cardinality ─────────────────────

    [Fact]
    public async Task Cardinality_FlatArray_ReturnsLength()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Float32>(4))");
        catalog.Plan("INSERT INTO t VALUES ([1.0, 2.0, 3.0, 4.0])");

        List<Row> rows = await ExecuteQueryAsync("SELECT cardinality(v) AS n FROM t", catalog);
        Assert.Equal(4, rows[0]["n"].AsInt32());
    }

    [Fact]
    public async Task Cardinality_MultiDim_ReturnsProductOfDims()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (m Array<Float32>(2,3))");
        catalog.Plan("INSERT INTO t VALUES ([1.0, 2.0, 3.0, 4.0, 5.0, 6.0])");

        List<Row> rows = await ExecuteQueryAsync("SELECT cardinality(m) AS n FROM t", catalog);
        Assert.Equal(6, rows[0]["n"].AsInt32());
    }

    // ───────────────────── array_length(arr, dim) ─────────────────────

    [Fact]
    public async Task ArrayLength_FlatArray_Dim1_ReturnsLength()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Float32>(4))");
        catalog.Plan("INSERT INTO t VALUES ([10.0, 20.0, 30.0, 40.0])");

        List<Row> rows = await ExecuteQueryAsync("SELECT array_length(v, 1) AS n FROM t", catalog);
        Assert.Equal(4, rows[0]["n"].AsInt32());
    }

    [Fact]
    public async Task ArrayLength_FlatArray_Dim2_ReturnsNull()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Float32>(4))");
        catalog.Plan("INSERT INTO t VALUES ([10.0, 20.0, 30.0, 40.0])");

        List<Row> rows = await ExecuteQueryAsync("SELECT array_length(v, 2) AS n FROM t", catalog);
        Assert.True(rows[0]["n"].IsNull);
    }

    [Fact]
    public async Task ArrayLength_MultiDim_PerDim_ReturnsEachSize()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (m Array<Float32>(2,3))");
        catalog.Plan("INSERT INTO t VALUES ([1.0, 2.0, 3.0, 4.0, 5.0, 6.0])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_length(m, 1) AS d1, array_length(m, 2) AS d2 FROM t", catalog);
        Assert.Equal(2, rows[0]["d1"].AsInt32());
        Assert.Equal(3, rows[0]["d2"].AsInt32());
    }

    [Fact]
    public async Task ArrayLength_OutOfRangeDim_ReturnsNull()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (m Array<Float32>(2,3))");
        catalog.Plan("INSERT INTO t VALUES ([1.0, 2.0, 3.0, 4.0, 5.0, 6.0])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_length(m, 99) AS too_high, array_length(m, 0) AS too_low FROM t", catalog);
        Assert.True(rows[0]["too_high"].IsNull);
        Assert.True(rows[0]["too_low"].IsNull);
    }

    [Fact]
    public async Task ArrayLength_SingleArgForm_ThrowsAtParseOrPlanTime()
    {
        // PG strict: array_length requires the dim argument. The single-arg form
        // must fail before runtime so LLM-written PG-style queries don't error
        // mid-execution after partial work.
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Float32>(4))");
        catalog.Plan("INSERT INTO t VALUES ([1.0, 2.0, 3.0, 4.0])");

        await Assert.ThrowsAnyAsync<Exception>(
            () => ExecuteQueryAsync("SELECT array_length(v) AS n FROM t", catalog));
    }

    // ───────────────────── array_ndims ─────────────────────

    [Fact]
    public async Task ArrayNdims_FlatArray_ReturnsOne()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Float32>(4))");
        catalog.Plan("INSERT INTO t VALUES ([1.0, 2.0, 3.0, 4.0])");

        List<Row> rows = await ExecuteQueryAsync("SELECT array_ndims(v) AS d FROM t", catalog);
        Assert.Equal(1, rows[0]["d"].AsInt32());
    }

    [Fact]
    public async Task ArrayNdims_MultiDim_ReturnsNdim()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (m Array<Float32>(2,3), cube Array<Int32>(2,2,2))");
        catalog.Plan("INSERT INTO t VALUES ([1.0, 2.0, 3.0, 4.0, 5.0, 6.0], [0, 1, 2, 3, 4, 5, 6, 7])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_ndims(m) AS d2, array_ndims(cube) AS d3 FROM t", catalog);
        Assert.Equal(2, rows[0]["d2"].AsInt32());
        Assert.Equal(3, rows[0]["d3"].AsInt32());
    }
}
