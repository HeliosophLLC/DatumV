using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Execution.Operators;

/// <summary>
/// Operator for recursive Common Table Expressions (WITH RECURSIVE).
/// Executes the anchor member once, then iterates the recursive member using
/// the previous iteration's output as the working table until no new rows are
/// produced or <see cref="ExecutionContext.MaxRecursionDepth"/> is reached.
/// All accumulated rows (anchor + all iterations) are yielded.
/// </summary>
/// <remarks>
/// <para>
/// Recursive CTEs are always materialized — the full result set must be computed
/// before any row can be yielded to the outer query, because the recursive member
/// depends on the running accumulation.
/// </para>
/// <para>
/// Spill behaviour: when the in-memory accumulation exceeds the budget at an
/// iteration boundary, the operator transitions all cached batches to a
/// <see cref="SpillReaderWriter"/>. From that point on, both the running
/// accumulation AND the next iteration's working-table input read from the
/// spiller, so peak memory is bounded near the budget — the load-bearing
/// property for multi-tenant servers where one user's runaway recursion
/// must not OOM the host.
/// </para>
/// </remarks>
internal sealed class RecursiveCommonTableExpressionOperator : QueryOperator, IDisposable
{
    private readonly QueryOperator _anchorOperator;
    private readonly string _name;
    private readonly IReadOnlyList<string>? _explicitColumnNames;

    /// <summary>
    /// Factory that produces the recursive member's operator tree. Called once per
    /// iteration with a working-table operator (in-memory or spiller-backed) that replays
    /// the previous iteration's rows.
    /// </summary>
    private readonly Func<QueryOperator, QueryOperator> _recursiveMemberFactory;

    private List<RowBatch>? _allBatches;
    private Pool? _pool;
    private SpillReaderWriter? _spiller;
    private ColumnLookup? _materializedSchema;
    private MemoryAccountant? _accountant;
    private long _perRowBytes;
    private long _residentBytesNotified;

    /// <summary>
    /// Creates a recursive CTE operator.
    /// </summary>
    /// <param name="anchorOperator">The operator tree for the anchor (non-recursive) member.</param>
    /// <param name="recursiveMemberFactory">
    /// A factory that, given a working-table operator representing the previous iteration's
    /// output, returns the recursive member's operator tree. The working-table operator is
    /// substituted where the CTE name appears in the recursive member's FROM clause.
    /// </param>
    /// <param name="name">The CTE name.</param>
    /// <param name="explicitColumnNames">Optional explicit column names from the CTE definition.</param>
    public RecursiveCommonTableExpressionOperator(
        QueryOperator anchorOperator,
        Func<QueryOperator, QueryOperator> recursiveMemberFactory,
        string name,
        IReadOnlyList<string>? explicitColumnNames = null)
    {
        _anchorOperator = anchorOperator;
        _recursiveMemberFactory = recursiveMemberFactory;
        _name = name;
        _explicitColumnNames = explicitColumnNames;
    }

    /// <summary>The CTE name.</summary>
    public string Name => _name;

    /// <summary>The anchor operator tree.</summary>
    public QueryOperator AnchorOperator => _anchorOperator;

    /// <summary>True once the spiller has taken over (in-memory accumulation has been evicted to disk).</summary>
    [MemberNotNullWhen(true, nameof(_spiller))]
    public bool IsSpilling => _spiller is not null;

    [MemberNotNullWhen(true, nameof(_pool))]
    [MemberNotNullWhen(true, nameof(_materializedSchema))]
    private bool HasMaterialized => _allBatches is not null || _spiller is not null;

    /// <inheritdoc/>
    protected override OperatorPlanDescription DescribeForExplainImpl()
    {
        return new OperatorPlanDescription("Recursive CTE")
        {
            Properties = new Dictionary<string, string>
            {
                ["name"] = _name,
            },
            Children = [(AnchorOperator, "anchor")],
            Annotations = ["recursive member is generated at runtime"],
        };
    }

