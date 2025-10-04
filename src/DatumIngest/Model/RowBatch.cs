using System.Buffers;
using DatumIngest.Execution;

namespace DatumIngest.Model;

/// <summary>
/// A row-major batch of <see cref="Row"/> objects backed by an array rented
/// from <see cref="ArrayPool{T}"/>. Batching amortises async state-machine
/// overhead by yielding many rows per <c>MoveNextAsync</c> call.
/// </summary>
/// <remarks>
/// <para>
/// RowBatch is a dumb container — it does not manage <see cref="DataValue"/>
/// array lifetimes. Lifecycle management is handled by <see cref="LocalBufferPool"/>
/// via <see cref="LocalBufferPool.RentBatch"/> and <see cref="LocalBufferPool.ReturnBatch"/>.
/// </para>
/// </remarks>
public sealed class RowBatch
{
    private Row[] _rows;
    private bool _returned;

    private RowBatch(Row[] rows, int capacity)
    {
        _rows = rows;
        Capacity = capacity;
    }

    /// <summary>Maximum number of rows this batch can hold.</summary>
    public int Capacity { get; }

    /// <summary>Current number of rows in this batch.</summary>
    public int Count { get; private set; }

    /// <summary>Whether the batch has reached its capacity.</summary>
    public bool IsFull => Count >= Capacity;

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
            if ((uint)index >= (uint)Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

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
        if (Count >= Capacity)
        {
            throw new InvalidOperationException("RowBatch is full.");
        }

        _rows[Count] = row;
        Count++;
    }

    /// <summary>
    /// Rents a new batch with the given capacity. The backing array is rented
    /// from <see cref="ArrayPool{T}.Shared"/>.
    /// Individual <see cref="Row"/> objects are not returned to any pool;
    /// their lifecycle is managed separately by operators.
    /// </summary>
    /// <param name="capacity">The maximum number of rows the batch can hold.</param>
    /// <returns>An empty batch ready for <see cref="Add"/> calls.</returns>
    public static RowBatch Rent(int capacity)
    {
        Row[] rows = ArrayPool<Row>.Shared.Rent(capacity);
        return new RowBatch(rows, capacity);
    }

    /// <summary>
    /// Returns the backing array to <see cref="ArrayPool{T}.Shared"/>.
    /// The batch must not be used after calling this method.
    /// Individual <see cref="Row"/> objects are not returned to any pool;
    /// their lifecycle is managed separately by operators.
    /// </summary>
    public void Return()
    {
        if (_returned)
        {
            return;
        }

        _returned = true;
        Array.Clear(_rows, 0, Count);
        ArrayPool<Row>.Shared.Return(_rows);
        _rows = Array.Empty<Row>();
        Count = 0;
    }

    /// <summary>
    /// Returns the backing array to <see cref="ArrayPool{T}.Shared"/> without
    /// clearing Row references. Called by <see cref="LocalBufferPool.ReturnBatch"/>
    /// after it has already returned the contained <see cref="DataValue"/> arrays.
    /// </summary>
    internal void ReturnShell()
    {
        if (_returned)
        {
            throw new InvalidOperationException(
                "RowBatch has already been returned. This indicates a double-return bug — " +
                "two code paths are returning the same batch.");
        }

        _returned = true;
        Array.Clear(_rows, 0, Count);
        ArrayPool<Row>.Shared.Return(_rows);
        _rows = Array.Empty<Row>();
        Count = 0;
    }

    /// <summary>
    /// Creates a batch containing exactly one row. Useful for operators
    /// that yield a single result (e.g. scalar subquery, empty-row source).
    /// </summary>
    /// <param name="row">The single row.</param>
    /// <returns>A batch with <see cref="Count"/> equal to 1.</returns>
    public static RowBatch CreateSingleRow(Row row)
    {
        RowBatch batch = Rent(1);
        batch.Add(row);
        return batch;
    }
}
