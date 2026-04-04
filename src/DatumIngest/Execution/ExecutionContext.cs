using DatumIngest.Catalog;
using DatumIngest.DatumFile.Sidecar;
using DatumIngest.DatumFile.V2;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Execution;

/// <summary>
/// Shared context passed through the operator tree during query execution.
/// Carries the cancellation token, function registry, table catalog, and
/// optional query meter for cost tracking.
/// </summary>
public sealed class ExecutionContext : IDisposable
{
    private readonly bool _ownsStore;
    private readonly bool _ownsAccountant;
    private readonly bool _ownsVideoRegistry;
    private int _disposed;

    /// <summary>
    /// Creates a new execution context from an existing context, copying all properties. Useful for creating a child
    /// </summary>
    public ExecutionContext(ExecutionContext context)
    {
        CancellationToken = context.CancellationToken;
        FunctionRegistry = context.FunctionRegistry;
        Catalog = context.Catalog;
        Pool = context.Pool;
        Store = context.Store;
        QueryMeter = context.QueryMeter;
        Accountant = context.Accountant;
        BatchSize = context.BatchSize;
        AssertionDiagnostics = context.AssertionDiagnostics;
        MaxStratifyClasses = context.MaxStratifyClasses;
        Types = context.Types;
        VideoRegistry = context.VideoRegistry;
        // Copy contexts borrow the Store baseline, the accountant, and the video
        // registry from the parent — disposing a child must not tear down any of
        // the parent's resources. The video registry holds warm FFmpeg decoder
        // state that must outlive any single child query.
        _ownsStore = false;
        _ownsAccountant = false;
        _ownsVideoRegistry = false;
    }


