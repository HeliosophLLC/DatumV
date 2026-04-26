using System.Diagnostics;
using System.Text.Json;
using DatumIngest.Catalog;
using DatumIngest.DatumFile.Sidecar;
using DatumIngest.Diagnostics;
using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Web.Execution;

/// <summary>
/// Runs SQL batches through the <see cref="BatchExecutor"/> and streams
/// per-cell events (schema, rows, truncation, completion, errors,
/// model-token chunks) as NDJSON to an output stream.
/// </summary>
/// <remarks>
/// <para>
/// Single execution path: every batch goes through <see cref="BatchExecutor"/>.
/// Procedural blocks, single SELECT, multi-statement scripts all funnel
/// through the same event channel. LLM token streams arrive as
/// <c>chunk</c> events live on the same wire.
/// </para>
/// <para>
/// Cancellation: when the caller's <see cref="CancellationToken"/> fires
/// (typically <c>HttpContext.RequestAborted</c>), batch execution stops
/// and a final <c>error</c> event with <c>message:"cancelled"</c> is
/// emitted, followed by the symmetric <c>complete</c> event so any
/// pending NDJSON parse on the client sees a clean terminator.
/// </para>
/// </remarks>
public sealed class QueryStreamService
{
    private readonly TableCatalog _catalog;

    public QueryStreamService(TableCatalog catalog)
    {
        _catalog = catalog;
    }

    /// <summary>
    /// Streams an NDJSON batch response into <paramref name="output"/>.
    /// Returns when the batch completes, an error terminates it, or the
    /// caller cancels. Throws only on writer-level failures the caller
    /// can't recover from; everything else lands as an inline NDJSON
    /// <c>error</c> event.
    /// </summary>
    public Task ExecuteAsync(
        string sql,
        int maxRows,
        TraceOptions trace,
        Stream output,
        JsonSerializerOptions jsonOptions,
        CancellationToken ct)
        => ExecuteAsync(sql, maxRows, trace, parameters: null, output, jsonOptions, ct);

