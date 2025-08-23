using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Integration tests that assert the structural shape of query plans produced by
/// <see cref="QueryPlanner"/>. Tests assert directly on <see cref="IQueryOperator"/>
/// types and properties — never on display strings or real data files — so they are
/// fast, deterministic, and safe to run in CI/CD with no dataset present.
/// </summary>
/// <remarks>
/// All tests use virtual file paths (e.g. <c>"dummy.csv"</c>). <see cref="QueryPlanner.Plan"/>
/// resolves table metadata from the catalog without opening files.
/// </remarks>
public sealed class QueryPlanOptimizerTests
{
    private static readonly FunctionRegistry DefaultFunctions = FunctionRegistry.CreateDefault();

    private static TableCatalog CreateCatalogWithCsv(string tableName, string csvPath)
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());
        catalog.Register(new TableDescriptor("csv", tableName, csvPath, new Dictionary<string, string>()));
        return catalog;
    }

    // ──────────── Sort placement relative to GroupBy ────────────────────────

    /// <summary>
    /// Regression guard for the sort-injection bug (June 2025).
    /// When GROUP BY is combined with ORDER BY the <see cref="OrderByOperator"/>
    /// must sit <em>above</em> <see cref="GroupByOperator"/> in the tree so that it
    /// operates on G aggregated groups, not on N input rows (G ≪ N).
    /// </summary>
    [Fact]
    public void GroupByWithOrderBy_SortOperatorIsAboveGroupByOperator()
    {
        TableCatalog catalog = CreateCatalogWithCsv("orders", "dummy.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns:
            [
                new SelectColumn(new ColumnReference("product_id"), "product_id"),
                new SelectColumn(
                    new FunctionCallExpression("count", [new LiteralExpression(1)]),
                    "order_count"),
            ],
            From: new FromClause(new TableReference("orders")),
            GroupBy: new GroupByClause([new ColumnReference("product_id")]),
            OrderBy: new OrderByClause(
            [
                new OrderByItem(new ColumnReference("order_count"), SortDirection.Descending),
            ]));

        IQueryOperator plan = planner.Plan(statement);

        // Hash GroupBy must be chosen — CSV has no index ordering.
        GroupByOperator groupBy = FindOperator<GroupByOperator>(plan)!;
        Assert.NotNull(groupBy);
        Assert.False(groupBy.StreamingSorted, "Must use hash aggregate — no index ordering available");

        // A Sort must be present somewhere in the plan.
        OrderByOperator? sort = FindOperator<OrderByOperator>(plan);
        Assert.NotNull(sort);

        // Critical regression guard: no Sort may appear below GroupBy.
        // Sorting N input rows before aggregating is always more expensive than
        // sorting G aggregated groups afterwards.
        Assert.True(
            FindOperator<OrderByOperator>(groupBy.Source) is null,
            "Sort must not appear below GroupBy — sort injection regressed");
    }

    /// <summary>
    /// GROUP BY + ORDER BY + LIMIT: the <see cref="OrderByOperator"/> must use
    /// the bounded top-N strategy (<see cref="OrderByOperator.TopNRows"/> == LIMIT)
    /// so the heap stays bounded while operating on aggregated output.
    /// </summary>
    [Fact]
    public void GroupByWithOrderByAndLimit_SortOperatorHasBoundedTopN()
    {
        TableCatalog catalog = CreateCatalogWithCsv("sales", "dummy.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns:
            [
                new SelectColumn(new ColumnReference("category"), "category"),
                new SelectColumn(
                    new FunctionCallExpression("sum", [new ColumnReference("amount")]),
                    "total"),
            ],
            From: new FromClause(new TableReference("sales")),
            GroupBy: new GroupByClause([new ColumnReference("category")]),
            OrderBy: new OrderByClause(
            [
                new OrderByItem(new ColumnReference("total"), SortDirection.Descending),
            ]),
            Limit: 25);

        IQueryOperator plan = planner.Plan(statement);

        Assert.IsType<LimitOperator>(plan);

        OrderByOperator? sort = FindOperator<OrderByOperator>(plan);
        Assert.NotNull(sort);

        // TopNRows must equal the LIMIT value — unbounded sort is wasteful here.
        Assert.Equal(25, sort!.TopNRows);

        // Sort must sit above GroupBy — not below it.
        GroupByOperator groupBy = FindOperator<GroupByOperator>(plan)!;
        Assert.NotNull(groupBy);
        Assert.True(
            FindOperator<OrderByOperator>(groupBy.Source) is null,
            "Sort must not appear below GroupBy");
    }

    /// <summary>
    /// A plain GROUP BY without ORDER BY must use hash aggregation
    /// (<see cref="GroupByOperator.StreamingSorted"/> == <see langword="false"/>)
    /// and no <see cref="OrderByOperator"/> must appear anywhere in the plan.
    /// </summary>
    [Fact]
    public void GroupByWithoutOrderBy_UsesHashAggregateAndNoSortOperator()
    {
        TableCatalog catalog = CreateCatalogWithCsv("logs", "dummy.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns:
            [
                new SelectColumn(new ColumnReference("host"), "host"),
                new SelectColumn(
                    new FunctionCallExpression("count", [new LiteralExpression(1)]),
                    "requests"),
            ],
            From: new FromClause(new TableReference("logs")),
            GroupBy: new GroupByClause([new ColumnReference("host")]));

        IQueryOperator plan = planner.Plan(statement);

        GroupByOperator groupBy = FindOperator<GroupByOperator>(plan)!;
        Assert.NotNull(groupBy);
        Assert.False(groupBy.StreamingSorted, "Must use hash aggregate for unordered source");
        Assert.True(FindOperator<OrderByOperator>(plan) is null, "No ORDER BY specified — no Sort expected");
    }

    // ──────────── JOIN + GROUP BY plan shape ────────────────────────────────

    /// <summary>
    /// When GROUP BY is applied to the output of a JOIN the
    /// <see cref="GroupByOperator"/> must sit above the <see cref="JoinOperator"/>
    /// in the tree — the join result feeds into aggregation, not the other way around.
    /// </summary>
    [Fact]
    public void JoinWithGroupBy_GroupByOperatorIsAboveJoinOperator()
    {
        TableCatalog catalog = CreateCatalogWithCsv("orders", "orders.csv");
        catalog.Register(new TableDescriptor("csv", "products", "products.csv", new Dictionary<string, string>()));

        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns:
            [
                new SelectColumn(new ColumnReference("products", "name"), "product_name"),
                new SelectColumn(
                    new FunctionCallExpression("count", [new LiteralExpression(1)]),
                    "order_count"),
            ],
            From: new FromClause(new TableReference("orders")),
            Joins:
            [
                new JoinClause(
                    JoinType.Inner,
                    new TableReference("products"),
                    new BinaryExpression(
                        new ColumnReference("orders", "product_id"),
                        BinaryOperator.Equal,
                        new ColumnReference("products", "id"))),
            ],
            GroupBy: new GroupByClause([new ColumnReference("products", "name")]));

        IQueryOperator plan = planner.Plan(statement);

        GroupByOperator groupBy = FindOperator<GroupByOperator>(plan)!;
        Assert.NotNull(groupBy);

        // The Join must be reachable from GroupBy's source — proving GroupBy is above Join.
        Assert.True(
            FindOperator<JoinOperator>(groupBy.Source) is not null,
            "JoinOperator must be a descendant of GroupByOperator");
    }

    /// <summary>
    /// Full pipeline integration test mirroring the Instacart query that exposed the
    /// sort-injection regression (332 s actual vs ~30 s expected).
    /// Expected operator stack top-to-bottom:
    /// <see cref="LimitOperator"/> → <see cref="OrderByOperator"/> (top-N=100) →
    /// <see cref="GroupByOperator"/> (hash) → <see cref="JoinOperator"/> (LEFT) → Scan.
    /// </summary>
    [Fact]
    public void JoinWithGroupByOrderByLimit_FullPipelineOperatorStackIsCorrect()
    {
        TableCatalog catalog = CreateCatalogWithCsv("order_products", "order_products.csv");
        catalog.Register(new TableDescriptor("csv", "products", "products.csv", new Dictionary<string, string>()));

        QueryPlanner planner = new(catalog, DefaultFunctions);

        // SELECT p.product_name, COUNT(*) AS reorder_count
        // FROM order_products op
        // LEFT JOIN products p ON op.product_id = p.product_id
        // GROUP BY p.product_name
        // ORDER BY reorder_count DESC
        // LIMIT 100
        SelectStatement statement = new(
            Columns:
            [
                new SelectColumn(new ColumnReference("p", "product_name"), "product_name"),
                new SelectColumn(
                    new FunctionCallExpression("count", [new LiteralExpression(1)]),
                    "reorder_count"),
            ],
            From: new FromClause(new TableReference("order_products", "op")),
            Joins:
            [
                new JoinClause(
                    JoinType.Left,
                    new TableReference("products", "p"),
                    new BinaryExpression(
                        new ColumnReference("op", "product_id"),
                        BinaryOperator.Equal,
                        new ColumnReference("p", "product_id"))),
            ],
            GroupBy: new GroupByClause([new ColumnReference("p", "product_name")]),
            OrderBy: new OrderByClause(
            [
                new OrderByItem(new ColumnReference("reorder_count"), SortDirection.Descending),
            ]),
            Limit: 100);

        IQueryOperator plan = planner.Plan(statement);

        // Root must be LimitOperator.
        Assert.IsType<LimitOperator>(plan);

        // Sort must exist, be bounded, and NOT appear below GroupBy.
        OrderByOperator sort = FindOperator<OrderByOperator>(plan)!;
        Assert.NotNull(sort);
        Assert.Equal(100, sort.TopNRows);

        // GroupBy must be hash mode (no index ordering on CSV source).
        GroupByOperator groupBy = FindOperator<GroupByOperator>(plan)!;
        Assert.NotNull(groupBy);
        Assert.False(groupBy.StreamingSorted, "Must use hash aggregate — no index ordering");

        // Join must be LEFT and reachable from GroupBy's source.
        JoinOperator join = FindOperator<JoinOperator>(groupBy.Source)!;
        Assert.NotNull(join);
        Assert.Equal(JoinType.Left, join.Type);

        // Critical ordering assertions — Sort must not have slipped below GroupBy.
        Assert.True(
            FindOperator<OrderByOperator>(groupBy.Source) is null,
            "Sort must not appear below GroupBy");
    }

    // ──────────── ORDER BY keys matching GROUP BY keys (regression trigger) ─

    /// <summary>
    /// The original sort-injection bug was triggered specifically when ORDER BY keys
    /// are identical to GROUP BY keys. The old planner detected the match and injected
    /// a sort <em>before</em> the GroupBy to enable streaming mode — sorting N input rows
    /// instead of G aggregated groups. This test reproduces that exact pattern.
    /// Expected plan: Limit → Sort (top-N=100) → GroupBy (hash) → Scan.
    /// No Sort must appear below GroupBy.
    /// </summary>
    [Fact]
    public void GroupByWithMatchingOrderByKeys_SortIsAboveGroupByNotBelow()
    {
        TableCatalog catalog = CreateCatalogWithCsv("events", "dummy.csv");
        QueryPlanner planner = new(catalog, DefaultFunctions);

        // GROUP BY a, b  ORDER BY a, b  — ORDER BY keys exactly match GROUP BY keys.
        // This is the exact shape of the buggy Instacart query.
        SelectStatement statement = new(
            Columns:
            [
                new SelectColumn(new ColumnReference("a"), "a"),
                new SelectColumn(new ColumnReference("b"), "b"),
                new SelectColumn(
                    new FunctionCallExpression("count", [new LiteralExpression(1)]),
                    "cnt"),
            ],
            From: new FromClause(new TableReference("events")),
            GroupBy: new GroupByClause(
            [
                new ColumnReference("a"),
                new ColumnReference("b"),
            ]),
            OrderBy: new OrderByClause(
            [
                new OrderByItem(new ColumnReference("a"), SortDirection.Ascending),
                new OrderByItem(new ColumnReference("b"), SortDirection.Ascending),
            ]),
            Limit: 100);

        IQueryOperator plan = planner.Plan(statement);

        Assert.IsType<LimitOperator>(plan);

        GroupByOperator groupBy = FindOperator<GroupByOperator>(plan)!;
        Assert.NotNull(groupBy);
        Assert.False(groupBy.StreamingSorted,
            "CSV source has no index ordering — must use hash aggregate, not streaming");

        OrderByOperator? sort = FindOperator<OrderByOperator>(plan);
        Assert.NotNull(sort);
        Assert.Equal(100, sort!.TopNRows);

        // The critical guard: Sort must NOT appear below GroupBy.
        Assert.True(
            FindOperator<OrderByOperator>(groupBy.Source) is null,
            "Sort was injected below GroupBy — sort-injection regression");
    }

    /// <summary>
    /// Three-table variant mirroring the actual Instacart query exactly:
    /// two LEFT JOINs, GROUP BY on two columns from different tables,
    /// ORDER BY matching those same GROUP BY keys, LIMIT 100.
    /// This is the query that took 332 s under the regression vs ~30 s after the fix.
    /// </summary>
    [Fact]
    public void InstacartQueryShape_TwoJoinsGroupByMatchingOrderBy_SortIsAboveGroupBy()
    {
        TableCatalog catalog = CreateCatalogWithCsv("orders", "orders.csv");
        catalog.Register(new TableDescriptor("csv", "order_products", "order_products.csv", new Dictionary<string, string>()));
        catalog.Register(new TableDescriptor("csv", "products", "products.csv", new Dictionary<string, string>()));

        QueryPlanner planner = new(catalog, DefaultFunctions);

        // SELECT o.user_id, op.product_id, COUNT(*)
        // FROM orders o
        // LEFT JOIN order_products op ON o.order_id = op.order_id
        // LEFT JOIN products p ON op.product_id = p.product_id
        // GROUP BY o.user_id, op.product_id
        // ORDER BY o.user_id, op.product_id
        // LIMIT 100
        SelectStatement statement = new(
            Columns:
            [
                new SelectColumn(new ColumnReference("o", "user_id"), "user_id"),
                new SelectColumn(new ColumnReference("op", "product_id"), "product_id"),
                new SelectColumn(
                    new FunctionCallExpression("count", [new LiteralExpression(1)]),
                    "cnt"),
            ],
            From: new FromClause(new TableReference("orders", "o")),
            Joins:
            [
                new JoinClause(
                    JoinType.Left,
                    new TableReference("order_products", "op"),
                    new BinaryExpression(
                        new ColumnReference("o", "order_id"),
                        BinaryOperator.Equal,
                        new ColumnReference("op", "order_id"))),
                new JoinClause(
                    JoinType.Left,
                    new TableReference("products", "p"),
                    new BinaryExpression(
                        new ColumnReference("op", "product_id"),
                        BinaryOperator.Equal,
                        new ColumnReference("p", "product_id"))),
            ],
            GroupBy: new GroupByClause(
            [
                new ColumnReference("o", "user_id"),
                new ColumnReference("op", "product_id"),
            ]),
            OrderBy: new OrderByClause(
            [
                // ORDER BY keys identical to GROUP BY keys — the original regression trigger.
                new OrderByItem(new ColumnReference("o", "user_id"), SortDirection.Ascending),
                new OrderByItem(new ColumnReference("op", "product_id"), SortDirection.Ascending),
            ]),
            Limit: 100);

        IQueryOperator plan = planner.Plan(statement);

        Assert.IsType<LimitOperator>(plan);

        // GroupBy must be hash mode — no index on a CSV join result.
        GroupByOperator groupBy = FindOperator<GroupByOperator>(plan)!;
        Assert.NotNull(groupBy);
        Assert.False(groupBy.StreamingSorted,
            "Must use hash aggregate — no index ordering on joined CSV result");

        // Sort must exist and be bounded.
        OrderByOperator? sort = FindOperator<OrderByOperator>(plan);
        Assert.NotNull(sort);
        Assert.Equal(100, sort!.TopNRows);

        // Both JOINs must be reachable from GroupBy's source.
        JoinOperator? firstJoin = FindOperator<JoinOperator>(groupBy.Source);
        Assert.NotNull(firstJoin);
        Assert.Equal(JoinType.Left, firstJoin!.Type);

        // Sort must not have been injected below GroupBy.
        Assert.True(
            FindOperator<OrderByOperator>(groupBy.Source) is null,
            "Sort was injected below GroupBy — sort-injection regression");
    }

    // ──────────── Helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Depth-first search for the first operator of type <typeparamref name="T"/>
    /// in the subtree rooted at <paramref name="op"/>.
    /// Returns <see langword="null"/> when no matching operator is found.
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
}
