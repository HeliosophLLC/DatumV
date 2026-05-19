using System.Diagnostics;
using System.Runtime.ExceptionServices;

using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Catalog.Plans;
using Heliosoph.DatumV.Catalog.Registries;
using Heliosoph.DatumV.Diagnostics;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Execution;

/// <summary>
/// Per-statement event emitted by <c>BatchExecutor.RunWithEventsAsync</c>.
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
/// One in-RAM residency sample from the plan-wide <see cref="MemoryAccountant"/>.
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
/// driver missing, or first-init failed. See <see cref="VramProbe"/>.
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

/// <summary>
/// Executes a parsed procedural batch — a list of <see cref="Statement"/>s
/// — against a <see cref="TableCatalog"/>, threading a single
/// <see cref="ExecutionContext"/> through every child statement so procedural
/// variables and the variable-payload arena live for the duration of the
/// batch.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Supported procedural statements.</strong> Handles
/// <see cref="DeclareStatement"/>, <see cref="SetStatement"/>,
/// <see cref="BlockStatement"/>, <see cref="IfStatement"/>,
/// <see cref="WhileStatement"/>, <see cref="ForCounterStatement"/>,
/// <see cref="ForInStatement"/>, <see cref="QueryStatement"/>, and
/// <see cref="CallStatement"/>.
/// </para>
/// <para>
/// <strong>Expression evaluation</strong> — DECLARE / SET initialisers,
/// IF / WHILE predicates, FOR start/end bounds — runs by synthesising
/// <c>SELECT &lt;expression&gt;</c>, planning it through the same
/// catalog as user queries, executing with the batch context plumbed,
/// and reading the single resulting cell. This route gets UDF inlining,
/// model hoisting, literal hoisting, CSE, and every other planner-pass
/// "for free" — the procedural side stays thin.
/// </para>
/// </remarks>
public sealed class BatchExecutor
{
    /// <summary>
    /// Maximum nested procedure-call depth before the executor refuses to
    /// open a new frame. Recursive procedures (direct or mutual) are not
    /// supported; this cap prevents the call stack from overflowing and
    /// gives the user a clear error instead. Matches the T-SQL convention
    /// of 32 levels of nested stored-procedure calls.
    /// </summary>
    public const int MaxProcedureCallDepth = 32;

    private readonly TableCatalog _catalog;

    /// <summary>
    /// Creates an executor bound to <paramref name="catalog"/>. Each
    /// <c>ExecuteAsync</c> call constructs a fresh
    /// <see cref="ExecutionContext"/>; the executor itself is stateless and
    /// safe to reuse across calls.
    /// </summary>
    public BatchExecutor(TableCatalog catalog)
    {
        _catalog = catalog;
    }

    /// <summary>
    /// Minimum interval between <see cref="CellMemorySampleBatchEvent"/>
    /// emissions during a cell's row-streaming loop. 1Hz matches the
    /// <see cref="MemoryAccountant"/>'s own sampling cadence; emitting more
    /// often would just bus duplicate samples to the UI. Emissions at cell
    /// start and end are unconditional and bypass this throttle.
    /// </summary>
    private static readonly TimeSpan MemorySampleInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Default in-RAM residency budget applied when
    /// <see cref="RunWithEventsAsync(IReadOnlyList{ValueTuple{Statement, string?}}, Func{BatchEvent, ValueTask}, CancellationToken, long?)"/>
    /// isn't given an explicit budget. 2 GiB. Sized for desktop / server
    /// hosts: large enough that small interactive queries never hit it,
    /// small enough that a single runaway statement can't OOM the host.
    /// Streaming consumers (the Web app's query pane) rely on a non-null
    /// budget for the pressure-indicator percentage and threshold line
    /// to render meaningfully — without one the live indicator has no
    /// denominator.
    /// </summary>
    public const long DefaultMemoryBudgetBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// Emits one <see cref="CellMemorySampleBatchEvent"/> by reading the
    /// accountant's current residency, peak, and budget. The
    /// <paramref name="stopwatch"/> measures elapsed-since-cell-start so
    /// the UI can align samples on its own timeline without needing the
    /// accountant's internal stopwatch. Arena bytes sums every arena
    /// currently rented from <paramref name="pool"/> — operator-local
    /// arenas included — so blocking operators (OrderBy buffer, hash-join
    /// build) show up in the chart even before they yield rows.
    /// </summary>
    private static ValueTask EmitMemorySampleAsync(
        ExecutionContext context,
        Pool pool,
        string cellId,
        Stopwatch stopwatch,
        Func<BatchEvent, ValueTask> onEvent)
    {
        MemoryAccountant accountant = context.Accountant;
        long? vramUsed = null;
        long? vramTotal = null;
        if (VramProbe.TryGetUsage(out long usedBytes, out long totalBytes))
        {
            vramUsed = usedBytes;
            vramTotal = totalBytes;
        }
        return onEvent(new CellMemorySampleBatchEvent(
            cellId,
            stopwatch.Elapsed.TotalMilliseconds,
            accountant.CurrentResidentBytes,
            pool.TotalLiveArenaBytes(),
            accountant.PeakResidentBytes,
            accountant.MemoryBudgetBytes,
            vramUsed,
            vramTotal));
    }

    /// <summary>
    /// Runs <paramref name="statements"/> in order, threading a single
    /// <see cref="ExecutionContext"/> through every child query so
    /// <c>@var</c> references in any statement resolve to bindings made
    /// by earlier statements. Returns a <see cref="BatchResult"/>
    /// snapshotting the final variable bindings (so tests and host code
    /// can inspect them after the batch context disposes).
    /// </summary>
    public Task<BatchResult> ExecuteAsync(
        IReadOnlyList<Statement> statements,
        CancellationToken cancellationToken)
        => ExecuteAsync(WithoutSourceText(statements), cancellationToken);

    /// <summary>
    /// Variant that accepts each statement paired with the verbatim source
    /// slice it was parsed from. The slice is forwarded to
    /// <see cref="TableCatalog.PlanAsync(Statement, string?)"/> for procedural
    /// <c>CREATE FUNCTION</c> / <c>CREATE PROCEDURE</c> so the catalog file
    /// captures the body verbatim. Other statement kinds ignore the slice.
    /// Use <c>SqlParser.ParseBatchWithText</c> to obtain the pairs.
    /// </summary>
    public async Task<BatchResult> ExecuteAsync(
        IReadOnlyList<(Statement Statement, string? SourceText)> statements,
        CancellationToken cancellationToken)
    {
        using ExecutionContext context = new(_catalog);
        // 1Hz residency sampling for the whole batch. Each child query
        // borrows this accountant so the timer ticks during every query
        // inside the batch.
        context.Accountant.StartProfiling();
        await RunInternalAsync(statements, context, NoOpEventHandler, cancellationToken)
            .ConfigureAwait(false);

        // Snapshot bindings before the batch context disposes — values
        // are read against VariableStore and materialised into managed
        // form, so the result remains valid after the arena is released.
        Dictionary<string, object?> snapshot = SnapshotRootBindings(context);
        return new BatchResult(snapshot);
    }

    private static IReadOnlyList<(Statement, string?)> WithoutSourceText(
        IReadOnlyList<Statement> statements)
    {
        (Statement, string?)[] pairs = new (Statement, string?)[statements.Count];
        for (int i = 0; i < statements.Count; i++) pairs[i] = (statements[i], null);
        return pairs;
    }

