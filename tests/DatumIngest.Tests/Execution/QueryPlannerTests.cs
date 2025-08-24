using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Indexing;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

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

        // Unwrap the ProjectOperator added for SELECT * column ordering.
        if (plan is ProjectOperator projectWrap)
            plan = projectWrap.Source;

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
        OrderByOperator orderBy = Assert.IsType<OrderByOperator>(limit.Source);
        Assert.Equal(3, orderBy.TopNRows);
    }

    [Fact]
    public void Plan_WithOrderByLimitAndOffset_PassesTopNRowsAsSumOfLimitAndOffset()
    {
        TableCatalog catalog = CreateCatalogWithCsv("test", "dummy.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("test")),
            OrderBy: new OrderByClause(
            [
                new OrderByItem(new ColumnReference("x"), SortDirection.Ascending)
            ]),
            Limit: 5,
            Offset: 10);

        IQueryOperator plan = planner.Plan(statement);

        Assert.IsType<LimitOperator>(plan);
        LimitOperator limit = (LimitOperator)plan;
        OrderByOperator orderBy = Assert.IsType<OrderByOperator>(limit.Source);
        Assert.Equal(15, orderBy.TopNRows);
    }

    [Fact]
    public void Plan_WithOrderByAndNoLimit_TopNRowsIsNull()
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

        OrderByOperator orderBy = Assert.IsType<OrderByOperator>(plan);
        Assert.Null(orderBy.TopNRows);
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
            // CSV infers all integer columns as Int32 minimum to avoid silent truncation.
            Assert.Equal(1, rows[0]["val"].AsInt32());
            Assert.Equal(2, rows[1]["val"].AsInt32());
            Assert.Equal(3, rows[2]["val"].AsInt32());
        }
        finally
        {
            File.Delete(csvPath);
        }
    }

    // ─────────────── Predicate pushdown tests ───────────────

    [Fact]
    public void Plan_PredicatePushdown_SingleTableWhereGoesBeforeJoin()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        catalog.Register(new TableDescriptor("csv", "orders", "orders.csv", new Dictionary<string, string>()));
        catalog.Register(new TableDescriptor("csv", "items", "items.csv", new Dictionary<string, string>()));

        QueryPlanner planner = new(catalog, DefaultFunctions);

        // SELECT * FROM orders AS o JOIN items AS i ON o.id = i.order_id WHERE i.price > 100
        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("orders", "o")),
            Joins: [new JoinClause(
                JoinType.Inner,
                new TableReference("items", "i"),
                new BinaryExpression(
                    new ColumnReference("o", "id"),
                    BinaryOperator.Equal,
                    new ColumnReference("i", "order_id")))],
            Where: new BinaryExpression(
                new ColumnReference("i", "price"),
                BinaryOperator.GreaterThan,
                new LiteralExpression(100)));

        IQueryOperator plan = planner.Plan(statement);

        // Unwrap the ProjectOperator added for SELECT * column ordering.
        if (plan is ProjectOperator projectWrap1)
            plan = projectWrap1.Source;

        // The WHERE predicate on "i" should be pushed below the join as a Filter on the right side.
        // Plan: JoinOperator (no top-level filter since it was pushed down)
        Assert.IsType<JoinOperator>(plan);
        JoinOperator join = (JoinOperator)plan;

        // Right side should have a Filter wrapping the Alias/Scan for "items".
        IQueryOperator rightSide = join.Right;
        Assert.IsType<FilterOperator>(rightSide);
    }

    [Fact]
    public void Plan_PredicatePushdown_MultiTablePredicateStaysAboveJoin()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        catalog.Register(new TableDescriptor("csv", "a_table", "a.csv", new Dictionary<string, string>()));
        catalog.Register(new TableDescriptor("csv", "b_table", "b.csv", new Dictionary<string, string>()));

        QueryPlanner planner = new(catalog, DefaultFunctions);

        // SELECT * FROM a_table AS a JOIN b_table AS b ON a.id = b.id WHERE a.x > b.y
        // This predicate references both "a" and "b", so it cannot be pushed below the join.
        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("a_table", "a")),
            Joins: [new JoinClause(
                JoinType.Inner,
                new TableReference("b_table", "b"),
                new BinaryExpression(
                    new ColumnReference("a", "id"),
                    BinaryOperator.Equal,
                    new ColumnReference("b", "id")))],
            Where: new BinaryExpression(
                new ColumnReference("a", "x"),
                BinaryOperator.GreaterThan,
                new ColumnReference("b", "y")));

        IQueryOperator plan = planner.Plan(statement);

        // Unwrap the ProjectOperator added for SELECT * column ordering.
        if (plan is ProjectOperator projectWrap2)
            plan = projectWrap2.Source;

        // Multi-table predicate stays above the join.
        Assert.IsType<FilterOperator>(plan);
        FilterOperator filter = (FilterOperator)plan;
        Assert.IsType<JoinOperator>(filter.Source);
    }

    [Fact]
    public void Plan_PredicatePushdown_AndDecomposition_PushesEachHalf()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        catalog.Register(new TableDescriptor("csv", "left_data", "l.csv", new Dictionary<string, string>()));
        catalog.Register(new TableDescriptor("csv", "right_data", "r.csv", new Dictionary<string, string>()));

        QueryPlanner planner = new(catalog, DefaultFunctions);

        // SELECT * FROM left_data AS l JOIN right_data AS r ON l.id = r.id
        // WHERE l.status = 'active' AND r.score > 50
        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("left_data", "l")),
            Joins: [new JoinClause(
                JoinType.Inner,
                new TableReference("right_data", "r"),
                new BinaryExpression(
                    new ColumnReference("l", "id"),
                    BinaryOperator.Equal,
                    new ColumnReference("r", "id")))],
            Where: new BinaryExpression(
                new BinaryExpression(
                    new ColumnReference("l", "status"),
                    BinaryOperator.Equal,
                    new LiteralExpression("active")),
                BinaryOperator.And,
                new BinaryExpression(
                    new ColumnReference("r", "score"),
                    BinaryOperator.GreaterThan,
                    new LiteralExpression(50))));

        IQueryOperator plan = planner.Plan(statement);

        // Unwrap the ProjectOperator added for SELECT * column ordering.
        if (plan is ProjectOperator projectWrap3)
            plan = projectWrap3.Source;

        // Both predicates pushed down — no top-level filter.
        Assert.IsType<JoinOperator>(plan);
        JoinOperator join = (JoinOperator)plan;

        // Left side: Filter(l.status = 'active') -> Alias -> Scan
        Assert.IsType<FilterOperator>(join.Left);

        // Right side: Filter(r.score > 50) -> Alias -> Scan
        Assert.IsType<FilterOperator>(join.Right);
    }

    [Fact]
    public void Plan_PredicatePushdown_LeftJoin_DoesNotPush()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        catalog.Register(new TableDescriptor("csv", "main", "m.csv", new Dictionary<string, string>()));
        catalog.Register(new TableDescriptor("csv", "detail", "d.csv", new Dictionary<string, string>()));

        QueryPlanner planner = new(catalog, DefaultFunctions);

        // SELECT * FROM main AS m LEFT JOIN detail AS d ON m.id = d.main_id WHERE d.value > 10
        // LEFT JOIN prevents predicate pushdown.
        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("main", "m")),
            Joins: [new JoinClause(
                JoinType.Left,
                new TableReference("detail", "d"),
                new BinaryExpression(
                    new ColumnReference("m", "id"),
                    BinaryOperator.Equal,
                    new ColumnReference("d", "main_id")))],
            Where: new BinaryExpression(
                new ColumnReference("d", "value"),
                BinaryOperator.GreaterThan,
                new LiteralExpression(10)));

        IQueryOperator plan = planner.Plan(statement);

        // Unwrap the ProjectOperator added for SELECT * column ordering.
        if (plan is ProjectOperator projectWrap4)
            plan = projectWrap4.Source;

        // Predicate should stay above the join.
        Assert.IsType<FilterOperator>(plan);
        FilterOperator filter = (FilterOperator)plan;
        Assert.IsType<JoinOperator>(filter.Source);
    }

    // ─────────────── Transitive predicate inference tests ───────────────

    /// <summary>
    /// When <c>WHERE a.x = 10</c> and <c>JOIN ON a.x = b.x</c>, the planner
    /// should derive <c>b.x = 10</c> and push it as a filter on <c>b</c>'s scan.
    /// Both sides of the join should have filters.
    /// </summary>
    [Fact]
    public void Plan_TransitivePredicateInference_DerivesPushdownForJoinPartner()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        catalog.Register(new TableDescriptor("csv", "products", "p.csv", new Dictionary<string, string>()));
        catalog.Register(new TableDescriptor("csv", "order_products", "op.csv", new Dictionary<string, string>()));

        QueryPlanner planner = new(catalog, DefaultFunctions);

        // SELECT * FROM products AS p
        // JOIN order_products AS op ON p.product_id = op.product_id
        // WHERE p.product_id = 10
        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("products", "p")),
            Joins: [new JoinClause(
                JoinType.Inner,
                new TableReference("order_products", "op"),
                new BinaryExpression(
                    new ColumnReference("p", "product_id"),
                    BinaryOperator.Equal,
                    new ColumnReference("op", "product_id")))],
            Where: new BinaryExpression(
                new ColumnReference("p", "product_id"),
                BinaryOperator.Equal,
                new LiteralExpression(10)));

        IQueryOperator plan = planner.Plan(statement);

        // Unwrap the ProjectOperator added for SELECT * column ordering.
        if (plan is ProjectOperator projectWrap)
            plan = projectWrap.Source;

        // Both predicates should be pushed down — no top-level filter.
        Assert.IsType<JoinOperator>(plan);
        JoinOperator join = (JoinOperator)plan;

        // Left side (products): should have a filter for p.product_id = 10.
        Assert.IsType<FilterOperator>(join.Left);

        // Right side (order_products): should have a derived filter for op.product_id = 10.
        Assert.IsType<FilterOperator>(join.Right);
    }

    /// <summary>
    /// Transitive inference should not apply across LEFT JOINs because
    /// the right side can produce NULL rows.
    /// </summary>
    [Fact]
    public void Plan_TransitivePredicateInference_LeftJoin_NoDerivation()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        catalog.Register(new TableDescriptor("csv", "products", "p.csv", new Dictionary<string, string>()));
        catalog.Register(new TableDescriptor("csv", "order_products", "op.csv", new Dictionary<string, string>()));

        QueryPlanner planner = new(catalog, DefaultFunctions);

        // SELECT * FROM products AS p
        // LEFT JOIN order_products AS op ON p.product_id = op.product_id
        // WHERE p.product_id = 10
        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("products", "p")),
            Joins: [new JoinClause(
                JoinType.Left,
                new TableReference("order_products", "op"),
                new BinaryExpression(
                    new ColumnReference("p", "product_id"),
                    BinaryOperator.Equal,
                    new ColumnReference("op", "product_id")))],
            Where: new BinaryExpression(
                new ColumnReference("p", "product_id"),
                BinaryOperator.Equal,
                new LiteralExpression(10)));

        IQueryOperator plan = planner.Plan(statement);

        // Unwrap the ProjectOperator added for SELECT * column ordering.
        if (plan is ProjectOperator projectWrap)
            plan = projectWrap.Source;

        // LEFT JOIN prevents both inference AND pushdown. The original
        // predicate stays above the join.
        Assert.IsType<FilterOperator>(plan);
        FilterOperator topFilter = (FilterOperator)plan;
        Assert.IsType<JoinOperator>(topFilter.Source);

        JoinOperator join = (JoinOperator)topFilter.Source;

        // Right side should NOT have a derived filter.
        Assert.IsNotType<FilterOperator>(join.Right);
    }

    /// <summary>
    /// With multiple equi-join columns, transitive inference derives predicates
    /// for each matching column.
    /// </summary>
    [Fact]
    public void Plan_TransitivePredicateInference_MultipleColumns_DerivesAll()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        catalog.Register(new TableDescriptor("csv", "src", "s.csv", new Dictionary<string, string>()));
        catalog.Register(new TableDescriptor("csv", "tgt", "t.csv", new Dictionary<string, string>()));

        QueryPlanner planner = new(catalog, DefaultFunctions);

        // SELECT * FROM src AS s
        // JOIN tgt AS t ON s.a = t.a AND s.b = t.b
        // WHERE s.a = 1 AND s.b = 2
        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("src", "s")),
            Joins: [new JoinClause(
                JoinType.Inner,
                new TableReference("tgt", "t"),
                new BinaryExpression(
                    new BinaryExpression(
                        new ColumnReference("s", "a"),
                        BinaryOperator.Equal,
                        new ColumnReference("t", "a")),
                    BinaryOperator.And,
                    new BinaryExpression(
                        new ColumnReference("s", "b"),
                        BinaryOperator.Equal,
                        new ColumnReference("t", "b"))))],
            Where: new BinaryExpression(
                new BinaryExpression(
                    new ColumnReference("s", "a"),
                    BinaryOperator.Equal,
                    new LiteralExpression(1)),
                BinaryOperator.And,
                new BinaryExpression(
                    new ColumnReference("s", "b"),
                    BinaryOperator.Equal,
                    new LiteralExpression(2))));

        IQueryOperator plan = planner.Plan(statement);

        // Unwrap the ProjectOperator added for SELECT * column ordering.
        if (plan is ProjectOperator projectWrap)
            plan = projectWrap.Source;

        // All predicates pushed down — no top-level filter.
        Assert.IsType<JoinOperator>(plan);
        JoinOperator join = (JoinOperator)plan;

        // Both sides should have filters (original + derived).
        Assert.IsType<FilterOperator>(join.Left);
        Assert.IsType<FilterOperator>(join.Right);
    }

    // ─────────────── Projection pushdown tests ───────────────

    [Fact]
    public void Plan_ProjectionPushdown_OnlyRequestedColumnsPassedToScan()
    {
        TableCatalog catalog = CreateCatalogWithCsv("products", "products.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        // SELECT name FROM products
        SelectStatement statement = new(
            Columns: [new SelectColumn(new ColumnReference("name"))],
            From: new FromClause(new TableReference("products")));

        IQueryOperator plan = planner.Plan(statement);

        // Plan: Project -> Scan (no alias since no alias specified)
        Assert.IsType<ProjectOperator>(plan);
        ProjectOperator project = (ProjectOperator)plan;
        Assert.IsType<ScanOperator>(project.Source);
        ScanOperator scan = (ScanOperator)project.Source;

        Assert.NotNull(scan.RequiredColumns);
        Assert.Contains("name", scan.RequiredColumns);
    }

    [Fact]
    public void Plan_ProjectionPushdown_SelectStar_RequestsAllColumns()
    {
        TableCatalog catalog = CreateCatalogWithCsv("data", "data.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        // SELECT * FROM data
        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")));

        IQueryOperator plan = planner.Plan(statement);

        Assert.IsType<ScanOperator>(plan);
        ScanOperator scan = (ScanOperator)plan;

        // SELECT * means all columns — RequiredColumns should be null.
        Assert.Null(scan.RequiredColumns);
    }

    [Fact]
    public void Plan_ProjectionPushdown_IncludesWhereColumns()
    {
        TableCatalog catalog = CreateCatalogWithCsv("employees", "emp.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        // SELECT name FROM employees WHERE age > 30
        // Must include both "name" (SELECT) and "age" (WHERE).
        SelectStatement statement = new(
            Columns: [new SelectColumn(new ColumnReference("name"))],
            From: new FromClause(new TableReference("employees")),
            Where: new BinaryExpression(
                new ColumnReference("age"),
                BinaryOperator.GreaterThan,
                new LiteralExpression(30)));

        IQueryOperator plan = planner.Plan(statement);

        // Walk down to find the ScanOperator.
        ScanOperator? scan = FindOperator<ScanOperator>(plan);
        Assert.NotNull(scan);
        Assert.NotNull(scan!.RequiredColumns);
        Assert.Contains("name", scan.RequiredColumns);
        Assert.Contains("age", scan.RequiredColumns);
    }

    [Fact]
    public void Plan_ProjectionPushdown_IncludesJoinOnColumns()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        catalog.Register(new TableDescriptor("csv", "users", "u.csv", new Dictionary<string, string>()));
        catalog.Register(new TableDescriptor("csv", "orders", "o.csv", new Dictionary<string, string>()));

        QueryPlanner planner = new(catalog, DefaultFunctions);

        // SELECT u.name FROM users AS u JOIN orders AS o ON u.id = o.user_id
        SelectStatement statement = new(
            Columns: [new SelectColumn(new ColumnReference("u", "name"))],
            From: new FromClause(new TableReference("users", "u")),
            Joins: [new JoinClause(
                JoinType.Inner,
                new TableReference("orders", "o"),
                new BinaryExpression(
                    new ColumnReference("u", "id"),
                    BinaryOperator.Equal,
                    new ColumnReference("o", "user_id")))]);

        IQueryOperator plan = planner.Plan(statement);

        // Find the ScanOperator for "users" — should include "name" and "id".
        JoinOperator join = FindOperator<JoinOperator>(plan)!;
        Assert.NotNull(join);

        ScanOperator? usersScan = FindOperator<ScanOperator>(join.Left);
        Assert.NotNull(usersScan);
        Assert.NotNull(usersScan!.RequiredColumns);
        Assert.Contains("name", usersScan.RequiredColumns);
        Assert.Contains("id", usersScan.RequiredColumns);
    }

    /// <summary>
    /// Walks the operator tree depth-first to find the first operator of type T.
    /// </summary>
    private static T? FindOperator<T>(IQueryOperator op) where T : class, IQueryOperator
    {
        if (op is T target)
        {
            return target;
        }

        return op switch
        {
            FilterOperator filter => FindOperator<T>(filter.Source),
            ProjectOperator project => FindOperator<T>(project.Source),
            JoinOperator join => FindOperator<T>(join.Left) ?? FindOperator<T>(join.Right),
            OrderByOperator orderBy => FindOperator<T>(orderBy.Source),
            LimitOperator limit => FindOperator<T>(limit.Source),
            AliasOperator alias => FindOperator<T>(alias.Source),
            SubqueryOperator subquery => FindOperator<T>(subquery.InnerOperator),
            LateMaterializationOperator late => FindOperator<T>(late.Child),
            DistinctOperator distinct => FindOperator<T>(distinct.Source),
            GroupByOperator groupBy => FindOperator<T>(groupBy.Source),
            _ => null,
        };
    }

    // ─────────────── Greedy join reordering tests ───────────────

    /// <summary>
    /// When all joins are INNER and all sources have estimated row counts,
    /// the planner should place the largest table on the streaming (FROM/left)
    /// side so that LIMIT can short-circuit the probe without reading the
    /// entire build side.
    /// </summary>
    [Fact]
    public void Plan_InnerJoins_ReordersLargestTableToProbe()
    {
        // Arrange: small (100 rows) JOIN large (10_000 rows).
        // Without reordering, small is probe, large is build.
        // With reordering, large becomes probe, small becomes build.
        TableCatalog catalog = new();
        catalog.RegisterProvider("stub_small", () => new StubRowCountProvider(estimatedRowCount: 100));
        catalog.RegisterProvider("stub_large", () => new StubRowCountProvider(estimatedRowCount: 10_000));
        catalog.Register(new TableDescriptor("stub_small", "small_table", "s.csv", new Dictionary<string, string>()));
        catalog.Register(new TableDescriptor("stub_large", "large_table", "l.csv", new Dictionary<string, string>()));

        QueryPlanner planner = new(catalog, DefaultFunctions);

        // SELECT * FROM small_table JOIN large_table ON small_table.id = large_table.id
        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("small_table")),
            Joins: [new JoinClause(
                JoinType.Inner,
                new TableReference("large_table"),
                new BinaryExpression(
                    new ColumnReference("small_table", "id"),
                    BinaryOperator.Equal,
                    new ColumnReference("large_table", "id")))]);

        IQueryOperator plan = planner.Plan(statement);

        // The outermost join's left (probe) side should be the large table.
        JoinOperator join = FindOperator<JoinOperator>(plan)!;
        Assert.NotNull(join);

        ScanOperator? probeScan = FindOperator<ScanOperator>(join.Left);
        Assert.NotNull(probeScan);
        Assert.Equal("large_table", probeScan!.Descriptor.Name);

        ScanOperator? buildScan = FindOperator<ScanOperator>(join.Right);
        Assert.NotNull(buildScan);
        Assert.Equal("small_table", buildScan!.Descriptor.Name);
    }

    /// <summary>
    /// Three-table INNER join: the largest table should become the probe,
    /// and the two smaller tables should be ordered smallest-first on the build
    /// side, respecting ON-condition connectivity.
    /// </summary>
    [Fact]
    public void Plan_ThreeTableInnerJoin_ReordersCorrectly()
    {
        // orders (10_000) JOIN products (500) ON orders.pid = products.id
        //                  JOIN categories (50) ON products.cid = categories.id
        // Expected reording: FROM orders (largest, probe)
        //   JOIN categories (smallest satisfiable) ON ...
        //   Actually, categories.id only connects to products, not orders,
        //   so categories can't go first.
        // Expected: FROM orders JOIN products ON ... JOIN categories ON ...
        TableCatalog catalog = new();
        catalog.RegisterProvider("stub_orders", () => new StubRowCountProvider(estimatedRowCount: 10_000));
        catalog.RegisterProvider("stub_products", () => new StubRowCountProvider(estimatedRowCount: 500));
        catalog.RegisterProvider("stub_categories", () => new StubRowCountProvider(estimatedRowCount: 50));
        catalog.Register(new TableDescriptor("stub_orders", "orders", "o.csv", new Dictionary<string, string>()));
        catalog.Register(new TableDescriptor("stub_products", "products", "p.csv", new Dictionary<string, string>()));
        catalog.Register(new TableDescriptor("stub_categories", "categories", "c.csv", new Dictionary<string, string>()));

        QueryPlanner planner = new(catalog, DefaultFunctions);

        // Original SQL order: FROM products JOIN orders ON ... JOIN categories ON ...
        // (products is the smallest, orders is largest but written as JOIN)
        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("products")),
            Joins:
            [
                new JoinClause(
                    JoinType.Inner,
                    new TableReference("orders"),
                    new BinaryExpression(
                        new ColumnReference("products", "id"),
                        BinaryOperator.Equal,
                        new ColumnReference("orders", "product_id"))),
                new JoinClause(
                    JoinType.Inner,
                    new TableReference("categories"),
                    new BinaryExpression(
                        new ColumnReference("products", "category_id"),
                        BinaryOperator.Equal,
                        new ColumnReference("categories", "id"))),
            ]);

        IQueryOperator plan = planner.Plan(statement);

        // orders (largest) should be the probe.
        JoinOperator outerJoin = FindOperator<JoinOperator>(plan)!;
        Assert.NotNull(outerJoin);

        // The left side of the outer join should be another JoinOperator
        // (orders JOIN products), and the right side should be categories.
        JoinOperator innerJoin = Assert.IsType<JoinOperator>(FindDirectJoin(outerJoin.Left));
        Assert.NotNull(innerJoin);

        // Probe (deepest left) should be orders.
        ScanOperator? ordersScan = FindOperator<ScanOperator>(innerJoin.Left);
        Assert.NotNull(ordersScan);
        Assert.Equal("orders", ordersScan!.Descriptor.Name);

        // First build should be products (connects to orders).
        ScanOperator? productsScan = FindOperator<ScanOperator>(innerJoin.Right);
        Assert.NotNull(productsScan);
        Assert.Equal("products", productsScan!.Descriptor.Name);

        // Second build should be categories (connects to products, now available).
        ScanOperator? categoriesScan = FindOperator<ScanOperator>(outerJoin.Right);
        Assert.NotNull(categoriesScan);
        Assert.Equal("categories", categoriesScan!.Descriptor.Name);
    }

    /// <summary>
    /// LEFT OUTER joins should not trigger reordering — only INNER joins
    /// are eligible because changing the probe side changes semantics.
    /// </summary>
    [Fact]
    public void Plan_LeftJoin_DoesNotReorder()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("stub_small", () => new StubRowCountProvider(estimatedRowCount: 100));
        catalog.RegisterProvider("stub_large", () => new StubRowCountProvider(estimatedRowCount: 10_000));
        catalog.Register(new TableDescriptor("stub_small", "small_table", "s.csv", new Dictionary<string, string>()));
        catalog.Register(new TableDescriptor("stub_large", "large_table", "l.csv", new Dictionary<string, string>()));

        QueryPlanner planner = new(catalog, DefaultFunctions);

        // SELECT * FROM small_table LEFT JOIN large_table ON ...
        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("small_table")),
            Joins: [new JoinClause(
                JoinType.Left,
                new TableReference("large_table"),
                new BinaryExpression(
                    new ColumnReference("small_table", "id"),
                    BinaryOperator.Equal,
                    new ColumnReference("large_table", "id")))]);

        IQueryOperator plan = planner.Plan(statement);

        // Original order preserved: small_table is probe, large_table is build.
        JoinOperator join = FindOperator<JoinOperator>(plan)!;
        Assert.NotNull(join);

        ScanOperator? probeScan = FindOperator<ScanOperator>(join.Left);
        Assert.NotNull(probeScan);
        Assert.Equal("small_table", probeScan!.Descriptor.Name);
    }

    /// <summary>
    /// When a source lacks an estimated row count, reordering is skipped
    /// since we cannot determine relative sizes.
    /// </summary>
    [Fact]
    public void Plan_MissingRowCount_DoesNotReorder()
    {
        // CSV provider returns null for EstimatedRowCount.
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        catalog.Register(new TableDescriptor("csv", "small_table", "s.csv", new Dictionary<string, string>()));
        catalog.Register(new TableDescriptor("csv", "large_table", "l.csv", new Dictionary<string, string>()));

        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("small_table")),
            Joins: [new JoinClause(
                JoinType.Inner,
                new TableReference("large_table"),
                new BinaryExpression(
                    new ColumnReference("small_table", "id"),
                    BinaryOperator.Equal,
                    new ColumnReference("large_table", "id")))]);

        IQueryOperator plan = planner.Plan(statement);

        // Original order preserved: small_table is probe.
        JoinOperator join = FindOperator<JoinOperator>(plan)!;
        Assert.NotNull(join);

        ScanOperator? probeScan = FindOperator<ScanOperator>(join.Left);
        Assert.NotNull(probeScan);
        Assert.Equal("small_table", probeScan!.Descriptor.Name);
    }

    /// <summary>
    /// When the largest table is already the FROM source, no reordering occurs
    /// because the left-deep tree already has it as the probe.
    /// </summary>
    [Fact]
    public void Plan_LargestAlreadyProbe_DoesNotReorder()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("stub_small", () => new StubRowCountProvider(estimatedRowCount: 100));
        catalog.RegisterProvider("stub_large", () => new StubRowCountProvider(estimatedRowCount: 10_000));
        catalog.Register(new TableDescriptor("stub_small", "small_table", "s.csv", new Dictionary<string, string>()));
        catalog.Register(new TableDescriptor("stub_large", "large_table", "l.csv", new Dictionary<string, string>()));

        QueryPlanner planner = new(catalog, DefaultFunctions);

        // FROM large_table JOIN small_table — already optimal.
        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("large_table")),
            Joins: [new JoinClause(
                JoinType.Inner,
                new TableReference("small_table"),
                new BinaryExpression(
                    new ColumnReference("large_table", "id"),
                    BinaryOperator.Equal,
                    new ColumnReference("small_table", "id")))]);

        IQueryOperator plan = planner.Plan(statement);

        // Original order preserved: large_table is probe.
        JoinOperator join = FindOperator<JoinOperator>(plan)!;
        Assert.NotNull(join);

        ScanOperator? probeScan = FindOperator<ScanOperator>(join.Left);
        Assert.NotNull(probeScan);
        Assert.Equal("large_table", probeScan!.Descriptor.Name);
    }

    /// <summary>
    /// Finds the nearest <see cref="JoinOperator"/> by walking through
    /// transparent wrappers (alias, filter, project).
    /// </summary>
    private static JoinOperator? FindDirectJoin(IQueryOperator operatorNode)
    {
        IQueryOperator current = operatorNode;
        while (true)
        {
            switch (current)
            {
                case JoinOperator join:
                    return join;
                case AliasOperator alias:
                    current = alias.Source;
                    break;
                case FilterOperator filter:
                    current = filter.Source;
                    break;
                case ProjectOperator project:
                    current = project.Source;
                    break;
                default:
                    return null;
            }
        }
    }

    // ─────────────── PlanAsync late materialization tests ───────────────

    private static TableCatalog CreateCatalogWithKeyedProvider(
        string tableName,
        string filePath,
        string providerName,
        ITableProvider provider)
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider(providerName, () => provider);
        catalog.Register(new TableDescriptor(providerName, tableName, filePath,
            new Dictionary<string, string>()));
        return catalog;
    }

    /// <summary>
    /// When a keyed provider has expensive output-only columns, PlanAsync wraps
    /// the plan with a <see cref="LateMaterializationOperator"/>.
    /// </summary>
    [Fact]
    public async Task PlanAsync_ExpensiveOutputColumn_InsertsLateMaterialization()
    {
        StubKeyedProvider provider = new(
            keyColumn: "id",
            expensiveColumns: new[] { "payload" });

        TableCatalog catalog = CreateCatalogWithKeyedProvider(
            "data", "data.zip", "stub", provider);
        QueryPlanner planner = new(catalog, DefaultFunctions);

        // SELECT id, payload FROM data WHERE id = 1
        // "payload" is expensive and only in SELECT → should be deferred.
        SelectStatement statement = new(
            Columns:
            [
                new SelectColumn(new ColumnReference("id")),
                new SelectColumn(new ColumnReference("payload")),
            ],
            From: new FromClause(new TableReference("data")),
            Where: new BinaryExpression(
                new ColumnReference("id"),
                BinaryOperator.Equal,
                new LiteralExpression(1)));

        IQueryOperator plan = await planner.PlanAsync(statement, CancellationToken.None);

        LateMaterializationOperator? lateOp = FindOperator<LateMaterializationOperator>(plan);
        Assert.NotNull(lateOp);
        Assert.Equal("id", lateOp.KeyColumn);
        Assert.Contains("payload", lateOp.DeferredColumns);
    }

    /// <summary>
    /// When the expensive column is used in WHERE, it cannot be deferred, so
    /// PlanAsync does not insert a <see cref="LateMaterializationOperator"/>.
    /// </summary>
    [Fact]
    public async Task PlanAsync_ExpensiveColumnInWhere_NoLateMaterialization()
    {
        StubKeyedProvider provider = new(
            keyColumn: "id",
            expensiveColumns: new[] { "payload" });

        TableCatalog catalog = CreateCatalogWithKeyedProvider(
            "data", "data.zip", "stub", provider);
        QueryPlanner planner = new(catalog, DefaultFunctions);

        // SELECT id FROM data WHERE payload != ''
        // "payload" is used in WHERE → cannot defer.
        SelectStatement statement = new(
            Columns: [new SelectColumn(new ColumnReference("id"))],
            From: new FromClause(new TableReference("data")),
            Where: new BinaryExpression(
                new ColumnReference("payload"),
                BinaryOperator.NotEqual,
                new LiteralExpression("")));

        IQueryOperator plan = await planner.PlanAsync(statement, CancellationToken.None);

        LateMaterializationOperator? lateOp = FindOperator<LateMaterializationOperator>(plan);
        Assert.Null(lateOp);
    }

    /// <summary>
    /// SELECT * requires all columns and cannot determine output-only, so
    /// PlanAsync does not insert late materialization.
    /// </summary>
    [Fact]
    public async Task PlanAsync_SelectStar_NoLateMaterialization()
    {
        StubKeyedProvider provider = new(
            keyColumn: "id",
            expensiveColumns: new[] { "payload" });

        TableCatalog catalog = CreateCatalogWithKeyedProvider(
            "data", "data.zip", "stub", provider);
        QueryPlanner planner = new(catalog, DefaultFunctions);

        // SELECT * FROM data
        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("data")));

        IQueryOperator plan = await planner.PlanAsync(statement, CancellationToken.None);

        LateMaterializationOperator? lateOp = FindOperator<LateMaterializationOperator>(plan);
        Assert.Null(lateOp);
    }

    /// <summary>
    /// When the expensive column is used in a JOIN ON condition, it cannot be deferred.
    /// </summary>
    [Fact]
    public async Task PlanAsync_ExpensiveColumnInJoinOn_NoLateMaterialization()
    {
        StubKeyedProvider provider = new(
            keyColumn: "id",
            expensiveColumns: new[] { "payload" });

        TableCatalog catalog = CreateCatalogWithKeyedProvider(
            "data", "data.zip", "stub", provider);
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        catalog.Register(new TableDescriptor("csv", "other", "other.csv",
            new Dictionary<string, string>()));

        QueryPlanner planner = new(catalog, DefaultFunctions);

        // SELECT d.id FROM data d JOIN other o ON d.payload = o.val
        // "payload" used in JOIN ON → cannot defer.
        SelectStatement statement = new(
            Columns: [new SelectColumn(new ColumnReference("d", "id"))],
            From: new FromClause(new TableReference("data", "d")),
            Joins:
            [
                new JoinClause(
                    JoinType.Inner,
                    new TableReference("other", "o"),
                    new BinaryExpression(
                        new ColumnReference("d", "payload"),
                        BinaryOperator.Equal,
                        new ColumnReference("o", "val"))),
            ]);

        IQueryOperator plan = await planner.PlanAsync(statement, CancellationToken.None);

        LateMaterializationOperator? lateOp = FindOperator<LateMaterializationOperator>(plan);
        Assert.Null(lateOp);
    }

    /// <summary>
    /// With a table alias, the deferred columns use the alias for column lookup.
    /// </summary>
    [Fact]
    public async Task PlanAsync_WithAlias_SetsAliasOnLateMaterialization()
    {
        StubKeyedProvider provider = new(
            keyColumn: "id",
            expensiveColumns: new[] { "payload" });

        TableCatalog catalog = CreateCatalogWithKeyedProvider(
            "data", "data.zip", "stub", provider);
        QueryPlanner planner = new(catalog, DefaultFunctions);

        // SELECT d.id, d.payload FROM data d WHERE d.id = 1
        SelectStatement statement = new(
            Columns:
            [
                new SelectColumn(new ColumnReference("d", "id")),
                new SelectColumn(new ColumnReference("d", "payload")),
            ],
            From: new FromClause(new TableReference("data", "d")),
            Where: new BinaryExpression(
                new ColumnReference("d", "id"),
                BinaryOperator.Equal,
                new LiteralExpression(1)));

        IQueryOperator plan = await planner.PlanAsync(statement, CancellationToken.None);

        LateMaterializationOperator? lateOp = FindOperator<LateMaterializationOperator>(plan);
        Assert.NotNull(lateOp);
        Assert.Equal("d", lateOp.Alias);
    }

    // ─────────────── Implicit aliasing for unaliased JOINs ───────────────

    /// <summary>
    /// When tables in a JOIN have no explicit alias, the planner should implicitly
    /// wrap them with <see cref="AliasOperator"/> using the table name, so that
    /// column names are qualified and do not collide.
    /// </summary>
    [Fact]
    public void Plan_JoinWithoutAliases_ImplicitlyAliasesBothSides()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        catalog.Register(new TableDescriptor("csv", "left_table", "l.csv", new Dictionary<string, string>()));
        catalog.Register(new TableDescriptor("csv", "right_table", "r.csv", new Dictionary<string, string>()));

        QueryPlanner planner = new(catalog, DefaultFunctions);

        // SELECT * FROM left_table INNER JOIN right_table ON left_table.id = right_table.id
        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("left_table", Alias: null)),
            Joins: [new JoinClause(
                JoinType.Inner,
                new TableReference("right_table", Alias: null),
                new BinaryExpression(
                    new ColumnReference("left_table", "id"),
                    BinaryOperator.Equal,
                    new ColumnReference("right_table", "id")))]);

        IQueryOperator plan = planner.Plan(statement);

        // Unwrap the ProjectOperator added for SELECT * column ordering.
        if (plan is ProjectOperator projectWrapper)
            plan = projectWrapper.Source;

        // Plan should be: JoinOperator(AliasOperator(Scan), AliasOperator(Scan))
        Assert.IsType<JoinOperator>(plan);
        JoinOperator join = (JoinOperator)plan;

        AliasOperator leftAlias = Assert.IsType<AliasOperator>(join.Left);
        Assert.Equal("left_table", leftAlias.Alias);

        AliasOperator rightAlias = Assert.IsType<AliasOperator>(join.Right);
        Assert.Equal("right_table", rightAlias.Alias);
    }

    /// <summary>
    /// A single-table query without JOINs should NOT get an implicit alias, preserving
    /// unqualified column names in the output.
    /// </summary>
    [Fact]
    public void Plan_SingleTableNoAlias_NoImplicitAliasOperator()
    {
        TableCatalog catalog = CreateCatalogWithCsv("my_table", "data.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("my_table", Alias: null)));

        IQueryOperator plan = planner.Plan(statement);

        // No AliasOperator should be inserted for a single FROM table without JOINs.
        Assert.IsType<ScanOperator>(plan);
    }

    // ─────── GROUP BY sort injection tests ───────────────────────────────

    /// <summary>
    /// When GROUP BY keys and ORDER BY keys are identical the planner must
    /// place the Sort <em>above</em> GroupBy so that only the aggregated output
    /// rows are sorted — not all input rows.
    /// Expected plan: Sort → Project → Group By (hash) → Scan.
    /// No Sort node must appear below GroupBy in the tree.
    /// </summary>
    [Fact]
    public void Plan_GroupByMatchingOrderBy_SortIsAboveGroupBy()
    {
        TableCatalog catalog = CreateCatalogWithCsv("test", "dummy.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        // SELECT a, b, COUNT(*) FROM test GROUP BY a, b ORDER BY a, b
        SelectStatement statement = new(
            Columns:
            [
                new SelectColumn(new ColumnReference("a"), "a"),
                new SelectColumn(new ColumnReference("b"), "b"),
                new SelectColumn(
                    new FunctionCallExpression("count", [new LiteralExpression(1)]),
                    "cnt"),
            ],
            From: new FromClause(new TableReference("test")),
            GroupBy: new GroupByClause(
            [
                new ColumnReference("a"),
                new ColumnReference("b"),
            ]),
            OrderBy: new OrderByClause(
            [
                new OrderByItem(new ColumnReference("a"), SortDirection.Ascending),
                new OrderByItem(new ColumnReference("b"), SortDirection.Ascending),
            ]));

        IQueryOperator plan = planner.Plan(statement);

        // GroupBy must be the hash (non-streaming) variant — no sort injection.
        GroupByOperator groupBy = FindOperator<GroupByOperator>(plan)!;
        Assert.NotNull(groupBy);
        Assert.False(groupBy.StreamingSorted, "CSV source has no index ordering — must use hash aggregate");

        // The downstream OrderByOperator must exist (ORDER BY was not elided).
        OrderByOperator? sort = FindOperator<OrderByOperator>(plan);
        Assert.NotNull(sort);

        // Critical regression guard: Sort must NOT appear inside the GroupBy subtree.
        // Sorting N input rows before aggregating is always more expensive than sorting
        // G aggregated groups afterwards (G << N).
        Assert.Null(FindOperator<OrderByOperator>(groupBy.Source));
    }

    /// <summary>
    /// GROUP BY + ORDER BY + LIMIT: the Sort must appear above GroupBy (operating on
    /// aggregated output, not input rows) and must use the bounded top-N strategy so
    /// only N aggregated rows need to be materialised in the heap.
    /// Expected plan: Limit → Sort (top-N=10) → Project → Group By (hash) → Scan.
    /// </summary>
    [Fact]
    public void Plan_GroupByMatchingOrderByWithLimit_SortOnAggregatedOutputWithTopN()
    {
        TableCatalog catalog = CreateCatalogWithCsv("test", "dummy.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        // SELECT a, COUNT(*) FROM test GROUP BY a ORDER BY a LIMIT 10
        SelectStatement statement = new(
            Columns:
            [
                new SelectColumn(new ColumnReference("a"), "a"),
                new SelectColumn(
                    new FunctionCallExpression("count", [new LiteralExpression(1)]),
                    "cnt"),
            ],
            From: new FromClause(new TableReference("test")),
            GroupBy: new GroupByClause([new ColumnReference("a")]),
            OrderBy: new OrderByClause(
            [
                new OrderByItem(new ColumnReference("a"), SortDirection.Ascending),
            ]),
            Limit: 10);

        IQueryOperator plan = planner.Plan(statement);

        Assert.IsType<LimitOperator>(plan);
        GroupByOperator groupBy = FindOperator<GroupByOperator>(plan)!;
        Assert.NotNull(groupBy);
        Assert.False(groupBy.StreamingSorted, "CSV source has no index ordering — must use hash aggregate");

        // The downstream Sort must carry top-N so the heap is bounded.
        OrderByOperator? sort = FindOperator<OrderByOperator>(plan);
        Assert.NotNull(sort);
        Assert.Equal(10, sort!.TopNRows);

        // Critical regression guard: Sort must NOT appear inside the GroupBy subtree.
        Assert.Null(FindOperator<OrderByOperator>(groupBy.Source));
    }

    /// <summary>
    /// When ORDER BY keys do not match GROUP BY keys, no sort injection occurs and
    /// the normal blocking hash aggregate + downstream ORDER BY plan is produced.
    /// </summary>
    [Fact]
    public void Plan_GroupByNonMatchingOrderBy_NoSortInjection()
    {
        TableCatalog catalog = CreateCatalogWithCsv("test", "dummy.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        // SELECT a, b, COUNT(*) FROM test GROUP BY a, b ORDER BY b, a
        // ORDER BY order is different from GROUP BY order → no injection.
        SelectStatement statement = new(
            Columns:
            [
                new SelectColumn(new ColumnReference("a"), "a"),
                new SelectColumn(new ColumnReference("b"), "b"),
                new SelectColumn(
                    new FunctionCallExpression("count", [new LiteralExpression(1)]),
                    "cnt"),
            ],
            From: new FromClause(new TableReference("test")),
            GroupBy: new GroupByClause(
            [
                new ColumnReference("a"),
                new ColumnReference("b"),
            ]),
            OrderBy: new OrderByClause(
            [
                // Reversed from GROUP BY order.
                new OrderByItem(new ColumnReference("b"), SortDirection.Ascending),
                new OrderByItem(new ColumnReference("a"), SortDirection.Ascending),
            ]));

        IQueryOperator plan = planner.Plan(statement);

        GroupByOperator groupBy = FindOperator<GroupByOperator>(plan)!;
        Assert.NotNull(groupBy);
        Assert.False(groupBy.StreamingSorted, "No injection — ORDER BY order differs from GROUP BY");

        // Downstream OrderByOperator must still be present (not elided).
        Assert.NotNull(FindOperator<OrderByOperator>(plan));
    }

    /// <summary>
    /// When there is no ORDER BY clause, no sort injection occurs.
    /// </summary>
    [Fact]
    public void Plan_GroupByWithoutOrderBy_NoSortInjection()
    {
        TableCatalog catalog = CreateCatalogWithCsv("test", "dummy.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        // SELECT a, COUNT(*) FROM test GROUP BY a  (no ORDER BY)
        SelectStatement statement = new(
            Columns:
            [
                new SelectColumn(new ColumnReference("a"), "a"),
                new SelectColumn(
                    new FunctionCallExpression("count", [new LiteralExpression(1)]),
                    "cnt"),
            ],
            From: new FromClause(new TableReference("test")),
            GroupBy: new GroupByClause([new ColumnReference("a")]));

        IQueryOperator plan = planner.Plan(statement);

        GroupByOperator groupBy = FindOperator<GroupByOperator>(plan)!;
        Assert.NotNull(groupBy);
        Assert.False(groupBy.StreamingSorted, "No injection without ORDER BY");
        Assert.Null(FindOperator<OrderByOperator>(plan));
    }

    // ─────── GROUP BY without aggregates → DISTINCT rewrite tests ───────

    /// <summary>
    /// GROUP BY without aggregate functions should produce a streaming
    /// <see cref="DistinctOperator"/> instead of a blocking
    /// <see cref="GroupByOperator"/>, enabling LIMIT short-circuit.
    /// </summary>
    [Fact]
    public void Plan_GroupByWithoutAggregates_ProducesDistinctNotGroupBy()
    {
        TableCatalog catalog = CreateCatalogWithCsv("test", "dummy.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectColumn(new ColumnReference("a"), "a")],
            From: new FromClause(new TableReference("test")),
            GroupBy: new GroupByClause([new ColumnReference("a")]));

        IQueryOperator plan = planner.Plan(statement);

        // Plan should contain DistinctOperator (streaming) instead of GroupByOperator (blocking).
        Assert.IsType<ProjectOperator>(plan);
        ProjectOperator outerProject = (ProjectOperator)plan;
        Assert.IsType<DistinctOperator>(outerProject.Source);
        Assert.Null(FindOperator<GroupByOperator>(plan));
    }

    /// <summary>
    /// GROUP BY with an aggregate function must still produce a
    /// <see cref="GroupByOperator"/> (no rewrite to DISTINCT).
    /// </summary>
    [Fact]
    public void Plan_GroupByWithAggregate_ProducesGroupByOperator()
    {
        TableCatalog catalog = CreateCatalogWithCsv("test", "dummy.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns:
            [
                new SelectColumn(new ColumnReference("a"), "a"),
                new SelectColumn(
                    new FunctionCallExpression("count", [new LiteralExpression(1)]),
                    "cnt"),
            ],
            From: new FromClause(new TableReference("test")),
            GroupBy: new GroupByClause([new ColumnReference("a")]));

        IQueryOperator plan = planner.Plan(statement);

        Assert.NotNull(FindOperator<GroupByOperator>(plan));
        Assert.Null(FindOperator<DistinctOperator>(plan));
    }

    /// <summary>
    /// GROUP BY with HAVING (even without aggregates) must keep the blocking
    /// <see cref="GroupByOperator"/> because HAVING may reference aggregate
    /// results that require full materialisation.
    /// </summary>
    [Fact]
    public void Plan_GroupByWithHaving_KeepsGroupByOperator()
    {
        TableCatalog catalog = CreateCatalogWithCsv("test", "dummy.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectColumn(new ColumnReference("a"), "a")],
            From: new FromClause(new TableReference("test")),
            GroupBy: new GroupByClause([new ColumnReference("a")]),
            Having: new BinaryExpression(
                new ColumnReference("a"),
                BinaryOperator.GreaterThan,
                new LiteralExpression(0)));

        IQueryOperator plan = planner.Plan(statement);

        Assert.NotNull(FindOperator<GroupByOperator>(plan));
    }

    // ─────────────── Join elimination tests ───────────────

    /// <summary>
    /// A LEFT JOIN to a table whose columns are not referenced anywhere in the
    /// query output should be removed from the plan.
    /// </summary>
    [Fact]
    public void Plan_LeftJoinUnreferencedTable_EliminatesJoin()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        catalog.Register(new TableDescriptor("csv", "orders", "orders.csv", new Dictionary<string, string>()));
        catalog.Register(new TableDescriptor("csv", "products", "products.csv", new Dictionary<string, string>()));

        QueryPlanner planner = new(catalog, DefaultFunctions);

        // SELECT orders.id FROM orders LEFT JOIN products ON orders.pid = products.pid
        // products is unreferenced in SELECT → should be eliminated.
        SelectStatement statement = new(
            Columns: [new SelectColumn(new ColumnReference("orders", "id"), "id")],
            From: new FromClause(new TableReference("orders")),
            Joins:
            [
                new JoinClause(
                    JoinType.Left,
                    new TableReference("products"),
                    new BinaryExpression(
                        new ColumnReference("orders", "pid"),
                        BinaryOperator.Equal,
                        new ColumnReference("products", "pid"))),
            ]);

        IQueryOperator plan = planner.Plan(statement);

        // No JoinOperator should be present — the LEFT JOIN was eliminated.
        Assert.Null(FindOperator<JoinOperator>(plan));
    }

    /// <summary>
    /// An INNER JOIN to an unreferenced table must NOT be eliminated because
    /// INNER JOINs can filter rows (non-matching left rows are dropped).
    /// </summary>
    [Fact]
    public void Plan_InnerJoinUnreferencedTable_KeepsJoin()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        catalog.Register(new TableDescriptor("csv", "orders", "orders.csv", new Dictionary<string, string>()));
        catalog.Register(new TableDescriptor("csv", "products", "products.csv", new Dictionary<string, string>()));

        QueryPlanner planner = new(catalog, DefaultFunctions);

        // SELECT orders.id FROM orders INNER JOIN products ON orders.pid = products.pid
        // Even though products is unreferenced, INNER JOIN filters rows.
        SelectStatement statement = new(
            Columns: [new SelectColumn(new ColumnReference("orders", "id"), "id")],
            From: new FromClause(new TableReference("orders")),
            Joins:
            [
                new JoinClause(
                    JoinType.Inner,
                    new TableReference("products"),
                    new BinaryExpression(
                        new ColumnReference("orders", "pid"),
                        BinaryOperator.Equal,
                        new ColumnReference("products", "pid"))),
            ]);

        IQueryOperator plan = planner.Plan(statement);

        // INNER JOIN must be preserved.
        Assert.NotNull(FindOperator<JoinOperator>(plan));
    }

    /// <summary>
    /// Cascading elimination: when an unreferenced LEFT JOIN is the only
    /// reason another LEFT JOIN's table is referenced (via ON condition),
    /// removing the first should allow removing the second.
    /// </summary>
    [Fact]
    public void Plan_CascadingLeftJoinElimination_RemovesBoth()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        catalog.Register(new TableDescriptor("csv", "orders", "orders.csv", new Dictionary<string, string>()));
        catalog.Register(new TableDescriptor("csv", "products", "products.csv", new Dictionary<string, string>()));
        catalog.Register(new TableDescriptor("csv", "aisles", "aisles.csv", new Dictionary<string, string>()));

        QueryPlanner planner = new(catalog, DefaultFunctions);

        // SELECT orders.id FROM orders
        //   LEFT JOIN products ON orders.pid = products.pid
        //   LEFT JOIN aisles ON products.aid = aisles.aid
        // Neither products nor aisles contribute to SELECT.
        // aisles references products.aid, so products can only be eliminated
        // after aisles is eliminated first (cascading).
        SelectStatement statement = new(
            Columns: [new SelectColumn(new ColumnReference("orders", "id"), "id")],
            From: new FromClause(new TableReference("orders")),
            Joins:
            [
                new JoinClause(
                    JoinType.Left,
                    new TableReference("products"),
                    new BinaryExpression(
                        new ColumnReference("orders", "pid"),
                        BinaryOperator.Equal,
                        new ColumnReference("products", "pid"))),
                new JoinClause(
                    JoinType.Left,
                    new TableReference("aisles"),
                    new BinaryExpression(
                        new ColumnReference("products", "aid"),
                        BinaryOperator.Equal,
                        new ColumnReference("aisles", "aid"))),
            ]);

        IQueryOperator plan = planner.Plan(statement);

        // Both LEFT JOINs should be eliminated.
        Assert.Null(FindOperator<JoinOperator>(plan));
    }

    /// <summary>
    /// A LEFT JOIN whose columns are referenced in WHERE must be preserved.
    /// </summary>
    [Fact]
    public void Plan_LeftJoinReferencedInWhere_KeepsJoin()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        catalog.Register(new TableDescriptor("csv", "orders", "orders.csv", new Dictionary<string, string>()));
        catalog.Register(new TableDescriptor("csv", "products", "products.csv", new Dictionary<string, string>()));

        QueryPlanner planner = new(catalog, DefaultFunctions);

        // SELECT orders.id FROM orders LEFT JOIN products ON orders.pid = products.pid
        // WHERE products.name IS NOT NULL
        // products is referenced in WHERE → must keep the join.
        SelectStatement statement = new(
            Columns: [new SelectColumn(new ColumnReference("orders", "id"), "id")],
            From: new FromClause(new TableReference("orders")),
            Joins:
            [
                new JoinClause(
                    JoinType.Left,
                    new TableReference("products"),
                    new BinaryExpression(
                        new ColumnReference("orders", "pid"),
                        BinaryOperator.Equal,
                        new ColumnReference("products", "pid"))),
            ],
            Where: new IsNullExpression(
                new ColumnReference("products", "name"), Negated: true));

        IQueryOperator plan = planner.Plan(statement);

        // LEFT JOIN must be preserved because products is referenced in WHERE.
        Assert.NotNull(FindOperator<JoinOperator>(plan));
    }

    /// <summary>
    /// When the right side of a LEFT JOIN has 2x+ more estimated rows than the left,
    /// the planner sets the flipped flag so the smaller side is materialized.
    /// </summary>
    [Fact]
    public void Plan_LeftJoinBuildSideFlip_FlipsWhenRightIsLarger()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("stub_small", () => new StubRowCountProvider(estimatedRowCount: 100));
        catalog.RegisterProvider("stub_large", () => new StubRowCountProvider(estimatedRowCount: 10_000));
        catalog.Register(new TableDescriptor("stub_small", "small_table", "s.csv", new Dictionary<string, string>()));
        catalog.Register(new TableDescriptor("stub_large", "large_table", "l.csv", new Dictionary<string, string>()));

        QueryPlanner planner = new(catalog, DefaultFunctions);

        // SELECT * FROM small_table LEFT JOIN large_table ON ...
        // Right (build) has 10_000 rows, left (probe) has 100 → ratio 100x → flip.
        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("small_table")),
            Joins: [new JoinClause(
                JoinType.Left,
                new TableReference("large_table"),
                new BinaryExpression(
                    new ColumnReference("small_table", "id"),
                    BinaryOperator.Equal,
                    new ColumnReference("large_table", "id")))]);

        IQueryOperator plan = planner.Plan(statement);

        JoinOperator join = FindOperator<JoinOperator>(plan)!;
        Assert.NotNull(join);
        Assert.True(join.Flipped, "Expected flipped=true when right side is 100x larger");
    }

    /// <summary>
    /// When the right side of a LEFT JOIN is smaller than the left, no flip occurs.
    /// </summary>
    [Fact]
    public void Plan_LeftJoinBuildSideFlip_DoesNotFlipWhenRightIsSmaller()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("stub_small", () => new StubRowCountProvider(estimatedRowCount: 100));
        catalog.RegisterProvider("stub_large", () => new StubRowCountProvider(estimatedRowCount: 10_000));
        catalog.Register(new TableDescriptor("stub_small", "small_table", "s.csv", new Dictionary<string, string>()));
        catalog.Register(new TableDescriptor("stub_large", "large_table", "l.csv", new Dictionary<string, string>()));

        QueryPlanner planner = new(catalog, DefaultFunctions);

        // SELECT * FROM large_table LEFT JOIN small_table ON ...
        // Right (build) has 100 rows, left (probe) has 10_000 → normal, no flip.
        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("large_table")),
            Joins: [new JoinClause(
                JoinType.Left,
                new TableReference("small_table"),
                new BinaryExpression(
                    new ColumnReference("large_table", "id"),
                    BinaryOperator.Equal,
                    new ColumnReference("small_table", "id")))]);

        IQueryOperator plan = planner.Plan(statement);

        JoinOperator join = FindOperator<JoinOperator>(plan)!;
        Assert.NotNull(join);
        Assert.False(join.Flipped, "Expected flipped=false when right side is smaller");
    }

    /// <summary>
    /// When the ratio is below the 2x threshold, no flip occurs even though the
    /// right side is larger.
    /// </summary>
    [Fact]
    public void Plan_LeftJoinBuildSideFlip_DoesNotFlipBelowThreshold()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("stub_a", () => new StubRowCountProvider(estimatedRowCount: 1000));
        catalog.RegisterProvider("stub_b", () => new StubRowCountProvider(estimatedRowCount: 1500));
        catalog.Register(new TableDescriptor("stub_a", "table_a", "a.csv", new Dictionary<string, string>()));
        catalog.Register(new TableDescriptor("stub_b", "table_b", "b.csv", new Dictionary<string, string>()));

        QueryPlanner planner = new(catalog, DefaultFunctions);

        // Right=1500, left=1000 → ratio 1.5x < 2x → no flip.
        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("table_a")),
            Joins: [new JoinClause(
                JoinType.Left,
                new TableReference("table_b"),
                new BinaryExpression(
                    new ColumnReference("table_a", "id"),
                    BinaryOperator.Equal,
                    new ColumnReference("table_b", "id")))]);

        IQueryOperator plan = planner.Plan(statement);

        JoinOperator join = FindOperator<JoinOperator>(plan)!;
        Assert.NotNull(join);
        Assert.False(join.Flipped, "Expected flipped=false when ratio is below 2x");
    }

    /// <summary>
    /// Verifies that the planner sets <see cref="JoinOperator.PreferIndexNestedLoop"/> to
    /// <see langword="true"/> when a LIMIT clause is present and the build-side table has a
    /// sorted value index on the equi-join key column. This is the plan-time signal that
    /// unlocks index nested-loop join without waiting for the runtime
    /// <see cref="ExecutionContext.RowLimit"/> hint.
    /// </summary>
    [Fact]
    public void Plan_InnerJoinWithLimitAndIndexedBuildSide_SetsPreferIndexNestedLoop()
    {
        // Register a seekable provider so the catalog resolves it successfully.
        TableCatalog catalog = new();
        catalog.RegisterProvider("seekable", () => new SeekableStubProvider());

        TableDescriptor probeDescriptor = new("seekable", "probe_table", "probe.datum", new Dictionary<string, string>());
        TableDescriptor buildDescriptor = new("seekable", "build_table", "build.datum", new Dictionary<string, string>());
        catalog.Register(probeDescriptor);
        catalog.Register(buildDescriptor);

        // Register a sorted index on the build side's join key column.
        SortedValueIndex sortedIndex = new([new ValueIndexEntry(DataValue.FromFloat32(1f), 0, 0)]);
        SortedValueIndexSet sortedIndexSet = new(new Dictionary<string, SortedValueIndex>
        {
            ["build_id"] = sortedIndex,
        });

        SourceIndex sourceIndex = new(
            new SourceFingerprint(10, new byte[32]),
            new IndexSchema(
                new Schema([new ColumnInfo("build_id", DataKind.Float32, false)]), 10),
            [],
            sortedIndexes: sortedIndexSet);

        catalog.RegisterIndex("build_table", sourceIndex);

        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("probe_table")),
            Joins:
            [
                new JoinClause(
                    JoinType.Inner,
                    new TableReference("build_table"),
                    new BinaryExpression(
                        new ColumnReference("probe_table", "probe_id"),
                        BinaryOperator.Equal,
                        new ColumnReference("build_table", "build_id")))
            ],
            Limit: 100);

        IQueryOperator plan = planner.Plan(statement);

        JoinOperator? join = FindOperator<JoinOperator>(plan);
        Assert.NotNull(join);
        Assert.True(join.PreferIndexNestedLoop,
            "Planner should set PreferIndexNestedLoop when LIMIT is present and build side has a sorted index on the join key.");
    }

    /// <summary>
    /// Verifies that the planner does NOT set <see cref="JoinOperator.PreferIndexNestedLoop"/>
    /// when there is no LIMIT clause, even if the build side has an index.
    /// Without LIMIT the full result set is needed, so hash join is preferred.
    /// </summary>
    [Fact]
    public void Plan_InnerJoinWithoutLimit_DoesNotSetPreferIndexNestedLoop()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("seekable", () => new SeekableStubProvider());

        TableDescriptor probeDescriptor = new("seekable", "probe_table", "probe.datum", new Dictionary<string, string>());
        TableDescriptor buildDescriptor = new("seekable", "build_table", "build.datum", new Dictionary<string, string>());
        catalog.Register(probeDescriptor);
        catalog.Register(buildDescriptor);

        SortedValueIndex sortedIndex = new([new ValueIndexEntry(DataValue.FromFloat32(1f), 0, 0)]);
        SortedValueIndexSet sortedIndexSet = new(new Dictionary<string, SortedValueIndex>
        {
            ["build_id"] = sortedIndex,
        });

        SourceIndex sourceIndex = new(
            new SourceFingerprint(10, new byte[32]),
            new IndexSchema(
                new Schema([new ColumnInfo("build_id", DataKind.Float32, false)]), 10),
            [],
            sortedIndexes: sortedIndexSet);

        catalog.RegisterIndex("build_table", sourceIndex);

        QueryPlanner planner = new(catalog, DefaultFunctions);

        // No Limit clause.
        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("probe_table")),
            Joins:
            [
                new JoinClause(
                    JoinType.Inner,
                    new TableReference("build_table"),
                    new BinaryExpression(
                        new ColumnReference("probe_table", "probe_id"),
                        BinaryOperator.Equal,
                        new ColumnReference("build_table", "build_id")))
            ]);

        IQueryOperator plan = planner.Plan(statement);

        JoinOperator? join = FindOperator<JoinOperator>(plan);
        Assert.NotNull(join);
        Assert.False(join.PreferIndexNestedLoop,
            "Planner must not set PreferIndexNestedLoop without a LIMIT clause.");
    }

    /// <summary>
    /// Verifies that the planner chooses a <see cref="MergeJoinOperator"/> over a hash join
    /// when the build side exceeds the B+Tree auto-threshold row count, even when the leading
    /// ORDER BY / GROUP BY column does not match the join key.  At that scale the cost of
    /// building and probing a hash table exceeds the random-access penalty of merge join.
    /// </summary>
    [Fact]
    public void Plan_LargeJoinBothSidesIndexed_UsesMergeJoinWithoutOrderByAlignment()
    {
        TableCatalog catalog = new();
        // Both providers report 10 M rows — above the 5 M BPlusTreeAutoThreshold.
        catalog.RegisterProvider("seekable", () => new SeekableStubProvider(estimatedRowCount: 10_000_000));

        TableDescriptor leftDescriptor = new("seekable", "left_table", "left.datum", new Dictionary<string, string>());
        TableDescriptor rightDescriptor = new("seekable", "right_table", "right.datum", new Dictionary<string, string>());
        catalog.Register(leftDescriptor);
        catalog.Register(rightDescriptor);

        // Register sorted indexes on the join key column for both sides.
        SortedValueIndex sortedIndex = new([new ValueIndexEntry(DataValue.FromInt64(1L), 0, 0)]);
        SortedValueIndexSet sortedIndexSet = new(new Dictionary<string, SortedValueIndex>
        {
            ["join_id"] = sortedIndex,
        });

        SourceIndex sourceIndex = new(
            new SourceFingerprint(10_000_000, new byte[32]),
            new IndexSchema(
                new Schema([new ColumnInfo("join_id", DataKind.Int64, false)]), 10_000_000),
            [],
            sortedIndexes: sortedIndexSet);

        catalog.RegisterIndex("left_table", sourceIndex);
        catalog.RegisterIndex("right_table", sourceIndex);

        QueryPlanner planner = new(catalog, DefaultFunctions);

        // ORDER BY is on a column that does not match the join key — would fail the old
        // SortedJoinBenefitsDownstream check, but the large-build override must fire.
        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("left_table")),
            Joins:
            [
                new JoinClause(
                    JoinType.Inner,
                    new TableReference("right_table"),
                    new BinaryExpression(
                        new ColumnReference("left_table", "join_id"),
                        BinaryOperator.Equal,
                        new ColumnReference("right_table", "join_id")))
            ],
            OrderBy: new OrderByClause(
            [
                new OrderByItem(new ColumnReference("left_table", "other_col"), SortDirection.Ascending)
            ]));

        IQueryOperator plan = planner.Plan(statement);

        Assert.IsType<MergeJoinOperator>(FindOperator<MergeJoinOperator>(plan));
    }

    /// <summary>
    /// Verifies that the planner keeps a hash join when both sides are small (below the
    /// B+Tree auto-threshold) and the ORDER BY leading column does not match the join key.
    /// For small tables, hash join's sequential-scan pattern is cheaper than the
    /// random-access reads used by merge join.
    /// </summary>
    [Fact]
    public void Plan_SmallJoinNoOrderByAlignment_UsesHashJoinNotMergeJoin()
    {
        TableCatalog catalog = new();
        // Both providers report 100 rows — well below the 5 M BPlusTreeAutoThreshold.
        catalog.RegisterProvider("seekable", () => new SeekableStubProvider(estimatedRowCount: 100));

        TableDescriptor leftDescriptor = new("seekable", "small_left", "small_left.datum", new Dictionary<string, string>());
        TableDescriptor rightDescriptor = new("seekable", "small_right", "small_right.datum", new Dictionary<string, string>());
        catalog.Register(leftDescriptor);
        catalog.Register(rightDescriptor);

        SortedValueIndex sortedIndex = new([new ValueIndexEntry(DataValue.FromInt64(1L), 0, 0)]);
        SortedValueIndexSet sortedIndexSet = new(new Dictionary<string, SortedValueIndex>
        {
            ["join_id"] = sortedIndex,
        });

        SourceIndex sourceIndex = new(
            new SourceFingerprint(100, new byte[32]),
            new IndexSchema(
                new Schema([new ColumnInfo("join_id", DataKind.Int64, false)]), 100),
            [],
            sortedIndexes: sortedIndexSet);

        catalog.RegisterIndex("small_left", sourceIndex);
        catalog.RegisterIndex("small_right", sourceIndex);

        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("small_left")),
            Joins:
            [
                new JoinClause(
                    JoinType.Inner,
                    new TableReference("small_right"),
                    new BinaryExpression(
                        new ColumnReference("small_left", "join_id"),
                        BinaryOperator.Equal,
                        new ColumnReference("small_right", "join_id")))
            ],
            OrderBy: new OrderByClause(
            [
                new OrderByItem(new ColumnReference("small_left", "other_col"), SortDirection.Ascending)
            ]));

        IQueryOperator plan = planner.Plan(statement);

        // Merge join must NOT be chosen — hash join is cheaper for small tables.
        Assert.Null(FindOperator<MergeJoinOperator>(plan));
        Assert.NotNull(FindOperator<JoinOperator>(plan));
    }

    /// <summary>
    /// INNER JOINs should never be flipped (handled by TryReorderJoins instead).
    /// </summary>
    [Fact]
    public void Plan_InnerJoinBuildSideFlip_NeverFlips()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("stub_small", () => new StubRowCountProvider(estimatedRowCount: 100));
        catalog.RegisterProvider("stub_large", () => new StubRowCountProvider(estimatedRowCount: 10_000));
        catalog.Register(new TableDescriptor("stub_small", "small_table", "s.csv", new Dictionary<string, string>()));
        catalog.Register(new TableDescriptor("stub_large", "large_table", "l.csv", new Dictionary<string, string>()));

        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("small_table")),
            Joins: [new JoinClause(
                JoinType.Inner,
                new TableReference("large_table"),
                new BinaryExpression(
                    new ColumnReference("small_table", "id"),
                    BinaryOperator.Equal,
                    new ColumnReference("large_table", "id")))]);

        IQueryOperator plan = planner.Plan(statement);

        JoinOperator join = FindOperator<JoinOperator>(plan)!;
        Assert.NotNull(join);
        Assert.False(join.Flipped, "INNER JOINs should not be flipped");
    }
}

