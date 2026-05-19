using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.DatumFile.V2;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Execution;

/// <summary>
/// Receives <c>PRINT</c> diagnostic output rendered as text.
/// <see cref="ExecutionContext.PrintSink"/> is guaranteed non-null at all
/// times; assign <see langword="null"/> to reset it to the no-op handler.
/// </summary>
public delegate void PrintHandler(string? text);

/// <summary>
/// Shared context passed through the operator tree during query execution.
/// Carries the cancellation token, function registry, and table catalog.
/// </summary>
public sealed class ExecutionContext : IDisposable
{
    private static readonly PrintHandler NoOpPrintSink = static _ => { };

    private int _disposeCount;
    private readonly bool _ownsStore;
    private readonly bool _ownsAccountant;
    private readonly bool _ownsVideoRegistry;
    private readonly bool _ownsVariableStore;

    /// <summary>
    /// Creates a new execution context against <paramref name="catalog"/>.
    /// Internal because construction should normally go through
    /// <see cref="TableCatalog.CreateExecutionContext"/> — the context is a
    /// child of the catalog and the factory keeps that relationship explicit.
    /// The catalog supplies the function registry and pool, so they're not
    /// separate parameters.
    /// </summary>
    /// <param name="catalog">Registry of named tables and provider factories.</param>
    /// <param name="memoryBudgetBytes">Memory budget in bytes for operators that
    /// support spill-to-disk. Pass <see cref="long.MaxValue"/> for effectively-unbounded
    /// memory. Ignored when <paramref name="accountant"/> is non-null — the supplied
    /// accountant carries its own budget.</param>
    /// <param name="store">Optional value store for reference-type payloads.
    /// Defaults to a new <see cref="Model.Arena"/> if not provided.</param>
    /// <param name="types">Optional pre-existing type registry to share across
    /// multiple query contexts (e.g. across queries within a single procedural batch).</param>
    /// <param name="accountant">Optional existing <see cref="MemoryAccountant"/>
    /// to share with the surrounding scope (typically a <see cref="ExecutionContext"/>'s
    /// accountant). When <see langword="null"/>, the context constructs and owns its own.</param>
    /// <param name="videoRegistry">Optional pre-existing <see cref="Model.VideoRegistry"/>
    /// to share with a surrounding scope.</param>
    /// <param name="variableScope">Optional procedural variable scope, threaded
    /// from a surrounding <see cref="ExecutionContext"/>.</param>
    /// <param name="variableStore">Optional procedure-lifetime variable arena,
    /// paired with <paramref name="variableScope"/>.</param>
    /// <param name="printSink">
    /// Optional <c>PRINT</c> handler. Defaults to a no-op delegate that ignores all input; assign a non-null delegate to capture output.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for cooperative cancellation.</param>
    internal ExecutionContext(
        TableCatalog catalog,
        long? memoryBudgetBytes = null,
        Arena? store = null,
        TypeRegistry? types = null,
        MemoryAccountant? accountant = null,
        VideoRegistry? videoRegistry = null,
        VariableScope? variableScope = null,
        Arena? variableStore = null,
        PrintHandler? printSink = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        CancellationToken = cancellationToken;
        FunctionRegistry = catalog.Functions;
        Catalog = catalog;
        Pool = catalog.Pool;
        Types = types ?? new TypeRegistry();
        PrintSink = printSink ?? NoOpPrintSink;

        if (store is null)
        {
            // We own this arena's lifetime — give it a baseline reference so
            // mid-query batch returns can't drop refcount to 0 and pool it
            // (which would trip "Arena is already pooled" on the next batch
            // rent that adds it back). Caller-supplied stores are assumed to
            // already carry a baseline owned by the caller (e.g. SelectPlan
            // adds the baseline for its `_hoistStore`).
            Store = new Arena();
            Store.AddReference();
            _ownsStore = true;
        }
        else
        {
            Store = store;
            _ownsStore = false;
        }

        if (accountant is null)
        {
            Accountant = new MemoryAccountant(
                memoryBudgetBytes: memoryBudgetBytes,
                arenaBytesProbe: () => Store.BytesWritten);
            _ownsAccountant = true;
        }
        else
        {
            Accountant = accountant;
            _ownsAccountant = false;
        }
        
        if (videoRegistry is null)
        {
            VideoRegistry = new VideoRegistry();
            _ownsVideoRegistry = true;
        }
        else
        {
            VideoRegistry = videoRegistry;
            _ownsVideoRegistry = false;
        }

        // Every context carries a procedure-lifetime arena + variable
        // scope chain. Single-statement queries simply never declare into
        // them; multi-statement procedural batches do. Caller-supplied
        // values are borrowed (e.g. a Derive that shares the parent's
        // scope); when neither is supplied we allocate a fresh pair and
        // own their lifetime, disposing the arena on Dispose. The owned
        // arena is not pool-tracked (Arena pooling fires through
        // PoolBacking.RentArena registration, which this allocation
        // bypasses), so straight Dispose is the right release path —
        // refcount semantics don't apply.
        if (variableStore is null)
        {
            VariableStore = new Arena();
            _ownsVariableStore = true;
        }
        else
        {
            VariableStore = variableStore;
            _ownsVariableStore = false;
        }
        VariableScope = variableScope ?? new VariableScope(Accountant);
    }

