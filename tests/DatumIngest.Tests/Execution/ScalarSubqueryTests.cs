using System.Runtime.CompilerServices;
using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// End-to-end tests for scalar subquery execution, covering both uncorrelated
/// (plan-time constant folding) and correlated (per-row ScalarSubqueryOperator)
/// subqueries.
/// </summary>
public sealed class ScalarSubqueryTests : ServiceTestBase
{
    // ─────────────── Uncorrelated scalar subqueries ───────────────

    /// <summary>
    /// Uncorrelated scalar subquery in WHERE clause: the subquery is constant-folded
    /// at plan time and the filter uses the resulting literal.
    /// </summary>
    [Fact]
    public async Task UncorrelatedSubquery_InWhere_FiltersCorrectly()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("employees",
            columns: ["name", "salary"],
            ["alice", 50_000f],
            ["bob", 80_000f],
            ["carol", 60_000f]));
        catalog.Add(CreateProvider("thresholds",
            columns: ["min_salary"],
            [55_000f]));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT name FROM employees WHERE salary > (SELECT min_salary FROM thresholds)",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal("bob", results[0]["name"].AsString());
        Assert.Equal("carol", results[1]["name"].AsString());
    }

    /// <summary>
    /// Uncorrelated scalar subquery in SELECT list: the subquery value appears
    /// as a computed column in every output row.
    /// </summary>
    [Fact]
    public async Task UncorrelatedSubquery_InSelect_AddsComputedColumn()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("items",
            columns: ["name", "price"],
            ["widget", 10f],
            ["gadget", 20f]));
        catalog.Add(CreateProvider("settings",
            columns: ["tax_rate"],
            [0.1f]));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT name, price * (SELECT tax_rate FROM settings) AS tax FROM items",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal(1f, results[0]["tax"].AsFloat32(), 0.001f);
        Assert.Equal(2f, results[1]["tax"].AsFloat32(), 0.001f);
    }

    /// <summary>
    /// An uncorrelated subquery that returns zero rows produces SQL NULL.
    /// </summary>
    [Fact]
    public async Task UncorrelatedSubquery_EmptyResult_ReturnsNull()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("data",
            columns: ["x"],
            [1f]));
        catalog.Add(CreateProvider("empty_table",
            columns: ["x"]));

        // The subquery returns zero rows → NULL → the comparison "x > NULL" should filter out all rows.
        List<Row> results = await ExecuteQueryAsync(
            "SELECT x FROM data WHERE x > (SELECT x FROM empty_table)",
            catalog);

        Assert.Empty(results);
    }

    /// <summary>
    /// An uncorrelated subquery that returns more than one row must throw.
    /// </summary>
    [Fact]
    public async Task UncorrelatedSubquery_MultipleRows_Throws()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("data",
            columns: ["x"],
            [1f]));
        catalog.Add(CreateProvider("multi",
            columns: ["val"],
            [10f],
            [20f]));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ExecuteQueryAsync(
                "SELECT x FROM data WHERE x > (SELECT val FROM multi)",
                catalog));

        Assert.Contains("more than one row", exception.Message);
    }

    /// <summary>
    /// An uncorrelated subquery that returns more than one column must throw.
    /// </summary>
    [Fact]
    public async Task UncorrelatedSubquery_MultipleColumns_Throws()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("data",
            columns: ["x"],
            [1f]));
        catalog.Add(CreateProvider("wide",
            columns: ["a", "b"],
            [1f, 2f]));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ExecuteQueryAsync(
                "SELECT x FROM data WHERE x > (SELECT a, b FROM wide)",
                catalog));

        Assert.Contains("exactly one column", exception.Message);
    }

    /// <summary>
    /// Two independent uncorrelated subqueries in a single WHERE clause,
    /// both constant-folded independently.
    /// </summary>
    [Fact]
    public async Task UncorrelatedSubquery_MultipleSameQuery_BothFolded()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("data",
            columns: ["x"],
            [5f],
            [15f],
            [25f]));
        catalog.Add(CreateProvider("lo",
            columns: ["val"],
            [10f]));
        catalog.Add(CreateProvider("hi",
            columns: ["val"],
            [20f]));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT x FROM data WHERE x > (SELECT val FROM lo) AND x < (SELECT val FROM hi)",
            catalog);

        Assert.Single(results);
        Assert.Equal(15f, results[0]["x"].AsFloat32());
    }

    // ─────────────── Correlated scalar subqueries ───────────────

    /// <summary>
    /// A correlated scalar subquery in WHERE: the inner query references an
    /// outer-scope table alias and is executed per outer row.
    /// </summary>
    [Fact]
    public async Task CorrelatedSubquery_InWhere_ExecutesPerRow()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("orders",
            columns: ["id", "total"],
            [1f, 100f],
            [2f, 200f],
            [3f, 50f]));
        catalog.Add(CreateProvider("thresholds",
            columns: ["order_id", "min_total"],
            [1f, 150f],
            [2f, 150f],
            [3f, 150f]));

        // For each order, check if its total exceeds the threshold for that order_id.
        // Only order 2 (total=200) exceeds its threshold (min_total=150).
        List<Row> results = await ExecuteQueryAsync(
            "SELECT orders.id FROM orders WHERE orders.total > " +
            "(SELECT min_total FROM thresholds WHERE thresholds.order_id = orders.id)",
            catalog);

        Assert.Single(results);
        Assert.Equal(2f, results[0]["id"].AsFloat32());
    }

    /// <summary>
    /// A correlated scalar subquery in the SELECT list: computes a per-row
    /// derived value from another table.
    /// </summary>
    [Fact]
    public async Task CorrelatedSubquery_InSelect_ComputesPerRow()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("products",
            columns: ["id", "name"],
            [1f, "widget"],
            [2f, "gadget"]));
        catalog.Add(CreateProvider("prices",
            columns: ["product_id", "price"],
            [1f, 9.99f],
            [2f, 19.99f]));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT products.name, " +
            "(SELECT price FROM prices WHERE prices.product_id = products.id) AS price " +
            "FROM products",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal("widget", results[0]["name"].AsString());
        Assert.Equal(9.99f, results[0]["price"].AsFloat32(), 0.01f);
        Assert.Equal("gadget", results[1]["name"].AsString());
        Assert.Equal(19.99f, results[1]["price"].AsFloat32(), 0.01f);
    }

    /// <summary>
    /// A correlated subquery that matches no inner rows for a given outer row
    /// should produce NULL for that row.
    /// </summary>
    [Fact]
    public async Task CorrelatedSubquery_NoMatch_ReturnsNull()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("outer_table",
            columns: ["id"],
            [1f],
            [99f]));
        catalog.Add(CreateProvider("inner_table",
            columns: ["ref_id", "val"],
            [1f, 42f]));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT outer_table.id, " +
            "(SELECT val FROM inner_table WHERE inner_table.ref_id = outer_table.id) AS lookup " +
            "FROM outer_table",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal(42f, results[0]["lookup"].AsFloat32());
        Assert.True(results[1]["lookup"].IsNull);
    }

    /// <summary>
    /// A correlated subquery that returns multiple rows for an outer row must throw.
    /// </summary>
    [Fact]
    public async Task CorrelatedSubquery_MultipleRows_Throws()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("outer_table",
            columns: ["id"],
            [1f]));
        catalog.Add(CreateProvider("inner_table",
            columns: ["ref_id", "val"],
            [1f, 10f],
            [1f, 20f]));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ExecuteQueryAsync(
                "SELECT outer_table.id, " +
                "(SELECT val FROM inner_table WHERE inner_table.ref_id = outer_table.id) AS lookup " +
                "FROM outer_table",
                catalog));

        Assert.Contains("more than one row", exception.Message);
    }

    // ─────────────── Decorrelated scalar subqueries ───────────────

    /// <summary>
    /// Correlated scalar subquery with MAX aggregate is decorrelated into a GROUP BY
    /// LEFT JOIN, producing the same results as per-row execution.
    /// </summary>
    [Fact]
    public async Task DecorrelatedSubquery_Max_ProducesCorrectResults()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("data",
            columns: ["id", "name"],
            [1f, "alice"],
            [2f, "bob"],
            [3f, "carol"]));
        catalog.Add(CreateProvider("lookup",
            columns: ["ref_id", "weight"],
            [1f, 10f],
            [1f, 30f],
            [2f, 20f],
            [2f, 50f],
            [3f, 5f]));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT data.id, data.name, " +
            "(SELECT MAX(weight) FROM lookup WHERE lookup.ref_id = data.id) AS max_weight " +
            "FROM data",
            catalog);

        Assert.Equal(3, results.Count);
        Assert.Equal(30f, results[0]["max_weight"].AsFloat32());
        Assert.Equal(50f, results[1]["max_weight"].AsFloat32());
        Assert.Equal(5f, results[2]["max_weight"].AsFloat32());
    }

    /// <summary>
    /// Correlated scalar subquery with MIN aggregate is decorrelated correctly.
    /// </summary>
    [Fact]
    public async Task DecorrelatedSubquery_Min_ProducesCorrectResults()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("data",
            columns: ["id", "name"],
            [1f, "alice"],
            [2f, "bob"]));
        catalog.Add(CreateProvider("scores",
            columns: ["ref_id", "score"],
            [1f, 90f],
            [1f, 70f],
            [2f, 85f],
            [2f, 60f]));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT data.id, " +
            "(SELECT MIN(score) FROM scores WHERE scores.ref_id = data.id) AS min_score " +
            "FROM data",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal(70f, results[0]["min_score"].AsFloat32());
        Assert.Equal(60f, results[1]["min_score"].AsFloat32());
    }

    /// <summary>
    /// Correlated scalar subquery with SUM aggregate is decorrelated correctly.
    /// </summary>
    [Fact]
    public async Task DecorrelatedSubquery_Sum_ProducesCorrectResults()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("data",
            columns: ["id", "name"],
            [1f, "alice"],
            [2f, "bob"]));
        catalog.Add(CreateProvider("amounts",
            columns: ["ref_id", "amount"],
            [1f, 100f],
            [1f, 200f],
            [2f, 50f]));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT data.id, " +
            "(SELECT SUM(amount) FROM amounts WHERE amounts.ref_id = data.id) AS total " +
            "FROM data",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal(300f, results[0]["total"].AsFloat32());
        Assert.Equal(50f, results[1]["total"].AsFloat32());
    }

    /// <summary>
    /// Correlated scalar subquery with AVG aggregate is decorrelated correctly.
    /// </summary>
    [Fact]
    public async Task DecorrelatedSubquery_Avg_ProducesCorrectResults()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("data",
            columns: ["id", "name"],
            [1f, "alice"],
            [2f, "bob"]));
        catalog.Add(CreateProvider("scores",
            columns: ["ref_id", "score"],
            [1f, 80f],
            [1f, 100f],
            [2f, 60f],
            [2f, 40f]));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT data.id, " +
            "(SELECT AVG(score) FROM scores WHERE scores.ref_id = data.id) AS avg_score " +
            "FROM data",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal(90.0, results[0]["avg_score"].AsFloat64(), 0.01);
        Assert.Equal(50.0, results[1]["avg_score"].AsFloat64(), 0.01);
    }

    /// <summary>
    /// Decorrelated COUNT returns 0 (not NULL) for outer rows with no matching inner rows,
    /// preserving SQL COUNT semantics despite LEFT JOIN producing NULLs.
    /// </summary>
    [Fact]
    public async Task DecorrelatedSubquery_Count_ReturnsZeroForNoMatch()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("data",
            columns: ["id"],
            [1f],
            [2f],
            [99f]));
        catalog.Add(CreateProvider("items",
            columns: ["ref_id", "val"],
            [1f, 10f],
            [1f, 20f],
            [2f, 30f]));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT data.id, " +
            "(SELECT COUNT(val) FROM items WHERE items.ref_id = data.id) AS item_count " +
            "FROM data",
            catalog);

        Assert.Equal(3, results.Count);
        Assert.Equal(2L, results[0]["item_count"].ToInt64());
        Assert.Equal(1L, results[1]["item_count"].ToInt64());
        Assert.Equal(0L, results[2]["item_count"].ToInt64());
    }

    /// <summary>
    /// Non-COUNT aggregates (MAX, SUM, etc.) return NULL for outer rows with no
    /// matching inner rows, consistent with SQL semantics.
    /// </summary>
    [Fact]
    public async Task DecorrelatedSubquery_MaxNoMatch_ReturnsNull()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("data",
            columns: ["id"],
            [1f],
            [99f]));
        catalog.Add(CreateProvider("items",
            columns: ["ref_id", "val"],
            [1f, 42f]));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT data.id, " +
            "(SELECT MAX(val) FROM items WHERE items.ref_id = data.id) AS max_val " +
            "FROM data",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal(42f, results[0]["max_val"].AsFloat32());
        Assert.True(results[1]["max_val"].IsNull);
    }

    /// <summary>
    /// Multi-key correlation (composite join key) is decorrelated with
    /// a composite GROUP BY.
    /// </summary>
    [Fact]
    public async Task DecorrelatedSubquery_MultiKeyCorrelation_GroupsByAllKeys()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("data",
            columns: ["id", "region"],
            [1f, "east"],
            [1f, "west"],
            [2f, "east"]));
        catalog.Add(CreateProvider("metrics",
            columns: ["ref_id", "ref_region", "value"],
            [1f, "east", 10f],
            [1f, "east", 20f],
            [1f, "west", 5f],
            [2f, "east", 100f]));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT data.id, data.region, " +
            "(SELECT SUM(value) FROM metrics " +
            "WHERE metrics.ref_id = data.id AND metrics.ref_region = data.region) AS total " +
            "FROM data",
            catalog);

        Assert.Equal(3, results.Count);
        Assert.Equal(30f, results[0]["total"].AsFloat32());   // id=1, east: 10+20
        Assert.Equal(5f, results[1]["total"].AsFloat32());    // id=1, west: 5
        Assert.Equal(100f, results[2]["total"].AsFloat32());  // id=2, east: 100
    }

    /// <summary>
    /// A correlated subquery with non-equality correlation (e.g., greater-than) cannot
    /// be decorrelated and falls back to per-row execution without error.
    /// </summary>
    [Fact]
    public async Task DecorrelatedSubquery_InequalityCorrelation_FallsBackToPerRow()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("data",
            columns: ["id"],
            [10f],
            [20f]));
        catalog.Add(CreateProvider("items",
            columns: ["ref_id", "val"],
            [5f, 1f],
            [15f, 2f],
            [25f, 3f]));

        // ref_id < data.id: for id=10, matches ref_id=5 (count=1). For id=20, matches ref_id=5,15 (count=2).
        List<Row> results = await ExecuteQueryAsync(
            "SELECT data.id, " +
            "(SELECT COUNT(val) FROM items WHERE items.ref_id < data.id) AS cnt " +
            "FROM data",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal(1L, results[0]["cnt"].AsInt64());
        Assert.Equal(2L, results[1]["cnt"].AsInt64());
    }

    /// <summary>
    /// A correlated subquery selecting a non-aggregate expression (plain column) cannot
    /// be decorrelated and falls back correctly.
    /// </summary>
    [Fact]
    public async Task DecorrelatedSubquery_NonAggregate_FallsBackToPerRow()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("outer_table",
            columns: ["id"],
            [1f]));
        catalog.Add(CreateProvider("inner_table",
            columns: ["ref_id", "val"],
            [1f, 42f]));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT outer_table.id, " +
            "(SELECT val FROM inner_table WHERE inner_table.ref_id = outer_table.id) AS lookup " +
            "FROM outer_table",
            catalog);

        Assert.Single(results);
        Assert.Equal(42f, results[0]["lookup"].AsFloat32());
    }

    /// <summary>
    /// A subquery with existing GROUP BY is not eligible for decorrelation and
    /// falls back to per-row execution.
    /// </summary>
    [Fact]
    public async Task DecorrelatedSubquery_ExistingGroupBy_FallsBackToPerRow()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("outer_table",
            columns: ["id"],
            [1f]));
        catalog.Add(CreateProvider("inner_table",
            columns: ["ref_id", "category", "val"],
            [1f, "a", 10f],
            [1f, "a", 20f]));

        // Has GROUP BY already → cannot decorrelate. But still returns correct result.
        // SUM(val) grouped by category where ref_id=1 → category 'a' has 30.
        // Only one group, so scalar subquery returns 30.
        List<Row> results = await ExecuteQueryAsync(
            "SELECT outer_table.id, " +
            "(SELECT SUM(val) FROM inner_table WHERE inner_table.ref_id = outer_table.id GROUP BY category) AS total " +
            "FROM outer_table",
            catalog);

        Assert.Single(results);
        Assert.Equal(30f, results[0]["total"].AsFloat32());
    }

    /// <summary>
    /// A query containing both a decorrelatable subquery (MAX) and a non-decorrelatable
    /// one (plain column lookup) in the same SELECT list. Both produce correct results.
    /// </summary>
    [Fact]
    public async Task DecorrelatedSubquery_MixedDecorrelatableAndNot_BothCorrect()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("data",
            columns: ["id", "name"],
            [1f, "alice"],
            [2f, "bob"]));
        catalog.Add(CreateProvider("scores",
            columns: ["ref_id", "score"],
            [1f, 80f],
            [1f, 95f],
            [2f, 60f]));
        catalog.Add(CreateProvider("labels",
            columns: ["ref_id", "label"],
            [1f, "senior"],
            [2f, "junior"]));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT data.id, " +
            "(SELECT MAX(score) FROM scores WHERE scores.ref_id = data.id) AS best, " +
            "(SELECT label FROM labels WHERE labels.ref_id = data.id) AS title " +
            "FROM data",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal(95f, results[0]["best"].AsFloat32());
        Assert.Equal("senior", results[0]["title"].AsString());
        Assert.Equal(60f, results[1]["best"].AsFloat32());
        Assert.Equal("junior", results[1]["title"].AsString());
    }

    /// <summary>
    /// Decorrelated subquery with additional non-correlated WHERE predicates.
    /// The non-correlated filter is preserved in the derived table's WHERE clause.
    /// </summary>
    [Fact]
    public async Task DecorrelatedSubquery_WithNonCorrelatedFilter_PreservesFilter()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("data",
            columns: ["id"],
            [1f],
            [2f]));
        catalog.Add(CreateProvider("items",
            columns: ["ref_id", "active", "val"],
            [1f, 1f, 10f],
            [1f, 0f, 999f],
            [2f, 1f, 20f]));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT data.id, " +
            "(SELECT SUM(val) FROM items WHERE items.ref_id = data.id AND items.active = 1) AS active_total " +
            "FROM data",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal(10f, results[0]["active_total"].AsFloat32());  // active=0 row excluded
        Assert.Equal(20f, results[1]["active_total"].AsFloat32());
    }

    // ─────────────── Statement without subqueries (regression) ───────────────

    /// <summary>
    /// A query with no subqueries should still work through PlanWithSubqueriesAsync
    /// without any behavioral change.
    /// </summary>
    [Fact]
    public async Task NoSubquery_WorksThroughSubqueryPath()
    {
        TableCatalog catalog = CreateCatalog("data",
            columns: ["x"],
            [1f],
            [2f]);
        List<Row> results = await ExecuteQueryAsync("SELECT x FROM data WHERE x > 1", catalog);

        Assert.Single(results);
        Assert.Equal(2f, results[0]["x"].AsFloat32());
    }

}
