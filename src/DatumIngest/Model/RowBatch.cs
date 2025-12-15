using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace DatumIngest.Model;

/// <summary>
/// A row-major batch of <see cref="Row"/> objects backed by an array rented
/// from <see cref="ArrayPool{T}"/>. Batching amortises async state-machine
/// overhead by yielding many rows per <c>MoveNextAsync</c> call.
/// </summary>
/// <remarks>
/// <para>
/// RowBatch is a dumb container — it does not manage <see cref="DataValue"/>
/// array lifetimes. Lifecycle management is handled by <see cref="Pooling.Pool"/>
/// via <see cref="Pooling.Pool.RentRowBatch(ColumnLookup, int, Arena?)"/>
/// and <see cref="Pooling.Pool.ReturnRowBatch(RowBatch)"/>.
/// </para>
/// </remarks>
public sealed class RowBatch : IDisposable
{
    private Row[]? _rows;
    private Arena? _arena;
    private ColumnLookup _columnLookup;

    internal RowBatch(ColumnLookup columnLookup, Row[] rows, Arena arena, int count = 0)
    {
        _columnLookup = columnLookup;
        _rows = rows;
        _arena = arena;
        Count = count;
    }

    /// <summary>Maximum number of rows this batch can hold.</summary>
    public int Capacity => _rows?.Length ?? 0;

    /// <summary>Current number of rows in this batch.</summary>
    public int Count { get; private set; }

    /// <summary>Whether the batch has reached its capacity.</summary>
    public bool IsFull => Count >= Capacity;

    /// <summary>
    /// Gets the <see cref="ColumnLookup"/> associated with this batch, which contains column names and indices.
    /// </summary>
    public ColumnLookup ColumnLookup => _columnLookup;

    /// <summary>
    /// Gets a value indicating whether this batch has been disposed. Disposed batches should not be used or returned to the pool.
    /// Disposed batches have already had their backing array returned to the pool, so using them risks data corruption and memory safety issues.
    /// </summary>
    [MemberNotNullWhen(false, nameof(_rows))]
    [MemberNotNullWhen(false, nameof(_arena))]
    public bool Disposed { get; private set; }

    /// <summary>
    /// The per-query type registry. Set automatically when a batch is rented via
    /// <see cref="Execution.ExecutionContext.RentRowBatch(ColumnLookup)"/>; null for
    /// batches rented directly from the pool (provider-level batches without struct outputs).
    /// </summary>
    public TypeRegistry? Types { get; internal set; }

    /// <summary>
    /// Clears all fields and returns the backing row and arena as out parameters for reuse.
    /// Rows are not returned to any pool; their lifecycle is managed separately by operators.
    /// The batch must not be used after calling this method.
    /// </summary>
    /// <param name="rows">The array of rows to return.</param>
    /// <param name="arena">The arena to return.</param>
    /// <param name="count">The number of valid rows in the returned array.</param>
    public void Clear(out Row[] rows, out Arena arena, out int count)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        rows = _rows;
        arena = _arena;
        count = Count;

        _rows = [];
        _arena = null;
        Count = 0;
        Disposed = true;
    }

    /// <summary>
    /// Gets the <see cref="Arena"/> associated with this batch.
    /// </summary>
    public Arena Arena
    {
        get
        {
            ObjectDisposedException.ThrowIf(Disposed, this);

            return _arena;
        }
    }

    /// <summary>
    /// Returns the row at the given index.
    /// </summary>
    /// <param name="index">Zero-based row index within the batch.</param>
    /// <returns>The row at position <paramref name="index"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="index"/> is negative or greater than or equal to <see cref="Count"/>.
    /// </exception>
    public Row this[int index]
    {
        get
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);

            return _rows[index];
        }
    }

    /// <summary>
    /// Appends a row to this batch.
    /// </summary>
    /// <param name="row">The row to add.</param>
    /// <exception cref="InvalidOperationException">Thrown when the batch is already full.</exception>
    public void Add(Row row)
    {
        throw new NotImplementedException("DON'T USE THIS");
    }

    /// <summary>
    /// Appends a row to this batch.
    /// </summary>
    /// <param name="values">The array of data values for the new row.</param>
    /// <exception cref="InvalidOperationException">Thrown when the batch is already full.</exception>
    public void Add(DataValue[] values)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        if (Count >= Capacity)
        {
            throw new InvalidOperationException("RowBatch is full.");
        }

        _rows[Count] = new Row(_columnLookup, values);
        Count++;
    }

    /// <summary>
    /// Rents a new batch with the given capacity. The backing array is rented
    /// from <see cref="ArrayPool{T}.Shared"/>.
    /// Individual <see cref="Row"/> objects are not returned to any pool;
    /// their lifecycle is managed separately by operators.
    /// </summary>
    /// <param name="capacity">The maximum number of rows the batch can hold.</param>
    public static RowBatch Rent(int capacity)
    {
        throw new NotImplementedException("DON'T USE THIS");
    }

    /// <summary>
    /// Returns the backing array to <see cref="ArrayPool{T}.Shared"/>.
    /// The batch must not be used after calling this method.
    /// Individual <see cref="Row"/> objects are not returned to any pool;
    /// their lifecycle is managed separately by operators.
    /// </summary>
    public void Return()
    {
        throw new NotImplementedException("DON'T USE THIS");
    }

    /// <summary>
    /// Disposes this batch by returning the backing array to <see cref="ArrayPool{T}.Shared"/> and clearing all references to
    /// the contained rows and arena. Disposed batches should not be used or returned to the pool.
    /// </summary>
    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        _arena = null;

        if (_rows != null)
        {
            Array.Clear(_rows, 0, Count);
            ArrayPool<Row>.Shared.Return(_rows);
            _rows = [];
        }

        Count = 0;
        Disposed = true;
    }
}
