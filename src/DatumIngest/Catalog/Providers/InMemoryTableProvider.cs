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
    private readonly string[] _columns;
    private readonly object?[][] _rows;
    private readonly Schema _schema;
    private readonly ColumnLookup _fullLookup;

    /// <summary>
    /// Creates a provider from explicit column names and raw <c>object[]</c> rows.
    /// This is the primary API. Each cell is converted to a <see cref="DataValue"/>
    /// at scan time — see the class remarks for supported cell types.
    /// </summary>
    /// <param name="pool">The buffer pool used to rent row batches.</param>
    /// <param name="name">The logical table name.</param>
    /// <param name="columns">Column names, in the order cells appear in each row.</param>
    /// <param name="rows">Rows as <c>object?[]</c> arrays with one cell per column.</param>
    public InMemoryTableProvider(Pool pool, string name, string[] columns, object?[][] rows)
    {
        _pool = pool;
        Name = name;
        _columns = columns;
        _rows = rows;
        _schema = BuildSchema(_columns, _rows);
        _fullLookup = new ColumnLookup(_columns);
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
    public SourceIndex? GetSourceIndex() => null;

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> ScanAsync(
        IReadOnlySet<string>? requiredColumns,
        Expression? filterHint,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        // filterHint is advisory for zone-map pruning. An in-memory table has no
        // partitions, so the hint is unused — the caller applies the filter downstream.
        Projection projection = ResolveProjection(requiredColumns);

        await foreach (RowBatch batch in EmitRows(
            _pool, projection, _rows, cancellationToken).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    /// <inheritdoc/>
    public ISeekSession OpenSeekSession(IReadOnlySet<string>? requiredColumns)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        return new InMemorySeekSession(_pool, ResolveProjection(requiredColumns), _rows);
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

        internal InMemorySeekSession(Pool pool, Projection projection, object?[][] rows)
        {
            _pool = pool;
            _projection = projection;
            _rows = rows;
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

            await foreach (RowBatch batch in EmitRows(_pool, _projection, slice, cancellationToken).ConfigureAwait(false))
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
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        RowBatch? batch = null;

        foreach (object?[] rawRow in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            batch ??= pool.RentRowBatch(projection.Lookup, DefaultBatchSize);

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
            bool boolean => DataValue.FromBoolean(boolean),
            DateOnly date => DataValue.FromDate(date),
            DateTimeOffset dto => DataValue.FromDateTime(dto),
            DateTime dt => DataValue.FromDateTime(new DateTimeOffset(dt)),
            TimeOnly time => DataValue.FromTime(time),
            TimeSpan ts => DataValue.FromDuration(ts),
            Guid g => DataValue.FromUuid(g),
            byte[] bytes => DataValue.FromUInt8Array(bytes, arena),
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
            DataKind kind = InferKind(rows, i);
            infos[i] = new ColumnInfo(columns[i], kind, nullable: true);
        }

        return new Schema(infos);
    }

    private static DataKind InferKind(object?[][] rows, int ordinal)
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
                return dv.Kind;
            }

            return InferKindFromClrType(cell);
        }

        return DataKind.String;
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
        byte[] => DataKind.UInt8Array,
        _ => DataKind.String,
    };

    /// <summary>
    /// Cached projection plan: the output column lookup, the source-row ordinal for
    /// each output column, and the expected kind per output column (used when the
    /// source cell is null so we emit a typed null rather than defaulting to Unknown).
    /// </summary>
    private sealed record Projection(ColumnLookup Lookup, int[] SourceIndexes, DataKind[] Kinds);
}
