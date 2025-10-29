using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions.Window;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for the QUALIFY clause - a post-window-function filter that
/// eliminates the need for subquery wrappers around window functions.
/// Covers parsing, planning, and end-to-end execution.
/// </summary>
public sealed class QualifyTests : ServiceTestBase
{
    private static readonly string[] NameScoreColumns = ["name", "score"];
    private static readonly string[] CategoryItemScoreColumns = ["category", "item", "score"];
    private static readonly string[] ValColumns = ["val"];

    private async Task<List<Row>> CollectAsync(IQueryOperator operatorNode, ExecutionContext? context = null)
    {
        context ??= CreateExecutionContext();
        return await operatorNode.CollectRowsAsync(context);
    }

    // ─────────────── Parsing ───────────────

    /// <summary>
    /// QUALIFY referencing a SELECT alias parses correctly.
    /// </summary>
    [Fact]
    public void Parse_QualifyWithColumnReference()
    {
        SelectStatement statement = ParseStatement(
            "SELECT *, ROW_NUMBER() OVER (ORDER BY score DESC) AS rn FROM data QUALIFY rn <= 5");

        Assert.NotNull(statement.Qualify);
        BinaryExpression qualify = Assert.IsType<BinaryExpression>(statement.Qualify);
        Assert.Equal(BinaryOperator.LessThanOrEqual, qualify.Operator);

        ColumnReference left = Assert.IsType<ColumnReference>(qualify.Left);
        Assert.Equal("rn", left.ColumnName);
    }

    /// <summary>
    /// QUALIFY with an inline window function call parses the window specification.
    /// </summary>
    [Fact]
    public void Parse_QualifyWithInlineWindowFunction()
    {
        SelectStatement statement = ParseStatement(
            "SELECT name, score FROM data QUALIFY ROW_NUMBER() OVER (PARTITION BY category ORDER BY score DESC) <= 3");

        Assert.NotNull(statement.Qualify);
        BinaryExpression qualify = Assert.IsType<BinaryExpression>(statement.Qualify);

        WindowFunctionCallExpression windowCall = Assert.IsType<WindowFunctionCallExpression>(qualify.Left);
        Assert.Equal("ROW_NUMBER", windowCall.FunctionName, ignoreCase: true);
        Assert.NotNull(windowCall.Window.PartitionBy);
        Assert.Single(windowCall.Window.PartitionBy);
    }

    /// <summary>
    /// QUALIFY coexists with WHERE, GROUP BY, HAVING, ORDER BY, and LIMIT.
    /// </summary>
    [Fact]
    public void Parse_QualifyWithAllClauses()
    {
        SelectStatement statement = ParseStatement(
            "SELECT category, COUNT(*) AS cnt, ROW_NUMBER() OVER (ORDER BY COUNT(*) DESC) AS rn " +
            "FROM data " +
            "WHERE price > 0 " +
            "GROUP BY category " +
            "HAVING COUNT(*) > 1 " +
            "QUALIFY rn <= 10 " +
            "ORDER BY cnt DESC " +
            "LIMIT 100");

        Assert.NotNull(statement.Where);
        Assert.NotNull(statement.GroupBy);
        Assert.NotNull(statement.Having);
        Assert.NotNull(statement.Qualify);
        Assert.NotNull(statement.OrderBy);
        Assert.Equal(100, statement.Limit);
    }

    /// <summary>
    /// A standard SELECT without QUALIFY produces a null Qualify field.
    /// </summary>
    [Fact]
    public void Parse_WithoutQualify_ReturnsNull()
    {
        SelectStatement statement = ParseStatement("SELECT * FROM data");

        Assert.Null(statement.Qualify);
    }

    /// <summary>
    /// QUALIFY with a compound boolean predicate (AND) parses correctly.
    /// </summary>
    [Fact]
    public void Parse_QualifyCompoundPredicate()
    {
        SelectStatement statement = ParseStatement(
            "SELECT * FROM data QUALIFY rn <= 3 AND category = 'A'");

        Assert.NotNull(statement.Qualify);
        BinaryExpression qualify = Assert.IsType<BinaryExpression>(statement.Qualify);
        Assert.Equal(BinaryOperator.And, qualify.Operator);
    }

    /// <summary>
    /// QUALIFY clauses in both branches of a UNION ALL are preserved.
    /// </summary>
    [Fact]
    public void Parse_QualifyInSetOperation()
    {
        QueryExpression result = SqlParser.Parse(
            "SELECT * FROM t1 QUALIFY rn <= 5 UNION ALL SELECT * FROM t2 QUALIFY rn <= 3");

        CompoundQueryExpression compound = Assert.IsType<CompoundQueryExpression>(result);

        SelectQueryExpression left = Assert.IsType<SelectQueryExpression>(compound.Left);
        Assert.NotNull(left.Statement.Qualify);

        SelectQueryExpression right = Assert.IsType<SelectQueryExpression>(compound.Right);
        Assert.NotNull(right.Statement.Qualify);
    }

