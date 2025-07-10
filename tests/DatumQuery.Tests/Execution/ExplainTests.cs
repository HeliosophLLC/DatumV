using Axon.QueryEngine.Catalog;
using Axon.QueryEngine.Catalog.Providers;
using Axon.QueryEngine.Execution;
using Axon.QueryEngine.Execution.Operators;
using Axon.QueryEngine.Functions;
using Axon.QueryEngine.Model;
using Axon.QueryEngine.Parsing.Ast;
using ExecutionContext = Axon.QueryEngine.Execution.ExecutionContext;

namespace Axon.QueryEngine.Tests.Execution;

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

        Assert.Contains(node.Warnings, w => w.Contains("LIKE"));
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

        Assert.Equal("INNER Join", node.OperatorName);
        Assert.Contains("nested-loop", node.Details);
        Assert.Contains(node.Warnings, w => w.Contains("O(n*m)"));
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

        Assert.Contains("hash", node.Details);
        Assert.DoesNotContain("nested-loop", node.Details);
    }
}
