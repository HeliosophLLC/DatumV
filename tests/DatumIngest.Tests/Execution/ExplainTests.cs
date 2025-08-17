using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Indexing;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

public class ExplainTests
{
    private static readonly FunctionRegistry DefaultFunctions = FunctionRegistry.CreateDefault();

    private static readonly string FixturesPath = Path.Combine(
        AppContext.BaseDirectory, "Fixtures");

    private static TableCatalog CreateCatalogWithCsv(string tableName, string csvPath)
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        catalog.Register(new TableDescriptor("csv", tableName, csvPath, new Dictionary<string, string>()));
        return catalog;
    }

    private static string GetFixturePath(string fileName) =>
        Path.Combine(FixturesPath, fileName);

    // ──────────────── QueryExplainer (static EXPLAIN) ────────────────

    [Fact]
    public void Explain_ScanOnly_ProducesScanNode()
    {
        TableCatalog catalog = CreateCatalogWithCsv("data", "test.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")));

        IQueryOperator plan = planner.Plan(statement);
        ExplainPlanNode node = QueryExplainer.Explain(plan);

        Assert.Equal("Scan", node.OperatorName);
        Assert.Contains("data", node.Details);
        Assert.Contains("csv", node.Details);
        Assert.Empty(node.Children);
    }

    [Fact]
    public void Explain_FilterOverScan_ProducesFilterNode()
    {
        TableCatalog catalog = CreateCatalogWithCsv("data", "test.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")),
            Where: new BinaryExpression(
                new ColumnReference("x"),
                BinaryOperator.GreaterThan,
                new LiteralExpression(10.0)));

        IQueryOperator plan = planner.Plan(statement);
        ExplainPlanNode node = QueryExplainer.Explain(plan);

        Assert.Equal("Filter", node.OperatorName);
        Assert.Contains("x > 10", node.Details);
        Assert.Single(node.Children);
        Assert.Equal("Scan", node.Children[0].OperatorName);
    }

    [Fact]
    public void Explain_Join_ProducesJoinWithTwoChildren()
    {
        TableCatalog catalog = CreateCatalogWithCsv("left", "l.csv");
        catalog.Register(new TableDescriptor("csv", "right", "r.csv", new Dictionary<string, string>()));

        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("left")),
            Joins: [
                new JoinClause(
                    JoinType.Inner,
                    new TableReference("right"),
                    new BinaryExpression(
                        new ColumnReference("left", "id"),
                        BinaryOperator.Equal,
                        new ColumnReference("right", "id")))
            ]);

        IQueryOperator plan = planner.Plan(statement);
        ExplainPlanNode node = QueryExplainer.Explain(plan);

        // Unwrap the Project node added for SELECT * column ordering.
        if (node.OperatorName == "Project")
            node = node.Children[0];

        Assert.Equal("INNER Join", node.OperatorName);
        Assert.Contains("hash", node.Details);
        Assert.Equal(2, node.Children.Count);
        Assert.Equal("probe", node.Children[0].ChildLabel);
        Assert.Equal("build", node.Children[1].ChildLabel);
    }

    [Fact]
    public void Explain_CrossJoin_WarnsAboutCartesianProduct()
    {
        TableCatalog catalog = CreateCatalogWithCsv("a", "a.csv");
        catalog.Register(new TableDescriptor("csv", "b", "b.csv", new Dictionary<string, string>()));

        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("a")),
            Joins: [
                new JoinClause(JoinType.Cross, new TableReference("b"), null)
            ]);

        IQueryOperator plan = planner.Plan(statement);
        ExplainPlanNode node = QueryExplainer.Explain(plan);

        // Unwrap the Project node added for SELECT * column ordering.
        if (node.OperatorName == "Project")
            node = node.Children[0];

        Assert.Equal("CROSS Join", node.OperatorName);
        Assert.Contains("nested-loop", node.Details);
        Assert.Contains(node.Warnings, w => w.Contains("cartesian product"));
    }

    [Fact]
    public void Explain_OrderBy_WarnsAboutMaterialization()
    {
        TableCatalog catalog = CreateCatalogWithCsv("data", "test.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")),
            OrderBy: new OrderByClause([
                new OrderByItem(new ColumnReference("x"), SortDirection.Descending)
            ]));

        IQueryOperator plan = planner.Plan(statement);
        ExplainPlanNode node = QueryExplainer.Explain(plan);

        Assert.Equal("Sort", node.OperatorName);
        Assert.Contains("DESC", node.Details);
        Assert.Contains(node.Warnings, w => w.Contains("materializes"));
    }

    [Fact]
    public void Explain_LimitWithOffset_ShowsLimitAndOffset()
    {
        TableCatalog catalog = CreateCatalogWithCsv("data", "test.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")),
            Limit: 50,
            Offset: 10);

        IQueryOperator plan = planner.Plan(statement);
        ExplainPlanNode node = QueryExplainer.Explain(plan);

        Assert.Equal("Limit", node.OperatorName);
        Assert.Contains("50", node.Details);
        Assert.Contains("offset: 10", node.Details);
    }

    [Fact]
    public void Explain_OrderByWithLimit_ShowsBoundedTopNAnnotation()
    {
        TableCatalog catalog = CreateCatalogWithCsv("data", "test.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")),
            OrderBy: new OrderByClause([
                new OrderByItem(new ColumnReference("x"), SortDirection.Ascending)
            ]),
            Limit: 10);

        IQueryOperator plan = planner.Plan(statement);
        ExplainPlanNode root = QueryExplainer.Explain(plan);

        Assert.Equal("Limit", root.OperatorName);
        ExplainPlanNode sort = root.Children[0];
        Assert.Equal("Sort", sort.OperatorName);
        Assert.Contains(sort.Annotations, a => a.Contains("bounded top-N sort"));
        Assert.Contains(sort.Annotations, a => a.Contains("N=10"));
        Assert.Empty(sort.Warnings);
    }

    [Fact]
    public void Explain_OrderByWithLimitAndOffset_TopNIncludesOffset()
    {
        TableCatalog catalog = CreateCatalogWithCsv("data", "test.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")),
            OrderBy: new OrderByClause([
                new OrderByItem(new ColumnReference("x"), SortDirection.Descending)
            ]),
            Limit: 5,
            Offset: 10);

        IQueryOperator plan = planner.Plan(statement);
        ExplainPlanNode root = QueryExplainer.Explain(plan);

        ExplainPlanNode sort = root.Children[0];
        Assert.Contains(sort.Annotations, a => a.Contains("N=15"));
        Assert.Empty(sort.Warnings);
    }

    [Fact]
    public void Render_WithAnnotations_OutputsAnnotationMarkers()
    {
        ExplainPlanNode node = new()
        {
            OperatorName = "Sort",
            Details = "x ASC",
            Annotations = { "bounded top-N sort (N=10)" },
            Children =
            {
                new ExplainPlanNode { OperatorName = "Scan", Details = "table: data" }
            },
        };

        string output = node.Render();

        Assert.Contains("→", output);
        Assert.Contains("bounded top-N sort", output);
        Assert.DoesNotContain("⚠", output);
    }

    [Fact]
    public void Explain_ProjectWithAlias_ShowsColumnExpressions()
    {
        TableCatalog catalog = CreateCatalogWithCsv("data", "test.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [
                new SelectColumn(new ColumnReference("x")),
                new SelectColumn(
                    new BinaryExpression(
                        new ColumnReference("y"),
                        BinaryOperator.Multiply,
                        new LiteralExpression(2.0)),
                    Alias: "doubled")
            ],
            From: new FromClause(new TableReference("data")));

        IQueryOperator plan = planner.Plan(statement);
        ExplainPlanNode node = QueryExplainer.Explain(plan);

        Assert.Equal("Project", node.OperatorName);
        Assert.Contains("x", node.Details);
        Assert.Contains("y * 2 AS doubled", node.Details);
    }

    [Fact]
    public void Explain_FilterWithLike_WarnsAboutFullScan()
    {
        TableCatalog catalog = CreateCatalogWithCsv("data", "test.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")),
            Where: new BinaryExpression(
                new ColumnReference("name"),
                BinaryOperator.Like,
                new LiteralExpression("%test%")));

        IQueryOperator plan = planner.Plan(statement);
        ExplainPlanNode node = QueryExplainer.Explain(plan);

        Assert.Contains(node.Warnings, w => w.Contains("full scan"));
    }

    [Fact]
    public void Explain_FilterWithILike_WarnsAboutFullScan()
    {
        TableCatalog catalog = CreateCatalogWithCsv("data", "test.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")),
            Where: new BinaryExpression(
                new ColumnReference("name"),
                BinaryOperator.ILike,
                new LiteralExpression("%test%")));

        IQueryOperator plan = planner.Plan(statement);
        ExplainPlanNode node = QueryExplainer.Explain(plan);

        Assert.Contains(node.Warnings, w => w.Contains("full scan"));
    }

    [Fact]
    public void Explain_FilterWithRegexp_WarnsAboutFullScan()
    {
        TableCatalog catalog = CreateCatalogWithCsv("data", "test.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")),
            Where: new BinaryExpression(
                new ColumnReference("name"),
                BinaryOperator.Regexp,
                new LiteralExpression("^test")));

        IQueryOperator plan = planner.Plan(statement);
        ExplainPlanNode node = QueryExplainer.Explain(plan);

        Assert.Contains(node.Warnings, w => w.Contains("full scan"));
    }

    [Fact]
    public void Explain_Subquery_ShowsAlias()
    {
        TableCatalog catalog = CreateCatalogWithCsv("data", "test.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement innerQuery = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")));

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new SubquerySource(innerQuery, "sub")));

        IQueryOperator plan = planner.Plan(statement);
        ExplainPlanNode node = QueryExplainer.Explain(plan);

        Assert.Equal("Subquery", node.OperatorName);
        Assert.Contains("sub", node.Details);
        Assert.Single(node.Children);
    }

    [Fact]
    public void Explain_ComplexPlan_ContainsAllLayers()
    {
        TableCatalog catalog = CreateCatalogWithCsv("data", "test.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectColumn(new ColumnReference("x"))],
            From: new FromClause(new TableReference("data")),
            Where: new BinaryExpression(
                new ColumnReference("x"),
                BinaryOperator.GreaterThan,
                new LiteralExpression(0.0)),
            OrderBy: new OrderByClause([
                new OrderByItem(new ColumnReference("x"), SortDirection.Ascending)
            ]),
            Limit: 100);

        IQueryOperator plan = planner.Plan(statement);
        ExplainPlanNode root = QueryExplainer.Explain(plan);

        // Expected tree: Limit → Sort → Project → Filter → Scan
        Assert.Equal("Limit", root.OperatorName);

        ExplainPlanNode sort = root.Children[0];
        Assert.Equal("Sort", sort.OperatorName);

        ExplainPlanNode project = sort.Children[0];
        Assert.Equal("Project", project.OperatorName);

        ExplainPlanNode filter = project.Children[0];
        Assert.Equal("Filter", filter.OperatorName);

        ExplainPlanNode scan = filter.Children[0];
        Assert.Equal("Scan", scan.OperatorName);
    }

    // ──────────────── ExplainPlanNode.Render() ────────────────

    [Fact]
    public void Render_SingleNode_OutputsOperatorNameAndDetails()
    {
        ExplainPlanNode node = new()
        {
            OperatorName = "Scan",
            Details = "table: test, provider: csv, columns: [*]",
        };

        string output = node.Render();

        Assert.Contains("Scan", output);
        Assert.Contains("table: test", output);
    }

    [Fact]
    public void Render_WithWarnings_OutputsWarningMarkers()
    {
        ExplainPlanNode node = new()
        {
            OperatorName = "Sort",
            Details = "x ASC",
            Warnings = { "ORDER BY materializes all input rows for sorting." },
            Children =
            {
                new ExplainPlanNode { OperatorName = "Scan", Details = "table: data" }
            },
        };

        string output = node.Render();

        Assert.Contains("⚠", output);
        Assert.Contains("materializes", output);
    }

    [Fact]
    public void Render_WithChildLabels_OutputsLabels()
    {
        ExplainPlanNode node = new()
        {
            OperatorName = "INNER Join",
            Details = "strategy: hash",
            Children =
            {
                new ExplainPlanNode
                {
                    OperatorName = "Scan",
                    Details = "table: left",
                    ChildLabel = "probe",
                },
                new ExplainPlanNode
                {
                    OperatorName = "Scan",
                    Details = "table: right",
                    ChildLabel = "build",
                },
            },
        };

        string output = node.Render();

        Assert.Contains("[probe]", output);
        Assert.Contains("[build]", output);
    }

    [Fact]
    public void Render_WithRuntimeMetrics_OutputsRowsAndTiming()
    {
        ExplainPlanNode node = new()
        {
            OperatorName = "Scan",
            Details = "table: test",
            RowsProduced = 1000,
            TotalTime = TimeSpan.FromMilliseconds(5.5),
            SelfTime = TimeSpan.FromMilliseconds(5.5),
        };

        string output = node.Render();

        Assert.Contains("rows: 1,000", output);
        Assert.Contains("self:", output);
        Assert.Contains("total:", output);
    }

    [Fact]
    public void Render_WithFilterSelectivity_OutputsPercentage()
    {
        ExplainPlanNode node = new()
        {
            OperatorName = "Filter",
            Details = "predicate: x > 0",
            RowsProduced = 250,
            RowsConsumed = 1000,
            TotalTime = TimeSpan.FromMilliseconds(2.0),
            SelfTime = TimeSpan.FromMilliseconds(0.5),
        };

        string output = node.Render();

        Assert.Contains("rows in: 1,000", output);
        Assert.Contains("out: 250", output);
        Assert.Contains("25.0%", output);
    }

    // ──────────────── InstrumentedOperator ────────────────

    [Fact]
    public async Task InstrumentedOperator_CountsRowsProduced()
    {
        // Create a simple scan on a real CSV file.
        string csvPath = GetFixturePath("simple.csv");

        if (!File.Exists(csvPath))
        {
            return;
        }

        TableCatalog catalog = CreateCatalogWithCsv("data", csvPath);
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")));

        IQueryOperator plan = planner.Plan(statement);
        InstrumentedOperator instrumented = InstrumentedOperator.InstrumentTree(plan);

        ExecutionContext context = new(
            CancellationToken.None,
            FunctionRegistry.CreateDefault(),
            catalog);

        long actualRows = 0;
        await foreach (Row _ in instrumented.ExecuteAsync(context))
        {
            actualRows++;
        }

        Assert.Equal(actualRows, instrumented.RowsProduced);
        Assert.True(instrumented.TotalElapsed > TimeSpan.Zero);
    }

    [Fact]
    public async Task InstrumentedOperator_FilterShowsSelectivity()
    {
        string csvPath = GetFixturePath("simple.csv");

        if (!File.Exists(csvPath))
        {
            return;
        }

        TableCatalog catalog = CreateCatalogWithCsv("data", csvPath);
        QueryPlanner planner = new(catalog, DefaultFunctions);

        // WHERE clause that will filter some rows.
        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")),
            Where: new BinaryExpression(
                new ColumnReference("score"),
                BinaryOperator.GreaterThan,
                new LiteralExpression(90.0)));

        IQueryOperator plan = planner.Plan(statement);
        InstrumentedOperator instrumented = InstrumentedOperator.InstrumentTree(plan);

        ExecutionContext context = new(
            CancellationToken.None,
            FunctionRegistry.CreateDefault(),
            catalog);

        await foreach (Row _ in instrumented.ExecuteAsync(context))
        {
        }

        // The filter should produce fewer or equal rows than its child (scan).
        InstrumentedOperator filterChild = instrumented.GetInstrumentedChildren().First();
        Assert.True(instrumented.RowsProduced <= filterChild.RowsProduced);
    }

    [Fact]
    public async Task InstrumentedOperator_SelfTimeExcludesChildren()
    {
        string csvPath = GetFixturePath("simple.csv");

        if (!File.Exists(csvPath))
        {
            return;
        }

        TableCatalog catalog = CreateCatalogWithCsv("data", csvPath);
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")),
            Where: new BinaryExpression(
                new ColumnReference("score"),
                BinaryOperator.GreaterThan,
                new LiteralExpression(0.0)));

        IQueryOperator plan = planner.Plan(statement);
        InstrumentedOperator instrumented = InstrumentedOperator.InstrumentTree(plan);

        ExecutionContext context = new(
            CancellationToken.None,
            FunctionRegistry.CreateDefault(),
            catalog);

        await foreach (Row _ in instrumented.ExecuteAsync(context))
        {
        }

        // Self time should be <= total time.
        Assert.True(instrumented.SelfElapsed <= instrumented.TotalElapsed);
    }

    [Fact]
    public void InstrumentTree_PreservesOperatorStructure()
    {
        TableCatalog catalog = CreateCatalogWithCsv("data", "test.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectColumn(new ColumnReference("x"))],
            From: new FromClause(new TableReference("data")),
            Where: new BinaryExpression(
                new ColumnReference("x"),
                BinaryOperator.GreaterThan,
                new LiteralExpression(0.0)),
            Limit: 10);

        IQueryOperator plan = planner.Plan(statement);
        InstrumentedOperator root = InstrumentedOperator.InstrumentTree(plan);

        // Root is InstrumentedOperator wrapping LimitOperator.
        Assert.IsType<LimitOperator>(root.Inner);

        // LimitOperator's source is also an InstrumentedOperator.
        LimitOperator limitOp = (LimitOperator)root.Inner;
        Assert.IsType<InstrumentedOperator>(limitOp.Source);
    }

    // ──────────────── PopulateMetrics integration ────────────────

    [Fact]
    public async Task PopulateMetrics_FiltersHaveRowCountsAndSelectivity()
    {
        string csvPath = GetFixturePath("simple.csv");

        if (!File.Exists(csvPath))
        {
            return;
        }

        TableCatalog catalog = CreateCatalogWithCsv("data", csvPath);
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")),
            Where: new BinaryExpression(
                new ColumnReference("score"),
                BinaryOperator.GreaterThan,
                new LiteralExpression(90.0)));

        IQueryOperator originalPlan = planner.Plan(statement);
        ExplainPlanNode staticPlan = QueryExplainer.Explain(originalPlan);

        IQueryOperator freshPlan = planner.Plan(statement);
        InstrumentedOperator instrumentedRoot = InstrumentedOperator.InstrumentTree(freshPlan);

        ExecutionContext context = new(
            CancellationToken.None,
            FunctionRegistry.CreateDefault(),
            catalog);

        await foreach (Row _ in instrumentedRoot.ExecuteAsync(context))
        {
        }

        InstrumentedOperator.PopulateMetrics(staticPlan, instrumentedRoot);

        // The root is a Filter node.
        Assert.Equal("Filter", staticPlan.OperatorName);
        Assert.NotNull(staticPlan.RowsProduced);
        Assert.NotNull(staticPlan.RowsConsumed);
        Assert.NotNull(staticPlan.TotalTime);
        Assert.NotNull(staticPlan.SelfTime);

        // The child is a Scan node with its own metrics.
        Assert.NotNull(staticPlan.Children[0].RowsProduced);
    }

    // ──────────────── Expression formatting ────────────────

    [Fact]
    public void FormatExpression_ColumnReference_FormatsCorrectly()
    {
        string result = QueryExplainer.FormatExpression(new ColumnReference("table", "col"));
        Assert.Equal("table.col", result);
    }

    [Fact]
    public void FormatExpression_UnqualifiedColumn_FormatsCorrectly()
    {
        string result = QueryExplainer.FormatExpression(new ColumnReference("col"));
        Assert.Equal("col", result);
    }

    [Fact]
    public void FormatExpression_BinaryExpression_FormatsCorrectly()
    {
        string result = QueryExplainer.FormatExpression(
            new BinaryExpression(
                new ColumnReference("x"),
                BinaryOperator.GreaterThanOrEqual,
                new LiteralExpression(42.0)));
        Assert.Equal("x >= 42", result);
    }

    [Fact]
    public void FormatExpression_FunctionCall_FormatsCorrectly()
    {
        string result = QueryExplainer.FormatExpression(
            new FunctionCallExpression("UPPER", [new ColumnReference("name")]));
        Assert.Equal("UPPER(name)", result);
    }

    [Fact]
    public void FormatExpression_NegatedIsNull_FormatsCorrectly()
    {
        string result = QueryExplainer.FormatExpression(
            new IsNullExpression(new ColumnReference("x"), Negated: true));
        Assert.Equal("x IS NOT NULL", result);
    }

    [Fact]
    public void FormatExpression_NotOperator_FormatsCorrectly()
    {
        string result = QueryExplainer.FormatExpression(
            new UnaryExpression(UnaryOperator.Not, new ColumnReference("active")));
        Assert.Equal("NOT active", result);
    }

    [Fact]
    public void FormatExpression_Between_FormatsCorrectly()
    {
        string result = QueryExplainer.FormatExpression(
            new BetweenExpression(
                new ColumnReference("x"),
                new LiteralExpression(1.0),
                new LiteralExpression(10.0)));
        Assert.Equal("x BETWEEN 1 AND 10", result);
    }

    [Fact]
    public void FormatExpression_InExpression_FormatsCorrectly()
    {
        string result = QueryExplainer.FormatExpression(
            new InExpression(
                new ColumnReference("status"),
                [new LiteralExpression("a"), new LiteralExpression("b")]));
        Assert.Equal("status IN ('a', 'b')", result);
    }

    [Fact]
    public void FormatExpression_SearchedCase_FormatsCorrectly()
    {
        string result = QueryExplainer.FormatExpression(
            new CaseExpression(
                null,
                [
                    new WhenClause(
                        new BinaryExpression(
                            new ColumnReference("x"),
                            BinaryOperator.GreaterThan,
                            new LiteralExpression(0.0)),
                        new LiteralExpression("positive")),
                ],
                new LiteralExpression("non-positive")));
        Assert.Equal("CASE WHEN x > 0 THEN 'positive' ELSE 'non-positive' END", result);
    }

    [Fact]
    public void FormatExpression_SimpleCase_FormatsWithOperand()
    {
        string result = QueryExplainer.FormatExpression(
            new CaseExpression(
                new ColumnReference("status"),
                [
                    new WhenClause(new LiteralExpression(1.0), new LiteralExpression("active")),
                    new WhenClause(new LiteralExpression(2.0), new LiteralExpression("inactive")),
                ],
                null));
        Assert.Equal("CASE status WHEN 1 THEN 'active' WHEN 2 THEN 'inactive' END", result);
    }

    [Fact]
    public void FormatExpression_Literal_Null_FormatsAsNull()
    {
        string result = QueryExplainer.FormatExpression(new LiteralExpression(null));
        Assert.Equal("NULL", result);
    }

    [Fact]
    public void FormatExpression_Literal_String_FormatsWithQuotes()
    {
        string result = QueryExplainer.FormatExpression(new LiteralExpression("hello"));
        Assert.Equal("'hello'", result);
    }

    [Fact]
    public void FormatExpression_Literal_Bool_FormatsAsBoolean()
    {
        string trueResult = QueryExplainer.FormatExpression(new LiteralExpression(true));
        Assert.Equal("TRUE", trueResult);

        string falseResult = QueryExplainer.FormatExpression(new LiteralExpression(false));
        Assert.Equal("FALSE", falseResult);
    }

    // ──────────────── Full Render integration ────────────────

    [Fact]
    public void Render_FullExplainPlan_ProducesReadableTree()
    {
        TableCatalog catalog = CreateCatalogWithCsv("data", "test.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectColumn(new ColumnReference("x"))],
            From: new FromClause(new TableReference("data")),
            Where: new BinaryExpression(
                new ColumnReference("x"),
                BinaryOperator.GreaterThan,
                new LiteralExpression(0.0)),
            OrderBy: new OrderByClause([
                new OrderByItem(new ColumnReference("x"), SortDirection.Ascending)
            ]),
            Limit: 100);

        IQueryOperator plan = planner.Plan(statement);
        ExplainPlanNode root = QueryExplainer.Explain(plan);

        string rendered = root.Render();

        Assert.Contains("Limit", rendered);
        Assert.Contains("Sort", rendered);
        Assert.Contains("Project", rendered);
        Assert.Contains("Filter", rendered);
        Assert.Contains("Scan", rendered);
        Assert.Contains("└─", rendered);
    }

    [Fact]
    public void Explain_AliasedTable_ShowsAlias()
    {
        TableCatalog catalog = CreateCatalogWithCsv("data", "test.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data", Alias: "d")));

        IQueryOperator plan = planner.Plan(statement);
        ExplainPlanNode node = QueryExplainer.Explain(plan);

        Assert.Equal("Alias", node.OperatorName);
        Assert.Contains("d", node.Details);
        Assert.Single(node.Children);
        Assert.Equal("Scan", node.Children[0].OperatorName);
    }

    [Fact]
    public void Explain_FullOuterJoin_WarnsAboutMaterialization()
    {
        TableCatalog catalog = CreateCatalogWithCsv("a", "a.csv");
        catalog.Register(new TableDescriptor("csv", "b", "b.csv", new Dictionary<string, string>()));

        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("a")),
            Joins: [
                new JoinClause(
                    JoinType.FullOuter,
                    new TableReference("b"),
                    new BinaryExpression(
                        new ColumnReference("a", "id"),
                        BinaryOperator.Equal,
                        new ColumnReference("b", "id")))
            ]);

        IQueryOperator plan = planner.Plan(statement);
        ExplainPlanNode node = QueryExplainer.Explain(plan);

        // Unwrap the Project node added for SELECT * column ordering.
        if (node.OperatorName == "Project")
            node = node.Children[0];

        Assert.Equal("FULL OUTER Join", node.OperatorName);
        Assert.Contains(node.Warnings, w => w.Contains("materializes both sides"));
    }

    // ──────────────── Explainer strategy accuracy tests ────────────────

    [Fact]
    public void Explain_FunctionBasedJoin_ReportsHashStrategy()
    {
        TableCatalog catalog = CreateCatalogWithCsv("archive", "z.csv");
        catalog.Register(new TableDescriptor("csv", "images", "i.csv", new Dictionary<string, string>()));

        QueryPlanner planner = new(catalog, DefaultFunctions);

        // ON GET_FILENAME(archive.file_name) = images.file_name
        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("archive")),
            Joins: [
                new JoinClause(
                    JoinType.Inner,
                    new TableReference("images"),
                    new BinaryExpression(
                        new FunctionCallExpression("GET_FILENAME", [new ColumnReference("archive", "file_name")]),
                        BinaryOperator.Equal,
                        new ColumnReference("images", "file_name")))
            ]);

        IQueryOperator plan = planner.Plan(statement);
        ExplainPlanNode node = QueryExplainer.Explain(plan);

        // Unwrap the Project node added for SELECT * column ordering.
        if (node.OperatorName == "Project")
            node = node.Children[0];

        Assert.Equal("INNER Join", node.OperatorName);
        Assert.Contains("hash", node.Details);
        Assert.DoesNotContain("nested-loop", node.Details);
    }

    [Fact]
    public void Explain_MixedEquiAndNonEquiCondition_ReportsHashPlusFilter()
    {
        TableCatalog catalog = CreateCatalogWithCsv("left_tbl", "l.csv");
        catalog.Register(new TableDescriptor("csv", "right_tbl", "r.csv", new Dictionary<string, string>()));

        QueryPlanner planner = new(catalog, DefaultFunctions);

        // ON left_tbl.id = right_tbl.id AND left_tbl.val > right_tbl.threshold
        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("left_tbl")),
            Joins: [
                new JoinClause(
                    JoinType.Inner,
                    new TableReference("right_tbl"),
                    new BinaryExpression(
                        new BinaryExpression(
                            new ColumnReference("left_tbl", "id"),
                            BinaryOperator.Equal,
                            new ColumnReference("right_tbl", "id")),
                        BinaryOperator.And,
                        new BinaryExpression(
                            new ColumnReference("left_tbl", "val"),
                            BinaryOperator.GreaterThan,
                            new ColumnReference("right_tbl", "threshold"))))
            ]);

        IQueryOperator plan = planner.Plan(statement);
        ExplainPlanNode node = QueryExplainer.Explain(plan);

        // Unwrap the Project node added for SELECT * column ordering.
        if (node.OperatorName == "Project")
            node = node.Children[0];

        Assert.Equal("INNER Join", node.OperatorName);
        Assert.Contains("hash+filter", node.Details);
    }

    [Fact]
    public void Explain_NonEquiJoinOnly_ReportsNestedLoopWithWarning()
    {
        TableCatalog catalog = CreateCatalogWithCsv("sensor", "s.csv");
        catalog.Register(new TableDescriptor("csv", "threshold", "t.csv", new Dictionary<string, string>()));

        QueryPlanner planner = new(catalog, DefaultFunctions);

        // ON sensor.reading > threshold.max_value (no equi-join)
        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("sensor")),
            Joins: [
                new JoinClause(
                    JoinType.Inner,
                    new TableReference("threshold"),
                    new BinaryExpression(
                        new ColumnReference("sensor", "reading"),
                        BinaryOperator.GreaterThan,
                        new ColumnReference("threshold", "max_value")))
            ]);

        IQueryOperator plan = planner.Plan(statement);
        ExplainPlanNode node = QueryExplainer.Explain(plan);

        // Unwrap the Project node added for SELECT * column ordering.
        if (node.OperatorName == "Project")
            node = node.Children[0];

        Assert.Equal("INNER Join", node.OperatorName);
        Assert.Contains("nested-loop", node.Details);
        Assert.Contains(node.Warnings, w => w.Contains("O(n*m)"));
    }

    [Fact]
    public void Explain_WherePredicatePushdown_ShowsStatisticsFilterOnScan()
    {
        TableCatalog catalog = CreateCatalogWithCsv("data", "test.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")),
            Where: new BinaryExpression(
                new ColumnReference("x"),
                BinaryOperator.GreaterThan,
                new LiteralExpression(5.0)));

        IQueryOperator plan = planner.Plan(statement);
        ExplainPlanNode root = QueryExplainer.Explain(plan);

        // The root is a Filter; its child is the Scan with the statistics filter annotation.
        ExplainPlanNode scanNode = root.Children[0];
        Assert.Equal("Scan", scanNode.OperatorName);
        Assert.Contains("statistics filter:", scanNode.Details);
        Assert.Contains("x > 5", scanNode.Details);
    }

    [Fact]
    public void Explain_WherePredicatePushdown_AliasedTable_ShowsStatisticsFilter()
    {
        TableCatalog catalog = CreateCatalogWithCsv("data", "test.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data", Alias: "d")),
            Where: new BinaryExpression(
                new ColumnReference("d", "x"),
                BinaryOperator.LessThan,
                new LiteralExpression(10.0)));

        IQueryOperator plan = planner.Plan(statement);
        ExplainPlanNode root = QueryExplainer.Explain(plan);

        // Walk into the tree: Filter → Alias → Scan
        ExplainPlanNode aliasNode = root.Children[0];
        Assert.Equal("Alias", aliasNode.OperatorName);

        ExplainPlanNode scanNode = aliasNode.Children[0];
        Assert.Equal("Scan", scanNode.OperatorName);
        Assert.Contains("statistics filter:", scanNode.Details);
    }

    [Fact]
    public void Explain_CastBasedJoin_ReportsHashStrategy()
    {
        TableCatalog catalog = CreateCatalogWithCsv("source_a", "a.csv");
        catalog.Register(new TableDescriptor("csv", "source_b", "b.csv", new Dictionary<string, string>()));

        QueryPlanner planner = new(catalog, DefaultFunctions);

        // ON CAST(source_a.id AS INT) = source_b.id
        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("source_a")),
            Joins: [
                new JoinClause(
                    JoinType.Inner,
                    new TableReference("source_b"),
                    new BinaryExpression(
                        new CastExpression(new ColumnReference("source_a", "id"), "INT"),
                        BinaryOperator.Equal,
                        new ColumnReference("source_b", "id")))
            ]);

        IQueryOperator plan = planner.Plan(statement);
        ExplainPlanNode node = QueryExplainer.Explain(plan);

        // Unwrap the Project node added for SELECT * column ordering.
        if (node.OperatorName == "Project")
            node = node.Children[0];

        Assert.Contains("hash", node.Details);
        Assert.DoesNotContain("nested-loop", node.Details);
    }

    // ──────────────── Cardinality estimation ────────────────

    [Fact]
    public void Explain_ScanFromCsv_EstimatedRowsIsNull()
    {
        TableCatalog catalog = CreateCatalogWithCsv("data", "test.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")));

        IQueryOperator plan = planner.Plan(statement);
        ExplainPlanNode node = QueryExplainer.Explain(plan);

        Assert.Equal("Scan", node.OperatorName);
        Assert.Null(node.EstimatedRows);
    }

    [Fact]
    public void Explain_FilterPropagatesEstimatedRowsFromChild()
    {
        ExplainPlanNode scanNode = new()
        {
            OperatorName = "Scan",
            Details = "table: data",
            EstimatedRows = 10_000,
        };

        // Build a filter manually to test propagation.
        ScanOperator scan = new(
            new TableDescriptor("csv", "data", "test.csv", new Dictionary<string, string>()),
            requiredColumns: null);
        scan.EstimatedRowCount = 10_000;

        Expression predicate = new BinaryExpression(
            new ColumnReference("x"),
            BinaryOperator.GreaterThan,
            new LiteralExpression(5.0));

        FilterOperator filter = new(scan, predicate);
        ExplainPlanNode node = QueryExplainer.Explain(filter);

        Assert.Equal("Filter", node.OperatorName);
        Assert.NotNull(node.EstimatedRows);
        Assert.True(node.EstimatedRows!.Value > 0);
        Assert.True(node.EstimatedRows.Value < 10_000);

        // Child scan should have the original estimate.
        Assert.Equal(10_000, node.Children[0].EstimatedRows);
    }

    [Fact]
    public void Explain_FilterEqualitySelectivity_LowerThanDefault()
    {
        ScanOperator scan = new(
            new TableDescriptor("csv", "data", "test.csv", new Dictionary<string, string>()),
            requiredColumns: null);
        scan.EstimatedRowCount = 10_000;

        // Equality: selectivity = 0.10
        Expression equalPredicate = new BinaryExpression(
            new ColumnReference("status"),
            BinaryOperator.Equal,
            new LiteralExpression("active"));

        // Range: selectivity = 0.33
        Expression rangePredicate = new BinaryExpression(
            new ColumnReference("x"),
            BinaryOperator.GreaterThan,
            new LiteralExpression(5.0));

        FilterOperator equalFilter = new(scan, equalPredicate);
        FilterOperator rangeFilter = new(scan, rangePredicate);

        ExplainPlanNode equalNode = QueryExplainer.Explain(equalFilter);
        ExplainPlanNode rangeNode = QueryExplainer.Explain(rangeFilter);

        // Equality should produce fewer estimated rows than range.
        Assert.True(equalNode.EstimatedRows!.Value < rangeNode.EstimatedRows!.Value);
    }

    [Fact]
    public void Explain_FilterAndCompound_MultipliesSelectivities()
    {
        ScanOperator scan = new(
            new TableDescriptor("csv", "data", "test.csv", new Dictionary<string, string>()),
            requiredColumns: null);
        scan.EstimatedRowCount = 10_000;

        // Single equality: 0.10 → 1000
        Expression single = new BinaryExpression(
            new ColumnReference("x"),
            BinaryOperator.Equal,
            new LiteralExpression(5.0));

        // Two equalities with AND: 0.10 * 0.10 → 100
        Expression compound = new BinaryExpression(
            single,
            BinaryOperator.And,
            new BinaryExpression(
                new ColumnReference("y"),
                BinaryOperator.Equal,
                new LiteralExpression(10.0)));

        FilterOperator singleFilter = new(scan, single);
        FilterOperator compoundFilter = new(scan, compound);

        ExplainPlanNode singleNode = QueryExplainer.Explain(singleFilter);
        ExplainPlanNode compoundNode = QueryExplainer.Explain(compoundFilter);

        Assert.True(compoundNode.EstimatedRows!.Value < singleNode.EstimatedRows!.Value);
    }

    [Fact]
    public void Explain_ProjectPassthroughEstimate()
    {
        ScanOperator scan = new(
            new TableDescriptor("csv", "data", "test.csv", new Dictionary<string, string>()),
            requiredColumns: null);
        scan.EstimatedRowCount = 5_000;

        ProjectOperator project = new(scan,
            [new SelectColumn(new ColumnReference("x"))]);

        ExplainPlanNode node = QueryExplainer.Explain(project);

        Assert.Equal("Project", node.OperatorName);
        Assert.Equal(5_000, node.EstimatedRows);
    }

    [Fact]
    public void Explain_LimitCapsEstimate()
    {
        ScanOperator scan = new(
            new TableDescriptor("csv", "data", "test.csv", new Dictionary<string, string>()),
            requiredColumns: null);
        scan.EstimatedRowCount = 10_000;

        LimitOperator limit = new(scan, limit: 50);
        ExplainPlanNode node = QueryExplainer.Explain(limit);

        Assert.Equal("Limit", node.OperatorName);
        Assert.Equal(50, node.EstimatedRows);
    }

    [Fact]
    public void Explain_LimitWithOffset_IncludesOffsetInEstimate()
    {
        ScanOperator scan = new(
            new TableDescriptor("csv", "data", "test.csv", new Dictionary<string, string>()),
            requiredColumns: null);
        scan.EstimatedRowCount = 10_000;

        LimitOperator limit = new(scan, limit: 50, offset: 10);
        ExplainPlanNode node = QueryExplainer.Explain(limit);

        // Limit + offset = 60, capped by child estimate 10,000.
        Assert.Equal(60, node.EstimatedRows);
    }

    [Fact]
    public void Explain_LimitLargerThanChild_CappedByChild()
    {
        ScanOperator scan = new(
            new TableDescriptor("csv", "data", "test.csv", new Dictionary<string, string>()),
            requiredColumns: null);
        scan.EstimatedRowCount = 100;

        LimitOperator limit = new(scan, limit: 500);
        ExplainPlanNode node = QueryExplainer.Explain(limit);

        Assert.Equal(100, node.EstimatedRows);
    }

    [Fact]
    public void Explain_EquiJoinEstimate_UsesSelectivityFactor()
    {
        ScanOperator leftScan = new(
            new TableDescriptor("csv", "left", "l.csv", new Dictionary<string, string>()),
            requiredColumns: null);
        leftScan.EstimatedRowCount = 1_000;

        ScanOperator rightScan = new(
            new TableDescriptor("csv", "right", "r.csv", new Dictionary<string, string>()),
            requiredColumns: null);
        rightScan.EstimatedRowCount = 500;

        JoinOperator join = new(
            leftScan,
            rightScan,
            JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("left", "id"),
                BinaryOperator.Equal,
                new ColumnReference("right", "id")));

        ExplainPlanNode node = QueryExplainer.Explain(join);

        Assert.Equal("INNER Join", node.OperatorName);
        Assert.NotNull(node.EstimatedRows);
        // No NDV stats available → containment heuristic: max(1000, 500) = 1000
        Assert.Equal(1_000, node.EstimatedRows);
    }

    [Fact]
    public void Explain_CrossJoinEstimate_IsCartesianProduct()
    {
        ScanOperator leftScan = new(
            new TableDescriptor("csv", "a", "a.csv", new Dictionary<string, string>()),
            requiredColumns: null);
        leftScan.EstimatedRowCount = 100;

        ScanOperator rightScan = new(
            new TableDescriptor("csv", "b", "b.csv", new Dictionary<string, string>()),
            requiredColumns: null);
        rightScan.EstimatedRowCount = 200;

        JoinOperator join = new(
            leftScan, rightScan, JoinType.Cross, onCondition: null);

        ExplainPlanNode node = QueryExplainer.Explain(join);

        Assert.Equal(20_000, node.EstimatedRows);
    }

    [Fact]
    public void Explain_LeftJoinEstimate_AtLeastLeftSide()
    {
        ScanOperator leftScan = new(
            new TableDescriptor("csv", "left", "l.csv", new Dictionary<string, string>()),
            requiredColumns: null);
        leftScan.EstimatedRowCount = 1_000;

        ScanOperator rightScan = new(
            new TableDescriptor("csv", "right", "r.csv", new Dictionary<string, string>()),
            requiredColumns: null);
        rightScan.EstimatedRowCount = 10;

        JoinOperator join = new(
            leftScan,
            rightScan,
            JoinType.Left,
            new BinaryExpression(
                new ColumnReference("left", "id"),
                BinaryOperator.Equal,
                new ColumnReference("right", "id")));

        ExplainPlanNode node = QueryExplainer.Explain(join);

        // No stats → containment heuristic: max(1000, 10) = 1000, LEFT JOIN preserves left → max(1000, 1000) = 1000
        Assert.True(node.EstimatedRows!.Value >= 1_000);
    }

    [Fact]
    public void Explain_JoinWithNullEstimates_ReturnsNull()
    {
        // CSV providers return null for EstimatedRowCount.
        TableCatalog catalog = CreateCatalogWithCsv("left", "l.csv");
        catalog.Register(new TableDescriptor("csv", "right", "r.csv", new Dictionary<string, string>()));

        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("left")),
            Joins: [
                new JoinClause(
                    JoinType.Inner,
                    new TableReference("right"),
                    new BinaryExpression(
                        new ColumnReference("left", "id"),
                        BinaryOperator.Equal,
                        new ColumnReference("right", "id")))
            ]);

        IQueryOperator plan = planner.Plan(statement);
        ExplainPlanNode node = QueryExplainer.Explain(plan);

        // With CSV (null row counts), all estimates should be null.
        Assert.Null(node.EstimatedRows);
    }

    [Fact]
    public void Explain_SortPassthroughEstimate()
    {
        ScanOperator scan = new(
            new TableDescriptor("csv", "data", "test.csv", new Dictionary<string, string>()),
            requiredColumns: null);
        scan.EstimatedRowCount = 8_000;

        OrderByOperator sort = new(
            scan,
            [new OrderByItem(new ColumnReference("x"), SortDirection.Ascending)]);

        ExplainPlanNode node = QueryExplainer.Explain(sort);

        Assert.Equal("Sort", node.OperatorName);
        Assert.Equal(8_000, node.EstimatedRows);
    }

    [Fact]
    public void Render_WithEstimatedRows_ShowsTilde()
    {
        ExplainPlanNode node = new()
        {
            OperatorName = "Scan",
            Details = "table: data",
            EstimatedRows = 10_000,
        };

        string output = node.Render();

        Assert.Contains("~10,000 rows", output);
    }

    [Fact]
    public void Render_WithoutEstimatedRows_OmitsTilde()
    {
        ExplainPlanNode node = new()
        {
            OperatorName = "Scan",
            Details = "table: data",
        };

        string output = node.Render();

        Assert.DoesNotContain("~", output);
        Assert.DoesNotContain("rows", output);
    }

    [Fact]
    public void Render_EstimatedRowsWithRuntime_ShowsBoth()
    {
        ExplainPlanNode node = new()
        {
            OperatorName = "Scan",
            Details = "table: data",
            EstimatedRows = 10_000,
            RowsProduced = 9_500,
            SelfTime = TimeSpan.FromMilliseconds(3.0),
            TotalTime = TimeSpan.FromMilliseconds(3.0),
        };

        string output = node.Render();

        Assert.Contains("~10,000 rows", output);
        Assert.Contains("rows: 9,500", output);
    }

    [Fact]
    public void Explain_FilterNullChildEstimate_ProducesNullEstimate()
    {
        ScanOperator scan = new(
            new TableDescriptor("csv", "data", "test.csv", new Dictionary<string, string>()),
            requiredColumns: null);
        // Don't set EstimatedRowCount — defaults to null.

        Expression predicate = new BinaryExpression(
            new ColumnReference("x"),
            BinaryOperator.GreaterThan,
            new LiteralExpression(5.0));

        FilterOperator filter = new(scan, predicate);
        ExplainPlanNode node = QueryExplainer.Explain(filter);

        Assert.Null(node.EstimatedRows);
    }

    [Fact]
    public void Explain_LimitWithNullChildEstimate_UsesLimitAsEstimate()
    {
        ScanOperator scan = new(
            new TableDescriptor("csv", "data", "test.csv", new Dictionary<string, string>()),
            requiredColumns: null);

        LimitOperator limit = new(scan, limit: 100);
        ExplainPlanNode node = QueryExplainer.Explain(limit);

        // Even without a child estimate, LIMIT can still produce an estimate.
        Assert.Equal(100, node.EstimatedRows);
    }

    // ──────────────── Manifest-aware cardinality estimation ────────────────

    private static ScanOperator CreateScanWithManifest(
        string tableName, long rowCount, string columnName, long ndv, double? nullRatio = 0.0)
    {
        ScanOperator scan = new(
            new TableDescriptor("csv", tableName, $"{tableName}.csv", new Dictionary<string, string>()),
            requiredColumns: null);
        scan.EstimatedRowCount = rowCount;

        NumericFeatureManifest feature = new()
        {
            Name = columnName,
            Kind = DataKind.Float32,
            Count = rowCount,
            NullCount = nullRatio.HasValue ? (long)(rowCount * nullRatio.Value) : 0,
            ValidCount = rowCount - (nullRatio.HasValue ? (long)(rowCount * nullRatio.Value) : 0),
            EstimatedDistinctCount = ndv,
            TopKValues = [],
            NullRatio = nullRatio,
            Min = 0,
            Max = 100,
            Mean = 50,
            Variance = 25,
            StandardDeviation = 5,
            Skewness = 0,
            Kurtosis = 0,
            Histogram = new HistogramData([0, 100], [rowCount]),
            Quantiles = null,
            ZeroCount = 0,
            ZeroRatio = 0,
            OutlierCount = 0,
            OutlierRatio = 0,
            IntegerValued = true,
        };

        scan.ColumnStatistics = new Dictionary<string, FeatureManifest>(StringComparer.OrdinalIgnoreCase)
        {
            [columnName] = feature,
        };

        return scan;
    }

    [Fact]
    public void Explain_EqualityWithNdv_UsesOneOverNdv()
    {
        ScanOperator scan = CreateScanWithManifest("data", rowCount: 10_000, columnName: "status", ndv: 5);

        Expression predicate = new BinaryExpression(
            new ColumnReference("status"),
            BinaryOperator.Equal,
            new LiteralExpression("active"));

        FilterOperator filter = new(scan, predicate);
        ExplainPlanNode node = QueryExplainer.Explain(filter);

        // NDV=5: selectivity = 1/5 = 0.20, so 10,000 * 0.20 = 2,000
        Assert.Equal(2_000, node.EstimatedRows);
    }

    [Fact]
    public void Explain_EqualityWithHighNdv_ProducesLowEstimate()
    {
        ScanOperator scan = CreateScanWithManifest("data", rowCount: 10_000, columnName: "user_id", ndv: 10_000);

        Expression predicate = new BinaryExpression(
            new ColumnReference("user_id"),
            BinaryOperator.Equal,
            new LiteralExpression(42));

        FilterOperator filter = new(scan, predicate);
        ExplainPlanNode node = QueryExplainer.Explain(filter);

        // NDV=10,000: selectivity = 1/10,000 = 0.0001, so 10,000 * 0.0001 = 1
        Assert.Equal(1, node.EstimatedRows);
    }

    [Fact]
    public void Explain_NotEqualWithNdv_UsesOneMinusOneOverNdv()
    {
        ScanOperator scan = CreateScanWithManifest("data", rowCount: 10_000, columnName: "status", ndv: 5);

        Expression predicate = new BinaryExpression(
            new ColumnReference("status"),
            BinaryOperator.NotEqual,
            new LiteralExpression("active"));

        FilterOperator filter = new(scan, predicate);
        ExplainPlanNode node = QueryExplainer.Explain(filter);

        // NDV=5: selectivity = 1 - 1/5 = 0.80, so 10,000 * 0.80 = 8,000
        Assert.Equal(8_000, node.EstimatedRows);
    }

    [Fact]
    public void Explain_IsNullWithNullRatio_UsesActualRatio()
    {
        ScanOperator scan = CreateScanWithManifest("data", rowCount: 10_000, columnName: "email", ndv: 5, nullRatio: 0.30);

        Expression predicate = new IsNullExpression(new ColumnReference("email"), Negated: false);

        FilterOperator filter = new(scan, predicate);
        ExplainPlanNode node = QueryExplainer.Explain(filter);

        // NullRatio=0.30, so 10,000 * 0.30 = 3,000
        Assert.Equal(3_000, node.EstimatedRows);
    }

    [Fact]
    public void Explain_IsNotNullWithNullRatio_UsesComplement()
    {
        ScanOperator scan = CreateScanWithManifest("data", rowCount: 10_000, columnName: "email", ndv: 5, nullRatio: 0.30);

        Expression predicate = new IsNullExpression(new ColumnReference("email"), Negated: true);

        FilterOperator filter = new(scan, predicate);
        ExplainPlanNode node = QueryExplainer.Explain(filter);

        // IS NOT NULL: 1 - 0.30 = 0.70, so 10,000 * 0.70 = 7,000
        Assert.Equal(7_000, node.EstimatedRows);
    }

    [Fact]
    public void Explain_InExpressionWithNdv_UsesNdvBasedPerValue()
    {
        ScanOperator scan = CreateScanWithManifest("data", rowCount: 10_000, columnName: "status", ndv: 100);

        Expression predicate = new InExpression(
            new ColumnReference("status"),
            [
                new LiteralExpression("active"),
                new LiteralExpression("pending"),
                new LiteralExpression("closed"),
            ],
            Negated: false);

        FilterOperator filter = new(scan, predicate);
        ExplainPlanNode node = QueryExplainer.Explain(filter);

        // NDV=100, 3 values: 3 * (1/100) = 0.03, so 10,000 * 0.03 = 300
        Assert.Equal(300, node.EstimatedRows);
    }

    [Fact]
    public void Explain_CompoundAndWithNdv_MultipliesNdvSelectivities()
    {
        ScanOperator scan = new(
            new TableDescriptor("csv", "data", "data.csv", new Dictionary<string, string>()),
            requiredColumns: null);
        scan.EstimatedRowCount = 10_000;

        NumericFeatureManifest statusFeature = new()
        {
            Name = "status",
            Kind = DataKind.Float32,
            Count = 10_000,
            NullCount = 0,
            ValidCount = 10_000,
            EstimatedDistinctCount = 5,
            TopKValues = [],
            NullRatio = 0.0,
            Min = 0, Max = 4, Mean = 2, Variance = 1, StandardDeviation = 1,
            Skewness = 0, Kurtosis = 0,
            Histogram = new HistogramData([0, 4], [10_000]),
            ZeroCount = 0, ZeroRatio = 0, OutlierCount = 0, OutlierRatio = 0, IntegerValued = true,
        };

        NumericFeatureManifest categoryFeature = new()
        {
            Name = "category",
            Kind = DataKind.Float32,
            Count = 10_000,
            NullCount = 0,
            ValidCount = 10_000,
            EstimatedDistinctCount = 10,
            TopKValues = [],
            NullRatio = 0.0,
            Min = 0, Max = 9, Mean = 4, Variance = 2, StandardDeviation = 1.4,
            Skewness = 0, Kurtosis = 0,
            Histogram = new HistogramData([0, 9], [10_000]),
            ZeroCount = 0, ZeroRatio = 0, OutlierCount = 0, OutlierRatio = 0, IntegerValued = true,
        };

        scan.ColumnStatistics = new Dictionary<string, FeatureManifest>(StringComparer.OrdinalIgnoreCase)
        {
            ["status"] = statusFeature,
            ["category"] = categoryFeature,
        };

        // status = 'x' AND category = 'y' → (1/5) * (1/10) = 0.02, so 10,000 * 0.02 = 200
        Expression predicate = new BinaryExpression(
            new BinaryExpression(
                new ColumnReference("status"), BinaryOperator.Equal, new LiteralExpression("x")),
            BinaryOperator.And,
            new BinaryExpression(
                new ColumnReference("category"), BinaryOperator.Equal, new LiteralExpression("y")));

        FilterOperator filter = new(scan, predicate);
        ExplainPlanNode node = QueryExplainer.Explain(filter);

        Assert.Equal(200, node.EstimatedRows);
    }

    [Fact]
    public void Explain_WithoutManifest_FallsBackToDefaultSelectivity()
    {
        ScanOperator scan = new(
            new TableDescriptor("csv", "data", "data.csv", new Dictionary<string, string>()),
            requiredColumns: null);
        scan.EstimatedRowCount = 10_000;
        // No ColumnStatistics set.

        Expression predicate = new BinaryExpression(
            new ColumnReference("status"),
            BinaryOperator.Equal,
            new LiteralExpression("active"));

        FilterOperator filter = new(scan, predicate);
        ExplainPlanNode node = QueryExplainer.Explain(filter);

        // Default equality selectivity is 0.10, so 10,000 * 0.10 = 1,000
        Assert.Equal(1_000, node.EstimatedRows);
    }

    [Fact]
    public void Explain_AliasedTableWithNdv_ResolvesQualifiedColumn()
    {
        ScanOperator scan = CreateScanWithManifest("data", rowCount: 10_000, columnName: "status", ndv: 5);
        AliasOperator alias = new(scan, "d");

        // Predicate uses qualified name: d.status = 'active'
        Expression predicate = new BinaryExpression(
            new ColumnReference("d", "status"),
            BinaryOperator.Equal,
            new LiteralExpression("active"));

        FilterOperator filter = new(alias, predicate);
        ExplainPlanNode node = QueryExplainer.Explain(filter);

        // NDV=5: selectivity = 1/5 = 0.20, so 10,000 * 0.20 = 2,000
        Assert.Equal(2_000, node.EstimatedRows);
    }

    [Fact]
    public void Explain_ManifestRowCount_OverridesProviderCapabilities()
    {
        TableCatalog catalog = CreateCatalogWithCsv("data", "test.csv");

        // Register a manifest — this should override the null row count from CSV.
        QueryResultsManifest manifest = new()
        {
            RowCount = 50_000,
            GeneratedAtUtc = DateTime.UtcNow,
            Features = [],
        };
        catalog.RegisterManifest("data", manifest);

        QueryPlanner planner = new(catalog, DefaultFunctions);
        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")));

        IQueryOperator plan = planner.Plan(statement);
        ExplainPlanNode node = QueryExplainer.Explain(plan);

        // CSV returns null for EstimatedRowCount, but manifest overrides with 50,000.
        Assert.Equal("Scan", node.OperatorName);
        Assert.Equal(50_000, node.EstimatedRows);
    }

    [Fact]
    public void Explain_ManifestWithColumnStats_UsedInPlannerPipeline()
    {
        TableCatalog catalog = CreateCatalogWithCsv("data", "test.csv");

        NumericFeatureManifest statusFeature = new()
        {
            Name = "status",
            Kind = DataKind.Float32,
            Count = 1_000,
            NullCount = 0,
            ValidCount = 1_000,
            EstimatedDistinctCount = 4,
            TopKValues = [],
            NullRatio = 0.0,
            Min = 0, Max = 3, Mean = 1.5, Variance = 1, StandardDeviation = 1,
            Skewness = 0, Kurtosis = 0,
            Histogram = new HistogramData([0, 3], [1_000]),
            ZeroCount = 0, ZeroRatio = 0, OutlierCount = 0, OutlierRatio = 0, IntegerValued = true,
        };

        QueryResultsManifest manifest = new()
        {
            RowCount = 1_000,
            GeneratedAtUtc = DateTime.UtcNow,
            Features = [statusFeature],
        };
        catalog.RegisterManifest("data", manifest);

        QueryPlanner planner = new(catalog, DefaultFunctions);
        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")),
            Where: new BinaryExpression(
                new ColumnReference("status"),
                BinaryOperator.Equal,
                new LiteralExpression("active")));

        IQueryOperator plan = planner.Plan(statement);
        ExplainPlanNode node = QueryExplainer.Explain(plan);

        // Scan should have 1,000 rows (from manifest).
        // Filter with NDV=4: selectivity = 1/4 = 0.25, so 1,000 * 0.25 = 250
        ExplainPlanNode filterNode = node.OperatorName == "Project" ? node.Children[0] : node;
        Assert.Equal("Filter", filterNode.OperatorName);
        Assert.Equal(250, filterNode.EstimatedRows);
    }

    [Fact]
    public void Explain_EquiJoinWithNdv_UsesOneOverMaxNdv()
    {
        ScanOperator leftScan = CreateScanWithManifest("users", rowCount: 1_000, columnName: "id", ndv: 1_000);
        ScanOperator rightScan = CreateScanWithManifest("orders", rowCount: 5_000, columnName: "id", ndv: 800);

        // Rename right column to "user_id" to avoid collision — but NDV lookup
        // goes through join key expressions, not column names.
        // The join ON users.id = orders.id — both columns named "id".
        JoinOperator join = new(
            leftScan,
            rightScan,
            JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("users", "id"),
                BinaryOperator.Equal,
                new ColumnReference("orders", "id")));

        ExplainPlanNode node = QueryExplainer.Explain(join);

        // max(NDV_left=1000, NDV_right=800) = 1000
        // 1000 * 5000 / 1000 = 5000
        Assert.Equal(5_000, node.EstimatedRows);
    }

    // ──────────────── Access strategy & pruning annotations ────────────────

    [Fact]
    public void Explain_ScanWithBloomFilters_ShowsPruningAnnotation()
    {
        ScanOperator scan = new(
            new TableDescriptor("datum", "orders", "orders.datum", new Dictionary<string, string>()),
            requiredColumns: null);
        scan.EstimatedRowCount = 10_000;

        BloomFilterSet bloomFilters = new(
            new Dictionary<string, BloomFilter[]>
            {
                ["product_id"] = [new BloomFilter(100)],
            },
            chunkCount: 1);

        SourceIndex index = new(
            new SourceFingerprint(100, new byte[32]),
            new IndexSchema(new Schema([new ColumnInfo("product_id", DataKind.Float32, false)]), 10_000),
            []);
        scan.SetSourceIndex(new SourceIndex(
            index.Fingerprint, index.Schema, index.Chunks, bloomFilters));

        ExplainPlanNode node = QueryExplainer.Explain(scan);

        Assert.Equal("Scan", node.OperatorName);
        Assert.Contains(node.Annotations, annotation => annotation.Contains("bloom filter pruning"));
        Assert.Contains(node.Annotations, annotation => annotation.Contains("product_id"));
    }

    [Fact]
    public void Explain_ScanWithSortedIndex_ShowsPruningAnnotation()
    {
        ScanOperator scan = new(
            new TableDescriptor("datum", "data", "data.datum", new Dictionary<string, string>()),
            requiredColumns: null);
        scan.EstimatedRowCount = 5_000;

        SortedValueIndexSet sortedIndexes = new(
            new Dictionary<string, SortedValueIndex>
            {
                ["timestamp"] = new SortedValueIndex([]),
            });

        SourceIndex index = new(
            new SourceFingerprint(100, new byte[32]),
            new IndexSchema(new Schema([new ColumnInfo("timestamp", DataKind.Float32, false)]), 5_000),
            [],
            bloomFilters: null,
            sortedIndexes: sortedIndexes);
        scan.SetSourceIndex(index);

        ExplainPlanNode node = QueryExplainer.Explain(scan);

        Assert.Equal("Scan", node.OperatorName);
        Assert.Contains(node.Annotations, annotation => annotation.Contains("sorted index pruning"));
        Assert.Contains(node.Annotations, annotation => annotation.Contains("timestamp"));
    }

    [Fact]
    public void Explain_ScanWithoutIndex_HasNoPruningAnnotations()
    {
        ScanOperator scan = new(
            new TableDescriptor("csv", "data", "data.csv", new Dictionary<string, string>()),
            requiredColumns: null);
        scan.EstimatedRowCount = 1_000;

        ExplainPlanNode node = QueryExplainer.Explain(scan);

        Assert.Equal("Scan", node.OperatorName);
        Assert.Empty(node.Annotations);
    }

    [Fact]
    public void Explain_GenericFallback_UsesDescribeForExplain()
    {
        // DistinctOperator is not in the BuildNode switch, so it exercises the generic fallback.
        ScanOperator scan = new(
            new TableDescriptor("csv", "data", "data.csv", new Dictionary<string, string>()),
            requiredColumns: null);
        scan.EstimatedRowCount = 1_000;

        DistinctOperator distinct = new(scan);

        ExplainPlanNode node = QueryExplainer.Explain(distinct);

        Assert.Equal("Distinct", node.OperatorName);
        Assert.Single(node.Children);
        Assert.NotEmpty(node.Warnings);
    }
}
