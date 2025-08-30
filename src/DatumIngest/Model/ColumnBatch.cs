using System.Buffers;

namespace DatumIngest.Model;

/// <summary>
/// A column-major batch of data that owns its backing storage and arenas.
/// Each column is a contiguous <see cref="DataValue"/> array; string and binary
/// payloads are stored in shared <see cref="StringArena"/> and <see cref="DataArena"/>
/// buffers rather than individual heap objects.
/// </summary>
/// <remarks>
/// <para>
/// Column arrays are rented from <see cref="ArrayPool{T}.Shared"/> and returned on
/// <see cref="Dispose"/>.  Callers must dispose batches after consumption; in the
/// operator pipeline the consumer is responsible for disposal.
/// </para>
/// <para>
/// The <see cref="GetRow(int)"/> method produces a <see cref="Row"/> adapter for legacy
/// output paths (CLI display, gRPC streaming) — it allocates a <see cref="DataValue"/>
/// array per row and is not intended for hot loops.
/// </para>
/// </remarks>
public sealed class ColumnBatch : IDisposable
{
    private DataValue[][] _columns;
    private string[] _columnNames;
    private Dictionary<string, int> _nameIndex;
    private bool _disposed;

    private ColumnBatch(
        DataValue[][] columns,
        int columnCount,
        int rowCapacity,
        string[] columnNames,
        Dictionary<string, int> nameIndex,
        StringArena stringArena,
        DataArena dataArena)
    {
        _columns = columns;
        ColumnCount = columnCount;
        RowCapacity = rowCapacity;
        _columnNames = columnNames;
        _nameIndex = nameIndex;
        StringArena = stringArena;
        DataArena = dataArena;
    }

    /// <summary>Number of columns in this batch.</summary>
    public int ColumnCount { get; }

    /// <summary>Maximum row capacity of the backing column arrays.</summary>
    public int RowCapacity { get; }

    /// <summary>Number of rows that have been written.</summary>
    public int RowCount { get; private set; }

    /// <summary>The arena that owns UTF-8 encoded string data for this batch.</summary>
    public StringArena StringArena { get; }

    /// <summary>The arena that owns float/byte blob data for this batch.</summary>
    public DataArena DataArena { get; }

    /// <summary>The ordered column names.</summary>
    public IReadOnlyList<string> ColumnNames => _columnNames;

    // ───────────────────────── Construction ─────────────────────────

    /// <summary>
    /// Creates a new <see cref="ColumnBatch"/> with the specified schema and capacity.
    /// Column arrays are rented from <see cref="ArrayPool{T}.Shared"/>.
    /// </summary>
    /// <param name="columnNames">Ordered column names.</param>
    /// <param name="nameIndex">Case-insensitive name-to-ordinal map (shared with caller).</param>
    /// <param name="rowCapacity">Maximum number of rows each column array can hold.</param>
    /// <returns>An empty batch ready for writing.</returns>
    public static ColumnBatch Create(
        string[] columnNames,
        Dictionary<string, int> nameIndex,
        int rowCapacity)
    {
        int columnCount = columnNames.Length;
        DataValue[][] columns = ArrayPool<DataValue[]>.Shared.Rent(columnCount);

        for (int column = 0; column < columnCount; column++)
        {
            columns[column] = ArrayPool<DataValue>.Shared.Rent(rowCapacity);
        }

        StringArena stringArena = new();
        DataArena dataArena = new();

        return new ColumnBatch(columns, columnCount, rowCapacity, columnNames, nameIndex, stringArena, dataArena);
    }

    /// <summary>
    /// Creates a new <see cref="ColumnBatch"/> with the specified schema and capacity,
    /// building the name index automatically.
    /// </summary>
    /// <param name="columnNames">Ordered column names.</param>
    /// <param name="rowCapacity">Maximum number of rows each column array can hold.</param>
    /// <returns>An empty batch ready for writing.</returns>
    public static ColumnBatch Create(string[] columnNames, int rowCapacity)
    {
        Dictionary<string, int> nameIndex = new(columnNames.Length, StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < columnNames.Length; index++)
        {
            nameIndex[columnNames[index]] = index;
        }

        return Create(columnNames, nameIndex, rowCapacity);
    }

    // ───────────────────────── Writing ─────────────────────────

