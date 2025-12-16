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
        }
    }


    private TableCatalog? Parent { get; }
    internal Pool Pool { get; }
    private readonly PoolBacking _backing;
    private readonly FunctionRegistry _functions;
    private readonly UdfRegistry _udfs;
    private readonly ProcedureRegistry _procedures;
    private readonly CatalogStore? _catalogStore;
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

        // CREATE PROCEDURE is dispatched here (not in Plan(Statement)) so the
        // original SQL text can be captured verbatim for catalog persistence
        // and introspection — preserving the user's formatting and comments
        // through round-trips. Any other statement type funnels through the
        // standard AST-only path.
        if (statement is CreateProcedureStatement create)
        {
            ApplyCreateProcedure(create, sql);
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
                ApplyCreateFunction(create);
                return EmptyQueryPlan.Instance;

            case DropFunctionStatement drop:
                ApplyDropFunction(drop);
                return EmptyQueryPlan.Instance;

            case CreateProcedureStatement:
                throw new InvalidOperationException(
                    "CREATE PROCEDURE must be planned via Plan(string) so the " +
                    "original source text can be captured for catalog persistence. " +
                    "Build the SQL string and call Plan(sql) instead of constructing " +
                    "the AST manually.");

            case DropProcedureStatement drop:
                ApplyDropProcedure(drop);
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

    private void ApplyCreateFunction(CreateFunctionStatement create)
    {
        // Defaults must be contiguous at the tail of the parameter list so
        // call-site arity stays unambiguous (positional matching only).
        ValidateDefaultsContiguous(create.Parameters, $"CREATE FUNCTION {create.Name}");

        // Validate the body at registration time by running the inliner on it
        // against the current registry. This catches references to undefined
        // UDFs in the body and direct cycles (A -> A) eagerly. Indirect
        // cycles introduced by later registrations surface at the first call
        // site that closes the loop, since they require visibility we don't
        // have here.
        try
        {
            UdfInliner.Inline(create.Body, _udfs);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"CREATE FUNCTION {create.Name}: {ex.Message}", ex);
        }

        UdfDescriptor descriptor = new(
            create.Name,
            create.Parameters,
            create.ReturnTypeName,
            create.Body,
            create.ReturnIsNotNull);

        if (create.IfNotExists && _udfs.TryGet(create.Name, out _))
        {
            return;
        }

        _udfs.Register(descriptor, replace: create.OrReplace);
        _catalogStore?.Save(_udfs, _procedures);
    }

    private void ApplyDropFunction(DropFunctionStatement drop)
    {
        bool removed = _udfs.Unregister(drop.Name);
        if (!removed && !drop.IfExists)
        {
            throw new InvalidOperationException(
                $"UDF '{drop.Name}' is not registered. Use DROP FUNCTION IF EXISTS to make this a no-op.");
        }

        if (removed) _catalogStore?.Save(_udfs, _procedures);
    }

    private void ApplyCreateProcedure(CreateProcedureStatement create, string sourceText)
    {
        // Defaults must be contiguous at the tail of the parameter list so
        // call-site arity stays unambiguous (positional matching only).
        ValidateDefaultsContiguous(create.Parameters, $"CREATE PROCEDURE {create.Name}");

        // Validate referenced UDFs exist by inlining each expression in the
        // body's statement tree against the current registry. Catches things
        // like a typo'd udf.foo() before the user runs the procedure.
        try
        {
            ValidateProcedureBody(create.Body);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"CREATE PROCEDURE {create.Name}: {ex.Message}", ex);
        }

        ProcedureDescriptor descriptor = new(
            create.Name,
            create.Parameters,
            create.Body,
            sourceText);

        if (create.IfNotExists && _procedures.TryGet(create.Name, out _))
        {
            return;
        }

        _procedures.Register(descriptor, replace: create.OrReplace);
        _catalogStore?.Save(_udfs, _procedures);
    }

    private void ApplyDropProcedure(DropProcedureStatement drop)
    {
        bool removed = _procedures.Unregister(drop.Name);
        if (!removed && !drop.IfExists)
        {
            throw new InvalidOperationException(
                $"Procedure '{drop.Name}' is not registered. " +
                "Use DROP PROCEDURE IF EXISTS to make this a no-op.");
        }

        if (removed) _catalogStore?.Save(_udfs, _procedures);
    }

    /// <summary>
    /// Enforces that any parameters with <see cref="UdfParameter.Default"/>
    /// values appear contiguously at the tail of the parameter list. Without
    /// this constraint a call site like <c>foo(1, 2)</c> against
    /// <c>foo(@a, @b = 0, @c)</c> would be ambiguous — does the second
    /// argument bind to <c>@b</c> or (with default <c>@b</c>) to <c>@c</c>?
    /// Disallowing the shape removes the ambiguity at registration time.
    /// </summary>
    private static void ValidateDefaultsContiguous(
        IReadOnlyList<UdfParameter> parameters, string contextLabel)
    {
        bool sawDefault = false;
        foreach (UdfParameter p in parameters)
        {
            if (p.Default is not null)
            {
                sawDefault = true;
            }
            else if (sawDefault)
            {
                throw new InvalidOperationException(
                    $"{contextLabel}: parameter '@{p.Name}' has no default but follows a parameter " +
                    "with a default. Defaults must be contiguous at the end of the parameter list.");
            }
        }
    }

    /// <summary>
    /// Walks every expression in a procedure body's statement tree and
    /// runs the UDF inliner against it, so unresolved <c>udf.X(...)</c>
    /// references surface at <c>CREATE PROCEDURE</c> time rather than at
    /// the first <c>EXEC</c>. Doesn't substitute parameters — those are
    /// resolved at runtime when the procedure is invoked.
    /// </summary>
    private void ValidateProcedureBody(Statement statement)
    {
        switch (statement)
        {
            case BlockStatement block:
                foreach (Statement child in block.Statements) ValidateProcedureBody(child);
                break;
            case IfStatement ifs:
                _ = UdfInliner.Inline(ifs.Predicate, _udfs);
                ValidateProcedureBody(ifs.Then);
                if (ifs.Else is not null) ValidateProcedureBody(ifs.Else);
                break;
            case WhileStatement loop:
                _ = UdfInliner.Inline(loop.Predicate, _udfs);
                ValidateProcedureBody(loop.Body);
                break;
            case ForCounterStatement forC:
                _ = UdfInliner.Inline(forC.Start, _udfs);
                _ = UdfInliner.Inline(forC.End, _udfs);
                if (forC.Step is not null) _ = UdfInliner.Inline(forC.Step, _udfs);
                ValidateProcedureBody(forC.Body);
                break;
            case ForInStatement forIn:
                _ = UdfInliner.Inline(forIn.Source, _udfs);
                ValidateProcedureBody(forIn.Body);
                break;
            case DeclareStatement decl:
                if (decl.Initializer is not null) _ = UdfInliner.Inline(decl.Initializer, _udfs);
                break;
            case SetStatement set:
                _ = UdfInliner.Inline(set.Value, _udfs);
                break;
            case QueryStatement q:
                _ = UdfInliner.Inline(q.Query, _udfs);
                break;
            case ExecStatement exec:
                _ = UdfInliner.Inline(exec.Call, _udfs);
                break;
            case BreakStatement:
            case ContinueStatement:
                // No expressions to validate; legality (must sit inside a
                // loop) is enforced at invocation time by the executor.
                break;
            // Nested routine DDL inside a procedure body is rejected here so
            // the user sees the error at CREATE PROCEDURE rather than at the
            // first EXEC. Nested DML and table DDL are intentionally allowed
            // — procedures should be able to mutate data and shape temp
            // tables.
            case CreateFunctionStatement createFn:
                throw new InvalidOperationException(
                    $"Nested CREATE FUNCTION '{createFn.Name}' is not allowed inside a " +
                    "procedure body. Define UDFs at the top level before the procedure.");
            case CreateProcedureStatement createProc:
                throw new InvalidOperationException(
                    $"Nested CREATE PROCEDURE '{createProc.Name}' is not allowed inside a " +
                    "procedure body.");
            case DropFunctionStatement dropFn:
                throw new InvalidOperationException(
                    $"Nested DROP FUNCTION '{dropFn.Name}' is not allowed inside a procedure body.");
            case DropProcedureStatement dropProc:
                throw new InvalidOperationException(
                    $"Nested DROP PROCEDURE '{dropProc.Name}' is not allowed inside a procedure body.");
            default:
                break;
        }
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

    // /// <summary>
    // /// Optional parent catalog consulted when a table name is not found locally.
    // /// Used by query context overlays to fall through to the session's base catalog
    // /// for non-temp tables.
    // /// </summary>
    // public TableCatalog? Parent { get; init; }

    // /// <summary>
    // /// Registers a provider factory for a given provider identifier.
    // /// </summary>
    // /// <param name="providerName">Provider identifier (e.g. "csv", "json").</param>
    // /// <param name="factory">Factory function that creates a new provider instance.</param>
    // public void RegisterProvider(string providerName, Func<ITableProvider> factory)
    // {
    //     _providerFactories[providerName] = factory;
    // }

    // /// <summary>
    // /// Registers a table descriptor, making it available for resolution by name.
    // /// </summary>
    // /// <param name="descriptor">Table descriptor to register.</param>
    // public void Register(TableDescriptor descriptor)
    // {
    //     _descriptors[descriptor.Name] = descriptor;
    // }

    // /// <summary>
    // /// Removes a previously registered table from the catalog, cleaning up any
    // /// associated schemas, indexes, manifests, and pending sidecar references.
    // /// </summary>
    // /// <param name="tableName">The logical table name to remove.</param>
    // /// <returns><see langword="true"/> if the table was found and removed; otherwise <see langword="false"/>.</returns>
    // public bool Unregister(string tableName)
    // {
    //     bool removed = _descriptors.Remove(tableName);

    //     if (removed)
    //     {
    //         _schemas.Remove(tableName);
    //         _indexes.Remove(tableName);
    //         _manifests.Remove(tableName);
    //         _pendingIndexSidecarPaths.Remove(tableName);
    //         _analysisPending.Remove(tableName);
    //     }

    //     return removed;
    // }

    // /// <summary>
    // /// Marks a table as having been modified since its last analysis and removes
    // /// any cached index, manifest, and schema so that stale metadata is never
    // /// served to the query planner before a sidecar rebuild occurs.
    // /// </summary>
    // /// <param name="tableName">The logical table name.</param>
    // public void InvalidateAnalysis(string tableName)
    // {
    //     _analysisPending.Add(tableName);
    //     _indexes.Remove(tableName);
    //     _manifests.Remove(tableName);
    //     _schemas.Remove(tableName);
    // }

    // /// <summary>
    // /// Marks a table as having been modified since its last analysis.
    // /// A subsequent ANALYZE will rebuild sidecars; without this flag, ANALYZE is a no-op.
    // /// </summary>
    // /// <param name="tableName">The logical table name.</param>
    // public void MarkAnalysisPending(string tableName)
    // {
    //     _analysisPending.Add(tableName);
    // }

    // /// <summary>
    // /// Returns <see langword="true"/> if the table has been modified since its last analysis.
    // /// </summary>
    // /// <param name="tableName">The logical table name.</param>
    // public bool IsAnalysisPending(string tableName)
    // {
    //     return _analysisPending.Contains(tableName);
    // }

    // /// <summary>
    // /// Clears the analysis-pending flag, indicating that sidecars are up to date.
    // /// Called after a successful sidecar rebuild.
    // /// </summary>
    // /// <param name="tableName">The logical table name.</param>
    // public void ClearAnalysisPending(string tableName)
    // {
    //     _analysisPending.Remove(tableName);
    // }

    // /// <summary>
    // /// Registers a table from a file path, using the full filename (including
    // /// extension) as the table name and auto-detecting the provider.
    // /// </summary>
    // /// <param name="filePath">Absolute or relative path to the data file.</param>
    // /// <exception cref="ArgumentException">
    // /// Thrown when the file format cannot be determined. Use the
    // /// <see cref="Register(TableDescriptor)"/> overload with an explicit provider.
    // /// </exception>
    // public void Register(string filePath)
    // {
    //     Register(FileFormatDetector.DeriveTableName(filePath), filePath);
    // }

    // /// <summary>
    // /// Registers a table by name and file path, auto-detecting the provider
    // /// from the file extension, filename pattern, or magic bytes.
    // /// </summary>
    // /// <param name="name">Logical table name for SQL FROM clauses.</param>
    // /// <param name="filePath">Absolute or relative path to the data file.</param>
    // /// <exception cref="ArgumentException">
    // /// Thrown when the file format cannot be determined. Use the
    // /// <see cref="Register(TableDescriptor)"/> overload with an explicit provider.
    // /// </exception>
    // public void Register(string name, string filePath)
    // {
    //     Register(name, filePath, new Dictionary<string, string>());
    // }

    // /// <summary>
    // /// Registers a table by name and file path with provider-specific options,
    // /// auto-detecting the provider from the file extension, filename pattern,
    // /// or magic bytes.
    // /// </summary>
    // /// <param name="name">Logical table name for SQL FROM clauses.</param>
    // /// <param name="filePath">Absolute or relative path to the data file.</param>
    // /// <param name="options">Provider-specific key-value options (e.g. delimiter, header).</param>
    // /// <exception cref="ArgumentException">
    // /// Thrown when the file format cannot be determined. Use the
    // /// <see cref="Register(TableDescriptor)"/> overload with an explicit provider.
    // /// </exception>
    // public void Register(string name, string filePath, IReadOnlyDictionary<string, string> options)
    // {
    //     DetectedFormat format = FileFormatDetector.DetectFormat(filePath)
    //         ?? throw new ArgumentException(
    //             $"Cannot detect file format for '{filePath}'. " +
    //             $"Supported formats: {FileFormatDetector.SupportedFormatList}. " +
    //             "Use Register(TableDescriptor) with an explicit provider.",
    //             nameof(filePath));

    //     if (format.Compression != CompressionKind.None && SeekableProviders.Contains(format.Provider))
    //     {
    //         string tempPath = DecompressGzip(filePath);
    //         _tempFiles.Add(tempPath);
    //         Register(new TableDescriptor(format.Provider, name, tempPath, options));
    //     }
    //     else
    //     {
    //         Register(new TableDescriptor(format.Provider, name, filePath, options, format.Compression));
    //     }
    // }

    // /// <summary>
    // /// Registers a table by name and file path, auto-detecting the provider and
    // /// expanding multi-table sources (e.g. root-object JSON files) in one call.
    // /// </summary>
    // /// <param name="name">Logical table name for SQL FROM clauses.</param>
    // /// <param name="filePath">Absolute or relative path to the data file.</param>
    // /// <param name="cancellationToken">Cancellation token.</param>
    // /// <exception cref="ArgumentException">
    // /// Thrown when the file format cannot be determined.
    // /// </exception>
    // public Task RegisterAsync(string name, string filePath, CancellationToken cancellationToken)
    // {
    //     return RegisterAsync(name, filePath, new Dictionary<string, string>(), cancellationToken);
    // }

    // /// <summary>
    // /// Registers a table from a file path, using the full filename (including
    // /// extension) as the table name, auto-detecting the provider, and expanding
    // /// multi-table sources (e.g. root-object JSON files) in one call.
    // /// </summary>
    // /// <param name="filePath">Absolute or relative path to the data file.</param>
    // /// <param name="cancellationToken">Cancellation token.</param>
    // /// <exception cref="ArgumentException">
    // /// Thrown when the file format cannot be determined.
    // /// </exception>
    // public Task RegisterAsync(string filePath, CancellationToken cancellationToken)
    // {
    //     return RegisterAsync(FileFormatDetector.DeriveTableName(filePath), filePath, cancellationToken);
    // }

    // /// <summary>
    // /// Registers a table by name and file path with provider-specific options,
    // /// auto-detecting the provider and expanding multi-table sources
    // /// (e.g. root-object JSON files) in one call.
    // /// </summary>
    // /// <param name="name">Logical table name for SQL FROM clauses.</param>
    // /// <param name="filePath">Absolute or relative path to the data file.</param>
    // /// <param name="options">Provider-specific key-value options (e.g. delimiter, header).</param>
    // /// <param name="cancellationToken">Cancellation token.</param>
    // /// <exception cref="ArgumentException">
    // /// Thrown when the file format cannot be determined.
    // /// </exception>
    // public async Task RegisterAsync(
    //     string name,
    //     string filePath,
    //     IReadOnlyDictionary<string, string> options,
    //     CancellationToken cancellationToken)
    // {
    //     Register(name, filePath, options);
    // }

    // /// <summary>
    // /// Registers a table descriptor and expands multi-table sources
    // /// (e.g. root-object JSON files) in one call.
    // /// </summary>
    // /// <param name="descriptor">Table descriptor to register.</param>
    // /// <param name="cancellationToken">Cancellation token.</param>
    // public async Task RegisterAsync(TableDescriptor descriptor, CancellationToken cancellationToken)
    // {
    //     Register(descriptor);
    // }

    // /// <summary>
    // /// Resolves a table name to its descriptor.
    // /// </summary>
    // /// <param name="tableName">Logical table name from the SQL query.</param>
    // /// <returns>The matching descriptor.</returns>
    // /// <exception cref="KeyNotFoundException">Thrown when the table name is not registered.</exception>
    // public TableDescriptor Resolve(string tableName)
    // {
    //     if (_descriptors.TryGetValue(tableName, out TableDescriptor? descriptor))
    //     {
    //         return descriptor;
    //     }

    //     if (Parent is not null)
    //     {
    //         return Parent.Resolve(tableName);
    //     }

    //     throw new KeyNotFoundException($"Table '{tableName}' is not registered in the catalog.");
    // }

    // /// <summary>
    // /// Creates a provider instance for the given descriptor.
    // /// </summary>
    // /// <param name="descriptor">Table descriptor whose provider should be created.</param>
    // /// <returns>A new provider instance.</returns>
    // /// <exception cref="KeyNotFoundException">Thrown when no factory is registered for the provider.</exception>
    // public ITableProvider CreateProvider(TableDescriptor descriptor)
    // {
    //     if (_providerFactories.TryGetValue(descriptor.Provider, out Func<ITableProvider>? factory))
    //     {
    //         return factory();
    //     }

    //     if (Parent is not null)
    //     {
    //         return Parent.CreateProvider(descriptor);
    //     }

    //     throw new KeyNotFoundException($"No provider factory registered for '{descriptor.Provider}'.");
    // }

    // /// <summary>
    // /// Returns all registered table names, including those from the parent catalog.
    // /// </summary>
    // public IEnumerable<string> TableNames =>
    //     Parent is null
    //         ? _descriptors.Keys
    //         : _descriptors.Keys.Concat(
    //             Parent.TableNames.Where(name => !_descriptors.ContainsKey(name)));

    // /// <summary>
    // /// Returns the number of tables registered directly in this catalog,
    // /// excluding any tables inherited from <see cref="Parent"/>.
    // /// </summary>
    // public int LocalTableCount => _descriptors.Count;

    // /// <summary>
    // /// Returns all registered provider names.
    // /// </summary>
    // public IEnumerable<string> ProviderNames => _providerFactories.Keys;

    // /// <summary>
    // /// Attempts to resolve a table name without throwing.
    // /// </summary>
    // /// <param name="tableName">Logical table name from the SQL query.</param>
    // /// <param name="descriptor">The matching descriptor, or null if not found.</param>
    // /// <returns>True if the table was found; otherwise false.</returns>
    // public bool TryResolve(string tableName, out TableDescriptor? descriptor)
    // {
    //     if (_descriptors.TryGetValue(tableName, out descriptor))
    //     {
    //         return true;
    //     }

    //     if (Parent is not null)
    //     {
    //         return Parent.TryResolve(tableName, out descriptor);
    //     }

    //     descriptor = null;
    //     return false;
    // }

    // /// <summary>
    // /// Resolves a table name and returns its schema by creating the appropriate
    // /// provider and calling <see cref="ITableProvider.GetSchemaAsync"/>.
    // /// </summary>
    // /// <param name="tableName">Logical table name from the SQL query.</param>
    // /// <param name="cancellationToken">Cancellation token.</param>
    // /// <returns>The schema of the named table.</returns>
    // /// <exception cref="KeyNotFoundException">
    // /// Thrown when the table name or its provider is not registered.
    // /// </exception>
    // public async Task<Schema> GetSchemaAsync(string tableName, CancellationToken cancellationToken)
    // {
    //     // Return cached schema from sidecar if available, avoiding provider I/O.
    //     // Uses TryGetSchema which chains to the parent catalog.
    //     if (TryGetSchema(tableName, out Schema? cachedSchema) && cachedSchema is not null)
    //     {
    //         return cachedSchema;
    //     }

    //     // Return cached schema from index if available, avoiding provider I/O.
    //     // Uses TryGetIndex which chains to the parent catalog.
    //     if (TryGetIndex(tableName, out SourceIndex? index) && index is not null)
    //     {
    //         return index.Schema.Schema;
    //     }

    //     TableDescriptor descriptor = Resolve(tableName);
    //     ITableProvider provider = CreateProvider(descriptor);
    //     return await provider.GetSchemaAsync(descriptor, cancellationToken).ConfigureAwait(false);
    // }

    // /// <summary>
    // /// Registers a pre-built source index for a table, enabling chunk-based
    // /// partition pruning and cached schema resolution.
    // /// </summary>
    // /// <param name="tableName">Logical table name matching a registered descriptor.</param>
    // /// <param name="index">The source index to associate with the table.</param>
    // public void RegisterIndex(string tableName, SourceIndex index)
    // {
    //     _indexes[tableName] = index;
    // }

    // /// <summary>
    // /// Registers a v5 memory-mapped index set loaded externally (e.g. via an explicit
    // /// <c>--index-path</c> CLI option). The catalog takes ownership of the handle and
    // /// will dispose it when the catalog itself is disposed.
    // /// </summary>
    // /// <param name="mappedIndexSet">The mapped index set to track for disposal.</param>
    // internal void TrackMappedIndexSet(MappedSourceIndexSet mappedIndexSet)
    // {
    //     _mappedIndexSets.Add(mappedIndexSet);
    // }

    // /// <summary>
    // /// Attempts to retrieve a source index for the given table name.
    // /// If the index was discovered via a sidecar file but not yet loaded, it is deserialized
    // /// on demand here so that the heap cost is only paid when a query actually needs the index.
    // /// </summary>
    // /// <param name="tableName">Logical table name.</param>
    // /// <param name="index">The source index, or <c>null</c> if none is registered.</param>
    // /// <returns><c>true</c> if an index was found; otherwise <c>false</c>.</returns>
    // public bool TryGetIndex(string tableName, out SourceIndex? index)
    // {
    //     if (_indexes.TryGetValue(tableName, out index))
    //     {
    //         return true;
    //     }

    //     if (_pendingIndexSidecarPaths.ContainsKey(tableName))
    //     {
    //         LoadPendingIndexSidecar(tableName);
    //         return _indexes.TryGetValue(tableName, out index);
    //     }

    //     if (Parent is not null)
    //     {
    //         return Parent.TryGetIndex(tableName, out index);
    //     }

    //     index = null;
    //     return false;
    // }

    // /// <summary>
    // /// Registers a pre-computed <see cref="QueryResultsManifest"/> for a table,
    // /// enabling statistics-driven cardinality estimation in the query planner.
    // /// </summary>
    // /// <param name="tableName">Logical table name matching a registered descriptor.</param>
    // /// <param name="manifest">The manifest containing per-column statistics.</param>
    // public void RegisterManifest(string tableName, QueryResultsManifest manifest)
    // {
    //     _manifests[tableName] = manifest;
    // }

    // /// <summary>
    // /// Attempts to retrieve a <see cref="QueryResultsManifest"/> for the given table name.
    // /// </summary>
    // /// <param name="tableName">Logical table name.</param>
    // /// <param name="manifest">The manifest, or <c>null</c> if none is registered.</param>
    // /// <returns><c>true</c> if a manifest was found; otherwise <c>false</c>.</returns>
    // public bool TryGetManifest(string tableName, out QueryResultsManifest? manifest)
    // {
    //     if (_manifests.TryGetValue(tableName, out manifest))
    //     {
    //         return true;
    //     }

    //     if (Parent is not null)
    //     {
    //         return Parent.TryGetManifest(tableName, out manifest);
    //     }

    //     manifest = null;
    //     return false;
    // }

    // /// <summary>
    // /// Registers a pre-computed <see cref="Schema"/> for a table,
    // /// enabling cached schema resolution without provider I/O.
    // /// </summary>
    // /// <param name="tableName">Logical table name matching a registered descriptor.</param>
    // /// <param name="schema">The schema to cache.</param>
    // public void RegisterSchema(string tableName, Schema schema)
    // {
    //     _schemas[tableName] = schema;
    // }

    // /// <summary>
    // /// Attempts to retrieve a cached <see cref="Schema"/> for the given table name.
    // /// </summary>
    // /// <param name="tableName">Logical table name.</param>
    // /// <param name="schema">The cached schema, or <c>null</c> if none is registered.</param>
    // /// <returns><c>true</c> if a cached schema was found; otherwise <c>false</c>.</returns>
    // public bool TryGetSchema(string tableName, out Schema? schema)
    // {
    //     if (_schemas.TryGetValue(tableName, out schema))
    //     {
    //         return true;
    //     }

    //     if (Parent is not null)
    //     {
    //         return Parent.TryGetSchema(tableName, out schema);
    //     }

    //     schema = null;
    //     return false;
    // }

    // /// <summary>
    // /// Auto-discovers <c>.datum-index</c>, <c>.datum-manifest</c>, <c>.datum-vocabulary</c>,
    // /// and <c>.datum-schema</c> sidecar files for all registered tables. Each sidecar is
    // /// loaded at most once per unique source file path, and tables that already have a
    // /// registered artifact are skipped.
    // /// </summary>
    // /// <remarks>
    // /// This is the single entry point for sidecar discovery, replacing the per-site
    // /// implementations that previously existed in the CLI, gRPC server, and compute backend.
    // /// Call this after all tables have been registered and expanded.
    // /// </remarks>
    // public void DiscoverSidecars()
    // {
    //     HashSet<string> loadedIndexPaths = new(StringComparer.OrdinalIgnoreCase);
    //     HashSet<string> loadedManifestPaths = new(StringComparer.OrdinalIgnoreCase);
    //     HashSet<string> loadedSchemaPaths = new(StringComparer.OrdinalIgnoreCase);

    //     // Snapshot table names to avoid issues if the set is mutated.
    //     List<string> tableNames = new(_descriptors.Keys);

    //     foreach (string tableName in tableNames)
    //     {
    //         TableDescriptor descriptor = _descriptors[tableName];

    //         DiscoverSidecarIndex(descriptor, tableNames, loadedIndexPaths);
    //         DiscoverSidecarManifest(descriptor, tableNames, loadedManifestPaths);
    //         DiscoverSidecarSchema(descriptor, tableNames, loadedSchemaPaths);
    //     }
    // }

    // private void DiscoverSidecarIndex(
    //     TableDescriptor descriptor,
    //     List<string> tableNames,
    //     HashSet<string> loadedPaths)
    // {
    //     string sidecarPath = FileFormatDetector.GetSidecarBasePath(descriptor.FilePath) + ".datum-index";

    //     if (!File.Exists(sidecarPath) || !loadedPaths.Add(sidecarPath))
    //     {
    //         return;
    //     }

    //     // Register pending entries for all tables that share this source file without
    //     // reading the index data. The actual deserialization is deferred until the first
    //     // call to TryGetIndex, so that large index files (potentially gigabytes after
    //     // decompression) do not consume heap memory at shell startup time.
    //     foreach (string name in tableNames)
    //     {
    //         if (!_descriptors.TryGetValue(name, out TableDescriptor? d)
    //             || !string.Equals(d.FilePath, descriptor.FilePath, StringComparison.OrdinalIgnoreCase)
    //             || _indexes.ContainsKey(name)
    //             || _pendingIndexSidecarPaths.ContainsKey(name))
    //         {
    //             continue;
    //         }

    //         _pendingIndexSidecarPaths[name] = sidecarPath;
    //     }
    // }

    // /// <summary>
    // /// Deserializes the sidecar file associated with <paramref name="tableName"/> and registers
    // /// the resulting <see cref="SourceIndex"/> for every pending table that references the same
    // /// sidecar, so the file is read from disk at most once per sidecar path.
    // /// </summary>
    // private void LoadPendingIndexSidecar(string tableName)
    // {
    //     if (!_pendingIndexSidecarPaths.TryGetValue(tableName, out string? sidecarPath))
    //     {
    //         return;
    //     }

    //     // Collect every table waiting on the same sidecar file so all are populated
    //     // in one pass rather than deserializing the (potentially very large) file repeatedly.
    //     List<string> pendingNames = _pendingIndexSidecarPaths
    //         .Where(pair => string.Equals(pair.Value, sidecarPath, StringComparison.OrdinalIgnoreCase))
    //         .Select(pair => pair.Key)
    //         .ToList();

    //     foreach (string name in pendingNames)
    //     {
    //         _pendingIndexSidecarPaths.Remove(name);
    //     }

    //     if (!File.Exists(sidecarPath))
    //     {
    //         return;
    //     }

    //     MappedSourceIndexSet mapped = UnifiedIndexReader.Open(sidecarPath);
    //     _mappedIndexSets.Add(mapped);
    //     SourceIndexSet indexSet = mapped.IndexSet;

    //     foreach (string name in pendingNames)
    //     {
    //         if (_indexes.ContainsKey(name) || !_descriptors.TryGetValue(name, out TableDescriptor? d))
    //         {
    //             continue;
    //         }

    //         SourceIndex? entry = ResolveSidecarEntry(indexSet.Tables, name, d.FilePath);
    //         if (entry is not null)
    //         {
    //             _indexes[name] = entry;
    //         }
    //     }
    // }

    // private void DiscoverSidecarManifest(
    //     TableDescriptor descriptor,
    //     List<string> tableNames,
    //     HashSet<string> loadedPaths)
    // {
    //     string sidecarPath = FileFormatDetector.GetSidecarBasePath(descriptor.FilePath) + ".datum-manifest";

    //     if (!File.Exists(sidecarPath) || !loadedPaths.Add(sidecarPath))
    //     {
    //         return;
    //     }

    //     string json = File.ReadAllText(sidecarPath);
    //     SourceManifest? sourceManifest = ManifestSerializer.Deserialize(json);

    //     if (sourceManifest is null)
    //     {
    //         return;
    //     }

    //     RegisterSidecarEntries(
    //         sourceManifest.Tables,
    //         descriptor.FilePath,
    //         tableNames,
    //         (name, manifest) => { if (!_manifests.ContainsKey(name)) RegisterManifest(name, manifest); });
    // }

    // private void DiscoverSidecarSchema(
    //     TableDescriptor descriptor,
    //     List<string> tableNames,
    //     HashSet<string> loadedPaths)
    // {
    //     string sidecarPath = FileFormatDetector.GetSidecarBasePath(descriptor.FilePath) + ".datum-schema";

    //     if (!File.Exists(sidecarPath) || !loadedPaths.Add(sidecarPath))
    //     {
    //         return;
    //     }

    //     string json = File.ReadAllText(sidecarPath);
    //     SourceSchema? sourceSchema = SchemaSerializer.Deserialize(json);

    //     if (sourceSchema is null)
    //     {
    //         return;
    //     }

    //     RegisterSidecarEntries(
    //         sourceSchema.Tables,
    //         descriptor.FilePath,
    //         tableNames,
    //         (name, schema) => { if (!_schemas.ContainsKey(name)) _schemas[name] = schema; });
    // }

    // /// <summary>
    // /// Matches sidecar entries to registered tables sharing the same source file path,
    // /// then invokes <paramref name="register"/> for each match.
    // /// </summary>
    // private void RegisterSidecarEntries<T>(
    //     IReadOnlyDictionary<string, T> sidecarEntries,
    //     string filePath,
    //     List<string> tableNames,
    //     Action<string, T> register)
    //     where T : class
    // {
    //     foreach (string name in tableNames)
    //     {
    //         if (!_descriptors.TryGetValue(name, out TableDescriptor? d)
    //             || !string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
    //         {
    //             continue;
    //         }

    //         T? value = ResolveSidecarEntry(sidecarEntries, name, d.FilePath);
    //         if (value is not null)
    //         {
    //             register(name, value);
    //         }
    //     }
    // }

    // private static T? ResolveSidecarEntry<T>(
    //     IReadOnlyDictionary<string, T> sidecarEntries,
    //     string tableName,
    //     string sourceFilePath)
    //     where T : class
    // {
    //     T? value;

    //     // Primary key: registered catalog table name (for current sidecar format).
    //     if (sidecarEntries.TryGetValue(tableName, out value))
    //     {
    //         return value;
    //     }

    //     // Fallback: name derived from file conventions (e.g. orders_csv).
    //     string derivedTableName = FileFormatDetector.DeriveTableName(sourceFilePath);
    //     if (sidecarEntries.TryGetValue(derivedTableName, out value))
    //     {
    //         return value;
    //     }

    //     return null;
    // }

    // /// <summary>
    // /// Synchronously decompresses a gzip file to a temporary file.
    // /// </summary>
    // private static string DecompressGzip(string gzipFilePath)
    // {
    //     string tempPath = Path.Combine(
    //         Path.GetTempPath(),
    //         $"datum_gz_{Guid.NewGuid():N}");

    //     using FileStream source = new(
    //         gzipFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
    //         bufferSize: 81920);

    //     using GZipStream gzipStream = new(source, CompressionMode.Decompress);

    //     using FileStream target = new(
    //         tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
    //         bufferSize: 81920);

    //     gzipStream.CopyTo(target);
    //     return tempPath;
    // }

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
