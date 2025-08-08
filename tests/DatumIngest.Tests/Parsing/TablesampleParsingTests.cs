using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Parsing;

/// <summary>
/// Parser tests for the TABLESAMPLE clause: <c>TABLESAMPLE BERNOULLI|SYSTEM(percentage) [REPEATABLE(seed)]</c>.
/// </summary>
public sealed class TablesampleParsingTests
{
    private static SelectStatement Parse(string sql)
    {
        return ((SelectQueryExpression)SqlParser.Parse(sql)).Statement;
    }

    // ───────────────────── Basic syntax ─────────────────────

    /// <summary>
    /// TABLESAMPLE BERNOULLI(percentage) parses into a TablesampleClause with Bernoulli method.
    /// </summary>
    [Fact]
    public void TablesampleBernoulli_ParsesMethodAndPercentage()
    {
        SelectStatement result = Parse(
            "SELECT * FROM orders TABLESAMPLE BERNOULLI(10)");

        Assert.NotNull(result.From);
        TableReference tableReference = Assert.IsType<TableReference>(result.From!.Source);
        Assert.Equal("orders", tableReference.Name);
        Assert.NotNull(tableReference.Tablesample);
        Assert.Equal(TablesampleMethod.Bernoulli, tableReference.Tablesample!.Method);

        LiteralExpression percentage = Assert.IsType<LiteralExpression>(tableReference.Tablesample.Percentage);
        Assert.Equal(10d, percentage.Value);
        Assert.Null(tableReference.Tablesample.Seed);
    }

    /// <summary>
    /// TABLESAMPLE SYSTEM(percentage) parses into a TablesampleClause with System method.
    /// </summary>
    [Fact]
    public void TablesampleSystem_ParsesMethodAndPercentage()
    {
        SelectStatement result = Parse(
            "SELECT * FROM orders TABLESAMPLE SYSTEM(5)");

        Assert.NotNull(result.From);
        TableReference tableReference = Assert.IsType<TableReference>(result.From!.Source);
        Assert.NotNull(tableReference.Tablesample);
        Assert.Equal(TablesampleMethod.System, tableReference.Tablesample!.Method);

        LiteralExpression percentage = Assert.IsType<LiteralExpression>(tableReference.Tablesample.Percentage);
        Assert.Equal(5d, percentage.Value);
    }

    /// <summary>
    /// REPEATABLE(seed) clause is parsed when present.
    /// </summary>
    [Fact]
    public void TablesampleWithRepeatable_ParsesSeed()
    {
        SelectStatement result = Parse(
            "SELECT * FROM orders TABLESAMPLE BERNOULLI(10) REPEATABLE(42)");

        TableReference tableReference = Assert.IsType<TableReference>(result.From!.Source);
        Assert.NotNull(tableReference.Tablesample);
        Assert.NotNull(tableReference.Tablesample!.Seed);

        LiteralExpression seed = Assert.IsType<LiteralExpression>(tableReference.Tablesample.Seed);
        Assert.Equal(42d, seed.Value);
    }

    // ───────────────────── Alias combinations ─────────────────────

    /// <summary>
    /// TABLESAMPLE combined with an alias: table TABLESAMPLE BERNOULLI(p) AS alias.
    /// </summary>
    [Fact]
    public void TablesampleWithAlias_ParsesBothCorrectly()
    {
        SelectStatement result = Parse(
            "SELECT * FROM orders TABLESAMPLE BERNOULLI(5) AS o");

        TableReference tableReference = Assert.IsType<TableReference>(result.From!.Source);
        Assert.Equal("orders", tableReference.Name);
        Assert.Equal("o", tableReference.Alias);
        Assert.NotNull(tableReference.Tablesample);
        Assert.Equal(TablesampleMethod.Bernoulli, tableReference.Tablesample!.Method);
    }

    /// <summary>
    /// TABLESAMPLE with REPEATABLE and alias.
    /// </summary>
    [Fact]
    public void TablesampleWithRepeatableAndAlias_ParsesCorrectly()
    {
        SelectStatement result = Parse(
            "SELECT * FROM orders TABLESAMPLE SYSTEM(1) REPEATABLE(99) AS sample");

        TableReference tableReference = Assert.IsType<TableReference>(result.From!.Source);
        Assert.Equal("orders", tableReference.Name);
        Assert.Equal("sample", tableReference.Alias);
        Assert.NotNull(tableReference.Tablesample);
        Assert.Equal(TablesampleMethod.System, tableReference.Tablesample!.Method);
        Assert.NotNull(tableReference.Tablesample.Seed);
    }

