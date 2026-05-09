using DatumIngest.Catalog;
using DatumIngest.Model;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// End-to-end tests for set-returning function calls (table-valued functions
/// like <c>unnest</c> and <c>range</c>) appearing in a SELECT projection list,
/// which the <see cref="DatumIngest.Execution.Planner.ProjectionSetReturningRewriter"/>
/// rewrites into a synthesized FROM source.
/// </summary>
public sealed class ProjectionSetReturningTests : ServiceTestBase
{
    [Fact]
    public async Task Unnest_InProjection_NoFrom_ExpandsArrayElements()
    {
        TableCatalog catalog = CreateCatalog();

        List<Row> results = await ExecuteQueryAsync(
            "SELECT UNNEST(['a', 'b', 'v'])",
            catalog);

        Assert.Equal(3, results.Count);
        Assert.Equal("a", results[0]["value"].AsString());
        Assert.Equal("b", results[1]["value"].AsString());
        Assert.Equal("v", results[2]["value"].AsString());
    }

    [Fact]
    public async Task Range_InProjection_NoFrom_ExpandsToRows()
    {
        TableCatalog catalog = CreateCatalog();

        List<Row> results = await ExecuteQueryAsync(
            "SELECT RANGE(0, 4)",
            catalog);

        Assert.Equal(5, results.Count);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(i, results[i]["Value"].AsInt32());
        }
    }

    [Fact]
    public async Task Unnest_InProjection_WithFrom_CrossJoinsLaterallyPerRow()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("t",
            columns: ["id"],
            [1f],
            [2f]));

        // Static array argument — no correlation to outer row, so result is
        // each input row × every element: 2 rows × 3 elements = 6 rows.
        List<Row> results = await ExecuteQueryAsync(
            "SELECT t.id, UNNEST(['x', 'y', 'z']) FROM t",
            catalog);

        Assert.Equal(6, results.Count);
        // Order is outer-first (t.id), inner per row (3 elements each).
        Assert.Equal(1f, results[0]["id"].AsFloat32());
        Assert.Equal("x", results[0]["value"].AsString());
        Assert.Equal(1f, results[1]["id"].AsFloat32());
        Assert.Equal("y", results[1]["value"].AsString());
        Assert.Equal(1f, results[2]["id"].AsFloat32());
        Assert.Equal("z", results[2]["value"].AsString());
        Assert.Equal(2f, results[3]["id"].AsFloat32());
        Assert.Equal("x", results[3]["value"].AsString());
    }

    [Fact]
    public async Task SelectStarPlusUnnest_OmitsSyntheticSourceFromWildcard()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("testing",
            columns: ["id", "label"],
            [1f, "first"],
            [2f, "second"]));

        // SELECT * should pull only the original table's columns, NOT the
        // synthesized SRF source — and the column names should be unqualified
        // (no `testing.id`) even though the rewriter added a cross join.
        List<Row> results = await ExecuteQueryAsync(
            "SELECT *, UNNEST(['x', 'y']) FROM testing",
            catalog);

        Assert.Equal(4, results.Count);
        // First row should have exactly 3 columns: id, label, value (no testing.id, no __srf_0.value).
        Assert.Equal(3, results[0].FieldCount);
        Assert.Equal(1f, results[0]["id"].AsFloat32());
        Assert.Equal("first", results[0]["label"].AsString());
        Assert.Equal("x", results[0]["value"].AsString());
    }

    [Fact]
    public async Task CommaJoinWithUnnest_LateralLyExpandsPerRow()
    {
        // SQL-89 / PG comma-style FROM list: `FROM t, unnest(t.col)` lowers
        // to CROSS JOIN LATERAL — the function source implicitly correlates
        // to outer columns of `t`. Each outer row's array expands into
        // sum(array.length) rows.
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("docs",
            columns: ["id", "tags"],
            new object?[] { 1f, new[] { "alpha", "beta" } },
            new object?[] { 2f, new[] { "gamma" } }));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT docs.id, tag.value FROM docs, unnest(docs.tags) AS tag",
            catalog);

        // 2 from row1 (alpha, beta) + 1 from row2 (gamma) = 3 rows.
        Assert.Equal(3, results.Count);
        Assert.Equal(1f, results[0]["id"].AsFloat32());
        Assert.Equal("alpha", results[0]["value"].AsString());
        Assert.Equal(1f, results[1]["id"].AsFloat32());
        Assert.Equal("beta", results[1]["value"].AsString());
        Assert.Equal(2f, results[2]["id"].AsFloat32());
        Assert.Equal("gamma", results[2]["value"].AsString());
    }

    [Fact]
    public async Task TwoSetReturningFunctionsInProjection_Rejected()
    {
        TableCatalog catalog = CreateCatalog();

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExecuteQueryAsync(
                "SELECT UNNEST(['a', 'b']), RANGE(1, 3)",
                catalog));

        Assert.Contains("Only one set-returning function per SELECT projection is supported", ex.Message);
    }

    [Fact]
    public async Task NestedSetReturningInsideScalar_Rejected()
    {
        TableCatalog catalog = CreateCatalog();

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExecuteQueryAsync(
                // unnest is nested inside upper(): not supported in v1.
                "SELECT UPPER(UNNEST(['a', 'b']))",
                catalog));

        Assert.Contains("only supported as the top-level expression", ex.Message);
    }

    [Fact]
    public async Task SetReturningInWhereClause_Rejected()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("t",
            columns: ["id"],
            [1f]));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExecuteQueryAsync(
                "SELECT t.id FROM t WHERE UNNEST(['a']) = 'a'",
                catalog));

        Assert.Contains("not allowed in WHERE", ex.Message);
    }

    [Fact]
    public async Task SetReturningInOrderBy_Rejected()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("t",
            columns: ["id"],
            [1f]));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExecuteQueryAsync(
                "SELECT t.id FROM t ORDER BY UNNEST(['a'])",
                catalog));

        Assert.Contains("not allowed in ORDER BY", ex.Message);
    }
}
