using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Model;
using DatumIngest.Pooling;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for <see cref="DistinctOperator"/>. Covers in-memory dedup correctness,
/// the spill code path (with the post-spill route-to-spill gate that keeps the
/// in-memory set bounded), and pool leak balance for both single- and multi-
/// column key paths.
/// </summary>
public sealed class DistinctOperatorTests : ServiceTestBase
{
    private static readonly string[] XColumns = ["x"];

    private async Task<List<Row>> CollectAsync(
        QueryOperator op, ExecutionContext? context = null)
    {
        context ??= CreateExecutionContext();
        return await op.CollectRowsAsync(context);
    }

    // ─────────────── Correctness — single column ───────────────

    [Fact]
    public async Task Distinct_RemovesDuplicates_SingleColumn()
    {
        MockOperator source = CreateMockOperator(XColumns, [1f], [2f], [1f], [3f], [2f]);
        DistinctOperator op = new(source);

        List<Row> results = await CollectAsync(op);

        Assert.Equal(3, results.Count);
        float[] values = results.Select(row => row[0].AsFloat32()).OrderBy(v => v).ToArray();
        Assert.Equal([1f, 2f, 3f], values);
    }

    [Fact]
    public async Task Distinct_PreservesFirstOccurrenceOrder()
    {
        MockOperator source = CreateMockOperator(XColumns, [3f], [1f], [3f], [2f], [1f]);
        DistinctOperator op = new(source);

        List<Row> results = await CollectAsync(op);

        Assert.Equal(3, results.Count);
        float[] values = results.Select(row => row[0].AsFloat32()).ToArray();
        Assert.Equal([3f, 1f, 2f], values);
    }

    [Fact]
    public async Task Distinct_AllDuplicates_YieldsOne()
    {
        MockOperator source = CreateMockOperator(XColumns, [5f], [5f], [5f]);
        DistinctOperator op = new(source);

        List<Row> results = await CollectAsync(op);

        Assert.Single(results);
        Assert.Equal(5f, results[0][0].AsFloat32());
    }

