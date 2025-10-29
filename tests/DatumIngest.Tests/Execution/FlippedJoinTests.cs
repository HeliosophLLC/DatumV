using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for the build-side flip optimization in <see cref="JoinOperator"/>.
/// When <see cref="JoinOperator.Flipped"/> is <c>true</c>, the left side is materialized
/// (build) and the right side is streamed (probe), but output column order is preserved
/// as [left | right].
/// </summary>
public sealed class FlippedJoinTests : ServiceTestBase
{
    private static readonly string[] LeftNameColumns = ["l.id", "l.name"];
    private static readonly string[] RightScoreColumns = ["r.id", "r.score"];

    /// <summary>
    /// Memory budget small enough to force spilling through the Grace path.
    /// </summary>
    private const long TinyBudget = 256;

    /// <summary>
    /// A flipped LEFT JOIN produces correct results: matched rows combine left+right,
    /// unmatched left rows appear with null right columns.
    /// </summary>
    [Fact]
    public async Task FlippedLeftJoin_ProducesCorrectResults()
    {
        MockOperator left = CreateMockOperator(LeftNameColumns,
            [1f, "Alice"],
            [2f, "Bob"]);

        MockOperator right = CreateMockOperator(RightScoreColumns,
            [1f, 95f]);

        JoinOperator join = new(left, right, JoinType.Left,
            new BinaryExpression(
                new ColumnReference("l", "id"),
                BinaryOperator.Equal,
                new ColumnReference("r", "id")),
            flipped: true);

        List<Row> rows = await CollectAsync(join);

        Assert.Equal(2, rows.Count);

        Row alice = rows.First(row => row["l.name"].AsString() == "Alice");
        Assert.Equal(95f, alice["r.score"].AsFloat32());

        Row bob = rows.First(row => row["l.name"].AsString() == "Bob");
        Assert.True(bob["r.score"].IsNull);
    }

    /// <summary>
    /// A flipped LEFT JOIN preserves [left | right] column order in output rows.
    /// </summary>
    [Fact]
    public async Task FlippedLeftJoin_PreservesColumnOrder()
    {
        MockOperator left = CreateMockOperator(LeftNameColumns,
            [1f, "Alice"]);

        MockOperator right = CreateMockOperator(RightScoreColumns,
            [1f, 95f]);

        JoinOperator join = new(left, right, JoinType.Left,
            new BinaryExpression(
                new ColumnReference("l", "id"),
                BinaryOperator.Equal,
                new ColumnReference("r", "id")),
            flipped: true);

        List<Row> rows = await CollectAsync(join);

        Assert.Single(rows);
        Row row = rows[0];

        // Columns must be in [left | right] order: l.id, l.name, r.id, r.score.
        Assert.Equal("l.id", row.ColumnNames[0]);
        Assert.Equal("l.name", row.ColumnNames[1]);
        Assert.Equal("r.id", row.ColumnNames[2]);
        Assert.Equal("r.score", row.ColumnNames[3]);
    }

    /// <summary>
    /// A flipped LEFT JOIN where no left row matches any right row emits all left rows
    /// with null-padded right columns.
    /// </summary>
    [Fact]
    public async Task FlippedLeftJoin_NoMatches_EmitsAllLeftWithNullRight()
    {
        MockOperator left = CreateMockOperator(LeftNameColumns,
            [1f, "Alice"],
            [2f, "Bob"]);

        MockOperator right = CreateMockOperator(RightScoreColumns,
            [99f, 50f]);

        JoinOperator join = new(left, right, JoinType.Left,
            new BinaryExpression(
                new ColumnReference("l", "id"),
                BinaryOperator.Equal,
                new ColumnReference("r", "id")),
            flipped: true);

        List<Row> rows = await CollectAsync(join);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.True(row["r.score"].IsNull));
        Assert.Contains(rows, row => row["l.name"].AsString() == "Alice");
        Assert.Contains(rows, row => row["l.name"].AsString() == "Bob");
    }

    /// <summary>
    /// A flipped RIGHT JOIN produces correct results: matched rows combine left+right,
    /// unmatched right rows appear with null left columns.
    /// </summary>
    [Fact]
    public async Task FlippedRightJoin_ProducesCorrectResults()
    {
        MockOperator left = CreateMockOperator(LeftNameColumns,
            [1f, "Alice"]);

        MockOperator right = CreateMockOperator(RightScoreColumns,
            [1f, 95f],
            [2f, 70f]);

        JoinOperator join = new(left, right, JoinType.Right,
            new BinaryExpression(
                new ColumnReference("l", "id"),
                BinaryOperator.Equal,
                new ColumnReference("r", "id")),
            flipped: true);

        List<Row> rows = await CollectAsync(join);

        Assert.Equal(2, rows.Count);

        Row matched = rows.First(row => !row["l.name"].IsNull);
        Assert.Equal("Alice", matched["l.name"].AsString());
        Assert.Equal(95f, matched["r.score"].AsFloat32());

        Row unmatched = rows.First(row => row["l.name"].IsNull);
        Assert.Equal(70f, unmatched["r.score"].AsFloat32());
    }

    /// <summary>
    /// A flipped LEFT JOIN through the Grace hash join path (with memory budget)
    /// produces correct results with spilling.
    /// </summary>
    [Fact]
    public async Task FlippedLeftJoin_GracePath_ProducesCorrectResults()
    {
        MockOperator left = CreateMockOperator(LeftNameColumns,
            [1f, "Alice"],
            [2f, "Bob"],
            [3f, "Charlie"]);

        MockOperator right = CreateMockOperator(RightScoreColumns,
            [1f, 95f],
            [3f, 87f]);

        JoinOperator join = new(left, right, JoinType.Left,
            new BinaryExpression(
                new ColumnReference("l", "id"),
                BinaryOperator.Equal,
                new ColumnReference("r", "id")),
            flipped: true);

        List<Row> rows = await CollectAsync(join, CreateExecutionContext(memoryBudgetBytes: TinyBudget));

        Assert.Equal(3, rows.Count);

        Row alice = rows.First(row => row["l.name"].AsString() == "Alice");
        Assert.Equal(95f, alice["r.score"].AsFloat32());

        Row bob = rows.First(row => row["l.name"].AsString() == "Bob");
        Assert.True(bob["r.score"].IsNull);

        Row charlie = rows.First(row => row["l.name"].AsString() == "Charlie");
        Assert.Equal(87f, charlie["r.score"].AsFloat32());
    }

    /// <summary>
    /// A flipped INNER JOIN produces the same results as a non-flipped INNER JOIN.
    /// </summary>
    [Fact]
    public async Task FlippedInnerJoin_ProducesSameResults()
    {
        MockOperator left = CreateMockOperator(LeftNameColumns,
            [1f, "Alice"],
            [2f, "Bob"]);

        MockOperator right = CreateMockOperator(RightScoreColumns,
            [1f, 95f]);

        JoinOperator join = new(left, right, JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("l", "id"),
                BinaryOperator.Equal,
                new ColumnReference("r", "id")),
            flipped: true);

        List<Row> rows = await CollectAsync(join);

        Assert.Single(rows);
        Assert.Equal("Alice", rows[0]["l.name"].AsString());
        Assert.Equal(95f, rows[0]["r.score"].AsFloat32());
    }

    private async Task<List<Row>> CollectAsync(IQueryOperator op, ExecutionContext? context = null)
    {
        context ??= CreateExecutionContext();
        return await op.CollectRowsAsync(context);
    }
}