    /// <summary>
    /// Sets a <see cref="DataValue"/> at the specified column and row position.
    /// </summary>
    /// <param name="column">Zero-based column index.</param>
    /// <param name="row">Zero-based row index.</param>
    /// <param name="value">The value to store.</param>
    public void SetValue(int column, int row, DataValue value)
    {
        _columns[column][row] = value;
    }

    /// <summary>
    /// Advances the row count. Call after filling all columns for one or more rows.
    /// </summary>
    /// <param name="count">Number of rows written.</param>
    public void SetRowCount(int count)
    {
        RowCount = count;
    }

    // ───────────────────────── Reading ─────────────────────────

    /// <summary>
    /// Returns the <see cref="DataValue"/> at the specified row and column.
    /// </summary>
    /// <param name="row">Zero-based row index.</param>
    /// <param name="column">Zero-based column index.</param>
    /// <returns>The stored value.</returns>
    public DataValue GetValue(int row, int column)
        => _columns[column][row];

    /// <summary>
    /// Returns the full column as a span, sliced to <see cref="RowCount"/>.
    /// </summary>
    /// <param name="column">Zero-based column index.</param>
    /// <returns>A span of <see cref="DataValue"/> with <see cref="RowCount"/> elements.</returns>
    public ReadOnlySpan<DataValue> GetColumn(int column)
        => _columns[column].AsSpan(0, RowCount);

    /// <summary>
    /// Returns the writable column array.  Used by decoders that write directly
    /// into a column buffer.
    /// </summary>
    /// <param name="column">Zero-based column index.</param>
    /// <returns>The backing <see cref="DataValue"/> array for the column.</returns>
    internal DataValue[] GetColumnBuffer(int column)
        => _columns[column];

    /// <summary>
    /// Resolves a column name to its zero-based ordinal.
    /// </summary>
    /// <param name="name">Column name (case-insensitive).</param>
    /// <param name="ordinal">The column ordinal if found.</param>
    /// <returns><c>true</c> if the column exists.</returns>
    public bool TryGetColumnOrdinal(string name, out int ordinal)
        => _nameIndex.TryGetValue(name, out ordinal);

    /// <summary>
    /// Returns the column name at the specified ordinal.
    /// </summary>
    /// <param name="column">Zero-based column index.</param>
    /// <returns>The column name.</returns>
    public string GetColumnName(int column)
        => _columnNames[column];

    // ───────────────────────── Materialisation ─────────────────────────

    /// <summary>
    /// Materialises a string value from the arena.  Allocates a managed string.
    /// </summary>
    /// <param name="row">Zero-based row index.</param>
    /// <param name="column">Zero-based column index.</param>
    /// <returns>The decoded string.</returns>
    public string MaterializeString(int row, int column)
    {
        DataValue value = _columns[column][row];
        if (value.IsArenaBacked)
        {
            return value.AsString(StringArena);
        }

        return value.AsString();
    }

    /// <summary>
    /// Returns the raw UTF-8 bytes for an arena-backed string without allocating.
    /// </summary>
    /// <param name="row">Zero-based row index.</param>
    /// <param name="column">Zero-based column index.</param>
    /// <returns>A span of UTF-8 bytes.  Valid only while this batch is alive.</returns>
    public ReadOnlySpan<byte> GetStringBytes(int row, int column)
    {
        DataValue value = _columns[column][row];
        return value.GetArenaStringSpan(StringArena);
    }

    /// <summary>
    /// Produces a <see cref="Row"/> view of a single row by copying values out
    /// of the column arrays.  Arena-backed strings are materialised into real
    /// <see cref="string"/> objects so the <see cref="Row"/> is self-contained.
    /// </summary>
    /// <param name="rowIndex">Zero-based row index.</param>
    /// <returns>A self-contained row.</returns>
    /// <remarks>
    /// This allocates a new <see cref="DataValue"/> array per call. For hot paths
    /// where the caller manages buffer lifetimes, use <see cref="GetRow(int, DataValue[])"/>
    /// with a pre-allocated or pooled buffer instead.
    /// </remarks>
    public Row GetRow(int rowIndex)
    {
        DataValue[] values = new DataValue[ColumnCount];

        for (int column = 0; column < ColumnCount; column++)
        {
            DataValue value = _columns[column][rowIndex];

            if (value.IsArenaBacked)
            {
                values[column] = value.Materialize(StringArena, DataArena);
            }
            else
            {
                values[column] = value;
            }
        }

        return new Row(_columnNames, values, _nameIndex);
    }

