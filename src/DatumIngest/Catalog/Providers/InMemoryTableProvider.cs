using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using DatumIngest.Indexing;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// In-memory table provider for tests and small fixtures. Stores rows as raw
/// <c>object[]</c> arrays and materializes each cell into a <see cref="DataValue"/>
/// at scan time, writing string and byte-array payloads into the batch's
/// <see cref="Arena"/> so downstream consumers can resolve reference-type values
/// through <c>batch.Arena</c> the same way they do with <c>DatumFileTableProvider</c>.
/// </summary>
/// <remarks>
/// Cells can be any of:
/// <list type="bullet">
///   <item><description>A boxed CLR primitive (<c>int</c>, <c>string</c>, <c>bool</c>, <see cref="DateTimeOffset"/>, <see cref="Guid"/>, <c>byte[]</c>, etc.)</description></item>
///   <item><description>A <see cref="DataValue"/> (passed through unchanged — useful when a test needs to control the exact DataValue flavor, e.g. verifying non-inline string handling)</description></item>
///   <item><description><c>null</c> (materialized to <see cref="DataValue.Null"/> using the column's inferred kind)</description></item>
/// </list>
/// The legacy <c>Row[]</c> constructors are preserved for migration and internally
/// convert each <see cref="Row"/> into an <c>object[]</c> of boxed <see cref="DataValue"/>s;
/// downstream materialization uses the same passthrough path.
/// </remarks>
public sealed class InMemoryTableProvider : ITableProvider
{
    private const int DefaultBatchSize = 64;

    private Pool? _pool;
    private string[] _columns;
    private object?[][] _rows;
    private Schema _schema;
    private ColumnLookup _fullLookup;
    private readonly bool _indexEnabled;
    private readonly Lazy<SourceIndex?>? _lazySourceIndex;
    private SourceIndex? _overrideIndex;

    // IDENTITY state (PR10e). Captured from the schema-only ctor when
    // a column carries IdentitySpec; -1 otherwise. _identityNextValue
    // starts at the spec's seed and advances per ReserveNextIdentityValue
    // call (sessions hold the mutation lock so concurrent INSERTs
    // serialize). Lives in-memory only — temp tables die with the catalog.
    private int _identityColumnIndex = -1;
    private long _identitySeed;
    private long _identityStep;
    private long _identityNextValue;

    /// <summary>
    /// Serializes mutations and append sessions across async awaits.
    /// A session holds the permit for its entire lifetime so two
    /// writers can't overlap on the same provider; mutations
    /// (AddColumn / DropColumn / DeleteRows) take the permit for the
    /// duration of the swap. Read paths capture the current
    /// <c>_rows</c> / <c>_columns</c> / <c>_schema</c> references into
    /// locals at entry — a concurrent mutation that replaces the
    /// references afterward is invisible to the in-flight scan,
    /// mirroring the snapshot semantics used by
    /// <see cref="DatumFileTableProviderV2"/>.
    /// </summary>
    private readonly SemaphoreSlim _mutationLock = new(1, 1);

    /// <summary>
    /// Creates a provider from explicit column names and raw <c>object[]</c> rows.
    /// This is the primary API. Each cell is converted to a <see cref="DataValue"/>
    /// at scan time — see the class remarks for supported cell types.
    /// </summary>
    /// <param name="pool">The buffer pool used to rent row batches.</param>
    /// <param name="name">The logical table name.</param>
    /// <param name="columns">Column names, in the order cells appear in each row.</param>
    /// <param name="rows">Rows as <c>object?[]</c> arrays with one cell per column.</param>
    /// <param name="indexEnabled">
    /// When <c>true</c> (default), <see cref="GetSourceIndex"/> lazily builds a
    /// <see cref="SourceIndex"/> from the backing rows on first access. Set to
    /// <c>false</c> to verify the no-index scan fallback.
    /// </param>
    public InMemoryTableProvider(
        Pool pool,
        string name,
        string[] columns,
        object?[][] rows,
        bool indexEnabled = true)
    {
        _pool = pool;
        Name = name;
        _columns = columns;
        _rows = rows;
        _schema = BuildSchema(_columns, _rows);
        _fullLookup = new ColumnLookup(_columns);
        _indexEnabled = indexEnabled;
        _lazySourceIndex = indexEnabled
            ? new Lazy<SourceIndex?>(BuildIndex, LazyThreadSafetyMode.ExecutionAndPublication)
            : null;
    }

