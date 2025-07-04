using Axon.QueryEngine.Catalog;
using Axon.QueryEngine.Catalog.Providers;
using Axon.QueryEngine.Execution;
using Axon.QueryEngine.Execution.Operators;
using Axon.QueryEngine.Functions;
using Axon.QueryEngine.Model;
using Axon.QueryEngine.Parsing.Ast;
using ExecutionContext = Axon.QueryEngine.Execution.ExecutionContext;

namespace Axon.QueryEngine.Tests.Execution;

public class QueryPlannerTests
{
    private static readonly FunctionRegistry DefaultFunctions = FunctionRegistry.CreateDefault();

    private static TableCatalog CreateCatalogWithCsv(string tableName, string csvPath)
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        catalog.Register(new TableDescriptor("csv", tableName, csvPath, new Dictionary<string, string>()));
        return catalog;
    }

    // ─────────────── Plan structure tests ───────────────

    [Fact]
    public void Plan_SimpleSelect_ProducesScanOperator()
    {
        TableCatalog catalog = CreateCatalogWithCsv("test", "dummy.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("test")));

        IQueryOperator plan = planner.Plan(statement);

        // SELECT * FROM test => just a ScanOperator.
        Assert.IsType<ScanOperator>(plan);
    }

    [Fact]
    public void Plan_SelectWithWhere_ProducesFilterOverScan()
    {
        TableCatalog catalog = CreateCatalogWithCsv("test", "dummy.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("test")),
            Where: new BinaryExpression(
                new ColumnReference("x"),
                BinaryOperator.GreaterThan,
                new LiteralExpression(5)));

        IQueryOperator plan = planner.Plan(statement);

        Assert.IsType<FilterOperator>(plan);
        FilterOperator filter = (FilterOperator)plan;
        Assert.IsType<ScanOperator>(filter.Source);
    }

    [Fact]
    public void Plan_SelectWithProjection_ProducesProjectOverScan()
    {
        TableCatalog catalog = CreateCatalogWithCsv("test", "dummy.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectColumn(new ColumnReference("a"), "col_a")],
            From: new FromClause(new TableReference("test")));

        IQueryOperator plan = planner.Plan(statement);

        Assert.IsType<ProjectOperator>(plan);
        ProjectOperator project = (ProjectOperator)plan;
        Assert.IsType<ScanOperator>(project.Source);
    }

    [Fact]
    public void Plan_SelectWithWhereAndProjection_ProducesProjectOverFilterOverScan()
    {
        TableCatalog catalog = CreateCatalogWithCsv("test", "dummy.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectColumn(new ColumnReference("a"))],
            From: new FromClause(new TableReference("test")),
            Where: new BinaryExpression(
                new ColumnReference("a"),
                BinaryOperator.GreaterThan,
                new LiteralExpression(0)));

        IQueryOperator plan = planner.Plan(statement);

        // Plan: Project -> Filter -> Scan
        Assert.IsType<ProjectOperator>(plan);
        ProjectOperator project = (ProjectOperator)plan;
        Assert.IsType<FilterOperator>(project.Source);
        FilterOperator filter = (FilterOperator)project.Source;
        Assert.IsType<ScanOperator>(filter.Source);
    }

    [Fact]
    public void Plan_WithJoin_ProducesJoinOperator()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        catalog.Register(new TableDescriptor("csv", "left_table", "left.csv", new Dictionary<string, string>()));
        catalog.Register(new TableDescriptor("csv", "right_table", "right.csv", new Dictionary<string, string>()));

        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("left_table")),
            Joins: [new JoinClause(
                JoinType.Inner,
                new TableReference("right_table"),
                new BinaryExpression(
                    new ColumnReference("id"),
                    BinaryOperator.Equal,
                    new ColumnReference("id")))]);

        IQueryOperator plan = planner.Plan(statement);

        Assert.IsType<JoinOperator>(plan);
        JoinOperator join = (JoinOperator)plan;
        Assert.Equal(JoinType.Inner, join.Type);
    }

    [Fact]
    public void Plan_WithOrderBy_ProducesOrderByOperator()
    {
        TableCatalog catalog = CreateCatalogWithCsv("test", "dummy.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("test")),
            OrderBy: new OrderByClause(
            [
                new OrderByItem(new ColumnReference("x"), SortDirection.Ascending)
            ]));

        IQueryOperator plan = planner.Plan(statement);

        Assert.IsType<OrderByOperator>(plan);
    }

    [Fact]
    public void Plan_WithLimit_ProducesLimitOperator()
    {
        TableCatalog catalog = CreateCatalogWithCsv("test", "dummy.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("test")),
            Limit: 10);

        IQueryOperator plan = planner.Plan(statement);

        Assert.IsType<LimitOperator>(plan);
        LimitOperator limit = (LimitOperator)plan;
        Assert.Equal(10, limit.Limit);
    }

    [Fact]
    public void Plan_WithOffset_ProducesLimitWithOffset()
    {
        TableCatalog catalog = CreateCatalogWithCsv("test", "dummy.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("test")),
            Limit: 5,
            Offset: 10);

        IQueryOperator plan = planner.Plan(statement);

        Assert.IsType<LimitOperator>(plan);
        LimitOperator limit = (LimitOperator)plan;
        Assert.Equal(5, limit.Limit);
        Assert.Equal(10, limit.Offset);
    }

    [Fact]
    public void Plan_WithOrderByAndLimit_ProducesLimitOverOrderBy()
    {
        TableCatalog catalog = CreateCatalogWithCsv("test", "dummy.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("test")),
            OrderBy: new OrderByClause(
            [
                new OrderByItem(new ColumnReference("x"), SortDirection.Descending)
            ]),
            Limit: 3);

        IQueryOperator plan = planner.Plan(statement);

        Assert.IsType<LimitOperator>(plan);
        LimitOperator limit = (LimitOperator)plan;
        Assert.IsType<OrderByOperator>(limit.Source);
    }

    [Fact]
    public void Plan_WithAlias_ProducesAliasOperator()
    {
        TableCatalog catalog = CreateCatalogWithCsv("test", "dummy.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("test", "t")));

        IQueryOperator plan = planner.Plan(statement);

        Assert.IsType<AliasOperator>(plan);
        AliasOperator alias = (AliasOperator)plan;
        Assert.Equal("t", alias.Alias);
    }

    [Fact]
    public void Plan_Subquery_ProducesSubqueryOperator()
    {
        TableCatalog catalog = CreateCatalogWithCsv("test", "dummy.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement innerSelect = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("test")));

        SelectStatement outerSelect = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new SubquerySource(innerSelect, "sub")));

        IQueryOperator plan = planner.Plan(outerSelect);

        Assert.IsType<SubqueryOperator>(plan);
    }

    [Fact]
    public void Plan_UnknownTable_Throws()
    {
        TableCatalog catalog = new();
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("nonexistent")));

        Assert.Throws<KeyNotFoundException>(() => planner.Plan(statement));
    }

    // ─────────────── End-to-end execution test with in-memory CSV ───────────────

    [Fact]
    public async Task Plan_ExecutesAgainstRealCsvProvider()
    {
        // Create a small CSV fixture.
        string csvPath = Path.Combine(Path.GetTempPath(), $"planner_test_{Guid.NewGuid():N}.csv");
        try
        {
            await File.WriteAllTextAsync(csvPath, "name,age\nAlice,30\nBob,25\nCharlie,35\n");

            TableCatalog catalog = new();
            catalog.RegisterProvider("csv", () => new CsvTableProvider());
            catalog.Register(new TableDescriptor("csv", "people", csvPath, new Dictionary<string, string>()));

            QueryPlanner planner = new(catalog, DefaultFunctions);

            // SELECT * FROM people WHERE age > 28
            SelectStatement statement = new(
                Columns: [new SelectAllColumns()],
                From: new FromClause(new TableReference("people")),
                Where: new BinaryExpression(
                    new ColumnReference("age"),
                    BinaryOperator.GreaterThan,
                    new LiteralExpression(28)));

            IQueryOperator plan = planner.Plan(statement);

            ExecutionContext context = new(
                CancellationToken.None,
                FunctionRegistry.CreateDefault(),
                catalog);

            List<Row> rows = new();
            await foreach (Row row in plan.ExecuteAsync(context))
            {
                rows.Add(row);
            }

            Assert.Equal(2, rows.Count);
            // Age values are strings from CSV, but comparison works via float parsing.
            Assert.Contains(rows, r => r["name"].AsString() == "Alice");
            Assert.Contains(rows, r => r["name"].AsString() == "Charlie");
        }
        finally
        {
            File.Delete(csvPath);
        }
    }

    [Fact]
    public async Task Plan_SelectWithOrderByAndLimit_ExecutesCorrectly()
    {
        string csvPath = Path.Combine(Path.GetTempPath(), $"planner_order_{Guid.NewGuid():N}.csv");
        try
        {
            await File.WriteAllTextAsync(csvPath, "val\n5\n1\n3\n2\n4\n");

            TableCatalog catalog = new();
            catalog.RegisterProvider("csv", () => new CsvTableProvider());
            catalog.Register(new TableDescriptor("csv", "data", csvPath, new Dictionary<string, string>()));

            QueryPlanner planner = new(catalog, DefaultFunctions);

            // SELECT * FROM data ORDER BY val ASC LIMIT 3
            SelectStatement statement = new(
                Columns: [new SelectAllColumns()],
                From: new FromClause(new TableReference("data")),
                OrderBy: new OrderByClause(
                [
                    new OrderByItem(new ColumnReference("val"), SortDirection.Ascending)
                ]),
                Limit: 3);

            IQueryOperator plan = planner.Plan(statement);

            ExecutionContext context = new(
                CancellationToken.None,
                FunctionRegistry.CreateDefault(),
                catalog);

            List<Row> rows = new();
            await foreach (Row row in plan.ExecuteAsync(context))
            {
                rows.Add(row);
            }

            Assert.Equal(3, rows.Count);
            // CSV auto-detects numeric values as Scalar.
            Assert.Equal(1f, rows[0]["val"].AsScalar());
            Assert.Equal(2f, rows[1]["val"].AsScalar());
            Assert.Equal(3f, rows[2]["val"].AsScalar());
        }
        finally
        {
            File.Delete(csvPath);
        }
    }
}
