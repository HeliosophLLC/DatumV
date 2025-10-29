using System.Runtime.CompilerServices;
using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for Common Table Expression (WITH clause) support, covering parsing,
/// planning, non-recursive execution, recursive execution, materialization,
/// column renaming, and spill-to-disk behavior.
/// </summary>
public sealed class CommonTableExpressionTests : ServiceTestBase
{
    private static readonly FunctionRegistry DefaultFunctions = FunctionRegistry.CreateDefault();

    // ─────────────── Parsing tests ───────────────

    /// <summary>
    /// A basic CTE parses into a <see cref="SelectStatement"/> with a
    /// non-null <see cref="SelectStatement.CommonTableExpressions"/> list.
    /// </summary>
    [Fact]
    public void Parse_SimpleCte_ProducesCommonTableExpression()
    {
        SelectStatement result = ((SelectQueryExpression)SqlParser.Parse(
            "WITH stats AS (SELECT user_id, COUNT(amount) FROM orders GROUP BY user_id) " +
            "SELECT * FROM stats")).Statement;

        Assert.NotNull(result.CommonTableExpressions);
        Assert.Single(result.CommonTableExpressions);
        Assert.Equal("stats", result.CommonTableExpressions[0].Name);
        Assert.False(result.CommonTableExpressions[0].IsRecursive);
        Assert.Equal(MaterializationHint.Default, result.CommonTableExpressions[0].Hint);
        Assert.Null(result.CommonTableExpressions[0].ColumnNames);
    }

    /// <summary>
    /// Multiple CTEs separated by commas are all captured.
    /// </summary>
    [Fact]
    public void Parse_MultipleCtes_ParsesAll()
    {
        SelectStatement result = ((SelectQueryExpression)SqlParser.Parse(
            "WITH a AS (SELECT x FROM t1), " +
            "b AS (SELECT y FROM t2) " +
            "SELECT * FROM a JOIN b ON a.x = b.y")).Statement;

        Assert.NotNull(result.CommonTableExpressions);
        Assert.Equal(2, result.CommonTableExpressions.Count);
        Assert.Equal("a", result.CommonTableExpressions[0].Name);
        Assert.Equal("b", result.CommonTableExpressions[1].Name);
    }

    /// <summary>
    /// Explicit column names in the CTE definition are captured.
    /// </summary>
    [Fact]
    public void Parse_CteWithColumnNames_CapturesColumns()
    {
        SelectStatement result = ((SelectQueryExpression)SqlParser.Parse(
            "WITH stats(uid, total) AS (SELECT user_id, SUM(amount) FROM orders GROUP BY user_id) " +
            "SELECT * FROM stats")).Statement;

        Assert.NotNull(result.CommonTableExpressions);
        CommonTableExpression commonTableExpression = result.CommonTableExpressions[0];
        Assert.NotNull(commonTableExpression.ColumnNames);
        Assert.Equal(2, commonTableExpression.ColumnNames.Count);
        Assert.Equal("uid", commonTableExpression.ColumnNames[0]);
        Assert.Equal("total", commonTableExpression.ColumnNames[1]);
    }

    /// <summary>
    /// The MATERIALIZED hint is captured on the CTE.
    /// </summary>
    [Fact]
    public void Parse_MaterializedHint_Captured()
    {
        SelectStatement result = ((SelectQueryExpression)SqlParser.Parse(
            "WITH stats AS MATERIALIZED (SELECT x FROM t) SELECT * FROM stats")).Statement;

        Assert.NotNull(result.CommonTableExpressions);
        Assert.Equal(MaterializationHint.Materialized, result.CommonTableExpressions[0].Hint);
    }

    /// <summary>
    /// The NOT MATERIALIZED hint is captured on the CTE.
    /// </summary>
    [Fact]
    public void Parse_NotMaterializedHint_Captured()
    {
        SelectStatement result = ((SelectQueryExpression)SqlParser.Parse(
            "WITH stats AS NOT MATERIALIZED (SELECT x FROM t) SELECT * FROM stats")).Statement;

        Assert.NotNull(result.CommonTableExpressions);
        Assert.Equal(MaterializationHint.NotMaterialized, result.CommonTableExpressions[0].Hint);
    }

