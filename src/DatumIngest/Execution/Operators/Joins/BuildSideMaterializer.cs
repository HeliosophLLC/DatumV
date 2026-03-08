using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Execution.Operators.Joins;

/// <summary>
/// Drains a build-side operator into an operator-owned list of stabilized
/// <see cref="Row"/>s and tracks the lifecycle of the pool-rented backing
/// arrays. Replaces the per-call-site <c>List&lt;Row&gt; buildRows = new();</c>
/// + <c>RentAndCopyDataValues</c> + <c>foreach pool.ReturnRow</c> ritual that
/// repeats across the hash, nested-loop, and cross paths.
/// </summary>
/// <remarks>
/// <para>
/// Under one-arena-per-query, <c>Pool.RentAndCopyDataValues</c> takes the
/// same-store fast path: it allocates a fresh <see cref="DataValue"/>[]
/// rental but does not copy the underlying payloads (they already live in
/// the target store).
/// </para>
/// <para>
/// Stabilization detaches the rows from their source batches so the batches
/// can return to the pool immediately, freeing input-side memory before the
/// probe phase begins.
/// </para>
/// </remarks>
internal sealed class BuildSideMaterializer
{
    private readonly Pool _pool;
    private readonly Arena _store;
    private readonly List<Row> _rows = new();
    private MemoryAccountant? _accountant;
    private long _perRowBytes;
    private long _residentBytesNotified;

    public BuildSideMaterializer(Pool pool, Arena store)
    {
        _pool = pool;
        _store = store;
    }

    /// <summary>Number of materialized rows.</summary>
    public int Count => _rows.Count;

    /// <summary>Direct indexed access to a materialized row.</summary>
    public Row this[int index] => _rows[index];

    /// <summary>Read-only view of the materialized rows for downstream iteration.</summary>
    public IReadOnlyList<Row> Rows => _rows;

    /// <summary>
    /// Drains every batch from <paramref name="source"/>, copying each row's
    /// values into a pool-rented array stored in this materializer's target
    /// store, and returning each input batch to the pool as it is consumed.
    /// </summary>
    public async ValueTask MaterializeAsync(IQueryOperator source, ExecutionContext context)
    {
        _accountant = context.Accountant;
        await foreach (RowBatch batch in source.ExecuteAsync(context).ConfigureAwait(false))
        {
            try
            {
                if (_perRowBytes == 0 && batch.Count > 0)
                {
                    _perRowBytes = 20L * batch[0].FieldCount + 32L;
                }
                for (int i = 0; i < batch.Count; i++)
                {
                    Row sourceRow = batch[i];
                    DataValue[] copy = _pool.RentAndCopyDataValues(
                        sourceRow, batch.Arena, _store);
                    _rows.Add(new Row(sourceRow.ColumnLookup, copy));
                    _accountant.NotifyMaterialized(_perRowBytes);
                    _residentBytesNotified += _perRowBytes;
                }
            }
            finally
            {
                context.ReturnRowBatch(batch);
            }
        }
    }

    /// <summary>
    /// Returns each materialized row's backing <see cref="DataValue"/>[] to
    /// the pool and clears the list. Idempotent — safe to call from a
    /// <c>finally</c> block after an exception during materialization.
    /// </summary>
    public void Return()
    {
        foreach (Row row in _rows)
        {
            _pool.ReturnRow(row);
        }
        _rows.Clear();
        // Release the accounted residency; safe to call repeatedly because
        // we zero the counter after each release.
        if (_residentBytesNotified > 0 && _accountant is not null)
        {
            _accountant.NotifyReleased(_residentBytesNotified);
            _residentBytesNotified = 0;
        }
    }
}
