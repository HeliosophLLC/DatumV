using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Heliosoph.DatumV.Catalog.Executors;
using Heliosoph.DatumV.Catalog.Plans;
using Heliosoph.DatumV.Catalog.Providers;
using Heliosoph.DatumV.Catalog.Registries;
using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.ModelLibrary;
using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;
using Heliosoph.DatumV.Pooling;
using Heliosoph.DatumV.Serialization;

namespace Heliosoph.DatumV.Catalog;

/// <summary>
/// Registry of named tables and their associated providers.
/// Resolves table names referenced in SQL FROM clauses to
/// <see cref="TableDescriptor"/> instances and creates the
/// appropriate <see cref="ITableProvider"/> for each.
/// </summary>
public sealed class TableCatalog : IDisposable, IEnumerable<ITableProvider>, ICatalogActiveVersionLookup
{
    /// <summary>
    /// <see cref="ICatalogActiveVersionLookup.GetActiveVersion"/>: returns
    /// the catalog version of the currently-installed bare-form row for
    /// <paramref name="catalogId"/>, or <see langword="null"/> when no
    /// bare-form row exists. Bare rows have <c>PinnedAs == null</c> —
    /// pinned-form rows coexist on the same catalog id but intentionally
    /// don't drive "active." Replaces the previous
    /// <c>&lt;DATUM_MODELS&gt;/&lt;id&gt;/active</c> text-pointer file.
    /// </summary>
    public string? GetActiveVersion(string catalogId)
    {
        foreach (Registries.ModelDescriptor d in DeclaredModels.Entries)
        {
            if (d.PinnedAs is null
                && d.CatalogId is not null
                && string.Equals(d.CatalogId, catalogId, StringComparison.Ordinal))
            {
                return d.CatalogVersion;
            }
        }
        return null;
    }

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
        this.Events = new CatalogEvents();
        this.Pool = pool;
        this.SidecarRegistry = new();
        this.DeclaredModels = new();
        this.Functions = FunctionRegistry.CreateDefault();
        // Wire the model-catalog fallback so unhoisted models.X(...) calls
        // (procedural UDF bodies, CALL, etc.) resolve through this catalog.
        // The closure follows the parent-chain getter so child catalogs
        // inherit the root's models without duplicating registrations.
        this.Functions.SetModelCatalogResolver(() => Models);
        this.Udfs = new UdfRegistry();
        this.Procedures = new ProcedureRegistry();
        this.Views = new ViewRegistry();
        this.CatalogStore = catalogPath is null ? null : new CatalogStore(catalogPath);
        this.Routines = new RoutineRegistrar(this, Udfs, Procedures, Views, Functions, CatalogStore);

        // Construct the user-data (FlatFile) backend. The persist callback
        // wraps CatalogStore.Save with the facade's UDF / Procedure
        // registries — the backend doesn't need to know about those.
        string? catalogDirectory = CatalogStore is null
            ? null
            : global::System.IO.Path.GetDirectoryName(CatalogStore.Path) ?? Environment.CurrentDirectory;
        this.CatalogDirectory = catalogDirectory;
        this.FlatFileCatalog = new FlatFileCatalog(
            pool,
            SidecarRegistry,
            catalogDirectory,
            persistManifest: () => CatalogStore?.Save(Udfs, Procedures, DeclaredModels, Views));

        // Construct the read-only backends. System holds host-attached
        // projections; Virtual holds the SQL-standard / engine-introspection
        // views. They reject CREATE / DROP / CREATE INDEX but accept Add()
        // so the host can attach providers (e.g. ModelsTableProvider).
        this.SystemCatalog = new ReadOnlyTableCatalog(new[] { "system" });
        this.VirtualCatalog = new ReadOnlyTableCatalog(new[] { "information_schema", "datum_catalog" });

        // Models is a real, non-droppable schema mounted alongside the
        // other built-ins (S9). The schema is empty as a table namespace —
        // <c>models.X(...)</c> resolves lazily through
        // <see cref="FunctionRegistry.TryResolveModelFunction"/> against
        // the host-attached <see cref="ModelCatalog"/>, not via table
        // lookups. Mounting it as a backend makes the schema visible to
        // <c>SET search_path</c>, <c>information_schema.schemata</c>, and
        // diagnostics without changing the call-resolution path.
        this.ModelCatalog = new ReadOnlyTableCatalog(new[] { "models" });

        // Schema → backend map. Lookups, DDL, and Add() route through this.
        // Public is the home for user data; system/info_schema/datum_catalog/
        // models are read-only projections.
        this.Backends = new Dictionary<string, ITableCatalog>(StringComparer.OrdinalIgnoreCase)
        {
            ["public"] = FlatFileCatalog,
            ["system"] = SystemCatalog,
            ["information_schema"] = VirtualCatalog,
            ["datum_catalog"] = VirtualCatalog,
            ["models"] = ModelCatalog,
        };

        // Auto-register intrinsic system + virtual tables. information_schema
        // providers take `this` because they enumerate the catalog at scan
        // time; construction is safe because no scan occurs here.
        SystemCatalog.Add(new Providers.UdfsTableProvider(pool, Udfs));
        SystemCatalog.Add(new Providers.ProceduresTableProvider(pool, Procedures));
        SystemCatalog.Add(new Providers.ViewsTableProvider(pool, Views));
        SystemCatalog.Add(new Providers.SystemFilesProvider(pool, catalogDirectory, this));
        VirtualCatalog.Add(new Providers.InformationSchemaTablesProvider(pool, this));
        VirtualCatalog.Add(new Providers.InformationSchemaColumnsProvider(pool, this));
        VirtualCatalog.Add(new Providers.InformationSchemaSchemataProvider(pool));
        VirtualCatalog.Add(new Providers.InformationSchemaTableConstraintsProvider(pool, this));
        VirtualCatalog.Add(new Providers.InformationSchemaKeyColumnUsageProvider(pool, this));
        VirtualCatalog.Add(new Providers.InformationSchemaViewsProvider(pool, Views));
        VirtualCatalog.Add(new Providers.DatumCatalogFunctionsProvider(pool, Functions));
        VirtualCatalog.Add(new Providers.DatumCatalogFunctionParametersProvider(pool, Functions));
        SystemCatalog.Add(new Providers.TaskContractsTableProvider(pool));
        VirtualCatalog.Add(new Providers.DatumCatalogStatisticsProvider(pool, this));
        VirtualCatalog.Add(new Providers.DatumCatalogIndexesProvider(pool, this));
        VirtualCatalog.Add(new Providers.DatumCatalogInteractionsProvider(pool, this));