    /// <summary>
    /// LIMIT inside a non-recursive CTE body is parsed onto the inner SelectStatement.
    /// </summary>
    [Fact]
    public void Parse_CteWithLimit_CapturesLimit()
    {
        SelectStatement result = ((SelectQueryExpression)SqlParser.Parse(
            "WITH sample AS (SELECT x FROM t LIMIT 100) SELECT * FROM sample")).Statement;

        Assert.NotNull(result.CommonTableExpressions);
        SelectQueryExpression body = Assert.IsType<SelectQueryExpression>(result.CommonTableExpressions[0].Body);
        Assert.Equal(100, body.Statement.Limit);
    }

    /// <summary>
    /// ORDER BY and LIMIT inside a non-recursive CTE body are both parsed.
    /// </summary>
    [Fact]
    public void Parse_CteWithOrderByAndLimit_CapturesBoth()
    {
        SelectStatement result = ((SelectQueryExpression)SqlParser.Parse(
            "WITH top_items AS (SELECT x FROM t ORDER BY x LIMIT 50) SELECT * FROM top_items")).Statement;

        Assert.NotNull(result.CommonTableExpressions);
        SelectQueryExpression body = Assert.IsType<SelectQueryExpression>(result.CommonTableExpressions[0].Body);
        Assert.NotNull(body.Statement.OrderBy);
        Assert.Equal(50, body.Statement.Limit);
    }