    /// <inheritdoc/>
    protected override async IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context)
    {
        if (!HasMaterialized)
        {
            await MaterializeAsync(context).ConfigureAwait(false);
        }

        ColumnLookup outputLookup = _materializedSchema is null
            ? ColumnLookup.Empty
            : RenameColumnsIfNeeded(_materializedSchema);

        if (IsSpilling)
        {
            await foreach (RowBatch batch in _spiller.ReplayAsync(context, outputLookup).ConfigureAwait(false))
            {
                yield return batch;
            }
            yield break;
        }

        if (_allBatches is null)
        {
            yield break;
        }

        // Replay cached rows via RowCopyOutputWriter. Cached batches may carry per-batch
        // ColumnLookup refs that differ from the captured outputLookup, so we pass it
        // explicitly to the writer's shape-stable Add.
        RowCopyOutputWriter writer = new(context);
        try
        {
            foreach (RowBatch cached in _allBatches)
            {
                for (int i = 0; i < cached.Count; i++)
                {
                    RowBatch? full = writer.Add(outputLookup, cached, i);
                    if (full is not null) yield return full;
                }
            }

            RowBatch? trailing = writer.Flush();
            if (trailing is not null) yield return trailing;
        }
        finally
        {
            RowBatch? leftover = writer.Flush();
            if (leftover is not null) context.ReturnRowBatch(leftover);
        }
    }

    /// <summary>
    /// Executes the anchor, then iterates the recursive member until fixpoint or the max
    /// recursion depth. At each iteration boundary, checks the memory budget; on overflow,
    /// transitions all in-memory batches to a <see cref="SpillReaderWriter"/> so subsequent
    /// iterations write to disk and read their working table from disk.
    /// </summary>
    private async Task MaterializeAsync(ExecutionContext context)
    {
        Pool pool = context.Pool;
        _pool = pool;
        _accountant = context.Accountant;
        _allBatches = [];
        int maxDepth = context.MaxRecursionDepth;

        // Anchor pass.
        long iterationStartRow = 0;
        int iterationStartBatchIndex = 0;
        await foreach (RowBatch input in _anchorOperator.ExecuteAsync(context).ConfigureAwait(false))
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            _materializedSchema ??= input.ColumnLookup;
            if (input.Count == 0) { context.ReturnRowBatch(input); continue; }

            CaptureBatch(pool, input, _materializedSchema!);
        }

        // Possibly spill at the end of the anchor pass.
        MaybeSpill(context);

        // Recursive iterations. At each step the working table is the slice
        //   [iterationStartRow, currentRowCount)
        // of the running accumulation — either in-memory (_allBatches[batchIndex..])
        // or on disk (rows [startRow, currentRowCount) in the spiller).
        for (int depth = 0; depth < maxDepth; depth++)
        {
            // Detect fixpoint: previous iteration produced zero rows.
            long currentRowCount = CurrentTotalRowCount();
            if (currentRowCount == iterationStartRow)
            {
                return;
            }

            QueryOperator workingTableOperator = BuildWorkingTableOperator(
                _materializedSchema!,
                iterationStartRow,
                iterationStartBatchIndex,
                currentRowCount);
            QueryOperator recursiveMember = _recursiveMemberFactory(workingTableOperator);

            // Snapshot iteration boundaries BEFORE consuming the recursive member's output —
            // those are the markers; the *next* iteration uses these.
            long nextIterationStartRow = currentRowCount;
            int nextIterationStartBatchIndex = IsSpilling ? 0 : _allBatches!.Count;

            await foreach (RowBatch input in recursiveMember.ExecuteAsync(context).ConfigureAwait(false))
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                if (input.Count == 0) { context.ReturnRowBatch(input); continue; }
                CaptureBatch(pool, input, _materializedSchema!);
            }

            // Boundary spill check. If we transition here, the next iteration's working-table
            // read automatically routes through the spiller (BuildWorkingTableOperator picks
            // the backend based on IsSpilling at call time). The batch-index marker is no
            // longer meaningful once spilled — only the row marker matters.
            MaybeSpill(context);

            iterationStartRow = nextIterationStartRow;
            iterationStartBatchIndex = IsSpilling ? 0 : nextIterationStartBatchIndex;
        }

        // Hit max depth and the last iteration produced rows — runaway recursion.
        if (CurrentTotalRowCount() > iterationStartRow)
        {
            throw new RecursionDepthExceededException(_name, maxDepth);
        }
    }

    /// <summary>
    /// Captures one input batch into the running accumulation. Routes to the spiller if we've
    /// already transitioned, otherwise rebinds the input batch into <see cref="_allBatches"/>.
    /// </summary>
    private void CaptureBatch(Pool pool, RowBatch input, ColumnLookup schema)
    {
        if (IsSpilling)
        {
            _spiller.Write(input); // Stabilizes payloads, writes row metadata, returns the batch.
        }
        else
        {
            RowBatch cached = pool.RebindRowBatch(input, schema);
            _allBatches!.Add(cached);

            // Account structural per-row residency: DataValue cells + Row +
            // List<RowBatch> slot. Arena payloads live in the batch's own
            // arena (mmap, OS-paged) and aren't budgeted.
            if (_perRowBytes == 0)
            {
                _perRowBytes = DataValue.SizeBytes * (long)schema.Count + 32L;
            }
            long batchBytes = _perRowBytes * cached.Count;
            _accountant!.NotifyMaterialized(batchBytes);
            _residentBytesNotified += batchBytes;
        }
    }

    /// <summary>
    /// Total row count materialized so far across all iterations, regardless of whether the
    /// rows live in-memory or on disk.
    /// </summary>
    private long CurrentTotalRowCount()
    {
        if (IsSpilling) return _spiller.RowsWritten;
        long total = 0;
        foreach (RowBatch b in _allBatches!) total += b.Count;
        return total;
    }

    /// <summary>
    /// If the plan-wide accountant signals over-budget, transitions to spill
    /// mode by handing every cached batch to a fresh
    /// <see cref="SpillReaderWriter"/>. The in-memory accumulation's
    /// accounted bytes are released back to the accountant since the rows
    /// now live on disk.
    /// </summary>
    private void MaybeSpill(ExecutionContext context)
    {
        if (IsSpilling || _allBatches is null) return;
        if (!context.Accountant.WouldExceedBudget()) return;

        // Initial-arena-capacity hint: sum of cached arena capacities,
        // doubled for headroom (covers the recursive iterations still to
        // come). Same heuristic as CTE. Arena.Capacity is the right input
        // here — the spiller's consolidated arena needs to absorb the bytes
        // that were living in those per-batch arenas, regardless of whether
        // those bytes counted toward the spill budget.
        long inMemoryArenaBytes = 0;
        foreach (RowBatch b in _allBatches) inMemoryArenaBytes += b.Arena.Capacity;
        long hintLong = inMemoryArenaBytes * 2;
        int hint = hintLong > int.MaxValue ? int.MaxValue : (int)hintLong;

        _spiller = new SpillReaderWriter(
            context.Pool, _materializedSchema!, context.SpillDirectory, hint);

        foreach (RowBatch buffered in _allBatches)
        {
            _spiller.Write(buffered);
        }

        _allBatches = null;

        // Cached rows are now on disk; release their accounted bytes.
        if (_residentBytesNotified > 0)
        {
            context.Accountant.NotifyReleased(_residentBytesNotified);
            _residentBytesNotified = 0;
        }
    }

    /// <summary>
    /// Builds the working-table operator for the next iteration's recursive member. Picks the
    /// in-memory backend (when not spilling) or the spiller-backed backend (when spilling),
    /// representing the row range produced by the immediately preceding iteration.
    /// </summary>
    private QueryOperator BuildWorkingTableOperator(
        ColumnLookup schema,
        long startRow,
        int startBatchIndex,
        long endRow)
    {
        if (IsSpilling)
        {
            return new SpillerBackedWorkingTableOperator(
                _spiller, schema, startRow, endRow - startRow);
        }

        // In-memory: take a snapshot list to insulate the working-table replay from the next
        // iteration's appends to _allBatches. _allBatches.GetRange returns a fresh list whose
        // RowBatch references are still owned by us (refcount-shared).
        int count = _allBatches!.Count - startBatchIndex;
        IReadOnlyList<RowBatch> snapshot = _allBatches.GetRange(startBatchIndex, count);
        return new InMemoryWorkingTableOperator(schema, snapshot);
    }

    /// <summary>
    /// Renames the output columns if the CTE definition provides explicit column names.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ColumnLookup RenameColumnsIfNeeded(ColumnLookup columnLookup)
    {
        if (_explicitColumnNames is null) return columnLookup;

        string[] renamedNames = new string[columnLookup.Count];
        for (int index = 0; index < columnLookup.Count; index++)
        {
            renamedNames[index] = index < _explicitColumnNames.Count
                ? _explicitColumnNames[index]
                : columnLookup.ColumnNames[index];
        }
        return new ColumnLookup(renamedNames);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_allBatches is not null && _pool is not null)
        {
            foreach (RowBatch batch in _allBatches)
            {
                _pool.ReturnRowBatch(batch);
            }
            _allBatches = null;
        }

        // Release any residency we still hold (in-memory cache that wasn't
        // spilled). After spill, _residentBytesNotified is already zero.
        if (_residentBytesNotified > 0 && _accountant is not null)
        {
            _accountant.NotifyReleased(_residentBytesNotified);
            _residentBytesNotified = 0;
        }

        _spiller?.Dispose();
        _spiller = null;
    }

    /// <summary>
    /// Working-table replay backed by an in-memory snapshot of <see cref="RowBatch"/>es. Used
    /// before the recursive CTE transitions to spill mode.
    /// </summary>
    private sealed class InMemoryWorkingTableOperator(
        ColumnLookup schema,
        IReadOnlyList<RowBatch> batches) : QueryOperator
    {
        protected override OperatorPlanDescription DescribeForExplainImpl()
        {
            long rows = 0;
            foreach (RowBatch b in batches) rows += b.Count;
            return new OperatorPlanDescription("Working Table") { EstimatedRows = rows };
        }

        protected override async IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context)
        {
            // The cached batches stay owned by the outer recursive CTE; we yield COPIES that
            // share the cached arena, so the consumer can dispose its received batches normally.
            // Use the explicit-output-lookup overload — cached batches may have different
            // per-batch ColumnLookup refs.
            RowCopyOutputWriter writer = new(context);
            try
            {
                foreach (RowBatch cached in batches)
                {
                    for (int i = 0; i < cached.Count; i++)
                    {
                        RowBatch? full = writer.Add(schema, cached, i);
                        if (full is not null) yield return full;
                    }
                }

                RowBatch? trailing = writer.Flush();
                if (trailing is not null) yield return trailing;
            }
            finally
            {
                RowBatch? leftover = writer.Flush();
                if (leftover is not null) context.ReturnRowBatch(leftover);
            }
            await Task.CompletedTask;
        }
    }

    /// <summary>
    /// Working-table replay backed by a row range in a <see cref="SpillReaderWriter"/>. Used
    /// after the recursive CTE has transitioned to spill mode. Reads only the slice
    /// <c>[startRow, startRow + rowCount)</c> from the spill file — the previous iteration's
    /// output, not the entire accumulation.
    /// </summary>
    private sealed class SpillerBackedWorkingTableOperator(
        SpillReaderWriter spiller,
        ColumnLookup schema,
        long startRow,
        long rowCount) : QueryOperator
    {
        protected override OperatorPlanDescription DescribeForExplainImpl()
            => new("Working Table (spilled)") { EstimatedRows = rowCount };

        protected override async IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context)
        {
            await foreach (RowBatch batch in spiller
                .ReplayRangeAsync(context, schema, startRow, rowCount)
                .ConfigureAwait(false))
            {
                yield return batch;
            }
        }
    }
}
