using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

public class OperatorTests : ServiceTestBase
{
    private static readonly string[] AgeColumns = ["age"];
    private static readonly string[] XColumns = ["x"];
    private static readonly string[] ValColumns = ["val"];
    private static readonly string[] XyColumns = ["x", "y"];
    private static readonly string[] GroupValColumns = ["group", "val"];

    private async Task<List<Row>> CollectAsync(IQueryOperator op, ExecutionContext? context = null)
    {
        context ??= CreateExecutionContext();
        return await op.CollectRowsAsync(context);
    }

    // ─────────────── FilterOperator tests ───────────────

    [Fact]
    public async Task Filter_PassesMatchingRows()
    {
        MockOperator source = CreateMockOperator(AgeColumns, [25f], [15f], [30f]);

        FilterOperator filter = new(source,
            new BinaryExpression(
                new ColumnReference("age"),
                BinaryOperator.GreaterThanOrEqual,
                new LiteralExpression(18)));

        List<Row> rows = await CollectAsync(filter);

        Assert.Equal(2, rows.Count);
        Assert.Equal(25f, rows[0]["age"].AsFloat32());
        Assert.Equal(30f, rows[1]["age"].AsFloat32());
    }

    [Fact]
    public async Task Filter_RemovesAllRows()
    {
        MockOperator source = CreateMockOperator(XColumns, [1f], [2f]);

        FilterOperator filter = new(source,
            new BinaryExpression(
                new ColumnReference("x"),
                BinaryOperator.GreaterThan,
                new LiteralExpression(100)));

        List<Row> rows = await CollectAsync(filter);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task Filter_WithAnd()
    {
        MockOperator source = CreateMockOperator(XyColumns, [5f, 10f], [15f, 10f], [5f, 20f]);

        FilterOperator filter = new(source,
            new BinaryExpression(
                new BinaryExpression(
                    new ColumnReference("x"),
                    BinaryOperator.LessThan,
                    new LiteralExpression(10)),
                BinaryOperator.And,
                new BinaryExpression(
                    new ColumnReference("y"),
                    BinaryOperator.Equal,
                    new LiteralExpression(10))));

        List<Row> rows = await CollectAsync(filter);
        Assert.Single(rows);
        Assert.Equal(5f, rows[0]["x"].AsFloat32());
    }

    // ─────────────── ProjectOperator tests ───────────────

    [Fact]
    public async Task Project_SelectStar()
    {
        MockOperator source = CreateMockOperator(["a", "b"], [1f, "x"]);

        ProjectOperator project = new(source, [new SelectAllColumns()]);

        List<Row> rows = await CollectAsync(project);
        Assert.Single(rows);
        Assert.Equal(2, rows[0].FieldCount);
        Assert.Equal(1f, rows[0]["a"].AsFloat32());
        Assert.Equal("x", rows[0]["b"].AsString());
    }

    [Fact]
    public async Task Project_SelectStarExcept_ExcludesNamedColumns()
    {
        MockOperator source = CreateMockOperator(["a", "b", "c"], [1f, "x", 3f]);

        ProjectOperator project = new(source, [new SelectAllColumns(ExcludedColumns: ["b"])]);

        List<Row> rows = await CollectAsync(project);
        Assert.Single(rows);
        Assert.Equal(2, rows[0].FieldCount);
        Assert.Equal(1f, rows[0]["a"].AsFloat32());
        Assert.Equal(3f, rows[0]["c"].AsFloat32());
    }

    [Fact]
    public async Task Project_SelectStarExcept_MultipleExclusions()
    {
        MockOperator source = CreateMockOperator(["a", "b", "c", "d"], [1f, 2f, 3f, 4f]);

        ProjectOperator project = new(source, [new SelectAllColumns(ExcludedColumns: ["a", "c"])]);

        List<Row> rows = await CollectAsync(project);
        Assert.Single(rows);
        Assert.Equal(2, rows[0].FieldCount);
        Assert.Equal(2f, rows[0]["b"].AsFloat32());
        Assert.Equal(4f, rows[0]["d"].AsFloat32());
    }

    [Fact]
    public async Task Project_SelectTableStarExcept_ExcludesNamedColumns()
    {
        MockOperator source = CreateMockOperator(["t.a", "t.b", "t.c"], [1f, "x", 3f]);

        ProjectOperator project = new(source, [new SelectTableColumns("t", ExcludedColumns: ["b"])]);

        List<Row> rows = await CollectAsync(project);
        Assert.Single(rows);
        Assert.Equal(2, rows[0].FieldCount);
        Assert.Equal(1f, rows[0]["a"].AsFloat32());
        Assert.Equal(3f, rows[0]["c"].AsFloat32());
    }

    // ─────────────── SELECT * REPLACE tests ───────────────

    [Fact]
    public async Task Project_SelectStarReplace_ReplacesColumnValue()
    {
        MockOperator source = CreateMockOperator(["a", "b", "c"], [10f, 20f, 30f]);

        ProjectOperator project = new(source,
            [new SelectAllColumns(
                ReplacedColumns: [new ColumnReplacement(
                    new BinaryExpression(
                        new ColumnReference("b"),
                        BinaryOperator.Multiply,
                        new LiteralExpression(2)),
                    "b")])]);

        List<Row> rows = await CollectAsync(project);
        Assert.Single(rows);
        Assert.Equal(3, rows[0].FieldCount);
        Assert.Equal(10f, rows[0]["a"].AsFloat32());
        Assert.Equal(40f, rows[0]["b"].AsFloat32());
        Assert.Equal(30f, rows[0]["c"].AsFloat32());
    }

    [Fact]
    public async Task Project_SelectStarReplace_MultipleReplacements()
    {
        MockOperator source = CreateMockOperator(["a", "b", "c"], [5f, 10f, 15f]);

        ProjectOperator project = new(source,
            [new SelectAllColumns(
                ReplacedColumns: [
                    new ColumnReplacement(
                        new BinaryExpression(
                            new ColumnReference("a"),
                            BinaryOperator.Add,
                            new LiteralExpression(1)),
                        "a"),
                    new ColumnReplacement(
                        new BinaryExpression(
                            new ColumnReference("c"),
                            BinaryOperator.Multiply,
                            new LiteralExpression(0)),
                        "c")])]);

        List<Row> rows = await CollectAsync(project);
        Assert.Single(rows);
        Assert.Equal(3, rows[0].FieldCount);
        Assert.Equal(6f, rows[0]["a"].AsFloat32());
        Assert.Equal(10f, rows[0]["b"].AsFloat32());
        Assert.Equal(0f, rows[0]["c"].AsFloat32());
    }

    [Fact]
    public async Task Project_SelectStarExceptAndReplace_Combined()
    {
        MockOperator source = CreateMockOperator(["id", "price", "name"], [1f, 500f, "widget"]);

        ProjectOperator project = new(source,
            [new SelectAllColumns(
                ExcludedColumns: ["id"],
                ReplacedColumns: [new ColumnReplacement(
                    new BinaryExpression(
                        new ColumnReference("price"),
                        BinaryOperator.Divide,
                        new LiteralExpression(100)),
                    "price")])]);

        List<Row> rows = await CollectAsync(project);
        Assert.Single(rows);
        Assert.Equal(2, rows[0].FieldCount);
        Assert.Equal(5f, rows[0]["price"].AsFloat32());
        Assert.Equal("widget", rows[0]["name"].AsString());
    }

    [Fact]
    public async Task Project_SelectTableStarReplace_ReplacesColumnValue()
    {
        MockOperator source = CreateMockOperator(["t.x", "t.y"], [100f, 200f]);

        ProjectOperator project = new(source,
            [new SelectTableColumns("t",
                ReplacedColumns: [new ColumnReplacement(
                    new BinaryExpression(
                        new ColumnReference("t", "y"),
                        BinaryOperator.Add,
                        new LiteralExpression(50)),
                    "y")])]);

        List<Row> rows = await CollectAsync(project);
        Assert.Single(rows);
        Assert.Equal(2, rows[0].FieldCount);
        Assert.Equal(100f, rows[0]["x"].AsFloat32());
        Assert.Equal(250f, rows[0]["y"].AsFloat32());
    }

    [Fact]
    public async Task Project_NamedColumns()
    {
        MockOperator source = CreateMockOperator(["a", "b"], [1f, 2f]);

        ProjectOperator project = new(source,
        [
            new SelectColumn(new ColumnReference("a")),
            new SelectColumn(
                new BinaryExpression(
                    new ColumnReference("a"),
                    BinaryOperator.Add,
                    new ColumnReference("b")),
                "sum")
        ]);

        List<Row> rows = await CollectAsync(project);
        Assert.Single(rows);
        Assert.Equal(2, rows[0].FieldCount);
        Assert.Equal(1f, rows[0]["a"].AsFloat32());
        Assert.Equal(3f, rows[0]["sum"].AsFloat32());
    }

    [Fact]
    public async Task Project_WithAlias()
    {
        MockOperator source = CreateMockOperator(XColumns, [42f]);

        ProjectOperator project = new(source,
        [
            new SelectColumn(new ColumnReference("x"), "answer")
        ]);

        List<Row> rows = await CollectAsync(project);
        Assert.Equal(42f, rows[0]["answer"].AsFloat32());
    }

    [Fact]
    public async Task Project_FunctionCall()
    {
        MockOperator source = CreateMockOperator(["name"], ["hello"]);

        ProjectOperator project = new(source,
        [
            new SelectColumn(
                new FunctionCallExpression("len", [new ColumnReference("name")]),
                "name_len")
        ]);

        List<Row> rows = await CollectAsync(project);
        Assert.Equal(5, rows[0]["name_len"].AsInt32());
    }

    [Fact]
    public async Task Project_DuplicateFunctionNames_Deduplicated()
    {
        MockOperator source = CreateMockOperator(["name"], ["hello"]);

        ProjectOperator project = new(source,
        [
            new SelectColumn(
                new FunctionCallExpression("len", [new ColumnReference("name")])),
            new SelectColumn(
                new FunctionCallExpression("len", [new ColumnReference("name")]))
        ]);

        List<Row> rows = await CollectAsync(project);
        Assert.Equal(2, rows[0].FieldCount);
        Assert.Equal("len_1", rows[0].ColumnNames[0]);
        Assert.Equal("len_2", rows[0].ColumnNames[1]);
    }

    [Fact]
    public async Task Project_UnaliasedFunctionName_UsedAsColumnName()
    {
        MockOperator source = CreateMockOperator(["name"], ["hello"]);

        ProjectOperator project = new(source,
        [
            new SelectColumn(
                new FunctionCallExpression("len", [new ColumnReference("name")]))
        ]);

        List<Row> rows = await CollectAsync(project);
        Assert.Equal("len", rows[0].ColumnNames[0]);
    }

    [Fact]
    public async Task Project_UnaliasedExpression_NamedExpression()
    {
        MockOperator source = CreateMockOperator(["a", "b"], [1f, 2f]);

        ProjectOperator project = new(source,
        [
            new SelectColumn(
                new BinaryExpression(
                    new ColumnReference("a"),
                    BinaryOperator.Add,
                    new ColumnReference("b")))
        ]);

        List<Row> rows = await CollectAsync(project);
        Assert.Equal("expression", rows[0].ColumnNames[0]);
    }

    // ─────────────── JoinOperator tests ───────────────

    [Fact]
    public async Task InnerJoin_MatchingRows()
    {
        MockOperator left = CreateMockOperator(
            ["id", "name"],
            [1f, "Alice"],
            [2f, "Bob"],
            [3f, "Charlie"]);

        MockOperator right = CreateMockOperator(
            ["id", "score"],
            [1f, 95f],
            [3f, 87f]);

        JoinOperator join = new(left, right, JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("id"),
                BinaryOperator.Equal,
                new ColumnReference("id")));

        List<Row> rows = await CollectAsync(join);

        // Inner join on non-unique keys with nested loop (non-equi detected as same column name).
        // The condition references "id" on both sides, but without table qualifiers
        // it will match the first "id" in the combined row.
        // This actually works via nested-loop since both sides have "id".
        Assert.True(rows.Count >= 2);
    }