    // ─────────────── Operator-level execution ───────────────

    /// <summary>
    /// FilterOperator applied after WindowOperator correctly filters by
    /// the computed window column, simulating QUALIFY rn &lt;= 2.
    /// </summary>
    [Fact]
    public async Task Qualify_FiltersAfterWindowComputation()
    {
        MockOperator source = CreateMockOperator(NameScoreColumns,
            ["a", 10f],
            ["b", 30f],
            ["c", 20f]);

        WindowSpecification specification = new(
            PartitionBy: null,
            OrderBy: [new OrderByItem(new ColumnReference("score"), SortDirection.Descending)],
            Frame: null);

        WindowColumn windowColumn = new(new RowNumberFunction(), [], specification, "rn");
        WindowOperator windowOperator = new(source, [windowColumn]);

        Expression qualifyPredicate = new BinaryExpression(
            new ColumnReference("rn"),
            BinaryOperator.LessThanOrEqual,
            new LiteralExpression(DataValue.FromFloat32(2f)));

        FilterOperator qualifyFilter = new(windowOperator, qualifyPredicate);
        List<Row> results = await CollectAsync(qualifyFilter);

        Assert.Equal(2, results.Count);
        Assert.Equal("b", results[0]["name"].AsString());
        Assert.Equal("c", results[1]["name"].AsString());
    }

    /// <summary>
    /// Top-N per partition via WindowOperator + FilterOperator,
    /// simulating QUALIFY ROW_NUMBER() OVER (PARTITION BY category ORDER BY score DESC) &lt;= 2.
    /// </summary>
    [Fact]
    public async Task Qualify_TopNPerGroup()
    {
        MockOperator source = CreateMockOperator(CategoryItemScoreColumns,
            ["X", "x1", 10f],
            ["X", "x2", 30f],
            ["X", "x3", 20f],
            ["Y", "y1", 50f],
            ["Y", "y2", 40f],
            ["Y", "y3", 60f]);

        WindowSpecification specification = new(
            PartitionBy: [new ColumnReference("category")],
            OrderBy: [new OrderByItem(new ColumnReference("score"), SortDirection.Descending)],
            Frame: null);

        WindowColumn windowColumn = new(new RowNumberFunction(), [], specification, "rn");
        WindowOperator windowOperator = new(source, [windowColumn]);

        Expression qualifyPredicate = new BinaryExpression(
            new ColumnReference("rn"),
            BinaryOperator.LessThanOrEqual,
            new LiteralExpression(DataValue.FromFloat32(2f)));

        FilterOperator qualifyFilter = new(windowOperator, qualifyPredicate);
        List<Row> results = await CollectAsync(qualifyFilter);

        Assert.Equal(4, results.Count);

        List<Row> xRows = results.Where(row => row["category"].AsString() == "X").ToList();
        Assert.Equal(2, xRows.Count);
        Assert.Contains(xRows, row => row["item"].AsString() == "x2");
        Assert.Contains(xRows, row => row["item"].AsString() == "x3");

        List<Row> yRows = results.Where(row => row["category"].AsString() == "Y").ToList();
        Assert.Equal(2, yRows.Count);
        Assert.Contains(yRows, row => row["item"].AsString() == "y3");
        Assert.Contains(yRows, row => row["item"].AsString() == "y1");
    }

    /// <summary>
    /// QUALIFY predicate that matches no rows produces an empty result set.
    /// </summary>
    [Fact]
    public async Task Qualify_NoMatchingRows_ReturnsEmpty()
    {
        MockOperator source = CreateMockOperator(ValColumns,
            [1f],
            [2f]);

        WindowSpecification specification = new(
            PartitionBy: null,
            OrderBy: [new OrderByItem(new ColumnReference("val"), SortDirection.Ascending)],
            Frame: null);

        WindowColumn windowColumn = new(new RowNumberFunction(), [], specification, "rn");
        WindowOperator windowOperator = new(source, [windowColumn]);

        Expression qualifyPredicate = new BinaryExpression(
            new ColumnReference("rn"),
            BinaryOperator.GreaterThan,
            new LiteralExpression(DataValue.FromFloat32(100f)));

        FilterOperator qualifyFilter = new(windowOperator, qualifyPredicate);
        List<Row> results = await CollectAsync(qualifyFilter);

        Assert.Empty(results);
    }

    // ─────────────── End-to-end planner integration ───────────────

