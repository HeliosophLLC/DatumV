using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Execution.Operators;

/// <summary>
/// Operator that provides the result set of a Common Table Expression (CTE).
/// Supports two execution modes controlled by the <see cref="IsMaterialized"/> flag:
/// <list type="bullet">
/// <item>
/// <term>Inlined</term>
/// <description>
/// Each call to <see cref="QueryOperator.ExecuteAsync(ExecutionContext)"/> re-executes the inner operator tree,
/// behaving like a subquery at each reference site.
/// </description>
/// </item>
/// <item>
/// <term>Materialized</term>
/// <description>
/// The first call to <see cref="QueryOperator.ExecuteAsync(ExecutionContext)"/> fully consumes the inner operator
/// and buffers the result set into pool-owned <see cref="RowBatch"/> objects.
/// Subsequent calls replay by copying cached values into fresh output batches.
/// When a memory budget is configured and the buffer exceeds it, rows are spilled
/// via <see cref="SpillReaderWriter"/> and replayed from disk against a consolidated arena.
/// </description>
/// </item>
/// </list>
/// </summary>
internal sealed class CommonTableExpressionOperator : QueryOperator, IDisposable
{
    private readonly QueryOperator _innerOperator;
    private readonly string _name;
    private readonly bool _isMaterialized;
    private readonly IReadOnlyList<string>? _explicitColumnNames;

    private List<RowBatch>? _materializedBatches;
    private Pool? _pool;
    private SpillReaderWriter? _spiller;
    private ColumnLookup? _materializedSchema;
    private MemoryAccountant? _accountant;
    private long _residentBytesNotified;

    /// <summary>
    /// Creates a new CTE operator.
    /// </summary>
    /// <param name="innerOperator">The operator tree for the CTE body.</param>
    /// <param name="name">The CTE name used as the table alias.</param>
    /// <param name="isMaterialized">
    /// When <see langword="true"/>, the inner result is computed once and buffered.
    /// When <see langword="false"/>, each reference re-executes the inner operator.
    /// </param>
    /// <param name="explicitColumnNames">
    /// Optional column names from the CTE definition that rename the inner query's output
    /// columns positionally (e.g. <c>WITH cte(a, b) AS (...)</c>).
    /// </param>
    public CommonTableExpressionOperator(
        QueryOperator innerOperator,
        string name,
        bool isMaterialized,
        IReadOnlyList<string>? explicitColumnNames = null)
    {
        _innerOperator = innerOperator;
        _name = name;
        _isMaterialized = isMaterialized;
        _explicitColumnNames = explicitColumnNames;
    }

    /// <summary>The CTE name.</summary>
    public string Name => _name;

    /// <summary>Whether this CTE materializes its result set.</summary>
    public bool IsMaterialized => _isMaterialized;

    /// <summary>True once the spiller has taken over (in-memory cache has been evicted to disk).</summary>
    [MemberNotNullWhen(true, nameof(_spiller))]
    public bool IsSpilling => _spiller is not null;

    /// <summary>True once <see cref="MaterializeAsync"/> has run for this instance.</summary>
    [MemberNotNullWhen(true, nameof(_pool))]
    [MemberNotNullWhen(true, nameof(_materializedSchema))]
    private bool HasMaterialized => _materializedBatches is not null || _spiller is not null;

    /// <summary>The inner operator tree.</summary>
    public QueryOperator InnerOperator => _innerOperator;

    /// <inheritdoc/>
    protected override OperatorPlanDescription DescribeForExplainImpl()
    {
        Dictionary<string, string> properties = new()
        {
            ["name"] = _name,
            ["mode"] = _isMaterialized ? "materialized" : "inline",
        };

        return new OperatorPlanDescription("CTE")
        {
            Properties = properties,
            Children = [(InnerOperator, null)],
        };
    }

