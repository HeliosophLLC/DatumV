using DatumIngest.Catalog;
using DatumIngest.Catalog.Plans;
using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Data;

/// <summary>
/// ADO.NET-style forward-only reader over a <see cref="PreparedSql"/>'s
/// row stream. Opens against either a single <see cref="StatementPlan"/>
/// (one result set, <see cref="NextResultAsync"/> always false) or a
/// <see cref="StatementBatch"/> (one result set per child, advanced by
/// <see cref="NextResultAsync"/>).
/// </summary>
/// <remarks>
/// <para>
/// The reader owns the (per-execute) <see cref="BatchContext"/> and the
/// active plan's <see cref="IAsyncEnumerator{T}"/>; disposing the reader
/// closes both. Batch lifetime is a private detail — the underlying
/// plan iterator returns the prior batch to the pool on each advance,
/// and the iterator's <c>finally</c> returns the final batch on
/// dispose. Consumers see only typed row-at-a-time accessors and never
/// touch a <see cref="RowBatch"/> directly.
/// </para>
/// <para>
/// On open, the reader prefetches the first non-empty batch of the first
/// result set so <see cref="HasRows"/>, <see cref="FieldCount"/>, and
/// <see cref="GetName(int)"/> are queryable before <see cref="ReadAsync"/>
/// is called once. Empty result sets (DDL, no-RETURNING DML) leave the
/// schema empty; <see cref="HasRows"/> reports <see langword="false"/>.
/// <see cref="NextResultAsync"/> advances to the next child (batch
/// mode), planning it against catalog state that reflects all prior
/// children's iteration, and prefetches its first batch.
/// </para>
/// <para>
/// The reader is <see cref="IAsyncDisposable"/> only — there is no
/// synchronous <c>Dispose</c> on purpose. Production code uses
/// <c>await using</c>; test code reaches sync surface via the
/// <c>InProcessDatumDbSyncExtensions</c> helper in the test assembly.
/// </para>
/// </remarks>
public sealed class InProcessDatumDbReader : IAsyncDisposable
{
    private readonly PreparedSql _prepared;
    private readonly BatchContext _batchContext;
    private readonly bool _ownsBatchContext;
    private readonly CancellationToken _cancellationToken;

    // For batch mode: the child-plan enumerator we advance via NextResultAsync.
    // Null for single-plan mode.
    private readonly IAsyncEnumerator<StatementPlan>? _childPlanEnumerator;

    // For both modes: enumerator over the current result set's batches.
    // In single-plan mode this is the only enumerator and is set at open time.
    // In batch mode this gets reset on each NextResultAsync.
    private IAsyncEnumerator<RowBatch>? _batchEnumerator;

    private RowBatch? _currentBatch;
    private int _rowIndex = -1;
    private bool _firstReadConsumed;
    private bool _closed;

    private InProcessDatumDbReader(
        PreparedSql prepared,
        BatchContext batchContext,
        bool ownsBatchContext,
        CancellationToken cancellationToken,
        IAsyncEnumerator<StatementPlan>? childPlanEnumerator,
        IAsyncEnumerator<RowBatch>? batchEnumerator,
        RowBatch? prefetched)
    {
        _prepared = prepared;
        _batchContext = batchContext;
        _ownsBatchContext = ownsBatchContext;
        _cancellationToken = cancellationToken;
        _childPlanEnumerator = childPlanEnumerator;
        _batchEnumerator = batchEnumerator;
        _currentBatch = prefetched;
    }

    internal static async Task<InProcessDatumDbReader> OpenAsync(
        PreparedSql prepared,
        BatchContext batchContext,
        bool ownsBatchContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(batchContext);

        try
        {
            return prepared switch
            {
                StatementPlan plan => await OpenSinglePlanAsync(
                    plan, batchContext, ownsBatchContext, cancellationToken).ConfigureAwait(false),
                StatementBatch batch => await OpenBatchAsync(
                    batch, batchContext, ownsBatchContext, cancellationToken).ConfigureAwait(false),
                _ => throw new NotSupportedException(
                    $"InProcessDatumDbReader: unknown PreparedSql subtype '{prepared.GetType().Name}'."),
            };
        }
        catch
        {
            if (ownsBatchContext) batchContext.Dispose();
            throw;
        }
    }