    /// <summary>
    /// Gets or sets whether <see cref="Dispose"/> has been called on this context.
    /// </summary>
    public bool Disposed { get; private set;}

    /// <summary>
    /// Per-query registry of source videos backing <see cref="DataKind.VideoFrame"/>
    /// handles. Holds the warm FFmpeg decoder state for each registered video; the
    /// dictionary is empty (and consumes no FFmpeg state) for queries that touch no
    /// video columns. Lifetime: owned by this context when constructed without a
    /// registry argument; borrowed from a <see cref="ExecutionContext"/> when a procedural
    /// batch is in scope. See <see cref="Model.VideoRegistry"/> for the materialisation
    /// model.
    /// </summary>
    public VideoRegistry VideoRegistry { get; }


    /// <summary>
    /// Per-query type registry for self-describing struct/array <see cref="DataValue"/>s.
    /// Construction sites intern their output shape here and stamp the resulting 16-bit
    /// type-id on emitted values. Consumers look up the descriptor to read field names
    /// without threading <see cref="Model.ColumnInfo"/> separately.
    /// Shared across child contexts so type-ids are consistent across the operator tree.
    /// </summary>
    public TypeRegistry Types { get; }

    /// <summary>
    /// Per-query translator table from a file's on-disk struct type-ids to the
    /// ids in this query's <see cref="Types"/>. Populated by the source
    /// operator at file open from the footer's type table; consulted by the
    /// sidecar slot-decoding paths so <c>Array&lt;Struct&gt;</c> elements
    /// reading from a given <c>storeId</c> resolve to the correct runtime
    /// shape. Empty by default — files with no struct types and pre-v5
    /// files don't need translation.
    /// </summary>
    public TypeIdTranslationTable TypeIdTranslations { get; } = new();

    /// <summary>Cancellation token for cooperative cancellation.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Registry of scalar and table-valued functions.</summary>
    public FunctionRegistry FunctionRegistry { get; }

    /// <summary>Registry of named tables and provider factories.</summary>
    public TableCatalog Catalog { get; }

    /// <summary>
    /// The single per-query arena for all non-inline, non-sidecar
    /// <see cref="DataValue"/> payloads. Strings, byte arrays, vectors, JSON values,
    /// hoisted literals, model outputs, intermediate computed values — they all
    /// resolve through this arena. Lives for the duration of the query.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>One arena per query.</strong> Earlier designs had per-batch arenas
    /// (rented from a pool) plus a separate plan-scoped hoist store. That model
    /// produced a "which arena does this DataValue reference?" ambiguity that
    /// every consumer had to resolve correctly — and got wrong every few weeks.
    /// Collapsing to one arena per query removes the ambiguity: <c>IsInArena</c>
    /// unambiguously means "in this arena", consumers pass a single store, no
    /// routing decisions.
    /// </para>
    /// <para>
    /// <strong>Memory bounding.</strong> The arena grows for the query's life;
    /// when the query ends the arena is disposed and bytes go away. For long
    /// queries with large intermediate state, the arena is file-backed (mmap
    /// to a spill file) so growth is bounded by disk, not RAM. A future cap
    /// will reject inserts that would exceed a configurable budget.
    /// </para>
    /// <para>
    /// <strong>Sidecar is separate.</strong> Sidecars (durable, content-addressable
    /// via <see cref="SidecarRegistry"/>) remain their own tier. DataValues with
    /// <c>IsInSidecar</c> resolve through the registry; everything else goes
    /// through this arena.
    /// </para>
    /// </remarks>
    public Arena Store { get; }

