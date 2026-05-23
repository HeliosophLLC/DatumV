using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Streaming;

/// <summary>
/// Receives a streaming <see cref="BatchEvent"/> emitted by a Plan as it
/// executes. Async because the typical consumer writes NDJSON to a response
/// stream and the write is part of the bracket. Installed on
/// <c>ExecutionContext.CellSink</c>; defaults to a no-op so non-streaming
/// callers can ignore it.
/// </summary>
internal delegate ValueTask CellSinkHandler(BatchEvent ev);

/// <summary>
/// Shared monotonic cell-id source for one SQL batch. Lives off
/// <c>ExecutionContext</c> (root + derived contexts share the same instance)
/// so every productive Plan reaches the same counter regardless of the
/// scope chain it runs through. Enforces a hard cap so a tight loop around
/// a productive statement (<c>WHILE i&lt;1M SELECT i</c>) can't stream
/// unbounded cells into the response.
/// </summary>
internal sealed class CellIdAllocator
{
    /// <summary>
    /// Per-batch hard cap on cell events. Productive statements (SELECT,
    /// PRINT, DML, DDL, function CALL) emit cells regardless of nesting,
    /// so a loop with thousands of iterations around one of those will
    /// hit this cap and surface a clear error instead of pinning the UI.
    /// Silent statements (SET, DECLARE, control-flow wrappers) don't
    /// count — they don't emit cells when nested.
    /// </summary>
    public const int CellCap = 10_000;

    private int _counter;
    private readonly Stack<string> _liveCells = new();

    /// <summary>
    /// Returns the next <c>"c{n}"</c> cell id. Throws once the per-batch
    /// cap is reached so the caller can surface a clear error in place of
    /// an unbounded event stream.
    /// </summary>
    public string Next()
    {
        if (_counter >= CellCap)
        {
            throw new InvalidOperationException(
                $"This batch produced more than {CellCap:N0} cells — likely a tight loop around a SELECT, PRINT, or other output-producing statement. " +
                "Hoist the producing statement out of the loop, aggregate its output, or reduce the iteration count.");
        }
        return $"c{_counter++}";
    }

    /// <summary>
    /// Pushes <paramref name="cellId"/> as the live (started-but-not-yet-
    /// completed) cell. Productive Plan brackets call this immediately
    /// after emitting <see cref="CellStartedBatchEvent"/> so leaf plans
    /// (PRINT) can stamp diagnostic events on the enclosing cell.
    /// </summary>
    public void EnterCell(string cellId) => _liveCells.Push(cellId);

    /// <summary>
    /// Pops the live cell on bracket exit (success or failure).
    /// </summary>
    public void ExitCell()
    {
        if (_liveCells.Count > 0) _liveCells.Pop();
    }

    /// <summary>
    /// Most-recently-started, not-yet-completed cell id, or
    /// <see langword="null"/> at top level (no productive plan is on the
    /// stack). PRINT inside a productive ancestor cell stamps its
    /// <see cref="CellPrintBatchEvent"/> with this id.
    /// </summary>
    public string? CurrentCellId => _liveCells.Count > 0 ? _liveCells.Peek() : null;
}

/// <summary>
/// Per-statement event emitted by the SQL streaming surface
/// (<c>InProcessDatumDbCommand.StreamEventsAsync</c>).
/// Subtypes describe the lifecycle of a single executed statement (its cell):
/// <see cref="CellStartedBatchEvent"/> on entry, zero or more
/// <see cref="CellRowBatchEvent"/> while query rows produce, then exactly one
/// of <see cref="CellCompletedBatchEvent"/> on success or
/// <see cref="CellFailedBatchEvent"/> on throw.
/// </summary>
public abstract record BatchEvent;

/// <summary>
/// Cell entered. Fires before any rows produce.
/// </summary>
/// <param name="CellId">Unique identifier within this batch (e.g. "c0", "c1").</param>
/// <param name="Kind">
/// Statement category, lowercased: <c>"select"</c>, <c>"exec"</c>,
/// <c>"declare"</c>, <c>"set"</c>, <c>"if"</c>, <c>"while"</c>,
/// <c>"for"</c>, <c>"block"</c>. Maps 1:1 to AST subtype.
/// </param>
public sealed record CellStartedBatchEvent(string CellId, string Kind) : BatchEvent;

/// <summary>
/// One <see cref="RowBatch"/> produced by a query/exec cell. The batch is
/// live until the next event is emitted; consumers must process rows
/// synchronously inside the event handler.
/// </summary>
public sealed record CellRowBatchEvent(string CellId, RowBatch Batch) : BatchEvent;

/// <summary>
/// Cell completed successfully.
/// </summary>
public sealed record CellCompletedBatchEvent(string CellId, double ElapsedMs) : BatchEvent;

/// <summary>
/// Cell threw. The exception propagates after the event fires; consumers
/// see this and the exception in that order.
/// </summary>
public sealed record CellFailedBatchEvent(string CellId, Exception Error) : BatchEvent;

/// <summary>
/// Diagnostic message emitted by a <c>PRINT</c> statement. Distinct from
/// <see cref="CellRowBatchEvent"/> so consumers can route procedural
/// tracing to a separate channel (a debug pane, stderr, a log) without
/// confusing it with user-facing query rows. <see cref="Text"/> is the
/// stringified result of the expression evaluated by <c>PRINT</c>;
/// <see langword="null"/> when the expression evaluated to NULL.
/// </summary>
public sealed record CellPrintBatchEvent(string CellId, string? Text) : BatchEvent;

/// <summary>
/// One in-RAM residency sample from the plan-wide <c>MemoryAccountant</c>.
/// Emitted on a 1Hz cadence by the streaming layer while a cell runs so the
/// UI can render a live memory-pressure indicator alongside the row stream.
/// Read-only telemetry — consumers must not mutate query state in response.
/// </summary>
/// <param name="CellId">Cell this sample belongs to.</param>
/// <param name="ElapsedMs">Milliseconds since the accountant started.</param>
/// <param name="RowBytes">GC-resident residency (operator hash tables,
/// sort buffers, <c>VariableScope</c> payloads, DML buffers). The number
/// the spill budget compares against.</param>
/// <param name="ArenaBytes">Bytes written into the per-query / per-batch
/// arena. Anonymous and file-backed arenas alike are mmap-backed and
/// OS-paged, so this is informational only and does NOT count against the
/// spill budget.</param>
/// <param name="PeakRowBytes">Highest <see cref="RowBytes"/> seen so far.</param>
/// <param name="BudgetBytes">Spill budget if configured, otherwise <c>null</c>.</param>
/// <param name="VramUsedBytes">
/// Device VRAM currently allocated system-wide (across every process on
/// GPU 0). <c>null</c> when NVML isn't available — non-NVIDIA hosts,
/// driver missing, or first-init failed. See <c>VramProbe</c>.
/// </param>
/// <param name="VramTotalBytes">
/// Device VRAM capacity for GPU 0. <c>null</c> alongside
/// <see cref="VramUsedBytes"/> when the probe isn't available.
/// </param>
public sealed record CellMemorySampleBatchEvent(
    string CellId,
    double ElapsedMs,
    long RowBytes,
    long ArenaBytes,
    long PeakRowBytes,
    long? BudgetBytes,
    long? VramUsedBytes,
    long? VramTotalBytes) : BatchEvent;