/// <summary>
/// A stub <see cref="IKeyedTableProvider"/> for planner tests that declares
/// expensive columns and a key column without needing real data.
/// </summary>
internal sealed class StubKeyedProvider : IKeyedTableProvider
{
    private readonly string _keyColumn;
    private readonly string[] _expensiveColumns;

    public StubKeyedProvider(string keyColumn, string[] expensiveColumns)
    {
        _keyColumn = keyColumn;
        _expensiveColumns = expensiveColumns;
    }

    public Task<Schema> GetSchemaAsync(TableDescriptor descriptor, CancellationToken cancellationToken)
    {
        List<ColumnInfo> columns = new()
        {
            new ColumnInfo(_keyColumn, DataKind.String, nullable: false),
        };

        foreach (string expensive in _expensiveColumns)
        {
            columns.Add(new ColumnInfo(expensive, DataKind.UInt8Array, nullable: true));
        }

        return Task.FromResult(new Schema(columns));
    }

    public IAsyncEnumerable<Row> OpenAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        CancellationToken cancellationToken)
    {
        return AsyncEnumerable.Empty<Row>();
    }

    public Task<ProviderCapabilities> GetCapabilitiesAsync(
        TableDescriptor descriptor, CancellationToken cancellationToken)
    {
        Dictionary<string, ColumnCost> costs = new(StringComparer.OrdinalIgnoreCase);
        foreach (string expensive in _expensiveColumns)
        {
            costs[expensive] = ColumnCost.Expensive;
        }

        return Task.FromResult(new ProviderCapabilities(
            EstimatedRowCount: null,
            EstimatedRowSizeBytes: null,
            SupportsSeek: false,
            ColumnCosts: costs,
            KeyColumn: _keyColumn));
    }

    public async IAsyncEnumerable<Row> FetchByKeysAsync(
        TableDescriptor descriptor,
        string keyColumn,
        IReadOnlySet<DataValue> keyValues,
        IReadOnlySet<string>? requiredColumns,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }
}

