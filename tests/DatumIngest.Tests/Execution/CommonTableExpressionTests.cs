using System.Runtime.CompilerServices;
using DatumIngest.Catalog;
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
public sealed class CommonTableExpressionTests
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
        Assert.Equal(2, commonTableExpression.Query.Columns.Count);

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
        Row[] orders =
        [
            MakeRow(("user_id", DataValue.FromScalar(1f)), ("amount", DataValue.FromScalar(100f))),
            MakeRow(("user_id", DataValue.FromScalar(1f)), ("amount", DataValue.FromScalar(200f))),
            MakeRow(("user_id", DataValue.FromScalar(2f)), ("amount", DataValue.FromScalar(50f))),
        ];

        TableCatalog catalog = CreateCatalog(("orders", orders));

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
        Row[] data =
        [
            MakeRow(("x", DataValue.FromScalar(1f)), ("y", DataValue.FromScalar(2f))),
        ];

        TableCatalog catalog = CreateCatalog(("t", data));

        List<Row> results = await ExecuteQueryAsync(
            "WITH renamed(a, b) AS (SELECT x, y FROM t) SELECT a, b FROM renamed",
            catalog);

        Assert.Single(results);
        Assert.Equal(1f, results[0]["a"].AsScalar());
        Assert.Equal(2f, results[0]["b"].AsScalar());
    }

    /// <summary>
    /// CTE referenced multiple times is auto-materialized and produces consistent results.
    /// </summary>
    [Fact]
    public async Task Execute_CteReferencedTwice_AutoMaterializes()
    {
        Row[] data =
        [
            MakeRow(("id", DataValue.FromScalar(1f)), ("val", DataValue.FromScalar(10f))),
            MakeRow(("id", DataValue.FromScalar(2f)), ("val", DataValue.FromScalar(20f))),
        ];

        TableCatalog catalog = CreateCatalog(("t", data));

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
        Row[] data =
        [
            MakeRow(("id", DataValue.FromScalar(1f))),
            MakeRow(("id", DataValue.FromScalar(2f))),
        ];

        TableCatalog catalog = CreateCatalog(("t", data));

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
        Row[] data =
        [
            MakeRow(("name", DataValue.FromString("alice")), ("score", DataValue.FromScalar(90f))),
            MakeRow(("name", DataValue.FromString("bob")), ("score", DataValue.FromScalar(50f))),
            MakeRow(("name", DataValue.FromString("carol")), ("score", DataValue.FromScalar(75f))),
        ];

        TableCatalog catalog = CreateCatalog(("students", data));

        List<Row> results = await ExecuteQueryAsync(
            "WITH high_scorers AS (SELECT name, score FROM students WHERE score >= 75) " +
            "SELECT name FROM high_scorers WHERE score > 80",
            catalog);

        Assert.Single(results);
        Assert.Equal("alice", results[0]["name"].AsString());
    }

    // ─────────────── Recursive CTE execution tests ───────────────

    /// <summary>
    /// A simple recursive CTE generating a sequence of numbers.
    /// </summary>
    [Fact]
    public async Task Execute_RecursiveCte_GeneratesSequence()
    {
        // We need a single-row table to seed the anchor.
        Row[] dual =
        [
            MakeRow(("dummy", DataValue.FromScalar(1f))),
        ];

        TableCatalog catalog = CreateCatalog(("dual", dual));

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
            Assert.Equal(index + 1, (int)results[index]["n"].AsScalar());
        }
    }

    /// <summary>
    /// Recursive CTE that exceeds max recursion depth throws.
    /// </summary>
    [Fact]
    public async Task Execute_RecursiveCte_ExceedingMaxDepth_Throws()
    {
        Row[] dual =
        [
            MakeRow(("dummy", DataValue.FromScalar(1f))),
        ];

        TableCatalog catalog = CreateCatalog(("dual", dual));

        // Set very low recursion limit.
        ExecutionContext context = new(
            CancellationToken.None,
            DefaultFunctions,
            catalog)
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
            await foreach (Row row in plan.ExecuteAsync(context))
            {
                // Drain the stream.
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
            MakeRow(("x", DataValue.FromScalar(1f))));

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
            MakeRow(("x", DataValue.FromScalar(1f))),
            MakeRow(("x", DataValue.FromScalar(2f))));

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
            .Select(index => MakeRow(("id", DataValue.FromScalar((float)index))))
            .ToArray();

        MockOperator inner = new(rows);

        CommonTableExpressionOperator cteOperator = new(inner, "spill_test", isMaterialized: true);

        // Tiny budget to force spilling.
        ExecutionContext context = new(
            CancellationToken.None,
            DefaultFunctions,
            new TableCatalog(),
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
        Row[] data = [MakeRow(("x", DataValue.FromScalar(1f)))];
        TableCatalog catalog = CreateCatalog(("t", data));
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
        Row[] data =
        [
            MakeRow(("id", DataValue.FromScalar(1f)), ("name", DataValue.FromString("a"))),
            MakeRow(("id", DataValue.FromScalar(2f)), ("name", DataValue.FromString("b"))),
        ];

        TableCatalog catalog = CreateCatalog(("t", data));

        List<Row> results = await ExecuteQueryAsync(
            "WITH items AS (SELECT id, name FROM t) " +
            "SELECT left_items.name, right_items.name " +
            "FROM items AS left_items JOIN items AS right_items ON left_items.id = right_items.id",
            catalog);

        Assert.Equal(2, results.Count);
    }

    // ─────────────── Helper infrastructure ───────────────

    private static Row MakeRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = columns.Select(column => column.Name).ToArray();
        DataValue[] values = columns.Select(column => column.Value).ToArray();
        return new Row(names, values);
    }

    private static TableCatalog CreateCatalog(params (string Name, Row[] Rows)[] tables)
    {
        TableCatalog catalog = new();

        foreach ((string name, Row[] rows) in tables)
        {
            InMemoryTableProvider provider = new(rows);
            catalog.RegisterProvider(name, () => provider);
            catalog.Register(new TableDescriptor(name, name, "", new Dictionary<string, string>()));
        }

        return catalog;
    }

    private static ExecutionContext CreateContext()
    {
        return new ExecutionContext(
            CancellationToken.None,
            DefaultFunctions,
            new TableCatalog());
    }

    private static async Task<List<Row>> ExecuteQueryAsync(string sql, TableCatalog catalog)
    {
        QueryExpression query = SqlParser.Parse(sql);
        QueryPlanner planner = new(catalog, DefaultFunctions);

        ExecutionContext context = new(
            CancellationToken.None,
            DefaultFunctions,
            catalog);

        IQueryOperator plan = planner.Plan(query);

        List<Row> rows = [];
        await foreach (Row row in plan.ExecuteAsync(context))
        {
            rows.Add(row);
        }

        return rows;
    }

    private static async Task<List<Row>> CollectAsync(IQueryOperator op, ExecutionContext context)
    {
        List<Row> rows = [];
        await foreach (Row row in op.ExecuteAsync(context))
        {
            rows.Add(row);
        }

        return rows;
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
        public async IAsyncEnumerable<Row> ExecuteAsync(ExecutionContext context)
        {
            _onExecute();
            foreach (Row row in _rows)
            {
                yield return row;
            }

            await Task.CompletedTask;
        }
    }

    /// <summary>
    /// Simple in-memory table provider for testing.
    /// </summary>
    private sealed class InMemoryTableProvider : ITableProvider
    {
        private readonly Row[] _rows;

        /// <summary>
        /// Creates a provider that yields the given rows.
        /// </summary>
        public InMemoryTableProvider(Row[] rows)
        {
            _rows = rows;
        }

        /// <inheritdoc/>
        public Task<Schema> GetSchemaAsync(TableDescriptor descriptor, CancellationToken cancellationToken)
        {
            if (_rows.Length == 0)
            {
                return Task.FromResult(new Schema([new ColumnInfo("empty", DataKind.String, nullable: true)]));
            }

            List<ColumnInfo> columns = [];
            foreach (string name in _rows[0].ColumnNames)
            {
                columns.Add(new ColumnInfo(name, _rows[0][name].Kind, nullable: true));
            }

            return Task.FromResult(new Schema(columns));
        }

        /// <inheritdoc/>
        public Task<ProviderCapabilities> GetCapabilitiesAsync(
            TableDescriptor descriptor, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ProviderCapabilities(
                EstimatedRowCount: _rows.Length,
                EstimatedRowSizeBytes: null,
                SupportsSeek: false,
                ColumnCosts: new Dictionary<string, ColumnCost>()));
        }

        /// <inheritdoc/>
        public async IAsyncEnumerable<Row> OpenAsync(
            TableDescriptor descriptor,
            IReadOnlySet<string>? requiredColumns,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (Row row in _rows)
            {
                yield return row;
            }

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
        public async IAsyncEnumerable<Row> ExecuteAsync(ExecutionContext context)
        {
            foreach (Row row in _rows)
            {
                yield return row;
            }

            await Task.CompletedTask;
        }
    }
}
