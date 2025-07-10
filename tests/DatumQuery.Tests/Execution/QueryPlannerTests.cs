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

        // Predicate should stay above the join.
        Assert.IsType<FilterOperator>(plan);
        FilterOperator filter = (FilterOperator)plan;
        Assert.IsType<JoinOperator>(filter.Source);
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
            _ => null,
        };
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