    private static async Task<InProcessDatumDbReader> OpenSinglePlanAsync(
        StatementPlan plan, BatchContext batchContext, bool ownsBatchContext, CancellationToken ct)
    {
        IAsyncEnumerator<RowBatch> batchEnumerator = plan
            .ExecuteAsync(ct, batchContext)
            .GetAsyncEnumerator(ct);
        RowBatch? prefetched = await PrefetchFirstBatchAsync(batchEnumerator).ConfigureAwait(false);
        return new InProcessDatumDbReader(
            plan, batchContext, ownsBatchContext, ct,
            childPlanEnumerator: null,
            batchEnumerator: batchEnumerator,
            prefetched);
    }

    private static async Task<InProcessDatumDbReader> OpenBatchAsync(
        StatementBatch batch, BatchContext batchContext, bool ownsBatchContext, CancellationToken ct)
    {
        IAsyncEnumerator<StatementPlan> childPlanEnumerator = batch
            .StreamChildPlansAsync(ct, batchContext)
            .GetAsyncEnumerator(ct);

        // Open the first child's batch enumerator. If the batch is empty
        // (shouldn't be — ctor enforces N >= 1), surface as empty result set.
        IAsyncEnumerator<RowBatch>? batchEnumerator = null;
        RowBatch? prefetched = null;
        if (await childPlanEnumerator.MoveNextAsync().ConfigureAwait(false))
        {
            StatementPlan firstChild = childPlanEnumerator.Current;
            batchEnumerator = firstChild.ExecuteAsync(ct, batchContext).GetAsyncEnumerator(ct);
            prefetched = await PrefetchFirstBatchAsync(batchEnumerator).ConfigureAwait(false);
        }

        return new InProcessDatumDbReader(
            batch, batchContext, ownsBatchContext, ct,
            childPlanEnumerator,
            batchEnumerator,
            prefetched);
    }

    private static async Task<RowBatch?> PrefetchFirstBatchAsync(IAsyncEnumerator<RowBatch> enumerator)
    {
        while (await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            if (enumerator.Current.Count > 0) return enumerator.Current;
        }
        return null;
    }

    /// <summary>The prepared unit being iterated.</summary>
    public PreparedSql Prepared => _prepared;

    /// <summary>
    /// Number of columns in the current result set, or <c>0</c> when the
    /// current result set yields no rows.
    /// </summary>
    public int FieldCount => _currentBatch?.ColumnLookup.ColumnNames.Count ?? 0;

    /// <summary>Whether the current result set will yield at least one row.</summary>
    public bool HasRows => _currentBatch is not null;

    /// <summary>
    /// Rows affected count surfaced by the underlying plan. Always
    /// <c>-1</c> today — DML plans do not yet expose a count; SELECT and
    /// DDL legitimately return <c>-1</c>.
    /// </summary>
    public int RecordsAffected => -1;

    /// <summary>
    /// Advances to the next row in the current result set. Returns
    /// <see langword="true"/> when a row is available;
    /// <see langword="false"/> at end of the current result set. To move
    /// to the next result set (batch mode), call
    /// <see cref="NextResultAsync"/>.
    /// </summary>
    public async ValueTask<bool> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (_closed) return false;
        cancellationToken.ThrowIfCancellationRequested();

        // First call: the prefetched batch (if any) sits at index -1; expose row 0.
        if (!_firstReadConsumed)
        {
            _firstReadConsumed = true;
            if (_currentBatch is null) return false;
            _rowIndex = 0;
            return true;
        }

        // Same batch, more rows.
        if (_currentBatch is not null && _rowIndex + 1 < _currentBatch.Count)
        {
            _rowIndex++;
            return true;
        }

        // Need a new batch. Advancing the enumerator returns the prior
        // batch to the pool via the plan's iterator finally.
        if (_batchEnumerator is null)
        {
            return false;
        }
        while (await _batchEnumerator.MoveNextAsync().ConfigureAwait(false))
        {
            if (_batchEnumerator.Current.Count == 0) continue;
            _currentBatch = _batchEnumerator.Current;
            _rowIndex = 0;
            return true;
        }

