using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Pooling;

/// <summary>
/// 
/// </summary>
public sealed class Pool
{ 
    /// <summary>
    /// Initializes a new instance of the <see cref="Pool"/> class with the specified backing object.
    /// </summary>
    /// <param name="backing">The backing object containing the shared pool resources.</param>
    public Pool(PoolBacking backing)
    {
        Backing = backing;
    }

    /// <summary>
    /// Gets the backing object containing the shared pool resources.
    /// </summary>
    internal PoolBacking Backing { get; }

    
    /// <summary>
    /// Rents a <see cref="DataValue"/> array of exactly <paramref name="length"/> elements.
    /// Returns a previously returned buffer when one is available; allocates otherwise.
    /// </summary>
    public DataValue[] RentDataValues(int length) => Backing.RentDataValues(length);

    /// <summary>
    /// Rents a <see cref="RowBatch"/> with the specified capacity.
    /// </summary>
    public RowBatch RentRowBatch(int capacity) => Backing.RentRowBatch(capacity);

    /// <summary>
    /// Rents a <see cref="GroupState"/> with the specified number of accumulators.
    /// </summary>
    public GroupState RentGroupState(int accumulatorCount) => Backing.RentGroupState(accumulatorCount);

    /// <summary>
    /// Returns the <paramref name="buffer"/> to the pool for reuse.
    /// </summary>
    public void ReturnDataValues(DataValue[] buffer) => Backing.Return(buffer);

    /// <summary>
    /// Returns the <paramref name="row"/> and its backing <see cref="DataValue"/> array to the pool for reuse.
    /// </summary>
    public void ReturnRow(Row row) => Backing.Return(row);

    /// <summary>
    /// Returns the <paramref name="rowBatch"/> and all its contained buffers to the pool for reuse.
    /// </summary>
    /// <param name="rowBatch">The batch to return.</param>
    public void ReturnRowBatch(RowBatch rowBatch) => Backing.Return(rowBatch);

    /// <summary>
    /// Returns the <paramref name="groupState"/> and all its contained buffers to the pool for reuse.
    /// </summary>
    /// <param name="groupState">The group state to return.</param>
    public void ReturnGroupState(GroupState groupState) => Backing.Return(groupState);

    /// <summary>
    /// Rents a <see cref="DataValue"/> array and copies the source values into it.
    /// Used by caching operators (CTE, CrossValidate) that need to hold values
    /// independently of the input batch lifecycle.
    /// </summary>
    /// <param name="source">The values to copy.</param>
    /// <returns>A pool-rented array containing a copy of <paramref name="source"/>.</returns>
    public DataValue[] RentCopyDataValues(ReadOnlySpan<DataValue> source)
    {
        DataValue[] buffer = RentDataValues(source.Length);
        source.CopyTo(buffer);
        return buffer;
    }
}