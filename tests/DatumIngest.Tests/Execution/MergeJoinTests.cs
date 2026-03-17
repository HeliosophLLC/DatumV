using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for the <see cref="MergeJoinOperator"/> which joins two pre-sorted
/// input streams using a two-pointer algorithm.
/// </summary>
public sealed class MergeJoinTests : ServiceTestBase
{
    private static readonly string[] LeftNameColumns = ["l.id", "l.name"];
    private static readonly string[] RightScoreColumns = ["r.id", "r.score"];
    private static readonly string[] RightValColumns = ["r.id", "r.val"];
    /// <summary>
    /// INNER merge join with unique keys on both sides produces only matched rows.
    /// </summary>
    [Fact]
    public async Task InnerMergeJoin_UniqueKeys_ProducesMatchedRows()
    {
        MockOperator left = CreateMockOperator(LeftNameColumns,
            [1f, "Alice"],
            [2f, "Bob"],
            [3f, "Charlie"]);

        MockOperator right = CreateMockOperator(RightScoreColumns,
            [1f, 95f],
            [3f, 87f]);

        MergeJoinOperator join = CreateMergeJoin(left, right, JoinType.Inner);

        List<Row> rows = await CollectAsync(join);

        Assert.Equal(2, rows.Count);

        Row alice = rows.First(row => row["l.name"].AsString() == "Alice");
        Assert.Equal(95f, alice["r.score"].AsFloat32());

        Row charlie = rows.First(row => row["l.name"].AsString() == "Charlie");
        Assert.Equal(87f, charlie["r.score"].AsFloat32());
    }

    /// <summary>
    /// INNER merge join with many-to-many duplicates produces the correct cross product.
    /// </summary>
    [Fact]
    public async Task InnerMergeJoin_ManyToMany_ProducesCrossProduct()
    {
        MockOperator left = CreateMockOperator(LeftNameColumns,
            [1f, "A1"],
            [1f, "A2"],
            [2f, "B"]);

        MockOperator right = CreateMockOperator(RightValColumns,
            [1f, "X"],
            [1f, "Y"]);

        MergeJoinOperator join = CreateMergeJoin(left, right, JoinType.Inner);

        List<Row> rows = await CollectAsync(join);

        // 2 left × 2 right for key 1 = 4 rows, key 2 has no match = 0 rows.
        Assert.Equal(4, rows.Count);
        Assert.Contains(rows, row => row["l.name"].AsString() == "A1" && row["r.val"].AsString() == "X");
        Assert.Contains(rows, row => row["l.name"].AsString() == "A1" && row["r.val"].AsString() == "Y");
        Assert.Contains(rows, row => row["l.name"].AsString() == "A2" && row["r.val"].AsString() == "X");
        Assert.Contains(rows, row => row["l.name"].AsString() == "A2" && row["r.val"].AsString() == "Y");
    }

    /// <summary>
    /// LEFT merge join produces matched rows and unmatched left rows with null-padded right columns.
    /// </summary>
    [Fact]
    public async Task LeftMergeJoin_EmitsUnmatchedLeftRows()
    {
        MockOperator left = CreateMockOperator(LeftNameColumns,
            [1f, "Alice"],
            [2f, "Bob"],
            [3f, "Charlie"]);

        MockOperator right = CreateMockOperator(RightScoreColumns,
            [2f, 70f]);

        MergeJoinOperator join = CreateMergeJoin(left, right, JoinType.Left);

        List<Row> rows = await CollectAsync(join);

        Assert.Equal(3, rows.Count);

        Row alice = rows.First(row => row["l.name"].AsString() == "Alice");
        Assert.True(alice["r.score"].IsNull);

        Row bob = rows.First(row => row["l.name"].AsString() == "Bob");
        Assert.Equal(70f, bob["r.score"].AsFloat32());

        Row charlie = rows.First(row => row["l.name"].AsString() == "Charlie");
        Assert.True(charlie["r.score"].IsNull);
    }

    /// <summary>
    /// RIGHT merge join produces matched rows and unmatched right rows with null-padded left columns.
    /// </summary>
    [Fact]
    public async Task RightMergeJoin_EmitsUnmatchedRightRows()
    {
        MockOperator left = CreateMockOperator(LeftNameColumns,
            [2f, "Bob"]);

        MockOperator right = CreateMockOperator(RightScoreColumns,
            [1f, 95f],
            [2f, 70f],
            [3f, 87f]);

        MergeJoinOperator join = CreateMergeJoin(left, right, JoinType.Right);

        List<Row> rows = await CollectAsync(join);

        Assert.Equal(3, rows.Count);

        Row unmatched1 = rows.First(row => row["r.score"].AsFloat32() == 95f);
        Assert.True(unmatched1["l.name"].IsNull);

        Row matched = rows.First(row => row["r.score"].AsFloat32() == 70f);
        Assert.Equal("Bob", matched["l.name"].AsString());

        Row unmatched3 = rows.First(row => row["r.score"].AsFloat32() == 87f);
        Assert.True(unmatched3["l.name"].IsNull);
    }

