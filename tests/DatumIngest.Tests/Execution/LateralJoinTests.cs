
using DatumIngest.Catalog;
using DatumIngest.Model;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// End-to-end tests for LATERAL JOIN and CROSS/OUTER APPLY execution,
/// covering table-valued function sources and subquery sources with
/// correlated column references.
/// </summary>
public sealed class LateralJoinTests : ServiceTestBase
{
    /// <summary>
    /// CROSS JOIN LATERAL UNNEST expands a vector column per row, producing
    /// one output row per element. Rows with no elements are excluded.
    /// </summary>
    [Fact]
    public async Task CrossJoinLateral_Unnest_ExpandsVectorPerRow()
    {
        TableCatalog catalog = CreateCatalog("data",
            columns: ["name", "scores"],
            ["alice", DataValue.FromVector([1f, 2f, 3f])],
            ["bob", DataValue.FromVector([10f, 20f])]);
        List<Row> results = await ExecuteQueryAsync(
            "SELECT data.name, s.value FROM data CROSS JOIN LATERAL UNNEST(data.scores) AS s",
            catalog);

        Assert.Equal(5, results.Count);
        Assert.Equal("alice", results[0]["name"].AsString());
        Assert.Equal(1f, results[0]["value"].AsFloat32());
        Assert.Equal("alice", results[1]["name"].AsString());
        Assert.Equal(2f, results[1]["value"].AsFloat32());
        Assert.Equal("alice", results[2]["name"].AsString());
        Assert.Equal(3f, results[2]["value"].AsFloat32());
        Assert.Equal("bob", results[3]["name"].AsString());
        Assert.Equal(10f, results[3]["value"].AsFloat32());
        Assert.Equal("bob", results[4]["name"].AsString());
        Assert.Equal(20f, results[4]["value"].AsFloat32());
    }

    /// <summary>
    /// CROSS APPLY is a T-SQL alias for CROSS JOIN LATERAL.
    /// </summary>
    [Fact]
    public async Task CrossApply_BehavesAsCrossJoinLateral()
    {
        TableCatalog catalog = CreateCatalog("data",
            columns: ["name", "scores"],
            ["alice", DataValue.FromVector([1f, 2f])]);
        List<Row> results = await ExecuteQueryAsync(
            "SELECT data.name, s.value FROM data CROSS APPLY UNNEST(data.scores) AS s",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal("alice", results[0]["name"].AsString());
        Assert.Equal(1f, results[0]["value"].AsFloat32());
    }

    // ───────────────── LEFT JOIN LATERAL ─────────────────

    /// <summary>
    /// LEFT JOIN LATERAL preserves outer rows that produce no inner rows,
    /// padding the right side with NULLs.
    /// </summary>
    [Fact]
    public async Task LeftJoinLateral_PreservesUnmatchedOuterRows()
    {
        TableCatalog catalog = CreateCatalog("data",
            columns: ["name", "scores"],
            ["alice", DataValue.FromVector([1f, 2f])],
            ["bob", DataValue.FromVector([])],
            ["carol", DataValue.FromVector([5f])]);
        List<Row> results = await ExecuteQueryAsync(
            "SELECT data.name, s.value FROM data LEFT JOIN LATERAL UNNEST(data.scores) AS s",
            catalog);

        // alice: 2 rows, bob: 1 null-padded row, carol: 1 row → 4 total.
        Assert.Equal(4, results.Count);
        Assert.Equal("alice", results[0]["name"].AsString());
        Assert.Equal(1f, results[0]["value"].AsFloat32());
        Assert.Equal("alice", results[1]["name"].AsString());
        Assert.Equal(2f, results[1]["value"].AsFloat32());

        // Bob has empty vector → LEFT preserves with NULL value.
        Assert.Equal("bob", results[2]["name"].AsString());
        Assert.True(results[2]["value"].IsNull);

        Assert.Equal("carol", results[3]["name"].AsString());
        Assert.Equal(5f, results[3]["value"].AsFloat32());
    }

    /// <summary>
    /// OUTER APPLY is a T-SQL alias for LEFT JOIN LATERAL.
    /// </summary>
    [Fact]
    public async Task OuterApply_BehavesAsLeftJoinLateral()
    {
        TableCatalog catalog = CreateCatalog("data",
            columns: ["name", "scores"],
            ["alice", DataValue.FromVector([1f])],
            ["bob", DataValue.FromVector([])]);
        List<Row> results = await ExecuteQueryAsync(
            "SELECT data.name, s.value FROM data OUTER APPLY UNNEST(data.scores) AS s",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal("alice", results[0]["name"].AsString());
        Assert.Equal(1f, results[0]["value"].AsFloat32());
        Assert.Equal("bob", results[1]["name"].AsString());
        Assert.True(results[1]["value"].IsNull);
    }

    // ───────────────── LATERAL with subquery source ─────────────────

    /// <summary>
    /// LEFT JOIN LATERAL with a subquery source that references outer columns
    /// via a correlated WHERE clause.
    /// </summary>
    [Fact]
    public async Task LeftJoinLateral_CorrelatedSubquery()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("orders",
            columns: ["id", "customer"],
            [1f, "alice"],
            [2f, "bob"],
            [3f, "carol"]));
        catalog.Add(CreateProvider("items",
            columns: ["order_id", "product"],
            [1f, "widget"],
            [1f, "gadget"],
            [3f, "doohickey"]));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT orders.customer, sub.product " +
            "FROM orders " +
            "LEFT JOIN LATERAL (SELECT items.product FROM items WHERE items.order_id = orders.id) AS sub ON 1 = 1",
            catalog);

        // alice: 2 items, bob: 0 items (null-padded), carol: 1 item → 4 total.
        Assert.Equal(4, results.Count);
        Assert.Equal("alice", results[0]["customer"].AsString());
        Assert.Equal("widget", results[0]["product"].AsString());
        Assert.Equal("alice", results[1]["customer"].AsString());
        Assert.Equal("gadget", results[1]["product"].AsString());
        Assert.Equal("bob", results[2]["customer"].AsString());
        Assert.True(results[2]["product"].IsNull);
        Assert.Equal("carol", results[3]["customer"].AsString());
        Assert.Equal("doohickey", results[3]["product"].AsString());
    }

    /// <summary>
    /// CROSS JOIN LATERAL with a correlated subquery excludes outer rows
    /// that produce no inner matches.
    /// </summary>
    [Fact]
    public async Task CrossJoinLateral_CorrelatedSubquery_ExcludesUnmatchedRows()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("orders",
            columns: ["id", "customer"],
            [1f, "alice"],
            [2f, "bob"]));
        catalog.Add(CreateProvider("items",
            columns: ["order_id", "product"],
            [1f, "widget"]));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT orders.customer, sub.product " +
            "FROM orders " +
            "CROSS JOIN LATERAL (SELECT items.product FROM items WHERE items.order_id = orders.id) AS sub",
            catalog);

        // Only alice has items; bob is excluded (cross join semantics).
        Assert.Single(results);
        Assert.Equal("alice", results[0]["customer"].AsString());
        Assert.Equal("widget", results[0]["product"].AsString());
    }
}
