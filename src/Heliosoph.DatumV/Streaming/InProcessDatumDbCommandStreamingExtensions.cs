using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Data;
using Heliosoph.DatumV.Diagnostics;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Streaming;

/// <summary>
/// Streaming surface for <see cref="InProcessDatumDbCommand"/>. Exposes the
/// per-cell <see cref="BatchEvent"/> wire that productive Plans emit via
/// <see cref="Execution.ExecutionContext.CellSink"/>, plus a 1 Hz residency
/// sidecar that stamps <see cref="CellMemorySampleBatchEvent"/>s on the
/// most-recently-started cell so blocking operators (ORDER BY buffer fill,
/// hash-join build) keep the live indicator moving instead of freezing
/// for the whole accumulation phase. Lives off the Command class so the
/// ADO.NET verb surface stays minimal.
/// </summary>
public static class InProcessDatumDbCommandStreamingExtensions
{
    /// <summary>
    /// Default in-RAM residency budget applied when
    /// <see cref="StreamEventsAsync"/> isn't given an explicit one. 2 GiB.
    /// Sized so small interactive queries never hit it but a single
    /// runaway statement can't OOM the host. The streaming UI's pressure
    /// indicator needs a non-null budget to render its threshold line
    /// meaningfully — without one the live indicator has no denominator.
    /// </summary>
    public const long DefaultStreamMemoryBudgetBytes = 2L * 1024 * 1024 * 1024;

