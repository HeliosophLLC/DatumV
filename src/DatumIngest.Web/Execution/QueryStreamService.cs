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
        bool trace,
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
        bool trace,
        IReadOnlyDictionary<string, ParameterValue>? parameters,
        Stream output,
        JsonSerializerOptions jsonOptions,
        CancellationToken ct)
    {
        Stopwatch sw = Stopwatch.StartNew();

        // When the caller asked for a trace, attach a RecentActivityLog for
        // the duration of the request. Every Operators/Scalars span fired
        // during execution lands in its ring; FormatTrace() renders it for
        // the wire. Per-request scope avoids the per-row Activity allocation
        // cost for the (much larger) population of untraced requests, which
        // get the default zero-listener path.
        //
        // The 4096-entry ring covers a few hundred batches of operator pulls
        // plus the corresponding scalar spans without overflowing — large
        // enough for typical interactive queries to record fully. If we hit
        // the cap on a long-running query the oldest entries roll off,
        // which is the standard ring-buffer trade.
        RecentActivityLog? traceLog = trace ? new RecentActivityLog(capacity: 4096) : null;

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
            await WriteEventAsync(output, jsonOptions, new ErrorEvent("error", null, ex.Message, ex.ToString()), ct).ConfigureAwait(false);
            await WriteEventAsync(output, jsonOptions, new CompleteEvent("complete", sw.Elapsed.TotalMilliseconds), ct).ConfigureAwait(false);
            return;
        }

        if (statements.Count == 0)
        {
            traceLog?.Dispose();
            await WriteEventAsync(output, jsonOptions, new ErrorEvent("error", null, "empty SQL input", null), ct).ConfigureAwait(false);
            await WriteEventAsync(output, jsonOptions, new CompleteEvent("complete", sw.Elapsed.TotalMilliseconds), ct).ConfigureAwait(false);
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
                await WriteEventAsync(output, jsonOptions, new ErrorEvent("error", null, ex.Message, ex.ToString()), ct).ConfigureAwait(false);
                await WriteEventAsync(output, jsonOptions, new CompleteEvent("complete", sw.Elapsed.TotalMilliseconds), ct).ConfigureAwait(false);
                return;
            }
        }

        string sessionId = Guid.NewGuid().ToString("N");
        SidecarRegistry registry = _catalog.SidecarRegistry;

        await WriteEventAsync(output, jsonOptions, new SessionEvent("session", sessionId), ct).ConfigureAwait(false);

        bool errorEmitted = false;
        try
        {
            await ExecuteBatchAsync(statements, maxRows, output, jsonOptions, registry, ct)
                .ConfigureAwait(false);

            if (traceLog is not null)
            {
                string traceText = traceLog.FormatTrace();
                traceLog.Dispose();
                traceLog = null;
                if (!string.IsNullOrEmpty(traceText))
                {
                    // Trace is batch-wide today; emit attached to a synthetic
                    // "batch" cellId since it spans the whole run. Clients
                    // that filter by cellId can ignore it.
                    await WriteEventAsync(output, jsonOptions, new TraceEvent("trace", "batch", traceText), ct).ConfigureAwait(false);
                }
            }

            sw.Stop();
            await WriteEventAsync(output, jsonOptions, new CompleteEvent("complete", sw.Elapsed.TotalMilliseconds), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                await WriteEventAsync(output, jsonOptions, new ErrorEvent("error", null, "cancelled", null), CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* response stream may already be closed by the abort */ }
            errorEmitted = true;
        }
        catch (Exception ex)
        {
            await WriteEventAsync(output, jsonOptions, new ErrorEvent("error", null, ex.Message, ex.ToString()), ct).ConfigureAwait(false);
            errorEmitted = true;
        }
        finally
        {
            traceLog?.Dispose();

            if (errorEmitted)
            {
                try
                {
                    await WriteEventAsync(output, jsonOptions, new CompleteEvent("complete", sw.Elapsed.TotalMilliseconds), CancellationToken.None).ConfigureAwait(false);
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
        SidecarRegistry registry,
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
                    await WriteEventAsync(output, jsonOptions, new CellStartedEvent("cell_started", started.CellId, started.Kind, null), ct).ConfigureAwait(false);
                    break;

                case CellRowBatchEvent rowEvent:
                {
                    if (cellTruncated) break; // drain remaining batches in this cell

                    RowBatch batch = rowEvent.Batch;
                    Arena arena = batch.Arena;

                    if (!schemaEmitted)
                    {
                        await WriteEventAsync(output, jsonOptions, new SchemaEvent("schema", rowEvent.CellId, BuildSchema(batch)), ct).ConfigureAwait(false);
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
                        await WriteEventAsync(output, jsonOptions, new RowEvent("row", rowEvent.CellId, cells), ct).ConfigureAwait(false);
                        cellRowCount++;
                    }
                    break;
                }

                case CellChunkBatchEvent chunkEvent:
                    // Live model chunk (LLM token, etc.). Translate 1:1 to a
                    // wire `chunk` event scoped to the cell that produced it.
                    await WriteEventAsync(output, jsonOptions, new ChunkWireEvent("chunk", chunkEvent.CellId, chunkEvent.ModelName, chunkEvent.Text), ct).ConfigureAwait(false);
                    break;

                case CellMemorySampleBatchEvent memEvent:
                    // 1Hz residency sample. Translates to a wire `memory_sample`
                    // event the UI feeds into its sparkline + chip.
                    await WriteEventAsync(
                        output,
                        jsonOptions,
                        new MemorySampleEvent(
                            "memory_sample",
                            memEvent.CellId,
                            memEvent.ElapsedMs,
                            memEvent.RowBytes,
                            memEvent.ArenaBytes,
                            memEvent.PeakRowBytes,
                            memEvent.BudgetBytes),
                        ct).ConfigureAwait(false);
                    break;

                case CellCompletedBatchEvent completed:
                    if (cellTruncated)
                    {
                        await WriteEventAsync(output, jsonOptions, new TruncatedEvent("truncated", completed.CellId, cellRowCount), ct).ConfigureAwait(false);
                    }
                    await WriteEventAsync(output, jsonOptions, new CellCompletedEvent("cell_completed", completed.CellId, completed.ElapsedMs), ct).ConfigureAwait(false);
                    break;

                case CellFailedBatchEvent failed:
                    cellErrorEmitted = true;
                    await WriteEventAsync(output, jsonOptions, new ErrorEvent("error", failed.CellId, failed.Error.Message, failed.Error.ToString()), ct).ConfigureAwait(false);
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
            const long WebQueryBudgetBytes = 300L * 1024 * 1024;
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
    private static async ValueTask WriteEventAsync(
        Stream output,
        JsonSerializerOptions jsonOptions,
        object payload,
        CancellationToken ct)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType(), jsonOptions);
        await output.WriteAsync(json.AsMemory(), ct).ConfigureAwait(false);
        await output.WriteAsync(Newline.AsMemory(), ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }
}