  /// <summary>
  /// Creates a new execution context.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token for cooperative cancellation.</param>
  /// <param name="functionRegistry">Registry of scalar and table-valued functions.</param>
  /// <param name="catalog">Registry of named tables and provider factories.</param>
  /// <param name="queryMeter">Optional meter for accumulating Query Unit costs, or <see langword="null"/> for unmetered execution.</param>
  /// <param name="memoryBudgetBytes">
  /// Memory budget in bytes for operators that support spill-to-disk. When
  /// <see langword="null"/>. Pass <see cref="long.MaxValue"/> for
  /// effectively-unbounded memory (skips spill but keeps the spill-capable
  /// code paths exercised). When set, operators spill to temporary files when
  /// estimated memory exceeds this budget. Ignored when
  /// <paramref name="accountant"/> is non-null — the supplied accountant carries
  /// its own budget. Supported operators: hash join, ORDER BY, GROUP BY, DISTINCT,
  /// PIVOT, UNION/INTERSECT/EXCEPT, and materialised CTEs.
  /// </param>
  /// <param name="pool">Pool for reusing buffers during query execution.</param>
  /// <param name="store">
  /// Optional value store for reference-type payloads. Defaults to a new <see cref="Model.Arena"/>
  /// if not provided.
  /// </param>
  /// <param name="types">
  /// Optional pre-existing type registry to share across multiple query contexts (e.g. across
  /// queries within a single procedural batch). When <see langword="null"/>, a fresh registry
  /// is allocated per context.
  /// </param>
  /// <param name="accountant">
  /// Optional existing <see cref="MemoryAccountant"/> to share with the surrounding scope
  /// (typically a <see cref="BatchContext"/>'s accountant). When <see langword="null"/>, the
  /// context constructs and owns its own; the owned accountant is disposed when this
  /// context is disposed.
  /// </param>
  /// <param name="videoRegistry">
  /// Optional pre-existing <see cref="Model.VideoRegistry"/> to share with a surrounding
  /// scope (e.g. a procedural <see cref="BatchContext"/> where registered videos must
  /// survive across statements). When <see langword="null"/>, the context constructs and
  /// owns its own; the owned registry is disposed when this context is disposed.
  /// </param>
  public ExecutionContext(
        CancellationToken cancellationToken,
        FunctionRegistry functionRegistry,
        TableCatalog catalog,
        Pool pool,
        QueryMeter? queryMeter = null,
        long? memoryBudgetBytes = null,
        Arena? store = null,
        TypeRegistry? types = null,
        MemoryAccountant? accountant = null,
        VideoRegistry? videoRegistry = null)
    {
        // Null no longer means "no budget" — it means "use the default" so every
        // execution path goes through the spill-capable operators. Callers who
        // want unbounded memory must pass long.MaxValue explicitly.
        CancellationToken = cancellationToken;
        FunctionRegistry = functionRegistry;
        Catalog = catalog;
        Pool = pool;
        if (store is null)
        {
            // We own this arena's lifetime — give it a baseline reference so
            // mid-query batch returns can't drop refcount to 0 and pool it
            // (which would trip "Arena is already pooled" on the next batch
            // rent that adds it back). Caller-supplied stores are assumed to
            // already carry a baseline owned by the caller (e.g. QueryPlan
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
        QueryMeter = queryMeter;
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
        // When a caller passes a TypeRegistry (typically a procedural BatchContext
        // sharing one across loop iterations), reuse it so type-ids stamped on
        // values in one query remain resolvable in downstream queries.
        Types = types ?? new TypeRegistry();
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
    }

    /// <summary>
    /// Per-query registry of source videos backing <see cref="DataKind.VideoFrame"/>
    /// handles. Holds the warm FFmpeg decoder state for each registered video; the
    /// dictionary is empty (and consumes no FFmpeg state) for queries that touch no
    /// video columns. Lifetime: owned by this context when constructed without a
    /// registry argument; borrowed from a <see cref="BatchContext"/> when a procedural
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
    /// every consumer had to resolve correctly — and got wrong every few weeks
    /// (Demo 1.5 hoist bug, BytesWritten heuristic, ConcatFunction recurrence).
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
    public void ReturnRowBatch(RowBatch batch) => Pool.ReturnRowBatch(batch);

    /// <summary>
    /// Optional meter for accumulating Query Unit costs during execution.
    /// <see langword="null"/> when metering is not active (e.g. CLI execution).
    /// </summary>
    public QueryMeter? QueryMeter { get; }

    /// <summary>
    /// Plan-wide memory accountant shared with every materializing operator,
    /// <see cref="VariableScope"/>, and DML executor in this query. Forwards the
    /// budget check (<see cref="MemoryAccountant.WouldExceedBudget"/>) and
    /// residency notifications. Lifetime: owned by this context when constructed
    /// without an accountant argument; borrowed from a <see cref="BatchContext"/>
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
    /// Optional global concurrency budget that bounds the total number of parallel
    /// operator workers across all concurrent queries. When <see langword="null"/>,
    /// operators may spawn up to <see cref="DegreeOfParallelism"/> workers without
    /// limit — appropriate for single-query CLI usage. On the server, a shared
    /// <see cref="ParallelismBudget"/> prevents thread pool oversubscription.
    /// </summary>
    public ParallelismBudget? ParallelismBudget { get; init; }

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
    /// Returns a new context with the given outer row set for correlated subquery execution.
    /// All other properties are copied from the current context.
    /// </summary>
    /// <param name="outerRow">The outer row providing correlated column values.</param>
    /// <returns>A new <see cref="ExecutionContext"/> with <see cref="OuterRow"/> set.</returns>
    public ExecutionContext WithOuterRow(Row outerRow)
    {
        return new ExecutionContext(
            CancellationToken,
            FunctionRegistry,
            Catalog,
            Pool,
            QueryMeter,
            memoryBudgetBytes: null,
            Store,
            types: Types,
            accountant: Accountant,
            videoRegistry: VideoRegistry)
        {
            OuterRow = outerRow,
            RowLimit = RowLimit,
            MaxRecursionDepth = MaxRecursionDepth,
            DegreeOfParallelism = DegreeOfParallelism,
            ParallelismBudget = ParallelismBudget,
            BatchSize = BatchSize,
            AssertionDiagnostics = AssertionDiagnostics,
            MaxStratifyClasses = MaxStratifyClasses,
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
    /// When this query is running inside a procedural batch, the procedural
    /// executor sets this from <see cref="BatchContext.VariableStore"/> so
    /// the evaluator can resolve variable reads against
    /// the procedure-lifetime arena rather than the per-query
    /// <see cref="Store"/>. <see langword="null"/> for top-level queries
    /// outside any procedural batch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Borrowed, not owned.</strong> The <see cref="BatchContext"/>
    /// holds the baseline reference; this property is a read-only handle.
    /// The per-query <see cref="Store"/> rent/return cycle never touches
    /// its refcount, so it survives every child query's lifetime.
    /// </para>
    /// </remarks>
    public Arena? VariableStore { get; init; }

    /// <summary>
    /// Borrowed reference to the enclosing batch's variable scope chain.
    /// Walked by the evaluator (innermost frame first) when resolving
    /// variable reads. <see langword="null"/> for
    /// top-level queries outside any procedural batch.
    /// </summary>
    public VariableScope? VariableScope { get; init; }

    /// <summary>
    /// Sidecar registry borrowed from the active <see cref="Catalog"/>. Each
    /// <see cref="DatumFile.Sidecar.IBlobSource"/> in the catalog has a unique
    /// <c>storeId</c> byte stamped onto its DataValues at decode time; image
    /// accessors resolve through this registry to find the right source. Catalog-
    /// scoped (not per-query) so storeId assignments are stable across queries.
    /// </summary>
    public SidecarRegistry SidecarRegistry => Catalog.SidecarRegistry;

    /// <summary>
    /// Server-wide model catalog. Holds <see cref="DatumIngest.Models.ModelCatalogEntry"/>
    /// records for every registered model and the <see cref="DatumIngest.Models.IModel"/>
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
    public DatumIngest.Models.ModelCatalog? Models
    {
        get => _modelsOverride ?? Catalog.Models;
        init => _modelsOverride = value;
    }
    private readonly DatumIngest.Models.ModelCatalog? _modelsOverride;

    /// <summary>
    /// Releases context-owned resources. Idempotent. Disposes the accountant
    /// when this context constructed its own; releases the per-query
    /// <see cref="Store"/> baseline when this context allocated it. Borrowed
    /// resources (parent's accountant, caller-supplied store) are left
    /// untouched.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_ownsAccountant) Accountant.Dispose();
        if (_ownsStore) Store.ReleaseReference();
        if (_ownsVideoRegistry) VideoRegistry.Dispose();
    }
}