        // Replay any persisted UDFs / procedures into the registries.
        // Done after the system table registrations so the rehydrated
        // entries are immediately visible to introspection.
        if (CatalogStore is not null)
        {
            // Wire the tables provider before any save fires so the
            // file-write path always sees the catalog's current table
            // set (PR10a). The backend snapshots its own state.
            CatalogStore.SetFlatFileBackendStateProvider(FlatFileCatalog.SnapshotBackendState);

            CatalogStoreLoadReport report = CatalogStore.Load(Udfs, Procedures, Views);
            CatalogLoadReport = report;

            // The Load() call writes straight into _udfs without going
            // through ApplyCreateFunction, so procedural adapters in the
            // scalar registry haven't been wired yet. Reconcile them here so
            // a freshly opened catalog can immediately invoke any persisted
            // procedural UDF.
            Routines.SyncProceduralAdaptersFromRegistry();

            // Replay persisted tables. The FlatFile backend owns its own
            // state shape; it handles file resolution, provider
            // construction, and per-table tracking dicts.
            if (CatalogStore.LoadedFlatFileBackendState is { } flatFileState)
            {
                FlatFileCatalog.LoadBackendState(flatFileState);
            }
        }
    }

    internal Pool Pool { get; }

    /// <summary>
    /// Absolute path to the directory holding <c>.datum-catalog.json</c>
    /// and the conventional subdirs (<c>data/</c>, <c>udfs/</c>,
    /// <c>procedures/</c>, <c>models/</c>). <see langword="null"/> when
    /// the catalog is in-memory (no catalog path supplied to the
    /// constructor). Hosts use this to anchor a file-system watcher so
    /// out-of-band edits to the catalog tree (VS Code save, git checkout)
    /// can drive UI refreshes.
    /// </summary>
    public string? CatalogDirectory { get; }

    /// <summary>
    /// Catalog-change event bus. Subscribers attach to typed events
    /// (<c>FunctionCreated</c>, <c>TableDropped</c>, etc.) and are invoked
    /// after the underlying DDL commit. See <see cref="CatalogEvents"/>
    /// for subscriber discipline and the parent-drop cascade rule.
    /// </summary>
    public CatalogEvents Events { get; }

    private CatalogStore? CatalogStore { get; }

    internal RoutineRegistrar Routines { get; }

    /// <summary>
    /// Internal accessor for the user-data backend so per-statement
    /// executors (see <see cref="IndexExecutor"/>) can reach the same
    /// FlatFile-only operations the old in-class apply methods used.
    /// </summary>
    internal FlatFileCatalog FlatFileCatalog { get; }

    /// <summary>
    /// System-projection backend: owns the <c>system</c> schema
    /// (<c>system.udfs</c>, <c>system.procedures</c>, and
    /// <c>system.models</c> when the host attaches it). Read-only for DDL;
    /// providers are host-attached.
    /// </summary>
    private ReadOnlyTableCatalog SystemCatalog { get; }

    /// <summary>
    /// Virtual-projection backend: owns the SQL-standard
    /// <c>information_schema</c> and engine-introspection
    /// <c>datum_catalog</c> schemas. Read-only for DDL; providers are
    /// constructed alongside the catalog at startup.
    /// </summary>
    private ReadOnlyTableCatalog VirtualCatalog { get; }

    /// <summary>
    /// Models-namespace backend (S9). The schema is empty as a table
    /// namespace — <c>models.X(...)</c> resolves through the function
    /// registry's lazy <c>ModelCatalog</c> resolver, not via table
    /// lookups. Mounting it as a backend makes the schema visible to
    /// <c>SET search_path</c>, <c>information_schema.schemata</c>, and
    /// DROP-rejection without coupling the model dispatch to the
    /// schema router.
    /// </summary>
    private ReadOnlyTableCatalog ModelCatalog { get; }

    /// <summary>
    /// Schema-to-backend routing table. The facade consults this for
    /// every <see cref="TryGetTable(string, out ITableProvider?)"/> /
    /// <see cref="Add(ITableProvider)"/> / DDL apply call.
    /// </summary>
    internal Dictionary<string, ITableCatalog> Backends { get; }

    /// <summary>
    /// Optional dataset pre-flight source. Hosts that ship a dataset
    /// surface attach a <see cref="Execution.IPreFlightDatasetSource"/>
    /// here so <c>FROM &lt;schema&gt;.&lt;table&gt;</c> references against the
    /// dataset manifest trigger an install-required block at parse time
    /// instead of a planner "table not found" later.
    /// </summary>
    public Execution.IPreFlightDatasetSource? DatasetPreFlightSource { get; set; }

    /// <summary>
    /// Registers <paramref name="backend"/> under <paramref name="schema"/> so
    /// SQL references to <c>&lt;schema&gt;.&lt;table&gt;</c> route to it. Used by
    /// host-side init services (dataset catalog binder, future bind-on-boot
    /// extensions) to mount schemas after the core catalog has constructed.
    /// One backend instance may be mounted under multiple schemas — the
    /// router compares schema names, not backend identity.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A backend is already mounted under <paramref name="schema"/>.
    /// </exception>
    public void MountSchemaBackend(string schema, ITableCatalog backend)
    {
        ArgumentException.ThrowIfNullOrEmpty(schema);
        ArgumentNullException.ThrowIfNull(backend);
        if (Backends.ContainsKey(schema))
        {
            throw new ArgumentException(
                $"A catalog backend is already mounted under schema '{schema}'.");
        }
        Backends[schema] = backend;
    }

    /// <summary>
    /// The current session <c>search_path</c>. Reads atomically; the
    /// returned list is immutable, so callers can capture a stable
    /// snapshot for the rest of their query. Mutated only by
    /// <see cref="SchemaExecutor.SetSearchPath"/>.
    /// </summary>
    public IReadOnlyList<string> SearchPath => _searchPath;

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
    public FunctionRegistry Functions { get; }

    /// <summary>
    /// Registry of user-defined scalar functions (macros) registered against
    /// this catalog via <c>CREATE FUNCTION</c>. The planner consults this at
    /// plan time to inline every <c>udf.X(...)</c> call site. Falls through
    /// to the parent catalog when nested, so a session-level catalog inherits
    /// global UDFs registered on its root.
    /// </summary>
    public UdfRegistry Udfs { get; }

    /// <summary>
    /// Registry of named procedural blocks registered against this catalog
    /// via <c>CREATE PROCEDURE</c>. Consulted by the procedural batch
    /// executor on every <c>CALL proc.X(...)</c> call site to find the
    /// descriptor whose body should run.
    /// </summary>
    public ProcedureRegistry Procedures { get; }

    /// <summary>
    /// Registry of named SQL views registered via <c>CREATE VIEW</c>. The
    /// source planner consults this on every <c>TableReference</c>: a
    /// matching qualified name expands inline as a subquery substitution,
    /// before any table-provider lookup.
    /// </summary>
    public ViewRegistry Views { get; }

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
    public SidecarRegistry SidecarRegistry { get; }

    /// <summary>
    /// Server-wide model catalog. <see langword="null"/> until set by the host
    /// (typically at startup via <c>ModelHost.AttachTo</c>); inherited from a
    /// parent catalog when nested. Held on the table catalog so query planning
    /// has uniform access to it without threading a separate parameter through
    /// every entry point.
    /// </summary>
    public Models.ModelCatalog? Models { get; set; }

    /// <summary>
    /// Catalog-declared model vocabulary used by parse-time pre-flight to
    /// distinguish "this identifier is in the catalog, just not installed
    /// yet" from "this identifier is a typo." <see langword="null"/>
    /// when the host did not ship a catalog manifest; pre-flight then
    /// degrades to function-name typo hints only.
    /// </summary>
    public ModelLibrary.ICatalogVocabulary? CatalogVocabulary { get; set; }

    /// <summary>
    /// Process-scoped registry of SQL-defined models — entries created by
    /// <c>CREATE MODEL</c>. Parallel to <see cref="UdfRegistry"/>; surfaced
    /// separately so <c>system.models</c> stays distinct from
    /// <c>system.udfs</c>. Inherited from a parent catalog when nested so
    /// child catalogs see the same registrations without duplicating them.
    /// </summary>
    public ModelRegistry DeclaredModels { get; }

    /// <summary>
    /// The inference dispatcher used by <c>CREATE MODEL</c> to load ONNX
    /// sessions at registration time. <see langword="null"/> when the host
    /// has not wired an inference backend — in that case <c>CREATE MODEL</c>
    /// throws a clear error rather than silently failing later. Inherited
    /// from a parent catalog when nested.
    /// </summary>
    public Inference.IInferenceDispatcher? InferenceDispatcher { get; set; }

    /// <summary>
    /// Optional tracer for <c>models.X(...)</c> invocations. Set by hosts
    /// that want to observe per-dispatch shape + timing — the interactive
    /// shell wires this up via <c>.trace on</c>; production deployments
    /// can attach metric-emitting or structured-logging implementations.
    /// <see cref="Plans.SelectPlan"/> reads this value when constructing each
    /// query's <see cref="Heliosoph.DatumV.Execution.ExecutionContext"/>, so
    /// toggling at runtime affects subsequently planned queries.
    /// </summary>
    public Heliosoph.DatumV.Execution.IModelInvocationTracer? ModelTracer { get; set; }

    /// <summary>
    /// Gets the total number of tables registered in this catalog, including
    /// those inherited from the parent catalog if present.
    /// </summary>
    public int Count => FlatFileCatalog.Count + SystemCatalog.Count + VirtualCatalog.Count;

    /// <summary>
    /// Re-applies every <c>CREATE MODEL</c> statement persisted by a
    /// prior process. Hosts call this once at startup, after the inference
    /// dispatcher and models directory are wired, so SQL-defined models
    /// survive a restart with the same registration semantics as
    /// <see cref="UdfRegistry"/> entries (which rehydrate synchronously
    /// inside the constructor).
    /// </summary>
    /// <returns>
    /// A report of how many model entries were rehydrated, how many were
    /// skipped, and any warnings collected along the way. Empty when the
    /// catalog has no <see cref="CatalogStore"/> or no persisted models.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Why this isn't done inside the constructor: <c>CREATE MODEL</c>
    /// calls <c>IInferenceDispatcher.LoadBundleAsync</c>, which is async
    /// and can take real time (file IO + native session construction).
    /// Constructors can't be async; we'd be forced into
    /// <c>.GetAwaiter().GetResult()</c> with all the deadlock baggage
    /// that entails. Splitting rehydration into an explicit async entry
    /// point lets hosts schedule it on a hosted service after DI is
    /// fully assembled.
    /// </para>
    /// <para>
    /// Failure handling mirrors the UDF rehydrate path: each persisted
    /// entry is rehydrated independently; a per-entry failure (missing
    /// ONNX file, dispatcher refuses to load, parse error) logs a
    /// warning and continues with the remaining entries so one broken
    /// model doesn't keep all the others from coming back.
    /// </para>
    /// </remarks>
    public async Task<ModelRehydrationReport> RehydrateModelsAsync(
        IManifestStore? manifest = null,
        CancellationToken ct = default)
    {
        if (CatalogStore is null || CatalogStore.LoadedModelEntries.Count == 0)
        {
            return new ModelRehydrationReport(Loaded: 0, Skipped: 0, Warnings: []);
        }

        int loaded = 0;
        int skipped = 0;
        List<string> warnings = new();

        // Single context for the whole rehydration pass — every CREATE MODEL
        // reapplied below shares the same ambient state (accountant, types,
        // variable scope). Catalog rehydration has no caller context to
        // inherit from, so this context is the root.
        using Heliosoph.DatumV.Execution.ExecutionContext context = CreateExecutionContext(cancellationToken: ct);

        // Partition by row shape. Catalog-installed rows (CatalogId set)
        // resolve their source from the live manifest's installSql — edits
        // to the on-disk SQL file flow through on the next process start.
        // User-authored rows still round-trip their persisted SourceText
        // because no installSql file exists for them.
        List<PendingModelEntry> catalogRows = [];
        List<PendingModelEntry> userRows = [];
        foreach (PendingModelEntry e in CatalogStore.LoadedModelEntries)
        {
            if (e.CatalogId is not null && e.CatalogVersion is not null)
            {
                catalogRows.Add(e);
            }
            else if (e.SourceText is not null)
            {
                userRows.Add(e);
            }
            else
            {
                // Defensive: CatalogStore.Load already drops malformed rows,
                // but if a row slips through (e.g. catalog provenance fields
                // half-populated) skip it loudly rather than NRE downstream.
                skipped++;
                warnings.Add(
                    $"Skipping model '{e.Schema}.{e.Name}': persisted row has neither catalog provenance nor source text.");
            }
        }

        // Catalog rows: group by (catalog_id, version, isPinned). Each
        // distinct triple maps to one installSql execution — pinned-mode
        // groups apply the bare-→-pinned identifier rewrite first. Within
        // a group, the persisted row names tell us which identifiers we
        // expect to land; anything missing surfaces as a warning so a
        // catalog SQL edit that removed an identifier doesn't silently
        // leave a phantom registration.
        IEnumerable<IGrouping<(string CatalogId, string CatalogVersion, bool IsPinned), PendingModelEntry>> groups =
            catalogRows.GroupBy(e =>
                (CatalogId: e.CatalogId!, CatalogVersion: e.CatalogVersion!, IsPinned: e.PinnedAs is not null));
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            await RehydrateCatalogGroupAsync(
                group.Key.CatalogId,
                group.Key.CatalogVersion,
                group.Key.IsPinned,
                group.ToList(),
                manifest,
                warnings,
                context,
                ct,
                onLoaded: () => loaded++,
                onSkipped: () => skipped++).ConfigureAwait(false);
        }

        // User rows: parse the persisted CREATE MODEL source text and
        // re-apply through the standard registration path.
        foreach (PendingModelEntry entry in userRows)
        {
            ct.ThrowIfCancellationRequested();

            CreateModelStatement create;
            try
            {
                Statement parsed = SqlParser.ParseStatement(entry.SourceText!);
                if (parsed is not CreateModelStatement c)
                {
                    skipped++;
                    warnings.Add(
                        $"Skipping model '{entry.Schema}.{entry.Name}': persisted source did not parse as a CREATE MODEL statement.");
                    continue;
                }
                create = c;
            }
            catch (Exception ex)
            {
                skipped++;
                warnings.Add(
                    $"Skipping model '{entry.Schema}.{entry.Name}': source failed to parse — {ex.Message}");
                continue;
            }

            try
            {
                await Routines.ApplyCreateModelAsync(create, context, entry.SourceText!, suppressSave: true)
                    .ConfigureAwait(false);
                loaded++;
            }
            catch (Exception ex)
            {
                skipped++;
                warnings.Add(
                    $"Skipping model '{entry.Schema}.{entry.Name}': failed to rehydrate — {ex.Message}");
            }
        }

        return new ModelRehydrationReport(loaded, skipped, warnings);
    }

    /// <summary>
    /// Re-runs the installSql for one <c>(catalog_id, version, isPinned)</c>
    /// group: resolves the SQL file via <paramref name="manifest"/>, applies
    /// the pinned-identifier rewrite when <paramref name="isPinned"/> is
    /// true, sets the install context so each <c>CREATE MODEL</c> stamps
    /// its catalog provenance back onto the registered descriptor, and
    /// re-applies each statement through the standard registration path.
    /// Increments <paramref name="onLoaded"/> per persisted row that
    /// successfully landed and <paramref name="onSkipped"/> per row that
    /// didn't.
    /// </summary>
    private async Task RehydrateCatalogGroupAsync(
        string catalogId,
        string catalogVersion,
        bool isPinned,
        IReadOnlyList<PendingModelEntry> rows,
        IManifestStore? manifest,
        List<string> warnings,
        Heliosoph.DatumV.Execution.ExecutionContext context,
        CancellationToken ct,
        Action onLoaded,
        Action onSkipped)
    {
        if (manifest is null)
        {
            warnings.Add(
                $"Skipping catalog group '{catalogId}' v{catalogVersion}: no manifest store available to resolve installSql.");
            for (int i = 0; i < rows.Count; i++) { onSkipped(); }
            return;
        }

        CatalogModel? model = manifest.Manifest.Models.FirstOrDefault(
            m => string.Equals(m.Id, catalogId, StringComparison.Ordinal));
        CatalogVersion? version = model?.Versions.FirstOrDefault(
            v => string.Equals(v.Version, catalogVersion, StringComparison.Ordinal));
        if (model is null || version is null)
        {
            // Catalog entry dropped or the specific version cut removed.
            // The persisted row points at a hole; skip + warn so the user
            // sees it. Recovery is DROP MODEL or reinstall from the
            // current catalog.
            warnings.Add(
                $"Skipping catalog group '{catalogId}' v{catalogVersion}: not present in current manifest (entry or version removed).");
            for (int i = 0; i < rows.Count; i++) { onSkipped(); }
            return;
        }
        if (string.IsNullOrEmpty(version.InstallSql))
        {
            warnings.Add(
                $"Skipping catalog group '{catalogId}' v{catalogVersion}: manifest entry declares no installSql.");
            for (int i = 0; i < rows.Count; i++) { onSkipped(); }
            return;
        }

        string sqlPath = Path.GetFullPath(
            Path.Combine(manifest.ManifestDirectory, version.InstallSql));
        if (!File.Exists(sqlPath))
        {
            warnings.Add(
                $"Skipping catalog group '{catalogId}' v{catalogVersion}: installSql file not found at '{sqlPath}'.");
            for (int i = 0; i < rows.Count; i++) { onSkipped(); }
            return;
        }

        string sql;
        try
        {
            sql = await File.ReadAllTextAsync(sqlPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            warnings.Add(
                $"Skipping catalog group '{catalogId}' v{catalogVersion}: failed to read installSql at '{sqlPath}' — {ex.Message}");
            for (int i = 0; i < rows.Count; i++) { onSkipped(); }
            return;
        }
        if (isPinned)
        {
            sql = PinnedInstallSqlRewriter.Rewrite(sql, version);
        }

        IReadOnlyList<(Statement Statement, string SourceText)> statements;
        try
        {
            statements = SqlParser.ParseBatchWithText(sql);
        }
        catch (Exception ex)
        {
            warnings.Add(
                $"Skipping catalog group '{catalogId}' v{catalogVersion}: installSql failed to parse — {ex.Message}");
            for (int i = 0; i < rows.Count; i++) { onSkipped(); }
            return;
        }

        // Push install context so each CREATE MODEL re-stamps its catalog
        // provenance back onto the descriptor — keeps the row shape
        // round-trip-stable across rehydrate cycles. CurrentVersionPin
        // also drives USING-path resolution against the right version
        // folder during this group's execution.
        string? previousCatalogId = ModelInstallContext.CurrentCatalogId;
        string? previousVersionPin = ModelInstallContext.CurrentVersionPin;
        bool previousIsPinned = ModelInstallContext.CurrentInstallIsPinned;
        ModelInstallContext.CurrentCatalogId = catalogId;
        ModelInstallContext.CurrentVersionPin = catalogVersion;
        ModelInstallContext.CurrentInstallIsPinned = isPinned;

        HashSet<string> registered = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach ((Statement statement, string sourceText) in statements)
            {
                ct.ThrowIfCancellationRequested();
                if (statement is not CreateModelStatement create)
                {
                    // installSql may declare helper UDFs / procedures
                    // alongside CREATE MODEL. Those persisted in their own
                    // catalog sections (udfs, procedures) and were already
                    // rehydrated synchronously during CatalogStore.Load.
                    // Re-executing them here would double-register; skip.
                    continue;
                }
                try
                {
                    await Routines.ApplyCreateModelAsync(create, context, sourceText, suppressSave: true)
                        .ConfigureAwait(false);
                    registered.Add(create.Name);
                }
                catch (Exception ex)
                {
                    warnings.Add(
                        $"Rehydrating '{catalogId}' v{catalogVersion}: CREATE MODEL '{create.Name}' failed — {ex.Message}");
                }
            }
        }
        finally
        {
            ModelInstallContext.CurrentInstallIsPinned = previousIsPinned;
            ModelInstallContext.CurrentVersionPin = previousVersionPin;
            ModelInstallContext.CurrentCatalogId = previousCatalogId;
        }

        // Match the persisted row identifiers against what landed. Drift
        // here means the catalog SQL file was edited to remove an
        // identifier the user still has registered; the row gets dropped.
        foreach (PendingModelEntry row in rows)
        {
            if (registered.Contains(row.Name)) { onLoaded(); }
            else
            {
                onSkipped();
                warnings.Add(
                    $"Skipping model '{row.Schema}.{row.Name}': installSql for '{catalogId}' v{catalogVersion} did not register expected identifier.");
            }
        }
    }

    /// <summary>
    /// Creates an <see cref="ExecutionContext"/> parented to this catalog.
    /// The context inherits <see cref="Functions"/> and <see cref="Pool"/>
    /// from the catalog; pass overrides for memory budget, value store, type
    /// registry, accountant, video registry, or cancellation token as named
    /// arguments when entering a scope that already owns them (e.g. a
    /// <see cref="Heliosoph.DatumV.Execution.ExecutionContext"/> borrowing its accountant across child
    /// queries).
    /// </summary>
    public Heliosoph.DatumV.Execution.ExecutionContext CreateExecutionContext(
        long? memoryBudgetBytes = null,
        Arena? store = null,
        TypeRegistry? types = null,
        MemoryAccountant? accountant = null,
        VideoRegistry? videoRegistry = null,
        VariableScope? variableScope = null,
        Arena? variableStore = null,
        PrintHandler? printHandler = null,
        CancellationToken cancellationToken = default)
        => new(
            this,
            memoryBudgetBytes,
            store,
            types,
            accountant,
            videoRegistry,
            variableScope,
            variableStore,
            printHandler,
            cancellationToken)
        {
            // Snapshot the catalog's tracer into the per-query context.
            // Setting / clearing TableCatalog.ModelTracer at runtime affects
            // subsequently planned queries; queries already running keep the
            // tracer they captured at execution start.
            ModelTracer = ModelTracer,
        };

    /// <summary>
    /// Parses <paramref name="sql"/> and returns the right
    /// <see cref="PreparedSql"/> shape: a <see cref="StatementPlan"/> for
    /// a single statement, a <see cref="Plans.StatementBatch"/> for a
    /// semicolon-separated multi-statement script. Pure — no side
    /// effects until the returned unit is iterated (or its children are,
    /// for batches). Empty input is a caller error.
    /// </summary>
    /// <remarks>
    /// Use this entry when the caller doesn't know up-front whether the
    /// SQL contains one or many statements (the in-process data API
    /// dispatches through here). Callers that know they have a single
    /// statement can call <see cref="PlanAsync(string)"/> directly for
    /// a tighter return type.
    /// </remarks>
    public async Task<PreparedSql> PrepareAsync(string sql)
    {
        IReadOnlyList<(Statement Statement, string SourceText)> entries =
            SqlParser.ParseBatchWithText(sql);
        if (entries.Count == 0)
        {
            throw new ArgumentException(
                "PrepareAsync: input contained no statements.", nameof(sql));
        }
        if (entries.Count == 1)
        {
            return await PlanAsync(entries[0].Statement, entries[0].SourceText).ConfigureAwait(false);
        }
        List<(Statement, string?)> batchEntries = new(entries.Count);
        foreach ((Statement statement, string sourceText) in entries)
        {
            batchEntries.Add((statement, sourceText));
        }
        return new Plans.StatementBatch(this, batchEntries);
    }

    /// <summary>
    /// Parses and plans <paramref name="sql"/> against this catalog, returning an
    /// <see cref="StatementPlan"/> that may be inspected (<see cref="StatementPlan.ExplainTree"/>),
    /// analyzed (<see cref="StatementPlan.AnalyzeAsync"/>), or executed. Literal payloads
    /// are pre-materialized (hoisted) into a plan-scoped arena so per-row evaluation skips re-encoding.
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
    public Task<StatementPlan> PlanAsync(string sql)
    {
        Statement statement = SqlParser.ParseStatement(sql);
        return PlanAsync(statement, sql);
    }

    /// <summary>
    /// Plans an already-parsed <see cref="Statement"/>. Pure — no side
    /// effects until the returned plan is iterated.
    /// </summary>
    public Task<StatementPlan> PlanAsync(Statement statement)
        => PlanAsync(statement, sourceText: null);

    /// <summary>
    /// Canonical planning entry point. Returns an <see cref="StatementPlan"/>
    /// that has NOT yet executed any side effects. Iterating
    /// <c>StatementPlan.ExecuteAsync</c> applies them; reading
    /// <see cref="StatementPlan.ExplainTree"/> does not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For pure queries (<see cref="QueryStatement"/>, <see cref="CallStatement"/>)
    /// the returned plan is a <see cref="Plans.SelectPlan"/>. For DDL and
    /// DML statements the returned plan is the structured per-family plan
    /// (<see cref="Plans.RoutinePlan"/>, <see cref="Plans.SchemaPlan"/>,
    /// <see cref="Plans.AlterTablePlan"/>, etc.) whose
    /// <c>ExecuteImplAsync</c> applies the side effect on iteration.
    /// This makes <c>EXPLAIN CREATE TABLE</c> and <c>EXPLAIN DELETE</c>
    /// safe — they read the tree without iterating.
    /// </para>
    /// <para>
    /// Callers that want eager DDL/DML application iterate the returned
    /// plan via <see cref="ExecuteAsync(StatementPlan, CancellationToken)"/>
    /// (or its <c>DrainAsync()</c> extension when no rows are wanted).
    /// </para>
    /// </remarks>
    public async Task<StatementPlan> PlanAsync(Statement statement, string? sourceText)
    {
        // Pure queries: plan eagerly (the planner has no side effects, just builds an operator tree).
        if (statement is QueryStatement queryStatement)
        {
            return PlanQuery(queryStatement.Query);
        }
        if (statement is CallStatement call)
        {
            return PlanCall(call);
        }

        // CTAS is a composable plan: CtasPlan owns a child SelectPlan so
        // EXPLAIN walks the full SELECT subtree under the CTAS node
        // without applying any catalog mutation.
        if (statement is CreateTableAsSelectStatement ctas)
        {
            return await Plans.CtasPlan.PlanAsync(this, ctas, sourceText).ConfigureAwait(false);
        }

        // DML plans own their source plans at plan time (where applicable):
        // INSERT … SELECT carries a real child SelectPlan; UPDATE / DELETE
        // drivers stay internal to the executor. Pre-flight gating for DML
        // (PreFlightWalker) still runs eagerly here so EXPLAIN INSERT/
        // UPDATE/DELETE surfaces missing-model / missing-dataset errors
        // before the planner builds the source tree.
        if (statement is InsertStatement or UpdateStatement or DeleteStatement)
        {
            PreFlightRequirements preflight = PreFlightWalker.WalkStatement(
                statement, Models, CatalogVocabulary, Functions, DatasetPreFlightSource);
            if (preflight.Models.Count > 0
                || preflight.Datasets.Count > 0
                || preflight.Suggestions.Count > 0)
            {
                throw new PreFlightRequiredException(preflight);
            }
        }
        if (statement is InsertStatement insert)
        {
            // INSERT … SELECT composes a child source plan; VALUES /
            // DEFAULT VALUES has no source query to plan.
            StatementPlan? sourcePlan = insert.Source is InsertQuerySource queryRow
                ? PlanQuery(queryRow.Query)
                : null;
            if (insert.Returning is null)
            {
                return Plans.DmlPlan.ForInsert(this, insert, sourcePlan);
            }
            // RETURNING: compose DmlPlan + CapturedRowsSource + projection.
            // Falls back to bare DmlPlan when target resolution fails at
            // plan time, so the executor surfaces the cleaner diagnostic
            // ("table not found" / "is a view" / "read-only provider") at
            // execute time instead of double-reporting it here.
            if (TryResolveDmlTargetSchema(insert.SchemaName, insert.TableName, out Schema? targetSchema))
            {
                Plans.CapturedRowsSource capturedSource = new(this);
                Plans.DmlPlan dml = Plans.DmlPlan.ForInsert(this, insert, sourcePlan, capturedSource);
                return Plans.DmlReturningPlan.Compose(
                    Plans.DmlReturningKind.Insert, insert.TableName, targetSchema,
                    dml, capturedSource, insert.Returning, this);
            }
            return Plans.DmlPlan.ForInsert(this, insert, sourcePlan);
        }
        if (statement is UpdateStatement update)
        {
            if (update.Returning is null)
            {
                return Plans.DmlPlan.ForUpdate(this, update);
            }
            if (TryResolveDmlTargetSchema(update.SchemaName, update.TableName, out Schema? targetSchema))
            {
                Plans.CapturedRowsSource capturedSource = new(this);
                Plans.DmlPlan dml = Plans.DmlPlan.ForUpdate(this, update, capturedSource);
                return Plans.DmlReturningPlan.Compose(
                    Plans.DmlReturningKind.Update, update.TableName, targetSchema,
                    dml, capturedSource, update.Returning, this);
            }
            return Plans.DmlPlan.ForUpdate(this, update);
        }
        if (statement is DeleteStatement delete)
        {
            if (delete.Returning is null)
            {
                return Plans.DmlPlan.ForDelete(this, delete);
            }
            if (TryResolveDmlTargetSchema(delete.SchemaName, delete.TableName, out Schema? targetSchema))
            {
                Plans.CapturedRowsSource capturedSource = new(this);
                Plans.DmlPlan dml = Plans.DmlPlan.ForDelete(this, delete, capturedSource);
                return Plans.DmlReturningPlan.Compose(
                    Plans.DmlReturningKind.Delete, delete.TableName, targetSchema,
                    dml, capturedSource, delete.Returning, this);
            }
            return Plans.DmlPlan.ForDelete(this, delete);
        }

        // Procedural statements (BEGIN…END / IF / WHILE / FOR / DECLARE /
        // SET / PRINT) compose at plan time so EXPLAIN of a procedural
        // batch shows the full structure. Child plans are recursively
        // planned through this method. Runtime execution still flows
        // through BatchExecutor's AST walk in this step — the plan
        // classes' ExecuteImplAsync throws until a future step migrates
        // the procedural runtime into the plans.
        if (statement is BlockStatement block)
        {
            List<StatementPlan> children = new(block.Statements.Count);
            foreach (Statement child in block.Statements)
            {
                children.Add(await PlanAsync(child, sourceText).ConfigureAwait(false));
            }
            return new Plans.BlockPlan(this, block, children);
        }
        else if (statement is IfStatement ifs)
        {
            StatementPlan thenPlan = await PlanAsync(ifs.Then, sourceText).ConfigureAwait(false);
            StatementPlan? elsePlan = ifs.Else is not null
                ? await PlanAsync(ifs.Else, sourceText).ConfigureAwait(false)
                : null;
            return new Plans.IfPlan(this, ifs, thenPlan, elsePlan);
        }
        else if (statement is WhileStatement loop)
        {
            StatementPlan bodyPlan = await PlanAsync(loop.Body, sourceText).ConfigureAwait(false);
            return new Plans.WhilePlan(this, loop, bodyPlan);
        }
        else if (statement is ForCounterStatement forC)
        {
            StatementPlan bodyPlan = await PlanAsync(forC.Body, sourceText).ConfigureAwait(false);
            return new Plans.ForCounterPlan(this, forC, bodyPlan);
        }
        else if (statement is ForInStatement forIn)
        {
            StatementPlan sourcePlan = PlanQuery(forIn.Source);
            StatementPlan bodyPlan = await PlanAsync(forIn.Body, sourceText).ConfigureAwait(false);
            return new Plans.ForInPlan(this, forIn, sourcePlan, bodyPlan);
        }
        else if (statement is DeclareStatement decl)
        {
            return Plans.ProceduralLeafPlan.ForDeclare(this, decl);
        }
        else if (statement is SetStatement set)
        {
            return Plans.ProceduralLeafPlan.ForSet(this, set);
        }
        else if (statement is PrintStatement print)
        {
            return Plans.ProceduralLeafPlan.ForPrint(this, print);
        }
        else if (statement is BreakStatement breakStmt)
        {
            return Plans.ProceduralLeafPlan.ForBreak(this, breakStmt);
        }
        else if (statement is ContinueStatement continueStmt)
        {
            return Plans.ProceduralLeafPlan.ForContinue(this, continueStmt);
        }
        else if (statement is CreateFunctionStatement createFn)
        {
            return Plans.RoutinePlan.ForCreateFunction(this, createFn, sourceText);
        }
        else if (statement is DropFunctionStatement dropFn)
        {
            return Plans.RoutinePlan.ForDropFunction(this, dropFn, sourceText);
        }
        else if (statement is CreateProcedureStatement createProc)
        {
            return Plans.RoutinePlan.ForCreateProcedure(this, createProc, sourceText);
        }
        else if (statement is DropProcedureStatement dropProc)
        {
            return Plans.RoutinePlan.ForDropProcedure(this, dropProc, sourceText);
        }
        else if (statement is CreateViewStatement createView)
        {
            return Plans.ViewPlan.ForCreateView(this, createView, sourceText);
        }
        else if (statement is DropViewStatement dropView)
        {
            return Plans.ViewPlan.ForDropView(this, dropView, sourceText);
        }
        else if (statement is CreateModelStatement createModel)
        {
            return Plans.ModelPlan.ForCreateModel(this, createModel, sourceText);
        }
        else if (statement is DropModelStatement dropModel)
        {
            return Plans.ModelPlan.ForDropModel(this, dropModel, sourceText);
        }
        else if (statement is EvictModelStatement evictModel)
        {
            return Plans.ModelPlan.ForEvictModel(this, evictModel, sourceText);
        }
        else if (statement is ResetCalibrationStatement resetCalibration)
        {
            return Plans.ModelPlan.ForResetCalibration(this, resetCalibration, sourceText);
        }
        else if (statement is CreateSchemaStatement createSchema)
        {
            return Plans.SchemaPlan.ForCreateSchema(this, createSchema, sourceText);
        }
        else if (statement is DropSchemaStatement dropSchema)
        {
            return Plans.SchemaPlan.ForDropSchema(this, dropSchema, sourceText);
        }
        else if (statement is SetSearchPathStatement setSearchPath)
        {
            return Plans.SchemaPlan.ForSetSearchPath(this, setSearchPath);
        }
        else if (statement is CreateTableStatement createTable)
        {
            return Plans.TablePlan.ForCreateTable(this, createTable, sourceText);
        }
        else if (statement is DropTableStatement dropTable)
        {
            return Plans.TablePlan.ForDropTable(this, dropTable, sourceText);
        }
        else if (statement is CreateIndexStatement createIndex)
        {
            return Plans.IndexPlan.ForCreateIndex(this, createIndex, sourceText);
        }
        else if (statement is DropIndexStatement dropIndex)
        {
            return Plans.IndexPlan.ForDropIndex(this, dropIndex, sourceText);
        }
        else if (statement is ReindexTableStatement reindex)
        {
            return Plans.IndexPlan.ForReindex(this, reindex);
        }
        else if (statement is AnalyzeTableStatement analyze)
        {
            return Plans.IndexPlan.ForAnalyze(this, analyze);
        }
        else if (statement is AlterTableAddColumnStatement alterAdd)
        {
            return Plans.AlterTablePlan.ForAddColumn(this, alterAdd, sourceText);
        }
        else if (statement is AlterTableDropColumnStatement alterDropCol)
        {
            return Plans.AlterTablePlan.ForDropColumn(this, alterDropCol, sourceText);
        }
        else if (statement is AlterTableDropConstraintStatement alterDropConstraint)
        {
            return Plans.AlterTablePlan.ForDropConstraint(this, alterDropConstraint, sourceText);
        }
        else if (statement is AlterTableAlterColumnDropStatement alterColumnDrop)
        {
            return Plans.AlterTablePlan.ForAlterColumnDrop(this, alterColumnDrop, sourceText);
        }
        else if (statement is AlterTableAlterColumnSetStatement alterColumnSet)
        {
            return Plans.AlterTablePlan.ForAlterColumnSet(this, alterColumnSet, sourceText);
        }

        // Every supported statement type has an explicit structural route
        // above. Anything reaching here is a new AST node a contributor
        // added without wiring a plan family — fail loudly at plan time
        // rather than silently falling through to an opaque thunk.
        throw new NotSupportedException(
            $"Statement type '{statement.GetType().Name}' is not supported by PlanAsync. " +
            "Add a structured plan class under Heliosoph.DatumV.Catalog.Plans and an explicit route in PlanAsync.");
    }

    private StatementPlan PlanCall(CallStatement call)
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

    internal SelectPlan PlanQuery(QueryExpression query)
    {
        // PG-style named arguments — rewrite fn(a := 1, b => 2) into the
        // canonical positional shape before UdfInliner / planner passes,
        // which all assume positional argument lists.
        QueryExpression permuted = NamedArgPermuter.Permute(query, Functions, Udfs, SearchPath);

        // Parse-time pre-flight: walk the top-level statement for
        // catalog-model references that need a download or a typo fix
        // before we build any operator tree. Runs pre-UdfInliner so UDF
        // bodies stay opaque (their models.* references aren't surfaced
        // until the user names the model directly). Empty result == no
        // blocker.
        PreFlightRequirements preflight = PreFlightWalker.Walk(
            permuted, Models, CatalogVocabulary, Functions, DatasetPreFlightSource);
        if (preflight.Models.Count > 0
            || preflight.Datasets.Count > 0
            || preflight.Suggestions.Count > 0)
        {
            throw new PreFlightRequiredException(preflight);
        }

        QueryExpression inlined = UdfInliner.Inline(permuted, Udfs, SearchPath, Procedures);
        QueryPlanner planner = new(this, Functions);
        QueryOperator op = planner.Plan(inlined);
        return new SelectPlan(op, this, Functions);
    }

    /// <summary>
    /// Convenience "batch of one" executor: constructs a fresh
    /// <see cref="ExecutionContext"/> against this catalog, starts profiling on
    /// its <see cref="MemoryAccountant"/>, and streams the plan's output.
    /// The context is disposed when the iterator finishes (success, break,
    /// or throw).
    /// </summary>
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(
        StatementPlan plan,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        using Execution.ExecutionContext context = CreateExecutionContext(cancellationToken: cancellationToken);
        context.Accountant.StartProfiling();
        await foreach (RowBatch batch in plan.ExecuteAsync(cancellationToken, context).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    /// <summary>
    /// Convenience "batch of one" analyzer: same lifetime story as
    /// <see cref="ExecuteAsync(StatementPlan, CancellationToken)"/>, runs the
    /// plan under instrumentation, and returns the populated EXPLAIN
    /// ANALYZE tree.
    /// </summary>
    public async Task<ExplainPlanNode> AnalyzeAsync(
        StatementPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        using Heliosoph.DatumV.Execution.ExecutionContext context = CreateExecutionContext(cancellationToken: cancellationToken);
        context.Accountant.StartProfiling();
        return await plan.AnalyzeAsync(cancellationToken, context).ConfigureAwait(false);
    }

    /// <summary>
    /// Best-effort target-schema resolution for DML composition. Returns
    /// the live <see cref="Schema"/> when the (schema, table) qualifier
    /// resolves through the search path and the provider is registered;
    /// returns <see langword="false"/> when any step fails so the caller
    /// can fall back to a bare <see cref="Plans.DmlPlan"/> and let the
    /// executor surface the precise diagnostic at apply time.
    /// </summary>
    private bool TryResolveDmlTargetSchema(
        string? schemaName,
        string tableName,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Schema? schema)
    {
        try
        {
            SchemaResolver resolver = new(this, SearchPath);
            QualifiedName qn = resolver.Resolve(schemaName, tableName);
            if (TryGetTable(qn.ToString(), out ITableProvider? provider))
            {
                schema = provider.GetSchema();
                return true;
            }
        }
        catch
        {
            // Resolution exceptions (missing schema, ambiguous, etc.) fall
            // through to the executor for the canonical diagnostic.
        }
        schema = null;
        return false;
    }

    /// <summary>
    /// Returns the user-created index descriptors for <paramref name="tableName"/>,
    /// or <see langword="null"/> when the table has no indexes (or doesn't exist).
    /// Used by the <c>information_schema.table_constraints</c> /
    /// <c>information_schema.key_column_usage</c> views to surface UNIQUE
    /// constraints alongside PRIMARY KEY constraints, and by
    /// <c>SchemaCatalogController</c> to enumerate per-table indexes for
    /// the catalog-explorer UI.
    /// </summary>
    public IReadOnlyList<IndexDescriptor>? GetTableIndexes(string tableName)
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
    /// or <see langword="null"/> when none qualifies.
    /// </summary>
    public string? FirstWritableSchema()
    {
        foreach (string schema in _searchPath)
        {
            if (Backends.TryGetValue(schema, out ITableCatalog? backend) && backend.SupportsDdl)
            {
                return schema;
            }
        }

        return null;
    }

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
            if (!Backends.ContainsKey(schema))
            {
                throw new InvalidOperationException(
                    $"SET search_path: schema '{schema}' does not exist.");
            }
        }
        // Snapshot to a fresh immutable list so callers that captured the
        // old reference keep their view.
        _searchPath = schemas.ToArray();
    }

    internal static bool IsBuiltinSchema(string schema)
        => string.Equals(schema, "public", StringComparison.OrdinalIgnoreCase)
        || string.Equals(schema, "system", StringComparison.OrdinalIgnoreCase)
        || string.Equals(schema, "information_schema", StringComparison.OrdinalIgnoreCase)
        || string.Equals(schema, "datum_catalog", StringComparison.OrdinalIgnoreCase)
        || string.Equals(schema, "models", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Routes <paramref name="schema"/> to its owning backend, or returns
    /// <see langword="false"/> when no backend is mounted for that schema.
    /// Used by lookup / Add / DDL paths so the facade can dispatch
    /// uniformly.
    /// </summary>
    internal bool TryResolveBackend(string schema, [NotNullWhen(true)] out ITableCatalog? backend)
        => Backends.TryGetValue(schema, out backend);

    /// <summary>
    /// Returns the backend that owns <paramref name="schema"/>, or
    /// <see langword="false"/> when no backend is mounted there. Exposed
    /// to <see cref="SchemaResolver"/> so it can pick DDL-capable
    /// schemas during <c>CREATE TABLE</c> resolution.
    /// </summary>
    internal bool TryFindBackend(string schema, [NotNullWhen(true)] out ITableCatalog? backend)
        => Backends.TryGetValue(schema, out backend);

    /// <summary>
    /// Returns <see langword="true"/> if a table with the given name is registered
    /// in this catalog or its parent; otherwise <see langword="false"/>.
    /// </summary>
    /// <param name="fullyQualifiedName">The qualified name of the table to check.</param>
    /// <returns><see langword="true"/> if the table exists; otherwise <see langword="false"/>.</returns>
    public bool HasTable(string fullyQualifiedName) => HasTable(QualifiedName.Parse(fullyQualifiedName));

    /// <summary>
    /// Returns <see langword="true"/> if a table with the given name is registered
    /// in this catalog or its parent; otherwise <see langword="false"/>.
    /// </summary>
    /// <param name="qn">The schema and name pair of the table to check.</param>
    /// <returns><see langword="true"/> if the table exists; otherwise <see langword="false"/>.</returns>
    public bool HasTable(QualifiedName qn)
    {
        if (TryResolveBackend(qn.Schema, out ITableCatalog? backend)
            && backend.TryGetTable(qn, out _))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Attempts to get the table provider associated with the given logical table name.
    /// </summary>
    /// <param name="name">The logical name of the table.</param>
    /// <param name="provider">When this method returns, contains the table provider associated with the given name, if found; otherwise, <c>null</c>.</param>
    /// <returns><see langword="true"/> if the table provider was found; otherwise, <see langword="false"/>.</returns>
    public bool TryGetTable(string name, [NotNullWhen(true)] out ITableProvider? provider)
        => TryGetTable(QualifiedName.Parse(name), out provider);

    /// <summary>
    /// Schema-aware lookup using a pre-built <see cref="QualifiedName"/>.
    /// Bypasses the string indexer's parse step. Used by
    /// <see cref="SchemaResolver"/> in the hot path.
    /// </summary>
    public bool TryGetTable(QualifiedName name, [NotNullWhen(true)] out ITableProvider? provider)
    {
        if (TryResolveBackend(name.Schema, out ITableCatalog? backend)
            && backend.TryGetTable(name, out provider))
        {
            return true;
        }

        provider = null;
        return false;
    }

    /// <summary>
    /// Gets the table provider associated with the given logical table name.
    /// If the name is not found in this catalog, the parent catalog is consulted
    /// if it exists.
    /// </summary>
    /// <param name="name">The logical name of the table.</param>
    /// <returns>The table provider associated with the given name.</returns>
    /// <exception cref="KeyNotFoundException">Thrown if the table name is not found in this catalog or its parent.</exception>
    public ITableProvider this[string name] => this[QualifiedName.Parse(name)];

    /// <summary>
    /// Gets the table provider associated with the given logical table name.
    /// If the name is not found in this catalog, the parent catalog is consulted
    /// if it exists.
    /// </summary>
    /// <param name="qn">The schema and name pair of the table.</param>
    /// <returns>The table provider associated with the given name.</returns>
    /// <exception cref="KeyNotFoundException">Thrown if the table name is not found in this catalog or its parent.</exception>
    public ITableProvider this[QualifiedName qn]
    {
        get
        {
            if (TryResolveBackend(qn.Schema, out ITableCatalog? backend)
                && backend.TryGetTable(qn, out ITableProvider? provider))
            {
                return provider;
            }
            
            throw new KeyNotFoundException($"Table '{qn}' is not registered in the catalog.");
        }
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
    /// Registers a table descriptor, making it available for resolution by name.
    /// The provider is created immediately and kept alive until the catalog is disposed.
    /// </summary>
    /// <param name="tableDescriptor">The table descriptor to register.</param>
    /// <returns>The created table provider.</returns>
    /// <exception cref="ArgumentException">Thrown if a table with the same name is already registered in this catalog or its parent.</exception>
    public ITableProvider Add(TableDescriptor tableDescriptor)
    {
        QualifiedName qn = QualifiedName.Parse(tableDescriptor.Name);

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

        if (!TryResolveBackend(qn.Schema, out ITableCatalog? backend))
        {
            throw new ArgumentException(
                $"No catalog backend is mounted for schema '{qn.Schema}' " +
                $"(provider '{qn}').");
        }

        return backend.Add(tableProvider);
    }

    /// <inheritdoc />
    public IEnumerator<ITableProvider> GetEnumerator()
    {
        // Walk every mounted backend. One backend instance can be mounted
        // under multiple schemas (e.g. the dataset schema catalog) — dedup
        // by reference so each provider yields exactly once. FlatFile /
        // System / Virtual are visited first because of their canonical
        // position at the front of the Backends dict; the dataset and any
        // other dynamically-mounted backends follow.
        HashSet<ITableCatalog> seen = new(ReferenceEqualityComparer.Instance);
        foreach (ITableCatalog backend in Backends.Values)
        {
            if (!seen.Add(backend)) continue;
            foreach (ITableProvider provider in backend.ListTables())
            {
                yield return provider;
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public void Dispose()
    {
        // Dispose Models first so its calibration save timer flushes
        // before the providers that surface it (system.models et al)
        // tear down. ModelCatalog.Dispose is the only path that writes
        // a final calibration JSON on clean exit; skipping it loses
        // every curve measured since the last 1-minute tick.
        Models?.Dispose();

        // Disposes all locally-registered providers via each backend.
        // Parent-catalog providers remain owned by the parent.
        FlatFileCatalog.Dispose();
        SystemCatalog.Dispose();
        VirtualCatalog.Dispose();
    }
}