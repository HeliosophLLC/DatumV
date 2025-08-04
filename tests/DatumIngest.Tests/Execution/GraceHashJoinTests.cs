using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for the Grace hash join executor integrated through <see cref="JoinOperator"/>.
/// Each test uses a memory budget small enough to force spilling, then verifies
/// the results match the expected output for each join type.
/// </summary>
public sealed class GraceHashJoinTests
{
    /// <summary>
    /// Memory budget small enough to force spilling even for a few rows.
    /// Each row is ~40 bytes overhead per DataValue + dictionary entry overhead (~48 bytes).
    /// With 2 columns per row, ~176 bytes/row. A budget of 256 bytes should force spills quickly.
    /// </summary>
    private const long TinyBudget = 256;

    /// <summary>
    /// Verifies that INNER JOIN with spilling produces correct matched rows.
    /// </summary>
    [Fact]
    public async Task InnerJoin_WithSpill_ProducesCorrectMatches()
    {
        MockOperator left = new(
            MakeRow(("l.id", DataValue.FromScalar(1f)), ("l.name", DataValue.FromString("Alice"))),
            MakeRow(("l.id", DataValue.FromScalar(2f)), ("l.name", DataValue.FromString("Bob"))),
            MakeRow(("l.id", DataValue.FromScalar(3f)), ("l.name", DataValue.FromString("Charlie"))));

        MockOperator right = new(
            MakeRow(("r.id", DataValue.FromScalar(1f)), ("r.score", DataValue.FromScalar(95f))),
            MakeRow(("r.id", DataValue.FromScalar(3f)), ("r.score", DataValue.FromScalar(87f))));

        JoinOperator join = new(left, right, JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("l", "id"),
                BinaryOperator.Equal,
                new ColumnReference("r", "id")));

        List<Row> rows = await CollectAsync(join, CreateContext(TinyBudget));

        Assert.Equal(2, rows.Count);

        Row alice = rows.First(row => row["l.name"].AsString() == "Alice");
        Assert.Equal(95f, alice["r.score"].AsScalar());

