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
    /// Rents a <see cref="DataValue"/> array to the pool for reuse and copies the specified source
    /// values into it, stabilizing them in the process.
    /// </summary>
    /// <param name="row">The source values to copy.</param>
    /// <param name="sourceArena">The arena containing the source values.</param>
    /// <param name="targetArena">The arena to stabilize the values in.</param>
    /// <returns>The rented and copied data value array.</returns>
    public DataValue[] RentAndCopyDataValues(Row row, Arena sourceArena, Arena targetArena)
        => RentAndCopyDataValues(row.RawValues, sourceArena, targetArena);

    /// <summary>
    /// Rents a <see cref="DataValue"/> array to the pool for reuse and copies the specified source
    /// values into it, stabilizing them in the process, then adds the values as a new row to the
    /// specified output batch.
    /// </summary>
    /// <param name="inputBatch">The input row batch.</param>
    /// <param name="rowIndex">The index of the row to copy.</param>
    /// <param name="outputBatch">The output row batch.</param>
    public void RentAndCopyToOutput(RowBatch inputBatch, int rowIndex, RowBatch outputBatch)
    {
        DataValue[] values = RentAndCopyDataValues(inputBatch[rowIndex], inputBatch.Arena, outputBatch.Arena);

        outputBatch.Add(values);
    }

    /// <summary>
    /// Rents a <see cref="DataValue"/> array to the pool for reuse and copies the specified source
    /// values into it, stabilizing them in the process.
    /// </summary>
    /// <param name="source">The source values to copy.</param>
    /// <param name="sourceArena">The arena containing the source values.</param>
    /// <param name="targetArena">The arena to stabilize the values in.</param>
    /// <returns>The rented and copied data value array.</returns>
    /// <remarks>
    /// When <paramref name="sourceArena"/> and <paramref name="targetArena"/> are
    /// the same reference (the common case under one-arena-per-query), the function
    /// short-circuits to a bulk <see cref="ReadOnlySpan{T}.CopyTo(Span{T})"/> — no
    /// per-element <see cref="DataValueRetention.Stabilize"/> dispatch, no per-element
    /// flag checks. Behaviour is identical: same-store stabilisation already
    /// returns its input unchanged. Use this overload when a single-arena fast
    /// path is desired without burdening the call site with the check.
    /// </remarks>
    public DataValue[] RentAndCopyDataValues(ReadOnlySpan<DataValue> source, Arena sourceArena, Arena targetArena)
    {
        DataValue[]? buffer = null;
        try
        {
            buffer = Backing.RentDataValues(source.Length);

            if (ReferenceEquals(sourceArena, targetArena))
            {
                // Same-arena fast path — Stabilize would short-circuit per element,
                // but a bulk Span.CopyTo skips the loop and dispatch entirely.
                source.CopyTo(buffer);
            }
            else
            {
                for (int i = 0; i < source.Length; i++)
                {
                    buffer[i] = DataValueRetention.Stabilize(source[i], sourceArena, targetArena);
                }
            }

            return buffer;
        }
        catch
        {
            // If stabilization or copying throws, return the buffer to the pool to avoid leaks.
            if (buffer != null)
            {
                ReturnDataValues(buffer);
            }

            throw;
        }
    }

    /// <summary>
    /// Rents a <see cref="RowBatch"/> with the specified capacity.
    /// </summary>
    public RowBatch RentRowBatch(ColumnLookup columnLookup, int capacity, Arena? arena = null)
        => Backing.RentRowBatch(columnLookup, capacity, arena);


    /// <summary>
    /// Rents a <see cref="RowBatch"/> from the pool, copies the contents of the <paramref name="inputBatch"/>
    /// into it, and returns the new batch. The input batch is returned to the pool after copying.
    /// </summary>
    /// <param name="inputBatch">The input row batch.</param>
    /// <param name="columnLookup">The column lookup for the new batch.</param>
    /// <returns>The new row batch.</returns>
    public RowBatch RebindRowBatch(RowBatch inputBatch, ColumnLookup columnLookup)
    {
        inputBatch.Clear(out Row[] rows, out Arena arena, out int count);

        for (int i = 0; i < count; i++)
        {
            // Have to rebind the column lookup
            rows[i] = new Row(columnLookup, rows[i].RawValues);
        }

        return new(columnLookup, rows, rows.Length, arena, count);
    }

    /// <summary>
    /// Rents a <see cref="ColumnBatch"/> with the specified column lookup and row capacity.
    /// </summary>
    /// <param name="columnLookup">The column lookup for the batch.</param>
    /// <param name="rowCapacity">The row capacity for the batch.</param>
    /// <param name="arena">An optional arena to use for the batch; if null, a new arena will be created.</param>
    /// <returns>A rented <see cref="ColumnBatch"/>.</returns>
    public ColumnBatch RentColumnBatch(ColumnLookup columnLookup, int rowCapacity, Arena? arena = null)
        => Backing.RentColumnBatch(columnLookup, rowCapacity, arena);

    /// <summary>
    /// Rents an <see cref="Arena"/> from the pool, optionally with an initial-capacity hint
    /// for freshly-allocated arenas. Pooled arenas keep their existing capacity; the hint
    /// only matters when no pooled arena is available and a new one has to be constructed.
    /// </summary>
    /// <param name="initialCapacity">
    /// Initial mmap region size for newly-allocated arenas, in bytes. Use this when the
    /// caller knows a rough upper bound on bytes it will append (e.g. spill consolidation)
    /// to avoid repeated doubling reallocations. Zero (default) uses Arena's built-in default.
    /// </param>
    public Arena RentArena(int initialCapacity = 0) => Backing.RentArena(initialCapacity);

    /// <summary>
    /// Rents a fresh file-backed <see cref="Arena"/> via the pool. The arena is always
    /// freshly constructed (file-backed arenas can't be reused across queries) but its
    /// rent flows through the pool's counter accounting so the leak invariant covers both
    /// anonymous and file-backed arenas uniformly. Terminal release deletes the backing file.
    /// </summary>
    public Arena RentFileBackedArena(string filePath, int initialCapacity)
        => Backing.RentFileBackedArena(filePath, initialCapacity);

    /// <summary>
    /// Releases one reference on <paramref name="arena"/>. When the refcount hits zero, the
    /// arena is recycled (or disposed if it has grown beyond the pool's per-arena cap).
    /// Returns <see langword="true"/> when the arena was fully released this call;
    /// <see langword="false"/> when other owners still hold references.
    /// </summary>
    public bool ReturnArena(Arena arena) => Backing.TryReturn(arena);

    /// <summary>
    /// Total <see cref="Arena.BytesWritten"/> summed across every arena
    /// currently rented from this pool (refcount &gt; 0). Used by the
    /// streaming memory profile to report a query's in-flight arena bytes
    /// — including operator-local arenas like OrderBy's bufferArena and
    /// spill consolidated arenas that the BatchExecutor sidecar can't
    /// observe directly. Iteration is at-most-1Hz from the sidecar; the
    /// approximate snapshot is fine for visualisation.
    /// </summary>
    public long TotalLiveArenaBytes() => Backing.TotalLiveArenaBytes();

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
    /// <param name="row">The row to return.</param>
    public void ReturnRow(Row row) => Backing.Return(row);

    /// <summary>
    /// Returns the <paramref name="rowBatch"/> and all its contained buffers to the pool for reuse.
    /// </summary>
    /// <param name="rowBatch">The row batch to return.</param>
    public void ReturnRowBatch(RowBatch rowBatch) => Backing.Return(rowBatch);

    /// <summary>
    /// Returns the <paramref name="columnBatch"/> and all its contained buffers to the pool for reuse.
    /// </summary>
    /// <param name="columnBatch">The column batch to return.</param>
    public void ReturnColumnBatch(ColumnBatch columnBatch) => Backing.Return(columnBatch);


    /// <summary>
    /// Returns the <paramref name="groupState"/> and all its contained buffers to the pool for reuse.
    /// </summary>
    /// <param name="groupState">The group state to return.</param>
    public void ReturnGroupState(GroupState groupState) => Backing.Return(groupState);
}