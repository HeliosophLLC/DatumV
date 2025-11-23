
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
    // The four CROSS/LEFT/CROSS-APPLY/OUTER-APPLY × UNNEST tests retired with
    // UnnestFunction. LATERAL JOIN with a TVF source has no remaining built-in
    // exerciser; restore equivalent tests with the new typed-array TVF when one
    // ships post-reference-array consolidation.

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
