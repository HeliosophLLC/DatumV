using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Execution;

/// <summary>
/// Tests that aggregate function calls (e.g. <c>COUNT(*)</c>, <c>SUM(x)</c>)
/// appearing in <c>ORDER BY</c> clauses are correctly lifted into the
/// <see cref="Heliosoph.DatumV.Execution.Operators.GroupByOperator"/>'s aggregate
/// columns by the planner and rewritten as column references — so the
/// downstream <see cref="Heliosoph.DatumV.Execution.Operators.OrderByOperator"/>
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

    /// <summary>
    /// Aggregate referenced only in ORDER BY (not in SELECT). The planner
    /// must register the aggregate on the GroupBy <em>and</em> append a
    /// passthrough to the projection so the column survives until the sort
    /// runs, then trim it back out so the user-visible schema matches their
    /// SELECT list.
    /// </summary>
    [Fact]
    public async Task OrderByAggregate_NotInSelect_PassesThroughAndTrims()
    {
        TableCatalog catalog = CategoryCatalog();

        List<Row> results = await ExecuteQueryAsync(
            "SELECT category FROM t GROUP BY category ORDER BY COUNT(*)",
            catalog);

        Assert.Equal(3, results.Count);

        // Sort order: gamma=1, beta=2, alpha=3 — ascending by hidden count.
        Assert.Equal("gamma", results[0]["category"].AsString());
        Assert.Equal("beta", results[1]["category"].AsString());
        Assert.Equal("alpha", results[2]["category"].AsString());

        // The aggregate column must NOT appear in the user-visible schema.
        Assert.Single(results[0].ColumnNames);
        Assert.Equal("category", results[0].ColumnNames[0]);
    }

    /// <summary>
    /// Aggregate appears in SELECT under an alias and in ORDER BY as the bare
    /// call — the rewrite produces a passthrough for the aggregate's synthetic
    /// name (since the alias renames it), but the trim removes the passthrough
    /// so the output still has just <c>category, c</c>.
    /// </summary>
    [Fact]
    public async Task OrderByBareAggregate_AliasInSelect_TrimsPassthrough()
    {
        TableCatalog catalog = CategoryCatalog();

        List<Row> results = await ExecuteQueryAsync(
            "SELECT category, COUNT(*) AS c FROM t GROUP BY category ORDER BY COUNT(*)",
            catalog);

        Assert.Equal(3, results.Count);
        Assert.Equal(1L, results[0]["c"].AsInt64());
        Assert.Equal(2L, results[1]["c"].AsInt64());
        Assert.Equal(3L, results[2]["c"].AsInt64());

        // Output schema is exactly [category, c] — no leaked passthrough column.
        Assert.Equal(2, results[0].ColumnNames.Count);
        Assert.Contains("category", results[0].ColumnNames);
        Assert.Contains("c", results[0].ColumnNames);
    }

    /// <summary>
    /// Compound expression in ORDER BY containing a bare aggregate
    /// (<c>ORDER BY COUNT(*) DESC</c> uses the column directly, but
    /// <c>ORDER BY COUNT(*) + 1</c> nests it inside a binary op). The rewrite
    /// must walk into the expression and still produce a passthrough.
    /// </summary>
    [Fact]
    public async Task OrderByAggregate_NestedInExpression_NotInSelect()
    {
        TableCatalog catalog = CategoryCatalog();

        List<Row> results = await ExecuteQueryAsync(
            "SELECT category FROM t GROUP BY category ORDER BY COUNT(*) + 1",
            catalog);

        Assert.Equal(3, results.Count);
        Assert.Equal("gamma", results[0]["category"].AsString());
        Assert.Equal("beta", results[1]["category"].AsString());
        Assert.Equal("alpha", results[2]["category"].AsString());
        Assert.Single(results[0].ColumnNames);
    }
}
