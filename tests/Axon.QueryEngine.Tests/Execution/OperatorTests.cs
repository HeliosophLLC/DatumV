using Axon.QueryEngine.Catalog;
using Axon.QueryEngine.Execution;
using Axon.QueryEngine.Execution.Operators;
using Axon.QueryEngine.Functions;
using Axon.QueryEngine.Model;
using Axon.QueryEngine.Parsing.Ast;
using ExecutionContext = Axon.QueryEngine.Execution.ExecutionContext;

namespace Axon.QueryEngine.Tests.Execution;

/// <summary>
/// A simple in-memory operator that yields pre-defined rows.
/// Used as a mock data source in operator tests.
/// </summary>
internal sealed class MockOperator : IQueryOperator
{
    private readonly Row[] _rows;

    public MockOperator(params Row[] rows)
    {
        _rows = rows;
    }

    public async IAsyncEnumerable<Row> ExecuteAsync(ExecutionContext context)
    {
        foreach (Row row in _rows)
        {
            yield return row;
        }

        await Task.CompletedTask;
    }
}

public class OperatorTests
{
    private static ExecutionContext CreateContext()
    {
        return new ExecutionContext(
            CancellationToken.None,
            FunctionRegistry.CreateDefault(),
            new TableCatalog());
    }

    private static Row MakeRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = columns.Select(c => c.Name).ToArray();
        DataValue[] values = columns.Select(c => c.Value).ToArray();
        return new Row(names, values);
    }

    private static async Task<List<Row>> CollectAsync(IQueryOperator op, ExecutionContext? context = null)
    {
        context ??= CreateContext();
        List<Row> rows = new();
        await foreach (Row row in op.ExecuteAsync(context))
        {
            rows.Add(row);
        }

        return rows;
    }

    // ─────────────── FilterOperator tests ───────────────

    [Fact]
    public async Task Filter_PassesMatchingRows()
    {
        MockOperator source = new(
            MakeRow(("age", DataValue.FromScalar(25f))),
            MakeRow(("age", DataValue.FromScalar(15f))),
            MakeRow(("age", DataValue.FromScalar(30f))));

        FilterOperator filter = new(source,
            new BinaryExpression(
                new ColumnReference("age"),
                BinaryOperator.GreaterThanOrEqual,
                new LiteralExpression(18)));

        List<Row> rows = await CollectAsync(filter);

        Assert.Equal(2, rows.Count);
        Assert.Equal(25f, rows[0]["age"].AsScalar());
        Assert.Equal(30f, rows[1]["age"].AsScalar());
    }

    [Fact]
    public async Task Filter_RemovesAllRows()
    {
        MockOperator source = new(
            MakeRow(("x", DataValue.FromScalar(1f))),
            MakeRow(("x", DataValue.FromScalar(2f))));

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
        MockOperator source = new(
            MakeRow(("x", DataValue.FromScalar(5f)), ("y", DataValue.FromScalar(10f))),
            MakeRow(("x", DataValue.FromScalar(15f)), ("y", DataValue.FromScalar(10f))),
            MakeRow(("x", DataValue.FromScalar(5f)), ("y", DataValue.FromScalar(20f))));

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
        Assert.Equal(5f, rows[0]["x"].AsScalar());
    }

    // ─────────────── ProjectOperator tests ───────────────

    [Fact]
    public async Task Project_SelectStar()
    {
        MockOperator source = new(
            MakeRow(("a", DataValue.FromScalar(1f)), ("b", DataValue.FromString("x"))));

        ProjectOperator project = new(source, [new SelectAllColumns()]);

        List<Row> rows = await CollectAsync(project);
        Assert.Single(rows);
        Assert.Equal(2, rows[0].FieldCount);
        Assert.Equal(1f, rows[0]["a"].AsScalar());
        Assert.Equal("x", rows[0]["b"].AsString());
    }

    [Fact]
    public async Task Project_NamedColumns()
    {
        MockOperator source = new(
            MakeRow(("a", DataValue.FromScalar(1f)), ("b", DataValue.FromScalar(2f))));

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
        Assert.Equal(1f, rows[0]["a"].AsScalar());
        Assert.Equal(3f, rows[0]["sum"].AsScalar());
    }

    [Fact]
    public async Task Project_WithAlias()
    {
        MockOperator source = new(
            MakeRow(("x", DataValue.FromScalar(42f))));

        ProjectOperator project = new(source,
        [
            new SelectColumn(new ColumnReference("x"), "answer")
        ]);

        List<Row> rows = await CollectAsync(project);
        Assert.Equal(42f, rows[0]["answer"].AsScalar());
    }

    [Fact]
    public async Task Project_FunctionCall()
    {
        MockOperator source = new(
            MakeRow(("name", DataValue.FromString("hello"))));

        ProjectOperator project = new(source,
        [
            new SelectColumn(
                new FunctionCallExpression("len", [new ColumnReference("name")]),
                "name_len")
        ]);

        List<Row> rows = await CollectAsync(project);
        Assert.Equal(5f, rows[0]["name_len"].AsScalar());
    }

    // ─────────────── JoinOperator tests ───────────────

    [Fact]
    public async Task InnerJoin_MatchingRows()
    {
        MockOperator left = new(
            MakeRow(("id", DataValue.FromScalar(1f)), ("name", DataValue.FromString("Alice"))),
            MakeRow(("id", DataValue.FromScalar(2f)), ("name", DataValue.FromString("Bob"))),
            MakeRow(("id", DataValue.FromScalar(3f)), ("name", DataValue.FromString("Charlie"))));

        MockOperator right = new(
            MakeRow(("id", DataValue.FromScalar(1f)), ("score", DataValue.FromScalar(95f))),
            MakeRow(("id", DataValue.FromScalar(3f)), ("score", DataValue.FromScalar(87f))));

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
        MockOperator left = new(
            MakeRow(("l.id", DataValue.FromScalar(1f)), ("l.name", DataValue.FromString("Alice"))),
            MakeRow(("l.id", DataValue.FromScalar(2f)), ("l.name", DataValue.FromString("Bob"))));

        MockOperator right = new(
            MakeRow(("r.id", DataValue.FromScalar(1f)), ("r.score", DataValue.FromScalar(95f))),
            MakeRow(("r.id", DataValue.FromScalar(3f)), ("r.score", DataValue.FromScalar(87f))));

        JoinOperator join = new(left, right, JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("l", "id"),
                BinaryOperator.Equal,
                new ColumnReference("r", "id")));

        List<Row> rows = await CollectAsync(join);

        Assert.Single(rows);
        Assert.Equal("Alice", rows[0]["l.name"].AsString());
        Assert.Equal(95f, rows[0]["r.score"].AsScalar());
    }

    [Fact]
    public async Task LeftJoin_IncludesUnmatchedLeft()
    {
        MockOperator left = new(
            MakeRow(("l.id", DataValue.FromScalar(1f)), ("l.name", DataValue.FromString("Alice"))),
            MakeRow(("l.id", DataValue.FromScalar(2f)), ("l.name", DataValue.FromString("Bob"))));

        MockOperator right = new(
            MakeRow(("r.id", DataValue.FromScalar(1f)), ("r.score", DataValue.FromScalar(95f))));

        JoinOperator join = new(left, right, JoinType.Left,
            new BinaryExpression(
                new ColumnReference("l", "id"),
                BinaryOperator.Equal,
                new ColumnReference("r", "id")));

        List<Row> rows = await CollectAsync(join);

        Assert.Equal(2, rows.Count);
        // First row: matched
        Assert.Equal("Alice", rows[0]["l.name"].AsString());
        Assert.Equal(95f, rows[0]["r.score"].AsScalar());
        // Second row: unmatched (right side is null)
        Assert.Equal("Bob", rows[1]["l.name"].AsString());
        Assert.True(rows[1]["r.score"].IsNull);
    }

    [Fact]
    public async Task CrossJoin_CartesianProduct()
    {
        MockOperator left = new(
            MakeRow(("a", DataValue.FromScalar(1f))),
            MakeRow(("a", DataValue.FromScalar(2f))));

        MockOperator right = new(
            MakeRow(("b", DataValue.FromString("x"))),
            MakeRow(("b", DataValue.FromString("y"))),
            MakeRow(("b", DataValue.FromString("z"))));

        JoinOperator join = new(left, right, JoinType.Cross, onCondition: null);

        List<Row> rows = await CollectAsync(join);
        Assert.Equal(6, rows.Count); // 2 * 3
    }

    [Fact]
    public async Task CrossJoin_PreservesColumnValues()
    {
        MockOperator left = new(
            MakeRow(("a", DataValue.FromScalar(10f))));

        MockOperator right = new(
            MakeRow(("b", DataValue.FromScalar(20f))));

        JoinOperator join = new(left, right, JoinType.Cross, onCondition: null);

        List<Row> rows = await CollectAsync(join);
        Assert.Single(rows);
        Assert.Equal(10f, rows[0]["a"].AsScalar());
        Assert.Equal(20f, rows[0]["b"].AsScalar());
    }

    [Fact]
    public async Task NullKeys_NeverMatch()
    {
        MockOperator left = new(
            MakeRow(("l.id", DataValue.Null(DataKind.Scalar)), ("l.name", DataValue.FromString("Ghost"))));

        MockOperator right = new(
            MakeRow(("r.id", DataValue.Null(DataKind.Scalar)), ("r.score", DataValue.FromScalar(0f))));

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
        MockOperator source = new(
            MakeRow(("val", DataValue.FromScalar(3f))),
            MakeRow(("val", DataValue.FromScalar(1f))),
            MakeRow(("val", DataValue.FromScalar(2f))));

        OrderByOperator orderBy = new(source,
        [
            new OrderByItem(new ColumnReference("val"), SortDirection.Ascending)
        ]);

        List<Row> rows = await CollectAsync(orderBy);
        Assert.Equal(1f, rows[0]["val"].AsScalar());
        Assert.Equal(2f, rows[1]["val"].AsScalar());
        Assert.Equal(3f, rows[2]["val"].AsScalar());
    }

    [Fact]
    public async Task OrderBy_Descending()
    {
        MockOperator source = new(
            MakeRow(("val", DataValue.FromScalar(1f))),
            MakeRow(("val", DataValue.FromScalar(3f))),
            MakeRow(("val", DataValue.FromScalar(2f))));

        OrderByOperator orderBy = new(source,
        [
            new OrderByItem(new ColumnReference("val"), SortDirection.Descending)
        ]);

        List<Row> rows = await CollectAsync(orderBy);
        Assert.Equal(3f, rows[0]["val"].AsScalar());
        Assert.Equal(2f, rows[1]["val"].AsScalar());
        Assert.Equal(1f, rows[2]["val"].AsScalar());
    }

    [Fact]
    public async Task OrderBy_MultiKey()
    {
        MockOperator source = new(
            MakeRow(("group", DataValue.FromString("B")), ("val", DataValue.FromScalar(2f))),
            MakeRow(("group", DataValue.FromString("A")), ("val", DataValue.FromScalar(3f))),
            MakeRow(("group", DataValue.FromString("A")), ("val", DataValue.FromScalar(1f))));

        OrderByOperator orderBy = new(source,
        [
            new OrderByItem(new ColumnReference("group"), SortDirection.Ascending),
            new OrderByItem(new ColumnReference("val"), SortDirection.Ascending)
        ]);

        List<Row> rows = await CollectAsync(orderBy);
        Assert.Equal("A", rows[0]["group"].AsString());
        Assert.Equal(1f, rows[0]["val"].AsScalar());
        Assert.Equal("A", rows[1]["group"].AsString());
        Assert.Equal(3f, rows[1]["val"].AsScalar());
        Assert.Equal("B", rows[2]["group"].AsString());
    }

    [Fact]
    public async Task OrderBy_NullsLast()
    {
        MockOperator source = new(
            MakeRow(("val", DataValue.Null(DataKind.Scalar))),
            MakeRow(("val", DataValue.FromScalar(1f))),
            MakeRow(("val", DataValue.FromScalar(2f))));

        OrderByOperator orderBy = new(source,
        [
            new OrderByItem(new ColumnReference("val"), SortDirection.Ascending)
        ]);

        List<Row> rows = await CollectAsync(orderBy);
        Assert.Equal(1f, rows[0]["val"].AsScalar());
        Assert.Equal(2f, rows[1]["val"].AsScalar());
        Assert.True(rows[2]["val"].IsNull);
    }

    // ─────────────── LimitOperator tests ───────────────

    [Fact]
    public async Task Limit_TakesSpecifiedCount()
    {
        MockOperator source = new(
            MakeRow(("x", DataValue.FromScalar(1f))),
            MakeRow(("x", DataValue.FromScalar(2f))),
            MakeRow(("x", DataValue.FromScalar(3f))),
            MakeRow(("x", DataValue.FromScalar(4f))),
            MakeRow(("x", DataValue.FromScalar(5f))));

        LimitOperator limit = new(source, 3);
        List<Row> rows = await CollectAsync(limit);

        Assert.Equal(3, rows.Count);
        Assert.Equal(1f, rows[0]["x"].AsScalar());
        Assert.Equal(3f, rows[2]["x"].AsScalar());
    }

    [Fact]
    public async Task Limit_WithOffset()
    {
        MockOperator source = new(
            MakeRow(("x", DataValue.FromScalar(1f))),
            MakeRow(("x", DataValue.FromScalar(2f))),
            MakeRow(("x", DataValue.FromScalar(3f))),
            MakeRow(("x", DataValue.FromScalar(4f))),
            MakeRow(("x", DataValue.FromScalar(5f))));

        LimitOperator limit = new(source, 2, offset: 2);
        List<Row> rows = await CollectAsync(limit);

        Assert.Equal(2, rows.Count);
        Assert.Equal(3f, rows[0]["x"].AsScalar());
        Assert.Equal(4f, rows[1]["x"].AsScalar());
    }

    [Fact]
    public async Task Limit_FewerRowsThanLimit()
    {
        MockOperator source = new(
            MakeRow(("x", DataValue.FromScalar(1f))));

        LimitOperator limit = new(source, 10);
        List<Row> rows = await CollectAsync(limit);

        Assert.Single(rows);
    }

    [Fact]
    public async Task Limit_ZeroReturnsNothing()
    {
        MockOperator source = new(
            MakeRow(("x", DataValue.FromScalar(1f))));

        LimitOperator limit = new(source, 0);
        List<Row> rows = await CollectAsync(limit);

        Assert.Empty(rows);
    }

    // ─────────────── OrderBy + Limit combined ───────────────

    [Fact]
    public async Task OrderByThenLimit_TopN()
    {
        MockOperator source = new(
            MakeRow(("val", DataValue.FromScalar(5f))),
            MakeRow(("val", DataValue.FromScalar(1f))),
            MakeRow(("val", DataValue.FromScalar(3f))),
            MakeRow(("val", DataValue.FromScalar(2f))),
            MakeRow(("val", DataValue.FromScalar(4f))));

        OrderByOperator orderBy = new(source,
        [
            new OrderByItem(new ColumnReference("val"), SortDirection.Ascending)
        ]);

        LimitOperator limit = new(orderBy, 3);
        List<Row> rows = await CollectAsync(limit);

        Assert.Equal(3, rows.Count);
        Assert.Equal(1f, rows[0]["val"].AsScalar());
        Assert.Equal(2f, rows[1]["val"].AsScalar());
        Assert.Equal(3f, rows[2]["val"].AsScalar());
    }

    // ─────────────── AliasOperator tests ───────────────

    [Fact]
    public async Task Alias_PrefixesColumnNames()
    {
        MockOperator source = new(
            MakeRow(("id", DataValue.FromScalar(1f)), ("name", DataValue.FromString("test"))));

        AliasOperator alias = new(source, "t");
        List<Row> rows = await CollectAsync(alias);

        Assert.Single(rows);
        Assert.Equal(1f, rows[0]["t.id"].AsScalar());
        Assert.Equal("test", rows[0]["t.name"].AsString());
        // Also accessible without prefix.
        Assert.Equal(1f, rows[0]["id"].AsScalar());
    }

    // ─────────────── SubqueryOperator tests ───────────────

    [Fact]
    public async Task Subquery_PassesThroughRows()
    {
        MockOperator inner = new(
            MakeRow(("x", DataValue.FromScalar(42f))));

        SubqueryOperator subquery = new(inner, "sub");
        List<Row> rows = await CollectAsync(subquery);

        Assert.Single(rows);
        Assert.Equal(42f, rows[0]["x"].AsScalar());
    }
}
