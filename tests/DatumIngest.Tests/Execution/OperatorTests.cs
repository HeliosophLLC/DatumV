using System.Runtime.CompilerServices;
using DatumQuery.Catalog;
using DatumQuery.Execution;
using DatumQuery.Execution.Operators;
using DatumQuery.Functions;
using DatumQuery.Model;
using DatumQuery.Parsing.Ast;
using ExecutionContext = DatumQuery.Execution.ExecutionContext;

namespace DatumQuery.Tests.Execution;

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

    // ─────────────── Expression-based hash join tests ───────────────

    [Fact]
    public async Task HashJoin_FunctionExpression_MatchesCorrectly()
    {
        // Simulates GET_FILENAME(z.file_name) = i.file_name
        // Left side has full paths, right side has just filenames.
        MockOperator left = new(
            MakeRow(("l.file_name", DataValue.FromString("images/cat.jpg")), ("l.data", DataValue.FromScalar(1f))),
            MakeRow(("l.file_name", DataValue.FromString("images/dog.png")), ("l.data", DataValue.FromScalar(2f))),
            MakeRow(("l.file_name", DataValue.FromString("images/bird.jpg")), ("l.data", DataValue.FromScalar(3f))));

        MockOperator right = new(
            MakeRow(("r.file_name", DataValue.FromString("cat.jpg")), ("r.score", DataValue.FromScalar(95f))),
            MakeRow(("r.file_name", DataValue.FromString("bird.jpg")), ("r.score", DataValue.FromScalar(80f))));

        // ON GET_FILENAME(l.file_name) = r.file_name
        JoinOperator join = new(left, right, JoinType.Inner,
            new BinaryExpression(
                new FunctionCallExpression("GET_FILENAME", [new ColumnReference("l", "file_name")]),
                BinaryOperator.Equal,
                new ColumnReference("r", "file_name")));

        List<Row> rows = await CollectAsync(join);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r["l.data"].AsScalar() == 1f && r["r.score"].AsScalar() == 95f);
        Assert.Contains(rows, r => r["l.data"].AsScalar() == 3f && r["r.score"].AsScalar() == 80f);
    }

    [Fact]
    public async Task HashJoin_CompoundKeys_MatchesOnBothKeys()
    {
        MockOperator left = new(
            MakeRow(("l.a", DataValue.FromScalar(1f)), ("l.b", DataValue.FromString("x")), ("l.val", DataValue.FromScalar(10f))),
            MakeRow(("l.a", DataValue.FromScalar(1f)), ("l.b", DataValue.FromString("y")), ("l.val", DataValue.FromScalar(20f))),
            MakeRow(("l.a", DataValue.FromScalar(2f)), ("l.b", DataValue.FromString("x")), ("l.val", DataValue.FromScalar(30f))));

        MockOperator right = new(
            MakeRow(("r.a", DataValue.FromScalar(1f)), ("r.b", DataValue.FromString("x")), ("r.info", DataValue.FromString("match1"))),
            MakeRow(("r.a", DataValue.FromScalar(2f)), ("r.b", DataValue.FromString("y")), ("r.info", DataValue.FromString("no_match"))));

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
        Assert.Equal(10f, rows[0]["l.val"].AsScalar());
        Assert.Equal("match1", rows[0]["r.info"].AsString());
    }

    [Fact]
    public async Task HashJoin_ResidualFilter_AppliedAfterHashMatch()
    {
        MockOperator left = new(
            MakeRow(("l.id", DataValue.FromScalar(1f)), ("l.val", DataValue.FromScalar(100f))),
            MakeRow(("l.id", DataValue.FromScalar(2f)), ("l.val", DataValue.FromScalar(200f))));

        MockOperator right = new(
            MakeRow(("r.id", DataValue.FromScalar(1f)), ("r.threshold", DataValue.FromScalar(150f))),
            MakeRow(("r.id", DataValue.FromScalar(2f)), ("r.threshold", DataValue.FromScalar(150f))));

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
        Assert.Equal(2f, rows[0]["l.id"].AsScalar());
    }

    [Fact]
    public async Task HashJoin_NullExpressionKey_NeverMatches()
    {
        MockOperator left = new(
            MakeRow(("l.path", DataValue.Null(DataKind.String))));

        MockOperator right = new(
            MakeRow(("r.name", DataValue.Null(DataKind.String))));

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
        MockOperator left = new(
            MakeRow(("l.id", DataValue.FromScalar(1f)), ("l.tag", DataValue.FromString("A"))),
            MakeRow(("l.id", DataValue.FromScalar(1f)), ("l.tag", DataValue.FromString("B"))));

        MockOperator right = new(
            MakeRow(("r.id", DataValue.FromScalar(1f)), ("r.info", DataValue.FromString("X"))),
            MakeRow(("r.id", DataValue.FromScalar(1f)), ("r.info", DataValue.FromString("Y"))));

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
        MockOperator left = new(
            MakeRow(("l.path", DataValue.FromString("a/file1.txt")), ("l.size", DataValue.FromScalar(100f))),
            MakeRow(("l.path", DataValue.FromString("b/file2.txt")), ("l.size", DataValue.FromScalar(200f))));

        MockOperator right = new(
            MakeRow(("r.name", DataValue.FromString("file1.txt")), ("r.label", DataValue.FromString("doc"))));

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
        MockOperator inner = new(
            MakeRow(("x", DataValue.FromScalar(42f))));

        SubqueryOperator subquery = new(inner, "sub");
        List<Row> rows = await CollectAsync(subquery);

        Assert.Single(rows);
        Assert.Equal(42f, rows[0]["x"].AsScalar());
    }

    // ─────────────── LateMaterializationOperator tests ───────────────

    /// <summary>
    /// Enriches child rows with deferred columns fetched from a keyed provider.
    /// </summary>
    [Fact]
    public async Task LateMaterialization_EnrichesRowsWithDeferredColumns()
    {
        MockOperator source = new(
            MakeRow(("file_name", DataValue.FromString("a.txt")), ("size", DataValue.FromScalar(10f))),
            MakeRow(("file_name", DataValue.FromString("b.txt")), ("size", DataValue.FromScalar(20f))));

        TableDescriptor descriptor = new("mock", "files", "dummy.zip",
            new Dictionary<string, string>());

        MockKeyedProvider keyedProvider = new(new Dictionary<string, Row>
        {
            ["a.txt"] = MakeRow(("file_name", DataValue.FromString("a.txt")), ("file_bytes", DataValue.FromUInt8Array(new byte[] { 1, 2, 3 }))),
            ["b.txt"] = MakeRow(("file_name", DataValue.FromString("b.txt")), ("file_bytes", DataValue.FromUInt8Array(new byte[] { 4, 5 }))),
        });

        HashSet<string> deferred = new(StringComparer.OrdinalIgnoreCase) { "file_bytes" };
        LateMaterializationOperator op = new(source, descriptor, "file_name", deferred, alias: null);

        TableCatalog catalog = new();
        catalog.RegisterProvider("mock", () => keyedProvider);
        catalog.Register(descriptor);

        ExecutionContext context = new(
            CancellationToken.None,
            FunctionRegistry.CreateDefault(),
            catalog);

        List<Row> rows = await CollectAsync(op, context);

        Assert.Equal(2, rows.Count);
        Assert.Equal(new byte[] { 1, 2, 3 }, rows[0]["file_bytes"].AsUInt8Array());
        Assert.Equal(new byte[] { 4, 5 }, rows[1]["file_bytes"].AsUInt8Array());
        // Original columns preserved.
        Assert.Equal(10f, rows[0]["size"].AsScalar());
    }

    /// <summary>
    /// When an alias is specified, both unqualified and qualified column names are present.
    /// </summary>
    [Fact]
    public async Task LateMaterialization_WithAlias_AddsQualifiedColumns()
    {
        // Source rows simulate post-JOIN output where AliasOperator has qualified
        // the column names with the table alias.
        MockOperator source = new(
            MakeRow(("z.file_name", DataValue.FromString("a.txt"))));

        TableDescriptor descriptor = new("mock", "files", "dummy.zip",
            new Dictionary<string, string>());

        MockKeyedProvider keyedProvider = new(new Dictionary<string, Row>
        {
            ["a.txt"] = MakeRow(("file_name", DataValue.FromString("a.txt")), ("file_bytes", DataValue.FromUInt8Array(new byte[] { 9 }))),
        });

        HashSet<string> deferred = new(StringComparer.OrdinalIgnoreCase) { "file_bytes" };
        LateMaterializationOperator op = new(source, descriptor, "file_name", deferred, alias: "z");

        TableCatalog catalog = new();
        catalog.RegisterProvider("mock", () => keyedProvider);
        catalog.Register(descriptor);

        ExecutionContext context = new(
            CancellationToken.None,
            FunctionRegistry.CreateDefault(),
            catalog);

        List<Row> rows = await CollectAsync(op, context);

        Assert.Single(rows);
        Assert.Equal(new byte[] { 9 }, rows[0]["file_bytes"].AsUInt8Array());
        Assert.Equal(new byte[] { 9 }, rows[0]["z.file_bytes"].AsUInt8Array());
    }

    /// <summary>
    /// Empty child produces no output and does not call the keyed provider.
    /// </summary>
    [Fact]
    public async Task LateMaterialization_EmptyChild_ReturnsEmpty()
    {
        MockOperator source = new();

        TableDescriptor descriptor = new("mock", "files", "dummy.zip",
            new Dictionary<string, string>());

        MockKeyedProvider keyedProvider = new(new Dictionary<string, Row>());
        HashSet<string> deferred = new(StringComparer.OrdinalIgnoreCase) { "file_bytes" };
        LateMaterializationOperator op = new(source, descriptor, "file_name", deferred, alias: null);

        TableCatalog catalog = new();
        catalog.RegisterProvider("mock", () => keyedProvider);
        catalog.Register(descriptor);

        ExecutionContext context = new(
            CancellationToken.None,
            FunctionRegistry.CreateDefault(),
            catalog);

        List<Row> rows = await CollectAsync(op, context);

        Assert.Empty(rows);
        Assert.False(keyedProvider.WasCalled);
    }

    /// <summary>
    /// Rows whose key has no match in the keyed provider get null for deferred columns.
    /// </summary>
    [Fact]
    public async Task LateMaterialization_UnmatchedKeys_FillsNull()
    {
        MockOperator source = new(
            MakeRow(("file_name", DataValue.FromString("missing.txt")), ("x", DataValue.FromScalar(1f))));

        TableDescriptor descriptor = new("mock", "files", "dummy.zip",
            new Dictionary<string, string>());

        MockKeyedProvider keyedProvider = new(new Dictionary<string, Row>());
        HashSet<string> deferred = new(StringComparer.OrdinalIgnoreCase) { "file_bytes" };
        LateMaterializationOperator op = new(source, descriptor, "file_name", deferred, alias: null);

        TableCatalog catalog = new();
        catalog.RegisterProvider("mock", () => keyedProvider);
        catalog.Register(descriptor);

        ExecutionContext context = new(
            CancellationToken.None,
            FunctionRegistry.CreateDefault(),
            catalog);

        List<Row> rows = await CollectAsync(op, context);

        Assert.Single(rows);
        Assert.True(rows[0]["file_bytes"].IsNull);
        Assert.Equal(1f, rows[0]["x"].AsScalar());
    }

    /// <summary>
    /// After a JOIN, both tables may have a column with the same unqualified name
    /// (e.g. <c>file_name</c>). The operator must use the alias-qualified key
    /// (<c>z.file_name</c>) to look up the correct value from child rows.
    /// Reproduces a bug where unqualified lookup picked up the wrong table's
    /// <c>file_name</c>, causing <see cref="IKeyedTableProvider.FetchByKeysAsync"/>
    /// to miss all entries and return null bytes.
    /// </summary>
    [Fact]
    public async Task LateMaterialization_AmbiguousKeyAfterJoin_UsesQualifiedLookup()
    {
        // Simulate post-JOIN rows where both tables contribute a "file_name" column.
        // The ZIP's file_name is "dir/a.txt", but the images table's file_name is
        // "a.txt" (just the filename portion). The unqualified "file_name" in the
        // row's name index resolves to the images table's value because it appears
        // later in the combined row.
        MockOperator source = new(
            MakeRow(
                ("z.file_name", DataValue.FromString("dir/a.txt")),
                ("file_name", DataValue.FromString("a.txt")),
                ("i.file_name", DataValue.FromString("a.txt")),
                ("caption", DataValue.FromString("a bicycle"))));

        TableDescriptor descriptor = new("mock", "archive", "dummy.zip",
            new Dictionary<string, string>());

        // The keyed provider stores entries by their full path (as the ZIP does).
        MockKeyedProvider keyedProvider = new(new Dictionary<string, Row>
        {
            ["dir/a.txt"] = MakeRow(
                ("file_name", DataValue.FromString("dir/a.txt")),
                ("file_bytes", DataValue.FromUInt8Array(new byte[] { 0xFF, 0xD8 }))),
        });

        HashSet<string> deferred = new(StringComparer.OrdinalIgnoreCase) { "file_bytes" };
        LateMaterializationOperator op = new(source, descriptor, "file_name", deferred, alias: "z");

        TableCatalog catalog = new();
        catalog.RegisterProvider("mock", () => keyedProvider);
        catalog.Register(descriptor);

        ExecutionContext context = new(
            CancellationToken.None,
            FunctionRegistry.CreateDefault(),
            catalog);

        List<Row> rows = await CollectAsync(op, context);

        Assert.Single(rows);
        // Must fetch the actual bytes, not null.
        Assert.False(rows[0]["file_bytes"].IsNull, "file_bytes should not be null — the operator must use the alias-qualified key to find the ZIP entry.");
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, rows[0]["file_bytes"].AsUInt8Array());
    }
}