    [Fact]
    public async Task Distinct_AllDistinct_YieldsAll()
    {
        MockOperator source = CreateMockOperator(XColumns, [1f], [2f], [3f]);
        DistinctOperator op = new(source);

        List<Row> results = await CollectAsync(op);

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task Distinct_EmptySource_YieldsNothing()
    {
        MockOperator source = CreateMockOperator(XColumns);
        DistinctOperator op = new(source);

        List<Row> results = await CollectAsync(op);

        Assert.Empty(results);
    }

    // ─────────────── Correctness — multi-column ───────────────

    [Fact]
    public async Task Distinct_RemovesDuplicates_MultiColumn()
    {
        MockOperator source = CreateMockOperator(["a", "b"],
            [1f, "x"],
            [2f, "y"],
            [1f, "x"],
            [1f, "y"],
            [2f, "y"]);
        DistinctOperator op = new(source);

        List<Row> results = await CollectAsync(op);

        // Distinct combinations: (1,x), (2,y), (1,y) → 3 rows.
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task Distinct_MultiColumn_DistinguishesByAllColumns()
    {
        // Same first column, different second column → still distinct.
        MockOperator source = CreateMockOperator(["a", "b"],
            [1f, "x"],
            [1f, "y"],
            [1f, "z"]);
        DistinctOperator op = new(source);

        List<Row> results = await CollectAsync(op);

        Assert.Equal(3, results.Count);
    }

    // ─────────────── Spill path ───────────────

    [Fact]
    public async Task Distinct_WithSpill_ProducesCorrectResults()
    {
        // 1000 input rows where every value 0..499 appears twice, interleaved.
        // Distinct result: 500 rows. Tight budget forces spill.
        object?[][] rows = Enumerable.Range(0, 500)
            .SelectMany(index => new[] { new object?[] { (float)index }, new object?[] { (float)index } })
            .ToArray();
        MockOperator source = CreateMockOperator(XColumns, rows: rows);
        DistinctOperator op = new(source);

        ExecutionContext context = CreateExecutionContext(memoryBudgetBytes: 1024);
        List<Row> results = await CollectAsync(op, context);

        Assert.Equal(500, results.Count);
        Assert.True(op.SpillingTriggered, "Expected the budget to trigger spill so this test exercises the spill path.");

        float[] values = results.Select(row => row[0].AsFloat32()).OrderBy(v => v).ToArray();
        float[] expected = Enumerable.Range(0, 500).Select(i => (float)i).ToArray();
        Assert.Equal(expected, values);

        op.Dispose();
    }

    /// <summary>
    /// Exposes the post-spill route-to-spill gate: if every <c>isNew</c> row were
    /// added to the in-memory set unconditionally (the pre-fix shape), the drain
    /// phase's partition-local set would seed every spilled key from in-memory
    /// and the second probe would always match — drain would emit nothing.
    /// With the gate in place, post-spill rows skip the in-memory set and reach
    /// drain via the spill files. The assertion <c>DrainEmittedRowCount &gt; 0</c>
    /// proves the gate is wired correctly.
    /// </summary>
    [Fact]
    public async Task Distinct_AfterSpill_DrainEmitsSpilledRows()
    {
        object?[][] rows = Enumerable.Range(0, 500).Select(index => new object?[] { (float)index }).ToArray();
        MockOperator source = CreateMockOperator(XColumns, rows: rows);
        DistinctOperator op = new(source);

        ExecutionContext context = CreateExecutionContext(memoryBudgetBytes: 1024);
        List<Row> results = await CollectAsync(op, context);

        Assert.Equal(500, results.Count);
        Assert.True(op.SpillingTriggered, "Budget should have triggered spill for this dataset.");
        Assert.True(
            op.DrainEmittedRowCount > 0,
            $"Expected drain phase to emit spilled rows after budget exceeded, but emitted "
            + $"{op.DrainEmittedRowCount}. The in-memory set absorbed every row, meaning the "
            + $"spill machinery is dead code and memory growth is unbounded.");

        op.Dispose();
    }

    // ─────────────── Spill volume validation ───────────────

    /// <summary>
    /// A tight memory budget should route a substantial number of rows through
    /// the spill machinery — not just the single boundary row that flips
    /// <see cref="DistinctOperator.SpillingTriggered"/>. With 500 distinct keys and
    /// a 1 KB budget the in-memory set can hold only a tiny fraction; the rest
    /// must reach the disk path.
    /// </summary>
    [Fact]
    public async Task Distinct_TightBudget_RoutesMostRowsToSpill()
    {
        const int distinctCount = 500;
        object?[][] rows = Enumerable.Range(0, distinctCount)
            .Select(index => new object?[] { (float)index })
            .ToArray();
        MockOperator source = CreateMockOperator(XColumns, rows: rows);
        DistinctOperator op = new(source);

        ExecutionContext context = CreateExecutionContext(memoryBudgetBytes: 1024);
        List<Row> results = await CollectAsync(op, context);

        Assert.Equal(distinctCount, results.Count);
        Assert.True(op.SpillingTriggered);

        // The in-memory set keeps whatever slipped in before spill triggered. With a
        // 1 KB budget the threshold is hit very early, so the vast majority of rows
        // should route to spill. Loose lower bound to avoid flake on estimator cadence.
        Assert.True(
            op.SpilledRowCount > distinctCount / 2,
            $"Expected the majority of {distinctCount} rows to route to spill under a "
            + $"1 KB budget, but only {op.SpilledRowCount} did. The in-memory set is "
            + $"absorbing too much before the spill gate fires.");

        op.Dispose();
    }

    /// <summary>
    /// A generous budget should produce identical output to the tight-budget run
    /// without touching the spill machinery at all. Catches regressions where the
    /// spill code path is taken even when memory is plentiful.
    /// </summary>
    [Fact]
    public async Task Distinct_GenerousBudget_NoSpill()
    {
        const int distinctCount = 500;
        object?[][] rows = Enumerable.Range(0, distinctCount)
            .Select(index => new object?[] { (float)index })
            .ToArray();
        MockOperator source = CreateMockOperator(XColumns, rows: rows);
        DistinctOperator op = new(source);

        ExecutionContext context = CreateExecutionContext(memoryBudgetBytes: 10 * 1024 * 1024);
        List<Row> results = await CollectAsync(op, context);

        Assert.Equal(distinctCount, results.Count);
        Assert.False(op.SpillingTriggered, "10 MB budget is more than enough for 500 float keys; no spill should have triggered.");
        Assert.Equal(0, op.SpilledRowCount);
        Assert.Equal(0, op.DrainEmittedRowCount);

        op.Dispose();
    }

    /// <summary>
    /// Same dataset under two different budgets must produce the same logical output
    /// (same set of distinct values, possibly different order). Asserts both that
    /// the spill path is correct AND that the no-spill path is correct, against the
    /// same ground truth.
    /// </summary>
    [Fact]
    public async Task Distinct_OutputIdenticalAcrossBudgets()
    {
        const int distinctCount = 500;
        object?[][] rows = Enumerable.Range(0, distinctCount)
            .SelectMany(index => new[] { new object?[] { (float)index }, new object?[] { (float)index } })
            .ToArray();

        MockOperator generousSource = CreateMockOperator(XColumns, rows: rows);
        DistinctOperator generousOp = new(generousSource);
        List<Row> generousResults = await CollectAsync(
            generousOp, CreateExecutionContext(memoryBudgetBytes: 10 * 1024 * 1024));
        Assert.False(generousOp.SpillingTriggered);
        generousOp.Dispose();

        MockOperator tightSource = CreateMockOperator(XColumns, rows: rows);
        DistinctOperator tightOp = new(tightSource);
        List<Row> tightResults = await CollectAsync(
            tightOp, CreateExecutionContext(memoryBudgetBytes: 1024));
        Assert.True(tightOp.SpillingTriggered);
        Assert.True(tightOp.SpilledRowCount > 0);
        tightOp.Dispose();

        float[] generousValues = generousResults.Select(row => row[0].AsFloat32()).OrderBy(v => v).ToArray();
        float[] tightValues = tightResults.Select(row => row[0].AsFloat32()).OrderBy(v => v).ToArray();
        Assert.Equal(generousValues, tightValues);
    }

    /// <summary>
    /// Long strings are stored arena-backed (the inline-string optimisation only
    /// covers ≤ 16 UTF-8 bytes). Under a tight budget, post-spill rows must have
    /// their string payloads correctly stabilised through the spiller's consolidated
    /// arena and resolved on replay — otherwise drain would emit garbage strings or
    /// fail equality. Asserts every distinct string round-trips intact.
    /// </summary>
    /// <remarks>
    /// String payloads are extracted from each <see cref="DataValue"/> during the
    /// <c>await foreach</c> while the batch's arena is still alive (resolving
    /// offsets via <see cref="DataValue.AsString(IValueStore)"/>); the standard
    /// <c>CollectAsync</c> helper only shallow-clones the structs, which would
    /// leave the offsets dangling once the operator's <c>hashSetArena</c> /
    /// spiller arena is returned in <c>finally</c>.
    /// </remarks>
    [Fact]
    public async Task Distinct_TightBudget_LongStrings_RoundTripCorrectly()
    {
        // 32-byte strings → arena-backed. 200 distinct values × 2 occurrences each.
        string[] distinct = Enumerable.Range(0, 200)
            .Select(index => $"long-key-payload-{index:D000000}-pad-bytes")
            .ToArray();
        object?[][] rows = distinct
            .SelectMany(s => new[] { new object?[] { s }, new object?[] { s } })
            .ToArray();
        Pool pool = GetService<Pool>();
        MockOperator source = CreateMockOperator(XColumns, rows: rows);
        DistinctOperator op = new(source);

        ExecutionContext context = CreateExecutionContext(memoryBudgetBytes: 1024);
        List<string> emitted = [];
        try
        {
            await foreach (RowBatch batch in op.ExecuteAsync(context))
            {
                for (int i = 0; i < batch.Count; i++)
                {
                    emitted.Add(batch[i][0].AsString(batch.Arena));
                }
                pool.ReturnRowBatch(batch);
            }
        }
        finally
        {
            op.Dispose();
        }

        Assert.Equal(distinct.Length, emitted.Count);
        Assert.True(op.SpillingTriggered);
        Assert.True(op.SpilledRowCount > 0,
            "Expected long-string rows to route to spill under a 1 KB budget.");

        Assert.Equal(distinct.OrderBy(s => s).ToArray(), emitted.OrderBy(s => s).ToArray());
    }

    // ─────────────── Pool leak balance ───────────────

    [Fact]
    public async Task Distinct_SingleColumn_NoSpill_PoolDoesNotLeak()
    {
        Pool pool = GetService<Pool>();
        MockOperator source = CreateMockOperator(XColumns, [1f], [2f], [1f], [3f]);
        DistinctOperator op = new(source);

        List<Row> results = await CollectAsync(op);
        op.Dispose();

        Assert.Equal(3, results.Count);
        AssertPoolBalanced(pool);
    }

    [Fact]
    public async Task Distinct_MultiColumn_NoSpill_PoolDoesNotLeak()
    {
        Pool pool = GetService<Pool>();
        MockOperator source = CreateMockOperator(["a", "b"],
            [1f, "x"], [2f, "y"], [1f, "x"], [3f, "z"]);
        DistinctOperator op = new(source);

        List<Row> results = await CollectAsync(op);
        op.Dispose();

        Assert.Equal(3, results.Count);
        AssertPoolBalanced(pool);
    }

    [Fact]
    public async Task Distinct_WithSpill_PoolDoesNotLeak()
    {
        Pool pool = GetService<Pool>();
        object?[][] rows = Enumerable.Range(0, 500)
            .SelectMany(index => new[] { new object?[] { (float)index }, new object?[] { (float)index } })
            .ToArray();
        MockOperator source = CreateMockOperator(XColumns, rows: rows);
        DistinctOperator op = new(source);

        ExecutionContext context = CreateExecutionContext(memoryBudgetBytes: 1024);
        List<Row> results = await CollectAsync(op, context);

        Assert.Equal(500, results.Count);
        Assert.True(op.SpillingTriggered, "Expected the budget to trigger spill so the leak check covers spill paths.");

        op.Dispose();
        AssertPoolBalanced(pool);
    }

    /// <summary>
    /// Asserts every rent on this pool has a matching return — the "no leak"
    /// invariant. Run after the operator and any owned RowBatches have been
    /// disposed/returned.
    /// </summary>
    private static void AssertPoolBalanced(Pool pool)
    {
        long dvRent = pool.Backing.DataValueArrayRentCount;
        long dvReturn = pool.Backing.DataValueArrayReturnCount;
        long rbRent = pool.Backing.RowBatchRentCount;
        long rbReturn = pool.Backing.RowBatchReturnCount;
        long arenaRent = pool.Backing.ArenaRentCount;
        long arenaReleased = pool.Backing.ArenaFullyReleasedCount;

        Assert.True(
            dvRent == dvReturn && rbRent == rbReturn && arenaRent == arenaReleased,
            $"Pool not balanced — DataValue[] rent/return: {dvRent}/{dvReturn}, "
            + $"RowBatch rent/return: {rbRent}/{rbReturn}, "
            + $"Arena rent/fully-released: {arenaRent}/{arenaReleased}.");
    }
}