    /// <inheritdoc/>
    protected override async IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context)
    {
        Pool pool = context.Pool;

        if (!_isMaterialized)
        {
            // Inline mode: forward inner batches as-is, or rebuild rows under the renamed
            // ColumnLookup when the CTE introduces explicit column names. RebindRowBatch is
            // not enough on its own — each Row instance carries its own ColumnLookup, so the
            // output batch must contain freshly-constructed Rows that share the renamed
            // lookup before downstream column-name lookups can find the new names.
            await foreach (RowBatch inputBatch in _innerOperator.ExecuteAsync(context).ConfigureAwait(false))
            {
                if (_explicitColumnNames is null)
                {
                    yield return inputBatch;
                    continue;
                }

                ColumnLookup renamed = RenameColumnsIfNeeded(inputBatch.ColumnLookup);
                RowBatch renamedBatch = pool.RebindRowBatch(inputBatch, renamed);

                yield return renamedBatch;
            }

            yield break;
        }

        // Materialized path: compute once, replay on subsequent calls.
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

        if (_materializedBatches is null)
        {
            yield break;
        }

        // Replay cached rows by copying values into fresh output batches whose arena is the
        // cached batch's arena (so arena-backed values resolve without an extra copy).
        // Invariant: outputBatch != null ⟺ the producer still owns it. Yielding transfers
        // ownership, so we null the local *before* yield. The finally then only fires for
        // a not-yet-yielded leftover, which protects against mid-fill exceptions (e.g. a
        // Stabilize NotSupportedException) and consumer cancellation. The post-yield
        // assignment trick wouldn't help — that statement only runs on resumption (next
        // MoveNextAsync), not on iterator disposal.
        RowBatch? outputBatch = null;
        try
        {
            foreach (RowBatch cachedBatch in _materializedBatches)
            {
                for (int i = 0; i < cachedBatch.Count; i++)
                {
                    outputBatch ??= context.RentRowBatch(outputLookup);
                    pool.RentAndCopyToOutput(cachedBatch, i, outputBatch);
                    if (outputBatch.IsFull)
                    {
                        RowBatch toYield = outputBatch;
                        outputBatch = null;
                        yield return toYield;
                    }
                }
            }

            if (outputBatch is not null)
            {
                RowBatch toYield = outputBatch;
                outputBatch = null;
                yield return toYield;
            }
        }
        finally
        {
            // Only fires for a partially-filled batch that was never yielded — typically
            // a mid-fill exception. The consumer doesn't know about it, so we own its
            // cleanup. After a successful yield the local is null and this is a no-op.
            if (outputBatch is not null)
            {
                context.ReturnRowBatch(outputBatch);
            }
        }
    }

    /// <summary>
    /// Consumes the inner operator fully, rebinding each input batch into the
    /// cached materialization. When the memory budget is exceeded, transitions to
    /// the <see cref="SpillReaderWriter"/> which consolidates payload bytes and
    /// writes row metadata to a temp file.
    /// </summary>
    private async Task MaterializeAsync(ExecutionContext context)
    {
        Pool pool = context.Pool;
        _pool = pool;
        _accountant = context.Accountant;
        _materializedBatches = [];

        // Per-row residency for the cached RowBatch: DataValue cells +
        // a Row + List<RowBatch> slot. Arena payloads each batch references
        // live in the batch's own arena (mmap, OS-paged) and aren't budgeted.
        // Computed lazily once we know fieldCount.
        long perRowBytes = 0;

        await foreach (RowBatch inputBatch in _innerOperator.ExecuteAsync(context).ConfigureAwait(false))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            context.QueryMeter?.ThrowIfExceeded();

            _materializedSchema ??= inputBatch.ColumnLookup;

            if (inputBatch.Count == 0)
            {
                context.ReturnRowBatch(inputBatch);
                continue;
            }

            if (IsSpilling)
            {
                _spiller.Write(inputBatch);
                continue;
            }

            RowBatch cacheBatch = pool.RebindRowBatch(inputBatch, _materializedSchema);
            _materializedBatches.Add(cacheBatch);

            if (perRowBytes == 0)
            {
                perRowBytes = 20L * _materializedSchema.Count + 32L;
            }
            long batchBytes = perRowBytes * cacheBatch.Count;
            context.Accountant.NotifyMaterialized(batchBytes);
            _residentBytesNotified += batchBytes;

            if (context.Accountant.WouldExceedBudget())
            {
                SpillCacheToDisk(context);
            }
        }
    }

    /// <summary>
    /// Transitions to spill mode: stands up a <see cref="SpillReaderWriter"/>, hands every
    /// cached batch to it (which stabilizes payloads into the consolidated arena, writes
    /// row metadata to disk, and returns the batches to the pool), and clears the in-memory
    /// cache. Future input batches go directly through the spiller.
    /// </summary>
    private void SpillCacheToDisk(ExecutionContext context)
    {
        // Initial-capacity hint for the consolidated arena's backing file: sum of every
        // cached batch's per-batch arena capacity, doubled for headroom. Per-batch arenas
        // already hold a representative payload mix; their sum is a good proxy for what
        // the consolidated arena will hold post-stabilize, and doubling absorbs the
        // remaining input rows that haven't been spilled yet plus the file-backed grow
        // overhead (unmap/SetLength/remap) we'd otherwise pay later.
        long initialArenaCapacity = 0;
        foreach (RowBatch buffered in _materializedBatches!)
        {
            initialArenaCapacity += buffered.Arena.Capacity;
        }
        initialArenaCapacity *= 2;

        // Arena.CreateFileBacked now takes long — pass the hint through directly.
        _spiller = new SpillReaderWriter(
            context.Pool, _materializedSchema!, context.SpillDirectory, initialArenaCapacity);

        foreach (RowBatch buffered in _materializedBatches)
        {
            _spiller.Write(buffered);
        }

        _materializedBatches = null;

        // Cached rows are now on disk; release their accounted bytes from
        // the plan-wide counter. Subsequent input batches bypass the cache
        // and write straight through the spiller.
        if (_residentBytesNotified > 0)
        {
            context.Accountant.NotifyReleased(_residentBytesNotified);
            _residentBytesNotified = 0;
        }
    }

    /// <summary>
    /// Renames the output columns if the CTE definition provides explicit column names.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ColumnLookup RenameColumnsIfNeeded(ColumnLookup columnLookup)
    {
        if (_explicitColumnNames is null)
        {
            return columnLookup;
        }

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
        if (_materializedBatches is not null && _pool is not null)
        {
            foreach (RowBatch batch in _materializedBatches)
            {
                _pool.ReturnRowBatch(batch);
            }
            _materializedBatches = null;
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
}
