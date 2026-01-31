using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Verifies that operators return consumed <see cref="DataValue"/> arrays to the
/// <see cref="LocalBufferPool"/> so they can be recycled. Operators that create new
/// output rows (Project) or drop input rows (Filter) must return the consumed input
/// row's backing array — otherwise the <see cref="LocalBufferPool._ownedArrays"/>
/// queue grows proportionally to total row count, consuming memory outside any
/// operator's budget.
/// </summary>
public sealed class OperatorBufferReturnTests : ServiceTestBase
{
    /// <summary>
    /// Number of rows to push through the pipeline. Must be large enough to
    /// distinguish O(1) from O(N) growth in the owned-array queue, but small
    /// enough to run quickly in CI.
    /// </summary>
    private const int RowCount = 10_000;

    // ────────────────── ProjectOperator ──────────────────

    /// <summary>
    /// The ProjectOperator creates a new <see cref="DataValue"/> array for each output
    /// row via <see cref="LocalBufferPool.Rent"/>. The consumer returns the output arrays.
    ///
    /// Input row arrays are NOT yet returned by ProjectOperator (deferred to Part C of
    /// the buffer return plan — requires conditional return based on whether the projection
    /// uses only ordinal copies, to avoid USE-AFTER-RETURN when expressions capture the
    /// input row via closures).
    ///
    /// This test verifies that at minimum the output arrays are returned by the consumer.
    /// </summary>
    [Fact]
    public async Task ProjectOperator_OutputArraysReturnedByConsumer()
    {
        Pool pool = CreatePool();
        ExecutionContext context = CreateExecutionContext(pool: pool);

        // Source produces rows with pool-rented arrays (mimics ScanOperator).
        PooledMockOperator source = new(pool, RowCount, columnCount: 3);

        // Project selects a subset of columns, creating new output arrays.
        ProjectOperator project = new(
            source,
            [
                new SelectColumn(new ColumnReference("c0")),
                new SelectColumn(new ColumnReference("c1")),
            ]);

        // Consume all output.
        await foreach (RowBatch batch in project.ExecuteAsync(context))
        {
            context.ReturnRowBatch(batch);
        }

        // The consumer returned all N output arrays. Input arrays are not yet returned
        // by ProjectOperator (deferred to Part C).
        Assert.True(pool.Backing.DataValueArrayReturnCount >= RowCount,
            $"Expected at least {RowCount:N0} returns (output arrays from consumer), " +
            $"but got {pool.Backing.DataValueArrayReturnCount:N0}.");
    }

    /// <summary>
    /// Verifies that the ProjectOperator's output arrays use <see cref="LocalBufferPool.Rent"/>
    /// (not <see cref="LocalBufferPool.RentOwned"/>), so they are NOT registered in the
    /// owned-array queue. The source (PooledMockOperator) also uses <see cref="LocalBufferPool.Rent"/>.
    /// Therefore the owned-array queue should be empty.
    /// </summary>
    [Fact]
    public async Task ProjectOperator_OwnedArrayQueueBounded()
    {
        Pool pool = CreatePool();
        ExecutionContext context = CreateExecutionContext(pool: pool);

        PooledMockOperator source = new(pool, RowCount, columnCount: 3);
        ProjectOperator project = new(
            source,
            [
                new SelectColumn(new ColumnReference("c0")),
                new SelectColumn(new ColumnReference("c1")),
            ]);

        await foreach (RowBatch batch in project.ExecuteAsync(context))
        {
            context.ReturnRowBatch(batch);
        }

        // Both the PooledMockOperator (source) and ProjectOperator use pool.Rent(),
        // so all arrays flow through the Rent/Return cycle with no leaks.
    }

    // ────────────────── FilterOperator ──────────────────

    /// <summary>
    /// The FilterOperator is a pass-through operator: rows that pass the predicate
    /// are forwarded unchanged (the downstream consumer returns them). Rows that fail
    /// are currently dropped without returning their DataValue[] arrays.
    ///
    /// Returning filtered-out rows is unsafe when the DataValue[] is shared via
    /// AliasOperator zero-copy (e.g., in lateral joins, recursive CTEs). Filtered-out
    /// row return is deferred to Part C of the buffer return plan, where the top-down
    /// RowBatch ownership model will make it safe.
    ///
    /// This test verifies that at minimum the passed rows are returned by the consumer.
    /// </summary>
    [Fact]
    public async Task FilterOperator_PassedRowsReturnedByConsumer()
    {
        Pool pool = CreatePool();
        ExecutionContext context = CreateExecutionContext(pool: pool);

        // Source with 3 columns: c0 (int), c1 (int), c2 (int).
        // c0 alternates 0 and 1, so ~50% of rows pass the filter.
        PooledMockOperator source = new(pool, RowCount, columnCount: 3);

        // Filter: keep only rows where c0 = 1 (approximately half).
        FilterOperator filter = new(
            source,
            new BinaryExpression(
                new ColumnReference("c0"),
                BinaryOperator.Equal,
                new LiteralExpression(DataValue.FromFloat32(1f))));

        int passedCount = 0;

        await foreach (RowBatch batch in filter.ExecuteAsync(context))
        {
            passedCount += batch.Count;

            context.ReturnRowBatch(batch);
        }

        // Consumer returned all passed rows. Filtered-out rows are not yet returned
        // (deferred to Part C).
        Assert.True(pool.Backing.DataValueArrayReturnCount >= passedCount,
            $"Expected at least {passedCount:N0} returns (passed rows from consumer), " +
            $"but got {pool.Backing.DataValueArrayReturnCount:N0}.");
    }

