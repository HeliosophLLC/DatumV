using System.Collections;
using System.Diagnostics.CodeAnalysis;
using DatumIngest.Catalog.Executors;
using DatumIngest.Catalog.Plans;
using DatumIngest.Catalog.Providers;
using DatumIngest.Catalog.Registries;
using DatumIngest.DatumFile.Sidecar;
using DatumIngest.Execution;
using DatumIngest.Functions;
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
    /// Opens a new catalog containing a single <c>.datum</c> file. Owns its own pool
    /// and disposes everything when <see cref="Dispose"/> is called. The table name
    /// defaults to <see cref="PathDetector.DeriveTableName(string)"/> if not supplied.
    /// </summary>
    /// <param name="path">Path to the <c>.datum</c> file.</param>
    /// <param name="name">Optional override for the SQL table name.</param>
    public static TableCatalog FromFile(string path, string? name = null)
    {
        PoolBacking poolBacking = new();
        Pool pool = new(poolBacking);
        TableCatalog catalog = new(pool);
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
        PoolBacking poolBacking = new();
        Pool pool = new(poolBacking);
        TableCatalog catalog = new(pool, catalogPath);
        SearchOption searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (string file in Directory.EnumerateFiles(path, "*.datum", searchOption))
        {
            // Skip files the catalog already registered from its persisted
            // table list — otherwise CREATE TABLE'd tables get re-added by
            // directory enumeration and Add() throws on the duplicate name.
            string derivedName = PathDetector.DeriveTableName(file);
            if (catalog.HasTable(derivedName)) continue;
            catalog.AddFile(file);
        }
        return catalog;
    }

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
    /// <param name="allowExplicitTablePaths">
    /// When <see langword="true"/>, <c>CREATE TABLE … AT 'path'</c> is honored;
    /// the persistent <c>.datum</c> file lands at the supplied location. Defaults
    /// to <see langword="false"/> so production hosts get the safe behaviour
    /// (table file always derived from the catalog directory + table name).
    /// Tests opt in.
    /// </param>
    public TableCatalog(Pool pool, string? catalogPath, bool allowExplicitTablePaths = false)
    {
        this.AllowExplicitTablePaths = allowExplicitTablePaths;
        this.Events = new CatalogEvents();
        this.Pool = pool;
        this._backing = pool.Backing;
        this._functions = FunctionRegistry.CreateDefault();
        // Wire the model-catalog fallback so unhoisted models.X(...) calls
        // (procedural UDF bodies, CALL, etc.) resolve through this catalog.
        // The closure follows the parent-chain getter so child catalogs
        // inherit the root's models without duplicating registrations.
        this._functions.SetModelCatalogResolver(() => Models);
        this._udfs = new UdfRegistry();
        this._procedures = new ProcedureRegistry();
        this._catalogStore = catalogPath is null ? null : new CatalogStore(catalogPath);
        this._routines = new RoutineRegistrar(this, _udfs, _procedures, _functions, _catalogStore);

        // Construct the user-data (FlatFile) backend. The persist callback
        // wraps CatalogStore.Save with the facade's UDF / Procedure
        // registries — the backend doesn't need to know about those.
        string? catalogDirectory = _catalogStore is null
            ? null
            : System.IO.Path.GetDirectoryName(_catalogStore.Path) ?? Environment.CurrentDirectory;
        this._flatFile = new FlatFileCatalog(
            pool,
            _sidecarRegistry,
            catalogDirectory,
            allowExplicitTablePaths,
            persistManifest: () => _catalogStore?.Save(_udfs, _procedures));

        // Construct the read-only backends. System holds host-attached
        // projections; Virtual holds the SQL-standard / engine-introspection
        // views. They reject CREATE / DROP / CREATE INDEX but accept Add()
        // so the host can attach providers (e.g. ModelsTableProvider).
        this._system = new ReadOnlyTableCatalog(new[] { "system" });
        this._virtual = new ReadOnlyTableCatalog(new[] { "information_schema", "datum_catalog" });

        // Models is a real, non-droppable schema mounted alongside the
        // other built-ins (S9). The schema is empty as a table namespace —
        // <c>models.X(...)</c> resolves lazily through
        // <see cref="FunctionRegistry.TryResolveModelFunction"/> against
        // the host-attached <see cref="ModelCatalog"/>, not via table
        // lookups. Mounting it as a backend makes the schema visible to
        // <c>SET search_path</c>, <c>information_schema.schemata</c>, and
        // diagnostics without changing the call-resolution path.
        this._models = new ReadOnlyTableCatalog(new[] { "models" });

        // Schema → backend map. Lookups, DDL, and Add() route through this.
        // Public is the home for user data; system/info_schema/datum_catalog/
        // models are read-only projections.
        this._backends = new Dictionary<string, ITableCatalog>(StringComparer.OrdinalIgnoreCase)
        {
            ["public"] = _flatFile,
            ["system"] = _system,
            ["information_schema"] = _virtual,
            ["datum_catalog"] = _virtual,
            ["models"] = _models,
        };

        // Auto-register intrinsic system + virtual tables. information_schema
        // providers take `this` because they enumerate the catalog at scan
        // time; construction is safe because no scan occurs here.
        _system.Add(new Providers.UdfsTableProvider(pool, _udfs));
        _system.Add(new Providers.ProceduresTableProvider(pool, _procedures));
        _virtual.Add(new Providers.InformationSchemaTablesProvider(pool, this));
        _virtual.Add(new Providers.InformationSchemaColumnsProvider(pool, this));
        _virtual.Add(new Providers.InformationSchemaSchemataProvider(pool));
        _virtual.Add(new Providers.InformationSchemaTableConstraintsProvider(pool, this));
        _virtual.Add(new Providers.InformationSchemaKeyColumnUsageProvider(pool, this));
        _virtual.Add(new Providers.DatumCatalogFunctionsProvider(pool, _functions));
        _virtual.Add(new Providers.DatumCatalogFunctionParametersProvider(pool, _functions));
        _virtual.Add(new Providers.DatumCatalogStatisticsProvider(pool, this));
        _virtual.Add(new Providers.DatumCatalogIndexesProvider(pool, this));
        _virtual.Add(new Providers.DatumCatalogInteractionsProvider(pool, this));

        // Replay any persisted UDFs / procedures into the registries.
        // Done after the system table registrations so the rehydrated
        // entries are immediately visible to introspection.
        if (_catalogStore is not null)
        {
            // Wire the tables provider before any save fires so the
            // file-write path always sees the catalog's current table
            // set (PR10a). The backend snapshots its own state.
            _catalogStore.SetFlatFileBackendStateProvider(_flatFile.SnapshotBackendState);

            CatalogStoreLoadReport report = _catalogStore.Load(_udfs, _procedures);
            CatalogLoadReport = report;

            // The Load() call writes straight into _udfs without going
            // through ApplyCreateFunction, so procedural adapters in the
            // scalar registry haven't been wired yet. Reconcile them here so
            // a freshly opened catalog can immediately invoke any persisted
            // procedural UDF.
            _routines.SyncProceduralAdaptersFromRegistry();

            // Replay persisted tables. The FlatFile backend owns its own
            // state shape; it handles file resolution, provider
            // construction, and per-table tracking dicts.
            if (_catalogStore.LoadedFlatFileBackendState is { } flatFileState)
            {
                _flatFile.LoadBackendState(flatFileState);
            }
        }
    }


    private TableCatalog? Parent { get; }
    internal Pool Pool { get; }

    /// <summary>
    /// When <see langword="true"/>, <c>CREATE TABLE … AT 'path'</c>
    /// statements are honored. Default is <see langword="false"/> —
    /// production hosts reject the clause so table files always land in
    /// the catalog's working directory.
    /// </summary>
    public bool AllowExplicitTablePaths { get; }

    /// <summary>
    /// Catalog-change event bus. Subscribers attach to typed events
    /// (<c>FunctionCreated</c>, <c>TableDropped</c>, etc.) and are invoked
    /// after the underlying DDL commit. See <see cref="CatalogEvents"/>
    /// for subscriber discipline and the parent-drop cascade rule.
    /// </summary>
    public CatalogEvents Events { get; }

    private readonly PoolBacking _backing;
    private readonly FunctionRegistry _functions;
    private readonly UdfRegistry _udfs;
    private readonly ProcedureRegistry _procedures;
    private readonly CatalogStore? _catalogStore;
    private readonly RoutineRegistrar _routines;
    private readonly SidecarRegistry _sidecarRegistry = new();
    private Models.ModelCatalog? _modelCatalog;
    private readonly ModelRegistry _declaredModels = new();
    private Inference.IInferenceDispatcher? _inferenceDispatcher;
    private DatumIngest.Execution.IModelInvocationTracer? _modelTracer;
    /// <summary>
    /// User-data backend: holds <c>public</c> + any user-created schemas.
    /// Owns persistent state, path resolution, and the file-touching half
    /// of CREATE / DROP / CREATE INDEX / DROP INDEX. The only backend
    /// where <see cref="ITableCatalog.SupportsDdl"/> is true.
    /// </summary>
    private readonly FlatFileCatalog _flatFile;

    /// <summary>
    /// Internal accessor for the user-data backend so per-statement
    /// executors (see <see cref="IndexExecutor"/>) can reach the same
    /// FlatFile-only operations the old in-class apply methods used.
    /// </summary>
    internal FlatFileCatalog FlatFile => _flatFile;

    /// <summary>
    /// System-projection backend: owns the <c>system</c> schema
    /// (<c>system.udfs</c>, <c>system.procedures</c>, and
    /// <c>system.models</c> when the host attaches it). Read-only for DDL;
    /// providers are host-attached.
    /// </summary>
    private readonly ReadOnlyTableCatalog _system;

    /// <summary>
    /// Virtual-projection backend: owns the SQL-standard
    /// <c>information_schema</c> and engine-introspection
    /// <c>datum_catalog</c> schemas. Read-only for DDL; providers are
    /// constructed alongside the catalog at startup.
    /// </summary>
    private readonly ReadOnlyTableCatalog _virtual;

    /// <summary>
    /// Models-namespace backend (S9). The schema is empty as a table
    /// namespace — <c>models.X(...)</c> resolves through the function
    /// registry's lazy <c>ModelCatalog</c> resolver, not via table
    /// lookups. Mounting it as a backend makes the schema visible to
    /// <c>SET search_path</c>, <c>information_schema.schemata</c>, and
    /// DROP-rejection without coupling the model dispatch to the
    /// schema router.
    /// </summary>
    private readonly ReadOnlyTableCatalog _models;

    /// <summary>
    /// Schema-to-backend routing table. The facade consults this for
    /// every <see cref="TryGetTable(string, out ITableProvider?)"/> /
    /// <see cref="Add(ITableProvider)"/> / DDL apply call.
    /// </summary>
    private readonly Dictionary<string, ITableCatalog> _backends;

    /// <summary>
    /// Internal mutable access to the schema-to-backend routing table for
    /// per-statement executors that own the CREATE / DROP SCHEMA lifecycle
    /// (see <see cref="SchemaExecutor"/>).
    /// </summary>
    internal Dictionary<string, ITableCatalog> Backends => _backends;

    /// <summary>
    /// Session-scoped <c>search_path</c> for resolving unqualified table
    /// references. Defaults to <c>["public", "system"]</c> — so
    /// <c>SELECT * FROM udfs</c> walks to <c>system.udfs</c>. Mutated
    /// atomically by <c>SET search_path = …</c>; reads return the
    /// immutable list captured at the point of read so in-flight queries
    /// that captured an earlier snapshot aren't affected by a concurrent
    /// SET. PG semantics.
    /// </summary>
    private volatile IReadOnlyList<string> _searchPath = new[] { "public", "system" };

    /// <summary>
    /// The current session <c>search_path</c>. Reads atomically; the
    /// returned list is immutable, so callers can capture a stable
    /// snapshot for the rest of their query. Mutated only by
    /// <see cref="SchemaExecutor.SetSearchPath"/>.
    /// </summary>
    public IReadOnlyList<string> SearchPath => _searchPath;

    /// <summary>
    /// Returns the user-created index descriptors for <paramref name="tableName"/>,
    /// or <see langword="null"/> when the table has no indexes (or doesn't exist).
    /// Used by the <c>information_schema.table_constraints</c> /
    /// <c>information_schema.key_column_usage</c> views to surface UNIQUE
    /// constraints alongside PRIMARY KEY constraints.
    /// </summary>
    internal IReadOnlyList<IndexDescriptor>? GetTableIndexes(string tableName)
    {
        QualifiedName qn = QualifiedName.Parse(tableName);
        return TryResolveBackend(qn.Schema, out ITableCatalog? backend)
            ? backend.GetTableIndexes(qn)
            : null;
    }

    /// <summary>
    /// Returns the user-supplied PRIMARY KEY constraint name for
    /// <paramref name="tableName"/>, or the derived default
    /// (<c>&lt;table&gt;_pkey</c>) when no custom name was supplied.
    /// Used by <c>information_schema.table_constraints</c> and by
    /// <c>DROP CONSTRAINT</c> name-matching.
    /// </summary>
    internal string GetPrimaryKeyConstraintName(string tableName)
    {
        QualifiedName qn = QualifiedName.Parse(tableName);
        if (TryResolveBackend(qn.Schema, out ITableCatalog? backend)
            && backend.GetCustomPrimaryKeyConstraintName(qn) is { } custom)
        {
            return custom;
        }
        // Derive PG-default `<unqualified-table>_pkey`. Strip the schema
        // so the constraint name reads as users_pkey, not public.users_pkey.
        return Providers.InformationSchemaTableConstraintsProvider
            .PrimaryKeyConstraintName(qn.Name);
    }

    /// <summary>
    /// Report from the catalog file's load on construction. <see langword="null"/>
    /// when no <c>catalogPath</c> was supplied. Hosts can surface
    /// <see cref="CatalogStoreLoadReport.Warnings"/> in their startup logs so a
    /// user notices a corrupt or skipped UDF instead of silently missing it.
    /// </summary>
    public CatalogStoreLoadReport? CatalogLoadReport { get; }

    /// <summary>
    /// The function registry used by <see cref="PlanAsync(string)"/> for SQL planning.
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
    /// executor on every <c>CALL proc.X(...)</c> call site to find the
    /// descriptor whose body should run.
    /// </summary>
    public ProcedureRegistry Procedures => _procedures;

    /// <summary>
    /// Builds a <see cref="QualifiedName"/> from a DDL statement that
    /// carries both an explicit <c>SchemaName</c> (the parsed
    /// <c>schema.table</c> qualifier) and a <c>TableName</c>. When the
    /// schema is supplied explicitly it wins; otherwise the
    /// <see cref="SchemaResolver"/> walks the current
    /// <see cref="SearchPath"/> for an existing table. If no match is
    /// found, the first search_path entry is returned as a best-guess
    /// so the subsequent backend lookup fails consistently (callers'
    /// IF EXISTS branches handle that uniformly).
    /// </summary>
    internal QualifiedName ResolveDdlName(string? explicitSchema, string tableName)
    {
        SchemaResolver resolver = new(this, _searchPath);
        resolver.TryResolve(explicitSchema, tableName, out QualifiedName resolved);
        return resolved;
    }

    /// <summary>
    /// Picks the first DDL-capable schema on the current search_path,
    /// or <see langword="null"/> when none qualifies. Used for the
    /// existence pre-check inside <see cref="TableExecutor.CreateTableAsync"/> —
    /// it has to know the prospective target schema before
    /// <c>ResolveForCreate</c> is invoked.
    /// </summary>
    public string? FirstWritableSchema()
    {
        foreach (string schema in _searchPath)
        {
            if (_backends.TryGetValue(schema, out ITableCatalog? backend) && backend.SupportsDdl)
            {
                return schema;
            }
        }

        return null;
    }

    internal static bool IsBuiltinSchema(string schema)
        => string.Equals(schema, "public", StringComparison.OrdinalIgnoreCase)
        || string.Equals(schema, "system", StringComparison.OrdinalIgnoreCase)
        || string.Equals(schema, "information_schema", StringComparison.OrdinalIgnoreCase)
        || string.Equals(schema, "datum_catalog", StringComparison.OrdinalIgnoreCase)
        || string.Equals(schema, "models", StringComparison.OrdinalIgnoreCase);

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
    /// the engine knows how to execute. DDL is applied to the catalog as a
    /// side effect and produces an empty result plan; this keeps the API
    /// surface a single entry point for the host.
    /// </para>
    /// <para>
    /// Before planning, every <c>udf.X(...)</c> call site in the parsed AST
    /// is inlined via <see cref="UdfInliner"/> so the planner sees only the
    /// substituted bodies. UDFs are macros — by plan time, no UDF call
    /// remains in the tree.
    /// </para>
    /// <para>
    /// The catalog exposes the async entry only — DML executors run on their
    /// natural async path without thread-pool-blocking sync-over-async
    /// bridges. Synchronous test code reaches this API via the
    /// <c>TableCatalogTestExtensions.Plan(...)</c> extension methods in the
    /// test assembly.
    /// </para>
    /// </remarks>
    public Task<IQueryPlan> PlanAsync(string sql)
    {
        Statement statement = SqlParser.ParseStatement(sql);
        return PlanAsync(statement, sql);
    }

    /// <summary>
    /// Plans an already-parsed <see cref="Statement"/> against this catalog.
    /// Same dispatch as <see cref="PlanAsync(string)"/> minus the parsing
    /// step; useful for callers that have built a statement programmatically
    /// (e.g. the procedural batch executor synthesising
    /// <c>SELECT &lt;expr&gt;</c> for DECLARE / SET initialisers).
    /// </summary>
    public Task<IQueryPlan> PlanAsync(Statement statement)
        => PlanAsync(statement, sourceText: null);

    /// <summary>
    /// Async statement dispatch — the canonical planning entry point. DDL
    /// applies as a side effect (returning <see cref="EmptyQueryPlan"/>);
    /// queries return a plan whose batches stream on
    /// <see cref="IQueryPlan.ExecuteAsync(CancellationToken)"/>; DML executes
    /// inline and returns either <see cref="EmptyQueryPlan"/> or a
    /// <c>RETURNING</c> plan.
    /// </summary>
    /// <remarks>
    /// Threads <paramref name="sourceText"/> through for DDL statements that
    /// need to round-trip the original SQL text through the catalog file
    /// (procedural <c>CREATE FUNCTION</c> / <c>CREATE PROCEDURE</c> bodies
    /// don't have a faithful AST formatter, so without the slice they fall
    /// back to a synthesised header that won't reparse on catalog reload).
    /// Callers that parsed via <c>SqlParser.ParseBatchWithText</c> already
    /// have the per-statement slice; callers that built the AST
    /// programmatically pass <see langword="null"/>.
    /// </remarks>
    public async Task<IQueryPlan> PlanAsync(Statement statement, string? sourceText)
    {
        switch (statement)
        {
            case QueryStatement queryStatement:
                return PlanQuery(queryStatement.Query);

            case CreateFunctionStatement create:
                _routines.ApplyCreateFunction(create, sourceText);
                return EmptyQueryPlan.Instance;

            case DropFunctionStatement drop:
                _routines.ApplyDropFunction(drop, sourceText);
                return EmptyQueryPlan.Instance;

            case CreateProcedureStatement create:
                _routines.ApplyCreateProcedure(create, sourceText);
                return EmptyQueryPlan.Instance;

            case DropProcedureStatement drop:
                _routines.ApplyDropProcedure(drop, sourceText);
                return EmptyQueryPlan.Instance;

            case CreateModelStatement createModel:
                await _routines.ApplyCreateModelAsync(createModel, sourceText).ConfigureAwait(false);
                return EmptyQueryPlan.Instance;

            case DropModelStatement dropModel:
                _routines.ApplyDropModel(dropModel, sourceText);
                return EmptyQueryPlan.Instance;

            case CallStatement call:
                return PlanCall(call);

            case CreateTableStatement createTable:
                return await TableExecutor.CreateTableAsync(this, createTable, sourceText).ConfigureAwait(false);

            case DropTableStatement dropTable:
                return TableExecutor.DropTable(this, dropTable, sourceText);

            case CreateSchemaStatement createSchema:
                return SchemaExecutor.CreateSchema(this, createSchema, sourceText);

            case DropSchemaStatement dropSchema:
                return SchemaExecutor.DropSchema(this, dropSchema, sourceText);

            case SetSearchPathStatement setSearchPath:
                return SchemaExecutor.SetSearchPath(this, setSearchPath);

            case CreateIndexStatement createIndex:
                return await IndexExecutor.CreateIndexAsync(this, createIndex, sourceText).ConfigureAwait(false);

            case DropIndexStatement dropIndex:
                return IndexExecutor.DropIndex(this, dropIndex, sourceText);

            case ReindexTableStatement reindex:
                return await IndexExecutor.ReindexAsync(this, reindex).ConfigureAwait(false);

            case AnalyzeTableStatement analyze:
                return await AnalyzeExecutor.ExecuteAsync(this, analyze).ConfigureAwait(false);

            case AlterTableAddColumnStatement alterAdd:
                if (alterAdd.TableIfExists && !TryGetTable(ResolveDdlName(alterAdd.SchemaName, alterAdd.TableName).ToString(), out _)) return EmptyQueryPlan.Instance;
                return await AlterTableExecutor.AddColumnAsync(this, alterAdd, sourceText).ConfigureAwait(false);

            case AlterTableDropColumnStatement alterDrop:
                if (alterDrop.TableIfExists && !TryGetTable(ResolveDdlName(alterDrop.SchemaName, alterDrop.TableName).ToString(), out _)) return EmptyQueryPlan.Instance;
                return AlterTableExecutor.DropColumn(this, alterDrop, sourceText);

            case AlterTableDropConstraintStatement alterDropConstraint:
                if (alterDropConstraint.TableIfExists && !TryGetTable(ResolveDdlName(alterDropConstraint.SchemaName, alterDropConstraint.TableName).ToString(), out _)) return EmptyQueryPlan.Instance;
                return await AlterTableExecutor.DropConstraintAsync(this, alterDropConstraint, sourceText).ConfigureAwait(false);

            case AlterTableAlterColumnDropStatement alterColumnDrop:
                if (alterColumnDrop.TableIfExists && !TryGetTable(ResolveDdlName(alterColumnDrop.SchemaName, alterColumnDrop.TableName).ToString(), out _)) return EmptyQueryPlan.Instance;
                return await AlterTableExecutor.AlterColumnDropAsync(this, alterColumnDrop, sourceText).ConfigureAwait(false);

            case InsertStatement insert:
                return await InsertExecutor.ExecuteAsync(this, insert).ConfigureAwait(false);

            case UpdateStatement update:
                await UpdateExecutor.ExecuteAsync(this, update).ConfigureAwait(false);
                return EmptyQueryPlan.Instance;

            case DeleteStatement delete:
                await DeleteExecutor.ExecuteAsync(this, delete).ConfigureAwait(false);
                return EmptyQueryPlan.Instance;

            default:
                throw new NotSupportedException(
                    $"Statement type '{statement.GetType().Name}' is not yet supported by PlanAsync. " +
                    $"Use the dedicated APIs (e.g. AddFile for file registration) or extend PlanAsync to dispatch this statement.");
        }
    }

    private IQueryPlan PlanCall(CallStatement call)
    {
        // Lower CALL udf.fn(args) to SELECT udf.fn(args) — a tableless query
        // against the implicit single-row source. UDF inlining and model hoisting
        // apply exactly as they would for an explicit SELECT, so UDFs, model
        // invocations, and template strings in the body all work unchanged.
        SelectStatement syntheticSelect = new(
            Columns: [new SelectColumn(call.Call)]);
        QueryExpression syntheticQuery = new SelectQueryExpression(syntheticSelect);
        return PlanQuery(syntheticQuery);
    }

    internal IQueryPlan PlanQuery(QueryExpression query)
    {
        QueryExpression inlined = UdfInliner.Inline(query, _udfs, SearchPath, _procedures);
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

    /// <summary>
    /// Process-scoped registry of SQL-defined models — entries created by
    /// <c>CREATE MODEL</c>. Parallel to <see cref="UdfRegistry"/>; surfaced
    /// separately so <c>system.models</c> stays distinct from
    /// <c>system.udfs</c>. Inherited from a parent catalog when nested so
    /// child catalogs see the same registrations without duplicating them.
    /// </summary>
    public ModelRegistry DeclaredModels => Parent?.DeclaredModels ?? _declaredModels;

    /// <summary>
    /// The inference dispatcher used by <c>CREATE MODEL</c> to load ONNX
    /// sessions at registration time. <see langword="null"/> when the host
    /// has not wired an inference backend — in that case <c>CREATE MODEL</c>
    /// throws a clear error rather than silently failing later. Inherited
    /// from a parent catalog when nested.
    /// </summary>
    public Inference.IInferenceDispatcher? InferenceDispatcher
    {
        get => _inferenceDispatcher ?? Parent?.InferenceDispatcher;
        set
        {
            if (Parent is not null && value is not null)
            {
                throw new InvalidOperationException(
                    "InferenceDispatcher cannot be set on a nested table catalog — set it on the root.");
            }
            _inferenceDispatcher = value;
        }
    }

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

    /// <summary>
    /// Gets the total number of tables registered in this catalog, including
    /// those inherited from the parent catalog if present.
    /// </summary>
    public int Count => _flatFile.Count + _system.Count + _virtual.Count + (Parent?.Count ?? 0);

    /// <summary>
    /// Routes <paramref name="schema"/> to its owning backend, or returns
    /// <see langword="false"/> when no backend is mounted for that schema.
    /// Used by lookup / Add / DDL paths so the facade can dispatch
    /// uniformly.
    /// </summary>
    internal bool TryResolveBackend(string schema, [NotNullWhen(true)] out ITableCatalog? backend)
        => _backends.TryGetValue(schema, out backend);

    /// <summary>
    /// Schema-aware lookup using a pre-built <see cref="QualifiedName"/>.
    /// Bypasses the string indexer's parse step. Used by
    /// <see cref="SchemaResolver"/> in the hot path.
    /// </summary>
    internal bool TryGetTable(QualifiedName name, [NotNullWhen(true)] out ITableProvider? provider)
    {
        if (TryResolveBackend(name.Schema, out ITableCatalog? backend)
            && backend.TryGetTable(name, out provider))
        {
            return true;
        }
        if (Parent is not null)
        {
            return Parent.TryGetTable(name, out provider);
        }
        provider = null;
        return false;
    }

    /// <summary>
    /// Returns the backend that owns <paramref name="schema"/>, or
    /// <see langword="false"/> when no backend is mounted there. Exposed
    /// to <see cref="SchemaResolver"/> so it can pick DDL-capable
    /// schemas during <c>CREATE TABLE</c> resolution.
    /// </summary>
    internal bool TryFindBackend(string schema, [NotNullWhen(true)] out ITableCatalog? backend)
        => _backends.TryGetValue(schema, out backend);

    /// <summary>
    /// Replaces the session <c>search_path</c>. Used by
    /// <c>SET search_path = …</c>. Validates that every schema in the
    /// new path is mounted; throws on the first unknown schema.
    /// </summary>
    /// <remarks>
    /// PG accepts unknown schemas silently with a warning; we error
    /// upfront so typos can't quietly hide tables from resolution.
    /// </remarks>
    internal void SetSearchPath(IReadOnlyList<string> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        foreach (string schema in schemas)
        {
            if (!_backends.ContainsKey(schema))
            {
                throw new InvalidOperationException(
                    $"SET search_path: schema '{schema}' does not exist.");
            }
        }
        // Snapshot to a fresh immutable list so callers that captured the
        // old reference keep their view.
        _searchPath = schemas.ToArray();
    }

    /// <summary>
    /// Returns <see langword="true"/> if a table with the given name is registered
    /// in this catalog or its parent; otherwise <see langword="false"/>.
    /// </summary>
    /// <param name="name">The name of the table to check.</param>
    /// <returns><see langword="true"/> if the table exists; otherwise <see langword="false"/>.</returns>
    public bool HasTable(string name)
    {
        QualifiedName qn = QualifiedName.Parse(name);
        if (TryResolveBackend(qn.Schema, out ITableCatalog? backend)
            && backend.TryGetTable(qn, out _))
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
        QualifiedName qn = QualifiedName.Parse(name);
        if (TryResolveBackend(qn.Schema, out ITableCatalog? backend)
            && backend.TryGetTable(qn, out provider))
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
            QualifiedName qn = QualifiedName.Parse(name);
            if (TryResolveBackend(qn.Schema, out ITableCatalog? backend)
                && backend.TryGetTable(qn, out ITableProvider? provider))
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
        QualifiedName qn = QualifiedName.Parse(tableDescriptor.Name);
        if (Parent is TableCatalog parent && parent.HasTable(tableDescriptor.Name))
        {
            throw new ArgumentException($"A table with the name '{tableDescriptor.Name}' is already registered in the parent catalog.");
        }
        if (!TryResolveBackend(qn.Schema, out ITableCatalog? backend))
        {
            throw new ArgumentException(
                $"No catalog backend is mounted for schema '{qn.Schema}' " +
                $"(table '{tableDescriptor.Name}').");
        }
        // v2 reader validates magic + version inside its constructor via
        // DatumFileReaderV2.Open. Files written by older format versions
        // throw InvalidDataException at open time.
        DatumFileTableProviderV2 provider = new(tableDescriptor, Pool);
        try
        {
            return backend.Add(provider);
        }
        catch
        {
            (provider as IDisposable)?.Dispose();
            throw;
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
        ArgumentNullException.ThrowIfNull(tableProvider);
        QualifiedName qn = tableProvider.QualifiedName;
        if (Parent is TableCatalog parent
            && parent.TryResolveBackend(qn.Schema, out ITableCatalog? parentBackend)
            && parentBackend.TryGetTable(qn, out _))
        {
            throw new ArgumentException($"A table with the name '{qn}' is already registered in the parent catalog.");
        }
        if (!TryResolveBackend(qn.Schema, out ITableCatalog? backend))
        {
            throw new ArgumentException(
                $"No catalog backend is mounted for schema '{qn.Schema}' " +
                $"(provider '{qn}').");
        }
        return backend.Add(tableProvider);
    }

    /// <summary>
    /// Removes a previously registered table from the catalog. Delegates
    /// to <see cref="FlatFileCatalog.DropTable"/>, which also deletes the
    /// backing <c>.datum</c> file and sidecars when the table is
    /// persistent. Use <see cref="TableExecutor.DropTable"/> for full DROP TABLE
    /// semantics (IF EXISTS, error reporting, manifest persistence).
    /// </summary>
    public void Remove(string tableName)
    {
        // NOTE: you can't drop from the parent catalog.
        QualifiedName qn = QualifiedName.Parse(tableName);
        if (TryResolveBackend(qn.Schema, out ITableCatalog? backend) && backend.SupportsDdl)
        {
            backend.DropTable(qn);
        }
        // Read-only backends silently ignore Remove — matches the old
        // permissive behaviour of the public API.
    }


    /// <inheritdoc />
    public IEnumerator<ITableProvider> GetEnumerator()
    {
        HashSet<QualifiedName> seen = new();
        foreach (ITableProvider provider in _flatFile.ListTables())
        {
            seen.Add(provider.QualifiedName);
            yield return provider;
        }
        foreach (ITableProvider provider in _system.ListTables())
        {
            seen.Add(provider.QualifiedName);
            yield return provider;
        }
        foreach (ITableProvider provider in _virtual.ListTables())
        {
            seen.Add(provider.QualifiedName);
            yield return provider;
        }

        if (Parent is not null)
        {
            foreach (ITableProvider provider in Parent)
            {
                if (!seen.Contains(provider.QualifiedName))
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
        // Disposes all locally-registered providers via each backend.
        // Parent-catalog providers remain owned by the parent.
        _flatFile.Dispose();
        _system.Dispose();
        _virtual.Dispose();
    }


}