/// <summary>
/// A mock <see cref="IKeyedTableProvider"/> for unit testing
/// <see cref="LateMaterializationOperator"/>.
/// </summary>
internal sealed class MockKeyedProvider : IKeyedTableProvider
{
    private readonly Dictionary<string, Row> _rowsByKey;

    /// <summary>Whether <see cref="FetchByKeysAsync"/> was called.</summary>
    public bool WasCalled { get; private set; }

    public MockKeyedProvider(Dictionary<string, Row> rowsByKey)
    {
        _rowsByKey = rowsByKey;
    }

    public Task<Schema> GetSchemaAsync(TableDescriptor descriptor, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<Row> OpenAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task<ProviderCapabilities> GetCapabilitiesAsync(
        TableDescriptor descriptor, CancellationToken cancellationToken)
    {
        return Task.FromResult<ProviderCapabilities>(null!);
    }

    public async IAsyncEnumerable<Row> FetchByKeysAsync(
        TableDescriptor descriptor,
        string keyColumn,
        IReadOnlySet<DataValue> keyValues,
        IReadOnlySet<string>? requiredColumns,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        WasCalled = true;

        foreach (DataValue keyValue in keyValues)
        {
            string key = keyValue.AsString();
            if (_rowsByKey.TryGetValue(key, out Row? row))
            {
                yield return row;
            }
        }

        await Task.CompletedTask;
    }
}