    /// <summary>
    /// Produces a <see cref="Row"/> view of a single row using a caller-provided
    /// <see cref="DataValue"/> buffer, avoiding per-row array allocations.
    /// Arena-backed strings are materialised so the <see cref="Row"/> is self-contained.
    /// </summary>
    /// <param name="rowIndex">Zero-based row index.</param>
    /// <param name="buffer">
    /// Pre-allocated <see cref="DataValue"/> array with at least <see cref="ColumnCount"/>
    /// elements.  The caller owns the buffer lifetime.
    /// </param>
    /// <returns>A row that shares <paramref name="buffer"/> for its values.</returns>
    public Row GetRow(int rowIndex, DataValue[] buffer)
    {
        for (int column = 0; column < ColumnCount; column++)
        {
            DataValue value = _columns[column][rowIndex];
            buffer[column] = value.IsArenaBacked
                ? value.Materialize(StringArena, DataArena)
                : value;
        }

        return new Row(_columnNames, buffer, _nameIndex);
    }

    // ───────────────────────── Arena merging ─────────────────────────

    /// <summary>
    /// Adjusts the arena offsets of all arena-backed <see cref="DataValue"/> entries in a
    /// column buffer by adding <paramref name="baseOffset"/>.
    /// Used after merging a per-column private <see cref="StringArena"/> into the batch's
    /// shared arena during parallel decode.
    /// </summary>
    /// <param name="column">The column buffer whose values may need offset adjustment.</param>
    /// <param name="rowCount">Number of valid rows in the buffer.</param>
    /// <param name="baseOffset">Byte offset to add to each arena-backed value's stored offset.</param>
    public static void AdjustArenaOffsets(DataValue[] column, int rowCount, int baseOffset)
    {
        for (int row = 0; row < rowCount; row++)
        {
            if (column[row].IsArenaBacked)
            {
                column[row] = column[row].WithArenaOffset(baseOffset);
            }
        }
    }

    // ───────────────────────── Column renaming ─────────────────────────

    /// <summary>
    /// Returns a new <see cref="ColumnBatch"/> that shares the same column arrays
    /// and arenas as this batch but uses different column names and index.
    /// The returned batch borrows all storage from this batch; disposing either
    /// one invalidates the shared arrays.  The caller must ensure only one batch
    /// is disposed — typically the renamed batch is yielded and this batch is
    /// abandoned (not disposed).
    /// </summary>
    /// <param name="names">New column names array (must have <see cref="ColumnCount"/> elements).</param>
    /// <param name="nameIndex">New case-insensitive name-to-ordinal dictionary.</param>
    /// <returns>A column batch sharing storage with new column names.</returns>
    internal ColumnBatch WithColumnNames(string[] names, Dictionary<string, int> nameIndex)
    {
        _columnNames = names;
        _nameIndex = nameIndex;
        return this;
    }

    // ───────────────────────── Adapter ─────────────────────────

    /// <summary>
    /// Creates a <see cref="ColumnBatch"/> from a <see cref="RowBatch"/> by transposing
    /// row-major data into column-major layout.  String values are stored directly
    /// (not arena-backed) since they already exist as managed objects.
    /// </summary>
    /// <param name="rowBatch">The row batch to convert.</param>
    /// <returns>A new column batch.  The caller is responsible for disposal.</returns>
    public static ColumnBatch FromRowBatch(RowBatch rowBatch)
    {
        if (rowBatch.Count == 0)
        {
            return Create(Array.Empty<string>(), 0);
        }

        Row first = rowBatch[0];
        string[] names = new string[first.FieldCount];
        for (int column = 0; column < first.FieldCount; column++)
        {
            names[column] = first.ColumnNames[column];
        }

        ColumnBatch batch = Create(names, rowBatch.Count);

        for (int row = 0; row < rowBatch.Count; row++)
        {
            Row currentRow = rowBatch[row];
            for (int column = 0; column < batch.ColumnCount; column++)
            {
                batch._columns[column][row] = currentRow[column];
            }
        }

        batch.RowCount = rowBatch.Count;
        return batch;
    }

    // ───────────────────────── Disposal ─────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        for (int column = 0; column < ColumnCount; column++)
        {
            ArrayPool<DataValue>.Shared.Return(_columns[column], clearArray: true);
        }

        ArrayPool<DataValue[]>.Shared.Return(_columns, clearArray: true);
        _columns = Array.Empty<DataValue[]>();
        StringArena.Dispose();
        DataArena.Dispose();
    }
}