    /// <summary>
    /// Creates an empty provider whose schema is declared up front — for
    /// <c>CREATE TEMP TABLE</c> bodies that need a table with declared
    /// types but zero rows. Subsequent <c>INSERT</c>s populate the rows.
    /// </summary>
    /// <param name="pool">The buffer pool used to rent row batches.</param>
    /// <param name="name">The logical table name.</param>
    /// <param name="schema">
    /// The column descriptors for the empty table. Cell types in
    /// subsequent inserts must be consistent with each column's
    /// declared <see cref="ColumnInfo.Kind"/>.
    /// </param>
    public InMemoryTableProvider(Pool pool, string name, Schema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        _pool = pool;
        Name = name;
        _columns = schema.Columns.Select(c => c.Name).ToArray();
        _rows = [];
        _schema = schema;
        _fullLookup = new ColumnLookup(_columns);
        _indexEnabled = false; // empty table — no point auto-building.
        _lazySourceIndex = null;

        // Capture IDENTITY state from the schema. The catalog already
        // validates "at most one IDENTITY column"; we trust the input.
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            if (schema.Columns[i].Identity is { } spec)
            {
                _identityColumnIndex = i;
                _identitySeed = spec.Seed;
                _identityStep = spec.Step;
                _identityNextValue = spec.Seed;
                break;
            }
        }
    }

    /// <summary>
    /// Creates a provider from a sequence of <see cref="Row"/>s. Column names are
    /// derived from the first row's <see cref="Row.ColumnNames"/>; schema kinds are
    /// inferred from the first non-null <see cref="DataValue"/> per column. Each row
    /// is stored internally as an <c>object[]</c> of boxed DataValues — migration path
    /// for existing tests.
    /// </summary>
    /// <param name="pool">The buffer pool used to rent row batches.</param>
    /// <param name="name">The logical table name.</param>
    /// <param name="rows">The rows to serve.</param>
    public InMemoryTableProvider(Pool pool, string name, Row[] rows)
        : this(pool, name,
            columns: rows.Length == 0 ? [] : rows[0].ColumnNames.ToArray(),
            rows: ConvertRows(rows, rows.Length == 0 ? [] : rows[0].ColumnNames.ToArray()))
    {
    }

    /// <summary>
    /// Creates a provider from explicit column names and a sequence of <see cref="Row"/>s.
    /// Use when the first row's column names don't represent the full schema.
    /// </summary>
    public InMemoryTableProvider(Pool pool, string name, string[] columns, Row[] rows)
        : this(pool, name, columns, ConvertRows(rows, columns))
    {
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public bool Seekable => true;

    /// <summary>
    /// Gets whether <see cref="Dispose"/> has been called on this provider.
    /// </summary>
    [MemberNotNullWhen(false, nameof(_pool))]
    public bool Disposed { get; private set; }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Disposed) return;

        _pool = null;
        Disposed = true;
    }

    /// <inheritdoc/>
    public long GetRowCount() => _rows.Length;

    /// <inheritdoc/>
    public Schema GetSchema() => _schema;

    /// <inheritdoc/>
    public Manifest.QueryResultsManifest? GetManifest() => null;

    /// <inheritdoc/>
    /// <remarks>
    /// Returns (in priority order): the index set via <see cref="ProvideSourceIndex"/>
    /// if any; otherwise the lazily-built index from the backing rows when indexing is
    /// enabled; otherwise <c>null</c>.
    /// </remarks>
    public SourceIndex? GetSourceIndex()
    {
        if (_overrideIndex is not null)
        {
            return _overrideIndex;
        }

        return _lazySourceIndex?.Value;
    }

    /// <summary>
    /// Overrides the source index returned by <see cref="GetSourceIndex"/> with a
    /// caller-supplied instance. Used by tests that need to verify index-consuming
    /// operators against hand-constructed <see cref="SourceIndex"/> fixtures (e.g.
    /// specific bloom-filter shapes for pruning tests).
    /// </summary>
    /// <remarks>
    /// Replaces the lazy-built index if any. Subsequent <see cref="GetSourceIndex"/>
    /// calls return <paramref name="index"/> until this method is called again.
    /// </remarks>
    public void ProvideSourceIndex(SourceIndex index)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        _overrideIndex = index;
    }

    /// <summary>
    /// Builds a <see cref="SourceIndex"/> from the backing rows by feeding them into
    /// an <see cref="IncrementalIndexBuilder"/>. Auto-selects bloom filters on all
    /// columns plus sorted/bitmap indexes on compact columns (the same auto-index
    /// policy the production indexer uses). HLL cardinality is disabled for determinism.
    /// </summary>
    private SourceIndex? BuildIndex()
    {
        if (_rows.Length == 0)
        {
            return null;
        }

        SourceFingerprint fingerprint = new(fileSize: 0, new byte[32]);
        SourceIndexBuilder builder = new(
            bloomAllColumns: true,
            computeCardinality: false);

        IncrementalIndexBuilder incremental = builder.CreateIncrementalBuilder(fingerprint);
        using Arena buildArena = new();

        DataKind[] kinds = new DataKind[_columns.Length];
        for (int c = 0; c < _columns.Length; c++)
        {
            kinds[c] = _schema.Columns[c].Kind;
        }

        foreach (object?[] rawRow in _rows)
        {
            DataValue[] values = new DataValue[_columns.Length];
            for (int c = 0; c < _columns.Length; c++)
            {
                object? cell = c < rawRow.Length ? rawRow[c] : null;
                values[c] = MaterializeCell(cell, kinds[c], buildArena);
            }

            Row row = new(_fullLookup, values);
            incremental.AddRow(row, buildArena);
        }

        return incremental.Finalize();
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> ScanAsync(
        IReadOnlySet<string>? requiredColumns,
        Expression? filterHint,
        Arena? targetArena,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Model.TypeIdTranslationTable? typeIdTranslations = null)
    {
        // typeIdTranslations is unused — in-memory rows already carry runtime
        // TypeIds (no on-disk → runtime gap to bridge). Parameter exists to
        // satisfy the ITableProvider contract.
        _ = typeIdTranslations;
        ObjectDisposedException.ThrowIf(Disposed, this);

        // filterHint is advisory for zone-map pruning. An in-memory table has no
        // partitions, so the hint is unused — the caller applies the filter downstream.
        Projection projection = ResolveProjection(requiredColumns);

        await foreach (RowBatch batch in EmitRows(
            _pool, projection, _rows, targetArena, cancellationToken).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    /// <inheritdoc/>
    public ISeekSession OpenSeekSession(IReadOnlySet<string>? requiredColumns, Arena? targetArena = null)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        return new InMemorySeekSession(_pool, ResolveProjection(requiredColumns), _rows, targetArena);
    }

    // ──────────────────── Mutation (catalog-level ALTER TABLE) ────────────────────

    /// <inheritdoc/>
    public bool CanAlterColumns => true;

    /// <inheritdoc/>
    public bool CanAppendRows => true;

    /// <inheritdoc/>
    public bool CanDeleteRows => true;

    /// <inheritdoc/>
    public bool CanUpdateRows => true;

    /// <inheritdoc/>
    public void AddColumn(ColumnInfo column)
    {
        ArgumentNullException.ThrowIfNull(column);
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (!column.Nullable)
        {
            throw new ArgumentException(
                $"AddColumn requires column '{column.Name}' to be nullable: existing rows are " +
                "back-filled with nulls.",
                nameof(column));
        }

        _mutationLock.Wait();
        try
        {
            for (int i = 0; i < _columns.Length; i++)
            {
                if (string.Equals(_columns[i], column.Name, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Column '{column.Name}' is already present in table '{Name}'.");
                }
            }

            // Append the column slot, back-fill existing rows with null.
            string[] newColumns = new string[_columns.Length + 1];
            Array.Copy(_columns, newColumns, _columns.Length);
            newColumns[^1] = column.Name;

            object?[][] newRows = new object?[_rows.Length][];
            for (int r = 0; r < _rows.Length; r++)
            {
                object?[] oldRow = _rows[r];
                object?[] newRow = new object?[oldRow.Length + 1];
                Array.Copy(oldRow, newRow, oldRow.Length);
                newRow[^1] = null;
                newRows[r] = newRow;
            }

            ColumnInfo[] newSchemaColumns = new ColumnInfo[_schema.Columns.Count + 1];
            for (int i = 0; i < _schema.Columns.Count; i++) newSchemaColumns[i] = _schema.Columns[i];
            newSchemaColumns[^1] = column;

            // PK indices stay valid: the new column is appended past
            // the existing range, so no shift is needed.
            int[]? carriedPkIndices = _schema.PrimaryKeyColumnIndices.Count > 0
                ? _schema.PrimaryKeyColumnIndices.ToArray()
                : null;

            _columns = newColumns;
            _rows = newRows;
            _schema = new Schema(newSchemaColumns, carriedPkIndices);
            _fullLookup = new ColumnLookup(_columns);
            _overrideIndex = null;
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <inheritdoc/>
    public void DropColumn(string columnName)
    {
        ArgumentNullException.ThrowIfNull(columnName);
        ObjectDisposedException.ThrowIf(Disposed, this);

        _mutationLock.Wait();
        try
        {
            int dropIndex = -1;
            for (int i = 0; i < _columns.Length; i++)
            {
                if (string.Equals(_columns[i], columnName, StringComparison.OrdinalIgnoreCase))
                {
                    dropIndex = i;
                    break;
                }
            }
            if (dropIndex < 0)
            {
                throw new InvalidOperationException(
                    $"Column '{columnName}' is not present in table '{Name}'.");
            }

            string[] newColumns = new string[_columns.Length - 1];
            for (int i = 0, j = 0; i < _columns.Length; i++)
            {
                if (i == dropIndex) continue;
                newColumns[j++] = _columns[i];
            }

            object?[][] newRows = new object?[_rows.Length][];
            for (int r = 0; r < _rows.Length; r++)
            {
                object?[] oldRow = _rows[r];
                object?[] newRow = new object?[oldRow.Length - 1];
                for (int i = 0, j = 0; i < oldRow.Length; i++)
                {
                    if (i == dropIndex) continue;
                    newRow[j++] = oldRow[i];
                }
                newRows[r] = newRow;
            }

            ColumnInfo[] newSchemaColumns = new ColumnInfo[_schema.Columns.Count - 1];
            for (int i = 0, j = 0; i < _schema.Columns.Count; i++)
            {
                if (i == dropIndex) continue;
                newSchemaColumns[j++] = _schema.Columns[i];
            }

            // Carry forward PRIMARY KEY indices, shifting any index past
            // the dropped position down by one. The catalog rejects
            // dropping a PK column itself (so dropIndex is never in the
            // PK set here), but we still need the shift for indices to
            // the right of the drop.
            int[]? newPkIndices = null;
            if (_schema.PrimaryKeyColumnIndices.Count > 0)
            {
                int[] shifted = new int[_schema.PrimaryKeyColumnIndices.Count];
                for (int p = 0; p < shifted.Length; p++)
                {
                    int oldIdx = _schema.PrimaryKeyColumnIndices[p];
                    shifted[p] = oldIdx > dropIndex ? oldIdx - 1 : oldIdx;
                }
                newPkIndices = shifted;
            }

            _columns = newColumns;
            _rows = newRows;
            _schema = new Schema(newSchemaColumns, newPkIndices);
            _fullLookup = new ColumnLookup(_columns);
            _overrideIndex = null;
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <inheritdoc/>
    public IAppendSession BeginAppend()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        _mutationLock.Wait();
        try
        {
            return new InMemoryAppendSession(this);
        }
        catch
        {
            _mutationLock.Release();
            throw;
        }
    }

    /// <inheritdoc/>
    public void UpdateRows(IReadOnlyList<RowUpdateRequest> requests, IValueStore? sourceStore = null)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (requests.Count == 0) return;

        _mutationLock.Wait();
        try
        {
            // Validate up-front so a partial mutation never lands.
            foreach (RowUpdateRequest req in requests)
            {
                if (req.LiveRowIndex < 0 || req.LiveRowIndex >= _rows.Length)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(requests), req.LiveRowIndex,
                        $"UpdateRows: row index {req.LiveRowIndex} out of range for table " +
                        $"'{Name}' (row count {_rows.Length}).");
                }
                foreach (int columnIndex in req.NewValues.Keys)
                {
                    if (columnIndex < 0 || columnIndex >= _columns.Length)
                    {
                        throw new ArgumentOutOfRangeException(
                            nameof(requests), columnIndex,
                            $"UpdateRows: column index {columnIndex} out of range for table " +
                            $"'{Name}' (column count {_columns.Length}).");
                    }
                }
            }

            // Apply: copy-on-write per affected row so concurrent scan
            // iterators reading _rows pre-swap see the old values, and
            // the swap in _rows = newRows publishes the post-update view.
            object?[][] newRows = new object?[_rows.Length][];
            HashSet<long> touchedRows = new(requests.Count);
            foreach (RowUpdateRequest req in requests) touchedRows.Add(req.LiveRowIndex);
            for (int r = 0; r < _rows.Length; r++)
            {
                if (touchedRows.Contains(r))
                {
                    object?[] copy = new object?[_rows[r].Length];
                    Array.Copy(_rows[r], copy, _rows[r].Length);
                    newRows[r] = copy;
                }
                else
                {
                    newRows[r] = _rows[r];
                }
            }

            // ConvertDataValueToCell needs an Arena for resolving
            // non-inline payloads (strings, byte arrays). UPDATE executors
            // pass an Arena as sourceStore; if a caller hands us an
            // IValueStore that isn't an Arena the resolution falls back
            // to a fresh local arena (works for inline values and arena-
            // backed values supplied via AsString(arena)/AsUInt8Array(arena)
            // — the latter may be wrong for sidecar-resident values, but
            // the InMemory provider isn't a sensible target for those
            // anyway).
            using Arena materializeArena = new();
            Arena resolveArena = sourceStore as Arena ?? materializeArena;

            foreach (RowUpdateRequest req in requests)
            {
                object?[] row = newRows[req.LiveRowIndex];
                foreach ((int columnIndex, DataValue newValue) in req.NewValues)
                {
                    ColumnInfo column = _schema.Columns[columnIndex];
                    if (newValue.IsNull && !column.Nullable)
                    {
                        throw new InvalidOperationException(
                            $"UpdateRows: column '{column.Name}' is NOT NULL but the supplied value is null.");
                    }
                    row[columnIndex] = ConvertDataValueToCell(newValue, resolveArena);
                }
            }

            _rows = newRows;
            _overrideIndex = null;
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <inheritdoc/>
    public void DeleteRows(IReadOnlyList<long> rowIndices)
    {
        ArgumentNullException.ThrowIfNull(rowIndices);
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (rowIndices.Count == 0) return;

        _mutationLock.Wait();
        try
        {
            HashSet<long> drop = new(rowIndices.Count);
            foreach (long idx in rowIndices)
            {
                if (idx < 0 || idx >= _rows.Length)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(rowIndices), idx,
                        $"Row index {idx} is out of range for table '{Name}' (row count {_rows.Length}).");
                }
                drop.Add(idx);
            }

            object?[][] newRows = new object?[_rows.Length - drop.Count][];
            int j = 0;
            for (int i = 0; i < _rows.Length; i++)
            {
                if (drop.Contains(i)) continue;
                newRows[j++] = _rows[i];
            }
            _rows = newRows;
            _overrideIndex = null;
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <summary>
    /// Converts a <see cref="DataValue"/> read from an incoming
    /// <see cref="RowBatch"/> into a managed CLR cell suitable for
    /// long-term storage in the in-memory backing array. Decouples the
    /// stored cell from the source batch's arena so subsequent scans can
    /// rematerialize against fresh arenas.
    /// </summary>
    private static object? ConvertDataValueToCell(DataValue value, Arena arena)
    {
        if (value.IsNull) return null;

        return value.Kind switch
        {
            DataKind.Boolean => value.AsBoolean(),
            DataKind.Int8 => value.AsInt8(),
            DataKind.Int16 => value.AsInt16(),
            DataKind.Int32 => value.AsInt32(),
            DataKind.Int64 => value.AsInt64(),
            DataKind.UInt8 when value.IsArray => value.AsUInt8Array(arena),
            DataKind.UInt8 => value.AsUInt8(),
            DataKind.UInt16 => value.AsUInt16(),
            DataKind.UInt32 => value.AsUInt32(),
            DataKind.UInt64 => value.AsUInt64(),
            DataKind.Float32 => value.AsFloat32(),
            DataKind.Float64 => value.AsFloat64(),
            DataKind.Decimal => value.AsDecimal(),
            DataKind.Date => value.AsDate(),
            DataKind.Time => value.AsTime(),
            DataKind.DateTime => value.AsDateTime(),
            DataKind.Duration => value.AsDuration(),
            DataKind.Uuid => value.AsUuid(),
            DataKind.String => value.AsString(arena),
            // Blob kinds — extract bytes; the schema's declared kind drives
            // re-materialisation in MaterializeCell so a `byte[]` from an
            // Image column round-trips as Image (not as UInt8[]).
            DataKind.Image or DataKind.Audio or DataKind.Video or DataKind.Json
                => value.AsByteSpan(arena).ToArray(),
            _ => throw new NotSupportedException(
                $"InMemoryTableProvider.AppendRowsAsync does not yet support DataKind.{value.Kind}" +
                (value.IsArray ? " (array)" : "") + ". Extend ConvertDataValueToCell with a stable extraction path."),
        };
    }

    /// <summary>
    /// Resolves the output column lookup, source-row index for each output column,
    /// and per-column <see cref="DataKind"/> (used for null materialization). When
    /// <paramref name="requiredColumns"/> is <c>null</c> the full schema is used.
    /// </summary>
    private Projection ResolveProjection(IReadOnlySet<string>? requiredColumns)
    {
        if (requiredColumns is null)
        {
            int[] allIndexes = new int[_columns.Length];
            for (int i = 0; i < allIndexes.Length; i++) allIndexes[i] = i;

            DataKind[] allKinds = new DataKind[_columns.Length];
            for (int i = 0; i < allKinds.Length; i++) allKinds[i] = _schema.Columns[i].Kind;

            return new Projection(_fullLookup, allIndexes, allKinds);
        }

        // Preserve source column order; include only columns present in requiredColumns.
        List<int> sourceIndexes = new();
        List<string> names = new();
        List<DataKind> kinds = new();

        for (int i = 0; i < _columns.Length; i++)
        {
            if (requiredColumns.Contains(_columns[i]))
            {
                sourceIndexes.Add(i);
                names.Add(_columns[i]);
                kinds.Add(_schema.Columns[i].Kind);
            }
        }

        return new Projection(
            new ColumnLookup(names.ToArray()),
            sourceIndexes.ToArray(),
            kinds.ToArray());
    }

    private sealed class InMemorySeekSession : ISeekSession
    {
        private Pool? _pool;
        private Projection? _projection;
        private object?[][]? _rows;
        private readonly Arena? _targetArena;

        internal InMemorySeekSession(Pool pool, Projection projection, object?[][] rows, Arena? targetArena)
        {
            _pool = pool;
            _projection = projection;
            _rows = rows;
            _targetArena = targetArena;
        }

        [MemberNotNullWhen(false, nameof(_pool), nameof(_projection), nameof(_rows))]
        public bool Disposed { get; private set; }

        public async IAsyncEnumerable<RowBatch> SeekAsync(
            long startRow,
            int count,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);

            if (startRow >= _rows.Length || count <= 0)
            {
                yield break;
            }

            int start = (int)Math.Min(startRow, int.MaxValue);
            int available = Math.Min(count, _rows.Length - start);
            ArraySegment<object?[]> slice = new(_rows, start, available);

            await foreach (RowBatch batch in EmitRows(_pool, _projection, slice, _targetArena, cancellationToken).ConfigureAwait(false))
            {
                yield return batch;
            }
        }

        public void Dispose()
        {
            if (Disposed) return;

            _pool = null;
            _projection = null;
            _rows = null;
            Disposed = true;
        }
    }

    private static async IAsyncEnumerable<RowBatch> EmitRows(
        Pool pool,
        Projection projection,
        IEnumerable<object?[]> rows,
        Arena? targetArena,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        RowBatch? batch = null;

        foreach (object?[] rawRow in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            batch ??= pool.RentRowBatch(projection.Lookup, DefaultBatchSize, targetArena);

            DataValue[] values = pool.RentDataValues(projection.SourceIndexes.Length);
            for (int i = 0; i < projection.SourceIndexes.Length; i++)
            {
                int sourceIndex = projection.SourceIndexes[i];
                object? cell = sourceIndex < rawRow.Length ? rawRow[sourceIndex] : null;
                values[i] = MaterializeCell(cell, projection.Kinds[i], batch.Arena);
            }
            batch.Add(values);

            if (batch.IsFull)
            {
                yield return batch;
                batch = null;
            }
        }

        if (batch is not null)
        {
            yield return batch;
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Converts a single raw cell into a <see cref="DataValue"/>. DataValue cells are
    /// passed through unchanged (caller is responsible for arena lifetime). Strings
    /// and byte arrays are stored into <paramref name="arena"/>; DataValue chooses
    /// inline-vs-arena-backed storage for strings automatically based on UTF-8 length.
    /// </summary>
    private static DataValue MaterializeCell(object? cell, DataKind expectedKind, Arena arena)
    {
        if (cell is null)
        {
            return DataValue.Null(expectedKind);
        }

        if (cell is DataValue dv)
        {
            return dv;
        }

        return cell switch
        {
            string s => DataValue.FromString(s, arena),
            int i => DataValue.FromInt32(i),
            long l => DataValue.FromInt64(l),
            short sh => DataValue.FromInt16(sh),
            sbyte sb => DataValue.FromInt8(sb),
            uint ui => DataValue.FromUInt32(ui),
            ulong ul => DataValue.FromUInt64(ul),
            ushort us => DataValue.FromUInt16(us),
            byte b => DataValue.FromUInt8(b),
            float f => DataValue.FromFloat32(f),
            double d => DataValue.FromFloat64(d),
            decimal m => DataValue.FromDecimal(m),
            bool boolean => DataValue.FromBoolean(boolean),
            DateOnly date => DataValue.FromDate(date),
            DateTimeOffset dto => DataValue.FromDateTime(dto),
            DateTime dt => DataValue.FromDateTime(new DateTimeOffset(dt)),
            TimeOnly time => DataValue.FromTime(time),
            TimeSpan ts => DataValue.FromDuration(ts),
            Guid g => DataValue.FromUuid(g),
            byte[] bytes when expectedKind == DataKind.Image => DataValue.FromImage(bytes, arena),
            byte[] bytes when expectedKind == DataKind.Audio => DataValue.FromAudio(bytes, arena),
            byte[] bytes when expectedKind == DataKind.Video => DataValue.FromVideo(bytes, arena),
            byte[] bytes when expectedKind == DataKind.Json => DataValue.FromJson(bytes, arena),
            byte[] bytes => DataValue.FromByteArray(bytes, arena),
            _ => throw new ArgumentException(
                $"InMemoryTableProvider cannot materialize cell of type {cell.GetType().FullName}. " +
                "Pass a supported CLR primitive, a byte[], or a DataValue directly."),
        };
    }

    private static object?[][] ConvertRows(Row[] rows, string[] columns)
    {
        object?[][] result = new object?[rows.Length][];
        for (int r = 0; r < rows.Length; r++)
        {
            Row row = rows[r];
            object?[] cells = new object?[columns.Length];
            for (int c = 0; c < columns.Length; c++)
            {
                // Prefer ordinal lookup when the Row's column at position c matches the
                // expected name (common case — rows were built from the same schema).
                // Otherwise fall back to name-keyed lookup; null if the name isn't present.
                if (c < row.FieldCount && SameNameAt(row, c, columns[c]))
                {
                    cells[c] = row[c];
                }
                else if (row.TryGetValue(columns[c], out DataValue value))
                {
                    cells[c] = value;
                }
                else
                {
                    cells[c] = null;
                }
            }
            result[r] = cells;
        }
        return result;
    }

    private static bool SameNameAt(Row row, int ordinal, string expected)
    {
        return ordinal < row.ColumnNames.Count
            && string.Equals(row.ColumnNames[ordinal], expected, StringComparison.OrdinalIgnoreCase);
    }

    private static Schema BuildSchema(string[] columns, object?[][] rows)
    {
        if (columns.Length == 0)
        {
            return new Schema([new ColumnInfo("empty", DataKind.String, nullable: true)]);
        }

        ColumnInfo[] infos = new ColumnInfo[columns.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            (DataKind kind, bool isArray) = InferShape(rows, i);
            infos[i] = new ColumnInfo(columns[i], kind, nullable: true) { IsArray = isArray };
        }

        return new Schema(infos);
    }

    private static (DataKind Kind, bool IsArray) InferShape(object?[][] rows, int ordinal)
    {
        // Walk rows until a non-null cell is found; fall back to String for all-null columns.
        foreach (object?[] row in rows)
        {
            if (ordinal >= row.Length) continue;
            object? cell = row[ordinal];
            if (cell is null) continue;

            if (cell is DataValue dv)
            {
                if (dv.IsNull) continue;
                return (dv.Kind, dv.IsArray);
            }

            return InferShapeFromClrType(cell);
        }

        return (DataKind.String, false);
    }

    private static (DataKind Kind, bool IsArray) InferShapeFromClrType(object value)
    {
        // byte[] is the only CLR type that maps to a typed-array column (UInt8 + IsArray).
        if (value is byte[]) return (DataKind.UInt8, true);
        return (InferKindFromClrType(value), false);
    }

    private static DataKind InferKindFromClrType(object value) => value switch
    {
        string => DataKind.String,
        int => DataKind.Int32,
        long => DataKind.Int64,
        short => DataKind.Int16,
        sbyte => DataKind.Int8,
        uint => DataKind.UInt32,
        ulong => DataKind.UInt64,
        ushort => DataKind.UInt16,
        byte => DataKind.UInt8,
        float => DataKind.Float32,
        double => DataKind.Float64,
        bool => DataKind.Boolean,
        DateOnly => DataKind.Date,
        DateTimeOffset => DataKind.DateTime,
        DateTime => DataKind.DateTime,
        TimeOnly => DataKind.Time,
        TimeSpan => DataKind.Duration,
        Guid => DataKind.Uuid,
        // byte[] is special-cased upstream by InferShapeFromClrType which returns
        // (UInt8, IsArray=true). It shouldn't reach this kind-only mapping; falls
        // through to String here as a defensive default.
        _ => DataKind.String,
    };

    /// <summary>
    /// Cached projection plan: the output column lookup, the source-row ordinal for
    /// each output column, and the expected kind per output column (used when the
    /// source cell is null so we emit a typed null rather than defaulting to Unknown).
    /// </summary>
    private sealed record Projection(ColumnLookup Lookup, int[] SourceIndexes, DataKind[] Kinds);

    /// <summary>
    /// Append session that stages incoming cells in a managed list and
    /// snaps them into the provider's <c>_rows</c> array on
    /// <see cref="CommitAsync"/>. Disposing without committing drops
    /// the staged list — abort is just an unreferenced
    /// <see cref="List{T}"/> the GC reclaims.
    /// </summary>
    private sealed class InMemoryAppendSession : IAppendSession
    {
        private readonly InMemoryTableProvider _provider;
        private readonly List<object?[]> _staged = new();
        private bool _committed;
        private bool _disposed;

        // Captured at session start. Reservations advance the
        // session-local counter; commit pushes back to the provider.
        private readonly long _initialIdentityNextValue;
        private long _identityNextValue;
        private bool _identityReserved;

        public InMemoryAppendSession(InMemoryTableProvider provider)
        {
            _provider = provider;
            _initialIdentityNextValue = provider._identityNextValue;
            _identityNextValue = _initialIdentityNextValue;
        }

        public IdentityState? IdentityState
        {
            get
            {
                int idx = _provider._identityColumnIndex;
                if (idx < 0) return null;
                return new IdentityState(
                    ColumnIndex: idx,
                    ColumnKind: _provider._schema.Columns[idx].Kind,
                    Spec: new IdentitySpec(_provider._identitySeed, _provider._identityStep),
                    NextValue: _identityNextValue);
            }
        }

        public long ReserveNextIdentityValue()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_committed) throw new InvalidOperationException("Cannot reserve IDENTITY values after CommitAsync.");
            if (_provider._identityColumnIndex < 0)
            {
                throw new InvalidOperationException(
                    $"Table '{_provider.Name}' has no IDENTITY column.");
            }

            long reserved = _identityNextValue;
            _identityNextValue = checked(_identityNextValue + _provider._identityStep);
            _identityReserved = true;
            return reserved;
        }

        public Task WriteAsync(RowBatch batch, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_committed) throw new InvalidOperationException("Cannot write after CommitAsync.");
            ArgumentNullException.ThrowIfNull(batch);
            cancellationToken.ThrowIfCancellationRequested();

            int columnCount = _provider._columns.Length;
            if (batch.ColumnLookup.Count != columnCount)
            {
                throw new InvalidOperationException(
                    $"Append batch has {batch.ColumnLookup.Count} columns but table " +
                    $"'{_provider.Name}' has {columnCount}.");
            }
            for (int i = 0; i < columnCount; i++)
            {
                string expected = _provider._columns[i];
                string actual = batch.ColumnLookup.GetColumnName(i);
                if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Append batch column {i} is named '{actual}' but table " +
                        $"'{_provider.Name}' expects '{expected}'.");
                }
            }

            for (int r = 0; r < batch.Count; r++)
            {
                Row row = batch[r];
                object?[] cells = new object?[columnCount];
                for (int c = 0; c < columnCount; c++)
                {
                    cells[c] = ConvertDataValueToCell(row[c], batch.Arena);
                }
                _staged.Add(cells);
            }

            return Task.CompletedTask;
        }

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_committed) throw new InvalidOperationException("CommitAsync was already called.");
            cancellationToken.ThrowIfCancellationRequested();

            if (_staged.Count > 0)
            {
                object?[][] newRows = new object?[_provider._rows.Length + _staged.Count][];
                Array.Copy(_provider._rows, newRows, _provider._rows.Length);
                for (int i = 0; i < _staged.Count; i++)
                {
                    newRows[_provider._rows.Length + i] = _staged[i];
                }
                _provider._rows = newRows;
                _provider._overrideIndex = null;
            }
            // Persist the advanced IDENTITY counter even when no rows
            // committed — same semantics as the .datum provider:
            // reservations always burn (matches PostgreSQL's nextval()
            // sequence semantics).
            if (_identityReserved)
            {
                _provider._identityNextValue = _identityNextValue;
            }
            _committed = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            // Staged list goes to the GC if we never committed.
            _provider._mutationLock.Release();
            return ValueTask.CompletedTask;
        }
    }
}
