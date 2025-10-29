using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Functions.Aggregates;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for the parallel hash join probe and parallel hash aggregate paths.
/// These tests use <see cref="ExecutionContext.DegreeOfParallelism"/> &gt; 1
/// to activate parallel operator dispatch. Because the data sources are
/// <see cref="MockOperator"/> instances (not <see cref="ScanOperator"/>),
/// the estimated row count is <c>null</c>, which satisfies the activation
/// threshold <c>estimatedRows is null or &gt;= 100_000</c>.
/// </summary>
public sealed class ParallelOperatorTests : ServiceTestBase
{
    private static readonly string[] XColumns = ["x"];
    private static readonly string[] LeftNameColumns = ["l.id", "l.name"];
    private static readonly string[] RightScoreColumns = ["r.id", "r.score"];

    private ExecutionContext CreateParallelContext(int degreeOfParallelism = 2)
    {
        Pool pool = GetService<Pool>();
        return new ExecutionContext(
            CancellationToken.None,
            FunctionRegistry.CreateDefault(),
            CreateCatalog(),
            new LocalBufferPool(),
            pool)
        {
            DegreeOfParallelism = degreeOfParallelism,
            ParallelismBudget = new ParallelismBudget(degreeOfParallelism),
        };
    }

    private static async Task<List<Row>> CollectAsync(IQueryOperator op, ExecutionContext context)
    {
        return await op.CollectRowsAsync(context);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Parallel Hash Join Probe
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that a parallel INNER JOIN produces the same matches as the
    /// sequential path.
    /// </summary>
    [Fact]
    public async Task ParallelProbe_InnerJoin_MatchesSequentialResult()
    {
        MockOperator left = CreateMockOperator(LeftNameColumns,
            [1f, "Alice"],
            [2f, "Bob"],
            [3f, "Charlie"]);

        MockOperator right = CreateMockOperator(RightScoreColumns,
            [1f, 95f],
            [3f, 87f]);

        JoinOperator join = new(left, right, JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("l", "id"),
                BinaryOperator.Equal,
                new ColumnReference("r", "id")));

        ExecutionContext context = CreateParallelContext();
        List<Row> rows = await CollectAsync(join, context);

        Assert.Equal(2, rows.Count);

        Row alice = rows.First(row => row["l.name"].AsString() == "Alice");
        Assert.Equal(95f, alice["r.score"].AsFloat32());

        Row charlie = rows.First(row => row["l.name"].AsString() == "Charlie");
        Assert.Equal(87f, charlie["r.score"].AsFloat32());
    }

    /// <summary>
    /// Verifies that a parallel LEFT JOIN includes unmatched left rows with
    /// null right-side columns.
    /// </summary>
    [Fact]
    public async Task ParallelProbe_LeftJoin_IncludesUnmatchedLeft()
    {
        MockOperator left = CreateMockOperator(LeftNameColumns,
            [1f, "Alice"],
            [2f, "Bob"],
            [3f, "Charlie"]);

        MockOperator right = CreateMockOperator(RightScoreColumns,
            [1f, 95f]);

        JoinOperator join = new(left, right, JoinType.Left,
            new BinaryExpression(
                new ColumnReference("l", "id"),
                BinaryOperator.Equal,
                new ColumnReference("r", "id")));

        ExecutionContext context = CreateParallelContext();
        List<Row> rows = await CollectAsync(join, context);

        Assert.Equal(3, rows.Count);

        Row alice = rows.First(row => row["l.name"].AsString() == "Alice");
        Assert.Equal(95f, alice["r.score"].AsFloat32());

        Row bob = rows.First(row => row["l.name"].AsString() == "Bob");
        Assert.True(bob["r.score"].IsNull);

        Row charlie = rows.First(row => row["l.name"].AsString() == "Charlie");
        Assert.True(charlie["r.score"].IsNull);
    }

    /// <summary>
    /// Verifies that a parallel INNER JOIN with duplicate build keys produces
    /// the correct cartesian product per key.
    /// </summary>
    [Fact]
    public async Task ParallelProbe_InnerJoin_DuplicateBuildKeys()
    {
        MockOperator left = CreateMockOperator(LeftNameColumns,
            [1f, "Alice"],
            [1f, "Bob"]);

        MockOperator right = CreateMockOperator(["r.id", "r.val"],
            [1f, "X"],
            [1f, "Y"]);

        JoinOperator join = new(left, right, JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("l", "id"),
                BinaryOperator.Equal,
                new ColumnReference("r", "id")));

