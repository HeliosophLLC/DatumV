using DatumIngest.Catalog;
using DatumIngest.Model;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests that aggregate function calls (e.g. <c>COUNT(*)</c>, <c>SUM(x)</c>)
/// appearing in <c>ORDER BY</c> clauses are correctly lifted into the
/// <see cref="DatumIngest.Execution.Operators.GroupByOperator"/>'s aggregate
/// columns by the planner and rewritten as column references — so the
/// downstream <see cref="DatumIngest.Execution.Operators.OrderByOperator"/>
/// can sort by the precomputed group value rather than (incorrectly)
/// attempting to evaluate the aggregate per row.
/// </summary>
public sealed class OrderByAggregateTests : ServiceTestBase
{
    private TableCatalog CategoryCatalog() => CreateCatalog(
        "t",
        columns: ["category"],
        new object?[] { "alpha" },
        new object?[] { "beta" },
        new object?[] { "alpha" },
        new object?[] { "alpha" },
        new object?[] { "gamma" },
        new object?[] { "beta" });

    /// <summary>
    /// Bare <c>COUNT(*)</c> in <c>ORDER BY</c> — also referenced in <c>SELECT</c> —
    /// must dedup against the SELECT-side aggregate column, not double-register.
    /// Result rows are sorted ascending by group count.
    /// </summary>
    [Fact]
    public async Task OrderByBareAggregate_ReusesSelectAggregateColumn()
    {
        TableCatalog catalog = CategoryCatalog();

        List<Row> results = await ExecuteQueryAsync(
            "SELECT category, COUNT(*) FROM t GROUP BY category ORDER BY COUNT(*)",
            catalog);

        Assert.Equal(3, results.Count);

        // Counts: gamma=1, beta=2, alpha=3 — ascending by count.
        Assert.Equal("gamma", results[0]["category"].AsString());
        Assert.Equal(1L, results[0]["COUNT('*')"].AsInt64());
        Assert.Equal("beta", results[1]["category"].AsString());
        Assert.Equal(2L, results[1]["COUNT('*')"].AsInt64());
        Assert.Equal("alpha", results[2]["category"].AsString());
        Assert.Equal(3L, results[2]["COUNT('*')"].AsInt64());
    }

    /// <summary>
    /// <c>ORDER BY COUNT(*) DESC</c> — bare aggregate with descending direction.
    /// </summary>
    [Fact]
    public async Task OrderByBareAggregateDescending()
    {
        TableCatalog catalog = CategoryCatalog();

        List<Row> results = await ExecuteQueryAsync(
            "SELECT category, COUNT(*) FROM t GROUP BY category ORDER BY COUNT(*) DESC",
            catalog);

        Assert.Equal(3, results.Count);
        Assert.Equal(3L, results[0]["COUNT('*')"].AsInt64());
        Assert.Equal(2L, results[1]["COUNT('*')"].AsInt64());
        Assert.Equal(1L, results[2]["COUNT('*')"].AsInt64());
    }

    /// <summary>
    /// Aliased aggregate in SELECT, referenced by alias in ORDER BY — the
    /// existing alias-resolution path still works (no regression from the
    /// aggregate-rewrite pass touching ORDER BY).
    /// </summary>
    [Fact]
    public async Task OrderByAliasedAggregate_ResolvedThroughAlias()
    {
        TableCatalog catalog = CategoryCatalog();

        List<Row> results = await ExecuteQueryAsync(
            "SELECT category, COUNT(*) AS c FROM t GROUP BY category ORDER BY c",
            catalog);

        Assert.Equal(3, results.Count);
        Assert.Equal(1L, results[0]["c"].AsInt64());
        Assert.Equal(2L, results[1]["c"].AsInt64());
        Assert.Equal(3L, results[2]["c"].AsInt64());
    }

    // NOTE: `SELECT category FROM t GROUP BY category ORDER BY COUNT(*)` —
    // aggregate in ORDER BY that doesn't also appear in SELECT — isn't yet
    // supported. The rewrite pass correctly registers the aggregate on the
    // GroupBy and rewrites the ORDER BY item to a column reference, but the
    // downstream projection (which emits only the SELECT list) drops the
    // aggregate column before the OrderByOperator runs, so the column ref
    // dangles. Fixing it needs the projection to keep ORDER BY-only aggregate
    // columns as hidden passthroughs. Out of scope for this fix.
}