    /// <summary>
    /// FULL OUTER merge join emits all rows: matched, unmatched left, and unmatched right.
    /// </summary>
    [Fact]
    public async Task FullOuterMergeJoin_EmitsAllRows()
    {
        MockOperator left = CreateMockOperator(LeftNameColumns,
            [1f, "Alice"],
            [3f, "Charlie"]);

        MockOperator right = CreateMockOperator(RightScoreColumns,
            [2f, 70f],
            [3f, 87f]);

        MergeJoinOperator join = CreateMergeJoin(left, right, JoinType.FullOuter);

        List<Row> rows = await CollectAsync(join);

        Assert.Equal(3, rows.Count);

        // Alice has no right match — null-padded right.
        Row alice = rows.First(row => !row["l.name"].IsNull && row["l.name"].AsString() == "Alice");
        Assert.True(alice["r.score"].IsNull);

        // Score 70 has no left match — null-padded left.
        Row unmatched = rows.First(row => !row["r.score"].IsNull && row["r.score"].AsFloat32() == 70f);
        Assert.True(unmatched["l.name"].IsNull);

        // Charlie + 87 matched.
        Row charlie = rows.First(row => !row["l.name"].IsNull && row["l.name"].AsString() == "Charlie");
        Assert.Equal(87f, charlie["r.score"].AsFloat32());
    }

    /// <summary>
    /// NULL keys never match in merge join — they are emitted with null-padded
    /// counterparts when the join type requires all rows to appear.
    /// </summary>
    [Fact]
    public async Task MergeJoin_NullKeys_NeverMatch()
    {
        MockOperator left = CreateMockOperator(LeftNameColumns,
            [DataValue.Null(DataKind.Float32), "Null1"],
            [1f, "Alice"]);

        MockOperator right = CreateMockOperator(RightScoreColumns,
            [DataValue.Null(DataKind.Float32), 99f],
            [1f, 95f]);

        MergeJoinOperator join = CreateMergeJoin(left, right, JoinType.Left);

        List<Row> rows = await CollectAsync(join);

        Assert.Equal(2, rows.Count);

        // NULL left key — emitted with null right.
        Row nullRow = rows.First(row => row["l.name"].AsString() == "Null1");
        Assert.True(nullRow["r.score"].IsNull);

        // Alice matched with 95.
        Row alice = rows.First(row => row["l.name"].AsString() == "Alice");
        Assert.Equal(95f, alice["r.score"].AsFloat32());
    }

    /// <summary>
    /// Merge join with a residual filter applies the filter after key match.
    /// </summary>
    [Fact]
    public async Task MergeJoin_ResidualFilter_FiltersAfterKeyMatch()
    {
        MockOperator left = CreateMockOperator(LeftNameColumns,
            [1f, "Alice"],
            [1f, "Bob"]);

        MockOperator right = CreateMockOperator(["r.id", "r.target", "r.score"],
            [1f, "Alice", 95f]);

        // ON l.id = r.id AND l.name = r.target
        JoinKeyExtractionResult extraction = JoinKeyExtractor.TryExtract(
            new BinaryExpression(
                new BinaryExpression(
                    new ColumnReference("l", "id"),
                    BinaryOperator.Equal,
                    new ColumnReference("r", "id")),
                BinaryOperator.And,
                new BinaryExpression(
                    new ColumnReference("l", "name"),
                    BinaryOperator.Equal,
                    new ColumnReference("r", "target"))))!;

        // Use only the first key pair for merge ordering; second becomes residual.
        JoinKeyExtractionResult singleKeyExtraction = new(
            [extraction.KeyPairs[0]],
            new BinaryExpression(
                new ColumnReference("l", "name"),
                BinaryOperator.Equal,
                new ColumnReference("r", "target")));

        MergeJoinOperator join = new(left, right, JoinType.Left, singleKeyExtraction, "id", "id");

        List<Row> rows = await CollectAsync(join);

        Assert.Equal(2, rows.Count);

        // Alice matches the residual filter.
        Row alice = rows.First(row => row["l.name"].AsString() == "Alice");
        Assert.Equal(95f, alice["r.score"].AsFloat32());

        // Bob does not match the residual — LEFT JOIN emits with null right.
        Row bob = rows.First(row => row["l.name"].AsString() == "Bob");
        Assert.True(bob["r.score"].IsNull);
    }

    /// <summary>
    /// Merge join with an empty left input produces no rows for INNER join,
    /// and drains the right side for RIGHT join.
    /// </summary>
    [Fact]
    public async Task MergeJoin_EmptyLeftInput_InnerProducesNothing()
    {
        MockOperator left = CreateMockOperator(LeftNameColumns);
        MockOperator right = CreateMockOperator(RightScoreColumns,
            [1f, 95f]);

        MergeJoinOperator join = CreateMergeJoin(left, right, JoinType.Inner);

        List<Row> rows = await CollectAsync(join);

        Assert.Empty(rows);
    }