        ExecutionContext context = CreateParallelContext();
        List<Row> rows = await CollectAsync(join, context);

        // 2 left × 2 right = 4 matched rows.
        Assert.Equal(4, rows.Count);
    }

    /// <summary>
    /// Verifies that null keys are not matched in a parallel INNER JOIN.
    /// </summary>
    [Fact]
    public async Task ParallelProbe_InnerJoin_NullKeysDoNotMatch()
    {
        MockOperator left = CreateMockOperator(LeftNameColumns,
            [null, "Null"],
            [1f, "Alice"]);

        MockOperator right = CreateMockOperator(RightScoreColumns,
            [null, 0f],
            [1f, 95f]);

        JoinOperator join = new(left, right, JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("l", "id"),
                BinaryOperator.Equal,
                new ColumnReference("r", "id")));

        ExecutionContext context = CreateParallelContext();
        List<Row> rows = await CollectAsync(join, context);

        // Only the non-null key matches.
        Assert.Single(rows);
        Assert.Equal("Alice", rows[0]["l.name"].AsString());
    }

    /// <summary>
    /// Verifies that a parallel join with compound key produces correct matches.
    /// </summary>
    [Fact]
    public async Task ParallelProbe_InnerJoin_CompoundKeys()
    {
        MockOperator left = CreateMockOperator(["l.a", "l.b", "l.val"],
            [1f, "X", 100f],
            [1f, "Y", 200f],
            [2f, "X", 300f]);

        MockOperator right = CreateMockOperator(["r.a", "r.b", "r.val"],
            [1f, "X", 10f],
            [2f, "Z", 20f]);

        // l.a = r.a AND l.b = r.b
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

        ExecutionContext context = CreateParallelContext();
        List<Row> rows = await CollectAsync(join, context);

        // Only (1, X) matches.
        Assert.Single(rows);
        Assert.Equal(100f, rows[0]["l.val"].AsFloat32());
        Assert.Equal(10f, rows[0]["r.val"].AsFloat32());
    }

    /// <summary>
    /// Verifies correctness with higher parallelism (4 workers).
    /// </summary>
    [Fact]
    public async Task ParallelProbe_InnerJoin_FourWorkers()
    {
        object?[][] leftRows = Enumerable.Range(0, 20)
            .Select(i => new object?[] { (float)i, i * 10f })
            .ToArray();

        object?[][] rightRows = Enumerable.Range(0, 20)
            .Where(i => i % 3 == 0) // 0, 3, 6, 9, 12, 15, 18
            .Select(i => new object?[] { (float)i, $"R{i}" })
            .ToArray();

        MockOperator left = CreateMockOperator(["l.id", "l.val"], rows: leftRows);
        MockOperator right = CreateMockOperator(["r.id", "r.name"], rows: rightRows);

        JoinOperator join = new(left, right, JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("l", "id"),
                BinaryOperator.Equal,
                new ColumnReference("r", "id")));

        ExecutionContext context = CreateParallelContext(degreeOfParallelism: 4);
        List<Row> rows = await CollectAsync(join, context);

        Assert.Equal(7, rows.Count);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Parallel Hash Aggregate
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that a parallel global aggregation (COUNT*) produces the same
    /// result as the sequential path.
    /// </summary>
    [Fact]
    public async Task ParallelAggregate_GlobalCount()
    {
        MockOperator source = CreateMockOperator(XColumns,
            [1f],
            [2f],
            [3f]);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(new CountFunction(), [], "COUNT(*)", IsCountStar: true),
            ]);

        ExecutionContext context = CreateParallelContext();
        List<Row> results = await CollectAsync(groupBy, context);

        Assert.Single(results);
        Assert.Equal(3L, results[0]["COUNT(*)"].AsInt64());
    }

    /// <summary>
    /// Verifies that a parallel global aggregation with SUM and AVG produces
    /// correct merged results.
    /// </summary>
    [Fact]
    public async Task ParallelAggregate_GlobalSumAndAvg()
    {
        MockOperator source = CreateMockOperator(["price"],
            [10f],
            [20f],
            [30f]);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(
                    new SumFunction(),
                    [new ColumnReference("price")],
                    "SUM(price)"),
                new AggregateColumn(
                    new AvgFunction(),
                    [new ColumnReference("price")],
                    "AVG(price)"),
            ]);

        ExecutionContext context = CreateParallelContext();
        List<Row> results = await CollectAsync(groupBy, context);

        Assert.Single(results);
        Assert.Equal(60f, results[0]["SUM(price)"].AsFloat32());
        Assert.Equal(20.0, results[0]["AVG(price)"].AsFloat64());
    }

    /// <summary>
    /// Verifies that a parallel single-key GROUP BY produces correct groups
    /// with COUNT and SUM.
    /// </summary>
    [Fact]
    public async Task ParallelAggregate_SingleKey_GroupBy()
    {
        MockOperator source = CreateMockOperator(["category", "value"],
            ["A", 1f],
            ["B", 2f],
            ["A", 3f],
            ["B", 4f],
            ["A", 5f]);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new ColumnReference("category")],
            aggregateColumns:
            [
                new AggregateColumn(new CountFunction(), [], "COUNT(*)", IsCountStar: true),
                new AggregateColumn(
                    new SumFunction(),
                    [new ColumnReference("value")],
                    "SUM(value)"),
            ]);

        ExecutionContext context = CreateParallelContext();
        List<Row> results = await CollectAsync(groupBy, context);

        Assert.Equal(2, results.Count);

        Row groupA = results.First(row => row["category"].AsString() == "A");
        Row groupB = results.First(row => row["category"].AsString() == "B");

        Assert.Equal(3L, groupA["COUNT(*)"].AsInt64());
        Assert.Equal(9f, groupA["SUM(value)"].AsFloat32());

        Assert.Equal(2L, groupB["COUNT(*)"].AsInt64());
        Assert.Equal(6f, groupB["SUM(value)"].AsFloat32());
    }

    /// <summary>
    /// Verifies that a parallel composite-key GROUP BY produces correct groups.
    /// </summary>
    [Fact]
    public async Task ParallelAggregate_CompositeKey_GroupBy()
    {
        MockOperator source = CreateMockOperator(["dept", "status", "amount"],
            ["X", "active", 100f],
            ["X", "inactive", 200f],
            ["X", "active", 300f],
            ["Y", "active", 50f]);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new ColumnReference("dept"), new ColumnReference("status")],
            aggregateColumns:
            [
                new AggregateColumn(new CountFunction(), [], "COUNT(*)", IsCountStar: true),
                new AggregateColumn(
                    new SumFunction(),
                    [new ColumnReference("amount")],
                    "SUM(amount)"),
            ]);

        ExecutionContext context = CreateParallelContext();
        List<Row> results = await CollectAsync(groupBy, context);

        Assert.Equal(3, results.Count);

        Row groupXActive = results.First(row =>
            row["dept"].AsString() == "X" && row["status"].AsString() == "active");
        Assert.Equal(2L, groupXActive["COUNT(*)"].AsInt64());
        Assert.Equal(400f, groupXActive["SUM(amount)"].AsFloat32());

        Row groupXInactive = results.First(row =>
            row["dept"].AsString() == "X" && row["status"].AsString() == "inactive");
        Assert.Equal(1L, groupXInactive["COUNT(*)"].AsInt64());
        Assert.Equal(200f, groupXInactive["SUM(amount)"].AsFloat32());
    }

    /// <summary>
    /// Verifies that a parallel GROUP BY with MIN and MAX produces correct results.
    /// </summary>
    [Fact]
    public async Task ParallelAggregate_MinMax()
    {
        MockOperator source = CreateMockOperator(["group", "val"],
            ["G1", 5f],
            ["G1", 15f],
            ["G1", 10f]);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new ColumnReference("group")],
            aggregateColumns:
            [
                new AggregateColumn(
                    new MinFunction(),
                    [new ColumnReference("val")],
                    "MIN(val)"),
                new AggregateColumn(
                    new MaxFunction(),
                    [new ColumnReference("val")],
                    "MAX(val)"),
            ]);

        ExecutionContext context = CreateParallelContext();
        List<Row> results = await CollectAsync(groupBy, context);

        Assert.Single(results);
        Assert.Equal(5f, results[0]["MIN(val)"].AsFloat32());
        Assert.Equal(15f, results[0]["MAX(val)"].AsFloat32());
    }

    /// <summary>
    /// Verifies that a parallel GROUP BY with empty input and grouping keys
    /// returns no rows.
    /// </summary>
    [Fact]
    public async Task ParallelAggregate_EmptyInput_WithGroupBy_ReturnsNoRows()
    {
        MockOperator source = CreateMockOperator(XColumns);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new ColumnReference("x")],
            aggregateColumns:
            [
                new AggregateColumn(new CountFunction(), [], "COUNT(*)", IsCountStar: true),
            ]);

        ExecutionContext context = CreateParallelContext();
        List<Row> results = await CollectAsync(groupBy, context);

        Assert.Empty(results);
    }

    /// <summary>
    /// Verifies that a parallel global aggregation on empty input returns one row
    /// with COUNT(*) = 0.
    /// </summary>
    [Fact]
    public async Task ParallelAggregate_GlobalEmptyInput_ReturnsOneRow()
    {
        MockOperator source = CreateMockOperator(XColumns);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(new CountFunction(), [], "COUNT(*)", IsCountStar: true),
            ]);

        ExecutionContext context = CreateParallelContext();
        List<Row> results = await CollectAsync(groupBy, context);

        Assert.Single(results);
        Assert.Equal(0L, results[0]["COUNT(*)"].AsInt64());
    }

    /// <summary>
    /// Verifies that null group keys create a distinct group in parallel mode.
    /// </summary>
    [Fact]
    public async Task ParallelAggregate_NullGroupKey_CreatesGroup()
    {
        MockOperator source = CreateMockOperator(["category", "value"],
            [null, 1f],
            ["A", 2f],
            [null, 3f]);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new ColumnReference("category")],
            aggregateColumns:
            [
                new AggregateColumn(new CountFunction(), [], "COUNT(*)", IsCountStar: true),
            ]);

        ExecutionContext context = CreateParallelContext();
        List<Row> results = await CollectAsync(groupBy, context);

        Assert.Equal(2, results.Count);

        Row nullGroup = results.First(row => row["category"].IsNull);
        Assert.Equal(2L, nullGroup["COUNT(*)"].AsInt64());

        Row groupA = results.First(row => !row["category"].IsNull);
        Assert.Equal(1L, groupA["COUNT(*)"].AsInt64());
    }

    /// <summary>
    /// Verifies correctness with higher parallelism (4 workers) and many groups.
    /// </summary>
    [Fact]
    public async Task ParallelAggregate_FourWorkers_ManyGroups()
    {
        // 100 rows across 10 groups.
        object?[][] rows = Enumerable.Range(0, 100)
            .Select(i => new object?[] { $"G{i % 10}", (float)i })
            .ToArray();

        MockOperator source = CreateMockOperator(["group", "value"], rows: rows);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new ColumnReference("group")],
            aggregateColumns:
            [
                new AggregateColumn(new CountFunction(), [], "COUNT(*)", IsCountStar: true),
                new AggregateColumn(
                    new SumFunction(),
                    [new ColumnReference("value")],
                    "SUM(value)"),
            ]);

        ExecutionContext context = CreateParallelContext(degreeOfParallelism: 4);
        List<Row> results = await CollectAsync(groupBy, context);

        Assert.Equal(10, results.Count);

        // Each group has exactly 10 rows.
        foreach (Row result in results)
        {
            Assert.Equal(10L, result["COUNT(*)"].AsInt64());
        }

        // Total sum of all values: 0+1+...+99 = 4950.
        float totalSum = results.Sum(r => r["SUM(value)"].AsFloat32());
        Assert.Equal(4950f, totalSum);
    }

    /// <summary>
    /// Verifies that the parallelism budget is properly released after execution.
    /// </summary>
    [Fact]
    public async Task ParallelAggregate_ReleasesBudget()
    {
        MockOperator source = CreateMockOperator(XColumns,
            [1f],
            [2f]);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(new CountFunction(), [], "COUNT(*)", IsCountStar: true),
            ]);

        ParallelismBudget budget = new(4);
        Pool pool = GetService<Pool>();
        ExecutionContext context = new(
            CancellationToken.None,
            FunctionRegistry.CreateDefault(),
            CreateCatalog(),
            new LocalBufferPool(),
            pool)
        {
            DegreeOfParallelism = 2,
            ParallelismBudget = budget,
        };

        List<Row> results = await CollectAsync(groupBy, context);

        Assert.Single(results);

        // Budget should be fully released after execution completes.
        Assert.Equal(4, budget.AvailableWorkers);

        budget.Dispose();
    }

    /// <summary>
    /// Verifies that the join parallel probe releases budget slots after execution.
    /// </summary>
    [Fact]
    public async Task ParallelProbe_ReleasesBudget()
    {
        MockOperator left = CreateMockOperator(LeftNameColumns,
            [1f, "Alice"]);

        MockOperator right = CreateMockOperator(RightScoreColumns,
            [1f, 95f]);

        JoinOperator join = new(left, right, JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("l", "id"),
                BinaryOperator.Equal,
                new ColumnReference("r", "id")));

        ParallelismBudget budget = new(4);
        Pool pool = GetService<Pool>();
        ExecutionContext context = new(
            CancellationToken.None,
            FunctionRegistry.CreateDefault(),
            CreateCatalog(),
            new LocalBufferPool(),
            pool)
        {
            DegreeOfParallelism = 2,
            ParallelismBudget = budget,
        };

        List<Row> rows = await CollectAsync(join, context);

        Assert.Single(rows);

        // Budget should be fully released after execution completes.
        Assert.Equal(4, budget.AvailableWorkers);

        budget.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Partitioned Hash Aggregate — routing correctness
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that the partitioned fan-out correctly assigns composite GROUP BY
    /// keys to a single worker so that every group accumulates its full count.
    /// With round-robin routing a group's rows would be split across workers and
    /// the post-merge would collapse them; with partitioned routing the merge phase
    /// is skipped, so any routing error that splits a group would produce incorrect
    /// per-group counts instead of the correct value of 10.
    /// </summary>
    [Fact]
    public async Task ParallelAggregate_PartitionedRouting_CompositeKey_EachGroupFullyAccumulated()
    {
        // 500 rows: 5 departments × 10 statuses = 50 distinct (dept, status) groups,
        // each with exactly 10 rows. Groups are constructed so both key columns vary
        // independently, giving 50 distinct composite keys with no aliasing.
        object?[][] inputRows = Enumerable.Range(0, 500)
            .Select(i => new object?[] { $"D{i / 100}", $"S{(i % 100) / 10}", 1f })
            .ToArray();

        MockOperator source = CreateMockOperator(["dept", "status", "value"], rows: inputRows);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions:
            [
                new ColumnReference("dept"),
                new ColumnReference("status"),
            ],
            aggregateColumns:
            [
                new AggregateColumn(new CountFunction(), [], "COUNT(*)", IsCountStar: true),
                new AggregateColumn(
                    new SumFunction(),
                    [new ColumnReference("value")],
                    "SUM(value)"),
            ]);

        ExecutionContext context = CreateParallelContext(degreeOfParallelism: 4);
        List<Row> results = await CollectAsync(groupBy, context);

        Assert.Equal(50, results.Count);

        // Every group must have exactly 10 rows. If any group were split across
        // workers (routing bug) its unseen rows would form a separate group entry
        // or simply not be merged — both would produce a count other than 10.
        foreach (Row result in results)
        {
            Assert.Equal(10L, result["COUNT(*)"].AsInt64());
            Assert.Equal(10f, result["SUM(value)"].AsFloat32());
        }
    }

    /// <summary>
    /// Verifies that a single-key GROUP BY with far more distinct groups than
    /// workers (100 groups, 4 workers) produces exact counts for every group.
    /// This stresses the modulo-routing distribution and confirms that the
    /// routing hash used by the feeder and the accumulation hash used by each
    /// worker agree on key placement.
    /// </summary>
    [Fact]
    public async Task ParallelAggregate_PartitionedRouting_SingleKey_MoreGroupsThanWorkers()
    {
        // 400 rows: 100 distinct groups, each with exactly 4 rows.
        object?[][] inputRows = Enumerable.Range(0, 400)
            .Select(i => new object?[] { $"G{i % 100}", (float)i })
            .ToArray();

        MockOperator source = CreateMockOperator(["group", "value"], rows: inputRows);

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [new ColumnReference("group")],
            aggregateColumns:
            [
                new AggregateColumn(new CountFunction(), [], "COUNT(*)", IsCountStar: true),
                new AggregateColumn(
                    new SumFunction(),
                    [new ColumnReference("value")],
                    "SUM(value)"),
            ]);

        ExecutionContext context = CreateParallelContext(degreeOfParallelism: 4);
        List<Row> results = await CollectAsync(groupBy, context);

        Assert.Equal(100, results.Count);

        // Each group Gi spans rows i, i+100, i+200, i+300.
        foreach (Row result in results)
        {
            string groupName = result["group"].AsString();
            int groupIndex = int.Parse(groupName.AsSpan(1));

            long expectedCount = 4L;
            float expectedSum = groupIndex + (groupIndex + 100) + (groupIndex + 200) + (groupIndex + 300);

            Assert.Equal(expectedCount, result["COUNT(*)"].AsInt64());
            Assert.Equal(expectedSum, result["SUM(value)"].AsFloat32());
        }
    }
}
