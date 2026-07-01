using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Execution;

/// <summary>
/// When a query groups by an <em>expression</em> (not a bare column), that
/// expression may reappear in the SELECT list, HAVING, or ORDER BY. Because the
/// GroupByOperator collapses source rows, the expression's input columns are
/// gone downstream — the planner must rewrite those repeats into references to
/// the group's precomputed key column rather than re-evaluate them. These tests
/// pin that behaviour, including the alias and ordinal forms that resolve to a
/// grouping expression.
/// </summary>
public sealed class GroupedExpressionProjectionTests : ServiceTestBase
{
    // words(w): "a","a","b" → upper(w) yields "A" (2 rows) and "B" (1 row).
    private TableCatalog WordsCatalog() => CreateCatalog(
        "words",
        columns: ["w"],
        new object?[] { "a" },
        new object?[] { "a" },
        new object?[] { "b" });

    [Fact]
    public async Task ProjectGroupingExpression_Explicit()
    {
        TableCatalog catalog = WordsCatalog();

        List<Row> results = await ExecuteQueryAsync(
            "SELECT upper(w) AS u, COUNT(*) AS c FROM words GROUP BY upper(w) ORDER BY u",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal("A", results[0]["u"].AsString());
        Assert.Equal(2L, results[0]["c"].AsInt64());
        Assert.Equal("B", results[1]["u"].AsString());
        Assert.Equal(1L, results[1]["c"].AsInt64());
    }

    [Fact]
    public async Task ProjectGroupingExpression_GroupedByAlias()
    {
        TableCatalog catalog = WordsCatalog();

        List<Row> results = await ExecuteQueryAsync(
            "SELECT upper(w) AS u, COUNT(*) AS c FROM words GROUP BY u ORDER BY u",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal("A", results[0]["u"].AsString());
        Assert.Equal(2L, results[0]["c"].AsInt64());
        Assert.Equal("B", results[1]["u"].AsString());
    }

    [Fact]
    public async Task ProjectGroupingExpression_Nested()
    {
        TableCatalog catalog = WordsCatalog();

        // The grouping expression appears inside a larger projection expression.
        List<Row> results = await ExecuteQueryAsync(
            "SELECT upper(w) || '!' AS u, COUNT(*) AS c FROM words GROUP BY upper(w) ORDER BY upper(w)",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal("A!", results[0]["u"].AsString());
        Assert.Equal("B!", results[1]["u"].AsString());
    }

    [Fact]
    public async Task ProjectGroupingExpression_NoAggregate()
    {
        TableCatalog catalog = WordsCatalog();

        // No aggregate → the DISTINCT rewrite path. The final projection still
        // repeats the grouping expression and must reference the key column.
        List<Row> results = await ExecuteQueryAsync(
            "SELECT upper(w) AS u FROM words GROUP BY upper(w) ORDER BY u",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal("A", results[0]["u"].AsString());
        Assert.Equal("B", results[1]["u"].AsString());
    }

    [Fact]
    public async Task HavingOnGroupingExpression()
    {
        TableCatalog catalog = WordsCatalog();

        List<Row> results = await ExecuteQueryAsync(
            "SELECT upper(w) AS u, COUNT(*) AS c FROM words GROUP BY upper(w) HAVING upper(w) = 'A'",
            catalog);

        Assert.Single(results);
        Assert.Equal("A", results[0]["u"].AsString());
        Assert.Equal(2L, results[0]["c"].AsInt64());
    }

    [Fact]
    public async Task OrderByGroupingExpression_NotProjected()
    {
        TableCatalog catalog = WordsCatalog();

        // ORDER BY references the grouping expression, which the SELECT list
        // doesn't emit — it must survive to the sort via a passthrough.
        List<Row> results = await ExecuteQueryAsync(
            "SELECT COUNT(*) AS c FROM words GROUP BY upper(w) ORDER BY upper(w)",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal(2L, results[0]["c"].AsInt64()); // "A" group
        Assert.Equal(1L, results[1]["c"].AsInt64()); // "B" group
        // The passthrough is trimmed — only the SELECT column remains.
        Assert.Single(results[0].ColumnNames);
    }

    [Fact]
    public async Task ProjectGroupingExpression_ByAliasCast()
    {
        // The reported query shape: alias maps to a CAST expression.
        TableCatalog catalog = CreateCatalog(
            "nums",
            columns: ["n"],
            new object?[] { 1 },
            new object?[] { 1 },
            new object?[] { 2 });

        List<Row> results = await ExecuteQueryAsync(
            "SELECT CAST(n AS Int64) AS cn, COUNT(*) AS c FROM nums GROUP BY cn ORDER BY cn",
            catalog);

        Assert.Equal(2, results.Count);
        Assert.Equal(2L, results[0]["c"].AsInt64());
        Assert.Equal(1L, results[1]["c"].AsInt64());
    }
}
