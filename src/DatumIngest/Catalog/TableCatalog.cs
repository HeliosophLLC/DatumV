using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using DatumIngest.Catalog.Providers;
using DatumIngest.DatumFile.Sidecar;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Indexing;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;
using DatumIngest.Serialization;

namespace DatumIngest.Catalog;

/// <summary>
/// Registry of named tables and their associated providers.
/// Resolves table names referenced in SQL FROM clauses to
/// <see cref="TableDescriptor"/> instances and creates the
/// appropriate <see cref="ITableProvider"/> for each.
/// </summary>
public sealed class TableCatalog : IDisposable, IEnumerable<ITableProvider>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TableCatalog"/> class with an optional parent catalog and a resource pool.
     /// If a parent catalog is provided, this catalog will fall through to the parent for any table names that are not found locally.
     /// The resource pool is used for managing provider resources such as buffers and file handles.
    /// </summary>
    /// <param name="parent">The optional parent catalog to fall through to for unresolved table names.</param>
    public TableCatalog(TableCatalog parent) : this(parent.Pool)
    {
        this.Parent = parent;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TableCatalog"/> class with the given resource pool.
    /// </summary>
    /// <param name="pool">The resource pool to use for table providers.</param>
    public TableCatalog(Pool pool) : this(pool, catalogPath: null) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="TableCatalog"/> class with
    /// the given resource pool and an optional catalog file path. When
    /// <paramref name="catalogPath"/> is non-null:
    /// <list type="bullet">
    ///   <item><description>Existing UDFs are loaded from the file at construction (if it exists).</description></item>
    ///   <item><description>Subsequent <c>CREATE FUNCTION</c> / <c>DROP FUNCTION</c> writes through to the same file atomically.</description></item>
    /// </list>
    /// When the path is <see langword="null"/>, the registry is in-memory only.
    /// </summary>
    /// <param name="pool">The resource pool to use for table providers.</param>
    /// <param name="catalogPath">
    /// Optional absolute path for the catalog's JSON file. Conventional name is
    /// <see cref="CatalogStore.DefaultFileName"/>. The directory is created on
    /// first save if missing.
    /// </param>
    public TableCatalog(Pool pool, string? catalogPath)
    {
        this.Pool = pool;
        this._backing = pool.Backing;
        this._functions = FunctionRegistry.CreateDefault();
        this._udfs = new UdfRegistry();
        this._procedures = new ProcedureRegistry();
        this.Tables = new();
        this._catalogStore = catalogPath is null ? null : new CatalogStore(catalogPath);
        this._routines = new RoutineRegistrar(_udfs, _procedures, _functions, _catalogStore);

        // Auto-register intrinsic system tables. information_schema providers
        // take `this` because they enumerate the catalog at scan time;
        // construction is safe because no scan occurs during initialization.
        Add(new Providers.UdfsTableProvider(pool, _udfs));
        Add(new Providers.ProceduresTableProvider(pool, _procedures));
        Add(new Providers.InformationSchemaTablesProvider(pool, this));
        Add(new Providers.InformationSchemaColumnsProvider(pool, this));
        Add(new Providers.InformationSchemaSchemataProvider(pool));
        Add(new Providers.DatumCatalogFunctionsProvider(pool, _functions));
        Add(new Providers.DatumCatalogFunctionParametersProvider(pool, _functions));
        Add(new Providers.DatumCatalogStatisticsProvider(pool, this));
        Add(new Providers.DatumCatalogIndexesProvider(pool, this));
        Add(new Providers.DatumCatalogInteractionsProvider(pool, this));

        // Replay any persisted UDFs / procedures into the registries.
        // Done after the system table registrations so the rehydrated
        // entries are immediately visible to introspection.
        if (_catalogStore is not null)
        {
            CatalogStoreLoadReport report = _catalogStore.Load(_udfs, _procedures);
            CatalogLoadReport = report;

            // The Load() call writes straight into _udfs without going
            // through ApplyCreateFunction, so procedural adapters in the
            // scalar registry haven't been wired yet. Reconcile them here so
            // a freshly opened catalog can immediately invoke any persisted
            // procedural UDF.
            _routines.SyncProceduralAdaptersFromRegistry();
        }
    }


    private TableCatalog? Parent { get; }
    internal Pool Pool { get; }
    private readonly PoolBacking _backing;
    private readonly FunctionRegistry _functions;
    private readonly UdfRegistry _udfs;
    private readonly ProcedureRegistry _procedures;
    private readonly CatalogStore? _catalogStore;
    private readonly RoutineRegistrar _routines;
    private ConcurrentDictionary<string, ITableProvider> Tables { get; }
    private readonly SidecarRegistry _sidecarRegistry = new();

    /// <summary>
    /// Report from the catalog file's load on construction. <see langword="null"/>
    /// when no <c>catalogPath</c> was supplied. Hosts can surface
    /// <see cref="CatalogStoreLoadReport.Warnings"/> in their startup logs so a
    /// user notices a corrupt or skipped UDF instead of silently missing it.
    /// </summary>
    public CatalogStoreLoadReport? CatalogLoadReport { get; }

    /// <summary>
    /// The function registry used by <see cref="Plan(string)"/> for SQL planning.
    /// Defaults to <see cref="FunctionRegistry.CreateDefault"/> per catalog. Exposed
    /// for tooling that needs to enumerate registered functions (e.g. building a
    /// language-server manifest).
    /// </summary>
    public FunctionRegistry Functions => _functions;

    /// <summary>
    /// Registry of user-defined scalar functions (macros) registered against
    /// this catalog via <c>CREATE FUNCTION</c>. The planner consults this at
    /// plan time to inline every <c>udf.X(...)</c> call site. Falls through
    /// to the parent catalog when nested, so a session-level catalog inherits
    /// global UDFs registered on its root.
    /// </summary>
    public UdfRegistry Udfs => _udfs;

    /// <summary>
    /// Registry of named procedural blocks registered against this catalog
    /// via <c>CREATE PROCEDURE</c>. Consulted by the procedural batch
    /// executor on every <c>EXEC proc.X(...)</c> call site to find the
    /// descriptor whose body should run.
    /// </summary>
    public ProcedureRegistry Procedures => _procedures;

    /// <summary>
    /// Opens a new catalog containing a single <c>.datum</c> file. Owns its own pool
    /// and disposes everything when <see cref="Dispose"/> is called. The table name
    /// defaults to <see cref="PathDetector.DeriveTableName(string)"/> if not supplied.
    /// </summary>
    /// <param name="path">Path to the <c>.datum</c> file.</param>
    /// <param name="name">Optional override for the SQL table name.</param>
    public static TableCatalog FromFile(string path, string? name = null)
    {
        TableCatalog catalog = new(new Pool(new PoolBacking()));
        catalog.AddFile(path, name);
        return catalog;
    }

    /// <summary>
    /// Opens a new catalog populated with every <c>.datum</c> file in the given
    /// directory. Each file is registered using its derived table name. Owns its
    /// own pool. A <see cref="CatalogStore.DefaultFileName"/> file in the directory
    /// is loaded automatically if present; UDFs created during the session are
    /// written back to the same file.
    /// </summary>
    /// <param name="path">Path to a directory containing <c>.datum</c> files.</param>
    /// <param name="recursive">When <see langword="true"/>, recursively scans subdirectories.</param>
    public static TableCatalog FromDirectory(string path, bool recursive = false)
    {
        string catalogPath = Path.Combine(path, CatalogStore.DefaultFileName);
        TableCatalog catalog = new(new Pool(new PoolBacking()), catalogPath);
        SearchOption searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (string file in Directory.EnumerateFiles(path, "*.datum", searchOption))
        {
            catalog.AddFile(file);
        }
        return catalog;
    }

    /// <summary>
    /// Registers a <c>.datum</c> file as a queryable table. Returns this catalog
    /// for fluent chaining.
    /// </summary>
    /// <param name="path">Path to the <c>.datum</c> file.</param>
    /// <param name="name">Optional override for the SQL table name. Defaults to <see cref="PathDetector.DeriveTableName(string)"/>.</param>
    public TableCatalog AddFile(string path, string? name = null)
    {
        Add(new TableDescriptor(Name: name ?? PathDetector.DeriveTableName(path), FilePath: path));
        return this;
    }

    /// <summary>
    /// Parses and plans <paramref name="sql"/> against this catalog, returning an
    /// <see cref="IQueryPlan"/> that may be inspected (<see cref="IQueryPlan.ExplainTree"/>),
    /// analyzed (<see cref="IQueryPlan.AnalyzeAsync"/>), or executed
    /// (<see cref="IQueryPlan.ExecuteAsync(CancellationToken)"/>). Literal payloads are pre-materialized
    /// (hoisted) into a plan-scoped arena so per-row evaluation skips re-encoding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Accepts both query expressions (SELECT, compound queries) and DDL that
    /// the engine knows how to execute (currently <c>CREATE FUNCTION</c> /
    /// <c>DROP FUNCTION</c>). DDL is applied to the catalog as a side effect
    /// of <see cref="Plan(string)"/> and produces an empty result plan; this
    /// keeps the API surface a single entry point for the host.
    /// </para>
    /// <para>
    /// Before planning, every <c>udf.X(...)</c> call site in the parsed AST
    /// is inlined via <see cref="UdfInliner"/> so the planner sees only the
    /// substituted bodies. UDFs are macros — by plan time, no UDF call
    /// remains in the tree.
    /// </para>
    /// </remarks>
    public IQueryPlan Plan(string sql)
    {
        Statement statement = SqlParser.ParseStatement(sql);

        // CREATE PROCEDURE / procedural CREATE FUNCTION are dispatched here
        // (not in Plan(Statement)) so the original SQL text can be captured
        // verbatim for catalog persistence and introspection — preserving the
        // user's formatting and comments through round-trips. Macro UDFs and
        // every other statement type funnel through the standard AST-only path
        // (their body round-trips cleanly through QueryExplainer.FormatExpression
        // so the source text is unnecessary).
        switch (statement)
        {
            case CreateProcedureStatement create:
                _routines.ApplyCreateProcedure(create, sql);
                return EmptyQueryPlan.Instance;
            case CreateFunctionStatement createFn when createFn.StatementBody is not null:
                _routines.ApplyCreateFunction(createFn, sql);
                return EmptyQueryPlan.Instance;
        }

        return Plan(statement);
    }

    /// <summary>
    /// Plans an already-parsed <see cref="Statement"/> against this catalog.
    /// Same dispatch as <see cref="Plan(string)"/> minus the parsing step;
    /// useful for callers that have built a statement programmatically
    /// (e.g. the procedural batch executor synthesising
    /// <c>SELECT &lt;expr&gt;</c> for DECLARE / SET initialisers).
    /// </summary>
    /// <remarks>
    /// <see cref="CreateProcedureStatement"/> is rejected here because the
    /// original source text is required for catalog persistence and only
    /// reaches the catalog through <see cref="Plan(string)"/>. Programmatic
    /// callers that build a procedure AST should serialize it themselves
    /// and call <see cref="Plan(string)"/>.
    /// </remarks>
    public IQueryPlan Plan(Statement statement)
    {
        switch (statement)
        {
            case QueryStatement queryStatement:
                return PlanQuery(queryStatement.Query);

            case CreateFunctionStatement create:
                _routines.ApplyCreateFunction(create);
                return EmptyQueryPlan.Instance;

            case DropFunctionStatement drop:
                _routines.ApplyDropFunction(drop);
                return EmptyQueryPlan.Instance;

            case CreateProcedureStatement create:
                // Source text is null when coming from the AST-only path (e.g.
                // BatchExecutor). ApplyCreateProcedure falls back to a synthetic
                // description so the procedure still runs and persists; only the
                // display text in system_procedures.source_text is affected.
                _routines.ApplyCreateProcedure(create, sourceText: null);
                return EmptyQueryPlan.Instance;

            case DropProcedureStatement drop:
                _routines.ApplyDropProcedure(drop);
                return EmptyQueryPlan.Instance;

            case ExecStatement exec:
                return PlanExec(exec);

            default:
                throw new NotSupportedException(
                    $"Statement type '{statement.GetType().Name}' is not yet supported by Plan(string). " +
                    $"Use the dedicated APIs (e.g. AddFile for file registration) or extend Plan to dispatch this statement.");
        }
    }

    private IQueryPlan PlanExec(ExecStatement exec)
    {
        // Lower EXEC udf.fn(args) to SELECT udf.fn(args) — a tableless query
        // against the implicit single-row source. UDF inlining and model hoisting
        // apply exactly as they would for an explicit SELECT, so UDFs, model
        // invocations, and template strings in the body all work unchanged.
        SelectStatement syntheticSelect = new(
            Columns: [new SelectColumn(exec.Call)]);
        QueryExpression syntheticQuery = new SelectQueryExpression(syntheticSelect);
        return PlanQuery(syntheticQuery);
    }

    private IQueryPlan PlanQuery(QueryExpression query)
    {
        QueryExpression inlined = UdfInliner.Inline(query, _udfs);
        QueryPlanner planner = new(this, _functions);
        IQueryOperator op = planner.Plan(inlined);
        return new QueryPlan(op, this, _functions, _backing);
    }

    /// <summary>
    /// Per-catalog map from <c>storeId</c> byte to <see cref="IBlobSource"/>. Each
    /// <see cref="DatumFileTableProviderV2"/> with a <c>.datum-blob</c> sidecar registers
    /// its source here at <see cref="Add(TableDescriptor)"/> time and gets back a byte;
    /// the decoder stamps that byte onto every sidecar-flagged
    /// <see cref="DataValue"/>; image accessors resolve through the registry at access
    /// time. Catalog-scoped (not query-scoped) so storeId assignments stay stable
    /// across queries against the same provider. A nested child catalog falls through
    /// to its parent's registry so providers added via either layer share one byte
    /// space.
    /// </summary>
    public SidecarRegistry SidecarRegistry => Parent?.SidecarRegistry ?? _sidecarRegistry;

    /// <summary>
    /// Server-wide model catalog. <see langword="null"/> until set by the host
    /// (typically at startup via <c>BuiltinModels.Register</c>); inherited from a
    /// parent catalog when nested. Held on the table catalog so query planning
    /// has uniform access to it without threading a separate parameter through
    /// every entry point.
    /// </summary>
    public Models.ModelCatalog? Models
    {
        get => _modelCatalog ?? Parent?.Models;
        set
        {
            if (Parent is not null && value is not null)
            {
                throw new InvalidOperationException(
                    "Models cannot be set on a nested table catalog — set it on the root.");
            }
            _modelCatalog = value;
        }
    }
    private Models.ModelCatalog? _modelCatalog;

    /// <summary>
    /// Optional tracer for <c>models.X(...)</c> invocations. Set by hosts
    /// that want to observe per-dispatch shape + timing — the interactive
    /// shell wires this up via <c>.trace on</c>; production deployments
    /// can attach metric-emitting or structured-logging implementations.
    /// <see cref="QueryPlan"/> reads this value when constructing each
    /// query's <see cref="DatumIngest.Execution.ExecutionContext"/>, so
    /// toggling at runtime affects subsequently planned queries.
    /// </summary>
    public DatumIngest.Execution.IModelInvocationTracer? ModelTracer
    {
        get => _modelTracer ?? Parent?.ModelTracer;
        set
        {
            if (Parent is not null && value is not null)
            {
                throw new InvalidOperationException(
                    "ModelTracer cannot be set on a nested table catalog — set it on the root.");
            }
            _modelTracer = value;
        }
    }
    private DatumIngest.Execution.IModelInvocationTracer? _modelTracer;

    /// <summary>
    /// Gets the total number of tables registered in this catalog, including
    /// those inherited from the parent catalog if present.
    /// </summary>
    public int Count => Tables.Count + (Parent?.Count ?? 0);

    /// <summary>
    /// Returns <see langword="true"/> if a table with the given name is registered
    /// in this catalog or its parent; otherwise <see langword="false"/>.
    /// </summary>
    /// <param name="name">The name of the table to check.</param>
    /// <returns><see langword="true"/> if the table exists; otherwise <see langword="false"/>.</returns>
    public bool HasTable(string name)
    {
        if (Tables.ContainsKey(name))
        {
            return true;
        }
        else if (Parent is not null)
        {
            return Parent.HasTable(name);
        }
        else
        {
            return false;
        }
    }

    /// <summary>
    /// Attempts to get the table provider associated with the given logical table name.
    /// </summary>
    /// <param name="name">The logical name of the table.</param>
    /// <param name="provider">When this method returns, contains the table provider associated with the given name, if found; otherwise, <c>null</c>.</param>
    /// <returns><see langword="true"/> if the table provider was found; otherwise, <see langword="false"/>.</returns>
    public bool TryGetTable(string name, [NotNullWhen(true)] out ITableProvider? provider)
    {
        if (Tables.TryGetValue(name, out provider))
        {
            return true;
        }
        else if (Parent is not null)
        {
            return Parent.TryGetTable(name, out provider);
        }
        else
        {
            provider = null;
            return false;
        }
    }

    /// <summary>
    /// Gets the table provider associated with the given logical table name.
    /// If the name is not found in this catalog, the parent catalog is consulted
    /// if it exists.
    /// </summary>
    /// <param name="name">The logical name of the table.</param>
    /// <returns>The table provider associated with the given name.</returns>
    /// <exception cref="KeyNotFoundException">Thrown if the table name is not found in this catalog or its parent.</exception>
    public ITableProvider this[string name]
    {
        get
        {
            if (Tables.TryGetValue(name, out ITableProvider? provider))
            {
                return provider;
            }
            else if (Parent is not null)
            {
                return Parent[name];
            }
            else
            {
                throw new KeyNotFoundException($"Table '{name}' is not registered in the catalog.");
            }
        }
    }

    /// <summary>
    /// Registers a table descriptor, making it available for resolution by name.
    /// The provider is created immediately and kept alive until the catalog is disposed.
    /// </summary>
    /// <param name="tableDescriptor">The table descriptor to register.</param>
    /// <returns>The created table provider.</returns>
    /// <exception cref="ArgumentException">Thrown if a table with the same name is already registered in this catalog or its parent.</exception>
    public ITableProvider Add(TableDescriptor tableDescriptor)
    {
        if (Parent is TableCatalog parent && parent.Tables.ContainsKey(tableDescriptor.Name))
        {
            throw new ArgumentException($"A table with the name '{tableDescriptor.Name}' is already registered in the parent catalog.");
        }
        // v2 reader validates magic + version inside its constructor via
        // DatumFileReaderV2.Open. Files written by older format versions
        // throw InvalidDataException at open time.
        DatumFileTableProviderV2 provider = new(tableDescriptor, Pool);
        if (Tables.TryAdd(tableDescriptor.Name, provider))
        {
            RegisterProviderSidecar(provider);
            return Tables[tableDescriptor.Name];
        }
        else
        {
            (provider as IDisposable)?.Dispose();
            throw new ArgumentException($"A table with the name '{tableDescriptor.Name}' is already registered.");
        }
    }

    /// <summary>
    /// Registers a table provider instance, making it available for resolution by name.
    /// The provider is kept alive until the catalog is disposed.
    /// </summary>
    /// <param name="tableProvider">The table provider instance to register.</param>
    /// <returns>The registered table provider.</returns>
    /// <exception cref="ArgumentException">Thrown if a table with the same name is already registered in this catalog or its parent.</exception>
    public ITableProvider Add(ITableProvider tableProvider)
    {
        if (Parent is TableCatalog parent && parent.Tables.ContainsKey(tableProvider.Name))
        {
            throw new ArgumentException($"A table with the name '{tableProvider.Name}' is already registered in the parent catalog.");
        }
        else if (Tables.TryAdd(tableProvider.Name, tableProvider))
        {
            RegisterProviderSidecar(tableProvider);
            return Tables[tableProvider.Name];
        }
        else
        {
            throw new ArgumentException($"A table with the name '{tableProvider.Name}' is already registered.");
        }
    }

    /// <summary>
    /// If <paramref name="provider"/> is a v1 / v2 <c>.datum</c> file
    /// provider with a <c>.datum-blob</c> companion sidecar, registers
    /// the sidecar with this catalog's <see cref="SidecarRegistry"/> and
    /// stamps the assigned <c>storeId</c> onto the provider so its
    /// decoder can label sidecar-flagged DataValues at decode time.
    /// No-op for tabular-only providers and for non-datum providers.
    /// </summary>
    private void RegisterProviderSidecar(ITableProvider provider)
    {
        if (provider is not IDatumFileTableProvider datumProvider) return;
        if (datumProvider.Sidecar is not { } source) return;

        datumProvider.SidecarStoreId = SidecarRegistry.Register(source);
    }

    /// <summary>
    /// Removes a previously registered table from the catalog, cleaning up any
    /// </summary>
    public void Remove(string tableName)
    {
        if (Tables.TryGetValue(tableName, out ITableProvider? provider))
        {
            // TODO: when we properly support information_schema then we'll need
            // to take steps to prevent dropping it. Leaving this here until that time
            // since this is where we'd want to check.
            // NOTE: you can't drop from the parent catalog
            Tables.TryRemove(tableName, out _);    
        }   
    }


    /// <inheritdoc />
    public IEnumerator<ITableProvider> GetEnumerator()
    {
        foreach (var provider in Tables.Values)
        {
            yield return provider;
        }

        if (Parent is not null)
        {
            foreach (var provider in Parent)
            {
                if (!Tables.ContainsKey(provider.Name))
                {
                    yield return provider;
                }
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // foreach (MappedSourceIndexSet mapped in _mappedIndexSets)
        // {
        //     mapped.Dispose();
        // }

        // _mappedIndexSets.Clear();

        // foreach (string tempFile in _tempFiles)
        // {
        //     try
        //     {
        //         if (File.Exists(tempFile))
        //         {
        //             File.Delete(tempFile);
        //         }
        //     }
        //     catch (IOException)
        //     {
        //         // Best-effort cleanup.
        //     }
        // }

        // _tempFiles.Clear();
    }


}
