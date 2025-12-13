using System.Diagnostics;

using DatumIngest.Catalog;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

/// <summary>
/// Per-statement event emitted by <see cref="BatchExecutor.RunWithEventsAsync"/>.
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
/// synchronously inside the event handler. Same auto-return contract as
/// <see cref="IQueryPlan.ExecuteAsync(CancellationToken)"/>.
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
/// One chunk of streaming model output produced inside a query/exec
/// cell. Fired live as the underlying <see cref="IModelStreamingSink.OnChunk"/>
/// hook fires, before the row that ultimately carries the collected
/// value lands. For non-LLM models these never fire — only LLMs (and
/// future streaming-capable backends) emit multi-chunk output.
/// </summary>
public sealed record CellChunkBatchEvent(string CellId, string ModelName, string Text) : BatchEvent;

/// <summary>
/// Executes a parsed procedural batch — a list of <see cref="Statement"/>s
/// — against a <see cref="TableCatalog"/>, threading a single
/// <see cref="BatchContext"/> through every child statement so procedural
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
/// <see cref="ExecStatement"/>.
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
    private readonly TableCatalog _catalog;

    /// <summary>
    /// Creates an executor bound to <paramref name="catalog"/>. Each
    /// <see cref="ExecuteAsync"/> call constructs a fresh
    /// <see cref="BatchContext"/>; the executor itself is stateless and
    /// safe to reuse across calls.
    /// </summary>
    public BatchExecutor(TableCatalog catalog)
    {
        _catalog = catalog;
    }

    /// <summary>
    /// Runs <paramref name="statements"/> in order, threading a single
    /// <see cref="BatchContext"/> through every child query so
    /// <c>@var</c> references in any statement resolve to bindings made
    /// by earlier statements. Returns a <see cref="BatchResult"/>
    /// snapshotting the final variable bindings (so tests and host code
    /// can inspect them after the batch context disposes).
    /// </summary>
    public async Task<BatchResult> ExecuteAsync(
        IReadOnlyList<Statement> statements,
        CancellationToken cancellationToken)
    {
        using BatchContext batchContext = new();
        await RunInternalAsync(statements, batchContext, NoOpEventHandler, cancellationToken)
            .ConfigureAwait(false);

        // Snapshot bindings before the batch context disposes — values
        // are read against VariableStore and materialised into managed
        // form, so the result remains valid after the arena is released.
        Dictionary<string, object?> snapshot = SnapshotRootBindings(batchContext);
        return new BatchResult(snapshot);
    }

    /// <summary>
    /// Streaming variant: runs <paramref name="statements"/> and forwards
    /// per-cell lifecycle events to <paramref name="onEvent"/> as each
    /// statement enters / produces rows / completes. Used by the DevWeb
    /// streaming endpoint to translate procedural batches into NDJSON
    /// cell events on the wire.
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
    /// contract from <see cref="IQueryPlan.ExecuteAsync(CancellationToken)"/>
    /// applies — the batch is recycled when the next event fires.
    /// </para>
    /// </remarks>
    public async Task RunWithEventsAsync(
        IReadOnlyList<Statement> statements,
        Func<BatchEvent, ValueTask> onEvent,
        CancellationToken cancellationToken)
    {
        using BatchContext batchContext = new();
        await RunInternalAsync(statements, batchContext, onEvent, cancellationToken)
            .ConfigureAwait(false);
    }

    private static readonly Func<BatchEvent, ValueTask> NoOpEventHandler =
        static _ => ValueTask.CompletedTask;

    private async Task RunInternalAsync(
        IReadOnlyList<Statement> statements,
        BatchContext batchContext,
        Func<BatchEvent, ValueTask> onEvent,
        CancellationToken ct)
    {
        int counter = 0;
        foreach (Statement stmt in statements)
        {
            ct.ThrowIfCancellationRequested();
            await ExecuteOneEventfulAsync(stmt, batchContext, onEvent, () => $"c{counter++}", ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Single-statement dispatch with event emission. Bracket the execution
    /// with <see cref="CellStartedBatchEvent"/> / <see cref="CellCompletedBatchEvent"/>
    /// (or <see cref="CellFailedBatchEvent"/>); recurses into sub-statements
    /// for control-flow constructs so each inner iteration / branch produces
    /// its own cell.
    /// </summary>
    private async Task ExecuteOneEventfulAsync(
        Statement stmt,
        BatchContext batchContext,
        Func<BatchEvent, ValueTask> onEvent,
        Func<string> nextCellId,
        CancellationToken ct)
    {
        string cellId = nextCellId();
        Stopwatch sw = Stopwatch.StartNew();
        await onEvent(new CellStartedBatchEvent(cellId, KindOf(stmt))).ConfigureAwait(false);

        try
        {
            switch (stmt)
            {
                case QueryStatement q:
                {
                    IQueryPlan plan = _catalog.Plan(q);
                    BatchEventStreamingSink sink = new(cellId, onEvent);
                    await foreach (RowBatch batch in plan
                        .ExecuteAsync(ct, sink, batchContext)
                        .ConfigureAwait(false))
                    {
                        await onEvent(new CellRowBatchEvent(cellId, batch)).ConfigureAwait(false);
                    }
                    break;
                }
                case ExecStatement exec:
                {
                    IQueryPlan plan = _catalog.Plan(exec);
                    BatchEventStreamingSink sink = new(cellId, onEvent);
                    await foreach (RowBatch batch in plan
                        .ExecuteAsync(ct, sink, batchContext)
                        .ConfigureAwait(false))
                    {
                        await onEvent(new CellRowBatchEvent(cellId, batch)).ConfigureAwait(false);
                    }
                    break;
                }
                case BlockStatement block:
                {
                    batchContext.VariableScope.PushFrame();
                    try
                    {
                        foreach (Statement child in block.Statements)
                        {
                            ct.ThrowIfCancellationRequested();
                            await ExecuteOneEventfulAsync(child, batchContext, onEvent, nextCellId, ct)
                                .ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        batchContext.VariableScope.PopFrame();
                    }
                    break;
                }
                case IfStatement ifs:
                {
                    bool predicate = await EvaluatePredicateAsync(ifs.Predicate, batchContext, ct)
                        .ConfigureAwait(false);
                    if (predicate)
                    {
                        await ExecuteOneEventfulAsync(ifs.Then, batchContext, onEvent, nextCellId, ct)
                            .ConfigureAwait(false);
                    }
                    else if (ifs.Else is not null)
                    {
                        await ExecuteOneEventfulAsync(ifs.Else, batchContext, onEvent, nextCellId, ct)
                            .ConfigureAwait(false);
                    }
                    break;
                }
                case WhileStatement loop:
                {
                    const int IterationCap = 1_000_000;
                    int iter = 0;
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (iter++ >= IterationCap)
                        {
                            throw new InvalidOperationException(
                                $"WHILE loop exceeded {IterationCap} iterations — likely a missing termination condition.");
                        }
                        bool keepGoing = await EvaluatePredicateAsync(loop.Predicate, batchContext, ct)
                            .ConfigureAwait(false);
                        if (!keepGoing) break;
                        await ExecuteOneEventfulAsync(loop.Body, batchContext, onEvent, nextCellId, ct)
                            .ConfigureAwait(false);
                    }
                    break;
                }
                case ForCounterStatement forC:
                    await ExecuteForCounterAsync(forC, batchContext, onEvent, nextCellId, ct)
                        .ConfigureAwait(false);
                    break;
                case ForInStatement forIn:
                    await ExecuteForInAsync(forIn, batchContext, onEvent, nextCellId, ct)
                        .ConfigureAwait(false);
                    break;
                case DeclareStatement decl:
                    await ExecuteDeclareAsync(decl, batchContext, ct).ConfigureAwait(false);
                    break;
                case SetStatement set:
                    await ExecuteSetAsync(set, batchContext, ct).ConfigureAwait(false);
                    break;
                default:
                    throw new NotSupportedException(
                        $"Procedural statement type '{stmt.GetType().Name}' is not yet supported. " +
                        "CREATE FUNCTION / DDL must be applied through TableCatalog.Plan(string) " +
                        "before the batch runs.");
            }

            await onEvent(new CellCompletedBatchEvent(cellId, sw.Elapsed.TotalMilliseconds))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await onEvent(new CellFailedBatchEvent(cellId, ex)).ConfigureAwait(false);
            throw;
        }
    }

    private static string KindOf(Statement stmt) => stmt switch
    {
        QueryStatement => "select",
        ExecStatement => "exec",
        BlockStatement => "block",
        IfStatement => "if",
        WhileStatement => "while",
        DeclareStatement => "declare",
        SetStatement => "set",
        ForCounterStatement => "for",
        ForInStatement => "for",
        _ => stmt.GetType().Name.ToLowerInvariant(),
    };

    private async Task ExecuteDeclareAsync(
        DeclareStatement decl, BatchContext batchContext, CancellationToken ct)
    {
        DataValue stable;
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
            stable = await EvaluateScalarAsync(effective, batchContext, ct).ConfigureAwait(false);
        }
        else
        {
            // No initializer → bind a typed NULL. The TypeName is required by
            // the parser when there's no initializer, so non-null here.
            DataKind? kind = decl.TypeName is not null
                ? ExpressionTypeResolver.ResolveCastTargetKind(decl.TypeName)
                : null;
            if (kind is null)
            {
                throw new InvalidOperationException(
                    $"DECLARE @{decl.VariableName}: cannot resolve type name '{decl.TypeName}'. " +
                    "Use a recognised SQL type (Int32, Float64, String, Boolean, etc.) or supply an initializer.");
            }
            stable = DataValue.Null(kind.Value);
        }

        batchContext.VariableScope.Declare(decl.VariableName, stable);
    }

    private async Task ExecuteSetAsync(
        SetStatement set, BatchContext batchContext, CancellationToken ct)
    {
        DataValue stable = await EvaluateScalarAsync(set.Value, batchContext, ct).ConfigureAwait(false);
        batchContext.VariableScope.Set(set.VariableName, stable);
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
    private async Task ExecuteForCounterAsync(
        ForCounterStatement forC,
        BatchContext batchContext,
        Func<BatchEvent, ValueTask> onEvent,
        Func<string> nextCellId,
        CancellationToken ct)
    {
        DataValue startVal = await EvaluateScalarAsync(forC.Start, batchContext, ct).ConfigureAwait(false);
        DataValue endVal = await EvaluateScalarAsync(forC.End, batchContext, ct).ConfigureAwait(false);
        long start = ToInt64(startVal, $"FOR @{forC.VariableName} start");
        long end = ToInt64(endVal, $"FOR @{forC.VariableName} end");

        batchContext.VariableScope.PushFrame();
        try
        {
            if (start > end) return;
            for (long i = start; i <= end; i++)
            {
                ct.ThrowIfCancellationRequested();
                DataValue iValue = DataValue.FromInt64(i);
                if (i == start)
                {
                    batchContext.VariableScope.Declare(forC.VariableName, iValue);
                }
                else
                {
                    batchContext.VariableScope.Set(forC.VariableName, iValue);
                }
                await ExecuteOneEventfulAsync(forC.Body, batchContext, onEvent, nextCellId, ct)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            batchContext.VariableScope.PopFrame();
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
    /// <see cref="BatchContext.VariableStore"/> before the binding
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
        BatchContext batchContext,
        Func<BatchEvent, ValueTask> onEvent,
        Func<string> nextCellId,
        CancellationToken ct)
    {
        QueryStatement sourceQuery = new(forIn.Source);
        IQueryPlan plan = _catalog.Plan(sourceQuery);

        IReadOnlyList<string>? fieldNames = null;

        await foreach (RowBatch batch in plan
            .ExecuteAsync(ct, streamingSink: null, batchContext)
            .ConfigureAwait(false))
        {
            // Field names are stable across batches in a single query plan,
            // so capture once on the first batch and reuse for every row.
            fieldNames ??= batch.ColumnLookup.ColumnNames;
            int colCount = fieldNames.Count;

            for (int rowIdx = 0; rowIdx < batch.Count; rowIdx++)
            {
                ct.ThrowIfCancellationRequested();
                Row row = batch[rowIdx];

                // Stabilise field values into the procedure-lifetime store,
                // then build a struct that points entirely into VariableStore.
                DataValue[] fields = new DataValue[colCount];
                for (int j = 0; j < colCount; j++)
                {
                    fields[j] = DataValueRetention.Stabilize(
                        row[j], batch.Arena, batchContext.VariableStore);
                }
                DataValue rowStruct = DataValue.FromStruct(
                    (short)colCount, fields, batchContext.VariableStore);

                batchContext.VariableScope.PushFrame();
                try
                {
                    batchContext.VariableScope.Declare(
                        forIn.VariableName, rowStruct, fieldNames);
                    await ExecuteOneEventfulAsync(forIn.Body, batchContext, onEvent, nextCellId, ct)
                        .ConfigureAwait(false);
                }
                finally
                {
                    batchContext.VariableScope.PopFrame();
                }
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
    /// into <see cref="BatchContext.VariableStore"/> so it remains valid
    /// after the synthetic query's per-batch arena recycles. Throws if
    /// the expression doesn't yield exactly one row.
    /// </summary>
    private async Task<DataValue> EvaluateScalarAsync(
        Expression expression, BatchContext batchContext, CancellationToken ct)
    {
        QueryStatement synthetic = new(
            new SelectQueryExpression(
                new SelectStatement(Columns: [new SelectColumn(expression)])));
        IQueryPlan plan = _catalog.Plan(synthetic);

        DataValue stable = default;
        bool captured = false;
        await foreach (RowBatch batch in plan
            .ExecuteAsync(ct, streamingSink: null, batchContext)
            .ConfigureAwait(false))
        {
            if (captured) continue; // drain remainder; auto-pool happens on iteration
            if (batch.Count > 0)
            {
                // Stabilise into the variable store while the producing
                // arena is still alive (current batch's lifetime).
                stable = DataValueRetention.Stabilize(
                    batch[0][0], batch.Arena, batchContext.VariableStore);
                captured = true;
            }
        }

        if (!captured)
        {
            throw new InvalidOperationException(
                "Procedural expression evaluation produced zero rows; expected exactly one.");
        }
        return stable;
    }

    private async Task<bool> EvaluatePredicateAsync(
        Expression expression, BatchContext batchContext, CancellationToken ct)
    {
        DataValue value = await EvaluateScalarAsync(expression, batchContext, ct).ConfigureAwait(false);
        // Three-valued logic: NULL is false for control-flow purposes,
        // matching T-SQL (which treats NULL as "unknown" → branch not taken).
        if (value.IsNull) return false;
        if (value.Kind == DataKind.Boolean) return value.AsBoolean();

        throw new InvalidOperationException(
            $"Procedural predicate evaluated to {value.Kind} rather than Boolean. " +
            "IF / WHILE predicates must be boolean-valued expressions.");
    }

    private static Dictionary<string, object?> SnapshotRootBindings(BatchContext batchContext)
    {
        // Walk every visible binding in the scope chain and materialise
        // into managed form. The result must outlive the batch context's
        // VariableStore (which is about to dispose), so we convert each
        // value here while VariableStore is still alive.
        Dictionary<string, object?> snapshot = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, DataValue> entry in batchContext.VariableScope.EnumerateVisible())
        {
            snapshot[entry.Key] = Materialize(entry.Value, batchContext.VariableStore);
        }
        return snapshot;
    }

    /// <summary>
    /// Materialises a <see cref="DataValue"/> into managed form for
    /// post-batch inspection. Coverage matches what's testable today:
    /// numerics, booleans, strings, NULLs. Unsupported kinds return the
    /// raw <see cref="DataValue"/> boxed — the caller can decide how to
    /// surface it.
    /// </summary>
    private static object? Materialize(DataValue value, IValueStore store)
    {
        if (value.IsNull) return null;
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
            DataKind.String => value.AsString(store),
            _ => value, // boxed DataValue — caller can stabilise / inspect manually
        };
    }
}

/// <summary>
/// Adapter that turns the synchronous <see cref="IModelStreamingSink"/>
/// hook into <see cref="CellChunkBatchEvent"/> emissions on the
/// <see cref="BatchExecutor"/>'s event channel. Constructed per cell
/// (cellId is captured); attached to <see cref="IQueryPlan.ExecuteAsync(System.Threading.CancellationToken, IModelStreamingSink?)"/>
/// so model-emitting cells produce live chunk events alongside their
/// row events.
/// </summary>
/// <remarks>
/// <strong>Sync-over-async contract.</strong> The sink interface is
/// synchronous (called from inside the operator's await chain), so the
/// adapter blocks on the consumer's <c>ValueTask</c> if it doesn't
/// complete synchronously. DevWeb's NDJSON sink writes synchronously
/// to the response stream and returns <see cref="ValueTask.CompletedTask"/>,
/// so the block is a no-op. Other consumers should follow the same
/// rule for chunk events or buffer.
/// </remarks>
internal sealed class BatchEventStreamingSink : IModelStreamingSink
{
    private readonly string _cellId;
    private readonly Func<BatchEvent, ValueTask> _onEvent;

    public BatchEventStreamingSink(string cellId, Func<BatchEvent, ValueTask> onEvent)
    {
        _cellId = cellId;
        _onEvent = onEvent;
    }

    public void OnChunk(string modelName, ValueRef chunk)
    {
        if (chunk.IsNull || chunk.Kind != DataKind.String) return;
        string text = chunk.AsString();
        if (text.Length == 0) return;

        ValueTask vt = _onEvent(new CellChunkBatchEvent(_cellId, modelName, text));
        if (!vt.IsCompletedSuccessfully)
        {
            vt.AsTask().GetAwaiter().GetResult();
        }
    }

    public void OnCompleted(string modelName)
    {
        // Per-cell completion is signalled by CellCompletedBatchEvent
        // emitted from the executor; nothing to do here.
    }

    public void OnFailed(string modelName, Exception exception)
    {
        // Per-cell failure is signalled by CellFailedBatchEvent emitted
        // from the executor's catch handler; nothing to do here.
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
    /// valid after <c>BatchContext.VariableStore</c> disposes. Useful for
    /// tests that assert "@x ended up as 5" without inspecting raw
    /// DataValue offsets.
    /// </summary>
    public IReadOnlyDictionary<string, object?> FinalBindings => _finalBindings;
}