        _currentBatch = null;
        _rowIndex = -1;
        return false;
    }

    /// <summary>
    /// Advances to the next result set. Returns <see langword="true"/>
    /// when a new result set is available (and a fresh
    /// <see cref="ReadAsync"/> loop should begin against it);
    /// <see langword="false"/> when no more result sets exist.
    /// </summary>
    /// <remarks>
    /// Single-plan readers always return <see langword="false"/> — they
    /// represent one result set. Batch readers plan the next child against
    /// catalog state that already reflects all prior children's
    /// iteration, then prefetch its first batch.
    /// </remarks>
    public async ValueTask<bool> NextResultAsync(CancellationToken cancellationToken = default)
    {
        if (_closed) return false;
        cancellationToken.ThrowIfCancellationRequested();

        if (_childPlanEnumerator is null)
        {
            // Single-plan mode: drain the remaining batches so the plan's
            // finally fires (returning the last batch to the pool), then
            // signal end-of-results.
            await DrainCurrentResultSetAsync().ConfigureAwait(false);
            return false;
        }

        // Batch mode: drain the current child's remaining batches, dispose
        // its enumerator (firing its finally), then advance to the next.
        await DrainCurrentResultSetAsync().ConfigureAwait(false);
        if (_batchEnumerator is not null)
        {
            await _batchEnumerator.DisposeAsync().ConfigureAwait(false);
            _batchEnumerator = null;
        }

        if (!await _childPlanEnumerator.MoveNextAsync().ConfigureAwait(false))
        {
            _currentBatch = null;
            _rowIndex = -1;
            _firstReadConsumed = false;
            return false;
        }

        StatementPlan nextChild = _childPlanEnumerator.Current;
        _batchEnumerator = nextChild.ExecuteAsync(_cancellationToken, _batchContext).GetAsyncEnumerator(_cancellationToken);
        _currentBatch = await PrefetchFirstBatchAsync(_batchEnumerator).ConfigureAwait(false);
        _rowIndex = -1;
        _firstReadConsumed = false;
        return true;
    }

    private async ValueTask DrainCurrentResultSetAsync()
    {
        if (_batchEnumerator is null) return;
        while (await _batchEnumerator.MoveNextAsync().ConfigureAwait(false))
        {
            // Discard — the plan's iterator returns each prior batch on advance.
        }
    }

    /// <summary>Column name at <paramref name="ordinal"/>.</summary>
    public string GetName(int ordinal) => CurrentBatch().ColumnLookup.ColumnNames[ordinal];

    /// <summary>Resolves a column name to its ordinal. Throws when the name is unknown.</summary>
    public int GetOrdinal(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        ColumnLookup lookup = CurrentBatch().ColumnLookup;
        if (lookup.NameIndex.TryGetValue(name, out int ord)) return ord;
        throw new ArgumentException(
            $"InProcessDatumDbReader: no column named '{name}'.", nameof(name));
    }

    /// <summary>The runtime <see cref="DataKind"/> of the column.</summary>
    public DataKind GetDataKind(int ordinal) => CurrentRow()[ordinal].Kind;

    /// <summary>Returns <see langword="true"/> when the cell is SQL NULL.</summary>
    public bool IsDBNull(int ordinal) => CurrentRow()[ordinal].IsNull;

    /// <summary>Returns the raw <see cref="DataValue"/> for the current row + ordinal.</summary>
    public DataValue GetValue(int ordinal) => CurrentRow()[ordinal];

    /// <summary>Returns the cell as a 32-bit integer.</summary>
    public int GetInt32(int ordinal) => CurrentRow()[ordinal].AsInt32();

    /// <summary>Returns the cell as a 64-bit integer.</summary>
    public long GetInt64(int ordinal) => CurrentRow()[ordinal].AsInt64();

    /// <summary>Returns the cell as a 32-bit float.</summary>
    public float GetFloat(int ordinal) => CurrentRow()[ordinal].AsFloat32();

    /// <summary>Returns the cell as a 64-bit float.</summary>
    public double GetDouble(int ordinal) => CurrentRow()[ordinal].AsFloat64();

    /// <summary>Returns the cell as a boolean.</summary>
    public bool GetBoolean(int ordinal) => CurrentRow()[ordinal].AsBoolean();

    /// <summary>Returns the cell as a materialised string.</summary>
    public string GetString(int ordinal) => CurrentRow()[ordinal].AsString(CurrentBatch().Arena);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_closed) return;
        _closed = true;
        if (_batchEnumerator is not null)
        {
            await _batchEnumerator.DisposeAsync().ConfigureAwait(false);
            _batchEnumerator = null;
        }
        if (_childPlanEnumerator is not null)
        {
            await _childPlanEnumerator.DisposeAsync().ConfigureAwait(false);
        }
        if (_ownsBatchContext) _batchContext.Dispose();
    }

    private RowBatch CurrentBatch()
    {
        if (_currentBatch is null)
        {
            throw new InvalidOperationException(
                _closed
                    ? "InProcessDatumDbReader is closed."
                    : "No current row. Call ReadAsync()/Read() and check the return value before accessing columns.");
        }
        return _currentBatch;
    }

    private Row CurrentRow()
    {
        if (_currentBatch is null || _rowIndex < 0)
        {
            throw new InvalidOperationException(
                "No current row. Call ReadAsync()/Read() and check the return value before accessing columns.");
        }
        return _currentBatch[_rowIndex];
    }
}
