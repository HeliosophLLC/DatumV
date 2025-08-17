using System.Runtime.CompilerServices;
using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Functions.Window;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for the QUALIFY clause — a post-window-function filter that
/// eliminates the need for subquery wrappers around window functions.
/// Covers parsing, planning, and end-to-end execution.
/// </summary>
public sealed class QualifyTests
{
    private static readonly FunctionRegistry DefaultFunctions = FunctionRegistry.CreateDefault();

    private static ExecutionContext CreateContext()
    {
        return new ExecutionContext(
            CancellationToken.None,
            DefaultFunctions,
            new TableCatalog());
    }

    private static Row MakeRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = columns.Select(c => c.Name).ToArray();
        DataValue[] values = columns.Select(c => c.Value).ToArray();
        return new Row(names, values);
    }

    private static async Task<List<Row>> CollectAsync(IQueryOperator operatorNode, ExecutionContext? context = null)
    {
        context ??= CreateContext();
        List<Row> rows = [];
        await foreach (Row row in operatorNode.ExecuteAsync(context))
        {
            rows.Add(row);
        }

        return rows;
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

    private static async Task<List<Row>> ExecuteQueryAsync(string sql, TableCatalog catalog)
    {
        QueryExpression query = SqlParser.Parse(sql);
        QueryPlanner planner = new(catalog, DefaultFunctions);
        ExecutionContext context = new(CancellationToken.None, DefaultFunctions, catalog);
        IQueryOperator plan = planner.Plan(query);

        List<Row> rows = [];
        await foreach (Row row in plan.ExecuteAsync(context))
        {
            rows.Add(row);
        }

        return rows;
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
        MockOperator source = new(
            MakeRow(("name", DataValue.FromString("a")), ("score", DataValue.FromFloat32(10f))),
            MakeRow(("name", DataValue.FromString("b")), ("score", DataValue.FromFloat32(30f))),
            MakeRow(("name", DataValue.FromString("c")), ("score", DataValue.FromFloat32(20f))));

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
        MockOperator source = new(
            MakeRow(("category", DataValue.FromString("X")), ("item", DataValue.FromString("x1")), ("score", DataValue.FromFloat32(10f))),
            MakeRow(("category", DataValue.FromString("X")), ("item", DataValue.FromString("x2")), ("score", DataValue.FromFloat32(30f))),
            MakeRow(("category", DataValue.FromString("X")), ("item", DataValue.FromString("x3")), ("score", DataValue.FromFloat32(20f))),
            MakeRow(("category", DataValue.FromString("Y")), ("item", DataValue.FromString("y1")), ("score", DataValue.FromFloat32(50f))),
            MakeRow(("category", DataValue.FromString("Y")), ("item", DataValue.FromString("y2")), ("score", DataValue.FromFloat32(40f))),
            MakeRow(("category", DataValue.FromString("Y")), ("item", DataValue.FromString("y3")), ("score", DataValue.FromFloat32(60f))));

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
        MockOperator source = new(
            MakeRow(("val", DataValue.FromFloat32(1f))),
            MakeRow(("val", DataValue.FromFloat32(2f))));

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
        Row[] data =
        [
            MakeRow(("category", DataValue.FromString("A")), ("score", DataValue.FromFloat32(10f))),
            MakeRow(("category", DataValue.FromString("A")), ("score", DataValue.FromFloat32(30f))),
            MakeRow(("category", DataValue.FromString("A")), ("score", DataValue.FromFloat32(20f))),
            MakeRow(("category", DataValue.FromString("B")), ("score", DataValue.FromFloat32(50f))),
            MakeRow(("category", DataValue.FromString("B")), ("score", DataValue.FromFloat32(40f))),
        ];

        TableCatalog catalog = CreateCatalog(("data", data));

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
        Row[] data =
        [
            MakeRow(("name", DataValue.FromString("alice")), ("score", DataValue.FromFloat32(10f))),
            MakeRow(("name", DataValue.FromString("bob")), ("score", DataValue.FromFloat32(30f))),
            MakeRow(("name", DataValue.FromString("carol")), ("score", DataValue.FromFloat32(20f))),
            MakeRow(("name", DataValue.FromString("dave")), ("score", DataValue.FromFloat32(40f))),
        ];

        TableCatalog catalog = CreateCatalog(("data", data));

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
        Row[] employees =
        [
            MakeRow(("department", DataValue.FromString("eng")), ("status", DataValue.FromString("active")), ("salary", DataValue.FromFloat32(100f))),
            MakeRow(("department", DataValue.FromString("eng")), ("status", DataValue.FromString("active")), ("salary", DataValue.FromFloat32(200f))),
            MakeRow(("department", DataValue.FromString("eng")), ("status", DataValue.FromString("inactive")), ("salary", DataValue.FromFloat32(300f))),
            MakeRow(("department", DataValue.FromString("sales")), ("status", DataValue.FromString("active")), ("salary", DataValue.FromFloat32(150f))),
            MakeRow(("department", DataValue.FromString("sales")), ("status", DataValue.FromString("active")), ("salary", DataValue.FromFloat32(250f))),
            MakeRow(("department", DataValue.FromString("hr")), ("status", DataValue.FromString("active")), ("salary", DataValue.FromFloat32(50f))),
        ];

        TableCatalog catalog = CreateCatalog(("employees", employees));

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
        Row[] data =
        [
            MakeRow(("category", DataValue.FromString("A")), ("value", DataValue.FromFloat32(1f))),
            MakeRow(("category", DataValue.FromString("A")), ("value", DataValue.FromFloat32(1f))),
            MakeRow(("category", DataValue.FromString("A")), ("value", DataValue.FromFloat32(2f))),
            MakeRow(("category", DataValue.FromString("B")), ("value", DataValue.FromFloat32(3f))),
        ];

        TableCatalog catalog = CreateCatalog(("data", data));

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

    /// <summary>
    /// In-memory table provider implementing the full <see cref="ITableProvider"/> contract.
    /// </summary>
    private sealed class InMemoryTableProvider : ITableProvider
    {
        private readonly Row[] _rows;

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
}
