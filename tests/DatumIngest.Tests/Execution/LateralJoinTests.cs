
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
    /// LEFT JOIN LATERAL where the FIRST driving row has no matching inner rows
    /// and a SUBSEQUENT driving row does match. The unmatched-first row is
    /// deferred (stabilised into <c>context.Store</c>) until the first right row
    /// materialises on a later driving row, then flushed as combined-with-null-pad
    /// in input order. This preserves output-schema consistency — without the
    /// deferral, the left-solo pass-through emit would mix schemas with the
    /// subsequent combined emit in the same output batch.
    /// </summary>
    [Fact]
    public async Task LeftJoinLateral_UnmatchedLeftBeforeMatched_DefersToPreserveSchema()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("orders",
            columns: ["id", "customer"],
            [2f, "bob"],     // first — no matching items (no right row has matched yet)
            [1f, "alice"])); // second — matches items
        catalog.Add(CreateProvider("items",
            columns: ["order_id", "product"],
            [1f, "widget"]));

        List<Row> results = await ExecuteQueryAsync(
            "SELECT orders.customer, sub.product " +
            "FROM orders " +
            "LEFT JOIN LATERAL (SELECT items.product FROM items WHERE items.order_id = orders.id) AS sub ON 1 = 1",
            catalog);

        // bob (no match) was deferred until alice's match initialised the
        // null-pad template, then flushed as combined-with-null-pad in input order.
        Assert.Equal(2, results.Count);

        Assert.Equal("bob", results[0]["customer"].AsString());
        Assert.True(results[0]["product"].IsNull);

        Assert.Equal("alice", results[1]["customer"].AsString());
        Assert.Equal("widget", results[1]["product"].AsString());
    }

    /// <summary>
    /// LEFT JOIN LATERAL where NO driving row ever produces a matching inner row.
    /// All unmatched lefts are deferred during the loop; at end-of-execution the
    /// fallback path emits them as left-solo (with the driving-side schema).
    /// </summary>
    [Fact]
    public async Task LeftJoinLateral_NoRightRowEver_FallsBackToLeftSolo()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("orders",
            columns: ["id", "customer"],
            [1f, "alice"],
            [2f, "bob"]));
        catalog.Add(CreateProvider("items",
            columns: ["order_id", "product"]));  // empty — no rows ever match

        List<Row> results = await ExecuteQueryAsync(
            "SELECT orders.customer FROM orders " +
            "LEFT JOIN LATERAL (SELECT items.product FROM items WHERE items.order_id = orders.id) AS sub ON 1 = 1",
            catalog);

        // Both driving rows surface in input order with no right-side columns
        // materialised (the projection only selects orders.customer anyway).
        Assert.Equal(2, results.Count);
        Assert.Equal("alice", results[0]["customer"].AsString());
        Assert.Equal("bob", results[1]["customer"].AsString());
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