        Row charlie = rows.First(row => row["l.name"].AsString() == "Charlie");
        Assert.Equal(87f, charlie["r.score"].AsScalar());
    }

    /// <summary>
    /// Verifies that LEFT JOIN with spilling includes unmatched left rows with null right columns.
    /// </summary>
    [Fact]
    public async Task LeftJoin_WithSpill_IncludesUnmatchedLeft()
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

        List<Row> rows = await CollectAsync(join, CreateContext(TinyBudget));

        Assert.Equal(2, rows.Count);

        Row alice = rows.First(row => row["l.name"].AsString() == "Alice");
        Assert.Equal(95f, alice["r.score"].AsScalar());

        Row bob = rows.First(row => row["l.name"].AsString() == "Bob");
        Assert.True(bob["r.score"].IsNull);
    }

    /// <summary>
    /// Verifies that RIGHT JOIN with spilling includes unmatched right rows with null left columns.
    /// </summary>
    [Fact]
    public async Task RightJoin_WithSpill_IncludesUnmatchedRight()
    {
        MockOperator left = new(
            MakeRow(("l.id", DataValue.FromScalar(1f)), ("l.name", DataValue.FromString("Alice"))));

        MockOperator right = new(
            MakeRow(("r.id", DataValue.FromScalar(1f)), ("r.score", DataValue.FromScalar(95f))),
            MakeRow(("r.id", DataValue.FromScalar(2f)), ("r.score", DataValue.FromScalar(70f))));

        JoinOperator join = new(left, right, JoinType.Right,
            new BinaryExpression(
                new ColumnReference("l", "id"),
                BinaryOperator.Equal,
                new ColumnReference("r", "id")));

        List<Row> rows = await CollectAsync(join, CreateContext(TinyBudget));

        Assert.Equal(2, rows.Count);

        Row matched = rows.First(row => !row["l.name"].IsNull);
        Assert.Equal("Alice", matched["l.name"].AsString());
        Assert.Equal(95f, matched["r.score"].AsScalar());

        Row unmatched = rows.First(row => row["l.name"].IsNull);
        Assert.Equal(70f, unmatched["r.score"].AsScalar());
    }

    /// <summary>
    /// Verifies that FULL OUTER JOIN with spilling includes both unmatched left and right rows.
    /// </summary>
    [Fact]
    public async Task FullOuterJoin_WithSpill_IncludesBothUnmatched()
    {
        MockOperator left = new(
            MakeRow(("l.id", DataValue.FromScalar(1f)), ("l.name", DataValue.FromString("Alice"))),
            MakeRow(("l.id", DataValue.FromScalar(2f)), ("l.name", DataValue.FromString("Bob"))));

        MockOperator right = new(
            MakeRow(("r.id", DataValue.FromScalar(1f)), ("r.score", DataValue.FromScalar(95f))),
            MakeRow(("r.id", DataValue.FromScalar(3f)), ("r.score", DataValue.FromScalar(60f))));

        JoinOperator join = new(left, right, JoinType.FullOuter,
            new BinaryExpression(
                new ColumnReference("l", "id"),
                BinaryOperator.Equal,
                new ColumnReference("r", "id")));

        List<Row> rows = await CollectAsync(join, CreateContext(TinyBudget));

        Assert.Equal(3, rows.Count);

        // Matched: Alice + 95
        Row alice = rows.First(row => !row["l.name"].IsNull && row["l.name"].AsString() == "Alice");
        Assert.Equal(95f, alice["r.score"].AsScalar());

        // Unmatched left: Bob (no right match)
        Row bob = rows.First(row => !row["l.name"].IsNull && row["l.name"].AsString() == "Bob");
        Assert.True(bob["r.score"].IsNull);

        // Unmatched right: score 60 (no left match)
        Row orphan = rows.First(row => row["l.name"].IsNull);
        Assert.Equal(60f, orphan["r.score"].AsScalar());
    }

    /// <summary>
    /// Verifies that null keys never match, even with spilling.
    /// </summary>
    [Fact]
    public async Task NullKeys_NeverMatch_WithSpill()
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

        List<Row> rows = await CollectAsync(join, CreateContext(TinyBudget));

        Assert.Empty(rows);
    }

    /// <summary>
    /// Verifies that composite keys (multi-column join) work with spilling.
    /// </summary>
    [Fact]
    public async Task CompositeKey_InnerJoin_WithSpill()
    {
        MockOperator left = new(
            MakeRow(("l.a", DataValue.FromScalar(1f)), ("l.b", DataValue.FromString("x")), ("l.val", DataValue.FromScalar(10f))),
            MakeRow(("l.a", DataValue.FromScalar(1f)), ("l.b", DataValue.FromString("y")), ("l.val", DataValue.FromScalar(20f))));

        MockOperator right = new(
            MakeRow(("r.a", DataValue.FromScalar(1f)), ("r.b", DataValue.FromString("x")), ("r.val", DataValue.FromScalar(100f))),
            MakeRow(("r.a", DataValue.FromScalar(2f)), ("r.b", DataValue.FromString("x")), ("r.val", DataValue.FromScalar(200f))));

        // Join on l.a = r.a AND l.b = r.b
        Expression onCondition = new BinaryExpression(
            new BinaryExpression(
                new ColumnReference("l", "a"),
                BinaryOperator.Equal,
                new ColumnReference("r", "a")),
            BinaryOperator.And,
            new BinaryExpression(
                new ColumnReference("l", "b"),
                BinaryOperator.Equal,
                new ColumnReference("r", "b")));

        JoinOperator join = new(left, right, JoinType.Inner, onCondition);

        List<Row> rows = await CollectAsync(join, CreateContext(TinyBudget));

        Assert.Single(rows);
        Assert.Equal(10f, rows[0]["l.val"].AsScalar());
        Assert.Equal(100f, rows[0]["r.val"].AsScalar());
    }

    /// <summary>
    /// Verifies that a larger dataset with spilling produces the same results as in-memory join.
    /// </summary>
    [Fact]
    public async Task LargerDataset_SpillMatchesInMemory()
    {
        // Generate 100 left rows and 80 right rows with overlapping keys.
        Row[] leftRows = Enumerable.Range(0, 100)
            .Select(index => MakeRow(
                ("l.id", DataValue.FromScalar((float)index)),
                ("l.data", DataValue.FromString($"left_{index}"))))
            .ToArray();

        Row[] rightRows = Enumerable.Range(20, 80)
            .Select(index => MakeRow(
                ("r.id", DataValue.FromScalar((float)index)),
                ("r.data", DataValue.FromString($"right_{index}"))))
            .ToArray();

        Expression onCondition = new BinaryExpression(
            new ColumnReference("l", "id"),
            BinaryOperator.Equal,
            new ColumnReference("r", "id"));

        // Run in-memory (no budget).
        JoinOperator inMemoryJoin = new(new MockOperator(leftRows), new MockOperator(rightRows), JoinType.Inner, onCondition);
        List<Row> inMemoryResults = await CollectAsync(inMemoryJoin);

        // Run with spilling (tiny budget).
        JoinOperator spillJoin = new(new MockOperator(leftRows), new MockOperator(rightRows), JoinType.Inner, onCondition);
        List<Row> spillResults = await CollectAsync(spillJoin, CreateContext(TinyBudget));

        Assert.Equal(inMemoryResults.Count, spillResults.Count);
        Assert.Equal(80, spillResults.Count);

        // Verify all expected keys are present.
        HashSet<float> expectedKeys = Enumerable.Range(20, 80).Select(index => (float)index).ToHashSet();
        HashSet<float> actualKeys = spillResults.Select(row => row["l.id"].AsScalar()).ToHashSet();
        Assert.Equal(expectedKeys, actualKeys);
    }

    /// <summary>
    /// Verifies that duplicates on the build side produce multiple matches per probe row.
    /// </summary>
    [Fact]
    public async Task BuildDuplicates_ProduceMultipleMatches_WithSpill()
    {
        MockOperator left = new(
            MakeRow(("l.id", DataValue.FromScalar(1f)), ("l.label", DataValue.FromString("probe"))));

        MockOperator right = new(
            MakeRow(("r.id", DataValue.FromScalar(1f)), ("r.tag", DataValue.FromString("a"))),
            MakeRow(("r.id", DataValue.FromScalar(1f)), ("r.tag", DataValue.FromString("b"))),
            MakeRow(("r.id", DataValue.FromScalar(1f)), ("r.tag", DataValue.FromString("c"))));

        JoinOperator join = new(left, right, JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("l", "id"),
                BinaryOperator.Equal,
                new ColumnReference("r", "id")));

        List<Row> rows = await CollectAsync(join, CreateContext(TinyBudget));

        Assert.Equal(3, rows.Count);
        HashSet<string> tags = rows.Select(row => row["r.tag"].AsString()).ToHashSet();
        Assert.Equal(new HashSet<string> { "a", "b", "c" }, tags);
    }

    /// <summary>
    /// Verifies that empty build side produces no matches for INNER JOIN.
    /// </summary>
    [Fact]
    public async Task EmptyBuildSide_InnerJoin_ProducesNoResults()
    {
        MockOperator left = new(
            MakeRow(("l.id", DataValue.FromScalar(1f)), ("l.name", DataValue.FromString("Alice"))));

        MockOperator right = new();

        JoinOperator join = new(left, right, JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("l", "id"),
                BinaryOperator.Equal,
                new ColumnReference("r", "id")));

        List<Row> rows = await CollectAsync(join, CreateContext(TinyBudget));

        Assert.Empty(rows);
    }

    /// <summary>
    /// Verifies that empty build side with LEFT JOIN emits unmatched probe rows.
    /// </summary>
    [Fact]
    public async Task EmptyBuildSide_LeftJoin_EmitsUnmatchedProbe()
    {
        MockOperator left = new(
            MakeRow(("l.id", DataValue.FromScalar(1f)), ("l.name", DataValue.FromString("Alice"))));

        MockOperator right = new();

        JoinOperator join = new(left, right, JoinType.Left,
            new BinaryExpression(
                new ColumnReference("l", "id"),
                BinaryOperator.Equal,
                new ColumnReference("r", "id")));

        List<Row> rows = await CollectAsync(join, CreateContext(TinyBudget));

        Assert.Single(rows);
        Assert.Equal("Alice", rows[0]["l.name"].AsString());
    }

    /// <summary>
    /// Verifies the result is identical with and without a memory budget for a moderate dataset.
    /// </summary>
    [Fact]
    public async Task LeftJoin_SpillMatchesInMemory()
    {
        Row[] leftRows = Enumerable.Range(0, 50)
            .Select(index => MakeRow(
                ("l.id", DataValue.FromScalar((float)index)),
                ("l.data", DataValue.FromString($"left_{index}"))))
            .ToArray();

        Row[] rightRows = Enumerable.Range(25, 25)
            .Select(index => MakeRow(
                ("r.id", DataValue.FromScalar((float)index)),
                ("r.data", DataValue.FromString($"right_{index}"))))
            .ToArray();

        Expression onCondition = new BinaryExpression(
            new ColumnReference("l", "id"),
            BinaryOperator.Equal,
            new ColumnReference("r", "id"));

        JoinOperator inMemoryJoin = new(new MockOperator(leftRows), new MockOperator(rightRows), JoinType.Left, onCondition);
        List<Row> inMemoryResults = await CollectAsync(inMemoryJoin);

        JoinOperator spillJoin = new(new MockOperator(leftRows), new MockOperator(rightRows), JoinType.Left, onCondition);
        List<Row> spillResults = await CollectAsync(spillJoin, CreateContext(TinyBudget));

        // LEFT JOIN: all left rows should appear.
        Assert.Equal(50, inMemoryResults.Count);
        Assert.Equal(50, spillResults.Count);

        // Matched and unmatched counts should be the same.
        int inMemoryMatched = inMemoryResults.Count(row => !row["r.data"].IsNull);
        int spillMatched = spillResults.Count(row => !row["r.data"].IsNull);
        Assert.Equal(25, inMemoryMatched);
        Assert.Equal(25, spillMatched);
    }

    /// <summary>
    /// The hybrid streaming probe must allow a LIMIT to terminate the join early
    /// without consuming all probe rows. This is the core fix for the 21 GB OOM
    /// observed when combining a memory budget with a 32 M-row probe table and LIMIT 100.
    ///
    /// The test verifies that requesting only 3 rows from a 1 000-row probe × 10-row
    /// build INNER JOIN reads at most a small multiple of 3 probe rows (not all 1 000).
    /// </summary>
    [Fact]
    public async Task HybridProbe_WithLimit_TerminatesEarlyWithoutReadingAllProbeRows()
    {
        // Build side: 10 rows, all with distinct id values 0..9.
        Row[] buildRows = Enumerable.Range(0, 10)
            .Select(i => MakeRow(("r.id", DataValue.FromScalar((float)i)), ("r.val", DataValue.FromScalar((float)i * 10))))
            .ToArray();

        // Probe side: 1 000 rows, each matching a build row so yield is guaranteed early.
        // Track how many rows were actually consumed from the probe stream.
        int probeRowsConsumed = 0;
        Row[] probeRows = Enumerable.Range(0, 1000)
            .Select(i => MakeRow(("l.id", DataValue.FromScalar((float)(i % 10))), ("l.seq", DataValue.FromScalar((float)i))))
            .ToArray();

        CountingOperator probeOperator = new(probeRows, () => probeRowsConsumed++);

        // Memory budget large enough that NO partitions spill — all are in-memory,
        // so the hybrid streaming path fires for every probe row.
        long generousBudget = 1024 * 1024; // 1 MB

        JoinOperator join = new(
            probeOperator,
            new MockOperator(buildRows),
            JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("l", "id"),
                BinaryOperator.Equal,
                new ColumnReference("r", "id")));

        // Only take the first 3 results — simulates LIMIT 3.
        List<Row> results = new();
        await foreach (Row row in join.ExecuteAsync(CreateContext(generousBudget)))
        {
            results.Add(row);
            if (results.Count >= 3)
            {
                break;
            }
        }

        Assert.Equal(3, results.Count);

        // The hybrid path should have stopped pulling probe rows immediately after
        // 3 matches were found.  We allow a small slack (one batch's worth) but
        // the probe count must be far less than 1 000 — not all rows were read.
        Assert.True(probeRowsConsumed < 100,
            $"Expected far fewer than 100 probe rows consumed for LIMIT 3, but got {probeRowsConsumed}. " +
            "This suggests Phase 1b is still buffering all probe rows instead of streaming.");
    }

    /// <summary>
    /// When all build partitions are in-memory (no spill), the hybrid probe must
    /// still produce exactly the same results as the non-budget in-memory hash join.
    /// </summary>
    [Fact]
    public async Task HybridProbe_NoSpill_ProducesSameResultsAsInMemory()
    {
        Row[] leftRows = Enumerable.Range(0, 20)
            .Select(i => MakeRow(("l.id", DataValue.FromScalar((float)i)), ("l.v", DataValue.FromString($"L{i}"))))
            .ToArray();

        Row[] rightRows = Enumerable.Range(10, 10)
            .Select(i => MakeRow(("r.id", DataValue.FromScalar((float)i)), ("r.v", DataValue.FromString($"R{i}"))))
            .ToArray();

        Expression onCondition = new BinaryExpression(
            new ColumnReference("l", "id"),
            BinaryOperator.Equal,
            new ColumnReference("r", "id"));

        JoinOperator inMemoryJoin = new(new MockOperator(leftRows), new MockOperator(rightRows), JoinType.Inner, onCondition);
        List<Row> inMemoryResults = await CollectAsync(inMemoryJoin);

        JoinOperator hybridJoin = new(new MockOperator(leftRows), new MockOperator(rightRows), JoinType.Inner, onCondition);
        List<Row> hybridResults = await CollectAsync(hybridJoin, CreateContext(1024 * 1024));

        Assert.Equal(inMemoryResults.Count, hybridResults.Count);

        HashSet<string> inMemoryIds = inMemoryResults
            .Select(row => $"{row["l.v"].AsString()}/{row["r.v"].AsString()}")
            .ToHashSet();
        HashSet<string> hybridIds = hybridResults
            .Select(row => $"{row["l.v"].AsString()}/{row["r.v"].AsString()}")
            .ToHashSet();
        Assert.Equal(inMemoryIds, hybridIds);
    }

    /// <summary>
    /// When the build side slightly exceeds the memory budget, only enough partitions
    /// should spill to bring the in-memory footprint under budget. The remaining
    /// in-memory partitions must still service hybrid streaming so that LIMIT can
    /// terminate early. A cascading-spill bug would cause ALL partitions to spill,
    /// forcing every probe row to disk and defeating LIMIT entirely.
    /// </summary>
    [Fact]
    public async Task BorderlineBudget_DoesNotCascadeSpillAllPartitions()
    {
        // Build: 100 rows, 2 scalar columns → ~136 bytes/row → ~13,600 bytes total.
        Row[] buildRows = Enumerable.Range(0, 100)
            .Select(index => MakeRow(
                ("r.id", DataValue.FromScalar((float)index)),
                ("r.val", DataValue.FromScalar((float)(index * 10)))))
            .ToArray();

        // Probe: 500 rows, all matching some build row.
        int probeRowsConsumed = 0;
        Row[] probeRows = Enumerable.Range(0, 500)
            .Select(index => MakeRow(
                ("l.id", DataValue.FromScalar((float)(index % 100))),
                ("l.seq", DataValue.FromScalar((float)index))))
            .ToArray();

        CountingOperator probeOperator = new(probeRows, () => probeRowsConsumed++);

        // Budget is ~88% of build size — borderline, not catastrophically small.
        // With correct in-memory accounting, only ~1 of 4 partitions needs to spill.
        long borderlineBudget = 12_000;

        JoinOperator join = new(
            probeOperator,
            new MockOperator(buildRows),
            JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("l", "id"),
                BinaryOperator.Equal,
                new ColumnReference("r", "id")));

        // Take only 3 results — simulates LIMIT 3.
        List<Row> results = new();
        await foreach (Row row in join.ExecuteAsync(CreateContext(borderlineBudget)))
        {
            results.Add(row);
            if (results.Count >= 3)
            {
                break;
            }
        }

        Assert.Equal(3, results.Count);

        // With correct spill accounting, most partitions stay in-memory and the
        // hybrid path resolves LIMIT 3 after reading very few probe rows.
        // With the cascading-spill bug, all partitions spill and all 500 probe
        // rows are buffered to disk before Phase 2 can produce any output.
        Assert.True(probeRowsConsumed < 100,
            $"Expected fewer than 100 probe rows consumed for LIMIT 3 on a borderline budget, " +
            $"but got {probeRowsConsumed}. This indicates a cascading-spill bug where all " +
            "partitions were spilled despite the build side only slightly exceeding the budget.");
    }

    private static ExecutionContext CreateContext(long memoryBudgetBytes)
    {
        return new ExecutionContext(
            CancellationToken.None,
            FunctionRegistry.CreateDefault(),
            new TableCatalog(),
            memoryBudgetBytes: memoryBudgetBytes);
    }

    private static Row MakeRow(params (string Name, DataValue Value)[] columns)
    {
        string[] names = columns.Select(column => column.Name).ToArray();
        DataValue[] values = columns.Select(column => column.Value).ToArray();
        return new Row(names, values);
    }

    private static async Task<List<Row>> CollectAsync(IQueryOperator op, ExecutionContext? context = null)
    {
        context ??= new ExecutionContext(
            CancellationToken.None,
            FunctionRegistry.CreateDefault(),
            new TableCatalog());

        List<Row> rows = new();
        await foreach (Row row in op.ExecuteAsync(context))
        {
            rows.Add(row);
        }

        return rows;
    }
}