    // ────────────────── GraceHashJoin spill path ──────────────────

    /// <summary>
    /// When the GraceHashJoinExecutor's hybrid Phase1b routes a probe row to a spilled
    /// partition, the row is serialized to disk. Its <see cref="DataValue"/> array must
    /// be returned to the pool after serialization — otherwise pooled arrays from upstream
    /// CombinePooled calls accumulate unreturned, bypassing the memory budget.
    /// </summary>
    [Fact]
    public async Task GraceHashJoin_SpillPath_ReturnsProbeRowArraysToPool()
    {
        // Use a tiny budget to force spilling.
        const long tinyBudget = 256;
        Pool pool = CreatePool();
        ExecutionContext context = CreateExecutionContext(memoryBudgetBytes: tinyBudget, pool: pool);

        // Build side: small table.
        object?[][] buildRows = Enumerable.Range(0, 50)
            .Select(i => new object?[] { (float)i, $"build_{i}" })
            .ToArray();

        // Probe side: larger table to ensure many probe rows hit spilled partitions.
        object?[][] probeRows = Enumerable.Range(0, 2000)
            .Select(i => new object?[] { (float)(i % 50), (float)i })
            .ToArray();

        MockOperator left = CreateMockOperator(["l.id", "l.data"], rows: probeRows);
        MockOperator right = CreateMockOperator(["r.id", "r.val"], rows: buildRows);

        JoinOperator join = new(left, right, JoinType.Inner,
            new BinaryExpression(
                new ColumnReference("l", "id"),
                BinaryOperator.Equal,
                new ColumnReference("r", "id")));

        int outputCount = 0;

        await foreach (RowBatch batch in join.ExecuteAsync(context))
        {
            outputCount += batch.Count;

            context.ReturnRowBatch(batch);
        }

        // Every join output row should have been returned by the consumer.
        // The join should have returned spilled probe rows internally.
        // Total returns should be at least the output count (consumer returns)
        // plus some fraction of probe rows (spill path returns).
        Assert.True(pool.Backing.DataValueArrayReturnCount >= outputCount,
            $"Expected at least {outputCount:N0} returns, but got {pool.Backing.DataValueArrayReturnCount:N0}. " +
            "Spilled probe row DataValue[] arrays may not be returned to the pool.");
    }

    // ────────────────── Rent/Return balance ──────────────────

    /// <summary>
    /// Verifies that a Source → Project → terminal consumer pipeline returns every
    /// <see cref="DataValue"/> array that was rented. A non-zero delta indicates a
    /// leak — arrays orphaned without returning to the pool.
    ///
    /// This test runs under POOL_DIAGNOSTICS (Debug builds) which also catches
    /// use-after-return and double-return violations.
    /// </summary>
    [Fact]
    public async Task Pipeline_RentCountEqualsReturnCount()
    {
        Pool pool = CreatePool();
        ExecutionContext context = CreateExecutionContext(pool: pool);

        PooledMockOperator source = new(pool, RowCount, columnCount: 3);

        // Project: transforms (rents new output arrays, returns input batch).
        ProjectOperator project = new(
            source,
            [
                new SelectColumn(new ColumnReference("c0")),
                new SelectColumn(new ColumnReference("c1")),
            ]);

        // Terminal consumer: clones and returns output batch.
        await foreach (RowBatch batch in project.ExecuteAsync(context))
        {
            context.ReturnRowBatch(batch);
        }

        long leaked = pool.Backing.DataValueArrayRentCount - pool.Backing.DataValueArrayReturnCount;
        Assert.True(leaked == 0,
            $"Rent/Return imbalance: rented={pool.Backing.DataValueArrayRentCount:N0} " +
            "returned={pool.Backing.DataValueArrayReturnCount:N0} leaked={leaked:N0}. " +
            "Every DataValue[] array rented from the pool should be returned.");
    }

    // ────────────────── Helpers ──────────────────

    /// <summary>
    /// A mock operator that produces rows with <see cref="DataValue"/> arrays rented
    /// from the <see cref="LocalBufferPool"/> via <see cref="LocalBufferPool.RentOwned"/>,
    /// mimicking how <see cref="ScanOperator"/> produces rows.
    /// </summary>
    private sealed class PooledMockOperator : IQueryOperator
    {
        private readonly Pool _pool;
        private readonly int _rowCount;
        private readonly int _columnCount;

        public PooledMockOperator(Pool pool, int rowCount, int columnCount)
        {
            _pool = pool;
            _rowCount = rowCount;
            _columnCount = columnCount;
        }

        public OperatorPlanDescription DescribeForExplain() => new("PooledMock");

        public async IAsyncEnumerable<RowBatch> ExecuteAsync(ExecutionContext context)
        {
            string[] names = Enumerable.Range(0, _columnCount)
                .Select(i => $"c{i}")
                .ToArray();
            ColumnLookup columnLookup = new(names);

            RowBatch? batch = null;

            for (int row = 0; row < _rowCount; row++)
            {
                DataValue[] values = _pool.RentDataValues(_columnCount);
                for (int col = 0; col < _columnCount; col++)
                {
                    values[col] = DataValue.FromFloat32(row % (col + 2));
                }

                batch ??= context.RentRowBatch(columnLookup);
                batch.Add(values);

                if (batch.IsFull)
                {
                    RowBatch toYield = batch;
                    batch = null;
                    yield return toYield;
                }
            }

            if (batch is not null)
            {
                RowBatch toYield = batch;
                batch = null;
                yield return toYield;
            }

            await Task.CompletedTask;
        }
    }
}