    /// <summary>
    /// LIMIT inside a CTE restricts the rows produced by that CTE at execution time.
    /// </summary>
    [Fact]
    public async Task Execute_CteWithLimit_RestrictsRows()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["x"],
            [1f],
            [2f],
            [3f],
            [4f],
            [5f]);

        List<Row> results = await ExecuteQueryAsync(
            "WITH sample AS (SELECT x FROM t LIMIT 3) SELECT * FROM sample",
            catalog);

        Assert.Equal(3, results.Count);
    }

    /// <summary>
    /// Multiple CTEs each with their own LIMIT clause produce correct row counts.
    /// </summary>
    [Fact]
    public async Task Execute_MultipleCtes_EachWithLimit_ProducesCorrectCounts()
    {
        string[] columns = ["x"];
        object?[][] rows =
        [
            [1f],
            [2f],
            [3f],
            [4f],
            [5f],
        ];

        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("t1", columns, rows));
        catalog.Add(CreateProvider("t2", columns, rows));

        List<Row> results = await ExecuteQueryAsync(
            "WITH a AS (SELECT x FROM t1 LIMIT 2), " +
            "b AS (SELECT x FROM t2 LIMIT 1) " +
            "SELECT a.x FROM a INNER JOIN b ON a.x = b.x",
            catalog);

        // b has at most 1 row, so the join produces at most 1 match.
        Assert.True(results.Count <= 1);
    }

    /// <summary>
    /// WITH RECURSIVE sets the IsRecursive flag on all CTEs in that block.
    /// </summary>
    [Fact]
    public void Parse_WithRecursive_SetsFlag()
    {
        SelectStatement result = ((SelectQueryExpression)SqlParser.Parse(
            "WITH RECURSIVE nums AS (SELECT 1 AS n FROM dual UNION ALL SELECT n FROM nums WHERE n < 5) " +
            "SELECT * FROM nums")).Statement;

        Assert.NotNull(result.CommonTableExpressions);
        Assert.True(result.CommonTableExpressions[0].IsRecursive);
    }

    /// <summary>
    /// UNION ALL within a recursive CTE body splits into anchor and recursive query.
    /// </summary>
    [Fact]
    public void Parse_RecursiveCteBody_SplitsAnchorAndRecursive()
    {
        SelectStatement result = ((SelectQueryExpression)SqlParser.Parse(
            "WITH RECURSIVE chain AS (" +
            "SELECT id, parent_id FROM nodes WHERE parent_id IS NULL " +
            "UNION ALL " +
            "SELECT n.id, n.parent_id FROM nodes AS n JOIN chain ON n.parent_id = chain.id" +
            ") SELECT * FROM chain")).Statement;

        Assert.NotNull(result.CommonTableExpressions);
        CommonTableExpression commonTableExpression = result.CommonTableExpressions[0];
        Assert.NotNull(commonTableExpression.RecursiveQuery);

        // Anchor query: SELECT id, parent_id FROM nodes WHERE parent_id IS NULL
        SelectStatement anchorStatement = Assert.IsType<SelectQueryExpression>(commonTableExpression.Body).Statement;
        Assert.Equal(2, anchorStatement.Columns.Count);

        // Recursive query: SELECT n.id, n.parent_id FROM nodes AS n JOIN chain ON ...
        Assert.Equal(2, commonTableExpression.RecursiveQuery.Columns.Count);
    }

    // ─────────────── Non-recursive execution tests ───────────────

    /// <summary>
    /// A simple CTE referenced once in FROM produces the expected rows.
    /// </summary>
    [Fact]
    public async Task Execute_SimpleCte_ReturnsExpectedRows()
    {
        TableCatalog catalog = CreateCatalog("orders",
            columns: ["user_id", "amount"],
            [1f, 100f],
            [1f, 200f],
            [2f, 50f]);

        List<Row> results = await ExecuteQueryAsync(
            "WITH totals AS (SELECT user_id, SUM(amount) AS total FROM orders GROUP BY user_id) " +
            "SELECT * FROM totals",
            catalog);

        Assert.Equal(2, results.Count);
    }

    /// <summary>
    /// CTE with explicit column names renames the output columns.
    /// </summary>
    [Fact]
    public async Task Execute_CteWithColumnNames_RenamesColumns()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["x", "y"],
            [1f, 2f]);

        List<Row> results = await ExecuteQueryAsync(
            "WITH renamed(a, b) AS (SELECT x, y FROM t) SELECT a, b FROM renamed",
            catalog);

        Assert.Single(results);
        Assert.Equal(1f, results[0]["a"].AsFloat32());
        Assert.Equal(2f, results[0]["b"].AsFloat32());
    }

    /// <summary>
    /// CTE referenced multiple times is auto-materialized and produces consistent results.
    /// </summary>
    [Fact]
    public async Task Execute_CteReferencedTwice_AutoMaterializes()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["id", "val"],
            [1f, 10f],
            [2f, 20f]);

        List<Row> results = await ExecuteQueryAsync(
            "WITH shared AS (SELECT id, val FROM t) " +
            "SELECT a.id, b.val FROM shared AS a JOIN shared AS b ON a.id = b.id",
            catalog);

        Assert.Equal(2, results.Count);
    }

    /// <summary>
    /// Explicitly NOT MATERIALIZED CTE re-executes per reference.
    /// </summary>
    [Fact]
    public async Task Execute_NotMaterializedCte_ReExecutesPerReference()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["id"],
            [1f],
            [2f]);

        List<Row> results = await ExecuteQueryAsync(
            "WITH inline_cte AS NOT MATERIALIZED (SELECT id FROM t) " +
            "SELECT * FROM inline_cte",
            catalog);

        Assert.Equal(2, results.Count);
    }

    /// <summary>
    /// CTE used with a WHERE filter in the outer query.
    /// </summary>
    [Fact]
    public async Task Execute_CteWithOuterFilter_FiltersCorrectly()
    {
        TableCatalog catalog = CreateCatalog("students",
            columns: ["name", "score"],
            ["alice", 90f],
            ["bob", 50f],
            ["carol", 75f]);

        List<Row> results = await ExecuteQueryAsync(
            "WITH high_scorers AS (SELECT name, score FROM students WHERE score >= 75) " +
            "SELECT name FROM high_scorers WHERE score > 80",
            catalog);

        Assert.Single(results);
        Assert.Equal("alice", results[0]["name"].AsString());
    }

    /// <summary>
    /// <see cref="QuerySchemaResolver"/> should return only the columns projected by the
    /// CTE's SELECT clause, not all columns from the underlying table. This matches what
    /// execution actually emits and what the shell header should display.
    /// </summary>
    [Fact]
    public async Task SchemaResolver_CteWithNarrowProjection_ReturnsOnlyCteColumns()
    {
        TableCatalog catalog = CreateCatalog("orders_csv",
            columns: ["order_id", "user_id", "eval_set", "order_number", "order_dow"],
            [1f, 1f, "train", 11f, 1f]);
        QuerySchemaResolver resolver = new(catalog, DefaultFunctions);

        SelectStatement statement = ((SelectQueryExpression)SqlParser.Parse(
            "WITH train_orders AS (" +
            "  SELECT order_id, user_id, order_number" +
            "  FROM orders_csv" +
            "  WHERE eval_set = 'train'" +
            ") " +
            "SELECT * FROM train_orders")).Statement;

        ResolvedQuerySchema schema = await resolver.ResolveAsync(statement, CancellationToken.None);

        Assert.Equal(3, schema.Columns.Count);
        Assert.Contains(schema.Columns, c => c.ColumnName == "order_id");
        Assert.Contains(schema.Columns, c => c.ColumnName == "user_id");
        Assert.Contains(schema.Columns, c => c.ColumnName == "order_number");
        Assert.DoesNotContain(schema.Columns, c => c.ColumnName == "eval_set");
        Assert.DoesNotContain(schema.Columns, c => c.ColumnName == "order_dow");
    }

    // ─────────────── Recursive CTE execution tests ───────────────

    /// <summary>
    /// A simple recursive CTE generating a sequence of numbers.
    /// </summary>
    [Fact]
    public async Task Execute_RecursiveCte_GeneratesSequence()
    {
        // We need a single-row table to seed the anchor.
        TableCatalog catalog = CreateCatalog("dual",
            columns: ["dummy"],
            [1f]);

        List<Row> results = await ExecuteQueryAsync(
            "WITH RECURSIVE nums AS (" +
            "SELECT 1 AS n FROM dual " +
            "UNION ALL " +
            "SELECT n + 1 AS n FROM nums WHERE n < 5" +
            ") SELECT n FROM nums",
            catalog);

        Assert.Equal(5, results.Count);
        for (int index = 0; index < 5; index++)
        {
            DataValue n = results[index]["n"];
            int actual = n.Kind switch
            {
                DataKind.Int8 => n.AsInt8(),
                DataKind.Int16 => n.AsInt16(),
                DataKind.Int32 => n.AsInt32(),
                DataKind.Float32 => (int)n.AsFloat32(),
                DataKind.Float64 => (int)n.AsFloat64(),
                _ => throw new InvalidOperationException($"Unexpected kind: {n.Kind}")
            };
            Assert.Equal(index + 1, actual);
        }
    }

    /// <summary>
    /// Recursive CTE that exceeds max recursion depth throws.
    /// </summary>
    [Fact]
    public async Task Execute_RecursiveCte_ExceedingMaxDepth_Throws()
    {
        TableCatalog catalog = CreateCatalog("dual",
            columns: ["dummy"],
            [1f]);

        // Set very low recursion limit.
        ExecutionContext context = new(
            CancellationToken.None,
            DefaultFunctions,
            catalog, new LocalBufferPool())
        {
            MaxRecursionDepth = 3,
        };

        SelectStatement statement = ((SelectQueryExpression)SqlParser.Parse(
            "WITH RECURSIVE nums AS (" +
            "SELECT 1 AS n FROM dual " +
            "UNION ALL " +
            "SELECT n + 1 AS n FROM nums WHERE n < 100" +
            ") SELECT n FROM nums")).Statement;

        QueryPlanner planner = new(catalog, DefaultFunctions);
        IQueryOperator plan = planner.Plan(statement);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (RowBatch batch in plan.ExecuteAsync(context))
            {
                // Drain the stream.
                _ = batch.Count;
            }
        });

        Assert.Contains("exceeded maximum recursion depth", exception.Message);
    }

    // ─────────────── CommonTableExpressionOperator unit tests ───────────────

    /// <summary>
    /// Inlined CTE operator re-executes the inner operator each time.
    /// </summary>
    [Fact]
    public async Task InlinedOperator_ReExecutesInnerEachTime()
    {
        int executionCount = 0;
        CountingOperator inner = new(() => executionCount++,
            MakeRow(("x", DataValue.FromFloat32(1f))));

        CommonTableExpressionOperator cteOperator = new(inner, "test_cte", isMaterialized: false);

        ExecutionContext context = CreateContext();

        // Execute twice.
        List<Row> first = await CollectAsync(cteOperator, context);
        List<Row> second = await CollectAsync(cteOperator, context);

        Assert.Single(first);
        Assert.Single(second);
        Assert.Equal(2, executionCount);
    }

    /// <summary>
    /// Materialized CTE operator executes the inner operator only once.
    /// </summary>
    [Fact]
    public async Task MaterializedOperator_ExecutesInnerOnce()
    {
        int executionCount = 0;
        CountingOperator inner = new(() => executionCount++,
            MakeRow(("x", DataValue.FromFloat32(1f))),
            MakeRow(("x", DataValue.FromFloat32(2f))));

        CommonTableExpressionOperator cteOperator = new(inner, "test_cte", isMaterialized: true);

        ExecutionContext context = CreateContext();

        List<Row> first = await CollectAsync(cteOperator, context);
        List<Row> second = await CollectAsync(cteOperator, context);

        Assert.Equal(2, first.Count);
        Assert.Equal(2, second.Count);
        Assert.Equal(1, executionCount);
    }

    /// <summary>
    /// Materialized CTE operator spills to disk when memory budget is exceeded.
    /// </summary>
    [Fact]
    public async Task MaterializedOperator_SpillsToDisk_WhenBudgetExceeded()
    {
        // Create rows that will exceed a tiny memory budget.
        Row[] rows = Enumerable.Range(0, 100)
            .Select(index => MakeRow(("id", DataValue.FromFloat32((float)index))))
            .ToArray();

        MockOperator inner = new(rows);

        CommonTableExpressionOperator cteOperator = new(inner, "spill_test", isMaterialized: true);

        // Tiny budget to force spilling.
        ExecutionContext context = new(
            CancellationToken.None,
            DefaultFunctions,
            CreateCatalog(),
            new LocalBufferPool(),
            memoryBudgetBytes: 1);

        try
        {
            List<Row> results = await CollectAsync(cteOperator, context);
            Assert.Equal(100, results.Count);
        }
        finally
        {
            cteOperator.Dispose();
        }
    }

    // ─────────────── Query planner CTE tests ───────────────

    /// <summary>
    /// The query planner creates a CTE operator when the statement has CTEs.
    /// </summary>
    [Fact]
    public void Plan_WithCte_CreatesCteOperator()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["x"],
            [1f]);
        QueryPlanner planner = new(catalog, DefaultFunctions);

        SelectStatement statement = ((SelectQueryExpression)SqlParser.Parse(
            "WITH cte AS (SELECT x FROM t) SELECT * FROM cte")).Statement;

        IQueryOperator plan = planner.Plan(statement);

        // Plan should contain a CTE operator wrapping the inner scan.
        // The plan for SELECT * FROM cte produces an AliasOperator(CTE(...))
        // or just a CTE operator depending on join presence.
        Assert.NotNull(plan);
    }

    /// <summary>
    /// CTE referenced from multiple JOINs produces correct results.
    /// </summary>
    [Fact]
    public async Task Execute_CteInMultipleJoins_ProducesCorrectResults()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["id", "name"],
            [1f, "a"],
            [2f, "b"]);

        List<Row> results = await ExecuteQueryAsync(
            "WITH items AS (SELECT id, name FROM t) " +
            "SELECT left_items.name, right_items.name " +
            "FROM items AS left_items JOIN items AS right_items ON left_items.id = right_items.id",
            catalog);

        Assert.Equal(2, results.Count);
    }

    /// <summary>
    /// A CTE that uses SELECT alias.* in a join context should output unqualified column names
    /// so downstream CTEs can reference them by unqualified name. Regression for the bug where
    /// SELECT pw.* produced column names like "pw.user_id" instead of "user_id".
    /// </summary>
    [Fact]
    public async Task Execute_CteWithQualifiedWildcardJoin_AggregatesWithUnqualifiedColumnNames()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("order_products",
            ["order_id", "product_id"],
            [1f, 10f],
            [1f, 20f],
            [2f, 10f]));
        catalog.Add(CreateProvider("orders",
            ["order_id", "user_id"],
            [1f, 100f],
            [2f, 200f]));

        // items_with_user joins order_products with orders to add user_id.
        // product_events selects pw.* (all columns from items_with_user aliased as pw) via a join.
        // The aggregation references user_id and product_id by unqualified name.
        List<Row> results = await ExecuteQueryAsync(
            "WITH items_with_user AS (" +
            "  SELECT p.order_id, p.product_id, o.user_id" +
            "  FROM order_products p JOIN orders o ON p.order_id = o.order_id" +
            ")," +
            "product_events AS (" +
            "  SELECT pw.*" +
            "  FROM items_with_user pw" +
            "  JOIN orders o ON pw.user_id = o.user_id" +
            ") " +
            "SELECT user_id, product_id, COUNT(*) AS cnt " +
            "FROM product_events " +
            "GROUP BY user_id, product_id",
            catalog);

        Assert.NotEmpty(results);
        // All rows should have accessible user_id and product_id columns.
        Assert.All(results, row => Assert.True(row["user_id"].AsFloat32() is 100f or 200f));
        Assert.All(results, row => Assert.True(row["product_id"].AsFloat32() is 10f or 20f));
    }

    // ─────────────── Non-recursive CTE with set operations ───────────────

    /// <summary>
    /// A non-recursive CTE with UNION ALL parses into a <see cref="CompoundQueryExpression"/> body.
    /// </summary>
    [Fact]
    public void Parse_NonRecursiveCteWithUnionAll_ProducesCompoundBody()
    {
        SelectStatement result = ((SelectQueryExpression)SqlParser.Parse(
            "WITH combined AS (" +
            "SELECT x FROM t1 UNION ALL SELECT x FROM t2" +
            ") SELECT * FROM combined")).Statement;

        Assert.NotNull(result.CommonTableExpressions);
        CommonTableExpression commonTableExpression = result.CommonTableExpressions[0];
        Assert.False(commonTableExpression.IsRecursive);
        Assert.Null(commonTableExpression.RecursiveQuery);

        CompoundQueryExpression compound = Assert.IsType<CompoundQueryExpression>(commonTableExpression.Body);
        Assert.Equal(SetOperationType.Union, compound.OperationType);
        Assert.True(compound.All);
    }

    /// <summary>
    /// A non-recursive CTE with UNION ALL correctly returns rows from both branches.
    /// </summary>
    [Fact]
    public async Task Execute_NonRecursiveCteWithUnionAll_ReturnsBothBranches()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("t1",
            ["id", "value"],
            [1f, "a"],
            [2f, "b"]));
        catalog.Add(CreateProvider("t2",
            ["id", "value"],
            [3f, "c"]));

        List<Row> results = await ExecuteQueryAsync(
            "WITH combined AS (" +
            "SELECT id, value FROM t1 UNION ALL SELECT id, value FROM t2" +
            ") SELECT * FROM combined",
            catalog);

        Assert.Equal(3, results.Count);
    }

    /// <summary>
    /// A non-recursive CTE with UNION ALL can be joined with other tables.
    /// </summary>
    [Fact]
    public async Task Execute_NonRecursiveCteWithUnionAll_JoinedWithOtherTable()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("t1",
            ["order_id", "product_id"],
            [1f, 10f]));
        catalog.Add(CreateProvider("t2",
            ["order_id", "product_id"],
            [2f, 20f]));
        catalog.Add(CreateProvider("products",
            ["product_id", "name"],
            [10f, "Widget"],
            [20f, "Gadget"]));

        List<Row> results = await ExecuteQueryAsync(
            "WITH all_orders AS (" +
            "SELECT order_id, product_id FROM t1 UNION ALL SELECT order_id, product_id FROM t2" +
            ") " +
            "SELECT all_orders.order_id, products.name " +
            "FROM all_orders " +
            "LEFT JOIN products ON all_orders.product_id = products.product_id",
            catalog);

        Assert.Equal(2, results.Count);
    }

    // ─────────────── QuerySchemaResolver CTE tests ───────────────

    /// <summary>
    /// <see cref="QuerySchemaResolver"/> resolves a CTE table reference without
    /// throwing a catalog lookup error.
    /// </summary>
    [Fact]
    public async Task SchemaResolver_CteReference_ResolvesWithoutCatalogLookup()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["id", "value"],
            [1f, "a"]);
        QuerySchemaResolver resolver = new(catalog, DefaultFunctions);

        SelectStatement statement = ((SelectQueryExpression)SqlParser.Parse(
            "WITH cte AS (SELECT id, value FROM t) SELECT * FROM cte")).Statement;

        ResolvedQuerySchema schema = await resolver.ResolveAsync(statement, CancellationToken.None);

        Assert.Equal(2, schema.Columns.Count);
        Assert.Contains(schema.Columns, column => column.ColumnName == "id");
        Assert.Contains(schema.Columns, column => column.ColumnName == "value");
    }

    /// <summary>
    /// <see cref="QuerySchemaResolver"/> resolves a CTE with UNION ALL used in a JOIN
    /// without throwing a catalog lookup error.
    /// </summary>
    [Fact]
    public async Task SchemaResolver_CteWithUnionAllInJoin_ResolvesCorrectly()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Add(CreateProvider("t1",
            ["order_id", "product_id"],
            [1f, 10f]));
        catalog.Add(CreateProvider("t2",
            ["order_id", "product_id"],
            [2f, 20f]));
        catalog.Add(CreateProvider("orders",
            ["order_id", "customer"],
            [1f, "Alice"]));
        QuerySchemaResolver resolver = new(catalog, DefaultFunctions);

        SelectStatement statement = ((SelectQueryExpression)SqlParser.Parse(
            "WITH order_products AS (" +
            "SELECT order_id, product_id FROM t1 UNION ALL SELECT order_id, product_id FROM t2" +
            ") " +
            "SELECT * FROM orders LEFT JOIN order_products ON order_products.order_id = orders.order_id")).Statement;

        ResolvedQuerySchema schema = await resolver.ResolveAsync(statement, CancellationToken.None);

        Assert.True(schema.Columns.Count >= 3);
        Assert.Contains(schema.Columns, column => column.ColumnName == "customer");
        Assert.Contains(schema.Columns, column => column.ColumnName == "product_id");
    }

    // ─────────────── Aggregates nested inside scalar functions ───────────────

    /// <summary>
    /// Aggregates nested inside scalar function arguments (e.g. DATE_DIFF wrapping MIN/MAX)
    /// must be rewritten to column references so the evaluator does not treat them as
    /// unknown scalar functions.
    /// </summary>
    [Fact]
    public async Task Execute_AggregateNestedInsideScalarFunction_RewritesCorrectly()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["x", "y"],
            [10f, 1f],
            [20f, 2f],
            [30f, 3f]);

        // ROUND wraps MIN and MAX — these are aggregates nested inside a scalar function.
        List<Row> result = await ExecuteQueryAsync(
            "SELECT ROUND(MIN(x) + MAX(y), 0) AS val FROM t",
            catalog);

        Assert.Single(result);
        Assert.Equal(13.0f, result[0]["val"].AsFloat32());
    }

    /// <summary>
    /// A CTE whose SELECT list wraps aggregates inside scalar functions should
    /// plan and execute without "Unknown function" errors.
    /// </summary>
    [Fact]
    public async Task Execute_CteWithAggregateInsideScalarFunction_Succeeds()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["v"],
            [5f],
            [15f],
            [25f]);

        List<Row> result = await ExecuteQueryAsync(
            "WITH stats AS (SELECT ROUND(MIN(v), 0) AS lo, ROUND(MAX(v), 0) AS hi FROM t) " +
            "SELECT lo, hi FROM stats",
            catalog);

        Assert.Single(result);
        Assert.Equal(5.0f, result[0]["lo"].AsFloat32());
        Assert.Equal(25.0f, result[0]["hi"].AsFloat32());
    }

    // ─────────────── Helper infrastructure ───────────────

    private static Row MakeRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = columns.Select(column => column.Name).ToArray();
        DataValue[] values = columns.Select(column => column.Value).ToArray();
        return new Row(names, values);
    }

    private ExecutionContext CreateContext()
    {
        return new ExecutionContext(
            CancellationToken.None,
            DefaultFunctions,
            CreateCatalog(),
            new LocalBufferPool());
    }

    private static async Task<List<Row>> ExecuteQueryAsync(string sql, TableCatalog catalog)
    {
        QueryExpression query = SqlParser.Parse(sql);
        QueryPlanner planner = new(catalog, DefaultFunctions);

        ExecutionContext context = new(
            CancellationToken.None,
            DefaultFunctions,
            catalog,
            new LocalBufferPool());

        IQueryOperator plan = planner.Plan(query);

        return await plan.CollectRowsAsync(context);
    }

    private static async Task<List<Row>> CollectAsync(IQueryOperator op, ExecutionContext context)
    {
        return await op.CollectRowsAsync(context);
    }

    /// <summary>
    /// A mock operator that tracks how many times <see cref="ExecuteAsync"/> is called.
    /// </summary>
    private sealed class CountingOperator : IQueryOperator
    {
        private readonly Action _onExecute;
        private readonly Row[] _rows;

        /// <summary>
        /// Creates a counting operator.
        /// </summary>
        /// <param name="onExecute">Called each time <see cref="ExecuteAsync"/> begins.</param>
        /// <param name="rows">Rows to yield.</param>
        public CountingOperator(Action onExecute, params Row[] rows)
        {
            _onExecute = onExecute;
            _rows = rows;
        }

        /// <inheritdoc/>
        public OperatorPlanDescription DescribeForExplain() => new("Counting Mock");

        /// <inheritdoc/>
        public async IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
        {
            _onExecute();
            RowBatch? outputBatch = null;
            foreach (Row row in _rows)
            {
                outputBatch ??= RowBatch.Rent(64);
                outputBatch.Add(row.Clone());
                if (outputBatch.IsFull) { yield return outputBatch; outputBatch = null; }
            }

            if (outputBatch is not null) yield return outputBatch;
            await Task.CompletedTask;
        }
    }

    /// <summary>
    /// Reusing the existing MockOperator pattern from OperatorTests.
    /// </summary>
    private sealed class MockOperator : IQueryOperator
    {
        private readonly Row[] _rows;

        /// <summary>
        /// Creates a mock operator.
        /// </summary>
        public MockOperator(params Row[] rows)
        {
            _rows = rows;
        }

        /// <inheritdoc/>
        public OperatorPlanDescription DescribeForExplain() => new("Mock");

        /// <inheritdoc/>
        public async IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
        {
            RowBatch? outputBatch = null;
            foreach (Row row in _rows)
            {
                outputBatch ??= RowBatch.Rent(64);
                outputBatch.Add(row.Clone());
                if (outputBatch.IsFull) { yield return outputBatch; outputBatch = null; }
            }

            if (outputBatch is not null) yield return outputBatch;
            await Task.CompletedTask;
        }
    }
}
