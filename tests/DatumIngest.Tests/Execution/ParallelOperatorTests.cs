using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Functions.Aggregates;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
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
public sealed class ParallelOperatorTests
{
    private static ExecutionContext CreateParallelContext(int degreeOfParallelism = 2)
    {
        return new ExecutionContext(
            CancellationToken.None,
            FunctionRegistry.CreateDefault(),
            new TableCatalog(),
            new LocalBufferPool())
        {
            DegreeOfParallelism = degreeOfParallelism,
            ParallelismBudget = new ParallelismBudget(degreeOfParallelism),
        };
    }

    private static Row MakeRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = columns.Select(c => c.Name).ToArray();
        DataValue[] values = columns.Select(c => c.Value).ToArray();
        return new Row(names, values);
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
        MockOperator left = new(
            MakeRow(("l.id", DataValue.FromFloat32(1f)), ("l.name", DataValue.FromString("Alice"))),
            MakeRow(("l.id", DataValue.FromFloat32(2f)), ("l.name", DataValue.FromString("Bob"))),
            MakeRow(("l.id", DataValue.FromFloat32(3f)), ("l.name", DataValue.FromString("Charlie"))));

        MockOperator right = new(
            MakeRow(("r.id", DataValue.FromFloat32(1f)), ("r.score", DataValue.FromFloat32(95f))),
            MakeRow(("r.id", DataValue.FromFloat32(3f)), ("r.score", DataValue.FromFloat32(87f))));

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
        MockOperator left = new(
            MakeRow(("l.id", DataValue.FromFloat32(1f)), ("l.name", DataValue.FromString("Alice"))),
            MakeRow(("l.id", DataValue.FromFloat32(2f)), ("l.name", DataValue.FromString("Bob"))),
            MakeRow(("l.id", DataValue.FromFloat32(3f)), ("l.name", DataValue.FromString("Charlie"))));

        MockOperator right = new(
            MakeRow(("r.id", DataValue.FromFloat32(1f)), ("r.score", DataValue.FromFloat32(95f))));

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
        MockOperator left = new(
            MakeRow(("l.id", DataValue.FromFloat32(1f)), ("l.name", DataValue.FromString("Alice"))),
            MakeRow(("l.id", DataValue.FromFloat32(1f)), ("l.name", DataValue.FromString("Bob"))));

        MockOperator right = new(
            MakeRow(("r.id", DataValue.FromFloat32(1f)), ("r.val", DataValue.FromString("X"))),
            MakeRow(("r.id", DataValue.FromFloat32(1f)), ("r.val", DataValue.FromString("Y"))));

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
        MockOperator left = new(
            MakeRow(("l.id", DataValue.Null(DataKind.Float32)), ("l.name", DataValue.FromString("Null"))),
            MakeRow(("l.id", DataValue.FromFloat32(1f)), ("l.name", DataValue.FromString("Alice"))));

        MockOperator right = new(
            MakeRow(("r.id", DataValue.Null(DataKind.Float32)), ("r.score", DataValue.FromFloat32(0f))),
            MakeRow(("r.id", DataValue.FromFloat32(1f)), ("r.score", DataValue.FromFloat32(95f))));

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
        MockOperator left = new(
            MakeRow(("l.a", DataValue.FromFloat32(1f)), ("l.b", DataValue.FromString("X")), ("l.val", DataValue.FromFloat32(100f))),
            MakeRow(("l.a", DataValue.FromFloat32(1f)), ("l.b", DataValue.FromString("Y")), ("l.val", DataValue.FromFloat32(200f))),
            MakeRow(("l.a", DataValue.FromFloat32(2f)), ("l.b", DataValue.FromString("X")), ("l.val", DataValue.FromFloat32(300f))));

        MockOperator right = new(
            MakeRow(("r.a", DataValue.FromFloat32(1f)), ("r.b", DataValue.FromString("X")), ("r.val", DataValue.FromFloat32(10f))),
            MakeRow(("r.a", DataValue.FromFloat32(2f)), ("r.b", DataValue.FromString("Z")), ("r.val", DataValue.FromFloat32(20f))));

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
        Row[] leftRows = Enumerable.Range(0, 20)
            .Select(i => MakeRow(
                ("l.id", DataValue.FromFloat32(i)),
                ("l.val", DataValue.FromFloat32(i * 10f))))
            .ToArray();

