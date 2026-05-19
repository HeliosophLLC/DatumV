using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Execution;

/// <summary>
/// End-to-end tests for the multi-dim bracket-access syntax: <c>arr[y, x]</c>.
/// The parser accepts comma-separated indices; the type resolver still emits
/// the element kind; the evaluator computes a row-major flat offset and
/// reads the underlying element. 1-D bracket access (<c>arr[i]</c>) and
/// struct field access (<c>s['name']</c>) continue to work unchanged.
/// </summary>
public sealed class MultiDimBracketAccessTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_bracket_{Guid.NewGuid():N}");
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

    // ───────────────────── 2-D access ─────────────────────

    [Fact]
    public async Task TwoDim_ReadsRowMajorElements()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (m Array<Float32>(2,3))");
        // Row-major: m = [[1, 2, 3], [4, 5, 6]]
        catalog.Plan("INSERT INTO t VALUES ([1.0, 2.0, 3.0, 4.0, 5.0, 6.0])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT m[1, 1] AS a, m[1, 3] AS b, m[2, 1] AS c, m[2, 3] AS d FROM t",
            catalog);

        Assert.Equal(1f, rows[0]["a"].AsFloat32());
        Assert.Equal(3f, rows[0]["b"].AsFloat32());
        Assert.Equal(4f, rows[0]["c"].AsFloat32());
        Assert.Equal(6f, rows[0]["d"].AsFloat32());
    }

    // ───────────────────── 3-D access ─────────────────────

    [Fact]
    public async Task ThreeDim_ReadsRowMajorElements()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (cube Array<Int32>(2,2,2))");
        catalog.Plan("INSERT INTO t VALUES ([0, 1, 2, 3, 4, 5, 6, 7])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT cube[1, 1, 1] AS a, " +
            "       cube[2, 2, 2] AS b, " +
            "       cube[1, 2, 1] AS c " +
            "FROM t",
            catalog);

        Assert.Equal(0, rows[0]["a"].AsInt32());
        Assert.Equal(7, rows[0]["b"].AsInt32());
        Assert.Equal(2, rows[0]["c"].AsInt32());
    }

    // ───────────────────── 1-D bracket access (regression) ─────────────────────

    [Fact]
    public async Task OneDim_SingleIndex_StillWorks()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Float32>(4))");
        catalog.Plan("INSERT INTO t VALUES ([10.0, 20.0, 30.0, 40.0])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT v[1] AS first, v[4] AS last FROM t", catalog);

        Assert.Equal(10f, rows[0]["first"].AsFloat32());
        Assert.Equal(40f, rows[0]["last"].AsFloat32());
    }

    // ───────────────────── Out-of-range returns NULL ─────────────────────

    [Fact]
    public async Task MultiDim_OutOfRange_ReturnsNull()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (m Array<Float32>(2,3))");
        catalog.Plan("INSERT INTO t VALUES ([1.0, 2.0, 3.0, 4.0, 5.0, 6.0])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT m[1, 99] AS too_high, m[6, 1] AS too_low FROM t", catalog);

        Assert.True(rows[0]["too_high"].IsNull);
        Assert.True(rows[0]["too_low"].IsNull);
    }

    // ───────────────────── Wrong ndim throws ─────────────────────

    [Fact]
    public async Task MultiDim_WrongIndexCount_Throws()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (m Array<Float32>(2,3))");
        catalog.Plan("INSERT INTO t VALUES ([1.0, 2.0, 3.0, 4.0, 5.0, 6.0])");

        // 2-D column, only 1 index supplied.
        await Assert.ThrowsAsync<Heliosoph.DatumV.Execution.ExpressionEvaluationException>(
            () => ExecuteQueryAsync("SELECT m[1] FROM t", catalog));
    }

    // ───────────────────── Struct field access still works ─────────────────────

    [Fact]
    public async Task Struct_NamedFieldAccess_StillWorks()
    {
        // Regression: ensure the multi-index dispatch doesn't break the
        // existing struct['field'] form.
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (id Int32)");
        catalog.Plan("INSERT INTO t VALUES (42)");

        // Use a struct literal to avoid declaring a struct column.
        // The literal `2` parses as the narrowest fitting type (Int8) — match it.
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT {a: 1, b: 2}['b'] AS f FROM t", catalog);

        Assert.Equal(2, (sbyte)rows[0]["f"].ToInt32());
    }
}