    /// <summary>
    /// Streams an NDJSON batch response, binding any <c>$name</c> parameter
    /// references in the parsed statements against <paramref name="parameters"/>
    /// before planning. Pass <see langword="null"/> or an empty dictionary
    /// when no parameters are in play.
    /// </summary>
    public async Task ExecuteAsync(
        string sql,
        int maxRows,
        TraceOptions trace,
        IReadOnlyDictionary<string, ParameterValue>? parameters,
        Stream output,
        JsonSerializerOptions jsonOptions,
        CancellationToken ct)
    {
        Stopwatch sw = Stopwatch.StartNew();

        // When the caller asked for a trace, attach a RecentActivityLog for
        // the duration of the request. Every Operators/Scalars span fired
        // during execution lands in its ring; the sidecar timer drains
        // entries to the wire as trace_sample events. Per-request scope
        // avoids the per-row Activity allocation cost for the (much larger)
        // population of untraced requests, which get the default
        // zero-listener path.
        //
        // Ring sizing scales with the source set. Operators-only fits a
        // few hundred batches in 4096. Scalars-on is per-row per-function —
        // bump to 64Ki so 1 second's worth of moderate-cardinality scalar
        // dispatch fits without overflow, since the sidecar drains at 1Hz.
        // Overflow is reported via the `dropped` field on trace_sample so
        // the UI can flag a partial trace.
        string[] enabledSources = BuildSourceFilter(trace);
        RecentActivityLog? traceLog = trace.IsEnabled
            ? new RecentActivityLog(capacity: trace.Scalars ? 65_536 : 4096)
            : null;

        // When tracing is enabled, every write to the response stream
        // serialises through this semaphore — BatchExecutor's OnEvent
        // path (rows, schema, memory_sample) and the trace sidecar Task
        // both produce JSON lines concurrently, and Stream.WriteAsync
        // on the response body offers no built-in ordering. Without the
        // lock, two writes can interleave their bytes and produce
        // `{...}{...}` on a single line, which the client's JSON.parse
        // rejects with "Unexpected non-whitespace character after JSON".
        // Null when tracing is off — only the BatchExecutor-serialised
        // path writes, so no contention exists.
        SemaphoreSlim? writeLock = traceLog is not null ? new SemaphoreSlim(1, 1) : null;

        // Parse outside the response body — parse errors map to an
        // inline error+complete event pair so the wire format stays
        // consistent. Callers that want a 400 instead can detect a
        // pre-execute parse failure by checking statements.Count == 0
        // before invoking the service (not done here for symmetry).
        IReadOnlyList<(Statement Statement, string SourceText)> statements;
        try
        {
            statements = SqlParser.ParseBatchWithText(sql);
        }
        catch (Exception ex)
        {
            traceLog?.Dispose();
            await WriteEventAsync(output, jsonOptions, writeLock: null, new ErrorEvent("error", null, ex.Message, ex.ToString()), ct).ConfigureAwait(false);
            await WriteEventAsync(output, jsonOptions, writeLock: null, new CompleteEvent("complete", sw.Elapsed.TotalMilliseconds), ct).ConfigureAwait(false);
            return;
        }

        if (statements.Count == 0)
        {
            traceLog?.Dispose();
            await WriteEventAsync(output, jsonOptions, writeLock, new ErrorEvent("error", null, "empty SQL input", null), ct).ConfigureAwait(false);
            await WriteEventAsync(output, jsonOptions, writeLock, new CompleteEvent("complete", sw.Elapsed.TotalMilliseconds), ct).ConfigureAwait(false);
            return;
        }

        // Bind $name parameters across the whole batch. The binder validates
        // that the union of referenced names matches the supplied names
        // exactly — mismatches surface as an inline error event with the
        // offending parameter name, identical to a parse failure in shape.
        // A non-null but empty dictionary is the "no parameters supplied"
        // signal from the multipart envelope; we still run the binder so
        // SQL that mentions a `$name` without a value surfaces the
        // canonical "parameter not supplied" error rather than a less
        // helpful evaluator failure further down.
        if (parameters is not null)
        {
            try
            {
                Statement[] bareStatements = new Statement[statements.Count];
                for (int i = 0; i < statements.Count; i++)
                    bareStatements[i] = statements[i].Statement;

                IReadOnlyList<Statement> bound = ParameterBinder.Bind(bareStatements, parameters);

                (Statement, string)[] reseated = new (Statement, string)[statements.Count];
                for (int i = 0; i < statements.Count; i++)
                    reseated[i] = (bound[i], statements[i].SourceText);

                statements = reseated;
            }
            catch (ArgumentException ex)
            {
                traceLog?.Dispose();
                await WriteEventAsync(output, jsonOptions, writeLock, new ErrorEvent("error", null, ex.Message, ex.ToString()), ct).ConfigureAwait(false);
                await WriteEventAsync(output, jsonOptions, writeLock, new CompleteEvent("complete", sw.Elapsed.TotalMilliseconds), ct).ConfigureAwait(false);
                return;
            }
        }

        string sessionId = Guid.NewGuid().ToString("N");
        SidecarRegistry registry = _catalog.SidecarRegistry;

        await WriteEventAsync(output, jsonOptions, writeLock, new SessionEvent("session", sessionId), ct).ConfigureAwait(false);

        // Trace plumbing — only constructed when at least one source is
        // enabled. The sidecar drains the log at 1Hz and attributes entries
        // to whichever cell is currently in-flight. ExecuteBatchAsync's
        // OnEvent advances the cursor + emits the cell's terminal
        // trace_complete event on CellCompleted / CellFailed.
        TraceState? traceState = null;
        Task? sidecarTask = null;
        using CancellationTokenSource sidecarCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (traceLog is not null && writeLock is not null)
        {
            traceState = new TraceState(traceLog, enabledSources, writeLock, output, jsonOptions);
            sidecarTask = Task.Run(() => RunTraceSidecarAsync(traceState, sidecarCts.Token), sidecarCts.Token);
        }

        bool errorEmitted = false;
        try
        {
            await ExecuteBatchAsync(statements, maxRows, output, jsonOptions, writeLock, registry, traceState, ct)
                .ConfigureAwait(false);

            sw.Stop();
            await WriteEventAsync(output, jsonOptions, writeLock, new CompleteEvent("complete", sw.Elapsed.TotalMilliseconds), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                await WriteEventAsync(output, jsonOptions, writeLock, new ErrorEvent("error", null, "cancelled", null), CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* response stream may already be closed by the abort */ }
            errorEmitted = true;
        }
        catch (PreFlightRequiredException ex)
        {
            await WriteEventAsync(
                output, jsonOptions, writeLock,
                ToPreFlightEvent(ex), ct).ConfigureAwait(false);
            errorEmitted = true;
        }
        catch (Exception ex)
        {
            await WriteEventAsync(output, jsonOptions, writeLock, new ErrorEvent("error", null, ex.Message, ex.ToString()), ct).ConfigureAwait(false);
            errorEmitted = true;
        }
        finally
        {
            // Stop the sidecar first so it doesn't try to write to a
            // closing response stream. Then dispose the trace log (the
            // listener detaches; any in-flight Activity callbacks become
            // no-ops on the disposed log).
            sidecarCts.Cancel();
            if (sidecarTask is not null)
            {
                try { await sidecarTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch { /* sidecar exceptions don't escape this scope */ }
            }
            traceLog?.Dispose();
            writeLock?.Dispose();

            if (errorEmitted)
            {
                try
                {
                    // writeLock already disposed above — but at this
                    // point the sidecar is stopped and BatchExecutor is
                    // unwinding, so passing null is safe (the lock can't
                    // be needed for serialisation anymore).
                    await WriteEventAsync(output, jsonOptions, writeLock: null, new CompleteEvent("complete", sw.Elapsed.TotalMilliseconds), CancellationToken.None).ConfigureAwait(false);
                }
                catch { /* response stream broken; nothing to do */ }
            }
        }
    }

    private async Task ExecuteBatchAsync(
        IReadOnlyList<(Statement Statement, string SourceText)> statements,
        int maxRows,
        Stream output,
        JsonSerializerOptions jsonOptions,
        SemaphoreSlim? writeLock,
        SidecarRegistry registry,
        TraceState? traceState,
        CancellationToken ct)
    {
        BatchExecutor executor = new(_catalog);

        // Per-cell row-count + schema-emitted state. Reset on each
        // CellStartedBatchEvent so each cell has its own row budget.
        bool schemaEmitted = false;
        int cellRowCount = 0;
        bool cellTruncated = false;
        // When a cell fails, the executor re-throws after we emit the
        // per-cell error event. The outer try/catch in ExecuteAsync
        // would normally write a top-level error for the same failure
        // — this flag tells it to suppress the duplicate so the client
        // only sees one error per failure.
        bool cellErrorEmitted = false;

        async ValueTask OnEvent(BatchEvent ev)
        {
            switch (ev)
            {
                case CellStartedBatchEvent started:
                    schemaEmitted = false;
                    cellRowCount = 0;
                    cellTruncated = false;
                    if (traceState is not null) traceState.OnCellStarted(started.CellId);
                    await WriteEventAsync(output, jsonOptions, writeLock, new CellStartedEvent("cell_started", started.CellId, started.Kind, null), ct).ConfigureAwait(false);
                    break;

                case CellRowBatchEvent rowEvent:
                {
                    if (cellTruncated) break; // drain remaining batches in this cell

                    RowBatch batch = rowEvent.Batch;
                    Arena arena = batch.Arena;

                    if (!schemaEmitted)
                    {
                        await WriteEventAsync(output, jsonOptions, writeLock, new SchemaEvent("schema", rowEvent.CellId, BuildSchema(batch)), ct).ConfigureAwait(false);
                        schemaEmitted = true;
                    }

                    for (int r = 0; r < batch.Count; r++)
                    {
                        if (cellRowCount >= maxRows)
                        {
                            cellTruncated = true;
                            break;
                        }
                        Row row = batch[r];
                        JsonCell[] cells = new JsonCell[batch.ColumnLookup.Count];
                        for (int c = 0; c < batch.ColumnLookup.Count; c++)
                        {
                            cells[c] = WebCellFormatter.Format(row[c], arena, registry, batch.Types, batch.TypeIdTranslations);
                        }
                        await WriteEventAsync(output, jsonOptions, writeLock, new RowEvent("row", rowEvent.CellId, cells), ct).ConfigureAwait(false);
                        cellRowCount++;
                    }
                    break;
                }

                case CellMemorySampleBatchEvent memEvent:
                    // 1Hz residency sample. Translates to a wire `memory_sample`
                    // event the UI feeds into its sparkline + chip.
                    await WriteEventAsync(
                        output,
                        jsonOptions,
                        writeLock,
                        new MemorySampleEvent(
                            "memory_sample",
                            memEvent.CellId,
                            memEvent.ElapsedMs,
                            memEvent.RowBytes,
                            memEvent.ArenaBytes,
                            memEvent.PeakRowBytes,
                            memEvent.BudgetBytes,
                            memEvent.VramUsedBytes,
                            memEvent.VramTotalBytes),
                        ct).ConfigureAwait(false);
                    break;

                case CellCompletedBatchEvent completed:
                    if (cellTruncated)
                    {
                        await WriteEventAsync(output, jsonOptions, writeLock, new TruncatedEvent("truncated", completed.CellId, cellRowCount), ct).ConfigureAwait(false);
                    }
                    // Drain any trace entries still in the ring for this
                    // cell (the 1Hz sidecar might be mid-tick); emit the
                    // terminal trace_complete so the client can freeze
                    // the popover state. Must precede cell_completed so
                    // the client sees the cell's trace tail before the
                    // cell formally ends.
                    if (traceState is not null)
                    {
                        await traceState.DrainAndEmitAsync(isFinal: true, ct).ConfigureAwait(false);
                    }
                    await WriteEventAsync(output, jsonOptions, writeLock, new CellCompletedEvent("cell_completed", completed.CellId, completed.ElapsedMs), ct).ConfigureAwait(false);
                    break;

                case CellFailedBatchEvent failed:
                    cellErrorEmitted = true;
                    // Emit the trace tail before the error so the popover
                    // has the full picture up to the failure.
                    if (traceState is not null)
                    {
                        await traceState.DrainAndEmitAsync(isFinal: true, ct).ConfigureAwait(false);
                    }
                    Console.WriteLine($"CELL FAILED: {failed.Error}");
                    await WriteEventAsync(output, jsonOptions, writeLock, new ErrorEvent("error", failed.CellId, failed.Error.Message, failed.Error.ToString()), ct).ConfigureAwait(false);
                    // Exception propagates after this event; ExecuteAsync
                    // catches it and, seeing cellErrorEmitted, skips the
                    // top-level duplicate. Still writes the terminal
                    // `complete` so the client's stream parser sees a
                    // clean end.
                    break;
            }
        }

        // Widen each statement's source text to nullable so the executor's
        // unified pair signature accepts our parsed-from-SQL list. The
        // verbatim slice is what `CREATE FUNCTION` / `CREATE PROCEDURE`
        // catalog-persistence needs.
        (Statement, string?)[] pairs = new (Statement, string?)[statements.Count];
        for (int i = 0; i < statements.Count; i++)
            pairs[i] = (statements[i].Statement, statements[i].SourceText);

        try
        {
            // Web-app default budget: 300 MiB. Tighter than the engine's 2 GiB
            // default so interactive desktop queries see spill behavior trigger
            // on moderately large workloads rather than running for tens of
            // minutes before the budget engages. Hosted / server deployments
            // can override via configuration once that's wired up.
            const long WebQueryBudgetBytes = 1000L * 1024 * 1024;
            await executor.RunWithEventsAsync(pairs, OnEvent, ct, memoryBudgetBytes: WebQueryBudgetBytes).ConfigureAwait(false);
        }
        catch when (cellErrorEmitted)
        {
            // Per-cell error already on the wire (see CellFailedBatchEvent
            // case above). Swallow the propagated throw so the outer
            // ExecuteAsync's success path writes the terminal `complete`
            // event without an extra top-level error duplicate.
        }
    }

    private static ColumnDescriptor[] BuildSchema(RowBatch batch)
    {
        IReadOnlyList<string> names = batch.ColumnLookup.ColumnNames;
        ColumnDescriptor[] cols = new ColumnDescriptor[names.Count];
        if (batch.Count > 0)
        {
            Row probe = batch[0];
            for (int i = 0; i < names.Count; i++)
            {
                DataValue cell = probe[i];
                cols[i] = new ColumnDescriptor(names[i], cell.Kind.ToString(), cell.IsArray);
            }
        }
        else
        {
            for (int i = 0; i < names.Count; i++)
            {
                cols[i] = new ColumnDescriptor(names[i], DataKind.Unknown.ToString(), false);
            }
        }
        return cols;
    }

    private static readonly byte[] Newline = new[] { (byte)'\n' };

    // Async write — ASP.NET Core disallows synchronous I/O on the response
    // body by default (AllowSynchronousIO = false in Kestrel since 3.0).
    // Calling `Stream.Write` against it throws InvalidOperationException
    // and aborts the response with a 500 before any bytes reach the client.
    //
    // The optional <paramref name="writeLock"/> serialises the JSON +
    // newline + flush triplet so concurrent producers (BatchExecutor
    // OnEvent calls, the trace sidecar Task, the trace cell-boundary
    // drain) can't interleave bytes on the wire and corrupt a line.
    // Without the lock, two producers might land
    // `{...}{"type":"foo"}\n\n` as a single physical line, which the
    // client's JSON.parse rejects with "Unexpected non-whitespace
    // character after JSON". Lock is null when tracing is off (only
    // the BatchExecutor-serialised path writes, so no contention).
    private static PreFlightRequiredEvent ToPreFlightEvent(PreFlightRequiredException ex)
    {
        List<PreFlightModelRequirementWire> models = new(ex.Requirements.Models.Count);
        foreach (PreFlightModelRequirement r in ex.Requirements.Models)
        {
            models.Add(new PreFlightModelRequirementWire(
                r.TypedReference,
                r.Identifier,
                r.CatalogEntryId,
                r.Version,
                r.VersionPinned,
                r.Reason.ToString(),
                r.ApproxSizeMb,
                r.SiblingIdentifiers,
                r.EntryDeprecated,
                r.SupersededBy,
                r.VersionDeprecated,
                r.VersionDeprecationReason));
        }
        List<PreFlightSuggestionWire> suggestions = new(ex.Requirements.Suggestions.Count);
        foreach (PreFlightSuggestion s in ex.Requirements.Suggestions)
        {
            suggestions.Add(new PreFlightSuggestionWire(s.TypedName, s.Suggestion));
        }
        return new PreFlightRequiredEvent("preflight_required", ex.Message, models, suggestions);
    }

    private static async ValueTask WriteEventAsync(
        Stream output,
        JsonSerializerOptions jsonOptions,
        SemaphoreSlim? writeLock,
        object payload,
        CancellationToken ct)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType(), jsonOptions);
        if (writeLock is null)
        {
            await output.WriteAsync(json.AsMemory(), ct).ConfigureAwait(false);
            await output.WriteAsync(Newline.AsMemory(), ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
            return;
        }

        await writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await output.WriteAsync(json.AsMemory(), ct).ConfigureAwait(false);
            await output.WriteAsync(Newline.AsMemory(), ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private static string[] BuildSourceFilter(TraceOptions trace)
    {
        // Materialise the enabled-source name set the RecentActivityLog
        // filters by. Empty when no source is enabled (caller will skip
        // attaching a log at all). Order doesn't matter — the filter is
        // a HashSet lookup downstream.
        if (!trace.IsEnabled) return Array.Empty<string>();
        if (trace.Operators && trace.Scalars)
            return new[] { "DatumIngest.Operators", "DatumIngest.Scalars" };
        if (trace.Operators) return new[] { "DatumIngest.Operators" };
        return new[] { "DatumIngest.Scalars" };
    }

    // 1Hz periodic drain of the trace ring. Runs on a background task for
    // the whole request lifetime; ticks through the shared writeLock so
    // trace samples interleave cleanly with row + memory_sample events.
    private static async Task RunTraceSidecarAsync(
        TraceState state,
        CancellationToken ct)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await state.DrainAndEmitAsync(isFinal: false, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on completion / cancellation.
        }
    }
}

/// <summary>
/// Per-request trace plumbing. Owns the cursor + per-cell counters and
/// serialises trace-sample writes through the same
/// <see cref="SemaphoreSlim"/> the rest of the response stream uses so
/// the sidecar's writes never interleave with row / memory_sample bytes.
/// Constructed only when at least one trace source is enabled.
/// </summary>
internal sealed class TraceState
{
    private readonly RecentActivityLog _log;
    private readonly string[] _enabledSources;
    private readonly SemaphoreSlim _writeLock;
    private readonly Stream _output;
    private readonly JsonSerializerOptions _jsonOptions;
    // Drain mutation lock. Separate from _writeLock so we can read +
    // advance the cursor + serialise the JSON payload OUTSIDE the
    // write-lock critical section, then enter the write-lock only for
    // the network I/O. Without this, the sidecar's per-tick drain
    // would hold the response-stream lock during a CPU-bound serialise,
    // blocking every other producer (rows, memory_sample) until the
    // serialise finished — which is exactly the contention that
    // showed up as occasional UI hitches on trace-enabled queries.
    private readonly object _drainLock = new();
    private string? _currentCellId;
    private DateTimeOffset _cellStartUtc;
    private long _cursor = -1;
    private int _cellTotalEntries;
    private int _cellTotalDropped;
    private bool _cellTraceCompleteEmitted;

    public TraceState(
        RecentActivityLog log,
        string[] enabledSources,
        SemaphoreSlim writeLock,
        Stream output,
        JsonSerializerOptions jsonOptions)
    {
        _log = log;
        _enabledSources = enabledSources;
        _writeLock = writeLock;
        _output = output;
        _jsonOptions = jsonOptions;
    }

    public void OnCellStarted(string cellId)
    {
        // Called from inside the OnEvent handler (already serialised by
        // BatchExecutor's emit-lock). Snapshot under _drainLock so the
        // sidecar timer never sees a half-updated (cellId, startUtc)
        // pair.
        lock (_drainLock)
        {
            _currentCellId = cellId;
            _cellStartUtc = DateTimeOffset.UtcNow;
            // Cursor stays at the request-wide high-water mark — entries
            // captured before this cell started already fired through
            // prior trace_samples (or none, if this is the first cell).
            // Resetting would replay them.
            _cellTotalEntries = 0;
            _cellTotalDropped = 0;
            _cellTraceCompleteEmitted = false;
        }
    }

    /// <summary>
    /// Drains all new entries from the ring and emits a trace_sample event
    /// when at least one entry is present (or when <paramref name="isFinal"/>
    /// is true, in which case a trace_complete also fires regardless of
    /// drain size). No-op when no cell is currently in-flight.
    /// </summary>
    public async ValueTask DrainAndEmitAsync(bool isFinal, CancellationToken ct)
    {
        // Phase 1 (under _drainLock, no I/O): snapshot cell context,
        // advance cursor, build wire-shape payloads. Cheap; bounded by
        // drain.Entries.Length.
        string? cellId;
        TraceSampleEvent? sample = null;
        TraceCompleteEvent? completeEv = null;
        lock (_drainLock)
        {
            cellId = _currentCellId;
            if (cellId is null) return;
            if (_cellTraceCompleteEmitted) return;

            TraceDrainResult drain = _log.DrainSince(_cursor, _enabledSources);
            _cursor = drain.Cursor;
            _cellTotalDropped += drain.Dropped;

            if (drain.Entries.Length > 0)
            {
                TraceEntryWire[] wire = new TraceEntryWire[drain.Entries.Length];
                for (int i = 0; i < drain.Entries.Length; i++)
                {
                    RecentActivityEntry e = drain.Entries[i];
                    double tsMs = Math.Max(0, (e.StartedAt - _cellStartUtc).TotalMilliseconds);
                    wire[i] = new TraceEntryWire(
                        e.Sequence,
                        tsMs,
                        ShortSource(e.SourceName),
                        e.Name,
                        e.ParentName,
                        e.Duration.TotalMilliseconds);
                }
                _cellTotalEntries += wire.Length;
                sample = new TraceSampleEvent("trace_sample", cellId, wire, drain.Dropped);
            }

            if (isFinal)
            {
                completeEv = new TraceCompleteEvent("trace_complete", cellId, _cellTotalEntries, _cellTotalDropped);
                _cellTraceCompleteEmitted = true;
                _currentCellId = null;
            }
        }

        // Phase 2 (under _writeLock): emit the prepared payloads through
        // the shared response-stream lock so we serialize with row +
        // memory_sample writes. Each call to WriteEventAsync acquires
        // and releases independently — if a row write lands between
        // sample and complete, that's correct ordering: it occurred
        // *between* the two phases in real time.
        if (sample is not null)
        {
            await WriteEventAsync(sample, ct).ConfigureAwait(false);
        }
        if (completeEv is not null)
        {
            await WriteEventAsync(completeEv, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask WriteEventAsync(object payload, CancellationToken ct)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType(), _jsonOptions);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(json.AsMemory(), ct).ConfigureAwait(false);
            await _output.WriteAsync(Newline.AsMemory(), ct).ConfigureAwait(false);
            await _output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static string ShortSource(string fullName) => fullName switch
    {
        "DatumIngest.Operators" => "op",
        "DatumIngest.Scalars" => "fn",
        _ => fullName,
    };

    private static readonly byte[] Newline = new[] { (byte)'\n' };
}
