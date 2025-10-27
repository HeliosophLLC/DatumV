using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Parsing;

/// <summary>
/// Parser tests for the TABLESAMPLE clause: <c>TABLESAMPLE BERNOULLI|SYSTEM(percentage) [REPEATABLE(seed)]</c>.
/// </summary>
public sealed class TablesampleParsingTests : ServiceTestBase
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
        Assert.Equal((sbyte)10, percentage.Value);
        Assert.Null(tableReference.Tablesample.Seed);
        Assert.Null(tableReference.Tablesample.StratifyColumns);
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
        Assert.Equal((sbyte)5, percentage.Value);
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
        Assert.Equal((sbyte)42, seed.Value);
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
        Assert.Equal((sbyte)42, seed.Value);

        // The primary table should have no TABLESAMPLE
        TableReference primaryTable = Assert.IsType<TableReference>(result.From!.Source);
        Assert.Null(primaryTable.Tablesample);
    }

    // ───────────────────── STRATIFIED/BALANCED basic syntax ─────────────────────

    /// <summary>
    /// TABLESAMPLE STRATIFIED(percentage) ON column parses correctly.
    /// </summary>
    [Fact]
    public void Stratified_ParsesMethodPercentageAndColumn()
    {
        SelectStatement result = Parse(
            "SELECT * FROM training_data TABLESAMPLE STRATIFIED(10) ON label");

        TableReference tableReference = Assert.IsType<TableReference>(result.From!.Source);
        Assert.NotNull(tableReference.Tablesample);
        Assert.Equal(TablesampleMethod.Stratified, tableReference.Tablesample!.Method);

        LiteralExpression percentage = Assert.IsType<LiteralExpression>(tableReference.Tablesample.Percentage);
        Assert.Equal((sbyte)10, percentage.Value);
        Assert.Null(tableReference.Tablesample.Seed);

        Assert.NotNull(tableReference.Tablesample.StratifyColumns);
        Assert.Single(tableReference.Tablesample.StratifyColumns!);
        Assert.Equal("label", tableReference.Tablesample.StratifyColumns![0].ColumnName);
    }

    /// <summary>
    /// TABLESAMPLE BALANCED(count) ON column parses correctly.
    /// </summary>
    [Fact]
    public void Balanced_ParsesMethodCountAndColumn()
    {
        SelectStatement result = Parse(
            "SELECT * FROM training_data TABLESAMPLE BALANCED(1000) ON label");

        TableReference tableReference = Assert.IsType<TableReference>(result.From!.Source);
        Assert.NotNull(tableReference.Tablesample);
        Assert.Equal(TablesampleMethod.Balanced, tableReference.Tablesample!.Method);

        LiteralExpression count = Assert.IsType<LiteralExpression>(tableReference.Tablesample.Percentage);
        Assert.Equal((short)1000, count.Value);

        Assert.NotNull(tableReference.Tablesample.StratifyColumns);
        Assert.Single(tableReference.Tablesample.StratifyColumns!);
        Assert.Equal("label", tableReference.Tablesample.StratifyColumns![0].ColumnName);
    }

    /// <summary>
    /// TABLESAMPLE STRATIFIED with composite key ON (col1, col2) parses multiple columns.
    /// </summary>
    [Fact]
    public void Stratified_WithCompositeKey_ParsesMultipleColumns()
    {
        SelectStatement result = Parse(
            "SELECT * FROM training_data TABLESAMPLE BALANCED(500) ON (label, split)");

        TableReference tableReference = Assert.IsType<TableReference>(result.From!.Source);
        Assert.NotNull(tableReference.Tablesample);
        Assert.NotNull(tableReference.Tablesample!.StratifyColumns);
        Assert.Equal(2, tableReference.Tablesample.StratifyColumns!.Count);
        Assert.Equal("label", tableReference.Tablesample.StratifyColumns[0].ColumnName);
        Assert.Equal("split", tableReference.Tablesample.StratifyColumns[1].ColumnName);
    }

    /// <summary>
    /// TABLESAMPLE STRATIFIED with REPEATABLE parses the seed.
    /// </summary>
    [Fact]
    public void Stratified_WithRepeatable_ParsesSeed()
    {
        SelectStatement result = Parse(
            "SELECT * FROM training_data TABLESAMPLE STRATIFIED(20) ON label REPEATABLE(42)");

        TableReference tableReference = Assert.IsType<TableReference>(result.From!.Source);
        Assert.NotNull(tableReference.Tablesample);
        Assert.Equal(TablesampleMethod.Stratified, tableReference.Tablesample!.Method);
        Assert.NotNull(tableReference.Tablesample.Seed);

        LiteralExpression seed = Assert.IsType<LiteralExpression>(tableReference.Tablesample.Seed);
        Assert.Equal((sbyte)42, seed.Value);

        Assert.NotNull(tableReference.Tablesample.StratifyColumns);
        Assert.Equal("label", tableReference.Tablesample.StratifyColumns![0].ColumnName);
    }

    /// <summary>
    /// TABLESAMPLE STRATIFIED with alias parses both correctly.
    /// </summary>
    [Fact]
    public void Stratified_WithAlias_ParsesCorrectly()
    {
        SelectStatement result = Parse(
            "SELECT * FROM training_data TABLESAMPLE STRATIFIED(10) ON label AS s");

        TableReference tableReference = Assert.IsType<TableReference>(result.From!.Source);
        Assert.Equal("training_data", tableReference.Name);
        Assert.Equal("s", tableReference.Alias);
        Assert.NotNull(tableReference.Tablesample);
        Assert.Equal(TablesampleMethod.Stratified, tableReference.Tablesample!.Method);
        Assert.NotNull(tableReference.Tablesample.StratifyColumns);
    }

    /// <summary>
    /// TABLESAMPLE STRATIFIED with REPEATABLE and alias parses the full syntax.
    /// </summary>
    [Fact]
    public void Stratified_WithRepeatableAndAlias_ParsesCorrectly()
    {
        SelectStatement result = Parse(
            "SELECT * FROM training_data TABLESAMPLE BALANCED(100) ON label REPEATABLE(7) AS s");

        TableReference tableReference = Assert.IsType<TableReference>(result.From!.Source);
        Assert.Equal("training_data", tableReference.Name);
        Assert.Equal("s", tableReference.Alias);
        Assert.NotNull(tableReference.Tablesample);
        Assert.Equal(TablesampleMethod.Balanced, tableReference.Tablesample!.Method);
        Assert.NotNull(tableReference.Tablesample.Seed);
        Assert.NotNull(tableReference.Tablesample.StratifyColumns);
    }

    // ───────────────────── STRATIFIED/BALANCED case insensitivity ─────────────────────

    /// <summary>
    /// STRATIFIED and BALANCED are case-insensitive identifiers.
    /// </summary>
    [Theory]
    [InlineData("stratified", TablesampleMethod.Stratified)]
    [InlineData("STRATIFIED", TablesampleMethod.Stratified)]
    [InlineData("Stratified", TablesampleMethod.Stratified)]
    [InlineData("balanced", TablesampleMethod.Balanced)]
    [InlineData("BALANCED", TablesampleMethod.Balanced)]
    [InlineData("Balanced", TablesampleMethod.Balanced)]
    public void StratifiedBalancedMethodIsCaseInsensitive(string method, TablesampleMethod expected)
    {
        SelectStatement result = Parse(
            $"SELECT * FROM t TABLESAMPLE {method}(50) ON label");

        TableReference tableReference = Assert.IsType<TableReference>(result.From!.Source);
        Assert.NotNull(tableReference.Tablesample);
        Assert.Equal(expected, tableReference.Tablesample!.Method);
    }

    // ───────────────────── STRATIFIED/BALANCED validation ─────────────────────

    /// <summary>
    /// TABLESAMPLE STRATIFIED without ON clause fails with a parse error.
    /// </summary>
    [Fact]
    public void Stratified_MissingOnClause_Fails()
    {
        Assert.ThrowsAny<Exception>(() => Parse(
            "SELECT * FROM t TABLESAMPLE STRATIFIED(10)"));
    }

    /// <summary>
    /// TABLESAMPLE BALANCED without ON clause fails with a parse error.
    /// </summary>
    [Fact]
    public void Balanced_MissingOnClause_Fails()
    {
        Assert.ThrowsAny<Exception>(() => Parse(
            "SELECT * FROM t TABLESAMPLE BALANCED(100)"));
    }

    /// <summary>
    /// TABLESAMPLE BERNOULLI with ON clause fails with a parse error.
    /// </summary>
    [Fact]
    public void Bernoulli_WithOnClause_Fails()
    {
        Assert.ThrowsAny<Exception>(() => Parse(
            "SELECT * FROM t TABLESAMPLE BERNOULLI(10) ON label"));
    }

    /// <summary>
    /// TABLESAMPLE SYSTEM with ON clause fails with a parse error.
    /// </summary>
    [Fact]
    public void System_WithOnClause_Fails()
    {
        Assert.ThrowsAny<Exception>(() => Parse(
            "SELECT * FROM t TABLESAMPLE SYSTEM(10) ON label"));
    }

    // ───────────────────── STRATIFIED/BALANCED not reserved ─────────────────────

    /// <summary>
    /// STRATIFIED and BALANCED can still be used as table names.
    /// </summary>
    [Fact]
    public void StratifiedAsTableName_ParsesCorrectly()
    {
        SelectStatement result = Parse("SELECT * FROM stratified");

        TableReference tableReference = Assert.IsType<TableReference>(result.From!.Source);
        Assert.Equal("stratified", tableReference.Name);
        Assert.Null(tableReference.Tablesample);
    }

    [Fact]
    public void BalancedAsTableName_ParsesCorrectly()
    {
        SelectStatement result = Parse("SELECT * FROM balanced");

        TableReference tableReference = Assert.IsType<TableReference>(result.From!.Source);
        Assert.Equal("balanced", tableReference.Name);
        Assert.Null(tableReference.Tablesample);
    }

    // ───────────────────── STRATIFIED/BALANCED in JOIN ─────────────────────

    /// <summary>
    /// TABLESAMPLE STRATIFIED on a joined table with ON clause does not conflict with JOIN ON.
    /// </summary>
    [Fact]
    public void Stratified_InJoin_ParsesCorrectly()
    {
        SelectStatement result = Parse(
            "SELECT * FROM orders AS o JOIN items TABLESAMPLE STRATIFIED(10) ON category REPEATABLE(42) AS i ON o.id = i.order_id");

        Assert.NotNull(result.Joins);
        Assert.Single(result.Joins!);

        TableReference joinedTable = Assert.IsType<TableReference>(result.Joins![0].Source);
        Assert.Equal("items", joinedTable.Name);
        Assert.Equal("i", joinedTable.Alias);
        Assert.NotNull(joinedTable.Tablesample);
        Assert.Equal(TablesampleMethod.Stratified, joinedTable.Tablesample!.Method);
        Assert.NotNull(joinedTable.Tablesample.StratifyColumns);
        Assert.Equal("category", joinedTable.Tablesample.StratifyColumns![0].ColumnName);

        LiteralExpression seed = Assert.IsType<LiteralExpression>(joinedTable.Tablesample.Seed);
        Assert.Equal((sbyte)42, seed.Value);
    }

    // ───────────────────── Existing: BERNOULLI/SYSTEM not reserved ─────────────────────

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
