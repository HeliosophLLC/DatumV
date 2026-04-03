using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// Multi-dim status must propagate through <c>SELECT</c> projections so that
/// nested queries (and downstream signature dispatch) see the same shape
/// classification as the outermost source column. Before this work, a
/// multi-dim column projected through a subquery lost its <c>IsMultiDim</c>
/// at the <see cref="ResolvedColumn"/> boundary and the outer query saw it
/// as a flat array — silently mis-dispatching multi-dim-aware functions.
/// </summary>
public sealed class MultiDimProjectionPropagationTests : ServiceTestBase, IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"datum_proj_{Guid.NewGuid():N}");
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

    private static async Task<ResolvedQuerySchema> ResolveAsync(TableCatalog catalog, string sql)
    {
        QuerySchemaResolver resolver = new(catalog, FunctionRegistry.CreateDefault());
        QueryExpression query = SqlParser.Parse(sql);
        SelectStatement select = ((SelectQueryExpression)query).Statement;
        return await resolver.ResolveAsync(select, CancellationToken.None);
    }

    // ───────────────────── Schema-level propagation ─────────────────────

    [Fact]
    public async Task SubquerySelect_OfMultiDimColumn_PreservesIsMultiDim()
    {
        // The subquery's resolved schema must carry IsMultiDim = true so the
        // outer query's column reference resolves as multi-dim too.
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (m Array<Float32>(2,3))");
        catalog.Plan("INSERT INTO t VALUES ([1.0, 2.0, 3.0, 4.0, 5.0, 6.0])");

        ResolvedQuerySchema resolved = await ResolveAsync(
            catalog, "SELECT m FROM (SELECT m FROM t) sub");

        ResolvedColumn? mCol = resolved.FindColumn("m");
        Assert.NotNull(mCol);
        Assert.True(mCol.IsArray);
        Assert.True(mCol.IsMultiDim);
    }

    [Fact]
    public async Task SubquerySelect_OfFlatColumn_LeavesIsMultiDimFalse()
    {
        // Sanity: 1-D source columns must NOT gain IsMultiDim through projection.
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (v Array<Float32>(4))");
        catalog.Plan("INSERT INTO t VALUES ([1.0, 2.0, 3.0, 4.0])");

        ResolvedQuerySchema resolved = await ResolveAsync(
            catalog, "SELECT v FROM (SELECT v FROM t) sub");

        ResolvedColumn? vCol = resolved.FindColumn("v");
        Assert.NotNull(vCol);
        Assert.True(vCol.IsArray);
        Assert.False(vCol.IsMultiDim);
    }

    [Fact]
    public async Task DirectTableSelect_OfMultiDimColumn_HasIsMultiDim()
    {
        // Even without a subquery, the resolved-column view of the source table
        // must surface IsMultiDim so editor / catalog tooling sees it.
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (m Array<Float32>(2,3))");

        ResolvedQuerySchema resolved = await ResolveAsync(catalog, "SELECT m FROM t");

        ResolvedColumn? mCol = resolved.FindColumn("m");
        Assert.NotNull(mCol);
        Assert.True(mCol.IsMultiDim);
    }

    // ───────────────────── End-to-end: outer query sees multi-dim ─────────────────────

    [Fact]
    public async Task BracketAccess_OnSubqueryMultiDimColumn_Works()
    {
        // Without IsMultiDim propagation the outer query would reject m[y, x]
        // because the schema reported the column as flat 1-D.
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (m Array<Float32>(2,3))");
        catalog.Plan("INSERT INTO t VALUES ([1.0, 2.0, 3.0, 4.0, 5.0, 6.0])");

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT m[2, 3] AS e FROM (SELECT m FROM t) sub", catalog);

        Assert.Single(rows);
        Assert.Equal(6f, rows[0]["e"].AsFloat32());
    }

    [Fact]
    public async Task ArrayShape_OnSubqueryMultiDimColumn_ReturnsCorrectShape()
    {
        using TableCatalog catalog = NewFileCatalog();
        catalog.Plan("CREATE TABLE t (m Array<Float32>(2,3))");
        catalog.Plan("INSERT INTO t VALUES ([1.0, 2.0, 3.0, 4.0, 5.0, 6.0])");

        using Arena arena = new();
        arena.AddReference();
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT array_shape(m) AS s FROM (SELECT m FROM t) sub", catalog, store: arena);

        Assert.Equal([2, 3],
            rows[0]["s"].AsArraySpan<int>(arena, catalog.SidecarRegistry).ToArray());
    }
}