    // ───────────────────── Case insensitivity ─────────────────────

    /// <summary>
    /// BERNOULLI and SYSTEM are case-insensitive identifiers.
    /// </summary>
    [Theory]
    [InlineData("bernoulli", TablesampleMethod.Bernoulli)]
    [InlineData("BERNOULLI", TablesampleMethod.Bernoulli)]
    [InlineData("Bernoulli", TablesampleMethod.Bernoulli)]
    [InlineData("system", TablesampleMethod.System)]
    [InlineData("SYSTEM", TablesampleMethod.System)]
    [InlineData("System", TablesampleMethod.System)]
    public void TablesampleMethodIsCaseInsensitive(string method, TablesampleMethod expected)
    {
        SelectStatement result = Parse(
            $"SELECT * FROM t TABLESAMPLE {method}(50)");

        TableReference tableReference = Assert.IsType<TableReference>(result.From!.Source);
        Assert.NotNull(tableReference.Tablesample);
        Assert.Equal(expected, tableReference.Tablesample!.Method);
    }

    // ───────────────────── No TABLESAMPLE ─────────────────────

    /// <summary>
    /// Tables without TABLESAMPLE continue to parse normally.
    /// </summary>
    [Fact]
    public void TableWithoutTablesample_ParsesWithNullClause()
    {
        SelectStatement result = Parse("SELECT * FROM orders");

        TableReference tableReference = Assert.IsType<TableReference>(result.From!.Source);
        Assert.Null(tableReference.Tablesample);
    }

    // ───────────────────── Edge percentages ─────────────────────

    /// <summary>
    /// Fractional percentage parses correctly.
    /// </summary>
    [Fact]
    public void TablesampleFractionalPercentage_Parses()
    {
        SelectStatement result = Parse(
            "SELECT * FROM t TABLESAMPLE BERNOULLI(0.5)");

        TableReference tableReference = Assert.IsType<TableReference>(result.From!.Source);
        Assert.NotNull(tableReference.Tablesample);

        LiteralExpression percentage = Assert.IsType<LiteralExpression>(tableReference.Tablesample!.Percentage);
        Assert.Equal(0.5d, percentage.Value);
    }

    // ───────────────────── TABLESAMPLE in JOIN ─────────────────────

    /// <summary>
    /// TABLESAMPLE on a joined table parses correctly.
    /// </summary>
    [Fact]
    public void TablesampleInJoin_ParsesCorrectly()
    {
        SelectStatement result = Parse(
            "SELECT * FROM orders AS o JOIN items TABLESAMPLE BERNOULLI(10) REPEATABLE(42) AS i ON o.id = i.order_id");

        Assert.NotNull(result.Joins);
        Assert.Single(result.Joins!);

        TableReference joinedTable = Assert.IsType<TableReference>(result.Joins![0].Source);
        Assert.Equal("items", joinedTable.Name);
        Assert.Equal("i", joinedTable.Alias);
        Assert.NotNull(joinedTable.Tablesample);
        Assert.Equal(TablesampleMethod.Bernoulli, joinedTable.Tablesample!.Method);

        LiteralExpression seed = Assert.IsType<LiteralExpression>(joinedTable.Tablesample.Seed);
        Assert.Equal(42d, seed.Value);

        // The primary table should have no TABLESAMPLE
        TableReference primaryTable = Assert.IsType<TableReference>(result.From!.Source);
        Assert.Null(primaryTable.Tablesample);
    }

    // ───────────────────── BERNOULLI/SYSTEM not reserved ─────────────────────

    /// <summary>
    /// BERNOULLI and SYSTEM can still be used as table names since they are not reserved keywords.
    /// </summary>
    [Fact]
    public void BernoulliAsTableName_ParsesCorrectly()
    {
        SelectStatement result = Parse("SELECT * FROM bernoulli");

        TableReference tableReference = Assert.IsType<TableReference>(result.From!.Source);
        Assert.Equal("bernoulli", tableReference.Name);
        Assert.Null(tableReference.Tablesample);
    }

    /// <summary>
    /// SYSTEM can still be used as a table name.
    /// </summary>
    [Fact]
    public void SystemAsTableName_ParsesCorrectly()
    {
        SelectStatement result = Parse("SELECT * FROM system");

        TableReference tableReference = Assert.IsType<TableReference>(result.From!.Source);
        Assert.Equal("system", tableReference.Name);
        Assert.Null(tableReference.Tablesample);
    }
}
