using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace DatumIngest.Model;

/// <summary>
/// A column-major batch of data that owns its backing storage and arena.
/// Each column is a contiguous <see cref="DataValue"/> array; string and binary
/// payloads are stored in a shared <see cref="Arena"/> buffer rather than
/// individual heap objects.
/// </summary>
/// <remarks>
/// <para>
/// Column arrays are rented from <see cref="ArrayPool{T}.Shared"/> and returned on
/// <see cref="Dispose"/>.  Callers must dispose batches after consumption; in the
/// operator pipeline the consumer is responsible for disposal.
/// </para>
/// </remarks>
public sealed class ColumnBatch : IDisposable
{
    private Arena? _arena;
    private DataValue[][]? _columns;

    internal ColumnBatch(
        ColumnLookup columnLookup,
        DataValue[][] columns,
        Arena arena)
    {
        ColumnLookup = columnLookup;
        _columns = columns;
        _arena = arena;
    }

    /// <summary>Number of columns in this batch.</summary>
    public int ColumnCount => ColumnLookup.Count;

    /// <summary>
    /// Gets the <see cref="ColumnLookup"/> associated with this batch, which contains column names and indices.
    /// </summary>
    public ColumnLookup ColumnLookup { get; }

    /// <summary>Maximum row capacity of the backing column arrays.</summary>
    public int RowCapacity => _columns?[0]?.Length ?? 0;

    /// <summary>Number of rows that have been written.</summary>
    public int RowCount { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the batch has reached its row capacity and cannot accept more rows.
    /// </summary>
    public bool IsFull => RowCount >= RowCapacity;

    /// <summary>
    /// Gets a value indicating whether this batch has been disposed. Disposed batches should not be used or returned to the pool.
    /// </summary>
    [MemberNotNullWhen(false, nameof(_columns))]
    [MemberNotNullWhen(false, nameof(_arena))]
    public bool Disposed { get; private set; }

    /// <summary>The arena that owns all reference-type payloads (strings, floats, byte blobs) for this batch.</summary>
    public Arena Arena
    {
        get
        {
            ObjectDisposedException.ThrowIf(Disposed, this);

            return _arena;
        }
    }

    /// <summary>
    /// Gets the column buffers for this batch. Each column is a contiguous array of <see cref="DataValue"/> entries.
    /// String and binary payloads referenced by the column buffers are stored in the shared <see cref="Arena"/>.
    /// The batch owns the column buffers and will return them to the pool on disposal; callers must not return individual column buffers.
    /// </summary>
    public IReadOnlyList<DataValue[]> Columns
    {
        get
        {
            ObjectDisposedException.ThrowIf(Disposed, this);

            return _columns;
        }
    }

    /// <summary>
    /// Advances the row count. Call after filling all columns for one or more rows.
    /// </summary>
    /// <param name="count">Number of rows written.</param>
    public void SetRowCount(int count)
    {
        RowCount = count;
    }

    /// <summary>
    /// Copies the values of a single row into the provided target array. Used by operators that need to convert from column batches back to row-wise representation.
    /// </summary>
    /// <param name="row">Zero-based row index to copy.</param>
    /// <param name="target">Target array to receive the row values. Must have length equal to <see cref="ColumnCount"/>.</param>
    public void CopyRow(int row, DataValue[] target)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, RowCount);

        for (int col = 0, colCount = ColumnCount; col < colCount; col++)
        {
            target[col] = _columns[col][row];
        }
    }

    /// <summary>
    /// Returns the writable column array.  Used by decoders that write directly
    /// into a column buffer.
    /// </summary>
    /// <param name="column">Zero-based column index.</param>
    /// <returns>The backing <see cref="DataValue"/> array for the column.</returns>
    internal DataValue[] GetColumnBuffer(int column)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        if ((uint)column >= (uint)ColumnCount)
        {
            throw new ArgumentOutOfRangeException(nameof(column));
        }

        return _columns[column];
    }

    /// <summary>
    /// Adjusts the arena offsets of all arena-backed <see cref="DataValue"/> entries in a
    /// column buffer by adding <paramref name="baseOffset"/>.
    /// Used after merging a per-column private <see cref="Arena"/> into the batch's
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

    // ───────────────────────── Disposal ─────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Disposed) return;
        
        for (int column = 0; column < ColumnCount; column++)
        {
            ArrayPool<DataValue>.Shared.Return(_columns[column], clearArray: true);
        }

        ArrayPool<DataValue[]>.Shared.Return(_columns, clearArray: true);

        _columns = null;
        _arena = null;

        Disposed = true;
    }
}