    /// <summary>
    /// End-to-end: QUALIFY referencing a SELECT-list window alias
    /// selects top-2 per partition.
    /// </summary>
    [Fact]
    public async Task Qualify_EndToEnd_TopNPerGroup_ViaAlias()
    {
        TableCatalog catalog = CreateCatalog("data",
            columns: ["category", "score"],
            ["A", 10f],
            ["A", 30f],
            ["A", 20f],
            ["B", 50f],
            ["B", 40f]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT category, score, ROW_NUMBER() OVER (PARTITION BY category ORDER BY score DESC) AS rn " +
            "FROM data QUALIFY rn <= 2",
            catalog);

        Assert.Equal(4, results.Count);

        List<Row> aRows = results.Where(row => row["category"].AsString() == "A").ToList();
        Assert.Equal(2, aRows.Count);
        float[] aScores = aRows.Select(row => row["score"].AsFloat32()).OrderDescending().ToArray();
        Assert.Equal(30f, aScores[0]);
        Assert.Equal(20f, aScores[1]);

        List<Row> bRows = results.Where(row => row["category"].AsString() == "B").ToList();
        Assert.Equal(2, bRows.Count);
    }

    /// <summary>
    /// End-to-end: QUALIFY with an inline window function that does NOT
    /// appear in the SELECT list. The generated window column should be
    /// stripped from the final output by the projection operator.
    /// </summary>
    [Fact]
    public async Task Qualify_EndToEnd_InlineWindowExpression()
    {
        TableCatalog catalog = CreateCatalog("data",
            columns: ["name", "score"],
            ["alice", 10f],
            ["bob", 30f],
            ["carol", 20f],
            ["dave", 40f]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT name, score FROM data " +
            "QUALIFY ROW_NUMBER() OVER (ORDER BY score DESC) <= 2",
            catalog);

        Assert.Equal(2, results.Count);
        string[] names = results.Select(row => row["name"].AsString()).OrderBy(name => name).ToArray();
        Assert.Contains("bob", names);
        Assert.Contains("dave", names);

        // The synthetic rn column should NOT be in output.
        Assert.Equal(2, results[0].FieldCount);
    }

    /// <summary>
    /// End-to-end: WHERE → GROUP BY → HAVING → Window → QUALIFY pipeline
    /// with all stages active.
    /// </summary>
    [Fact]
    public async Task Qualify_EndToEnd_WithGroupByAndHaving()
    {
        TableCatalog catalog = CreateCatalog("employees",
            columns: ["department", "status", "salary"],
            ["eng", "active", 100f],
            ["eng", "active", 200f],
            ["eng", "inactive", 300f],
            ["sales", "active", 150f],
            ["sales", "active", 250f],
            ["hr", "active", 50f]);

        // WHERE filters to active only → GROUP BY department →
        // HAVING COUNT(*) > 1 → ROW_NUMBER by department name → QUALIFY rn = 1
        List<Row> results = await ExecuteQueryAsync(
            "SELECT department, SUM(salary) AS total_salary, " +
            "ROW_NUMBER() OVER (ORDER BY department) AS rn " +
            "FROM employees " +
            "WHERE status = 'active' " +
            "GROUP BY department " +
            "HAVING COUNT(*) > 1 " +
            "QUALIFY rn = 1",
            catalog);

        // active employees: eng (100+200=300, count=2), sales (150+250=400, count=2), hr (50, count=1)
        // HAVING COUNT(*) > 1 removes hr
        // ROW_NUMBER by department ASC: eng (rn=1), sales (rn=2)
        // QUALIFY rn = 1: only eng
        Assert.Single(results);
        Assert.Equal("eng", results[0]["department"].AsString());
        Assert.Equal(300f, results[0]["total_salary"].AsFloat32());
    }

    /// <summary>
    /// End-to-end: QUALIFY runs before DISTINCT, so duplicate rows
    /// are deduplicated after window filtering.
    /// </summary>
    [Fact]
    public async Task Qualify_EndToEnd_WithDistinct()
    {
        TableCatalog catalog = CreateCatalog("data",
            columns: ["category", "value"],
            ["A", 1f],
            ["A", 1f],
            ["A", 2f],
            ["B", 3f]);

        List<Row> results = await ExecuteQueryAsync(
            "SELECT DISTINCT category, value FROM data " +
            "QUALIFY ROW_NUMBER() OVER (PARTITION BY category ORDER BY value) <= 2",
            catalog);

        // QUALIFY keeps: A(1), A(1), B(3) — A(2) has rn=3
        // DISTINCT deduplicates: (A,1), (B,3)
        Assert.Equal(2, results.Count);
    }

    /// <summary>
    /// Parameter names in QUALIFY expressions are discovered by <see cref="ParameterBinder"/>.
    /// </summary>
    [Fact]
    public void Qualify_ParameterBinding()
    {
        SelectStatement statement = ParseStatement(
            "SELECT * FROM data QUALIFY ROW_NUMBER() OVER (ORDER BY score DESC) <= $topn");

        Assert.NotNull(statement.Qualify);

        HashSet<string> parameterNames = ParameterBinder.CollectParameterNames(statement);
        Assert.Contains("topn", parameterNames);
    }

    // ─────────────── Helpers ───────────────

    private static SelectStatement ParseStatement(string sql)
    {
        return ((SelectQueryExpression)SqlParser.Parse(sql)).Statement;
    }
}
