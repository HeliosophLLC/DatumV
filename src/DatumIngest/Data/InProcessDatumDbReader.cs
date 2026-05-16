using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Data;

/// <summary>
/// ADO.NET-style forward-only reader over a <see cref="StatementPlan"/>'s
/// row stream. The reader owns the plan's <see cref="IAsyncEnumerator{T}"/>
/// and the (per-execute) <see cref="BatchContext"/>; disposing the reader
/// closes both. Batch lifetime is a private detail — the
/// <see cref="StatementPlan"/>'s iterator returns the prior batch to the
/// pool on each advance, and the iterator's <c>finally</c> returns the
/// final batch on dispose. Consumers see only typed row-at-a-time
/// accessors and never touch a <see cref="RowBatch"/> directly.
/// </summary>
/// <remarks>
/// <para>
/// On open the reader prefetches the first non-empty batch so
/// <see cref="HasRows"/>, <see cref="FieldCount"/>, and
/// <see cref="GetName(int)"/> are queryable before
/// <see cref="ReadAsync"/> is called once. Plans that yield no rows (DDL,
/// no-RETURNING DML) leave the schema empty; <see cref="HasRows"/> reports
/// <see langword="false"/>.
/// </para>
/// <para>
/// <see cref="NextResult"/> always returns <see langword="false"/> in v1
/// — one statement per command. Multi-statement support arrives with the
/// batch-plan feature.
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
    private readonly StatementPlan _plan;
    private readonly BatchContext _batchContext;
    private readonly bool _ownsBatchContext;
    private readonly IAsyncEnumerator<RowBatch> _enumerator;

    private RowBatch? _currentBatch;
    private int _rowIndex = -1;
    private bool _firstReadConsumed;
    private bool _closed;

    private InProcessDatumDbReader(
        StatementPlan plan,
        BatchContext batchContext,
        bool ownsBatchContext,
        IAsyncEnumerator<RowBatch> enumerator,
        RowBatch? prefetched)
    {
        _plan = plan;
        _batchContext = batchContext;
        _ownsBatchContext = ownsBatchContext;
        _enumerator = enumerator;
        _currentBatch = prefetched;
        // rowIndex stays at -1 until the first ReadAsync; the prefetched
        // batch (if any) sits ready to serve schema queries.
    }

    internal static async Task<InProcessDatumDbReader> OpenAsync(
        StatementPlan plan,
        BatchContext batchContext,
        bool ownsBatchContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(batchContext);

        IAsyncEnumerator<RowBatch> enumerator = plan
            .ExecuteAsync(cancellationToken, batchContext)
            .GetAsyncEnumerator(cancellationToken);

        // Prefetch the first non-empty batch so schema queries answer
        // before the consumer's first ReadAsync.
        RowBatch? prefetched = null;
        try
        {
            while (await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                if (enumerator.Current.Count > 0)
                {
                    prefetched = enumerator.Current;
                    break;
                }
            }
        }
        catch
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
            if (ownsBatchContext) batchContext.Dispose();
            throw;
        }

        return new InProcessDatumDbReader(plan, batchContext, ownsBatchContext, enumerator, prefetched);
    }

    /// <summary>The plan being iterated.</summary>
    public StatementPlan Plan => _plan;

    /// <summary>
    /// Number of columns in the current result set, or <c>0</c> when the
    /// plan yields no rows.
    /// </summary>
    public int FieldCount => _currentBatch?.ColumnLookup.ColumnNames.Count ?? 0;

    /// <summary>Whether the plan will yield at least one row.</summary>
    public bool HasRows => _currentBatch is not null;

    /// <summary>
    /// Rows affected count surfaced by the underlying plan.
    /// Always <c>-1</c> today — DML plans do not yet expose a count;
    /// SELECT and DDL legitimately return <c>-1</c>.
    /// </summary>
    public int RecordsAffected => -1;

    /// <summary>
    /// Advances to the next row. Returns <see langword="true"/> when a row
    /// is available; <see langword="false"/> at end-of-stream. After
    /// returning <see langword="false"/>, accessor calls throw.
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

        // Need a new batch. Advancing the enumerator returns the prior batch
        // to the pool via the plan's internal iterator-finally — the reader
        // owns no batch lifecycle past handing it back.
        while (await _enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            if (_enumerator.Current.Count == 0) continue;
            _currentBatch = _enumerator.Current;
            _rowIndex = 0;
            return true;
        }

        _currentBatch = null;
        _rowIndex = -1;
        return false;
    }

    /// <summary>
    /// Always <see langword="false"/> in v1 — one statement per command.
    /// Multi-statement support arrives with the batch-plan feature.
    /// </summary>
    public bool NextResult() => false;

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
        // Disposing the enumerator fires the plan's finally — the last
        // yielded batch is returned to the pool there.
        await _enumerator.DisposeAsync().ConfigureAwait(false);
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