    /// <summary>
    /// Rents a <see cref="RowBatch"/> bound to <see cref="Store"/> as its arena
    /// and sized to <see cref="BatchSize"/> — the canonical operator-output
    /// rental in the one-arena-per-query model. Operators that don't have a
    /// row-count reason to rent a smaller batch should use this overload.
    /// </summary>
    public RowBatch RentRowBatch(ColumnLookup columnLookup)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        
        RowBatch batch = Pool.RentRowBatch(columnLookup, BatchSize, Store);
        batch.Types = Types;
        batch.TypeIdTranslations = TypeIdTranslations;
        return batch;
    }

    /// <summary>
    /// Rents a <see cref="RowBatch"/> with an explicit capacity. Use only when
    /// the batch is intentionally smaller than <see cref="BatchSize"/> — e.g.
    /// <see cref="Operators.SingleEmptyRowOperator"/> needs a 1-row batch, and
    /// <see cref="Operators.ModelInvocationOperator"/> sizes to the
    /// <see cref="RowLimit"/>-capped row count when LIMIT propagates a
    /// smaller window.
    /// </summary>
    public RowBatch RentRowBatch(ColumnLookup columnLookup, int capacity)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        RowBatch batch = Pool.RentRowBatch(columnLookup, capacity, Store);
        batch.Types = Types;
        batch.TypeIdTranslations = TypeIdTranslations;
        return batch;
    }

    /// <summary>
    /// Returns a <see cref="RowBatch"/> rented via this context. Symmetric
    /// counterpart of <see cref="RentRowBatch(ColumnLookup)"/>. Equivalent to
    /// <c>Pool.ReturnRowBatch(batch)</c>; spelled on the context so operator
    /// call sites read as a rent/return pair against the same handle.
    /// </summary>
    public void ReturnRowBatch(RowBatch batch)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        Pool.ReturnRowBatch(batch);
    }

    /// <summary>
    /// Plan-wide memory accountant shared with every materializing operator,
    /// <see cref="VariableScope"/>, and DML executor in this query. Forwards the
    /// budget check (<see cref="MemoryAccountant.WouldExceedBudget"/>) and
    /// residency notifications. Lifetime: owned by this context when constructed
    /// without an accountant argument; borrowed from a <see cref="ExecutionContext"/>
    /// when a procedural batch is in scope. See <see cref="MemoryAccountant"/>
    /// for the full residency / arena-bytes / sampling model.
    /// </summary>
    public MemoryAccountant Accountant { get; }

    /// <summary>
    /// Optional memory budget in bytes for operators that support spill-to-disk.
    /// Forwards to <see cref="MemoryAccountant.MemoryBudgetBytes"/> on
    /// <see cref="Accountant"/>; provided as a shortcut for read sites that
    /// only need the budget value.
    /// </summary>
    public long? MemoryBudgetBytes => Accountant.MemoryBudgetBytes;

    /// <summary>
    /// Directory where spill operators write their temp files (data.spill, data.arena).
    /// Defaults to <see cref="Path.GetTempPath"/>. Override at startup to dedicate a
    /// physical drive — e.g. on a multi-spindle host where the datum files live on one
    /// drive (read-heavy random access) and spill writes belong on another (write-heavy
    /// transient, file-backed Arena pages reclaimed by the OS under memory pressure).
    /// Each spill operation creates a GUID-prefixed subdirectory under this path; a
    /// startup sweep (<c>Directory.Delete(subdir, recursive: true)</c> for any
    /// <c>datum-spill-*</c> entry) cleans up orphans from a prior crashed process.
    /// </summary>
    public string SpillDirectory { get; init; } = Path.GetTempPath();

    /// <summary>
    /// Maximum recursion depth for recursive CTEs. Limits how many iterations
    /// the recursive member executes before the operator raises an error.
    /// Defaults to <c>1000</c>.
    /// </summary>
    public int MaxRecursionDepth { get; init; } = 1000;

    /// <summary>
    /// The outer row from a correlated scalar subquery, or <see langword="null"/> when
    /// not inside a correlated subquery. Used by <see cref="ExpressionEvaluator"/> to
    /// resolve column references to outer-scope tables.
    /// </summary>
    public Row? OuterRow { get; init; }

    /// <summary>
    /// Maximum number of output rows that a downstream <see cref="Operators.LimitOperator"/>
    /// will consume (LIMIT + OFFSET). Operators such as join can use this hint to choose
    /// cheaper strategies (e.g. index nested-loop) when only a small number of rows are needed.
    /// <see langword="null"/> when no LIMIT clause is active.
    /// </summary>
    public int? RowLimit { get; init; }

    /// <summary>
    /// Number of parallel workers an operator may spawn for CPU-bound work (e.g.
    /// parallel hash join probe, parallel hash aggregate). Defaults to <c>1</c>
    /// (single-threaded). Set to <see cref="Environment.ProcessorCount"/> or higher
    /// to enable intra-query parallelism.
    /// </summary>
    public int DegreeOfParallelism { get; init; } = 1;

    /// <summary>
    /// Maximum number of rows per <see cref="Model.RowBatch"/>. Operators fill
    /// batches up to this size before yielding. Defaults to
    /// <see cref="DatumFormatV2.DefaultPageSize"/> so in-memory batches align
    /// with on-disk page boundaries — one page = one batch = one operator
    /// working unit. Tests may override via the <c>init</c> setter for
    /// multi-batch stress, but production code should always use the default.
    /// </summary>
    public int BatchSize { get; init; } = DatumFormatV2.DefaultPageSize;

    /// <summary>
    /// Pool for reusing buffers during query execution.
    /// </summary>
    public Pool Pool { get; }

    /// <summary>
    /// Builds an <see cref="ExpressionEvaluator"/> parented to this context.
    /// The evaluator reads its function registry, sidecar registry, type
    /// registry, accountant, video registry, and (optional) variable scope /
    /// store off this context; <paramref name="sourceSchema"/> and
    /// <paramref name="letBindingExpressions"/> are operator-specific extras
    /// that aren't on the context.
    /// </summary>
    public ExpressionEvaluator CreateEvaluator(
        Schema? sourceSchema = null,
        IReadOnlyDictionary<string, Expression>? letBindingExpressions = null)
        => new(this, sourceSchema, letBindingExpressions);

    /// <summary>
    /// Builds an <see cref="EvaluationFrame"/> against this context's ambient
    /// state without wiring a lambda dispatcher. Use this for call sites that
    /// only need a frame for value stabilisation or accessor-style reads
    /// (e.g. <see cref="ExpressionEvaluator.ToValueRef(Heliosoph.DatumV.Model.DataValue, EvaluationFrame)"/>);
    /// callers that need lambda dispatch should use
    /// <c>ExpressionEvaluator.CreateFrame</c> instead so the
    /// <see cref="EvaluationFrame.LambdaInvoker"/> slot is populated.
    /// </summary>
    /// <param name="row">The row to evaluate against.</param>
    /// <param name="store">Optional source arena for the frame. Defaults to
    /// <see cref="Store"/>.</param>
    public EvaluationFrame CreateFrame(Row row, IValueStore? store = null)
        => new(row, store ?? Store, this, outerRow: OuterRow);

    /// <summary>
    /// Builds an <see cref="EvaluationFrame"/> with distinct source / target
    /// arenas. Use when reads come from one store and materialised results
    /// must land in another (e.g. <see cref="ExecutionContext.Declare"/> reading
    /// from a producing query's arena and writing into the variable store).
    /// No <see cref="EvaluationFrame.LambdaInvoker"/> is wired; build via
    /// <c>ExpressionEvaluator.CreateFrame</c> if you need lambda
    /// dispatch.
    /// </summary>
    public EvaluationFrame CreateFrame(Row row, IValueStore source, IValueStore target)
        => new(row, source, target, this, outerRow: OuterRow);

    /// <summary>
    /// Lifts <paramref name="value"/> from <paramref name="sourceStore"/>
    /// into a managed-payload <see cref="ValueRef"/> and binds it to
    /// <paramref name="name"/> in the topmost frame of
    /// <see cref="VariableScope"/>. Throws if the name is already declared
    /// in the topmost frame. When <paramref name="structFieldNames"/> is
    /// non-<see langword="null"/>, the names attach to this binding so
    /// downstream <c>@var['field']</c> access can resolve a position.
    /// </summary>
    public void Declare(
        string name,
        DataValue value,
        IValueStore sourceStore,
        IReadOnlyList<string>? structFieldNames = null)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        EvaluationFrame frame = CreateFrame(Row.Empty, sourceStore, VariableStore);
        ValueRef bound = ExpressionEvaluator.ToValueRef(value, frame);
        VariableScope.Declare(name, bound, structFieldNames);
    }

    /// <summary>
    /// Lifts <paramref name="value"/> from <paramref name="sourceStore"/>
    /// into a managed-payload <see cref="ValueRef"/> and updates the
    /// existing binding. Walks the scope chain outward to find the frame
    /// holding the name. Throws if the name is unbound.
    /// </summary>
    public void Set(string name, DataValue value, IValueStore sourceStore)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        EvaluationFrame frame = CreateFrame(Row.Empty, sourceStore, VariableStore);
        ValueRef bound = ExpressionEvaluator.ToValueRef(value, frame);
        VariableScope.Set(name, bound);
    }

    /// <summary>
    /// Returns a child context derived from this one. Every parameter defaults to
    /// "inherit from parent"; explicitly supplied parameters override that slot in
    /// the child. The child <strong>borrows</strong> every inherited resource —
    /// disposing the child does not release the parent's <see cref="Store"/>,
    /// <see cref="Accountant"/>, or <see cref="VideoRegistry"/>. Use this when
    /// entering a nested scope (correlated subquery, procedural body, lateral join)
    /// that needs to override one or two slots while keeping everything else
    /// pointing at the parent's ambient state.
    /// </summary>
    /// <param name="store">Override the value store. Pass when the child scope
    /// has its own arena (e.g. a procedural body's private variableStore).
    /// Inherits parent's <see cref="Store"/> if omitted.</param>
    /// <param name="variableScope">Override the procedural variable scope.
    /// Pass when entering a procedural body or BEGIN/END block. Inherits if omitted.</param>
    /// <param name="variableStore">Override the procedural variable arena.
    /// Must be passed together with <paramref name="variableScope"/>.</param>
    /// <param name="outerRow">Override the outer row for correlated subqueries.
    /// Inherits parent's <see cref="OuterRow"/> if omitted.</param>
    /// <param name="types">Override the type registry. Inherits if omitted.</param>
    /// <param name="accountant">Override the memory accountant. Inherits if omitted.</param>
    /// <param name="videoRegistry">Override the video registry. Inherits if omitted.</param>
    public ExecutionContext Derive(
        Arena? store = null,
        VariableScope? variableScope = null,
        Arena? variableStore = null,
        Row? outerRow = null,
        TypeRegistry? types = null,
        MemoryAccountant? accountant = null,
        VideoRegistry? videoRegistry = null)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        // Pass non-null values for every owned resource (Store, Accountant,
        // VideoRegistry, VariableStore, VariableScope) through the
        // constructor so the new context's _ownsX flags all land on false —
        // the child borrows, and disposing the child is a no-op for the
        // inherited resources. VariableStore/VariableScope MUST flow
        // through the constructor (not an object initializer) because the
        // constructor's null-default allocates a fresh owned arena + scope,
        // and the initializer would only overwrite the property while
        // leaving _ownsVariableStore = true — a setup that would have the
        // child release the parent's arena on Dispose.
        return new ExecutionContext(
            Catalog,
            store: store ?? Store,
            types: types ?? Types,
            accountant: accountant ?? Accountant,
            videoRegistry: videoRegistry ?? VideoRegistry,
            variableScope: variableScope ?? VariableScope,
            variableStore: variableStore ?? VariableStore,
            cancellationToken: CancellationToken,
            printSink: PrintSink)
        {
            OuterRow = outerRow ?? OuterRow,
            RowLimit = RowLimit,
            MaxRecursionDepth = MaxRecursionDepth,
            DegreeOfParallelism = DegreeOfParallelism,
            BatchSize = BatchSize,
            AssertionDiagnostics = AssertionDiagnostics,
            MaxStratifyClasses = MaxStratifyClasses,
            ModelTracer = ModelTracer,
            SpillDirectory = SpillDirectory,
            ProcedureCallDepth = ProcedureCallDepth
        };
    }

    /// <summary>
    /// Returns a child context that borrows every parent slot but replaces
    /// <see cref="RowLimit"/> with <paramref name="rowLimit"/>. Used by blocking /
    /// fan-out operators (<c>DISTINCT</c>, <c>GROUP BY</c>) to strip an inherited
    /// row limit so child operators don't pick limit-shape strategies, and by
    /// <c>LIMIT</c> to tighten the propagated hint. Separate from
    /// <see cref="Derive"/> because <see langword="null"/> is a meaningful
    /// override here (it clears the limit) and would collide with Derive's
    /// "null = inherit" convention.
    /// </summary>
    public ExecutionContext WithRowLimit(int? rowLimit)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        return new ExecutionContext(
            Catalog,
            store: Store,
            types: Types,
            accountant: Accountant,
            videoRegistry: VideoRegistry,
            variableScope: VariableScope,
            variableStore: VariableStore,
            cancellationToken: CancellationToken,
            printSink: PrintSink)
        {
            OuterRow = OuterRow,
            RowLimit = rowLimit,
            MaxRecursionDepth = MaxRecursionDepth,
            DegreeOfParallelism = DegreeOfParallelism,
            BatchSize = BatchSize,
            AssertionDiagnostics = AssertionDiagnostics,
            MaxStratifyClasses = MaxStratifyClasses,
            ModelTracer = ModelTracer,
            SpillDirectory = SpillDirectory,
            ProcedureCallDepth = ProcedureCallDepth
        };
    }

    /// <summary>
    /// Accumulates skip/warn counts and sample messages for <c>ASSERT</c> clauses evaluated
    /// during this query. <see langword="null"/> when no ASSERT clauses are present or when
    /// diagnostics collection has not been requested.
    /// </summary>
    public AssertionDiagnostics? AssertionDiagnostics { get; init; }

    /// <summary>
    /// Maximum number of distinct classes allowed in a TABLESAMPLE BALANCED
    /// stratification column. Limits the number of per-class reservoirs to
    /// bound memory usage. <see langword="null"/> means use the operator's
    /// internal default (10,000).
    /// </summary>
    public int? MaxStratifyClasses { get; init; }

    /// <summary>
    /// Optional per-query tracer for <c>models.X(...)</c> invocations. The
    /// interactive shell wires this up via <c>.trace on</c> to print a
    /// per-dispatch log. <see langword="null"/> when tracing isn't enabled —
    /// <see cref="Operators.ModelInvocationOperator"/> short-circuits the
    /// hook in that case.
    /// </summary>
    public IModelInvocationTracer? ModelTracer { get; init; }

    /// <summary>
    /// Borrowed reference to the enclosing batch's variable-payload arena.
    /// Procedure-lifetime arena holding bound variable payloads.
    /// Distinct from the per-query <see cref="Store"/>: variable bindings
    /// must survive across child queries inside a procedural batch
    /// (whose <see cref="Store"/> rent/return cycle would otherwise free
    /// their payloads), so they live here instead. Always non-null —
    /// single-statement queries simply never bind into it.
    /// </summary>
    /// <remarks>
    /// Ownership: when the constructor is not handed a borrow it allocates
    /// a fresh arena (not registered with the pool, so straight Dispose
    /// is the right release path) and disposes it on Dispose. Borrowed
    /// handles (e.g. a Derive that shares the parent's substrate) are
    /// not touched at Dispose.
    /// </remarks>
    public Arena VariableStore { get; }

    /// <summary>
    /// Procedural variable scope chain. Walked by the evaluator (innermost
    /// frame first) when resolving an unqualified column reference as a
    /// variable. Always non-null and starts with one root frame already
    /// pushed — additional frames are pushed/popped by <c>BEGIN</c> /
    /// <c>END</c>, lambda invocation, and procedural-loop scopes.
    /// </summary>
    public VariableScope VariableScope { get; }

    /// <summary>
    /// Number of procedure-call frames above this context. The top-level
    /// batch is depth 0; the body of a <c>CALL proc.X(...)</c> runs at
    /// depth 1; any <c>CALL proc.Y(...)</c> inside that body runs at
    /// depth 2; and so on. The procedure executor rejects new calls once
    /// the depth would exceed <see cref="BatchExecutor.MaxProcedureCallDepth"/>
    /// so a self- or mutually recursive procedure fails fast with a clear
    /// message instead of running until the call stack overflows. Defaults
    /// to <c>0</c> for top-level queries and any context not opened by a
    /// procedural call.
    /// </summary>
    public int ProcedureCallDepth { get; init; }

    /// <summary>
    /// Sink for <c>PRINT</c> diagnostic output. Always non-null —
    /// omitting (or passing <see langword="null"/> for) the
    /// <c>printSink</c> constructor parameter installs a no-op handler
    /// that silently drops every PRINT. Callers invoke directly
    /// (<c>context.PrintSink(text)</c>) with no null check. Fixed at
    /// construction; wire the handler at the call site that builds the
    /// context rather than mutating it afterwards.
    /// </summary>
    public PrintHandler PrintSink { get; }

    /// <summary>
    /// Sidecar registry borrowed from the active <see cref="Catalog"/>. Each
    /// <see cref="DatumFile.Sidecar.IBlobSource"/> in the catalog has a unique
    /// <c>storeId</c> byte stamped onto its DataValues at decode time; image
    /// accessors resolve through this registry to find the right source. Catalog-
    /// scoped (not per-query) so storeId assignments are stable across queries.
    /// </summary>
    public SidecarRegistry SidecarRegistry => Catalog.SidecarRegistry;

    /// <summary>
    /// Server-wide model catalog. Holds <see cref="Heliosoph.DatumV.Models.ModelCatalogEntry"/>
    /// records for every registered model and the <see cref="Heliosoph.DatumV.Models.IModel"/>
    /// instances they have been resolved into. Lives on <see cref="ExecutionContext"/>
    /// for plumbing convenience but is itself process-scoped — model residency is
    /// amortised across queries, sessions, and tenants.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="TableCatalog.Models"/> on the active catalog so callers
    /// that set models at the catalog level don't have to thread the reference through
    /// every <see cref="ExecutionContext"/> construction. Tests that need to override
    /// (e.g. inject a mock catalog) can set it explicitly via init.
    /// </remarks>
    public Heliosoph.DatumV.Models.ModelCatalog? Models
    {
        get => _modelsOverride ?? Catalog.Models;
        init => _modelsOverride = value;
    }
    private readonly Heliosoph.DatumV.Models.ModelCatalog? _modelsOverride;

    /// <summary>
    /// Releases context-owned resources. Idempotent. Disposes the accountant
    /// when this context constructed its own; releases the per-query
    /// <see cref="Store"/> baseline when this context allocated it. Borrowed
    /// resources (parent's accountant, caller-supplied store) are left
    /// untouched.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeCount, 1) != 0) return;
        // Accountant first: it samples VariableStore.BytesWritten on a
        // timer, so stopping it before releasing the arena's baseline
        // avoids a probe racing a refcount drop.
        if (_ownsAccountant) Accountant.Dispose();
        if (_ownsStore) Store.ReleaseReference();
        if (_ownsVideoRegistry) VideoRegistry.Dispose();
        if (_ownsVariableStore) VariableStore.Dispose();

        Disposed = true;
    }
}
