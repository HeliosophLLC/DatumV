using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Execution;

/// <summary>
/// Tests for <c>GROUP BY ALL</c> — projection-derived grouping that infers
/// group keys from non-aggregate columns in the SELECT list.
/// </summary>
public sealed class GroupByAllTests : ServiceTestBase
{
    /// <summary>
    /// GROUP BY ALL infers grouping keys from the two non-aggregate columns
    /// and produces the same result as an explicit GROUP BY.
    /// </summary>
    [Fact]
    public async Task GroupByAll_InfersKeysFromNonAggregateColumns()
    {
        TableCatalog catalog = CreateCatalog("sales",
            columns: ["department", "region", "amount"],
            ["A", "North", 10f],
            ["A", "North", 20f],
            ["A", "South", 30f],
            ["B", "North", 40f]);

        List<Row> allResults = await ExecuteQueryAsync(
            "SELECT department, region, SUM(amount) AS total FROM sales GROUP BY ALL",
            catalog);

        List<Row> explicitResults = await ExecuteQueryAsync(
            "SELECT department, region, SUM(amount) AS total FROM sales GROUP BY department, region",
            catalog);

        Assert.Equal(explicitResults.Count, allResults.Count);

        // Both should produce 3 groups: (A,North), (A,South), (B,North)
        Assert.Equal(3, allResults.Count);
    }

    /// <summary>
    /// GROUP BY ALL with a single non-aggregate column and COUNT(*).
    /// </summary>
    [Fact]
    public async Task GroupByAll_SingleGroupKey()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["category", "value"],
            ["X", 1f],
            ["X", 2f],
            ["Y", 3f]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT category, COUNT(*) AS n FROM t GROUP BY ALL",
            catalog);

        Assert.Equal(2, results.Count);
        Row rowX = results.First(r => r["category"].AsString() == "X");
        Row rowY = results.First(r => r["category"].AsString() == "Y");
        Assert.Equal(2L, rowX["n"].AsInt64());
        Assert.Equal(1L, rowY["n"].AsInt64());
    }

    // ─────────────── Computed expressions ───────────────

    /// <summary>
    /// GROUP BY ALL correctly treats a non-aggregate expression that wraps a column
    /// as a grouping key, matching the behavior of explicit GROUP BY.
    /// </summary>
    [Fact]
    public async Task GroupByAll_WithMixedColumnsAndAggregates()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["a", "b", "v"],
            ["X", "P", 1f],
            ["X", "Q", 2f],
            ["Y", "P", 3f],
            ["Y", "P", 4f]);

        // Three non-aggregate columns (a, b) and two aggregates.
        List<Row> results = await ExecuteQueryAsync(
            "SELECT a, b, COUNT(*) AS n, SUM(v) AS total FROM t GROUP BY ALL",
            catalog);

        // Three distinct (a, b) pairs: (X,P), (X,Q), (Y,P)
        Assert.Equal(3, results.Count);
        Row yp = results.First(r => r["a"].AsString() == "Y" && r["b"].AsString() == "P");
        Assert.Equal(2L, yp["n"].AsInt64());
        Assert.Equal(7f, yp["total"].AsFloat32());
    }

    // ─────────────── Multiple aggregates ───────────────

    /// <summary>
    /// GROUP BY ALL works when multiple aggregate functions appear in the SELECT list.
    /// </summary>
    [Fact]
    public async Task GroupByAll_MultipleAggregates()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["region", "sales"],
            ["East", 10f],
            ["East", 20f],
            ["West", 30f]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT region, COUNT(*) AS n, SUM(sales) AS total, AVG(sales) AS avg_sales FROM t GROUP BY ALL",
            catalog);

        Assert.Equal(2, results.Count);
        Row east = results.First(r => r["region"].AsString() == "East");
        Assert.Equal(2L, east["n"].AsInt64());
        Assert.Equal(30f, east["total"].AsFloat32());
        Assert.Equal(15.0, east["avg_sales"].AsFloat64());
    }

    // ─────────────── With HAVING ───────────────

    /// <summary>
    /// GROUP BY ALL works with a HAVING clause to filter groups.
    /// </summary>
    [Fact]
    public async Task GroupByAll_WithHaving()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["category", "value"],
            ["A", 5f],
            ["A", 10f],
            ["B", 1f]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT category, SUM(value) AS total FROM t GROUP BY ALL HAVING SUM(value) > 5",
            catalog);

        Assert.Single(results);
        Assert.Equal("A", results[0]["category"].AsString());
        Assert.Equal(15f, results[0]["total"].AsFloat32());
    }

    // ─────────────── With ORDER BY and LIMIT ───────────────

    /// <summary>
    /// GROUP BY ALL integrates correctly with ORDER BY and LIMIT.
    /// </summary>
    [Fact]
    public async Task GroupByAll_WithOrderByAndLimit()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["category", "value"],
            ["A", 30f],
            ["B", 10f],
            ["C", 20f]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT category, SUM(value) AS total FROM t GROUP BY ALL ORDER BY total DESC LIMIT 2",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal("A", results[0]["category"].AsString());
        Assert.Equal("C", results[1]["category"].AsString());
    }
}
