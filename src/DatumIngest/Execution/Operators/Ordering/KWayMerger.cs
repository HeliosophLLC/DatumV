using DatumIngest.DatumFile.Sidecar;
using DatumIngest.Model;

namespace DatumIngest.Execution.Operators.Ordering;

/// <summary>
/// K-way merge of pre-sorted runs into a single sorted output stream. Uses a
/// <see cref="PriorityQueue{TElement, TPriority}"/> keyed on each reader's
/// current sort-key array; on each iteration the winning reader's row is copied
/// to the output batch and the reader is advanced and re-enqueued (or disposed
/// when exhausted).
/// </summary>
/// <remarks>
/// <para>
/// Output rows are stabilised into <see cref="ExecutionContext.Store"/> via
/// <see cref="RowCopyOutputWriter"/>, so downstream operators that splice values
/// without re-stabilizing (e.g. <c>JoinSchema.CombinePooledValues</c>) resolve
/// offsets against the same arena the bytes live in.
/// </para>
/// <para>
/// The merger takes ownership of the readers it is given: the finally block
/// disposes every reader regardless of whether the merge completed normally or
/// was cancelled mid-stream. Reader <c>DisposeAsync</c> is idempotent so
/// re-dispose of already-drained readers is safe.
/// </para>
/// </remarks>
internal static class KWayMerger
{
    /// <summary>
    /// Merges <paramref name="readers"/> (each already positioned on its first row)
    /// into a single sorted output stream using <paramref name="comparer"/> as the
    /// ordering. Output batches are rented with <paramref name="schema"/>.
    /// </summary>
    public static async IAsyncEnumerable<RowBatch> MergeAsync(
        IReadOnlyList<SortedRunReader> readers,
        ColumnLookup schema,
        SortKeyComparer comparer,
        ExecutionContext context)
    {
        SidecarRegistry? sidecarRegistry = context.SidecarRegistry;
        PriorityQueue<SortedRunReader, SortedRunReader> heap = new(
            Comparer<SortedRunReader>.Create((a, b) =>
                comparer.Compare(
                    a.CurrentKeys, a.CurrentBatch.Arena, sidecarRegistry,
                    b.CurrentKeys, b.CurrentBatch.Arena, sidecarRegistry)));
        foreach (SortedRunReader r in readers)
        {
            heap.Enqueue(r, r);
        }

        RowCopyOutputWriter writer = new(context);

        try
        {
            while (heap.Count > 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                SortedRunReader winner = heap.Dequeue();

                RowBatch? full = writer.Add(schema, winner.CurrentBatch, winner.CurrentIndex);
                if (full is not null) yield return full;

                if (await winner.ReadNextAsync().ConfigureAwait(false))
                {
                    heap.Enqueue(winner, winner);
                }
                else
                {
                    await winner.DisposeAsync().ConfigureAwait(false);
                }
            }

            RowBatch? trailing = writer.Flush();
            if (trailing is not null) yield return trailing;
        }
        finally
        {
            RowBatch? leftover = writer.Flush();
            if (leftover is not null) context.ReturnRowBatch(leftover);
            foreach (SortedRunReader r in readers)
            {
                await r.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
