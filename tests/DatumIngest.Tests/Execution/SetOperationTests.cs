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
/// Tests for the <see cref="SetOperationOperator"/> covering all six
/// set operation strategies (UNION/INTERSECT/EXCEPT × ALL/DISTINCT)
/// plus parser integration and edge cases.
/// </summary>
public sealed class SetOperationTests : ServiceTestBase
{
    private static Row MakeRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = columns.Select(column => column.Name).ToArray();
        DataValue[] values = columns.Select(column => column.Value).ToArray();
        return new Row(names, values);
    }

    private static async Task<List<Row>> CollectAsync(
        IQueryOperator op, ExecutionContext? context = null)
    {
        context ??= TestExecutionContext.Create();
        return await op.CollectRowsAsync(context);
    }

    private static Row R(float value)
    {
        return MakeRow(("x", DataValue.FromFloat32(value)));
    }

    // ─────────────── Parsing ───────────────

    [Fact]
    public void Parse_UnionAll()
    {
        QueryExpression result = SqlParser.Parse("SELECT a FROM t1 UNION ALL SELECT a FROM t2");

        CompoundQueryExpression compound = Assert.IsType<CompoundQueryExpression>(result);
        Assert.Equal(SetOperationType.Union, compound.OperationType);
        Assert.True(compound.All);
    }

    [Fact]
    public void Parse_UnionDistinct()
    {
        QueryExpression result = SqlParser.Parse("SELECT a FROM t1 UNION SELECT a FROM t2");

        CompoundQueryExpression compound = Assert.IsType<CompoundQueryExpression>(result);
        Assert.Equal(SetOperationType.Union, compound.OperationType);
        Assert.False(compound.All);
    }

    [Fact]
    public void Parse_IntersectAll()
    {
        QueryExpression result = SqlParser.Parse("SELECT a FROM t1 INTERSECT ALL SELECT a FROM t2");

        CompoundQueryExpression compound = Assert.IsType<CompoundQueryExpression>(result);
        Assert.Equal(SetOperationType.Intersect, compound.OperationType);
        Assert.True(compound.All);
    }

    [Fact]
    public void Parse_IntersectDistinct()
    {
        QueryExpression result = SqlParser.Parse("SELECT a FROM t1 INTERSECT SELECT a FROM t2");

        CompoundQueryExpression compound = Assert.IsType<CompoundQueryExpression>(result);
        Assert.Equal(SetOperationType.Intersect, compound.OperationType);
        Assert.False(compound.All);
    }

    [Fact]
    public void Parse_ExceptAll()
    {
        QueryExpression result = SqlParser.Parse("SELECT a FROM t1 EXCEPT ALL SELECT a FROM t2");

        CompoundQueryExpression compound = Assert.IsType<CompoundQueryExpression>(result);
        Assert.Equal(SetOperationType.Except, compound.OperationType);
        Assert.True(compound.All);
    }

    [Fact]
    public void Parse_ExceptDistinct()
    {
        QueryExpression result = SqlParser.Parse("SELECT a FROM t1 EXCEPT SELECT a FROM t2");

        CompoundQueryExpression compound = Assert.IsType<CompoundQueryExpression>(result);
        Assert.Equal(SetOperationType.Except, compound.OperationType);
        Assert.False(compound.All);
    }

    [Fact]
    public void Parse_IntersectBindsTighterThanUnion()
    {
        // "A UNION B INTERSECT C" → "A UNION (B INTERSECT C)"
        QueryExpression result = SqlParser.Parse(
            "SELECT a FROM t1 UNION SELECT a FROM t2 INTERSECT SELECT a FROM t3");

        CompoundQueryExpression union = Assert.IsType<CompoundQueryExpression>(result);
        Assert.Equal(SetOperationType.Union, union.OperationType);

        Assert.IsType<SelectQueryExpression>(union.Left);

        CompoundQueryExpression intersect = Assert.IsType<CompoundQueryExpression>(union.Right);
        Assert.Equal(SetOperationType.Intersect, intersect.OperationType);
    }

    [Fact]
    public void Parse_ChainedUnionsAreLeftAssociative()
    {
        // "A UNION B UNION C" → "(A UNION B) UNION C"
        QueryExpression result = SqlParser.Parse(
            "SELECT a FROM t1 UNION SELECT a FROM t2 UNION SELECT a FROM t3");

        CompoundQueryExpression outer = Assert.IsType<CompoundQueryExpression>(result);
        Assert.Equal(SetOperationType.Union, outer.OperationType);
        Assert.IsType<SelectQueryExpression>(outer.Right);

        CompoundQueryExpression inner = Assert.IsType<CompoundQueryExpression>(outer.Left);
        Assert.Equal(SetOperationType.Union, inner.OperationType);
    }

    [Fact]
    public void Parse_CompoundWithTrailingOrderBy()
    {
        QueryExpression result = SqlParser.Parse(
            "SELECT a FROM t1 UNION SELECT a FROM t2 ORDER BY a");

        CompoundQueryExpression compound = Assert.IsType<CompoundQueryExpression>(result);
        Assert.NotNull(compound.OrderBy);
        Assert.Single(compound.OrderBy.Items);
    }

    [Fact]
    public void Parse_CompoundWithTrailingLimit()
    {
        QueryExpression result = SqlParser.Parse(
            "SELECT a FROM t1 UNION SELECT a FROM t2 LIMIT 10");

        CompoundQueryExpression compound = Assert.IsType<CompoundQueryExpression>(result);
        Assert.NotNull(compound.Limit);
    }

    [Fact]
    public void Parse_SimpleSelectReturnsSelectQueryExpression()
    {
        QueryExpression result = SqlParser.Parse("SELECT a FROM t");

        Assert.IsType<SelectQueryExpression>(result);
    }

    // ─────────────── UNION ALL ───────────────

    [Fact]
    public async Task UnionAll_ConcatenatesBothSources()
    {
        MockOperator left = new(R(1), R(2));
        MockOperator right = new(R(3), R(4));
        SetOperationOperator op = new(left, right, SetOperationType.Union, all: true);

        List<Row> results = await CollectAsync(op);

        Assert.Equal(4, results.Count);
        Assert.Equal(1f, results[0][0].AsFloat32());
        Assert.Equal(2f, results[1][0].AsFloat32());
        Assert.Equal(3f, results[2][0].AsFloat32());
        Assert.Equal(4f, results[3][0].AsFloat32());
    }

    [Fact]
    public async Task UnionAll_PreservesDuplicates()
    {
        MockOperator left = new(R(1), R(2), R(1));
        MockOperator right = new(R(2), R(3));
        SetOperationOperator op = new(left, right, SetOperationType.Union, all: true);

        List<Row> results = await CollectAsync(op);

        Assert.Equal(5, results.Count);
    }

    [Fact]
    public async Task UnionAll_EmptyLeft()
    {
        MockOperator left = new();
        MockOperator right = new(R(1), R(2));
        SetOperationOperator op = new(left, right, SetOperationType.Union, all: true);

        List<Row> results = await CollectAsync(op);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task UnionAll_EmptyRight()
    {
        MockOperator left = new(R(1), R(2));
        MockOperator right = new();
        SetOperationOperator op = new(left, right, SetOperationType.Union, all: true);

        List<Row> results = await CollectAsync(op);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task UnionAll_BothEmpty()
    {
        MockOperator left = new();
        MockOperator right = new();
        SetOperationOperator op = new(left, right, SetOperationType.Union, all: true);

        List<Row> results = await CollectAsync(op);

        Assert.Empty(results);
    }

    // ─────────────── UNION DISTINCT ───────────────

    [Fact]
    public async Task UnionDistinct_RemovesDuplicates()
    {
        MockOperator left = new(R(1), R(2), R(1));
        MockOperator right = new(R(2), R(3));
        SetOperationOperator op = new(left, right, SetOperationType.Union, all: false);

        List<Row> results = await CollectAsync(op);

        Assert.Equal(3, results.Count);
        float[] values = results.Select(row => row[0].AsFloat32()).OrderBy(value => value).ToArray();
        Assert.Equal([1f, 2f, 3f], values);
    }

    [Fact]
    public async Task UnionDistinct_AllDuplicates()
    {
        MockOperator left = new(R(1), R(1));
        MockOperator right = new(R(1), R(1));
        SetOperationOperator op = new(left, right, SetOperationType.Union, all: false);

        List<Row> results = await CollectAsync(op);

        Assert.Single(results);
    }

    [Fact]
    public async Task UnionDistinct_MultiColumn()
    {
        Row r1 = MakeRow(("a", DataValue.FromFloat32(1f)), ("b", DataValue.FromString("x")));
        Row r2 = MakeRow(("a", DataValue.FromFloat32(2f)), ("b", DataValue.FromString("y")));
        Row r1Dup = MakeRow(("a", DataValue.FromFloat32(1f)), ("b", DataValue.FromString("x")));

        MockOperator left = new(r1, r2);
        MockOperator right = new(r1Dup);
        SetOperationOperator op = new(left, right, SetOperationType.Union, all: false);

        List<Row> results = await CollectAsync(op);

        Assert.Equal(2, results.Count);
    }

    // ─────────────── INTERSECT DISTINCT ───────────────

    [Fact]
    public async Task IntersectDistinct_ReturnsCommonRows()
    {
        MockOperator left = new(R(1), R(2), R(3));
        MockOperator right = new(R(2), R(3), R(4));
        SetOperationOperator op = new(left, right, SetOperationType.Intersect, all: false);

        List<Row> results = await CollectAsync(op);

        Assert.Equal(2, results.Count);
        float[] values = results.Select(row => row[0].AsFloat32()).OrderBy(value => value).ToArray();
        Assert.Equal([2f, 3f], values);
    }

    [Fact]
    public async Task IntersectDistinct_NoOverlap()
    {
        MockOperator left = new(R(1), R(2));
        MockOperator right = new(R(3), R(4));
        SetOperationOperator op = new(left, right, SetOperationType.Intersect, all: false);

        List<Row> results = await CollectAsync(op);

        Assert.Empty(results);
    }

    [Fact]
    public async Task IntersectDistinct_EmptyRight()
    {
        MockOperator left = new(R(1), R(2));
        MockOperator right = new();
        SetOperationOperator op = new(left, right, SetOperationType.Intersect, all: false);

        List<Row> results = await CollectAsync(op);

        Assert.Empty(results);
    }

    [Fact]
    public async Task IntersectDistinct_DuplicatesInLeftYieldOnce()
    {
        MockOperator left = new(R(1), R(1), R(2));
        MockOperator right = new(R(1), R(2));
        SetOperationOperator op = new(left, right, SetOperationType.Intersect, all: false);

        List<Row> results = await CollectAsync(op);

        Assert.Equal(2, results.Count);
    }

    // ─────────────── INTERSECT ALL ───────────────

    [Fact]
    public async Task IntersectAll_ReturnsMinimumOccurrences()
    {
        // Left has 1 three times, 2 once. Right has 1 twice, 2 twice.
        // Result: 1 twice, 2 once.
        MockOperator left = new(R(1), R(1), R(1), R(2));
        MockOperator right = new(R(1), R(1), R(2), R(2));
        SetOperationOperator op = new(left, right, SetOperationType.Intersect, all: true);

        List<Row> results = await CollectAsync(op);

        Assert.Equal(3, results.Count);
        int onesCount = results.Count(row => row[0].AsFloat32() == 1f);
        int twosCount = results.Count(row => row[0].AsFloat32() == 2f);
        Assert.Equal(2, onesCount);
        Assert.Equal(1, twosCount);
    }

    [Fact]
    public async Task IntersectAll_NoOverlap()
    {
        MockOperator left = new(R(1));
        MockOperator right = new(R(2));
        SetOperationOperator op = new(left, right, SetOperationType.Intersect, all: true);

        List<Row> results = await CollectAsync(op);

        Assert.Empty(results);
    }

    // ─────────────── EXCEPT DISTINCT ───────────────

    [Fact]
    public async Task ExceptDistinct_RemovesRightRows()
    {
        MockOperator left = new(R(1), R(2), R(3));
        MockOperator right = new(R(2), R(4));
        SetOperationOperator op = new(left, right, SetOperationType.Except, all: false);

        List<Row> results = await CollectAsync(op);

        Assert.Equal(2, results.Count);
        float[] values = results.Select(row => row[0].AsFloat32()).OrderBy(value => value).ToArray();
        Assert.Equal([1f, 3f], values);
    }

    [Fact]
    public async Task ExceptDistinct_EmptyRight_ReturnsDistinctLeft()
    {
        MockOperator left = new(R(1), R(1), R(2));
        MockOperator right = new();
        SetOperationOperator op = new(left, right, SetOperationType.Except, all: false);

        List<Row> results = await CollectAsync(op);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task ExceptDistinct_AllRemoved()
    {
        MockOperator left = new(R(1), R(2));
        MockOperator right = new(R(1), R(2));
        SetOperationOperator op = new(left, right, SetOperationType.Except, all: false);

        List<Row> results = await CollectAsync(op);

        Assert.Empty(results);
    }

    [Fact]
    public async Task ExceptDistinct_DuplicatesInLeftStillSingle()
    {
        MockOperator left = new(R(1), R(1), R(1));
        MockOperator right = new(R(2));
        SetOperationOperator op = new(left, right, SetOperationType.Except, all: false);

        List<Row> results = await CollectAsync(op);

        // EXCEPT DISTINCT returns each row at most once.
        Assert.Single(results);
    }

    // ─────────────── EXCEPT ALL ───────────────

    [Fact]
    public async Task ExceptAll_SubtractsCounts()
    {
        // Left has 1 three times, 2 once. Right has 1 once.
        // Result: 1 twice, 2 once.
        MockOperator left = new(R(1), R(1), R(1), R(2));
        MockOperator right = new(R(1));
        SetOperationOperator op = new(left, right, SetOperationType.Except, all: true);

        List<Row> results = await CollectAsync(op);

        Assert.Equal(3, results.Count);
        int onesCount = results.Count(row => row[0].AsFloat32() == 1f);
        Assert.Equal(2, onesCount);
    }

    [Fact]
    public async Task ExceptAll_EmptyRight_ReturnsAllLeft()
    {
        MockOperator left = new(R(1), R(1), R(2));
        MockOperator right = new();
        SetOperationOperator op = new(left, right, SetOperationType.Except, all: true);

        List<Row> results = await CollectAsync(op);

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task ExceptAll_MoreInRightThanLeft()
    {
        MockOperator left = new(R(1));
        MockOperator right = new(R(1), R(1), R(1));
        SetOperationOperator op = new(left, right, SetOperationType.Except, all: true);

        List<Row> results = await CollectAsync(op);

        Assert.Empty(results);
    }

    // ─────────────── Edge cases ───────────────

    [Fact]
    public async Task UnionAll_LargeDataset()
    {
        Row[] leftRows = Enumerable.Range(0, 1000).Select(index => R(index)).ToArray();
        Row[] rightRows = Enumerable.Range(500, 1000).Select(index => R(index)).ToArray();
        MockOperator left = new(leftRows);
        MockOperator right = new(rightRows);
        SetOperationOperator op = new(left, right, SetOperationType.Union, all: true);

        List<Row> results = await CollectAsync(op);

        Assert.Equal(2000, results.Count);
    }

    [Fact]
    public async Task UnionDistinct_WithSpill()
    {
        // Use a very small memory budget to force spill-to-disk.
        Row[] leftRows = Enumerable.Range(0, 500).Select(index => R(index)).ToArray();
        Row[] rightRows = Enumerable.Range(250, 500).Select(index => R(index)).ToArray();
        MockOperator left = new(leftRows);
        MockOperator right = new(rightRows);
        SetOperationOperator op = new(left, right, SetOperationType.Union, all: false);

        ExecutionContext context = TestExecutionContext.Create(memoryBudgetBytes: 1024);
        List<Row> results = await CollectAsync(op, context);

        // 0..499 UNION 250..749 → 0..749 = 750 distinct values
        Assert.Equal(750, results.Count);

        float[] values = results.Select(row => row[0].AsFloat32()).OrderBy(value => value).ToArray();
        float[] expected = Enumerable.Range(0, 750).Select(index => (float)index).ToArray();
        Assert.Equal(expected, values);

        op.Dispose();
    }

    [Fact]
    public void Properties_AreExposed()
    {
        MockOperator left = new();
        MockOperator right = new();
        SetOperationOperator op = new(left, right, SetOperationType.Intersect, all: true);

        Assert.Same(left, op.Left);
        Assert.Same(right, op.Right);
        Assert.Equal(SetOperationType.Intersect, op.OperationType);
        Assert.True(op.All);
    }
}
