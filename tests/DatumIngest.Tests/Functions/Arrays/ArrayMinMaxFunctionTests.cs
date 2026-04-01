using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Arrays;

/// <summary>
/// End-to-end SQL tests for <c>array_min</c> / <c>array_max</c>. Exercises a
/// representative slice of element kinds (numeric, Boolean), edge cases
/// (single-element, multi-dim), and the rejection path for unsupported
/// element kinds (String, which is a reference-array carrier).
/// </summary>
/// <remarks>
/// Array literals (<c>[a, b, c, ...]</c>) require all elements to share an
/// exact <see cref="DataKind"/> — mixed integer/float literals are rejected
/// even when the column would coerce. Tests below use whole-number literals
/// so all elements parse as the same small-integer kind and the array is
/// coerced to the column's element kind on INSERT; CAST is used where a
/// non-coercible mix is desired (e.g. signed Int32 with negatives).
/// </remarks>
public sealed class ArrayMinMaxFunctionTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_arrminmax_{Guid.NewGuid():N}");
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
    public async Task ArrayMin_Float32_ReturnsSmallest()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Float32>(4))");
        catalog.Plan("INSERT INTO t VALUES ([3, 1, 4, 2])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_min(v) AS mn, array_max(v) AS mx FROM t", catalog);

        Assert.Equal(1.0f, rows[0]["mn"].AsFloat32());
        Assert.Equal(4.0f, rows[0]["mx"].AsFloat32());
    }

    [Fact]
    public async Task ArrayMin_Int32_NegativeValues()
    {
        // Negative literals are Int32 while small positives are Int8; cast
        // each element to Int32 so the array literal is kind-uniform.
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Int32>(5))");
        catalog.Plan("INSERT INTO t VALUES ([" +
            "cast(-3 as Int32), cast(7 as Int32), cast(-10 as Int32), " +
            "cast(0 as Int32), cast(2 as Int32)])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_min(v) AS mn, array_max(v) AS mx FROM t", catalog);

        Assert.Equal(-10, rows[0]["mn"].AsInt32());
        Assert.Equal(7, rows[0]["mx"].AsInt32());
    }

    [Fact]
    public async Task ArrayMin_UInt8_BytePayload()
    {
        // Use values that all fit in Int8 so the array literal is kind-uniform;
        // the column declaration coerces to UInt8.
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<UInt8>(4))");
        catalog.Plan("INSERT INTO t VALUES ([100, 5, 90, 33])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_min(v) AS mn, array_max(v) AS mx FROM t", catalog);

        Assert.Equal((byte)5, rows[0]["mn"].AsUInt8());
        Assert.Equal((byte)100, rows[0]["mx"].AsUInt8());
    }

    [Fact]
    public async Task ArrayMin_Float64_LargeRange()
    {
        // Whole-number literals parse to Int8 and coerce to Float64 at the
        // column boundary — the array_min reduction reads the typed Float64
        // span directly and returns a Float64.
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Float64>(4))");
        catalog.Plan("INSERT INTO t VALUES ([42, 7, 13, 100])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_min(v) AS mn, array_max(v) AS mx FROM t", catalog);

        Assert.Equal(7.0, rows[0]["mn"].AsFloat64());
        Assert.Equal(100.0, rows[0]["mx"].AsFloat64());
    }

    [Fact]
    public async Task ArrayMin_MultiDim_ReducesAcrossWholeTensor()
    {
        // Multi-dim arrays should reduce across the whole flat element span,
        // not per-row or per-dim. The result is the single global min/max.
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (m Array<Float32>(2, 3))");
        catalog.Plan("INSERT INTO t VALUES ([3, 1, 4, 5, 9, 2])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_min(m) AS mn, array_max(m) AS mx FROM t", catalog);

        Assert.Equal(1.0f, rows[0]["mn"].AsFloat32());
        Assert.Equal(9.0f, rows[0]["mx"].AsFloat32());
    }

    [Fact]
    public async Task ArrayMin_Boolean_FalseLessThanTrue()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Boolean>(3))");
        catalog.Plan("INSERT INTO t VALUES ([true, false, true])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_min(v) AS mn, array_max(v) AS mx FROM t", catalog);

        Assert.False(rows[0]["mn"].AsBoolean());
        Assert.True(rows[0]["mx"].AsBoolean());
    }

    [Fact]
    public async Task ArrayMin_SingleElement_ReturnsThatElement()
    {
        // Reduction over a one-element span: the only candidate wins.
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Int32>(1))");
        catalog.Plan("INSERT INTO t VALUES ([cast(42 as Int32)])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_min(v) AS mn, array_max(v) AS mx FROM t", catalog);

        Assert.Equal(42, rows[0]["mn"].AsInt32());
        Assert.Equal(42, rows[0]["mx"].AsInt32());
    }

    [Fact]
    public async Task ArrayMin_StringElements_Rejected()
    {
        // String arrays use a different carrier shape and cross-arena
        // comparison; not yet wired through array_min / array_max.
        await Assert.ThrowsAsync<ExpressionEvaluationException>(
            () => ExecuteQueryAsync("SELECT array_min(array('b', 'a', 'c'))", CreateCatalog()));
    }
}