    [Fact]
    public async Task InnerJoin_HashJoin_QualifiedKeys()
    {
        MockOperator left = CreateMockOperator(
            ["l.id", "l.name"],
            [1f, "Alice"],
            [2f, "Bob"]);

        MockOperator right = CreateMockOperator(
            ["r.id", "r.score"],
            [1f, 95f],
            [3f, 87f]);

        JoinOperator join = new(left, right, JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("l", "id"),
                BinaryOperator.Equal,
                new ColumnReference("r", "id")));

        List<Row> rows = await CollectAsync(join);

        Assert.Single(rows);
        Assert.Equal("Alice", rows[0]["l.name"].AsString());
        Assert.Equal(95f, rows[0]["r.score"].AsFloat32());
    }

    [Fact]
    public async Task LeftJoin_IncludesUnmatchedLeft()
    {
        MockOperator left = CreateMockOperator(
            ["l.id", "l.name"],
            [1f, "Alice"],
            [2f, "Bob"]);

        MockOperator right = CreateMockOperator(
            ["r.id", "r.score"],
            [1f, 95f]);

        JoinOperator join = new(left, right, JoinType.Left,
            new BinaryExpression(
                new ColumnReference("l", "id"),
                BinaryOperator.Equal,
                new ColumnReference("r", "id")));

        List<Row> rows = await CollectAsync(join);

        Assert.Equal(2, rows.Count);
        // First row: matched
        Assert.Equal("Alice", rows[0]["l.name"].AsString());
        Assert.Equal(95f, rows[0]["r.score"].AsFloat32());
        // Second row: unmatched (right side is null)
        Assert.Equal("Bob", rows[1]["l.name"].AsString());
        Assert.True(rows[1]["r.score"].IsNull);
    }

    [Fact]
    public async Task CrossJoin_CartesianProduct()
    {
        MockOperator left = CreateMockOperator(["a"], [1f], [2f]);

        MockOperator right = CreateMockOperator(["b"], ["x"], ["y"], ["z"]);

        JoinOperator join = new(left, right, JoinType.Cross, onCondition: null);

        List<Row> rows = await CollectAsync(join);
        Assert.Equal(6, rows.Count); // 2 * 3
    }

    [Fact]
    public async Task CrossJoin_PreservesColumnValues()
    {
        MockOperator left = CreateMockOperator(["a"], [10f]);

        MockOperator right = CreateMockOperator(["b"], [20f]);

        JoinOperator join = new(left, right, JoinType.Cross, onCondition: null);

        List<Row> rows = await CollectAsync(join);
        Assert.Single(rows);
        Assert.Equal(10f, rows[0]["a"].AsFloat32());
        Assert.Equal(20f, rows[0]["b"].AsFloat32());
    }

    [Fact]
    public async Task NullKeys_NeverMatch()
    {
        MockOperator left = CreateMockOperator(
            ["l.id", "l.name"],
            [DataValue.Null(DataKind.Float32), "Ghost"]);

        MockOperator right = CreateMockOperator(
            ["r.id", "r.score"],
            [DataValue.Null(DataKind.Float32), 0f]);

        JoinOperator join = new(left, right, JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("l", "id"),
                BinaryOperator.Equal,
                new ColumnReference("r", "id")));

        List<Row> rows = await CollectAsync(join);
        Assert.Empty(rows);
    }

    // ─────────────── OrderByOperator tests ───────────────

    [Fact]
    public async Task OrderBy_Ascending()
    {
        MockOperator source = CreateMockOperator(ValColumns, [3f], [1f], [2f]);

        OrderByOperator orderBy = new(source,
        [
            new OrderByItem(new ColumnReference("val"), SortDirection.Ascending)
        ]);

        List<Row> rows = await CollectAsync(orderBy);
        Assert.Equal(1f, rows[0]["val"].AsFloat32());
        Assert.Equal(2f, rows[1]["val"].AsFloat32());
        Assert.Equal(3f, rows[2]["val"].AsFloat32());
    }

    [Fact]
    public async Task OrderBy_Descending()
    {
        MockOperator source = CreateMockOperator(ValColumns, [1f], [3f], [2f]);

        OrderByOperator orderBy = new(source,
        [
            new OrderByItem(new ColumnReference("val"), SortDirection.Descending)
        ]);

        List<Row> rows = await CollectAsync(orderBy);
        Assert.Equal(3f, rows[0]["val"].AsFloat32());
        Assert.Equal(2f, rows[1]["val"].AsFloat32());
        Assert.Equal(1f, rows[2]["val"].AsFloat32());
    }

    [Fact]
    public async Task OrderBy_MultiKey()
    {
        MockOperator source = CreateMockOperator(GroupValColumns,
            ["B", 2f],
            ["A", 3f],
            ["A", 1f]);

        OrderByOperator orderBy = new(source,
        [
            new OrderByItem(new ColumnReference("group"), SortDirection.Ascending),
            new OrderByItem(new ColumnReference("val"), SortDirection.Ascending)
        ]);

        List<Row> rows = await CollectAsync(orderBy);
        Assert.Equal("A", rows[0]["group"].AsString());
        Assert.Equal(1f, rows[0]["val"].AsFloat32());
        Assert.Equal("A", rows[1]["group"].AsString());
        Assert.Equal(3f, rows[1]["val"].AsFloat32());
        Assert.Equal("B", rows[2]["group"].AsString());
    }

    [Fact]
    public async Task OrderBy_NullsLast()
    {
        MockOperator source = CreateMockOperator(ValColumns,
            [DataValue.Null(DataKind.Float32)],
            [1f],
            [2f]);

        OrderByOperator orderBy = new(source,
        [
            new OrderByItem(new ColumnReference("val"), SortDirection.Ascending)
        ]);

        List<Row> rows = await CollectAsync(orderBy);
        Assert.Equal(1f, rows[0]["val"].AsFloat32());
        Assert.Equal(2f, rows[1]["val"].AsFloat32());
        Assert.True(rows[2]["val"].IsNull);
    }

    // ─────────────── LimitOperator tests ───────────────

    [Fact]
    public async Task Limit_TakesSpecifiedCount()
    {
        MockOperator source = CreateMockOperator(XColumns, [1f], [2f], [3f], [4f], [5f]);

        LimitOperator limit = new(source, 3);
        List<Row> rows = await CollectAsync(limit);

        Assert.Equal(3, rows.Count);
        Assert.Equal(1f, rows[0]["x"].AsFloat32());
        Assert.Equal(3f, rows[2]["x"].AsFloat32());
    }

    [Fact]
    public async Task Limit_WithOffset()
    {
        MockOperator source = CreateMockOperator(XColumns, [1f], [2f], [3f], [4f], [5f]);

        LimitOperator limit = new(source, 2, offset: 2);
        List<Row> rows = await CollectAsync(limit);

        Assert.Equal(2, rows.Count);
        Assert.Equal(3f, rows[0]["x"].AsFloat32());
        Assert.Equal(4f, rows[1]["x"].AsFloat32());
    }

    [Fact]
    public async Task Limit_FewerRowsThanLimit()
    {
        MockOperator source = CreateMockOperator(XColumns, [1f]);

        LimitOperator limit = new(source, 10);
        List<Row> rows = await CollectAsync(limit);

        Assert.Single(rows);
    }

    [Fact]
    public async Task Limit_ZeroReturnsNothing()
    {
        MockOperator source = CreateMockOperator(XColumns, [1f]);

        LimitOperator limit = new(source, 0);
        List<Row> rows = await CollectAsync(limit);

        Assert.Empty(rows);
    }

    // ─────────────── OrderBy + Limit combined ───────────────

    [Fact]
    public async Task OrderByThenLimit_TopN()
    {
        MockOperator source = CreateMockOperator(ValColumns, [5f], [1f], [3f], [2f], [4f]);

        OrderByOperator orderBy = new(source,
        [
            new OrderByItem(new ColumnReference("val"), SortDirection.Ascending)
        ]);

        LimitOperator limit = new(orderBy, 3);
        List<Row> rows = await CollectAsync(limit);

        Assert.Equal(3, rows.Count);
        Assert.Equal(1f, rows[0]["val"].AsFloat32());
        Assert.Equal(2f, rows[1]["val"].AsFloat32());
        Assert.Equal(3f, rows[2]["val"].AsFloat32());
    }

    [Fact]
    public async Task OrderBy_BoundedTopN_OnlyKeepsNRows()
    {
        MockOperator source = CreateMockOperator(ValColumns, [5f], [1f], [3f], [2f], [4f]);

        OrderByOperator orderBy = new(source,
        [
            new OrderByItem(new ColumnReference("val"), SortDirection.Ascending)
        ], topNRows: 3);

        List<Row> rows = await CollectAsync(orderBy);

        Assert.Equal(3, rows.Count);
        Assert.Equal(1f, rows[0]["val"].AsFloat32());
        Assert.Equal(2f, rows[1]["val"].AsFloat32());
        Assert.Equal(3f, rows[2]["val"].AsFloat32());
    }

    [Fact]
    public async Task OrderBy_BoundedTopN_Descending()
    {
        MockOperator source = CreateMockOperator(ValColumns, [5f], [1f], [3f], [2f], [4f]);

        OrderByOperator orderBy = new(source,
        [
            new OrderByItem(new ColumnReference("val"), SortDirection.Descending)
        ], topNRows: 2);

        List<Row> rows = await CollectAsync(orderBy);

        Assert.Equal(2, rows.Count);
        Assert.Equal(5f, rows[0]["val"].AsFloat32());
        Assert.Equal(4f, rows[1]["val"].AsFloat32());
    }

    [Fact]
    public async Task OrderBy_BoundedTopN_WithOffset()
    {
        // topNRows = limit + offset = 2 + 1 = 3, then LimitOperator skips 1.
        MockOperator source = CreateMockOperator(ValColumns, [5f], [1f], [3f], [2f], [4f]);

        OrderByOperator orderBy = new(source,
        [
            new OrderByItem(new ColumnReference("val"), SortDirection.Ascending)
        ], topNRows: 3);

        LimitOperator limit = new(orderBy, 2, offset: 1);
        List<Row> rows = await CollectAsync(limit);

        Assert.Equal(2, rows.Count);
        Assert.Equal(2f, rows[0]["val"].AsFloat32());
        Assert.Equal(3f, rows[1]["val"].AsFloat32());
    }

    [Fact]
    public async Task OrderBy_BoundedTopN_MultiKey()
    {
        MockOperator source = CreateMockOperator(GroupValColumns,
            ["B", 2f],
            ["A", 3f],
            ["A", 1f],
            ["C", 0f]);

        OrderByOperator orderBy = new(source,
        [
            new OrderByItem(new ColumnReference("group"), SortDirection.Ascending),
            new OrderByItem(new ColumnReference("val"), SortDirection.Ascending)
        ], topNRows: 2);

        List<Row> rows = await CollectAsync(orderBy);

        Assert.Equal(2, rows.Count);
        Assert.Equal("A", rows[0]["group"].AsString());
        Assert.Equal(1f, rows[0]["val"].AsFloat32());
        Assert.Equal("A", rows[1]["group"].AsString());
        Assert.Equal(3f, rows[1]["val"].AsFloat32());
    }

    [Fact]
    public async Task OrderBy_BoundedTopN_FewerRowsThanN()
    {
        MockOperator source = CreateMockOperator(ValColumns, [2f], [1f]);

        OrderByOperator orderBy = new(source,
        [
            new OrderByItem(new ColumnReference("val"), SortDirection.Ascending)
        ], topNRows: 10);

        List<Row> rows = await CollectAsync(orderBy);

        Assert.Equal(2, rows.Count);
        Assert.Equal(1f, rows[0]["val"].AsFloat32());
        Assert.Equal(2f, rows[1]["val"].AsFloat32());
    }

    [Fact]
    public async Task OrderBy_BoundedTopN_NullsLast()
    {
        MockOperator source = CreateMockOperator(ValColumns,
            [DataValue.Null(DataKind.Float32)],
            [3f],
            [1f],
            [2f]);

        OrderByOperator orderBy = new(source,
        [
            new OrderByItem(new ColumnReference("val"), SortDirection.Ascending)
        ], topNRows: 2);

        List<Row> rows = await CollectAsync(orderBy);

        Assert.Equal(2, rows.Count);
        Assert.Equal(1f, rows[0]["val"].AsFloat32());
        Assert.Equal(2f, rows[1]["val"].AsFloat32());
    }

    // ─────────────── OrderByOperator governor enforcement ───────────────

    /// <summary>
    /// When the Query Unit budget is already exceeded, the ORDER BY operator
    /// throws <see cref="QueryBudgetExceededException"/> during unbounded
    /// sort materialization instead of consuming the entire input.
    /// </summary>
    [Fact]
    public async Task OrderBy_BudgetExceeded_ThrowsDuringMaterialization()
    {
        MockOperator source = CreateMockOperator(ValColumns, [3f], [1f], [2f]);

        OrderByOperator orderBy = new(source,
        [
            new OrderByItem(new ColumnReference("val"), SortDirection.Ascending)
        ]);

        // Pre-exceed the budget so the check fires on the first materialized row.
        QueryMeter meter = new(budget: 5);
        meter.Add(6);

        ExecutionContext context = CreateExecutionContext(meter: meter);

        await Assert.ThrowsAsync<QueryBudgetExceededException>(
            () => CollectAsync(orderBy, context));
    }

    /// <summary>
    /// When the Query Unit budget is already exceeded, the ORDER BY operator
    /// throws <see cref="QueryBudgetExceededException"/> during top-N collection
    /// instead of consuming the entire input.
    /// </summary>
    [Fact]
    public async Task OrderBy_BoundedTopN_BudgetExceeded_ThrowsDuringMaterialization()
    {
        MockOperator source = CreateMockOperator(ValColumns, [3f], [1f], [2f]);

        OrderByOperator orderBy = new(source,
        [
            new OrderByItem(new ColumnReference("val"), SortDirection.Ascending)
        ], topNRows: 2);

        // Pre-exceed the budget so the check fires on the first materialized row.
        QueryMeter meter = new(budget: 5);
        meter.Add(6);

        ExecutionContext context = CreateExecutionContext(meter: meter);

        await Assert.ThrowsAsync<QueryBudgetExceededException>(
            () => CollectAsync(orderBy, context));
    }

    /// <summary>
    /// When the cancellation token is cancelled, the ORDER BY operator
    /// throws <see cref="OperationCanceledException"/> during materialization.
    /// </summary>
    [Fact]
    public async Task OrderBy_CancellationToken_ThrowsDuringMaterialization()
    {
        MockOperator source = CreateMockOperator(ValColumns, [1f], [2f]);

        OrderByOperator orderBy = new(source,
        [
            new OrderByItem(new ColumnReference("val"), SortDirection.Ascending)
        ]);

        CancellationTokenSource cancellationTokenSource = new();
        cancellationTokenSource.Cancel();

        ExecutionContext context = CreateExecutionContext(cancellationToken: cancellationTokenSource.Token);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => CollectAsync(orderBy, context));
    }

    // ─────────────── AliasOperator tests ───────────────

    [Fact]
    public async Task Alias_PrefixesColumnNames()
    {
        MockOperator source = CreateMockOperator(["id", "name"], [1f, "test"]);

        AliasOperator alias = new(source, "t");
        List<Row> rows = await CollectAsync(alias);

        Assert.Single(rows);
        // FieldCount matches the source — columns are not doubled.
        Assert.Equal(2, rows[0].FieldCount);
        // ColumnNames are qualified only.
        Assert.Equal(["t.id", "t.name"], rows[0].ColumnNames);
        Assert.Equal(1f, rows[0]["t.id"].AsFloat32());
        Assert.Equal("test", rows[0]["t.name"].AsString());
        // Also accessible without prefix via the lookup index.
        Assert.Equal(1f, rows[0]["id"].AsFloat32());
    }

    /// <summary>
    /// SELECT * on a JOIN of aliased tables should emit only the qualified
    /// (alias-prefixed) column names. <see cref="AliasOperator"/> exposes
    /// unqualified names only via the lookup index, not as physical columns.
    /// </summary>
    [Fact]
    public async Task Project_SelectStar_JoinedAliases_OnlyQualifiedColumns()
    {
        // AliasOperator produces only qualified column names (l.id, l.name)
        // while keeping unqualified names in the lookup index.
        // JoinOperator concatenates both sides: l.id, l.name, r.id, r.score.
        MockOperator left = CreateMockOperator(["id", "name"], [1f, "Alice"]);
        MockOperator right = CreateMockOperator(["id", "score"], [1f, 95f]);

        AliasOperator aliasLeft = new(left, "l");
        AliasOperator aliasRight = new(right, "r");

        JoinOperator join = new(aliasLeft, aliasRight, JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("l", "id"),
                BinaryOperator.Equal,
                new ColumnReference("r", "id")));

        ProjectOperator project = new(join, [new SelectAllColumns()]);

        List<Row> rows = await CollectAsync(project);

        Assert.Single(rows);

        // Should contain only qualified names: l.id, l.name, r.id, r.score.
        string[] expectedColumns = ["l.id", "l.name", "r.id", "r.score"];
        Assert.Equal(expectedColumns.Length, rows[0].FieldCount);
        Assert.Equal(expectedColumns, rows[0].ColumnNames);

        Assert.Equal(1f, rows[0]["l.id"].AsFloat32());
        Assert.Equal("Alice", rows[0]["l.name"].AsString());
        Assert.Equal(95f, rows[0]["r.score"].AsFloat32());
    }

    /// <summary>
    /// SELECT * on a single non-aliased table should still emit all columns
    /// without any filtering.
    /// </summary>
    [Fact]
    public async Task Project_SelectStar_NoAliases_EmitsAllColumns()
    {
        MockOperator source = CreateMockOperator(["id", "name"], [1f, "Alice"]);

        ProjectOperator project = new(source, [new SelectAllColumns()]);

        List<Row> rows = await CollectAsync(project);

        Assert.Single(rows);
        Assert.Equal(2, rows[0].FieldCount);
        Assert.Equal(["id", "name"], rows[0].ColumnNames);
    }

    // ─────────────── Expression-based hash join tests ───────────────

    [Fact]
    public async Task HashJoin_FunctionExpression_MatchesCorrectly()
    {
        // Simulates GET_FILENAME(z.file_name) = i.file_name
        // Left side has full paths, right side has just filenames.
        MockOperator left = CreateMockOperator(
            ["l.file_name", "l.data"],
            ["images/cat.jpg", 1f],
            ["images/dog.png", 2f],
            ["images/bird.jpg", 3f]);

        MockOperator right = CreateMockOperator(
            ["r.file_name", "r.score"],
            ["cat.jpg", 95f],
            ["bird.jpg", 80f]);

        // ON GET_FILENAME(l.file_name) = r.file_name
        JoinOperator join = new(left, right, JoinType.Inner,
            new BinaryExpression(
                new FunctionCallExpression("GET_FILENAME", [new ColumnReference("l", "file_name")]),
                BinaryOperator.Equal,
                new ColumnReference("r", "file_name")));

        List<Row> rows = await CollectAsync(join);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r["l.data"].AsFloat32() == 1f && r["r.score"].AsFloat32() == 95f);
        Assert.Contains(rows, r => r["l.data"].AsFloat32() == 3f && r["r.score"].AsFloat32() == 80f);
    }

    [Fact]
    public async Task HashJoin_CompoundKeys_MatchesOnBothKeys()
    {
        MockOperator left = CreateMockOperator(
            ["l.a", "l.b", "l.val"],
            [1f, "x", 10f],
            [1f, "y", 20f],
            [2f, "x", 30f]);

        MockOperator right = CreateMockOperator(
            ["r.a", "r.b", "r.info"],
            [1f, "x", "match1"],
            [2f, "y", "no_match"]);

        // ON l.a = r.a AND l.b = r.b
        JoinOperator join = new(left, right, JoinType.Inner,
            new BinaryExpression(
                new BinaryExpression(
                    new ColumnReference("l", "a"),
                    BinaryOperator.Equal,
                    new ColumnReference("r", "a")),
                BinaryOperator.And,
                new BinaryExpression(
                    new ColumnReference("l", "b"),
                    BinaryOperator.Equal,
                    new ColumnReference("r", "b"))));

        List<Row> rows = await CollectAsync(join);

        Assert.Single(rows);
        Assert.Equal(10f, rows[0]["l.val"].AsFloat32());
        Assert.Equal("match1", rows[0]["r.info"].AsString());
    }

    [Fact]
    public async Task HashJoin_ResidualFilter_AppliedAfterHashMatch()
    {
        MockOperator left = CreateMockOperator(
            ["l.id", "l.val"],
            [1f, 100f],
            [2f, 200f]);

        MockOperator right = CreateMockOperator(
            ["r.id", "r.threshold"],
            [1f, 150f],
            [2f, 150f]);

        // ON l.id = r.id AND l.val > r.threshold
        // The equality is extracted as hash key; the > becomes residual.
        JoinOperator join = new(left, right, JoinType.Inner,
            new BinaryExpression(
                new BinaryExpression(
                    new ColumnReference("l", "id"),
                    BinaryOperator.Equal,
                    new ColumnReference("r", "id")),
                BinaryOperator.And,
                new BinaryExpression(
                    new ColumnReference("l", "val"),
                    BinaryOperator.GreaterThan,
                    new ColumnReference("r", "threshold"))));

        List<Row> rows = await CollectAsync(join);

        // Only l.id=2 (val=200) passes the residual l.val > r.threshold (150).
        Assert.Single(rows);
        Assert.Equal(2f, rows[0]["l.id"].AsFloat32());
    }

    [Fact]
    public async Task HashJoin_NullExpressionKey_NeverMatches()
    {
        MockOperator left = CreateMockOperator(
            ["l.path"],
            [DataValue.Null(DataKind.String)]);

        MockOperator right = CreateMockOperator(
            ["r.name"],
            [DataValue.Null(DataKind.String)]);

        // ON GET_FILENAME(l.path) = r.name — both null
        JoinOperator join = new(left, right, JoinType.Inner,
            new BinaryExpression(
                new FunctionCallExpression("GET_FILENAME", [new ColumnReference("l", "path")]),
                BinaryOperator.Equal,
                new ColumnReference("r", "name")));

        List<Row> rows = await CollectAsync(join);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task HashJoin_DuplicateKeys_ProducesAllCombinations()
    {
        MockOperator left = CreateMockOperator(
            ["l.id", "l.tag"],
            [1f, "A"],
            [1f, "B"]);

        MockOperator right = CreateMockOperator(
            ["r.id", "r.info"],
            [1f, "X"],
            [1f, "Y"]);

        JoinOperator join = new(left, right, JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("l", "id"),
                BinaryOperator.Equal,
                new ColumnReference("r", "id")));

        List<Row> rows = await CollectAsync(join);

        // 2 left × 2 right = 4 combinations
        Assert.Equal(4, rows.Count);
    }

    [Fact]
    public async Task LeftJoin_ExpressionKey_IncludesUnmatchedRows()
    {
        MockOperator left = CreateMockOperator(
            ["l.path", "l.size"],
            ["a/file1.txt", 100f],
            ["b/file2.txt", 200f]);

        MockOperator right = CreateMockOperator(
            ["r.name", "r.label"],
            ["file1.txt", "doc"]);

        // ON GET_FILENAME(l.path) = r.name
        JoinOperator join = new(left, right, JoinType.Left,
            new BinaryExpression(
                new FunctionCallExpression("GET_FILENAME", [new ColumnReference("l", "path")]),
                BinaryOperator.Equal,
                new ColumnReference("r", "name")));

        List<Row> rows = await CollectAsync(join);

        Assert.Equal(2, rows.Count);
        // Matched row
        Assert.Equal("doc", rows[0]["r.label"].AsString());
        // Unmatched row — right side is null
        Assert.True(rows[1]["r.label"].IsNull);
    }

    // ─────────────── SubqueryOperator tests ───────────────

    [Fact]
    public async Task Subquery_PassesThroughRows()
    {
        MockOperator inner = CreateMockOperator(XColumns, [42f]);

        SubqueryOperator subquery = new(inner, "sub");
        List<Row> rows = await CollectAsync(subquery);

        Assert.Single(rows);
        Assert.Equal(42f, rows[0]["x"].AsFloat32());
    }

    // ─────────────── Hash join: composite-key residual ───────────────

    /// <summary>
    /// Composite-key hash join with a residual non-equi conjunct. Two equi-conjuncts
    /// (l.a = r.a AND l.b = r.b) form the composite hash key; the trailing
    /// l.weight &gt; r.threshold becomes the residual filter applied after key match.
    /// Hits the composite-key path of <c>extraction.Residual is not null</c>, which
    /// the existing single-key residual test does not exercise.
    /// </summary>
    [Fact]
    public async Task HashJoin_CompositeKey_ResidualFilter_AppliedAfterMatch()
    {
        MockOperator left = CreateMockOperator(
            ["l.a", "l.b", "l.weight"],
            [1f, "x", 100f],
            [1f, "x", 50f],
            [2f, "y", 200f]);

        MockOperator right = CreateMockOperator(
            ["r.a", "r.b", "r.threshold"],
            [1f, "x", 75f],
            [2f, "y", 150f]);

        // ON (l.a = r.a) AND (l.b = r.b) AND (l.weight > r.threshold)
        Expression equiA = new BinaryExpression(
            new ColumnReference("l", "a"), BinaryOperator.Equal, new ColumnReference("r", "a"));
        Expression equiB = new BinaryExpression(
            new ColumnReference("l", "b"), BinaryOperator.Equal, new ColumnReference("r", "b"));
        Expression residual = new BinaryExpression(
            new ColumnReference("l", "weight"), BinaryOperator.GreaterThan, new ColumnReference("r", "threshold"));
        Expression onCondition = new BinaryExpression(
            new BinaryExpression(equiA, BinaryOperator.And, equiB),
            BinaryOperator.And,
            residual);

        JoinOperator join = new(left, right, JoinType.Inner, onCondition);

        List<Row> rows = await CollectAsync(join);

        // (1, "x", 100) vs (1, "x", 75): 100 > 75 → match
        // (1, "x",  50) vs (1, "x", 75):  50 > 75 → fails residual
        // (2, "y", 200) vs (2, "y",150): 200 > 150 → match
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r["l.weight"].AsFloat32() == 100f);
        Assert.Contains(rows, r => r["l.weight"].AsFloat32() == 200f);
    }

    // ─────────────── Nested-loop join: no equi-keys path ───────────────

    /// <summary>
    /// Inner join with a non-equi-only ON condition (l.x &gt; r.threshold).
    /// <see cref="JoinKeyExtractor"/> cannot decompose this into equalities so
    /// <see cref="JoinOperator"/> falls through to <c>ExecuteNestedLoopJoinAsync</c>.
    /// Validates the cartesian-product loop + residual evaluation path that no
    /// existing test reaches.
    /// </summary>
    [Fact]
    public async Task NestedLoopJoin_NoEquiKeys_AppliesNonEquiCondition()
    {
        MockOperator left = CreateMockOperator(
            ["l.x"],
            [10f],
            [50f],
            [100f]);

        MockOperator right = CreateMockOperator(
            ["r.threshold"],
            [25f],
            [75f]);

        JoinOperator join = new(left, right, JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("l", "x"),
                BinaryOperator.GreaterThan,
                new ColumnReference("r", "threshold")));

        List<Row> rows = await CollectAsync(join);

        // 10  > 25 → no  | 10  > 75 → no
        // 50  > 25 → yes | 50  > 75 → no
        // 100 > 25 → yes | 100 > 75 → yes
        Assert.Equal(3, rows.Count);
    }

    // ─────────────── Hash join: NullSensitiveAntiSemi at operator level ───────────────

    /// <summary>
    /// LEFT ANTI-SEMI join with <c>nullSensitiveAntiSemi: true</c>: when any
    /// build-side key is NULL, the entire result is empty (NOT IN semantics —
    /// any unknown comparison makes the whole condition unknown).
    /// </summary>
    [Fact]
    public async Task HashJoin_LeftAntiSemi_NullSensitive_BuildHasNull_ReturnsEmpty()
    {
        MockOperator left = CreateMockOperator(
            ["x"],
            [1f],
            [2f],
            [3f]);

        MockOperator right = CreateMockOperator(
            ["y"],
            [2f],
            [DataValue.Null(DataKind.Float32)]);

        JoinOperator join = new(left, right,
            JoinType.LeftAntiSemi,
            new BinaryExpression(
                new ColumnReference("x"),
                BinaryOperator.Equal,
                new ColumnReference("y")),
            nullSensitiveAntiSemi: true);

        List<Row> rows = await CollectAsync(join);

        Assert.Empty(rows);
    }

    /// <summary>
    /// LEFT ANTI-SEMI join with <c>nullSensitiveAntiSemi: true</c>: probe rows
    /// with a NULL key are excluded (NULL NOT IN ... is UNKNOWN). Non-null probe
    /// rows that don't appear in the build set are emitted.
    /// </summary>
    [Fact]
    public async Task HashJoin_LeftAntiSemi_NullSensitive_ProbeHasNull_ExcludesNullProbeRows()
    {
        MockOperator left = CreateMockOperator(
            ["x"],
            [1f],
            [2f],
            [DataValue.Null(DataKind.Float32)]);

        MockOperator right = CreateMockOperator(
            ["y"],
            [2f]);

        JoinOperator join = new(left, right,
            JoinType.LeftAntiSemi,
            new BinaryExpression(
                new ColumnReference("x"),
                BinaryOperator.Equal,
                new ColumnReference("y")),
            nullSensitiveAntiSemi: true);

        List<Row> rows = await CollectAsync(join);

        // x=1     : no match in build → emitted
        // x=2     : matches build       → excluded
        // x=NULL  : excluded (NULL NOT IN is UNKNOWN)
        Assert.Single(rows);
        Assert.Equal(1f, rows[0]["x"].AsFloat32());
    }

    // ─────────────── RIGHT join with empty probe (GetFirstRowForNullPadAsync) ───────────────

    /// <summary>
    /// RIGHT JOIN with an empty left (probe) side. The build side has unmatched
    /// rows; <c>GetFirstRowForNullPadAsync</c> returns <c>null</c> because the
    /// probe is empty, exercising the "no probe row ever" fallback that emits
    /// each unmatched build row solo with the build-side schema.
    /// </summary>
    [Fact]
    public async Task RightJoin_EmptyLeft_EmitsAllRightRowsSolo()
    {
        MockOperator left = CreateMockOperator(["l.id"]);  // empty

        MockOperator right = CreateMockOperator(
            ["r.id", "r.val"],
            [10f, "a"],
            [20f, "b"]);

        JoinOperator join = new(left, right, JoinType.Right,
            new BinaryExpression(
                new ColumnReference("l", "id"),
                BinaryOperator.Equal,
                new ColumnReference("r", "id")));

        List<Row> rows = await CollectAsync(join);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r["r.id"].AsFloat32() == 10f && r["r.val"].AsString() == "a");
        Assert.Contains(rows, r => r["r.id"].AsFloat32() == 20f && r["r.val"].AsString() == "b");
    }
}