    /// <summary>
    /// Merge join with an empty right input produces no rows for INNER join,
    /// and drains the left side for LEFT join.
    /// </summary>
    [Fact]
    public async Task MergeJoin_EmptyRightInput_LeftJoinEmitsAllLeft()
    {
        MockOperator left = CreateMockOperator(LeftNameColumns,
            [1f, "Alice"],
            [2f, "Bob"]);

        MockOperator right = CreateMockOperator(RightScoreColumns);

        MergeJoinOperator join = CreateMergeJoin(left, right, JoinType.Left);

        List<Row> rows = await CollectAsync(join);

        // Left rows emitted standalone (no right columns available for null padding).
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row["l.name"].AsString() == "Alice");
        Assert.Contains(rows, row => row["l.name"].AsString() == "Bob");
    }

    /// <summary>
    /// Merge join with both inputs empty produces no rows.
    /// </summary>
    [Fact]
    public async Task MergeJoin_BothEmpty_ProducesNothing()
    {
        MockOperator left = CreateMockOperator(LeftNameColumns);
        MockOperator right = CreateMockOperator(RightScoreColumns);

        MergeJoinOperator join = CreateMergeJoin(left, right, JoinType.Inner);

        List<Row> rows = await CollectAsync(join);

        Assert.Empty(rows);
    }

    /// <summary>
    /// Merge join with single-row inputs produces the matched row.
    /// </summary>
    [Fact]
    public async Task MergeJoin_SingleRowInputs_ProducesMatch()
    {
        MockOperator left = CreateMockOperator(LeftNameColumns,
            [1f, "Alice"]);

        MockOperator right = CreateMockOperator(RightScoreColumns,
            [1f, 95f]);

        MergeJoinOperator join = CreateMergeJoin(left, right, JoinType.Inner);

        List<Row> rows = await CollectAsync(join);

        Assert.Single(rows);
        Assert.Equal("Alice", rows[0]["l.name"].AsString());
        Assert.Equal(95f, rows[0]["r.score"].AsFloat32());
    }

    /// <summary>
    /// Merge join preserves [left | right] column order in output rows.
    /// </summary>
    [Fact]
    public async Task MergeJoin_PreservesColumnOrder()
    {
        MockOperator left = CreateMockOperator(LeftNameColumns,
            [1f, "Alice"]);

        MockOperator right = CreateMockOperator(RightScoreColumns,
            [1f, 95f]);

        MergeJoinOperator join = CreateMergeJoin(left, right, JoinType.Inner);

        List<Row> rows = await CollectAsync(join);

        Assert.Single(rows);
        Row row = rows[0];

        Assert.Equal("l.id", row.ColumnNames[0]);
        Assert.Equal("l.name", row.ColumnNames[1]);
        Assert.Equal("r.id", row.ColumnNames[2]);
        Assert.Equal("r.score", row.ColumnNames[3]);
    }

    /// <summary>
    /// RIGHT merge join with empty left input emits all right rows with null-padded left.
    /// </summary>
    [Fact]
    public async Task RightMergeJoin_EmptyLeftInput_EmitsAllRight()
    {
        MockOperator left = CreateMockOperator(LeftNameColumns);
        MockOperator right = CreateMockOperator(RightScoreColumns,
            [1f, 95f],
            [2f, 70f]);

        MergeJoinOperator join = CreateMergeJoin(left, right, JoinType.Right);

        List<Row> rows = await CollectAsync(join);

        // Right rows emitted standalone (no left columns available for null padding).
        Assert.Equal(2, rows.Count);
    }

    /// <summary>
    /// DescribeForExplain produces the expected operator name and properties.
    /// </summary>
    [Fact]
    public void DescribeForExplain_ShowsMergeJoinProperties()
    {
        MockOperator left = CreateMockOperator(LeftNameColumns);
        MockOperator right = CreateMockOperator(RightScoreColumns);

        MergeJoinOperator join = CreateMergeJoin(left, right, JoinType.Inner);

        OperatorPlanDescription description = join.DescribeForExplain();

        Assert.Equal("Inner Merge Join", description.OperatorName);
        Assert.NotNull(description.Properties);
        Assert.Equal("Inner", description.Properties["type"]);
        Assert.Equal("id", description.Properties["leftKey"]);
        Assert.Equal("id", description.Properties["rightKey"]);
        Assert.Single(description.Annotations);
        Assert.Contains("streaming merge", description.Annotations[0]);
    }

    /// <summary>
    /// Creates a merge join with the standard l.id = r.id key pair.
    /// </summary>
    private static MergeJoinOperator CreateMergeJoin(
        QueryOperator left, QueryOperator right, JoinType joinType)
    {
        JoinKeyExtractionResult extraction = new(
            [(new ColumnReference("l", "id"), new ColumnReference("r", "id"))],
            Residual: null);

        return new MergeJoinOperator(left, right, joinType, extraction, "id", "id");
    }

    private async Task<List<Row>> CollectAsync(QueryOperator op, ExecutionContext? context = null)
    {
        context ??= CreateExecutionContext();

        return await op.CollectRowsAsync(context);
    }
}