/// <summary>
/// A minimal <see cref="ITableProvider"/> that returns a configurable
/// <see cref="ProviderCapabilities.EstimatedRowCount"/> for greedy join
/// reordering tests. Produces no rows.
/// </summary>
internal sealed class StubRowCountProvider : ITableProvider
{
    private readonly long _estimatedRowCount;

    public StubRowCountProvider(long estimatedRowCount)
    {
        _estimatedRowCount = estimatedRowCount;
    }

    public Task<Schema> GetSchemaAsync(TableDescriptor descriptor, CancellationToken cancellationToken)
    {
        List<ColumnInfo> columns =
        [
            new ColumnInfo("id", DataKind.String, nullable: false),
        ];

        return Task.FromResult(new Schema(columns));
    }

    public IAsyncEnumerable<Row> OpenAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        CancellationToken cancellationToken)
    {
        return AsyncEnumerable.Empty<Row>();
    }

    public Task<ProviderCapabilities> GetCapabilitiesAsync(
        TableDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new ProviderCapabilities(
            EstimatedRowCount: _estimatedRowCount,
            EstimatedRowSizeBytes: null,
            SupportsSeek: false,
            ColumnCosts: new Dictionary<string, ColumnCost>()));
    }
}

/// <summary>
/// A minimal seekable <see cref="ISeekableTableProvider"/> for planner tests
/// that need a provider capable of index-based row reads. Produces no rows.
/// </summary>
internal sealed class SeekableStubProvider : ISeekableTableProvider
{
    private readonly long _estimatedRowCount;

    internal SeekableStubProvider(long estimatedRowCount = 10)
    {
        _estimatedRowCount = estimatedRowCount;
    }

    public Task<Schema> GetSchemaAsync(
        TableDescriptor descriptor, CancellationToken cancellationToken)
    {
        return Task.FromResult(new Schema([]));
    }

    public IAsyncEnumerable<Row> OpenAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        CancellationToken cancellationToken)
    {
        return AsyncEnumerable.Empty<Row>();
    }

    public Task<ProviderCapabilities> GetCapabilitiesAsync(
        TableDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new ProviderCapabilities(
            EstimatedRowCount: _estimatedRowCount,
            EstimatedRowSizeBytes: null,
            SupportsSeek: true,
            ColumnCosts: new Dictionary<string, ColumnCost>()));
    }

    public IAsyncEnumerable<Row> ReadRowRangeAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        long startRow,
        int count,
        CancellationToken cancellationToken)
    {
        return AsyncEnumerable.Empty<Row>();
    }
}
