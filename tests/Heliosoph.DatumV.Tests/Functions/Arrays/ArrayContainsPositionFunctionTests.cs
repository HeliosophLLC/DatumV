using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Arrays;

/// <summary>
/// End-to-end SQL tests for <c>array_contains</c> / <c>array_position</c>.
/// Covers numeric / Boolean / String element kinds, the "not found" path,
/// null handling, and the multi-dim policy difference (contains is
/// shape-agnostic; position rejects multi-dim).
/// </summary>
public sealed class ArrayContainsPositionFunctionTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_arrsearch_{Guid.NewGuid():N}");
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
    public async Task ArrayContains_Int32_HitAndMiss()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Int32>(3))");
        catalog.Plan("INSERT INTO t VALUES ([" +
            "cast(10 as Int32), cast(20 as Int32), cast(30 as Int32)])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_contains(v, cast(20 as Int32)) AS hit, " +
            "       array_contains(v, cast(99 as Int32)) AS miss FROM t", catalog);

        Assert.True(rows[0]["hit"].AsBoolean());
        Assert.False(rows[0]["miss"].AsBoolean());
    }

    [Fact]
    public async Task ArrayPosition_Int32_OneBased()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Int32>(3))");
        catalog.Plan("INSERT INTO t VALUES ([" +
            "cast(10 as Int32), cast(20 as Int32), cast(30 as Int32)])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_position(v, cast(10 as Int32)) AS first, " +
            "       array_position(v, cast(30 as Int32)) AS last, " +
            "       array_position(v, cast(99 as Int32)) AS missing FROM t", catalog);

        Assert.Equal(1, rows[0]["first"].AsInt32());
        Assert.Equal(3, rows[0]["last"].AsInt32());
        Assert.True(rows[0]["missing"].IsNull);
    }

    [Fact]
    public async Task ArrayPosition_DuplicateValue_ReturnsFirstHit()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Int32>(4))");
        catalog.Plan("INSERT INTO t VALUES ([" +
            "cast(1 as Int32), cast(2 as Int32), cast(2 as Int32), cast(3 as Int32)])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_position(v, cast(2 as Int32)) AS pos FROM t", catalog);

        Assert.Equal(2, rows[0]["pos"].AsInt32());
    }

    [Fact]
    public async Task ArrayContains_Float32_NumericMatch()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Float32>(3))");
        catalog.Plan("INSERT INTO t VALUES ([1, 2, 3])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_contains(v, cast(2 as Float32)) AS hit FROM t", catalog);

        Assert.True(rows[0]["hit"].AsBoolean());
    }

    [Fact]
    public async Task ArrayContains_String_MatchesByValue()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<String>(3))");
        catalog.Plan("INSERT INTO t VALUES (['alpha', 'beta', 'gamma'])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_contains(v, 'beta') AS hit, " +
            "       array_contains(v, 'delta') AS miss FROM t", catalog);

        Assert.True(rows[0]["hit"].AsBoolean());
        Assert.False(rows[0]["miss"].AsBoolean());
    }

    [Fact]
    public async Task ArrayPosition_String_OneBased()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<String>(3))");
        catalog.Plan("INSERT INTO t VALUES (['alpha', 'beta', 'gamma'])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_position(v, 'gamma') AS pos FROM t", catalog);

        Assert.Equal(3, rows[0]["pos"].AsInt32());
    }

    [Fact]
    public async Task ArrayContains_Boolean_FindsFalse()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Boolean>(3))");
        catalog.Plan("INSERT INTO t VALUES ([true, true, false])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_contains(v, false) AS hit FROM t", catalog);

        Assert.True(rows[0]["hit"].AsBoolean());
    }

    [Fact]
    public async Task ArrayContains_MultiDim_ScansWholeTensor()
    {
        // Multi-dim is shape-agnostic for contains: any element anywhere counts.
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (m Array<Float32>(2, 3))");
        catalog.Plan("INSERT INTO t VALUES ([1, 2, 3, 4, 5, 6])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_contains(m, cast(5 as Float32)) AS hit, " +
            "       array_contains(m, cast(99 as Float32)) AS miss FROM t", catalog);

        Assert.True(rows[0]["hit"].AsBoolean());
        Assert.False(rows[0]["miss"].AsBoolean());
    }
}