    /// <summary>
    /// Streaming variant: runs <paramref name="statements"/> and forwards
    /// per-cell lifecycle events to <paramref name="onEvent"/> as each
    /// statement enters / produces rows / completes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cell numbering is monotonic across the entire batch — including
    /// cells produced by nested control-flow (IF body, WHILE / FOR
    /// iterations, BEGIN/END blocks). A 5-iteration WHILE with two body
    /// statements produces 11 cells: one for the WHILE itself plus 10
    /// for the inner statements (5 × 2). FOR-counter and FOR-IN follow
    /// the same per-iteration cell-emission shape.
    /// </para>
    /// <para>
    /// <strong>Row batch lifetime.</strong> A <see cref="CellRowBatchEvent"/>
    /// carries a <see cref="RowBatch"/> that is live for the duration of
    /// the <paramref name="onEvent"/> invocation. The auto-return
    /// contract from <see cref="StatementPlan.ExecuteAsync(CancellationToken, ExecutionContext)"/>
    /// applies — the batch is recycled when the next event fires.
    /// </para>
    /// </remarks>
    public Task RunWithEventsAsync(
        IReadOnlyList<Statement> statements,
        Func<BatchEvent, ValueTask> onEvent,
        CancellationToken cancellationToken)
        => RunWithEventsAsync(WithoutSourceText(statements), onEvent, cancellationToken, memoryBudgetBytes: DefaultMemoryBudgetBytes);

    /// <summary>
    /// Variant of <see cref="RunWithEventsAsync(IReadOnlyList{Statement}, Func{BatchEvent, ValueTask}, CancellationToken)"/>
    /// that threads each statement's verbatim source slice through to the
    /// catalog for DDL persistence. See the
    /// <see cref="ExecuteAsync(IReadOnlyList{ValueTuple{Statement, string?}}, CancellationToken)"/>
    /// overload for the slice contract.
    /// </summary>
    public async Task RunWithEventsAsync(
        IReadOnlyList<(Statement Statement, string? SourceText)> statements,
        Func<BatchEvent, ValueTask> onEvent,
        CancellationToken cancellationToken,
        long? memoryBudgetBytes = null)
    {
        // Defaulting null → DefaultMemoryBudgetBytes (2 GiB) means the
        // streaming UI's pressure indicator always has a denominator and
        // a single runaway statement can't OOM the host. Callers that
        // truly need unbounded behaviour pass long.MaxValue explicitly.
        using ExecutionContext context = new(_catalog, memoryBudgetBytes ?? DefaultMemoryBudgetBytes);
        // 1Hz residency sampling for the whole batch. See ExecuteAsync for the rationale.
        context.Accountant.StartProfiling();

        // Sidecar 1Hz memory-sample emitter. The row-streaming path emits
        // throttled samples between batch yields, but a long-running operator
        // that doesn't yield rows (large GROUP BY accumulation, ORDER BY
        // buffer, hash-join build) would otherwise leave the UI's live
        // indicator frozen for the whole accumulation phase. This sidecar
        // ticks regardless of row cadence; both producers serialize through
        // `emitLock` so the wire stays in event order.
        string? currentCellId = null;
        Stopwatch? currentCellStopwatch = null;
        // Tracks the most-recent row batch's arena so sidecar samples report
        // per-query arena bytes (not just VariableStore, which stays empty
        // for non-procedural queries). Updated on each CellRowBatchEvent;
        // cleared on cell completion.
        Arena? currentRowBatchArena = null;
        SemaphoreSlim emitLock = new(1, 1);

        async ValueTask SerializedOnEvent(BatchEvent ev)
        {
            // Track which cell the sidecar should stamp on its samples.
            // Updated under the lock so the sidecar can never observe a
            // half-updated (id, stopwatch) pair.
            await emitLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                switch (ev)
                {
                    case CellStartedBatchEvent started:
                        currentCellId = started.CellId;
                        currentCellStopwatch = Stopwatch.StartNew();
                        currentRowBatchArena = null;
                        break;
                    case CellRowBatchEvent rowEvent:
                        currentRowBatchArena = rowEvent.Batch.Arena;
                        break;
                    case CellCompletedBatchEvent:
                    case CellFailedBatchEvent:
                        currentCellId = null;
                        currentCellStopwatch = null;
                        currentRowBatchArena = null;
                        break;
                }
                await onEvent(ev).ConfigureAwait(false);
            }
            finally
            {
                emitLock.Release();
            }
        }