        Row[] rightRows = Enumerable.Range(0, 20)
            .Where(i => i % 3 == 0) // 0, 3, 6, 9, 12, 15, 18
            .Select(i => MakeRow(
                ("r.id", DataValue.FromFloat32(i)),
                ("r.name", DataValue.FromString($"R{i}"))))
            .ToArray();

        MockOperator left = new(leftRows);
        MockOperator right = new(rightRows);

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
        MockOperator source = new(
            MakeRow(("x", DataValue.FromFloat32(1f))),
            MakeRow(("x", DataValue.FromFloat32(2f))),
            MakeRow(("x", DataValue.FromFloat32(3f))));

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
        Assert.Equal(3f, results[0]["COUNT(*)"].AsFloat32());
    }

    /// <summary>
    /// Verifies that a parallel global aggregation with SUM and AVG produces
    /// correct merged results.
    /// </summary>
    [Fact]
    public async Task ParallelAggregate_GlobalSumAndAvg()
    {
        MockOperator source = new(
            MakeRow(("price", DataValue.FromFloat32(10f))),
            MakeRow(("price", DataValue.FromFloat32(20f))),
            MakeRow(("price", DataValue.FromFloat32(30f))));

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
        Assert.Equal(20f, results[0]["AVG(price)"].AsFloat32());
    }

    /// <summary>
    /// Verifies that a parallel single-key GROUP BY produces correct groups
    /// with COUNT and SUM.
    /// </summary>
    [Fact]
    public async Task ParallelAggregate_SingleKey_GroupBy()
    {
        MockOperator source = new(
            MakeRow(("category", DataValue.FromString("A")), ("value", DataValue.FromFloat32(1f))),
            MakeRow(("category", DataValue.FromString("B")), ("value", DataValue.FromFloat32(2f))),
            MakeRow(("category", DataValue.FromString("A")), ("value", DataValue.FromFloat32(3f))),
            MakeRow(("category", DataValue.FromString("B")), ("value", DataValue.FromFloat32(4f))),
            MakeRow(("category", DataValue.FromString("A")), ("value", DataValue.FromFloat32(5f))));

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

        Assert.Equal(3f, groupA["COUNT(*)"].AsFloat32());
        Assert.Equal(9f, groupA["SUM(value)"].AsFloat32());

        Assert.Equal(2f, groupB["COUNT(*)"].AsFloat32());
        Assert.Equal(6f, groupB["SUM(value)"].AsFloat32());
    }

    /// <summary>
    /// Verifies that a parallel composite-key GROUP BY produces correct groups.
    /// </summary>
    [Fact]
    public async Task ParallelAggregate_CompositeKey_GroupBy()
    {
        MockOperator source = new(
            MakeRow(("dept", DataValue.FromString("X")), ("status", DataValue.FromString("active")), ("amount", DataValue.FromFloat32(100f))),
            MakeRow(("dept", DataValue.FromString("X")), ("status", DataValue.FromString("inactive")), ("amount", DataValue.FromFloat32(200f))),
            MakeRow(("dept", DataValue.FromString("X")), ("status", DataValue.FromString("active")), ("amount", DataValue.FromFloat32(300f))),
            MakeRow(("dept", DataValue.FromString("Y")), ("status", DataValue.FromString("active")), ("amount", DataValue.FromFloat32(50f))));

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
        Assert.Equal(2f, groupXActive["COUNT(*)"].AsFloat32());
        Assert.Equal(400f, groupXActive["SUM(amount)"].AsFloat32());

        Row groupXInactive = results.First(row =>
            row["dept"].AsString() == "X" && row["status"].AsString() == "inactive");
        Assert.Equal(1f, groupXInactive["COUNT(*)"].AsFloat32());
        Assert.Equal(200f, groupXInactive["SUM(amount)"].AsFloat32());
    }

    /// <summary>
    /// Verifies that a parallel GROUP BY with MIN and MAX produces correct results.
    /// </summary>
    [Fact]
    public async Task ParallelAggregate_MinMax()
    {
        MockOperator source = new(
            MakeRow(("group", DataValue.FromString("G1")), ("val", DataValue.FromFloat32(5f))),
            MakeRow(("group", DataValue.FromString("G1")), ("val", DataValue.FromFloat32(15f))),
            MakeRow(("group", DataValue.FromString("G1")), ("val", DataValue.FromFloat32(10f))));

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
        MockOperator source = new();

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
        MockOperator source = new();

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
        Assert.Equal(0f, results[0]["COUNT(*)"].AsFloat32());
    }

    /// <summary>
    /// Verifies that null group keys create a distinct group in parallel mode.
    /// </summary>
    [Fact]
    public async Task ParallelAggregate_NullGroupKey_CreatesGroup()
    {
        MockOperator source = new(
            MakeRow(("category", DataValue.Null(DataKind.String)), ("value", DataValue.FromFloat32(1f))),
            MakeRow(("category", DataValue.FromString("A")), ("value", DataValue.FromFloat32(2f))),
            MakeRow(("category", DataValue.Null(DataKind.String)), ("value", DataValue.FromFloat32(3f))));

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
        Assert.Equal(2f, nullGroup["COUNT(*)"].AsFloat32());

        Row groupA = results.First(row => !row["category"].IsNull);
        Assert.Equal(1f, groupA["COUNT(*)"].AsFloat32());
    }

    /// <summary>
    /// Verifies correctness with higher parallelism (4 workers) and many groups.
    /// </summary>
    [Fact]
    public async Task ParallelAggregate_FourWorkers_ManyGroups()
    {
        // 100 rows across 10 groups.
        Row[] rows = Enumerable.Range(0, 100)
            .Select(i => MakeRow(
                ("group", DataValue.FromString($"G{i % 10}")),
                ("value", DataValue.FromFloat32(i))))
            .ToArray();

        MockOperator source = new(rows);

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
            Assert.Equal(10f, result["COUNT(*)"].AsFloat32());
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
        MockOperator source = new(
            MakeRow(("x", DataValue.FromFloat32(1f))),
            MakeRow(("x", DataValue.FromFloat32(2f))));

        GroupByOperator groupBy = new(
            source,
            groupByExpressions: [],
            aggregateColumns:
            [
                new AggregateColumn(new CountFunction(), [], "COUNT(*)", IsCountStar: true),
            ]);

        ParallelismBudget budget = new(4);
        ExecutionContext context = new(
            CancellationToken.None,
            FunctionRegistry.CreateDefault(),
            new TableCatalog(), new LocalBufferPool())
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
        MockOperator left = new(
            MakeRow(("l.id", DataValue.FromFloat32(1f)), ("l.name", DataValue.FromString("Alice"))));

        MockOperator right = new(
            MakeRow(("r.id", DataValue.FromFloat32(1f)), ("r.score", DataValue.FromFloat32(95f))));

        JoinOperator join = new(left, right, JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("l", "id"),
                BinaryOperator.Equal,
                new ColumnReference("r", "id")));

        ParallelismBudget budget = new(4);
        ExecutionContext context = new(
            CancellationToken.None,
            FunctionRegistry.CreateDefault(),
            new TableCatalog(), new LocalBufferPool())
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
        Row[] inputRows = Enumerable.Range(0, 500)
            .Select(i => MakeRow(
                ("dept", DataValue.FromString($"D{i / 100}")),
                ("status", DataValue.FromString($"S{(i % 100) / 10}")),
                ("value", DataValue.FromFloat32(1f))))
            .ToArray();

        MockOperator source = new(inputRows);

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
            Assert.Equal(10f, result["COUNT(*)"].AsFloat32());
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
        Row[] inputRows = Enumerable.Range(0, 400)
            .Select(i => MakeRow(
                ("group", DataValue.FromString($"G{i % 100}")),
                ("value", DataValue.FromFloat32(i))))
            .ToArray();

        MockOperator source = new(inputRows);

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

            float expectedCount = 4f;
            float expectedSum = groupIndex + (groupIndex + 100) + (groupIndex + 200) + (groupIndex + 300);

            Assert.Equal(expectedCount, result["COUNT(*)"].AsFloat32());
            Assert.Equal(expectedSum, result["SUM(value)"].AsFloat32());
        }
    }
}
