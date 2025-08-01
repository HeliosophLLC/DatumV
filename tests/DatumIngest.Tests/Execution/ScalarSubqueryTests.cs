using System.Runtime.CompilerServices;
using DatumIngest.Catalog;
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
public sealed class ScalarSubqueryTests
{
    private static readonly FunctionRegistry DefaultFunctions = FunctionRegistry.CreateDefault();

    // ─────────────── Uncorrelated scalar subqueries ───────────────

    /// <summary>
    /// Uncorrelated scalar subquery in WHERE clause: the subquery is constant-folded
    /// at plan time and the filter uses the resulting literal.
    /// </summary>
    [Fact]
    public async Task UncorrelatedSubquery_InWhere_FiltersCorrectly()
    {
        Row[] employees =
        [
            MakeRow(("name", DataValue.FromString("alice")), ("salary", DataValue.FromScalar(50_000f))),
            MakeRow(("name", DataValue.FromString("bob")), ("salary", DataValue.FromScalar(80_000f))),
            MakeRow(("name", DataValue.FromString("carol")), ("salary", DataValue.FromScalar(60_000f))),
        ];

        Row[] thresholds =
        [
            MakeRow(("min_salary", DataValue.FromScalar(55_000f))),
        ];

        TableCatalog catalog = CreateCatalog(("employees", employees), ("thresholds", thresholds));
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
        Row[] items =
        [
            MakeRow(("name", DataValue.FromString("widget")), ("price", DataValue.FromScalar(10f))),
            MakeRow(("name", DataValue.FromString("gadget")), ("price", DataValue.FromScalar(20f))),
        ];

        Row[] settings =
        [
            MakeRow(("tax_rate", DataValue.FromScalar(0.1f))),
        ];

        TableCatalog catalog = CreateCatalog(("items", items), ("settings", settings));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT name, price * (SELECT tax_rate FROM settings) AS tax FROM items",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal(1f, results[0]["tax"].AsScalar(), 0.001f);
        Assert.Equal(2f, results[1]["tax"].AsScalar(), 0.001f);
    }

    /// <summary>
    /// An uncorrelated subquery that returns zero rows produces SQL NULL.
    /// </summary>
    [Fact]
    public async Task UncorrelatedSubquery_EmptyResult_ReturnsNull()
    {
        Row[] data =
        [
            MakeRow(("x", DataValue.FromScalar(1f))),
        ];

        Row[] empty = [];

        TableCatalog catalog = CreateCatalog(("data", data), ("empty_table", empty));

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
        Row[] data =
        [
            MakeRow(("x", DataValue.FromScalar(1f))),
        ];

        Row[] multi =
        [
            MakeRow(("val", DataValue.FromScalar(10f))),
            MakeRow(("val", DataValue.FromScalar(20f))),
        ];

        TableCatalog catalog = CreateCatalog(("data", data), ("multi", multi));

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
        Row[] data =
        [
            MakeRow(("x", DataValue.FromScalar(1f))),
        ];

        Row[] wide =
        [
            MakeRow(("a", DataValue.FromScalar(1f)), ("b", DataValue.FromScalar(2f))),
        ];

        TableCatalog catalog = CreateCatalog(("data", data), ("wide", wide));

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
        Row[] data =
        [
            MakeRow(("x", DataValue.FromScalar(5f))),
            MakeRow(("x", DataValue.FromScalar(15f))),
            MakeRow(("x", DataValue.FromScalar(25f))),
        ];

        Row[] lo = [MakeRow(("val", DataValue.FromScalar(10f)))];
        Row[] hi = [MakeRow(("val", DataValue.FromScalar(20f)))];

        TableCatalog catalog = CreateCatalog(("data", data), ("lo", lo), ("hi", hi));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT x FROM data WHERE x > (SELECT val FROM lo) AND x < (SELECT val FROM hi)",
            catalog);