        using CancellationTokenSource sidecarCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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
                            // Total arena bytes = sum across every Arena
                            // currently rented from the pool. This captures
                            // operator-internal arenas (OrderBy bufferArena,
                            // hash-join build arenas, spill consolidated
                            // arenas) that don't yield batches up to the
                            // BatchExecutor — exactly the cases where the
                            // earlier "VariableStore + last batch arena"
                            // approach reported zero for the entire
                            // accumulation phase of a blocking operator.
                            long arenaBytes = _catalog.Pool.TotalLiveArenaBytes();
                            long? vramUsed = null;
                            long? vramTotal = null;
                            if (VramProbe.TryGetUsage(out long usedBytes, out long totalBytes))
                            {
                                vramUsed = usedBytes;
                                vramTotal = totalBytes;
                            }
                            await onEvent(new CellMemorySampleBatchEvent(
                                cellId,
                                sw.Elapsed.TotalMilliseconds,
                                accountant.CurrentResidentBytes,
                                arenaBytes,
                                accountant.PeakResidentBytes,
                                accountant.MemoryBudgetBytes,
                                vramUsed,
                                vramTotal)).ConfigureAwait(false);
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
                // Expected on completion / cancellation.
            }
        }, sidecarCts.Token);

        try
        {
            await RunInternalAsync(statements, context, SerializedOnEvent, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            sidecarCts.Cancel();
            try { await sidecarTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            emitLock.Dispose();
        }
    }

    private static readonly Func<BatchEvent, ValueTask> NoOpEventHandler =
        static _ => ValueTask.CompletedTask;

    private async Task RunInternalAsync(
        IReadOnlyList<(Statement Statement, string? SourceText)> statements,
        ExecutionContext context,
        Func<BatchEvent, ValueTask> onEvent,
        CancellationToken ct)
    {
        // Single counter shared by the top-level driver + every recursive
        // call site. Enforces the per-batch cell cap so a tight loop
        // around a productive statement (e.g. WHILE i<1M SELECT i) can't
        // emit unbounded cell events into the response stream.
        int counter = 0;
        string NextCellId()
        {
            if (counter >= CellCap)
            {
                throw new InvalidOperationException(
                    $"This batch produced more than {CellCap:N0} cells — likely a tight loop around a SELECT, PRINT, or other output-producing statement. " +
                    "Hoist the producing statement out of the loop, aggregate its output, or reduce the iteration count.");
            }
            return $"c{counter++}";
        }
        try
        {
            foreach ((Statement stmt, string? sourceText) in statements)
            {
                ct.ThrowIfCancellationRequested();
                await ExecuteOneEventfulAsync(stmt, sourceText, context, onEvent, NextCellId, currentCellId: null, ct)
                    .ConfigureAwait(false);
            }
        }
        catch (LoopBreakSignal)
        {
            throw new InvalidOperationException(
                "BREAK is only valid inside a WHILE or FOR loop.");
        }
        catch (LoopContinueSignal)
        {
            throw new InvalidOperationException(
                "CONTINUE is only valid inside a WHILE or FOR loop.");
        }
    }

    // Per-batch hard cap on cell events. Productive statements (SELECT,
    // PRINT, DML, DDL, function CALL) emit cells regardless of nesting,
    // so a loop with thousands of iterations around one of those will
    // hit this cap and surface a clear error instead of pinning the UI.
    // Silent statements (SET, DECLARE, control-flow wrappers) don't
    // count — they don't emit cells when nested.
    private const int CellCap = 10_000;

    /// <summary>
    /// Single-statement dispatch. At top level (<paramref name="currentCellId"/>
    /// null) every statement gets bracketed with
    /// <see cref="CellStartedBatchEvent"/> / <see cref="CellCompletedBatchEvent"/>
    /// (or <see cref="CellFailedBatchEvent"/>) so the user sees one cell per
    /// statement they typed. In recursive (nested) calls only "productive"
    /// statements — ones that can produce visible output (rows, PRINT text)
    /// — get their own cell; silent control-flow wrappers
    /// (BLOCK / IF / WHILE / FOR / TRY / CALL of a procedure / SET / DECLARE / …)
    /// just dispatch into their body and inherit the enclosing cell id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what keeps a tight loop like <c>WHILE i &lt; N BEGIN SET r = r + 1 END</c>
    /// from emitting N cells. The WHILE gets exactly one cell (at top level);
    /// the body's SET is silent + nested so it skips bracketing entirely.
    /// A loop body that contains a SELECT or PRINT still emits one cell
    /// per iteration's productive child — which is the per-iteration
    /// streaming-result UX we want — bounded by <see cref="CellCap"/>.
    /// </para>
    /// </remarks>
    private Task ExecuteOneEventfulAsync(
        Statement stmt,
        ExecutionContext context,
        Func<BatchEvent, ValueTask> onEvent,
        Func<string> nextCellId,
        string? currentCellId,
        CancellationToken ct)
        => ExecuteOneEventfulAsync(stmt, sourceText: null, context, onEvent, nextCellId, currentCellId, ct);

    private async Task ExecuteOneEventfulAsync(
        Statement stmt,
        string? sourceText,
        ExecutionContext context,
        Func<BatchEvent, ValueTask> onEvent,
        Func<string> nextCellId,
        string? currentCellId,
        CancellationToken ct)
    {
        bool emitBrackets = currentCellId is null || IsProductiveWhenNested(stmt);
        string cellId;
        Stopwatch sw;
        if (emitBrackets)
        {
            cellId = nextCellId();
            sw = Stopwatch.StartNew();
            await onEvent(new CellStartedBatchEvent(cellId, KindOf(stmt))).ConfigureAwait(false);
            // Cell-entry memory sample (immediate, bypasses the sidecar's 1s
            // wait) — gives the UI a baseline so the sparkline doesn't start
            // blank during the first second of a cell.
            await EmitMemorySampleAsync(context, _catalog.Pool, cellId, sw, onEvent).ConfigureAwait(false);
        }
        else
        {
            cellId = currentCellId!;
            sw = Stopwatch.StartNew();
        }

        try
        {
            switch (stmt)
            {
                case QueryStatement q:
                {
                    if (TryGetAssignmentForm(q, out IReadOnlyList<string?>? assignTargets))
                    {
                        await ExecuteAssignmentSelectAsync(q, assignTargets!, context, ct)
                            .ConfigureAwait(false);
                        break;
                    }
                    // Regular SELECT — fall through to the unified
                    // catalog-dispatch arm.
                    goto default;
                }
                case CallStatement call:
                {
                    // CALL of a procedure routes through the procedure-
                    // invocation path; CALL of a function falls through to
                    // catalog.Plan for the unified scalar-dispatch arm.
                    if (TryGetProcedureCall(call, out ProcedureDescriptor? procDescriptor, out IReadOnlyList<Expression>? args))
                    {
                        await ExecuteProcedureCallAsync(
                            procDescriptor, args, context, onEvent, nextCellId, cellId, ct)
                            .ConfigureAwait(false);
                        break;
                    }
                    goto default;
                }
                case BlockStatement block:
                {
                    context.VariableScope.PushFrame();
                    try
                    {
                        foreach (Statement child in block.Statements)
                        {
                            ct.ThrowIfCancellationRequested();
                            await ExecuteOneEventfulAsync(child, context, onEvent, nextCellId, cellId, ct)
                                .ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        context.VariableScope.PopFrame();
                    }
                    break;
                }
                case IfStatement ifs:
                {
                    bool predicate = await EvaluatePredicateAsync(ifs.Predicate, context, ct)
                        .ConfigureAwait(false);
                    if (predicate)
                    {
                        await ExecuteOneEventfulAsync(ifs.Then, context, onEvent, nextCellId, cellId, ct)
                            .ConfigureAwait(false);
                    }
                    else if (ifs.Else is not null)
                    {
                        await ExecuteOneEventfulAsync(ifs.Else, context, onEvent, nextCellId, cellId, ct)
                            .ConfigureAwait(false);
                    }
                    break;
                }
                case WhileStatement loop:
                {
                    const int IterationCap = 1_000_000;
                    int iter = 0;
                    bool broke = false;
                    while (!broke)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (iter++ >= IterationCap)
                        {
                            throw new InvalidOperationException(
                                $"WHILE loop exceeded {IterationCap} iterations — likely a missing termination condition.");
                        }
                        bool keepGoing = await EvaluatePredicateAsync(loop.Predicate, context, ct)
                            .ConfigureAwait(false);
                        if (!keepGoing) break;
                        try
                        {
                            await ExecuteOneEventfulAsync(loop.Body, context, onEvent, nextCellId, cellId, ct)
                                .ConfigureAwait(false);
                        }
                        catch (LoopContinueSignal)
                        {
                            // Skip the rest of this iteration; predicate
                            // re-evaluates on the next pass.
                        }
                        catch (LoopBreakSignal)
                        {
                            broke = true;
                        }
                    }
                    break;
                }
                case ForCounterStatement forC:
                    await ExecuteForCounterAsync(forC, context, onEvent, nextCellId, cellId, ct)
                        .ConfigureAwait(false);
                    break;
                case ForInStatement forIn:
                    await ExecuteForInAsync(forIn, context, onEvent, nextCellId, cellId, ct)
                        .ConfigureAwait(false);
                    break;
                case DeclareStatement decl:
                    await ExecuteDeclareAsync(decl, context, ct).ConfigureAwait(false);
                    break;
                case SetStatement set:
                    await ExecuteSetAsync(set, context, ct).ConfigureAwait(false);
                    break;
                case BreakStatement:
                    throw LoopBreakSignal.Instance;
                case ContinueStatement:
                    throw LoopContinueSignal.Instance;
                case PrintStatement print:
                {
                    DataValue value = await EvaluateScalarAsync(print.Value, context, ct)
                        .ConfigureAwait(false);
                    string? text = RenderForPrint(value, context.VariableStore);
                    await onEvent(new CellPrintBatchEvent(cellId, text)).ConfigureAwait(false);
                    break;
                }
                case TryStatement tryStmt:
                {
                    await ExecuteTryAsync(tryStmt, context, onEvent, nextCellId, cellId, ct)
                        .ConfigureAwait(false);
                    break;
                }
                case AssertStatement assert:
                {
                    bool holds = await EvaluatePredicateAsync(assert.Predicate, context, ct)
                        .ConfigureAwait(false);
                    if (!holds)
                    {
                        string message;
                        if (assert.Message is not null)
                        {
                            DataValue m = await EvaluateScalarAsync(assert.Message, context, ct)
                                .ConfigureAwait(false);
                            message = RenderForPrint(m, context.VariableStore)
                                ?? "Assertion failed.";
                        }
                        else
                        {
                            message = $"Assertion failed: {QueryExplainer.FormatExpression(assert.Predicate)}";
                        }
                        throw new InvalidOperationException(message);
                    }
                    break;
                }
                case RaiseStatement raise:
                {
                    DataValue messageValue = await EvaluateScalarAsync(raise.Message, context, ct)
                        .ConfigureAwait(false);
                    string message = RenderForPrint(messageValue, context.VariableStore)
                        ?? "RAISE: <null>";
                    throw new InvalidOperationException(message);
                }
                default:
                    // Unified catalog-dispatch arm. Every non-procedural
                    // statement — SELECT (without LET-assignment), CALL
                    // (without procedure-call form), all DDL (CREATE /
                    // DROP / ALTER / ANALYZE / REINDEX for tables and
                    // functions / procedures), and all DML (INSERT /
                    // UPDATE / DELETE) — flows through TableCatalog.Plan.
                    // DDL plans (DdlPlan) apply their side effect on the
                    // first ExecuteAsync iteration and yield zero rows;
                    // DML plans apply side effects + yield RETURNING rows
                    // when present; query plans stream their result.
                    //
                    // Aligning here means new statement types only need a
                    // case in TableCatalog.Plan — BatchExecutor picks them
                    // up automatically. The source slice (when non-null)
                    // threads through so procedural CREATE FUNCTION /
                    // CREATE PROCEDURE round-trip through catalog
                    // persistence.
                    {
                        StatementPlan plan = await _catalog.PlanAsync(stmt, sourceText).ConfigureAwait(false);
                        await foreach (RowBatch batch in plan
                            .ExecuteAsync(ct, context)
                            .ConfigureAwait(false))
                        {
                            await onEvent(new CellRowBatchEvent(cellId, batch)).ConfigureAwait(false);
                            // Memory samples flow from the sidecar timer in
                            // RunWithEventsAsync at 1Hz regardless of batch
                            // cadence; no per-batch sample needed here.
                        }
                        break;
                    }
            }

            if (emitBrackets)
            {
                // Cell-end memory sample (always emitted, bypasses throttle) — gives
                // the UI a frozen final value so post-mortem inspection shows the
                // last-known state.
                await EmitMemorySampleAsync(context, _catalog.Pool, cellId, sw, onEvent).ConfigureAwait(false);
                await onEvent(new CellCompletedBatchEvent(cellId, sw.Elapsed.TotalMilliseconds))
                    .ConfigureAwait(false);
            }
        }
        catch (LoopBreakSignal)
        {
            // BREAK / CONTINUE are control flow, not failures. Fire the
            // completed event for this cell (only if we own the cell) and
            // let the signal bubble up to the enclosing loop (or to the
            // entry point, which converts a stray signal to a clear
            // "outside of a loop" error).
            if (emitBrackets)
            {
                await onEvent(new CellCompletedBatchEvent(cellId, sw.Elapsed.TotalMilliseconds))
                    .ConfigureAwait(false);
            }
            throw;
        }
        catch (LoopContinueSignal)
        {
            if (emitBrackets)
            {
                await onEvent(new CellCompletedBatchEvent(cellId, sw.Elapsed.TotalMilliseconds))
                    .ConfigureAwait(false);
            }
            throw;
        }
        catch (PreFlightRequiredException)
        {
            // Plan-time pre-flight blocks the cell before any rows /
            // operators come into being. Hosts catch this at the outer
            // boundary and project it to a structured `preflight_required`
            // wire event (install modal / typo suggestions). Emitting a
            // per-cell CellFailedBatchEvent here would race that path —
            // QueryStreamService's CellFailedBatchEvent handler writes
            // a generic error event and flips its cellErrorEmitted gate,
            // which then swallows the propagating exception before the
            // typed PreFlightRequiredException catch runs.
            throw;
        }
        catch (Exception ex)
        {
            // Silent nested statements don't own a cell — let the
            // exception propagate to the enclosing cell-owner, which
            // emits its own CellFailedBatchEvent.
            if (emitBrackets)
            {
                await onEvent(new CellFailedBatchEvent(cellId, ex)).ConfigureAwait(false);
            }
            throw;
        }
    }

    /// <summary>
    /// A statement is "productive" when it can produce user-visible output
    /// (rows or PRINT text), so a nested instance still earns its own cell.
    /// Silent statements (control flow, variable mutation, assignment-form
    /// SELECT, DDL that returns no result set) skip bracketing when nested
    /// and let any productive children speak for themselves.
    /// </summary>
    private static bool IsProductiveWhenNested(Statement stmt) => stmt switch
    {
        // Assignment-form SELECT writes into variables, no rows.
        QueryStatement q when TryGetAssignmentForm(q, out _) => false,
        // Regular SELECT — produces rows.
        QueryStatement => true,
        // PRINT — produces a print event.
        PrintStatement => true,
        // CALL of a procedure is silent on its own; the body's productive
        // children (SELECT, PRINT, …) get their own cells.
        CallStatement => false,
        // Control flow + variable mutation.
        BlockStatement => false,
        IfStatement => false,
        WhileStatement => false,
        ForCounterStatement => false,
        ForInStatement => false,
        TryStatement => false,
        DeclareStatement => false,
        SetStatement => false,
        BreakStatement => false,
        ContinueStatement => false,
        AssertStatement => false,
        RaiseStatement => false,
        CreateFunctionStatement => false,
        DropFunctionStatement => false,
        CreateProcedureStatement => false,
        DropProcedureStatement => false,
        // Anything else (DDL, DML, function CALL via default arm) routes
        // through TableCatalog.PlanAsync and may yield rows (DML RETURNING,
        // CREATE TABLE … AS SELECT, …); treat as productive.
        _ => true,
    };

    private static string KindOf(Statement stmt) => stmt switch
    {
        QueryStatement => "select",
        CallStatement => "call",
        BlockStatement => "block",
        IfStatement => "if",
        WhileStatement => "while",
        DeclareStatement => "declare",
        SetStatement => "set",
        ForCounterStatement => "for",
        ForInStatement => "for",
        BreakStatement => "break",
        ContinueStatement => "continue",
        PrintStatement => "print",
        TryStatement => "try",
        AssertStatement => "assert",
        RaiseStatement => "raise",
        CreateFunctionStatement => "create_function",
        DropFunctionStatement => "drop_function",
        CreateProcedureStatement => "create_procedure",
        DropProcedureStatement => "drop_procedure",
        _ => stmt.GetType().Name.ToLowerInvariant(),
    };

    /// <summary>
    /// Renders a <see cref="DataValue"/> to a string for <c>PRINT</c> output.
    /// Booleans render as lowercase <c>"true"</c>/<c>"false"</c> (SQL style);
    /// numerics use invariant culture so locale doesn't affect diagnostic
    /// output. NULL yields a <see langword="null"/> string so consumers can
    /// distinguish "missing" from the literal text "null".
    /// </summary>
    private static string? RenderForPrint(DataValue value, IValueStore store)
        => ProceduralEvaluator.RenderForPrint(value, store);

    private async Task ExecuteDeclareAsync(
        DeclareStatement decl, ExecutionContext context, CancellationToken ct)
    {
        ValueRef bound;
        if (decl.Initializer is not null)
        {
            // When the user supplies both a declared type and an initializer
            // (`DECLARE @sum INT64 = 0`), wrap the initializer in an implicit
            // CAST to the declared type. Without this the literal's natural
            // narrow type (e.g. Int8 for "0") would silently win, leaving
            // `@sum` bound to Int8 even though the declaration said Int64 —
            // a foot-gun for any subsequent arithmetic that expected the
            // declared range. When TypeName is omitted, type-inference from
            // the initializer is the intended behaviour, so no cast is
            // synthesised.
            Expression effective = decl.TypeName is not null
                ? new CastExpression(decl.Initializer, decl.TypeName, decl.Span)
                : decl.Initializer;
            DataValue stable = await EvaluateScalarAsync(effective, context, ct).ConfigureAwait(false);
            bound = LiftBoundaryValue(stable, context);
        }
        else
        {
            // No initializer → bind a typed NULL. The TypeName is required by
            // the parser when there's no initializer, so non-null here.
            // Annotations may carry an Array<T> wrapper produced by the
            // shared TypeNameParser; resolve to (kind, isArray) and pick the
            // matching null carrier.
            if (decl.TypeName is null
                || !TypeAnnotationResolver.TryParse(decl.TypeName, out DataKind kind, out bool isArray))
            {
                throw new InvalidOperationException(
                    $"DECLARE @{decl.VariableName}: cannot resolve type name '{decl.TypeName}'. " +
                    "Use a recognised SQL type (Int32, Float64, String, Boolean, Array<String>, etc.) or supply an initializer.");
            }
            bound = isArray ? ValueRef.NullArray(kind) : ValueRef.Null(kind);
        }

        context.VariableScope.Declare(decl.VariableName, bound);
    }

    private async Task ExecuteSetAsync(
        SetStatement set, ExecutionContext context, CancellationToken ct)
    {
        DataValue stable = await EvaluateScalarAsync(set.Value, context, ct).ConfigureAwait(false);
        context.VariableScope.Set(set.VariableName, LiftBoundaryValue(stable, context));
    }

    /// <summary>
    /// Lifts a <see cref="DataValue"/> produced by <see cref="EvaluateScalarAsync"/>
    /// (anchored in <see cref="ExecutionContext.VariableStore"/>) into a
    /// <see cref="ValueRef"/> for storage in <see cref="VariableScope"/>.
    /// Byte payloads materialise into managed memory so the binding survives
    /// any future arena recycle; inline scalars pass through unchanged.
    /// </summary>
    private static ValueRef LiftBoundaryValue(DataValue value, ExecutionContext context)
        => ProceduralEvaluator.LiftBoundaryValue(value, context);

    /// <summary>
    /// Returns the minimum required argument count for a procedure call:
    /// the count of leading parameters that have no default. The
    /// registration-time validation in <see cref="TableCatalog"/>
    /// guarantees defaults are contiguous at the tail, so the prefix
    /// length is the minimum legal arity.
    /// </summary>
    private static int ProcedureMinArity(IReadOnlyList<UdfParameter> parameters)
    {
        int min = 0;
        foreach (UdfParameter p in parameters)
        {
            if (p.Default is not null) break;
            min++;
        }
        return min;
    }

    /// <summary>
    /// Detects whether <paramref name="statement"/> is invoking a stored
    /// procedure by resolving the call through the catalog's procedure
    /// registry. Procedures require <c>CALL</c>; functions accept either
    /// <c>SELECT</c> or <c>CALL</c>. So when the target isn't a
    /// registered procedure, we fall through to the unified
    /// catalog.Plan path which handles it as a scalar/function call and
    /// surfaces "Unknown function" at evaluation time if the name
    /// resolves to nothing.
    /// </summary>
    private bool TryGetProcedureCall(
        CallStatement statement,
        out ProcedureDescriptor? descriptor,
        out IReadOnlyList<Expression>? arguments)
    {
        if (statement.Call is FunctionCallExpression call
            && _catalog.Procedures.TryResolve(call.SchemaName, call.FunctionName, _catalog.SearchPath, out descriptor))
        {
            arguments = call.Arguments;
            return true;
        }
        descriptor = null;
        arguments = null;
        return false;
    }

    /// <summary>
    /// Invokes a registered procedure: evaluates each argument in the
    /// caller's scope, opens a fresh <see cref="ExecutionContext"/> for the
    /// procedure body, declares each parameter from the corresponding
    /// argument value (with <c>IS NOT NULL</c> enforcement when declared),
    /// then runs the body's statements through the same eventful path
    /// that the rest of the executor uses. Cells produced by the body
    /// flow up to the caller's <paramref name="onEvent"/> stream so a
    /// procedure that does <c>SELECT</c>s shows its rows just as if the
    /// body had been inlined.
    /// </summary>
    /// <remarks>
    /// Each invocation gets its own <see cref="ExecutionContext"/> with a
    /// fresh <see cref="VariableScope"/> and procedure-lifetime arena —
    /// procedures don't share variable state with the caller. Arguments
    /// are evaluated in the CALLER's scope (so they can reference the
    /// caller's <c>@vars</c>), then stabilised across the boundary into
    /// the new context's variable store before the parameter is declared.
    /// </remarks>
    private async Task ExecuteProcedureCallAsync(
        ProcedureDescriptor? descriptor,
        IReadOnlyList<Expression>? arguments,
        ExecutionContext callerContext,
        Func<BatchEvent, ValueTask> onEvent,
        Func<string> nextCellId,
        string? currentCellId,
        CancellationToken ct)
    {
        if (descriptor is null || arguments is null)
        {
            throw new InvalidOperationException(
                "Internal error: TryGetProcedureCall yielded a null procedure descriptor or argument list.");
        }

        QualifiedName qn = descriptor.QualifiedName;

        // Trailing arguments may be omitted when the matching parameters
        // carry defaults; fill them in below from each parameter's Default
        // expression.
        int minRequired = ProcedureMinArity(descriptor.Parameters);
        if (arguments.Count < minRequired || arguments.Count > descriptor.Parameters.Count)
        {
            throw new InvalidOperationException(
                minRequired == descriptor.Parameters.Count
                    ? $"Procedure '{qn}' expects {descriptor.Parameters.Count} argument(s), got {arguments.Count}."
                    : $"Procedure '{qn}' expects {minRequired}–{descriptor.Parameters.Count} argument(s), got {arguments.Count}.");
        }

        // Cap nested-call depth before opening the new frame. Catches direct
        // recursion (proc A calls itself) and mutual recursion (A calls B
        // calls A) the same way — both produce ever-deeper contexts.
        int newDepth = callerContext.ProcedureCallDepth + 1;
        if (newDepth > MaxProcedureCallDepth)
        {
            throw new InvalidOperationException(
                $"Procedure call depth exceeded {MaxProcedureCallDepth} levels at " +
                $"'{qn}'. Procedural recursion is not supported — " +
                "rewrite as a recursive CTE or an iterative loop.");
        }

        // Evaluate each argument in the caller's scope so the args can
        // reference the caller's @vars. Capture the value as a managed
        // CLR object so it survives the boundary; we'll re-pack into the
        // procedure's variable store on the other side. When the caller
        // omits a trailing argument, fall back to the parameter's Default
        // expression — also evaluated in the caller's scope so defaults
        // can reference earlier @vars consistently.
        DataValue[] argValues = new DataValue[descriptor.Parameters.Count];
        for (int i = 0; i < descriptor.Parameters.Count; i++)
        {
            UdfParameter param = descriptor.Parameters[i];
            Expression argExpr = i < arguments.Count
                ? arguments[i]
                : param.Default!;  // arity check above guarantees a default is present
            DataValue v = await EvaluateScalarAsync(argExpr, callerContext, ct).ConfigureAwait(false);

            // Enforce IS NOT NULL on parameters at the call boundary.
            if (param.IsNotNull && v.IsNull)
            {
                throw new InvalidOperationException(
                    $"Procedure '{qn}' parameter '@{param.Name}' must not be null.");
            }

            argValues[i] = v;
        }

        // New ExecutionContext for the procedure's lifetime. Disposed at end —
        // the procedure-lifetime arena releases and any variable bindings
        // become unreachable, matching how a top-level procedural batch
        // tears down. Carries the bumped call depth so any further
        // CALLs the body issues see the running total.
        using ExecutionContext procContext = new(_catalog) { ProcedureCallDepth = newDepth };
        for (int i = 0; i < descriptor.Parameters.Count; i++)
        {
            // Stabilise from the caller's variable store into the
            // procedure's. The caller's value lives in callerContext.VariableStore
            // (because EvaluateScalarAsync stabilised it there). Re-stabilise
            // into procContext.VariableStore so the procedure body's reads
            // resolve against the right arena.
            procContext.Declare(
                descriptor.Parameters[i].Name,
                argValues[i],
                callerContext.VariableStore);
        }

        // Run the body's statements through the same dispatch as any other
        // procedural batch. Cell IDs continue from the caller's counter so
        // event consumers see a single coherent stream. The body inherits
        // the CALL's cellId — silent body statements stay quiet; productive
        // ones (SELECT, PRINT) emit their own nested cells.
        try
        {
            foreach (Statement bodyStatement in descriptor.Body.Statements)
            {
                ct.ThrowIfCancellationRequested();
                await ExecuteOneEventfulAsync(bodyStatement, procContext, onEvent, nextCellId, currentCellId, ct)
                    .ConfigureAwait(false);
            }
        }
        catch (LoopBreakSignal)
        {
            throw new InvalidOperationException(
                $"BREAK in procedure '{qn}' is only valid inside a WHILE or FOR loop.");
        }
        catch (LoopContinueSignal)
        {
            throw new InvalidOperationException(
                $"CONTINUE in procedure '{qn}' is only valid inside a WHILE or FOR loop.");
        }
    }

    /// <summary>
    /// Detects whether <paramref name="statement"/> is an assignment-form
    /// SELECT — every top-level <see cref="SelectColumn"/> carries an
    /// <see cref="SelectColumn.AssignedVariableName"/>. Mixed projections
    /// + assignments throw eagerly so the user sees a clear error rather
    /// than half-bound variables and half-rendered rows. UNION /
    /// INTERSECT / EXCEPT (CompoundQueryExpression) compositions of an
    /// assignment-form SELECT are also rejected — set ops over assignment
    /// columns are nonsensical.
    /// </summary>
    private static bool TryGetAssignmentForm(
        QueryStatement statement,
        out IReadOnlyList<string?>? assignTargets)
    {
        if (statement.Query is not SelectQueryExpression select)
        {
            assignTargets = null;
            return false;
        }

        IReadOnlyList<SelectColumn> columns = select.Statement.Columns;
        if (columns.Count == 0)
        {
            assignTargets = null;
            return false;
        }

        int assignmentCount = 0;
        foreach (SelectColumn col in columns)
        {
            if (col.AssignedVariableName is not null)
            {
                assignmentCount++;
            }
        }

        if (assignmentCount == 0)
        {
            assignTargets = null;
            return false;
        }

        if (assignmentCount != columns.Count)
        {
            throw new InvalidOperationException(
                "SELECT mixes variable assignments with projection columns. " +
                "All columns must use the `@var = expression` form, or none of them should. " +
                "Add an alias (`AS name`) to a column to opt it out of assignment and treat it " +
                "as a comparison instead.");
        }

        string?[] targets = new string?[columns.Count];
        for (int i = 0; i < columns.Count; i++)
        {
            targets[i] = columns[i].AssignedVariableName;
        }
        assignTargets = targets;
        return true;
    }

    /// <summary>
    /// Executes an assignment-form SELECT — a statement where every
    /// column is <c>@var = expression</c>. The plan runs as a normal
    /// projection (the assignment expressions are the projected columns);
    /// each row's values are stabilised into
    /// <see cref="ExecutionContext.VariableStore"/> and pushed through
    /// <see cref="VariableScope.Set"/>. Multiple matching rows update the
    /// variables in iteration order — the last row's values are what
    /// remain, matching T-SQL semantics. Zero rows leave the variables
    /// unchanged. The statement produces no <see cref="CellRowBatchEvent"/>s
    /// upstream — assignment-form SELECTs are silent at the
    /// <see cref="BatchEvent"/> wire level.
    /// </summary>
    private async Task ExecuteAssignmentSelectAsync(
        QueryStatement statement,
        IReadOnlyList<string?> assignTargets,
        ExecutionContext context,
        CancellationToken ct)
    {
        StatementPlan plan = await _catalog.PlanAsync(statement, sourceText: null).ConfigureAwait(false);

        await foreach (RowBatch batch in plan
            .ExecuteAsync(ct, context)
            .ConfigureAwait(false))
        {
            for (int rowIdx = 0; rowIdx < batch.Count; rowIdx++)
            {
                ct.ThrowIfCancellationRequested();
                Row row = batch[rowIdx];
                for (int colIdx = 0; colIdx < assignTargets.Count; colIdx++)
                {
                    string? variableName = assignTargets[colIdx];
                    if (variableName is null) continue; // defensive — shouldn't happen post-validation
                    // Lift directly from the producing batch's arena into a
                    // managed-payload ValueRef so the binding survives the
                    // batch's recycle without going through VariableStore.
                    EvaluationFrame batchFrame = new(row, batch.Arena, context.VariableStore, context);
                    ValueRef bound = ExpressionEvaluator.ToValueRef(row[colIdx], batchFrame);
                    context.VariableScope.Set(variableName, bound);
                }
            }
        }
    }

    /// <summary>
    /// Counter loop: <c>FOR @i = start TO end body</c>. Inclusive on both
    /// ends, matching the AST contract. The loop variable is auto-declared
    /// in a fresh frame pushed for the loop's lifetime — visible to the
    /// body, gone after the loop ends. Step is always +1 (parser today
    /// does not surface STEP); when <c>start &gt; end</c> the body never
    /// runs. The counter is bound as <see cref="DataKind.Int64"/>: any
    /// numeric kind from the bounds expressions is coerced.
    /// </summary>
    /// <summary>
    /// Runs a <see cref="TryStatement"/> with C#-style try/catch/finally
    /// semantics: execute the try body; if it throws (other than control-flow
    /// signals or cancellation) bind the exception's message to
    /// <c>@&lt;ErrorVariableName&gt;</c> in a fresh scope frame and run the catch
    /// body; finally, run the finally body unconditionally — even when an
    /// exception is bubbling up from try, catch, a control-flow signal, or
    /// cancellation. A throw from finally supersedes any pending exception.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>What gets caught.</strong> Catch only handles "user errors" —
    /// anything that escapes the try body that isn't a
    /// <see cref="LoopBreakSignal"/>, <see cref="LoopContinueSignal"/>, or
    /// <see cref="OperationCanceledException"/>. Loop control flow and
    /// cancellation pass straight through to their proper destinations
    /// (the enclosing loop, the cancellation token's owner) — finally still
    /// runs on the way out.
    /// </para>
    /// <para>
    /// <strong>Error scope.</strong> A fresh frame is pushed before
    /// <c>@&lt;ErrorVariableName&gt;</c> is declared, so the binding is visible
    /// only inside the catch body and disappears when the frame pops. The
    /// message string is stabilised into <see cref="ExecutionContext.VariableStore"/>
    /// so it remains valid across any inner query / arena recycle.
    /// </para>
    /// </remarks>
    private async Task ExecuteTryAsync(
        TryStatement tryStmt,
        ExecutionContext context,
        Func<BatchEvent, ValueTask> onEvent,
        Func<string> nextCellId,
        string? currentCellId,
        CancellationToken ct)
    {
        ExceptionDispatchInfo? pending = null;

        try
        {
            try
            {
                await ExecuteOneEventfulAsync(tryStmt.TryBody, context, onEvent, nextCellId, currentCellId, ct)
                    .ConfigureAwait(false);
            }
            catch (LoopBreakSignal) { throw; }
            catch (LoopContinueSignal) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Bind the exception message into a fresh frame so the catch
                // body can reference @<ErrorVariableName>; pop on exit so the
                // binding doesn't leak to the surrounding scope or to FINALLY.
                context.VariableScope.PushFrame();
                try
                {
                    // Managed-string payload — survives any future arena
                    // recycle without anchoring in VariableStore.
                    ValueRef messageValue = ValueRef.FromString(ex.Message ?? string.Empty);
                    context.VariableScope.Declare(tryStmt.ErrorVariableName, messageValue);
                    try
                    {
                        await ExecuteOneEventfulAsync(tryStmt.CatchBody, context, onEvent, nextCellId, currentCellId, ct)
                            .ConfigureAwait(false);
                    }
                    catch (LoopBreakSignal) { throw; }
                    catch (LoopContinueSignal) { throw; }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception catchEx)
                    {
                        // Catch body itself threw — defer to finally, then
                        // propagate. Use ExceptionDispatchInfo so the original
                        // stack trace is preserved.
                        pending = ExceptionDispatchInfo.Capture(catchEx);
                    }
                }
                finally
                {
                    context.VariableScope.PopFrame();
                }
            }
        }
        finally
        {
            // Always run the finally body, regardless of how try/catch exited.
            // If finally itself throws, that exception supersedes the pending
            // one (matches C#'s try/finally behaviour). The exception unwinding
            // out of this method is whichever throw won.
            if (tryStmt.FinallyBody is not null)
            {
                await ExecuteOneEventfulAsync(tryStmt.FinallyBody, context, onEvent, nextCellId, currentCellId, ct)
                    .ConfigureAwait(false);
            }
        }

        // If the catch body threw and finally completed cleanly, surface the
        // catch exception now (try-block exceptions handled in catch don't
        // re-throw; we only get here if catch ran and itself threw).
        pending?.Throw();
    }

    private async Task ExecuteForCounterAsync(
        ForCounterStatement forC,
        ExecutionContext context,
        Func<BatchEvent, ValueTask> onEvent,
        Func<string> nextCellId,
        string? currentCellId,
        CancellationToken ct)
    {
        DataValue startVal = await EvaluateScalarAsync(forC.Start, context, ct).ConfigureAwait(false);
        DataValue endVal = await EvaluateScalarAsync(forC.End, context, ct).ConfigureAwait(false);
        long start = ToInt64(startVal, $"FOR @{forC.VariableName} start");
        long end = ToInt64(endVal, $"FOR @{forC.VariableName} end");

        context.VariableScope.PushFrame();
        try
        {
            if (start > end) return;
            for (long i = start; i <= end; i++)
            {
                ct.ThrowIfCancellationRequested();
                ValueRef iValue = ValueRef.FromInt64(i);
                if (i == start)
                {
                    context.VariableScope.Declare(forC.VariableName, iValue);
                }
                else
                {
                    context.VariableScope.Set(forC.VariableName, iValue);
                }
                try
                {
                    await ExecuteOneEventfulAsync(forC.Body, context, onEvent, nextCellId, currentCellId, ct)
                        .ConfigureAwait(false);
                }
                catch (LoopContinueSignal)
                {
                    // Skip the rest of this iteration; the counter advances normally.
                }
                catch (LoopBreakSignal)
                {
                    return;
                }
            }
        }
        finally
        {
            context.VariableScope.PopFrame();
        }
    }

    /// <summary>
    /// Cursor loop: <c>FOR @row IN (SELECT ...) body</c>. Plans the source
    /// query, drives it batch-by-batch, and binds each row to the loop
    /// variable as a <see cref="DataKind.Struct"/> whose ordered fields
    /// mirror the source's columns. The source's column names are attached
    /// to the binding so <c>@row['column']</c> resolves at evaluation time
    /// without requiring a per-iteration schema lookup.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Lifetime.</strong> Each row's struct fields are stabilised
    /// from the producing batch's arena into
    /// <see cref="ExecutionContext.VariableStore"/> before the binding
    /// happens, so the binding remains valid even after the source batch
    /// recycles. A fresh frame is pushed and popped per iteration so the
    /// loop variable is re-declared cleanly each pass — this also keeps
    /// any inner DECLAREs in the body block-scoped to that single
    /// iteration.
    /// </para>
    /// <para>
    /// <strong>Streaming sink for the source query.</strong> The source
    /// query runs without a streaming sink — it's infrastructure feeding
    /// the loop, not user-facing output. Streaming model invocations
    /// inside the body, by contrast, get the per-cell sink threaded by
    /// the recursive call into the body.
    /// </para>
    /// </remarks>
    private async Task ExecuteForInAsync(
        ForInStatement forIn,
        ExecutionContext context,
        Func<BatchEvent, ValueTask> onEvent,
        Func<string> nextCellId,
        string? currentCellId,
        CancellationToken ct)
    {
        QueryStatement sourceQuery = new(forIn.Source);
        StatementPlan plan = await _catalog.PlanAsync(sourceQuery, sourceText: null).ConfigureAwait(false);

        IReadOnlyList<string>? fieldNames = null;
        ushort rowTypeId = 0;
        bool broke = false;

        await foreach (RowBatch batch in plan
            .ExecuteAsync(ct, context)
            .ConfigureAwait(false))
        {
            if (broke) break;
            // Field names are stable across batches in a single query plan,
            // so capture once on the first batch and reuse for every row.
            fieldNames ??= batch.ColumnLookup.ColumnNames;
            int colCount = fieldNames.Count;

            for (int rowIdx = 0; rowIdx < batch.Count; rowIdx++)
            {
                ct.ThrowIfCancellationRequested();
                Row row = batch[rowIdx];

                // Lift each field directly into a managed-payload ValueRef
                // against the producing batch's arena, then assemble a
                // ValueRef.FromStruct — the resulting binding has no arena
                // dependency and survives the batch recycle without
                // touching VariableStore.
                EvaluationFrame batchFrame = new(row, batch.Arena, context.VariableStore, context);
                ValueRef[] fieldRefs = new ValueRef[colCount];
                for (int j = 0; j < colCount; j++)
                {
                    fieldRefs[j] = ExpressionEvaluator.ToValueRef(row[j], batchFrame);
                }
                // Intern the row schema once into the batch-shared registry. Sharing
                // the registry across all queries in the batch (FOR source, body
                // SELECTs, …) is what makes the type-id resolvable from inside the
                // body — without it the body's renderers fall back to f0..fN.
                if (rowTypeId == 0)
                {
                    StructFieldDescriptor[] fieldDescriptors = new StructFieldDescriptor[colCount];
                    for (int j = 0; j < colCount; j++)
                    {
                        int fieldTypeId = context.Types.InternScalarType(fieldRefs[j].Kind);
                        fieldDescriptors[j] = new StructFieldDescriptor(fieldNames[j], fieldTypeId);
                    }
                    rowTypeId = (ushort)context.Types.InternStructType(fieldDescriptors);
                }
                ValueRef rowStruct = ValueRef.FromStruct(fieldRefs, rowTypeId);

                context.VariableScope.PushFrame();
                try
                {
                    context.VariableScope.Declare(
                        forIn.VariableName, rowStruct, fieldNames);
                    try
                    {
                        await ExecuteOneEventfulAsync(forIn.Body, context, onEvent, nextCellId, currentCellId, ct)
                            .ConfigureAwait(false);
                    }
                    catch (LoopContinueSignal)
                    {
                        // Skip the rest of this row's body; outer for-loop advances.
                    }
                    catch (LoopBreakSignal)
                    {
                        broke = true;
                    }
                }
                finally
                {
                    context.VariableScope.PopFrame();
                }

                if (broke) break;
            }
        }
    }

    /// <summary>
    /// Coerces a numeric <see cref="DataValue"/> to <see cref="long"/> for
    /// FOR-counter bounds. Throws on non-numeric or null input;
    /// <paramref name="context"/> is interpolated into the message so the
    /// user sees which side of the loop bound failed.
    /// </summary>
    private static long ToInt64(DataValue value, string context)
    {
        if (value.IsNull)
        {
            throw new InvalidOperationException(
                $"{context} evaluated to NULL; numeric value required.");
        }
        return value.Kind switch
        {
            DataKind.Int8 => value.AsInt8(),
            DataKind.Int16 => value.AsInt16(),
            DataKind.Int32 => value.AsInt32(),
            DataKind.Int64 => value.AsInt64(),
            DataKind.UInt8 => value.AsUInt8(),
            DataKind.UInt16 => value.AsUInt16(),
            DataKind.UInt32 => value.AsUInt32(),
            DataKind.UInt64 => checked((long)value.AsUInt64()),
            DataKind.Float32 => (long)value.AsFloat32(),
            DataKind.Float64 => (long)value.AsFloat64(),
            _ => throw new InvalidOperationException(
                $"{context} evaluated to {value.Kind}; numeric value required."),
        };
    }


    /// <summary>
    /// Evaluates <paramref name="expression"/> by synthesising
    /// <c>SELECT &lt;expression&gt;</c>, planning it through the catalog,
    /// and reading the single resulting cell. The value is stabilised
    /// into <see cref="ExecutionContext.VariableStore"/> so it remains valid
    /// after the synthetic query's per-batch arena recycles. Throws if
    /// the expression doesn't yield exactly one row.
    /// </summary>
    /// <remarks>
    /// Any <see cref="SubqueryExpression"/> nodes in <paramref name="expression"/>
    /// are pre-folded into <see cref="LiteralExpression"/> nodes before the
    /// synthesise step. The catalog's sync planner doesn't run the
    /// scalar-subquery rewriter, so a surviving <c>SubqueryExpression</c>
    /// would crash at evaluation time. Pre-folding handles common procedural
    /// shapes — <c>DECLARE @c INT64 = (SELECT count(*) FROM t)</c>,
    /// <c>SET @x = (SELECT max(v) FROM t) + 1</c>, etc. — by running each
    /// inner SELECT through the same engine the user query path uses.
    /// </remarks>
    private Task<DataValue> EvaluateScalarAsync(
        Expression expression, ExecutionContext context, CancellationToken ct)
        => ProceduralEvaluator.EvaluateScalarAsync(expression, context, ct);

    private Task<bool> EvaluatePredicateAsync(
        Expression expression, ExecutionContext context, CancellationToken ct)
        => ProceduralEvaluator.EvaluatePredicateAsync(expression, context, ct);

    private static Dictionary<string, object?> SnapshotRootBindings(ExecutionContext context)
    {
        // Walk every visible binding in the scope chain and materialise
        // into managed form. The scope now holds ValueRefs (managed payloads
        // for non-inline values), so the snapshot is independent of
        // VariableStore — the conversion is purely a kind-switch.
        Dictionary<string, object?> snapshot = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, ValueRef> entry in context.VariableScope.EnumerateVisible())
        {
            snapshot[entry.Key] = Materialize(entry.Value);
        }
        return snapshot;
    }

    /// <summary>
    /// Materialises a <see cref="ValueRef"/> into managed form for
    /// post-batch inspection. Coverage matches what's testable today:
    /// numerics, booleans, strings, NULLs. Arrays and other composite
    /// kinds return the raw <see cref="ValueRef"/> boxed — the caller
    /// can decide how to surface them (state-inspector renders show the
    /// shape, integration tests compare numeric/string scalars).
    /// </summary>
    private static object? Materialize(ValueRef value)
    {
        if (value.IsNull) return null;
        if (value.IsArray) return value; // boxed for caller to inspect
        return value.Kind switch
        {
            DataKind.Boolean => value.AsBoolean(),
            DataKind.UInt8 => value.AsUInt8(),
            DataKind.Int8 => value.AsInt8(),
            DataKind.Int16 => value.AsInt16(),
            DataKind.UInt16 => value.AsUInt16(),
            DataKind.Int32 => value.AsInt32(),
            DataKind.UInt32 => value.AsUInt32(),
            DataKind.Int64 => value.AsInt64(),
            DataKind.UInt64 => value.AsUInt64(),
            DataKind.Float32 => value.AsFloat32(),
            DataKind.Float64 => value.AsFloat64(),
            DataKind.String => value.AsString(),
            _ => value, // boxed ValueRef — caller can stabilise / inspect manually
        };
    }
}

/// <summary>
/// Result of running a procedural batch. Today a thin shape: just a
/// snapshot of any bindings the host wants to inspect post-run. Slice 5
/// will extend this with per-cell row data + streaming events.
/// </summary>
public sealed class BatchResult
{
    private readonly IReadOnlyDictionary<string, object?> _finalBindings;

    internal BatchResult(IReadOnlyDictionary<string, object?> finalBindings)
    {
        _finalBindings = finalBindings;
    }

    /// <summary>
    /// Snapshot of root-frame variable bindings at the moment the batch
    /// completed, with values materialised to managed types so they remain
    /// valid after <c>ExecutionContext.VariableStore</c> disposes. Useful for
    /// tests that assert "@x ended up as 5" without inspecting raw
    /// DataValue offsets.
    /// </summary>
    public IReadOnlyDictionary<string, object?> FinalBindings => _finalBindings;
}
