using System.Diagnostics;
using System.Runtime.ExceptionServices;

using DatumIngest.Catalog;
using DatumIngest.Catalog.Plans;
using DatumIngest.Catalog.Registries;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

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
/// cell. Fired live as the underlying <see cref="IModelStreamingSink.OnChunkAsync"/>
/// hook fires, before the row that ultimately carries the collected
/// value lands. For non-LLM models these never fire — only LLMs (and
/// future streaming-capable backends) emit multi-chunk output.
/// </summary>
public sealed record CellChunkBatchEvent(string CellId, string ModelName, string Text) : BatchEvent;

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

    /// <summary>
    /// Internal control-flow signal raised by <see cref="BreakStatement"/>.
    /// Caught by the innermost enclosing loop. If it escapes all loops the
    /// batch / procedure entry points convert it to a clear
    /// <see cref="InvalidOperationException"/>. Singleton for zero allocation.
    /// </summary>
    private sealed class LoopBreakSignal : Exception
    {
        public static readonly LoopBreakSignal Instance = new();
        private LoopBreakSignal() : base("BREAK outside of a loop.") { }
    }

    /// <summary>
    /// Internal control-flow signal raised by <see cref="ContinueStatement"/>.
    /// Caught by the innermost enclosing loop's per-iteration wrapper. If it
    /// escapes all loops the batch / procedure entry points convert it to a
    /// clear <see cref="InvalidOperationException"/>.
    /// </summary>
    private sealed class LoopContinueSignal : Exception
    {
        public static readonly LoopContinueSignal Instance = new();
        private LoopContinueSignal() : base("CONTINUE outside of a loop.") { }
    }

    private readonly TableCatalog _catalog;

    /// <summary>
    /// Creates an executor bound to <paramref name="catalog"/>. Each
    /// <c>ExecuteAsync</c> call constructs a fresh
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
        using BatchContext batchContext = new();
        await RunInternalAsync(statements, batchContext, NoOpEventHandler, cancellationToken)
            .ConfigureAwait(false);

        // Snapshot bindings before the batch context disposes — values
        // are read against VariableStore and materialised into managed
        // form, so the result remains valid after the arena is released.
        Dictionary<string, object?> snapshot = SnapshotRootBindings(batchContext);
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
    public Task RunWithEventsAsync(
        IReadOnlyList<Statement> statements,
        Func<BatchEvent, ValueTask> onEvent,
        CancellationToken cancellationToken)
        => RunWithEventsAsync(WithoutSourceText(statements), onEvent, cancellationToken);

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
        CancellationToken cancellationToken)
    {
        using BatchContext batchContext = new();
        await RunInternalAsync(statements, batchContext, onEvent, cancellationToken)
            .ConfigureAwait(false);
    }

    private static readonly Func<BatchEvent, ValueTask> NoOpEventHandler =
        static _ => ValueTask.CompletedTask;

    private async Task RunInternalAsync(
        IReadOnlyList<(Statement Statement, string? SourceText)> statements,
        BatchContext batchContext,
        Func<BatchEvent, ValueTask> onEvent,
        CancellationToken ct)
    {
        int counter = 0;
        try
        {
            foreach ((Statement stmt, string? sourceText) in statements)
            {
                ct.ThrowIfCancellationRequested();
                await ExecuteOneEventfulAsync(stmt, sourceText, batchContext, onEvent, () => $"c{counter++}", ct)
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

    /// <summary>
    /// Single-statement dispatch with event emission. Bracket the execution
    /// with <see cref="CellStartedBatchEvent"/> / <see cref="CellCompletedBatchEvent"/>
    /// (or <see cref="CellFailedBatchEvent"/>); recurses into sub-statements
    /// for control-flow constructs so each inner iteration / branch produces
    /// its own cell.
    /// </summary>
    private Task ExecuteOneEventfulAsync(
        Statement stmt,
        BatchContext batchContext,
        Func<BatchEvent, ValueTask> onEvent,
        Func<string> nextCellId,
        CancellationToken ct)
        => ExecuteOneEventfulAsync(stmt, sourceText: null, batchContext, onEvent, nextCellId, ct);

    private async Task ExecuteOneEventfulAsync(
        Statement stmt,
        string? sourceText,
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
                    if (TryGetAssignmentForm(q, out IReadOnlyList<string?>? assignTargets))
                    {
                        await ExecuteAssignmentSelectAsync(q, assignTargets!, batchContext, ct)
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
                            procDescriptor, args, batchContext, onEvent, nextCellId, ct)
                            .ConfigureAwait(false);
                        break;
                    }
                    goto default;
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
                    bool broke = false;
                    while (!broke)
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
                        try
                        {
                            await ExecuteOneEventfulAsync(loop.Body, batchContext, onEvent, nextCellId, ct)
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
                case BreakStatement:
                    throw LoopBreakSignal.Instance;
                case ContinueStatement:
                    throw LoopContinueSignal.Instance;
                case PrintStatement print:
                {
                    DataValue value = await EvaluateScalarAsync(print.Value, batchContext, ct)
                        .ConfigureAwait(false);
                    string? text = RenderForPrint(value, batchContext.VariableStore);
                    await onEvent(new CellPrintBatchEvent(cellId, text)).ConfigureAwait(false);
                    break;
                }
                case TryStatement tryStmt:
                {
                    await ExecuteTryAsync(tryStmt, batchContext, onEvent, nextCellId, ct)
                        .ConfigureAwait(false);
                    break;
                }
                case AssertStatement assert:
                {
                    bool holds = await EvaluatePredicateAsync(assert.Predicate, batchContext, ct)
                        .ConfigureAwait(false);
                    if (!holds)
                    {
                        string message;
                        if (assert.Message is not null)
                        {
                            DataValue m = await EvaluateScalarAsync(assert.Message, batchContext, ct)
                                .ConfigureAwait(false);
                            message = RenderForPrint(m, batchContext.VariableStore)
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
                    DataValue messageValue = await EvaluateScalarAsync(raise.Message, batchContext, ct)
                        .ConfigureAwait(false);
                    string message = RenderForPrint(messageValue, batchContext.VariableStore)
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
                    // Mutation/DDL statements return EmptyQueryPlan
                    // (their work happened as a side effect of Plan);
                    // query forms return a real plan whose batches stream
                    // out as CellRowBatchEvents.
                    //
                    // Aligning here means new statement types only need a
                    // case in TableCatalog.Plan — BatchExecutor picks them
                    // up automatically. The source slice (when non-null)
                    // threads through so procedural CREATE FUNCTION /
                    // CREATE PROCEDURE round-trip through catalog
                    // persistence.
                    {
                        IQueryPlan plan = await _catalog.PlanAsync(stmt, sourceText).ConfigureAwait(false);
                        if (plan is not EmptyQueryPlan)
                        {
                            BatchEventStreamingSink sink = new(cellId, onEvent);
                            await foreach (RowBatch batch in plan
                                .ExecuteAsync(ct, sink, batchContext)
                                .ConfigureAwait(false))
                            {
                                await onEvent(new CellRowBatchEvent(cellId, batch)).ConfigureAwait(false);
                            }
                        }
                        break;
                    }
            }

            await onEvent(new CellCompletedBatchEvent(cellId, sw.Elapsed.TotalMilliseconds))
                .ConfigureAwait(false);
        }
        catch (LoopBreakSignal)
        {
            // BREAK / CONTINUE are control flow, not failures. Fire the
            // completed event for this cell and let the signal bubble up
            // to the enclosing loop (or to the entry point, which converts
            // a stray signal to a clear "outside of a loop" error).
            await onEvent(new CellCompletedBatchEvent(cellId, sw.Elapsed.TotalMilliseconds))
                .ConfigureAwait(false);
            throw;
        }
        catch (LoopContinueSignal)
        {
            await onEvent(new CellCompletedBatchEvent(cellId, sw.Elapsed.TotalMilliseconds))
                .ConfigureAwait(false);
            throw;
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
    {
        if (value.IsNull) return null;
        object? managed = Materialize(value, store);
        return managed switch
        {
            null => null,
            bool b => b ? "true" : "false",
            string s => s,
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => managed.ToString(),
        };
    }

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
            stable = isArray ? DataValue.NullArrayOf(kind) : DataValue.Null(kind);
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
    /// caller's scope, opens a fresh <see cref="BatchContext"/> for the
    /// procedure body, declares each parameter from the corresponding
    /// argument value (with <c>IS NOT NULL</c> enforcement when declared),
    /// then runs the body's statements through the same eventful path
    /// that the rest of the executor uses. Cells produced by the body
    /// flow up to the caller's <paramref name="onEvent"/> stream so a
    /// procedure that does <c>SELECT</c>s shows its rows just as if the
    /// body had been inlined.
    /// </summary>
    /// <remarks>
    /// Each invocation gets its own <see cref="BatchContext"/> with a
    /// fresh <see cref="VariableScope"/> and procedure-lifetime arena —
    /// procedures don't share variable state with the caller. Arguments
    /// are evaluated in the CALLER's scope (so they can reference the
    /// caller's <c>@vars</c>), then stabilised across the boundary into
    /// the new context's variable store before the parameter is declared.
    /// </remarks>
    private async Task ExecuteProcedureCallAsync(
        ProcedureDescriptor? descriptor,
        IReadOnlyList<Expression>? arguments,
        BatchContext callerContext,
        Func<BatchEvent, ValueTask> onEvent,
        Func<string> nextCellId,
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

        // New BatchContext for the procedure's lifetime. Disposed at end —
        // the procedure-lifetime arena releases and any variable bindings
        // become unreachable, matching how a top-level procedural batch
        // tears down. Carries the bumped call depth so any further
        // CALLs the body issues see the running total.
        using BatchContext procContext = new() { ProcedureCallDepth = newDepth };
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
        // event consumers see a single coherent stream.
        try
        {
            foreach (Statement bodyStatement in descriptor.Body.Statements)
            {
                ct.ThrowIfCancellationRequested();
                await ExecuteOneEventfulAsync(bodyStatement, procContext, onEvent, nextCellId, ct)
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
    /// <see cref="BatchContext.VariableStore"/> and pushed through
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
        BatchContext batchContext,
        CancellationToken ct)
    {
        IQueryPlan plan = await _catalog.PlanAsync(statement).ConfigureAwait(false);

        await foreach (RowBatch batch in plan
            .ExecuteAsync(ct, streamingSink: null, batchContext)
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
                    DataValue stable = DataValueRetention.Stabilize(
                        row[colIdx], batch.Arena, batchContext.VariableStore);
                    batchContext.VariableScope.Set(variableName, stable);
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
    /// message string is stabilised into <see cref="BatchContext.VariableStore"/>
    /// so it remains valid across any inner query / arena recycle.
    /// </para>
    /// </remarks>
    private async Task ExecuteTryAsync(
        TryStatement tryStmt,
        BatchContext batchContext,
        Func<BatchEvent, ValueTask> onEvent,
        Func<string> nextCellId,
        CancellationToken ct)
    {
        ExceptionDispatchInfo? pending = null;

        try
        {
            try
            {
                await ExecuteOneEventfulAsync(tryStmt.TryBody, batchContext, onEvent, nextCellId, ct)
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
                batchContext.VariableScope.PushFrame();
                try
                {
                    DataValue messageValue = DataValue.FromString(
                        ex.Message ?? string.Empty, batchContext.VariableStore);
                    batchContext.VariableScope.Declare(tryStmt.ErrorVariableName, messageValue);
                    try
                    {
                        await ExecuteOneEventfulAsync(tryStmt.CatchBody, batchContext, onEvent, nextCellId, ct)
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
                    batchContext.VariableScope.PopFrame();
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
                await ExecuteOneEventfulAsync(tryStmt.FinallyBody, batchContext, onEvent, nextCellId, ct)
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
                try
                {
                    await ExecuteOneEventfulAsync(forC.Body, batchContext, onEvent, nextCellId, ct)
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
        IQueryPlan plan = await _catalog.PlanAsync(sourceQuery).ConfigureAwait(false);

        IReadOnlyList<string>? fieldNames = null;
        ushort rowTypeId = 0;
        bool broke = false;

        await foreach (RowBatch batch in plan
            .ExecuteAsync(ct, streamingSink: null, batchContext)
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

                // Stabilise field values into the procedure-lifetime store,
                // then build a struct that points entirely into VariableStore.
                DataValue[] fields = new DataValue[colCount];
                for (int j = 0; j < colCount; j++)
                {
                    fields[j] = DataValueRetention.Stabilize(
                        row[j], batch.Arena, batchContext.VariableStore);
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
                        int fieldTypeId = batchContext.Types.InternScalarType(fields[j].Kind);
                        fieldDescriptors[j] = new StructFieldDescriptor(fieldNames[j], fieldTypeId);
                    }
                    rowTypeId = (ushort)batchContext.Types.InternStructType(fieldDescriptors);
                }
                DataValue rowStruct = DataValue.FromStruct(
                    fields, batchContext.VariableStore, rowTypeId);

                batchContext.VariableScope.PushFrame();
                try
                {
                    batchContext.VariableScope.Declare(
                        forIn.VariableName, rowStruct, fieldNames);
                    try
                    {
                        await ExecuteOneEventfulAsync(forIn.Body, batchContext, onEvent, nextCellId, ct)
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
                    batchContext.VariableScope.PopFrame();
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
    /// Walks <paramref name="expression"/> and replaces every
    /// <see cref="SubqueryExpression"/> with a <see cref="LiteralExpression"/>
    /// holding the result of executing the inner SELECT. Each inner SELECT is
    /// run through the catalog's normal plan path with <paramref name="batchContext"/>
    /// threaded so <c>@var</c> references inside the subquery resolve against
    /// the procedural variable scope. Recurses through the common composable
    /// expression types so subqueries hidden inside arithmetic, casts, or
    /// function arguments still get folded.
    /// </summary>
    /// <remarks>
    /// Currently descends through <see cref="BinaryExpression"/>,
    /// <see cref="UnaryExpression"/>, <see cref="CastExpression"/>,
    /// <see cref="IsNullExpression"/>, <see cref="FunctionCallExpression"/>,
    /// <see cref="InExpression"/>, <see cref="BetweenExpression"/>, and
    /// <see cref="CaseExpression"/>. Other shapes pass through unchanged —
    /// extend the walker as new procedural patterns demand it.
    /// </remarks>
    private async Task<Expression> PrefoldSubqueriesAsync(
        Expression expression, BatchContext batchContext, CancellationToken ct)
    {
        switch (expression)
        {
            case SubqueryExpression subquery:
                return await FoldOneSubqueryAsync(subquery, batchContext, ct).ConfigureAwait(false);

            case CastExpression cast:
            {
                Expression inner = await PrefoldSubqueriesAsync(cast.Expression, batchContext, ct)
                    .ConfigureAwait(false);
                return ReferenceEquals(inner, cast.Expression)
                    ? cast
                    : new CastExpression(inner, cast.TargetType, cast.Span);
            }

            case BinaryExpression binary:
            {
                Expression left = await PrefoldSubqueriesAsync(binary.Left, batchContext, ct)
                    .ConfigureAwait(false);
                Expression right = await PrefoldSubqueriesAsync(binary.Right, batchContext, ct)
                    .ConfigureAwait(false);
                return ReferenceEquals(left, binary.Left) && ReferenceEquals(right, binary.Right)
                    ? binary
                    : new BinaryExpression(left, binary.Operator, right);
            }

            case UnaryExpression unary:
            {
                Expression operand = await PrefoldSubqueriesAsync(unary.Operand, batchContext, ct)
                    .ConfigureAwait(false);
                return ReferenceEquals(operand, unary.Operand)
                    ? unary
                    : new UnaryExpression(unary.Operator, operand);
            }

            case IsNullExpression isNull:
            {
                Expression inner = await PrefoldSubqueriesAsync(isNull.Expression, batchContext, ct)
                    .ConfigureAwait(false);
                return ReferenceEquals(inner, isNull.Expression)
                    ? isNull
                    : new IsNullExpression(inner, isNull.Negated);
            }

            case FunctionCallExpression fn:
            {
                Expression[]? rewrittenArgs = null;
                for (int i = 0; i < fn.Arguments.Count; i++)
                {
                    Expression rewritten = await PrefoldSubqueriesAsync(fn.Arguments[i], batchContext, ct)
                        .ConfigureAwait(false);
                    if (!ReferenceEquals(rewritten, fn.Arguments[i]))
                    {
                        rewrittenArgs ??= [.. fn.Arguments];
                        rewrittenArgs[i] = rewritten;
                    }
                }
                return rewrittenArgs is null
                    ? fn
                    : new FunctionCallExpression(fn.FunctionName, rewrittenArgs, fn.OrderBy, fn.Distinct, fn.Span, fn.WithinGroupOrderBy);
            }

            case InExpression inExpr:
            {
                Expression target = await PrefoldSubqueriesAsync(inExpr.Expression, batchContext, ct)
                    .ConfigureAwait(false);
                Expression[]? rewrittenValues = null;
                for (int i = 0; i < inExpr.Values.Count; i++)
                {
                    Expression rewritten = await PrefoldSubqueriesAsync(inExpr.Values[i], batchContext, ct)
                        .ConfigureAwait(false);
                    if (!ReferenceEquals(rewritten, inExpr.Values[i]))
                    {
                        rewrittenValues ??= [.. inExpr.Values];
                        rewrittenValues[i] = rewritten;
                    }
                }
                return ReferenceEquals(target, inExpr.Expression) && rewrittenValues is null
                    ? inExpr
                    : new InExpression(target, rewrittenValues ?? inExpr.Values, inExpr.Negated);
            }

            case BetweenExpression between:
            {
                Expression target = await PrefoldSubqueriesAsync(between.Expression, batchContext, ct)
                    .ConfigureAwait(false);
                Expression low = await PrefoldSubqueriesAsync(between.Low, batchContext, ct)
                    .ConfigureAwait(false);
                Expression high = await PrefoldSubqueriesAsync(between.High, batchContext, ct)
                    .ConfigureAwait(false);
                return ReferenceEquals(target, between.Expression)
                    && ReferenceEquals(low, between.Low)
                    && ReferenceEquals(high, between.High)
                    ? between
                    : new BetweenExpression(target, low, high, between.Negated);
            }

            case CaseExpression caseExpr:
            {
                Expression? operand = caseExpr.Operand;
                if (operand is not null)
                {
                    operand = await PrefoldSubqueriesAsync(operand, batchContext, ct)
                        .ConfigureAwait(false);
                }
                WhenClause[]? rewrittenClauses = null;
                for (int i = 0; i < caseExpr.WhenClauses.Count; i++)
                {
                    WhenClause clause = caseExpr.WhenClauses[i];
                    Expression cond = await PrefoldSubqueriesAsync(clause.Condition, batchContext, ct)
                        .ConfigureAwait(false);
                    Expression result = await PrefoldSubqueriesAsync(clause.Result, batchContext, ct)
                        .ConfigureAwait(false);
                    if (!ReferenceEquals(cond, clause.Condition) || !ReferenceEquals(result, clause.Result))
                    {
                        rewrittenClauses ??= [.. caseExpr.WhenClauses];
                        rewrittenClauses[i] = new WhenClause(cond, result);
                    }
                }
                Expression? elseResult = caseExpr.ElseResult;
                if (elseResult is not null)
                {
                    elseResult = await PrefoldSubqueriesAsync(elseResult, batchContext, ct)
                        .ConfigureAwait(false);
                }
                bool unchanged = ReferenceEquals(operand, caseExpr.Operand)
                    && rewrittenClauses is null
                    && ReferenceEquals(elseResult, caseExpr.ElseResult);
                return unchanged
                    ? caseExpr
                    : new CaseExpression(operand, rewrittenClauses ?? caseExpr.WhenClauses, elseResult, caseExpr.Span);
            }

            default:
                return expression;
        }
    }

    /// <summary>
    /// Plans + executes the inner SELECT of a <see cref="SubqueryExpression"/>
    /// and returns a <see cref="LiteralExpression"/> wrapping its single-row
    /// single-column result. Mirrors SQL standard scalar-subquery semantics:
    /// zero rows → NULL literal, more than one row → error.
    /// </summary>
    private async Task<Expression> FoldOneSubqueryAsync(
        SubqueryExpression subquery, BatchContext batchContext, CancellationToken ct)
    {
        QueryStatement innerStatement = new(new SelectQueryExpression(subquery.Query));
        IQueryPlan innerPlan = await _catalog.PlanAsync(innerStatement).ConfigureAwait(false);

        DataValue captured = default;
        bool haveValue = false;
        bool tooManyRows = false;
        await foreach (RowBatch batch in innerPlan
            .ExecuteAsync(ct, streamingSink: null, batchContext)
            .ConfigureAwait(false))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (haveValue)
                {
                    tooManyRows = true;
                    break;
                }
                Row row = batch[i];
                if (row.FieldCount != 1)
                {
                    throw new InvalidOperationException(
                        $"Scalar subquery must return exactly one column, but returned {row.FieldCount}.");
                }
                // Stabilise into the procedure-lifetime store before the
                // producing arena recycles on the next iteration.
                captured = DataValueRetention.Stabilize(
                    row[0], batch.Arena, batchContext.VariableStore);
                haveValue = true;
            }
            if (tooManyRows) break;
        }

        if (tooManyRows)
        {
            throw new InvalidOperationException(
                "Scalar subquery returned more than one row.");
        }

        if (!haveValue || captured.IsNull)
        {
            return new LiteralExpression(null);
        }

        // Materialise the DataValue into a CLR object suitable for
        // LiteralExpression. The synthesise-SELECT path will re-pack via
        // the literal lowerer, so primitives flow back through the engine
        // without needing arena access.
        object literal = captured.Kind switch
        {
            DataKind.Int8 => (object)(sbyte)captured.AsInt8(),
            DataKind.Int16 => captured.AsInt16(),
            DataKind.Int32 => captured.AsInt32(),
            DataKind.Int64 => captured.AsInt64(),
            DataKind.UInt8 => (sbyte)captured.AsUInt8(),
            DataKind.Float32 => captured.AsFloat32(),
            DataKind.Float64 => captured.AsFloat64(),
            DataKind.String => captured.AsString(),
            DataKind.Boolean => captured.AsBoolean(),
            _ => captured.ToFloat(),
        };
        return new LiteralExpression(literal);
    }

    /// <summary>
    /// Evaluates <paramref name="expression"/> by synthesising
    /// <c>SELECT &lt;expression&gt;</c>, planning it through the catalog,
    /// and reading the single resulting cell. The value is stabilised
    /// into <see cref="BatchContext.VariableStore"/> so it remains valid
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
    private async Task<DataValue> EvaluateScalarAsync(
        Expression expression, BatchContext batchContext, CancellationToken ct)
    {
        Expression rewritten = await PrefoldSubqueriesAsync(expression, batchContext, ct)
            .ConfigureAwait(false);

        QueryStatement synthetic = new(
            new SelectQueryExpression(
                new SelectStatement(Columns: [new SelectColumn(rewritten)])));
        IQueryPlan plan = await _catalog.PlanAsync(synthetic).ConfigureAwait(false);

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

    public ValueTask OnChunkAsync(string modelName, ValueRef chunk)
    {
        if (chunk.IsNull || chunk.Kind != DataKind.String) return ValueTask.CompletedTask;
        string text = chunk.AsString();
        if (text.Length == 0) return ValueTask.CompletedTask;

        return _onEvent(new CellChunkBatchEvent(_cellId, modelName, text));
    }

    public ValueTask OnCompletedAsync(string modelName) => ValueTask.CompletedTask;
    // Per-cell completion is signalled by CellCompletedBatchEvent emitted
    // from the executor; nothing to do here.

    public ValueTask OnFailedAsync(string modelName, Exception exception) => ValueTask.CompletedTask;
    // Per-cell failure is signalled by CellFailedBatchEvent emitted from
    // the executor's catch handler; nothing to do here.
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
