using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Execution.Operators.Windows;

/// <summary>
/// Drains an input operator's batches into a long-lived <see cref="List{Row}"/>
/// stabilised into <see cref="ExecutionContext.Store"/>. Used by the blocking
/// window-shaped operators (<see cref="WindowOperator"/>,
/// <see cref="FoldScanOperator"/>) which must see every row before any output
/// can be emitted because partition assignment and per-partition sort depend on
/// the full input.
/// </summary>
/// <remarks>
/// <para>
/// <b>Memory bound (Tier 2).</b> Reports each materialised row's structural
/// residency to the plan-wide <see cref="MemoryAccountant"/>; when
/// <see cref="MemoryAccountant.WouldExceedBudget"/> returns true, an
/// <see cref="ExecutionException"/> is thrown rather than silently OOMing.
/// Spill-to-disk for these operators is on the roadmap (Tier 3 / Tier 4).
/// </para>
/// <para>
/// <b>Arena lifetime.</b> Under one-arena-per-query, all input/output values
/// share <see cref="ExecutionContext.Store"/>, so
/// <c>Pool.RentAndCopyDataValues</c> hits its same-store fast path
/// (DataValue[] rental, no payload copy) and the materialised rows survive
/// each input batch's return because <see cref="ExecutionContext.Store"/>
/// outlives every batch.
/// </para>
/// <para>
/// <b>Cleanup.</b> <see cref="Dispose"/> returns every row's
/// <see cref="DataValue"/>[] to the pool and releases the accountant residency.
/// Idempotent; safe to call from a <c>finally</c> block on exception paths.
/// </para>
/// </remarks>
internal sealed class MaterializedInput : IDisposable
{
    private readonly ExecutionContext _context;
    private readonly Pool _pool;
    private readonly string _operatorLabel;

    private long _perRowBytes;
    private long _residentBytesNotified;
    private bool _disposed;

    /// <param name="context">The execution context whose arena and accountant own this materialisation.</param>
    /// <param name="operatorLabel">Operator name used in OOM messages (e.g. <c>"WINDOW"</c>, <c>"FOLD/SCAN"</c>).</param>
    public MaterializedInput(ExecutionContext context, string operatorLabel)
    {
        _context = context;
        _pool = context.Pool;
        _operatorLabel = operatorLabel;
    }

    /// <summary>The materialised rows in their original input order.</summary>
    public List<Row> Rows { get; } = new();

    /// <summary>The first non-empty input batch's <see cref="ColumnLookup"/>; null if no input rows.</summary>
    public ColumnLookup? SourceLookup { get; private set; }

    /// <summary>
    /// Drains <paramref name="source"/> into <see cref="Rows"/>, stabilising every
    /// row into <see cref="ExecutionContext.Store"/>. Per-row residency is reported
    /// to the plan-wide <see cref="MemoryAccountant"/>; throws
    /// <see cref="ExecutionException"/> when the budget is exceeded.
    /// </summary>
    public async ValueTask CollectAsync(IAsyncEnumerable<RowBatch> source)
    {
        await foreach (RowBatch batch in source.ConfigureAwait(false))
        {
            try
            {
                if (SourceLookup is null && batch.Count > 0)
                {
                    SourceLookup = batch.ColumnLookup;
                    // DataValue.SizeBytes (32) per cell + ~32 bytes for the Row + List<Row> slot.
                    // Arena payloads live in context.Store (mmap, OS-paged) and aren't budgeted.
                    _perRowBytes = DataValue.SizeBytes * (long)SourceLookup.Count + 32L;
                }

                for (int i = 0; i < batch.Count; i++)
                {
                    _context.CancellationToken.ThrowIfCancellationRequested();

                    Row sourceRow = batch[i];
                    DataValue[] copy = _pool.RentAndCopyDataValues(
                        sourceRow, batch.Arena, _context.Store);
                    // Preserve each row's own ColumnLookup — source batches could
                    // in principle yield different per-batch ColumnLookup references
                    // for the same logical schema (e.g. UNION ALL of two scans).
                    Rows.Add(new Row(sourceRow.ColumnLookup, copy));

                    _context.Accountant.NotifyMaterialized(_perRowBytes);
                    _residentBytesNotified += _perRowBytes;

                    if (_context.Accountant.WouldExceedBudget())
                    {
                        long budget = _context.Accountant.MemoryBudgetBytes ?? 0;
                        throw new ExecutionException(
                            $"{_operatorLabel} exceeded memory budget of {budget} bytes "
                            + $"after materialising {Rows.Count} input rows. {_operatorLabel} currently "
                            + $"buffers the entire input in memory; spill-to-disk for this operator "
                            + $"is on the roadmap.");
                    }
                }
            }
            finally
            {
                _context.ReturnRowBatch(batch);
            }
        }
    }

    /// <summary>
    /// Returns every materialised row's <see cref="DataValue"/>[] to the pool and
    /// releases the accountant residency. Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_residentBytesNotified > 0)
        {
            _context.Accountant.NotifyReleased(_residentBytesNotified);
            _residentBytesNotified = 0;
        }

        foreach (Row row in Rows)
        {
            _pool.ReturnRow(row);
        }
        Rows.Clear();
    }
}