        Assert.Single(results);
        Assert.Equal(15f, results[0]["x"].AsScalar());
    }

    // ─────────────── Correlated scalar subqueries ───────────────

    /// <summary>
    /// A correlated scalar subquery in WHERE: the inner query references an
    /// outer-scope table alias and is executed per outer row.
    /// </summary>
    [Fact]
    public async Task CorrelatedSubquery_InWhere_ExecutesPerRow()
    {
        Row[] orders =
        [
            MakeRow(("id", DataValue.FromScalar(1f)), ("total", DataValue.FromScalar(100f))),
            MakeRow(("id", DataValue.FromScalar(2f)), ("total", DataValue.FromScalar(200f))),
            MakeRow(("id", DataValue.FromScalar(3f)), ("total", DataValue.FromScalar(50f))),
        ];

        Row[] thresholds =
        [
            MakeRow(("order_id", DataValue.FromScalar(1f)), ("min_total", DataValue.FromScalar(150f))),
            MakeRow(("order_id", DataValue.FromScalar(2f)), ("min_total", DataValue.FromScalar(150f))),
            MakeRow(("order_id", DataValue.FromScalar(3f)), ("min_total", DataValue.FromScalar(150f))),
        ];

        TableCatalog catalog = CreateCatalog(("orders", orders), ("thresholds", thresholds));

        // For each order, check if its total exceeds the threshold for that order_id.
        // Only order 2 (total=200) exceeds its threshold (min_total=150).
        List<Row> results = await ExecuteQueryAsync(
            "SELECT orders.id FROM orders WHERE orders.total > " +
            "(SELECT min_total FROM thresholds WHERE thresholds.order_id = orders.id)",
            catalog);

        Assert.Single(results);
        Assert.Equal(2f, results[0]["id"].AsScalar());
    }

    /// <summary>
    /// A correlated scalar subquery in the SELECT list: computes a per-row
    /// derived value from another table.
    /// </summary>
    [Fact]
    public async Task CorrelatedSubquery_InSelect_ComputesPerRow()
    {
        Row[] products =
        [
            MakeRow(("id", DataValue.FromScalar(1f)), ("name", DataValue.FromString("widget"))),
            MakeRow(("id", DataValue.FromScalar(2f)), ("name", DataValue.FromString("gadget"))),
        ];

        Row[] prices =
        [
            MakeRow(("product_id", DataValue.FromScalar(1f)), ("price", DataValue.FromScalar(9.99f))),
            MakeRow(("product_id", DataValue.FromScalar(2f)), ("price", DataValue.FromScalar(19.99f))),
        ];

        TableCatalog catalog = CreateCatalog(("products", products), ("prices", prices));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT products.name, " +
            "(SELECT price FROM prices WHERE prices.product_id = products.id) AS price " +
            "FROM products",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal("widget", results[0]["name"].AsString());
        Assert.Equal(9.99f, results[0]["price"].AsScalar(), 0.01f);
        Assert.Equal("gadget", results[1]["name"].AsString());
        Assert.Equal(19.99f, results[1]["price"].AsScalar(), 0.01f);
    }

    /// <summary>
    /// A correlated subquery that matches no inner rows for a given outer row
    /// should produce NULL for that row.
    /// </summary>
    [Fact]
    public async Task CorrelatedSubquery_NoMatch_ReturnsNull()
    {
        Row[] outer =
        [
            MakeRow(("id", DataValue.FromScalar(1f))),
            MakeRow(("id", DataValue.FromScalar(99f))),
        ];

        Row[] inner =
        [
            MakeRow(("ref_id", DataValue.FromScalar(1f)), ("val", DataValue.FromScalar(42f))),
        ];

        TableCatalog catalog = CreateCatalog(("outer_table", outer), ("inner_table", inner));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT outer_table.id, " +
            "(SELECT val FROM inner_table WHERE inner_table.ref_id = outer_table.id) AS lookup " +
            "FROM outer_table",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal(42f, results[0]["lookup"].AsScalar());
        Assert.True(results[1]["lookup"].IsNull);
    }

    /// <summary>
    /// A correlated subquery that returns multiple rows for an outer row must throw.
    /// </summary>
    [Fact]
    public async Task CorrelatedSubquery_MultipleRows_Throws()
    {
        Row[] outer =
        [
            MakeRow(("id", DataValue.FromScalar(1f))),
        ];

        Row[] inner =
        [
            MakeRow(("ref_id", DataValue.FromScalar(1f)), ("val", DataValue.FromScalar(10f))),
            MakeRow(("ref_id", DataValue.FromScalar(1f)), ("val", DataValue.FromScalar(20f))),
        ];

        TableCatalog catalog = CreateCatalog(("outer_table", outer), ("inner_table", inner));

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
        Row[] data =
        [
            MakeRow(("id", DataValue.FromScalar(1f)), ("name", DataValue.FromString("alice"))),
            MakeRow(("id", DataValue.FromScalar(2f)), ("name", DataValue.FromString("bob"))),
            MakeRow(("id", DataValue.FromScalar(3f)), ("name", DataValue.FromString("carol"))),
        ];

        Row[] lookup =
        [
            MakeRow(("ref_id", DataValue.FromScalar(1f)), ("weight", DataValue.FromScalar(10f))),
            MakeRow(("ref_id", DataValue.FromScalar(1f)), ("weight", DataValue.FromScalar(30f))),
            MakeRow(("ref_id", DataValue.FromScalar(2f)), ("weight", DataValue.FromScalar(20f))),
            MakeRow(("ref_id", DataValue.FromScalar(2f)), ("weight", DataValue.FromScalar(50f))),
            MakeRow(("ref_id", DataValue.FromScalar(3f)), ("weight", DataValue.FromScalar(5f))),
        ];

        TableCatalog catalog = CreateCatalog(("data", data), ("lookup", lookup));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT data.id, data.name, " +
            "(SELECT MAX(weight) FROM lookup WHERE lookup.ref_id = data.id) AS max_weight " +
            "FROM data",
            catalog);

        Assert.Equal(3, results.Count);
        Assert.Equal(30f, results[0]["max_weight"].AsScalar());
        Assert.Equal(50f, results[1]["max_weight"].AsScalar());
        Assert.Equal(5f, results[2]["max_weight"].AsScalar());
    }

    /// <summary>
    /// Correlated scalar subquery with MIN aggregate is decorrelated correctly.
    /// </summary>
    [Fact]
    public async Task DecorrelatedSubquery_Min_ProducesCorrectResults()
    {
        Row[] data =
        [
            MakeRow(("id", DataValue.FromScalar(1f)), ("name", DataValue.FromString("alice"))),
            MakeRow(("id", DataValue.FromScalar(2f)), ("name", DataValue.FromString("bob"))),
        ];

        Row[] scores =
        [
            MakeRow(("ref_id", DataValue.FromScalar(1f)), ("score", DataValue.FromScalar(90f))),
            MakeRow(("ref_id", DataValue.FromScalar(1f)), ("score", DataValue.FromScalar(70f))),
            MakeRow(("ref_id", DataValue.FromScalar(2f)), ("score", DataValue.FromScalar(85f))),
            MakeRow(("ref_id", DataValue.FromScalar(2f)), ("score", DataValue.FromScalar(60f))),
        ];

        TableCatalog catalog = CreateCatalog(("data", data), ("scores", scores));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT data.id, " +
            "(SELECT MIN(score) FROM scores WHERE scores.ref_id = data.id) AS min_score " +
            "FROM data",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal(70f, results[0]["min_score"].AsScalar());
        Assert.Equal(60f, results[1]["min_score"].AsScalar());
    }

    /// <summary>
    /// Correlated scalar subquery with SUM aggregate is decorrelated correctly.
    /// </summary>
    [Fact]
    public async Task DecorrelatedSubquery_Sum_ProducesCorrectResults()
    {
        Row[] data =
        [
            MakeRow(("id", DataValue.FromScalar(1f)), ("name", DataValue.FromString("alice"))),
            MakeRow(("id", DataValue.FromScalar(2f)), ("name", DataValue.FromString("bob"))),
        ];

        Row[] amounts =
        [
            MakeRow(("ref_id", DataValue.FromScalar(1f)), ("amount", DataValue.FromScalar(100f))),
            MakeRow(("ref_id", DataValue.FromScalar(1f)), ("amount", DataValue.FromScalar(200f))),
            MakeRow(("ref_id", DataValue.FromScalar(2f)), ("amount", DataValue.FromScalar(50f))),
        ];

        TableCatalog catalog = CreateCatalog(("data", data), ("amounts", amounts));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT data.id, " +
            "(SELECT SUM(amount) FROM amounts WHERE amounts.ref_id = data.id) AS total " +
            "FROM data",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal(300f, results[0]["total"].AsScalar());
        Assert.Equal(50f, results[1]["total"].AsScalar());
    }

    /// <summary>
    /// Correlated scalar subquery with AVG aggregate is decorrelated correctly.
    /// </summary>
    [Fact]
    public async Task DecorrelatedSubquery_Avg_ProducesCorrectResults()
    {
        Row[] data =
        [
            MakeRow(("id", DataValue.FromScalar(1f)), ("name", DataValue.FromString("alice"))),
            MakeRow(("id", DataValue.FromScalar(2f)), ("name", DataValue.FromString("bob"))),
        ];

        Row[] scores =
        [
            MakeRow(("ref_id", DataValue.FromScalar(1f)), ("score", DataValue.FromScalar(80f))),
            MakeRow(("ref_id", DataValue.FromScalar(1f)), ("score", DataValue.FromScalar(100f))),
            MakeRow(("ref_id", DataValue.FromScalar(2f)), ("score", DataValue.FromScalar(60f))),
            MakeRow(("ref_id", DataValue.FromScalar(2f)), ("score", DataValue.FromScalar(40f))),
        ];

        TableCatalog catalog = CreateCatalog(("data", data), ("scores", scores));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT data.id, " +
            "(SELECT AVG(score) FROM scores WHERE scores.ref_id = data.id) AS avg_score " +
            "FROM data",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal(90f, results[0]["avg_score"].AsScalar(), 0.01f);
        Assert.Equal(50f, results[1]["avg_score"].AsScalar(), 0.01f);
    }

    /// <summary>
    /// Decorrelated COUNT returns 0 (not NULL) for outer rows with no matching inner rows,
    /// preserving SQL COUNT semantics despite LEFT JOIN producing NULLs.
    /// </summary>
    [Fact]
    public async Task DecorrelatedSubquery_Count_ReturnsZeroForNoMatch()
    {
        Row[] data =
        [
            MakeRow(("id", DataValue.FromScalar(1f))),
            MakeRow(("id", DataValue.FromScalar(2f))),
            MakeRow(("id", DataValue.FromScalar(99f))),
        ];

        Row[] items =
        [
            MakeRow(("ref_id", DataValue.FromScalar(1f)), ("val", DataValue.FromScalar(10f))),
            MakeRow(("ref_id", DataValue.FromScalar(1f)), ("val", DataValue.FromScalar(20f))),
            MakeRow(("ref_id", DataValue.FromScalar(2f)), ("val", DataValue.FromScalar(30f))),
        ];

        TableCatalog catalog = CreateCatalog(("data", data), ("items", items));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT data.id, " +
            "(SELECT COUNT(val) FROM items WHERE items.ref_id = data.id) AS item_count " +
            "FROM data",
            catalog);

        Assert.Equal(3, results.Count);
        Assert.Equal(2f, results[0]["item_count"].AsScalar());
        Assert.Equal(1f, results[1]["item_count"].AsScalar());
        Assert.Equal(0f, results[2]["item_count"].AsScalar());
    }

    /// <summary>
    /// Non-COUNT aggregates (MAX, SUM, etc.) return NULL for outer rows with no
    /// matching inner rows, consistent with SQL semantics.
    /// </summary>
    [Fact]
    public async Task DecorrelatedSubquery_MaxNoMatch_ReturnsNull()
    {
        Row[] data =
        [
            MakeRow(("id", DataValue.FromScalar(1f))),
            MakeRow(("id", DataValue.FromScalar(99f))),
        ];

        Row[] items =
        [
            MakeRow(("ref_id", DataValue.FromScalar(1f)), ("val", DataValue.FromScalar(42f))),
        ];

        TableCatalog catalog = CreateCatalog(("data", data), ("items", items));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT data.id, " +
            "(SELECT MAX(val) FROM items WHERE items.ref_id = data.id) AS max_val " +
            "FROM data",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal(42f, results[0]["max_val"].AsScalar());
        Assert.True(results[1]["max_val"].IsNull);
    }

    /// <summary>
    /// Multi-key correlation (composite join key) is decorrelated with
    /// a composite GROUP BY.
    /// </summary>
    [Fact]
    public async Task DecorrelatedSubquery_MultiKeyCorrelation_GroupsByAllKeys()
    {
        Row[] data =
        [
            MakeRow(("id", DataValue.FromScalar(1f)), ("region", DataValue.FromString("east"))),
            MakeRow(("id", DataValue.FromScalar(1f)), ("region", DataValue.FromString("west"))),
            MakeRow(("id", DataValue.FromScalar(2f)), ("region", DataValue.FromString("east"))),
        ];

        Row[] metrics =
        [
            MakeRow(("ref_id", DataValue.FromScalar(1f)), ("ref_region", DataValue.FromString("east")),
                ("value", DataValue.FromScalar(10f))),
            MakeRow(("ref_id", DataValue.FromScalar(1f)), ("ref_region", DataValue.FromString("east")),
                ("value", DataValue.FromScalar(20f))),
            MakeRow(("ref_id", DataValue.FromScalar(1f)), ("ref_region", DataValue.FromString("west")),
                ("value", DataValue.FromScalar(5f))),
            MakeRow(("ref_id", DataValue.FromScalar(2f)), ("ref_region", DataValue.FromString("east")),
                ("value", DataValue.FromScalar(100f))),
        ];

        TableCatalog catalog = CreateCatalog(("data", data), ("metrics", metrics));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT data.id, data.region, " +
            "(SELECT SUM(value) FROM metrics " +
            "WHERE metrics.ref_id = data.id AND metrics.ref_region = data.region) AS total " +
            "FROM data",
            catalog);

        Assert.Equal(3, results.Count);
        Assert.Equal(30f, results[0]["total"].AsScalar());   // id=1, east: 10+20
        Assert.Equal(5f, results[1]["total"].AsScalar());    // id=1, west: 5
        Assert.Equal(100f, results[2]["total"].AsScalar());  // id=2, east: 100
    }

    /// <summary>
    /// A correlated subquery with non-equality correlation (e.g., greater-than) cannot
    /// be decorrelated and falls back to per-row execution without error.
    /// </summary>
    [Fact]
    public async Task DecorrelatedSubquery_InequalityCorrelation_FallsBackToPerRow()
    {
        Row[] data =
        [
            MakeRow(("id", DataValue.FromScalar(10f))),
            MakeRow(("id", DataValue.FromScalar(20f))),
        ];

        Row[] items =
        [
            MakeRow(("ref_id", DataValue.FromScalar(5f)), ("val", DataValue.FromScalar(1f))),
            MakeRow(("ref_id", DataValue.FromScalar(15f)), ("val", DataValue.FromScalar(2f))),
            MakeRow(("ref_id", DataValue.FromScalar(25f)), ("val", DataValue.FromScalar(3f))),
        ];

        TableCatalog catalog = CreateCatalog(("data", data), ("items", items));

        // ref_id < data.id: for id=10, matches ref_id=5 (count=1). For id=20, matches ref_id=5,15 (count=2).
        List<Row> results = await ExecuteQueryAsync(
            "SELECT data.id, " +
            "(SELECT COUNT(val) FROM items WHERE items.ref_id < data.id) AS cnt " +
            "FROM data",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal(1f, results[0]["cnt"].AsScalar());
        Assert.Equal(2f, results[1]["cnt"].AsScalar());
    }

    /// <summary>
    /// A correlated subquery selecting a non-aggregate expression (plain column) cannot
    /// be decorrelated and falls back correctly.
    /// </summary>
    [Fact]
    public async Task DecorrelatedSubquery_NonAggregate_FallsBackToPerRow()
    {
        Row[] outer =
        [
            MakeRow(("id", DataValue.FromScalar(1f))),
        ];

        Row[] inner =
        [
            MakeRow(("ref_id", DataValue.FromScalar(1f)), ("val", DataValue.FromScalar(42f))),
        ];

        TableCatalog catalog = CreateCatalog(("outer_table", outer), ("inner_table", inner));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT outer_table.id, " +
            "(SELECT val FROM inner_table WHERE inner_table.ref_id = outer_table.id) AS lookup " +
            "FROM outer_table",
            catalog);

        Assert.Single(results);
        Assert.Equal(42f, results[0]["lookup"].AsScalar());
    }

    /// <summary>
    /// A subquery with existing GROUP BY is not eligible for decorrelation and
    /// falls back to per-row execution.
    /// </summary>
    [Fact]
    public async Task DecorrelatedSubquery_ExistingGroupBy_FallsBackToPerRow()
    {
        Row[] outer =
        [
            MakeRow(("id", DataValue.FromScalar(1f))),
        ];

        Row[] inner =
        [
            MakeRow(("ref_id", DataValue.FromScalar(1f)), ("category", DataValue.FromString("a")),
                ("val", DataValue.FromScalar(10f))),
            MakeRow(("ref_id", DataValue.FromScalar(1f)), ("category", DataValue.FromString("a")),
                ("val", DataValue.FromScalar(20f))),
        ];

        TableCatalog catalog = CreateCatalog(("outer_table", outer), ("inner_table", inner));

        // Has GROUP BY already → cannot decorrelate. But still returns correct result.
        // SUM(val) grouped by category where ref_id=1 → category 'a' has 30.
        // Only one group, so scalar subquery returns 30.
        List<Row> results = await ExecuteQueryAsync(
            "SELECT outer_table.id, " +
            "(SELECT SUM(val) FROM inner_table WHERE inner_table.ref_id = outer_table.id GROUP BY category) AS total " +
            "FROM outer_table",
            catalog);

        Assert.Single(results);
        Assert.Equal(30f, results[0]["total"].AsScalar());
    }

    /// <summary>
    /// A query containing both a decorrelatable subquery (MAX) and a non-decorrelatable
    /// one (plain column lookup) in the same SELECT list. Both produce correct results.
    /// </summary>
    [Fact]
    public async Task DecorrelatedSubquery_MixedDecorrelatableAndNot_BothCorrect()
    {
        Row[] data =
        [
            MakeRow(("id", DataValue.FromScalar(1f)), ("name", DataValue.FromString("alice"))),
            MakeRow(("id", DataValue.FromScalar(2f)), ("name", DataValue.FromString("bob"))),
        ];

        Row[] scores =
        [
            MakeRow(("ref_id", DataValue.FromScalar(1f)), ("score", DataValue.FromScalar(80f))),
            MakeRow(("ref_id", DataValue.FromScalar(1f)), ("score", DataValue.FromScalar(95f))),
            MakeRow(("ref_id", DataValue.FromScalar(2f)), ("score", DataValue.FromScalar(60f))),
        ];

        Row[] labels =
        [
            MakeRow(("ref_id", DataValue.FromScalar(1f)), ("label", DataValue.FromString("senior"))),
            MakeRow(("ref_id", DataValue.FromScalar(2f)), ("label", DataValue.FromString("junior"))),
        ];

        TableCatalog catalog = CreateCatalog(("data", data), ("scores", scores), ("labels", labels));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT data.id, " +
            "(SELECT MAX(score) FROM scores WHERE scores.ref_id = data.id) AS best, " +
            "(SELECT label FROM labels WHERE labels.ref_id = data.id) AS title " +
            "FROM data",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal(95f, results[0]["best"].AsScalar());
        Assert.Equal("senior", results[0]["title"].AsString());
        Assert.Equal(60f, results[1]["best"].AsScalar());
        Assert.Equal("junior", results[1]["title"].AsString());
    }

    /// <summary>
    /// Decorrelated subquery with additional non-correlated WHERE predicates.
    /// The non-correlated filter is preserved in the derived table's WHERE clause.
    /// </summary>
    [Fact]
    public async Task DecorrelatedSubquery_WithNonCorrelatedFilter_PreservesFilter()
    {
        Row[] data =
        [
            MakeRow(("id", DataValue.FromScalar(1f))),
            MakeRow(("id", DataValue.FromScalar(2f))),
        ];

        Row[] items =
        [
            MakeRow(("ref_id", DataValue.FromScalar(1f)), ("active", DataValue.FromScalar(1f)),
                ("val", DataValue.FromScalar(10f))),
            MakeRow(("ref_id", DataValue.FromScalar(1f)), ("active", DataValue.FromScalar(0f)),
                ("val", DataValue.FromScalar(999f))),
            MakeRow(("ref_id", DataValue.FromScalar(2f)), ("active", DataValue.FromScalar(1f)),
                ("val", DataValue.FromScalar(20f))),
        ];

        TableCatalog catalog = CreateCatalog(("data", data), ("items", items));
        List<Row> results = await ExecuteQueryAsync(
            "SELECT data.id, " +
            "(SELECT SUM(val) FROM items WHERE items.ref_id = data.id AND items.active = 1) AS active_total " +
            "FROM data",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal(10f, results[0]["active_total"].AsScalar());  // active=0 row excluded
        Assert.Equal(20f, results[1]["active_total"].AsScalar());
    }

    // ─────────────── Statement without subqueries (regression) ───────────────

    /// <summary>
    /// A query with no subqueries should still work through PlanWithSubqueriesAsync
    /// without any behavioral change.
    /// </summary>
    [Fact]
    public async Task NoSubquery_WorksThroughSubqueryPath()
    {
        Row[] data =
        [
            MakeRow(("x", DataValue.FromScalar(1f))),
            MakeRow(("x", DataValue.FromScalar(2f))),
        ];

        TableCatalog catalog = CreateCatalog(("data", data));
        List<Row> results = await ExecuteQueryAsync("SELECT x FROM data WHERE x > 1", catalog);

        Assert.Single(results);
        Assert.Equal(2f, results[0]["x"].AsScalar());
    }

    // ─────────────── Helper infrastructure ───────────────

    private static Row MakeRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = columns.Select(c => c.Name).ToArray();
        DataValue[] values = columns.Select(c => c.Value).ToArray();
        return new Row(names, values);
    }

    private static TableCatalog CreateCatalog(params (string Name, Row[] Rows)[] tables)
    {
        TableCatalog catalog = new();

        foreach ((string name, Row[] rows) in tables)
        {
            InMemoryTableProvider provider = new(rows);
            catalog.RegisterProvider(name, () => provider);
            catalog.Register(new TableDescriptor(name, name, "", new Dictionary<string, string>()));
        }

        return catalog;
    }

    private static async Task<List<Row>> ExecuteQueryAsync(string sql, TableCatalog catalog)
    {
        SelectStatement statement = SqlParser.Parse(sql);
        QueryPlanner planner = new(catalog, DefaultFunctions);

        ExecutionContext context = new(
            CancellationToken.None,
            DefaultFunctions,
            catalog);

        IQueryOperator plan = await planner.PlanWithSubqueriesAsync(statement, context, CancellationToken.None);

        List<Row> rows = [];
        await foreach (Row row in plan.ExecuteAsync(context))
        {
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// Simple in-memory provider for testing. Yields pre-built rows regardless
    /// of the descriptor path.
    /// </summary>
    private sealed class InMemoryTableProvider : ITableProvider
    {
        private readonly Row[] _rows;

        public InMemoryTableProvider(Row[] rows)
        {
            _rows = rows;
        }

        /// <inheritdoc/>
        public Task<Schema> GetSchemaAsync(TableDescriptor descriptor, CancellationToken cancellationToken)
        {
            if (_rows.Length == 0)
            {
                return Task.FromResult(new Schema([new ColumnInfo("empty", DataKind.String, nullable: true)]));
            }

            List<ColumnInfo> columns = [];
            foreach (string name in _rows[0].ColumnNames)
            {
                columns.Add(new ColumnInfo(name, _rows[0][name].Kind, nullable: true));
            }

            return Task.FromResult(new Schema(columns));
        }

        /// <inheritdoc/>
        public Task<ProviderCapabilities> GetCapabilitiesAsync(
            TableDescriptor descriptor, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ProviderCapabilities(
                EstimatedRowCount: _rows.Length,
                EstimatedRowSizeBytes: null,
                SupportsSeek: false,
                ColumnCosts: new Dictionary<string, ColumnCost>()));
        }

        /// <inheritdoc/>
        public async IAsyncEnumerable<Row> OpenAsync(
            TableDescriptor descriptor,
            IReadOnlySet<string>? requiredColumns,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (Row row in _rows)
            {
                yield return row;
            }

            await Task.CompletedTask;
        }
    }
}