    private static readonly TimeSpan MemorySampleInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Streams the prepared SQL's per-cell lifecycle events
    /// (<see cref="CellStartedBatchEvent"/>,
    /// <see cref="CellRowBatchEvent"/>,
    /// <see cref="CellCompletedBatchEvent"/>,
    /// <see cref="CellFailedBatchEvent"/>,
    /// <see cref="CellPrintBatchEvent"/>) plus 1 Hz residency samples
    /// (<see cref="CellMemorySampleBatchEvent"/>) as an
    /// <see cref="IAsyncEnumerable{T}"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Constructs a fresh <see cref="Execution.ExecutionContext"/> with a
    /// wired <see cref="Execution.ExecutionContext.CellSink"/>, opens a
    /// reader against the prepared SQL with <c>ownsContext: false</c>, and
    /// drives it to completion in a background driver task. Events flow
    /// through an unbounded channel so the consumer sees them in the
    /// order productive Plans emit them.
    /// </para>
    /// <para>
    /// The 1 Hz sidecar emits a residency sample on every tick whenever
    /// a cell is open — including during long-running blocking operators
    /// (ORDER BY buffer fill, hash-join build) that don't yield row
    /// batches. The sample is stamped with the most-recently-started cell
    /// id; samples never fire when no cell is open. Sidecar and Plan
    /// emissions serialise through a single channel writer so the wire
    /// order is preserved.
    /// </para>
    /// <para>
    /// <paramref name="memoryBudgetBytes"/> sets the
    /// <see cref="MemoryAccountant.MemoryBudgetBytes"/>; pass
    /// <see langword="null"/> for the
    /// <see cref="DefaultStreamMemoryBudgetBytes"/> 2 GiB default, or
    /// <see cref="long.MaxValue"/> for effectively-unbounded.
    /// </para>
    /// </remarks>
    public static async IAsyncEnumerable<BatchEvent> StreamEventsAsync(
        this InProcessDatumDbCommand command,
        long? memoryBudgetBytes = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        PreparedSql prepared = await command.PrepareAsync(cancellationToken).ConfigureAwait(false);

        Channel<BatchEvent> channel = Channel.CreateUnbounded<BatchEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        // Tracks the most-recently-started, not-yet-completed cell so the
        // memory sidecar can stamp residency samples on it. Updated under
        // emitLock alongside the channel write so the sidecar can never
        // observe a half-updated (id, stopwatch) pair. The bracket-side
        // cell-id stack on the allocator also tracks "current cell", but
        // it doesn't carry the per-cell stopwatch the sample wants —
        // we keep that here.
        string? currentCellId = null;
        Stopwatch? currentCellStopwatch = null;
        SemaphoreSlim emitLock = new(1, 1);

        // Ack gate: after the consumer foreach yields a CellRowBatchEvent,
        // we wait for them to finish processing it before the bracket
        // advances (which would recycle the RowBatch). The bracket emits
        // the event, then waits at ack; the consumer iterator releases
        // ack after the user's foreach body returns.
        SemaphoreSlim rowBatchAck = new(0, int.MaxValue);

        async ValueTask Sink(BatchEvent ev)
        {
            await emitLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                switch (ev)
                {
                    case CellStartedBatchEvent started:
                        currentCellId = started.CellId;
                        currentCellStopwatch = Stopwatch.StartNew();
                        break;
                    case CellCompletedBatchEvent:
                    case CellFailedBatchEvent:
                        currentCellId = null;
                        currentCellStopwatch = null;
                        break;
                }
                await channel.Writer.WriteAsync(ev, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                emitLock.Release();
            }
            // For row-batch events, gate the bracket on the consumer
            // finishing the foreach body — otherwise the bracket's next
            // MoveNextAsync auto-returns the RowBatch to the pool while
            // the consumer still has a live reference. Sidecar memory
            // samples and lifecycle events don't carry live batches, so
            // they don't need this synchronisation.
            if (ev is CellRowBatchEvent)
            {
                await rowBatchAck.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        using Execution.ExecutionContext context = new(
            command.Connection.Catalog,
            memoryBudgetBytes: memoryBudgetBytes ?? DefaultStreamMemoryBudgetBytes,
            cellSink: Sink,
            cellIds: new CellIdAllocator(),
            cancellationToken: cancellationToken);
        context.Accountant.StartProfiling();

        // 1 Hz residency sidecar: emits a memory sample whenever a cell is
        // open. Independent of row cadence so long blocking operators
        // (OrderBy buffer fill, hash-join build) keep the live indicator
        // moving instead of freezing for the whole accumulation phase.
        using CancellationTokenSource sidecarCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Pool pool = command.Connection.Catalog.Pool;
        Task sidecarTask = Task.Run(async () =>
        {
            using PeriodicTimer timer = new(MemorySampleInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(sidecarCts.Token).ConfigureAwait(false))
                {
                    await emitLock.WaitAsync(sidecarCts.Token).ConfigureAwait(false);
                    try
                    {
                        if (currentCellId is string cellId && currentCellStopwatch is Stopwatch sw)
                        {
                            MemoryAccountant accountant = context.Accountant;
                            long arenaBytes = pool.TotalLiveArenaBytes();
                            long? vramUsed = null;
                            long? vramTotal = null;
                            if (VramProbe.TryGetUsage(out long usedBytes, out long totalBytes))
                            {
                                vramUsed = usedBytes;
                                vramTotal = totalBytes;
                            }
                            await channel.Writer.WriteAsync(new CellMemorySampleBatchEvent(
                                cellId,
                                sw.Elapsed.TotalMilliseconds,
                                accountant.CurrentResidentBytes,
                                arenaBytes,
                                accountant.PeakResidentBytes,
                                accountant.MemoryBudgetBytes,
                                vramUsed,
                                vramTotal), sidecarCts.Token).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        emitLock.Release();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown — the consumer finished or cancelled.
            }
        }, sidecarCts.Token);

        // Driver task: opens the reader against the shared context and
        // drains it. Productive Plans emit cell events via Sink while
        // their iterators advance; consuming the row stream here triggers
        // those emissions. Completing the channel (success or capture
        // the exception) signals the foreach below to terminate.
        Task driverTask = Task.Run(async () =>
        {
            try
            {
                await using InProcessDatumDbReader reader = await command
                    .ExecuteReaderAsync(context, cancellationToken)
                    .ConfigureAwait(false);
                do
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        // Rows surface as CellRowBatchEvent through Sink; drain so
                        // the bracket advances to the next batch / cell.
                    }
                }
                while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, cancellationToken);

        try
        {
            await foreach (BatchEvent ev in channel.Reader
                .ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                try
                {
                    yield return ev;
                }
                finally
                {
                    // Pair with the row-batch ack in Sink. Released after
                    // the consumer's foreach body returns so the bracket
                    // can advance (and the RowBatch's pool-return fires)
                    // only when the consumer is done with the batch.
                    if (ev is CellRowBatchEvent) rowBatchAck.Release();
                }
            }
            // Surface a driver-task exception that completed the channel.
            await driverTask.ConfigureAwait(false);
        }
        finally
        {
            // Stop the sidecar before the context disposes so its arena
            // probe doesn't race a refcount drop.
            sidecarCts.Cancel();
            try
            {
                await sidecarTask.ConfigureAwait(false);
            }
            catch
            {
                // Best-effort sidecar shutdown — the consumer already has
                // its events; a sidecar shutdown hiccup shouldn't mask the
                // real result.
            }
        }
    }
}
