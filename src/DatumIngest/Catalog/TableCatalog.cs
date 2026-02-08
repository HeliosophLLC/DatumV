using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using DatumIngest.Catalog.Providers;
using DatumIngest.DatumFile.Sidecar;
using DatumIngest.DatumFile.V2;
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
        // Case-insensitive lookup matches SQL identifier semantics:
        // CREATE TABLE Test and SELECT * FROM TEST resolve to the same
        // entry. The persistent-table-entry map already uses
        // OrdinalIgnoreCase; aligning here keeps the two in sync.
        this.Tables = new(StringComparer.OrdinalIgnoreCase);
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
        Add(new Providers.InformationSchemaTableConstraintsProvider(pool, this));
        Add(new Providers.InformationSchemaKeyColumnUsageProvider(pool, this));
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
            // Wire the tables provider before any save fires so the
            // file-write path always sees the catalog's current table
            // set (PR10a). The closure captures `this` so the latest
            // _persistentTableEntries snapshot is used at save time.
            _catalogStore.SetTablesProvider(SnapshotPersistentTablesForSave);

            CatalogStoreLoadReport report = _catalogStore.Load(_udfs, _procedures);
            CatalogLoadReport = report;

            // The Load() call writes straight into _udfs without going
            // through ApplyCreateFunction, so procedural adapters in the
            // scalar registry haven't been wired yet. Reconcile them here so
            // a freshly opened catalog can immediately invoke any persisted
            // procedural UDF.
            _routines.SyncProceduralAdaptersFromRegistry();

            // Replay persisted tables. Resolves relative paths against
            // the catalog directory so the catalog moves with the data.
            foreach (CatalogFileTableEntry entry in _catalogStore.LoadTables())
            {
                if (string.IsNullOrEmpty(entry.Name) || string.IsNullOrEmpty(entry.FilePath)) continue;
                string resolved = ResolveTablePath(entry.FilePath);
                if (!File.Exists(resolved))
                {
                    // Stale catalog entry — file has been moved or
                    // deleted. Skip silently for now; a future REPAIR
                    // command can prune dead entries.
                    continue;
                }

                // Materialise the index list from the JSON entry so the
                // provider can open the corresponding .datum-cindex-* sidecars
                // at construction. Catalog files written before composite
                // indexes existed have entry.Indexes == null, which Add()
                // sees as "no indexes" — equivalent semantics.
                List<IndexDescriptor>? indexes = null;
                if (entry.Indexes is { Count: > 0 } indexEntries)
                {
                    indexes = new List<IndexDescriptor>(indexEntries.Count);
                    foreach (CatalogFileIndexEntry indexEntry in indexEntries)
                    {
                        if (string.IsNullOrEmpty(indexEntry.Name) || indexEntry.Columns is null || indexEntry.Columns.Count == 0)
                        {
                            continue;
                        }
                        IndexKind kind = ParseIndexKindOrDefault(indexEntry.Kind);
                        indexes.Add(new IndexDescriptor(
                            indexEntry.Name,
                            indexEntry.Columns.ToArray(),
                            indexEntry.IsUnique,
                            kind,
                            indexEntry.AnalyzerName));
                    }
                }

                string entryKey = Key(entry.Name);
                Add(new TableDescriptor(entry.Name, resolved, Indexes: indexes));
                _persistentTableEntries[entryKey] = entry.FilePath;
                if (indexes is { Count: > 0 })
                {
                    _persistentTableIndexes[entryKey] = indexes;
                    foreach (IndexDescriptor index in indexes)
                    {
                        _indexNameToTable[index.Name] = entryKey;
                    }
                }
                if (!string.IsNullOrEmpty(entry.PrimaryKeyConstraintName))
                {
                    _persistentTablePkNames[entryKey] = entry.PrimaryKeyConstraintName!;
                }
            }
        }
    }


    private TableCatalog? Parent { get; }
    internal Pool Pool { get; }

    /// <summary>
    /// Canonicalises a user-typed table name into the dictionary-key form
    /// used by <see cref="Tables"/> and the per-table tracking dicts.
    /// Unqualified names default to the <c>public</c> schema.
    /// </summary>
    private static string Key(string tableName) =>
        QualifiedName.Parse(tableName).ToString();

    /// <summary>
    /// When <see langword="true"/>, <c>CREATE TABLE … AT 'path'</c>
    /// statements are honored. Default is <see langword="false"/> —
    /// production hosts reject the clause so table files always land in
    /// the catalog's working directory.
    /// </summary>
    public bool AllowExplicitTablePaths { get; }
    private readonly PoolBacking _backing;
    private readonly FunctionRegistry _functions;
    private readonly UdfRegistry _udfs;
    private readonly ProcedureRegistry _procedures;
    private readonly CatalogStore? _catalogStore;
    private readonly RoutineRegistrar _routines;
    private ConcurrentDictionary<string, ITableProvider> Tables { get; }
    private readonly SidecarRegistry _sidecarRegistry = new();

    /// <summary>
    /// Tracks tables created via <c>CREATE TABLE</c> (i.e. ones that
    /// should round-trip through the catalog json). Maps the catalog
    /// table name to the path string we want to persist (relative when
    /// the table lives inside the catalog's directory tree, absolute
    /// otherwise). Tables added via host-side <see cref="AddFile"/>
    /// don't appear here — they're not the catalog file's responsibility.
    /// TEMP tables aren't tracked either; they die with the session.
    /// </summary>
    private readonly Dictionary<string, string> _persistentTableEntries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Tracks user-defined secondary indexes per persistent table (via
    /// <c>CREATE INDEX</c>). Keyed by table name (matches
    /// <see cref="_persistentTableEntries"/>). Each entry is the ordered list
    /// of <see cref="IndexDescriptor"/>s for that table. Indexes survive
    /// catalog reopen because they're persisted alongside the table's
    /// <see cref="CatalogFileTableEntry"/>.
    /// </summary>
    private readonly Dictionary<string, List<IndexDescriptor>> _persistentTableIndexes =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reverse lookup: index name → owning table name. Composite indexes
    /// have catalog-global names (mirroring Postgres) so <c>DROP INDEX</c>
    /// can resolve to its table without naming it. Populated from
    /// <see cref="_persistentTableIndexes"/> at load and on every
    /// <c>CREATE INDEX</c> / <c>DROP INDEX</c>.
    /// </summary>
    private readonly Dictionary<string, string> _indexNameToTable =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the user-created index descriptors for <paramref name="tableName"/>,
    /// or <see langword="null"/> when the table has no indexes (or doesn't exist).
    /// Used by the <c>information_schema.table_constraints</c> /
    /// <c>information_schema.key_column_usage</c> views to surface UNIQUE
    /// constraints alongside PRIMARY KEY constraints.
    /// </summary>
    internal IReadOnlyList<IndexDescriptor>? GetTableIndexes(string tableName)
        => _persistentTableIndexes.TryGetValue(Key(tableName), out List<IndexDescriptor>? list)
            ? list
            : null;

    /// <summary>
    /// User-supplied PRIMARY KEY constraint names keyed by table name.
    /// Populated at <c>CREATE TABLE</c> time when the user wrote
    /// <c>CONSTRAINT name PRIMARY KEY</c>; <see langword="null"/> entries
    /// or missing keys mean "derive the default <c>&lt;table&gt;_pkey</c>".
    /// Persists in <c>.datum-catalog.json</c> alongside the table entry.
    /// </summary>
    private readonly Dictionary<string, string> _persistentTablePkNames =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the user-supplied PRIMARY KEY constraint name for
    /// <paramref name="tableName"/>, or the derived default
    /// (<c>&lt;table&gt;_pkey</c>) when no custom name was supplied.
    /// Used by <c>information_schema.table_constraints</c> and by
    /// <c>DROP CONSTRAINT</c> name-matching.
    /// </summary>
    internal string GetPrimaryKeyConstraintName(string tableName)
    {
        if (_persistentTablePkNames.TryGetValue(Key(tableName), out string? custom))
        {
            return custom;
        }
        // Derive PG-default `<unqualified-table>_pkey`. Strip the schema
        // so the constraint name reads as users_pkey, not public.users_pkey.
        return Providers.InformationSchemaTableConstraintsProvider
            .PrimaryKeyConstraintName(QualifiedName.Parse(tableName).Name);
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
                _routines.ApplyDropFunction(drop);
                return EmptyQueryPlan.Instance;

            case CreateProcedureStatement create:
                _routines.ApplyCreateProcedure(create, sourceText);
                return EmptyQueryPlan.Instance;

            case DropProcedureStatement drop:
                _routines.ApplyDropProcedure(drop);
                return EmptyQueryPlan.Instance;

            case CallStatement call:
                return PlanCall(call);

            case CreateTableStatement createTable:
                await ApplyCreateTableAsync(createTable).ConfigureAwait(false);
                return EmptyQueryPlan.Instance;

            case DropTableStatement dropTable:
                ApplyDropTable(dropTable);
                return EmptyQueryPlan.Instance;

            case CreateIndexStatement createIndex:
                await ApplyCreateIndexAsync(createIndex).ConfigureAwait(false);
                return EmptyQueryPlan.Instance;

            case DropIndexStatement dropIndex:
                ApplyDropIndex(dropIndex);
                return EmptyQueryPlan.Instance;

            case ReindexTableStatement reindex:
                await ApplyReindexTableAsync(reindex).ConfigureAwait(false);
                return EmptyQueryPlan.Instance;

            case AnalyzeTableStatement analyze:
                await ApplyAnalyzeTableAsync(analyze).ConfigureAwait(false);
                return EmptyQueryPlan.Instance;

            case AlterTableAddColumnStatement alterAdd:
                if (alterAdd.TableIfExists && !TryGetTable(alterAdd.TableName, out _)) return EmptyQueryPlan.Instance;
                await ApplyAlterTableAddColumnAsync(alterAdd).ConfigureAwait(false);
                return EmptyQueryPlan.Instance;

            case AlterTableDropColumnStatement alterDrop:
                if (alterDrop.TableIfExists && !TryGetTable(alterDrop.TableName, out _)) return EmptyQueryPlan.Instance;
                ApplyAlterTableDropColumn(alterDrop);
                return EmptyQueryPlan.Instance;

            case AlterTableDropConstraintStatement alterDropConstraint:
                if (alterDropConstraint.TableIfExists && !TryGetTable(alterDropConstraint.TableName, out _)) return EmptyQueryPlan.Instance;
                await ApplyAlterTableDropConstraintAsync(alterDropConstraint).ConfigureAwait(false);
                return EmptyQueryPlan.Instance;

            case AlterTableAlterColumnDropStatement alterColumnDrop:
                if (alterColumnDrop.TableIfExists && !TryGetTable(alterColumnDrop.TableName, out _)) return EmptyQueryPlan.Instance;
                await ApplyAlterTableAlterColumnDropAsync(alterColumnDrop).ConfigureAwait(false);
                return EmptyQueryPlan.Instance;

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

    /// <summary>
    /// Applies a <c>CREATE TABLE</c> statement: validates the
    /// <c>AT 'path'</c> clause against <see cref="AllowExplicitTablePaths"/>,
    /// resolves the storage location, materialises the table (in-memory
    /// for TEMP, an empty <c>.datum</c> file for persistent), registers
    /// it with the catalog, and persists the entry in the catalog json
    /// (persistent only).
    /// </summary>
    /// <remarks>
    /// PR10a covers shape only — column kinds, NULL/NOT NULL,
    /// <c>IF NOT EXISTS</c>, optional <c>AT 'path'</c>. PR10b adds
    /// <c>DEFAULT &lt;literal&gt;</c> persisted in the footer prologue;
    /// PR10c/PR10c' adds INSERT VALUES/SELECT auto-fill from those
    /// defaults. PR10e adds <c>IDENTITY</c> with a per-table counter
    /// in the prologue and <c>IAppendSession.ReserveNextIdentityValue</c>
    /// auto-fill at INSERT time. PR10f adds <c>PRIMARY KEY</c>
    /// enforcement: the prologue carries the ordered PK column-index
    /// list, the catalog rejects tables whose key exceeds 16 bytes,
    /// and the INSERT layer scans existing rows to reject duplicate /
    /// null PK values.
    /// </remarks>
    private async Task ApplyCreateTableAsync(CreateTableStatement create)
    {
        // AT clause gating.
        if (create.StoragePath is not null && !AllowExplicitTablePaths)
        {
            throw new InvalidOperationException(
                $"CREATE TABLE '{create.TableName}' uses AT 'path' but the catalog has " +
                "AllowExplicitTablePaths = false. Pass allowExplicitTablePaths: true to the " +
                "TableCatalog constructor to opt in (test scenarios), or remove the AT clause " +
                "and let the catalog place the file at {catalog_dir}/{name}.datum.");
        }

        // Persistent CREATE TABLE only allowed with a catalog file.
        if (!create.IsTemp && _catalogStore is null)
        {
            throw new InvalidOperationException(
                $"CREATE TABLE '{create.TableName}' requires the catalog to be backed by a " +
                ".datum-catalog.json file. Either use CREATE TEMP TABLE for in-memory " +
                "scratch, or open the catalog with a catalogPath so persistent tables can be " +
                "recorded.");
        }

        // Existence check.
        if (HasTable(create.TableName))
        {
            if (create.IfNotExists) return;
            throw new InvalidOperationException(
                $"Table '{create.TableName}' already exists.");
        }

        // Build ColumnInfo[] from the AST's ColumnDefinition list.
        Schema schema = await BuildSchemaFromColumnDefinitionsAsync(create.Columns, create.PrimaryKeyColumns)
            .ConfigureAwait(false);

        if (create.IsTemp)
        {
            Add(new InMemoryTableProvider(Pool, create.TableName, schema));
            return;
        }

        // Persistent. Resolve the storage path, materialise an empty
        // .datum file, register, and record in the catalog json.
        string targetPath = ResolveCreateTablePath(create.TableName, create.StoragePath);
        if (File.Exists(targetPath))
        {
            // We already checked HasTable; a stale .datum on disk
            // without a catalog entry is treated as an error to avoid
            // accidentally overwriting unindexed user data.
            throw new InvalidOperationException(
                $"CREATE TABLE '{create.TableName}' would create a file at '{targetPath}' " +
                "but a file already exists there. Drop the existing file or pick a different " +
                "name / AT clause.");
        }

        ColumnDescriptorV2[] descriptors = new ColumnDescriptorV2[schema.Columns.Count];
        List<ColumnDefaultV4>? columnDefaults = null;
        List<ColumnComputedV4>? columnComputeds = null;
        IdentityWriterSpec? identityWriterSpec = null;
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            ColumnInfo c = schema.Columns[i];
            descriptors[i] = new ColumnDescriptorV2(
                Name: c.Name,
                Kind: c.Kind,
                Encoder: ColumnDescriptorV2.EncoderFor(c.Kind, c.IsArray),
                IsNullable: c.Nullable,
                IsArray: c.IsArray);

            if (c.DefaultExpression is not null)
            {
                columnDefaults ??= new List<ColumnDefaultV4>();
                columnDefaults.Add(new ColumnDefaultV4(
                    ColumnIndex: checked((ushort)i),
                    SqlFragment: QueryExplainer.FormatExpression(c.DefaultExpression)));
            }

            if (c.ComputedExpression is not null)
            {
                columnComputeds ??= new List<ColumnComputedV4>();
                columnComputeds.Add(new ColumnComputedV4(
                    ColumnIndex: checked((ushort)i),
                    SqlFragment: QueryExplainer.FormatExpression(c.ComputedExpression)));
            }

            if (c.Identity is { } identity)
            {
                // Schema-build validates "at most one IDENTITY column", so
                // hitting two here would be a structural bug — fall through
                // and let the writer's invariants surface it.
                identityWriterSpec = new IdentityWriterSpec(i, identity.Seed, identity.Step, identity.AcceptUserValues);
            }
        }

        // PK indices on the schema are already in declaration order;
        // translate to ushort for the prologue's storage.
        ushort[]? pkColumnIndices = schema.PrimaryKeyColumnIndices.Count == 0
            ? null
            : schema.PrimaryKeyColumnIndices.Select(i => checked((ushort)i)).ToArray();

        DatumFileWriterV2.CreateEmpty(
            targetPath, descriptors, columnDefaults, identityWriterSpec, pkColumnIndices, columnComputeds);

        // Create the on-disk PK index sidecar. Single bytes-keyed tree for
        // any PK shape — single-column or composite, fixed-size or variable.
        // Per-component values are normalised by CompositeKeyEncoder into a
        // memcmp-orderable byte sequence; the tree stores those bytes.
        if (schema.PrimaryKeyColumnIndices.Count >= 1)
        {
            string pkIndexPath = Catalog.Providers.DatumFileTableProviderV2.GetPrimaryKeyIndexPath(targetPath);
            Indexing.BTree.MutableBytes.MutableBPlusTreeBytes pkTree =
                Indexing.BTree.MutableBytes.MutableBPlusTreeBytes.Create(pkIndexPath);
            pkTree.Dispose();
        }

        Add(new TableDescriptor(create.TableName, targetPath));

        // Record in catalog json. Store the path relative to the
        // catalog directory when possible so the catalog moves with
        // the data.
        string createKey = Key(create.TableName);
        string persistedPath = ToPersistedPath(targetPath);
        _persistentTableEntries[createKey] = persistedPath;

        // User-supplied PRIMARY KEY constraint name from
        // `CONSTRAINT name PRIMARY KEY [(...)]`. Only meaningful when
        // there's actually a PK; the parser already ensures the name is
        // only attached to a PK clause.
        if (!string.IsNullOrEmpty(create.PrimaryKeyConstraintName)
            && schema.PrimaryKeyColumnIndices.Count > 0)
        {
            _persistentTablePkNames[createKey] = create.PrimaryKeyConstraintName!;
        }

        _catalogStore!.Save(_udfs, _procedures);
    }

    /// <summary>
    /// Applies a <c>DROP TABLE</c> statement: removes the table from
    /// the catalog, disposes its provider, deletes the underlying
    /// <c>.datum</c> file (and companion sidecars), and updates the
    /// catalog json. <c>IF EXISTS</c> suppresses the not-found error.
    /// </summary>
    private void ApplyDropTable(DropTableStatement drop)
    {
        string key = Key(drop.TableName);
        if (!Tables.TryGetValue(key, out ITableProvider? provider))
        {
            if (drop.IfExists) return;
            throw new InvalidOperationException(
                $"Table '{drop.TableName}' is not registered in the catalog.");
        }

        // Capture path BEFORE disposing the provider so we can delete
        // the file. Persistent tables track their path in
        // _persistentTableEntries.
        string? persistedPath = _persistentTableEntries.TryGetValue(key, out string? p) ? p : null;

        Tables.TryRemove(key, out _);
        try { provider.Dispose(); } catch { /* best-effort */ }

        if (persistedPath is not null)
        {
            string resolved = ResolveTablePath(persistedPath);
            // Delete the .datum file plus its companion sidecars.
            // Best-effort: leave the directory clean even if a
            // particular sidecar is locked or missing.
            TryDeleteFile(resolved);
            TryDeleteFile(System.IO.Path.ChangeExtension(resolved, ".datum-blob"));
            TryDeleteFile(System.IO.Path.ChangeExtension(resolved, ".datum-index"));
            TryDeleteFile(System.IO.Path.ChangeExtension(resolved, ".datum-manifest"));
            TryDeleteFile(System.IO.Path.ChangeExtension(resolved, ".datum-pkindex"));

            // User-defined secondary index sidecars (one per CREATE INDEX).
            // Glob-match the .datum file's stem so we sweep any orphans even
            // if the descriptor list is stale.
            string? dir = System.IO.Path.GetDirectoryName(resolved);
            string stem = System.IO.Path.GetFileNameWithoutExtension(resolved);
            if (!string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(stem))
            {
                foreach (string cindexFile in Directory.EnumerateFiles(dir, stem + ".datum-cindex-*"))
                {
                    TryDeleteFile(cindexFile);
                }
                foreach (string ftsFile in Directory.EnumerateFiles(dir, stem + ".datum-fts-*"))
                {
                    TryDeleteFile(ftsFile);
                }
            }

            _persistentTableEntries.Remove(key);
            if (_persistentTableIndexes.TryGetValue(key, out List<IndexDescriptor>? droppedIndexes))
            {
                foreach (IndexDescriptor index in droppedIndexes)
                {
                    _indexNameToTable.Remove(index.Name);
                }
                _persistentTableIndexes.Remove(key);
            }
            _persistentTablePkNames.Remove(key);
            _catalogStore?.Save(_udfs, _procedures);
        }
    }

    /// <summary>
    /// Applies a <c>CREATE INDEX</c> statement: validates the target table /
    /// columns, asks the provider to materialise a new
    /// <c>.datum-cindex-{name}</c> sidecar (and backfill it from existing
    /// rows), records the index in the catalog descriptor, and persists.
    /// </summary>
    private async Task ApplyCreateIndexAsync(CreateIndexStatement create)
    {
        // Validate the target table exists and is owned by this catalog.
        string key = Key(create.TableName);
        if (!Tables.TryGetValue(key, out ITableProvider? provider))
        {
            throw new InvalidOperationException(
                $"Table '{create.TableName}' is not registered in the catalog.");
        }

        // Index names are catalog-globally unique (Postgres semantics).
        if (_indexNameToTable.TryGetValue(create.IndexName, out string? existingOwner))
        {
            if (create.IfNotExists && string.Equals(existingOwner, key, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            throw new InvalidOperationException(
                $"Index '{create.IndexName}' already exists" +
                (string.Equals(existingOwner, key, StringComparison.OrdinalIgnoreCase)
                    ? $" on table '{existingOwner}'."
                    : $" on table '{existingOwner}'. Index names must be unique across the catalog."));
        }

        if (provider is not Providers.DatumFileTableProviderV2 datumProvider)
        {
            throw new InvalidOperationException(
                $"Table '{create.TableName}' does not support CREATE INDEX. " +
                "Only persistent .datum tables maintain composite indexes; " +
                "TEMP / InMemory / external-source tables are excluded.");
        }

        if (create.Columns.Count == 0)
        {
            throw new InvalidOperationException(
                $"CREATE INDEX '{create.IndexName}' requires at least one column.");
        }

        // Branch on USING method. Pre-FTS DDL has Method=null → composite.
        IndexKind kind = ResolveCreateIndexKind(create);
        IndexDescriptor descriptor = kind switch
        {
            IndexKind.Composite => BuildCompositeIndexDescriptor(create, datumProvider),
            IndexKind.FullText => BuildFullTextIndexDescriptor(create, datumProvider),
            _ => throw new InvalidOperationException(
                $"CREATE INDEX '{create.IndexName}': unsupported index kind {kind}."),
        };

        switch (kind)
        {
            case IndexKind.Composite:
                await datumProvider.AddCompositeIndexAsync(descriptor).ConfigureAwait(false);
                break;
            case IndexKind.FullText:
                await datumProvider.AddFtsIndexAsync(descriptor).ConfigureAwait(false);
                break;
        }

        // Update catalog state and persist.
        if (!_persistentTableIndexes.TryGetValue(key, out List<IndexDescriptor>? list))
        {
            list = new List<IndexDescriptor>();
            _persistentTableIndexes[key] = list;
        }
        list.Add(descriptor);
        _indexNameToTable[descriptor.Name] = key;
        _catalogStore?.Save(_udfs, _procedures);
    }

    private static IndexKind ResolveCreateIndexKind(CreateIndexStatement create)
    {
        if (create.Method is null)
        {
            return IndexKind.Composite;
        }

        return create.Method.ToLowerInvariant() switch
        {
            "btree" or "composite" => IndexKind.Composite,
            "fts" or "fulltext" => IndexKind.FullText,
            _ => throw new InvalidOperationException(
                $"CREATE INDEX '{create.IndexName}': USING method '{create.Method}' is not recognized. " +
                "Supported methods: BTREE (default), FTS."),
        };
    }

    private static IndexDescriptor BuildCompositeIndexDescriptor(CreateIndexStatement create, Providers.DatumFileTableProviderV2 datumProvider)
    {
        // WITH options aren't recognised on composite indexes today; fail loudly
        // if someone supplies one so typos don't get silently dropped.
        if (create.Options is { Count: > 0 })
        {
            throw new InvalidOperationException(
                $"CREATE INDEX '{create.IndexName}': WITH options are not supported for composite (B+Tree) indexes. " +
                "Known options apply to USING FTS only.");
        }

        Model.Schema schema = datumProvider.GetSchema();
        foreach (string columnName in create.Columns)
        {
            Model.ColumnInfo? column = schema.FindColumn(columnName);
            if (column is null)
            {
                throw new InvalidOperationException(
                    $"CREATE INDEX '{create.IndexName}': column '{columnName}' does not exist on table '{create.TableName}'.");
            }
            if (column.IsArray)
            {
                throw new InvalidOperationException(
                    $"CREATE INDEX '{create.IndexName}': column '{columnName}' is an array column and cannot be indexed.");
            }
            if (column.Kind is Model.DataKind.Decimal or Model.DataKind.Point2D or Model.DataKind.Point3D)
            {
                throw new InvalidOperationException(
                    $"CREATE INDEX '{create.IndexName}': column '{columnName}' has kind '{column.Kind}' which has no canonical sort encoding.");
            }
        }

        return new IndexDescriptor(create.IndexName, create.Columns.ToArray(), create.IsUnique);
    }

    private static IndexDescriptor BuildFullTextIndexDescriptor(CreateIndexStatement create, Providers.DatumFileTableProviderV2 datumProvider)
    {
        if (create.IsUnique)
        {
            throw new InvalidOperationException(
                $"CREATE INDEX '{create.IndexName}': UNIQUE is not valid for full-text indexes — " +
                "duplicate postings are the whole point of an inverted index.");
        }

        if (create.Columns.Count != 1)
        {
            throw new InvalidOperationException(
                $"CREATE INDEX '{create.IndexName}': USING FTS requires exactly one column (got {create.Columns.Count}). " +
                "For cross-column search, either create one FTS index per column or use a generated " +
                "concatenation column.");
        }

        string columnName = create.Columns[0];
        Model.Schema schema = datumProvider.GetSchema();
        Model.ColumnInfo? column = schema.FindColumn(columnName);
        if (column is null)
        {
            throw new InvalidOperationException(
                $"CREATE INDEX '{create.IndexName}': column '{columnName}' does not exist on table '{create.TableName}'.");
        }
        if (column.IsArray || column.Kind != Model.DataKind.String)
        {
            throw new InvalidOperationException(
                $"CREATE INDEX '{create.IndexName}': USING FTS requires a non-array String column. " +
                $"Column '{columnName}' is {column.Kind}{(column.IsArray ? "[]" : string.Empty)}.");
        }

        // Forbid two FTS indexes on the same column for v1. Deferred-decisions
        // #5 calls out the cheap escape hatch (allow multiple FTS indexes per
        // column with different analyzers); when that ships, drop this check.
        if (datumProvider.TryGetTextSearchIndex(columnName, out _))
        {
            throw new InvalidOperationException(
                $"CREATE INDEX '{create.IndexName}': column '{columnName}' already has a full-text index. " +
                "Multiple FTS indexes per column are deferred to a future PR.");
        }

        string analyzerName = ResolveFtsAnalyzerOption(create);

        // Validate the analyzer is registered before we commit to creating the sidecar.
        if (!Indexing.Fts.FtsAnalyzerRegistry.Default.TryGet(analyzerName, out _))
        {
            string known = string.Join(", ", Indexing.Fts.FtsAnalyzerRegistry.Default.RegisteredNames);
            throw new InvalidOperationException(
                $"CREATE INDEX '{create.IndexName}': analyzer '{analyzerName}' is not registered. " +
                $"Known analyzers: {known}.");
        }

        return new IndexDescriptor(
            create.IndexName,
            create.Columns.ToArray(),
            IsUnique: false,
            Kind: IndexKind.FullText,
            AnalyzerName: analyzerName);
    }

    private const string DefaultFtsAnalyzerName = "simple_en";

    private static string ResolveFtsAnalyzerOption(CreateIndexStatement create)
    {
        if (create.Options is null || create.Options.Count == 0)
        {
            return DefaultFtsAnalyzerName;
        }

        foreach (string key in create.Options.Keys)
        {
            if (!string.Equals(key, "analyzer", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"CREATE INDEX '{create.IndexName}': unknown WITH option '{key}'. " +
                    "USING FTS recognizes only 'analyzer' in v1.");
            }
        }

        return create.Options["analyzer"];
    }

    private static IndexKind ParseIndexKindOrDefault(string? wireValue)
    {
        if (string.IsNullOrEmpty(wireValue))
        {
            return IndexKind.Composite;
        }
        return wireValue.ToLowerInvariant() switch
        {
            "composite" or "btree" => IndexKind.Composite,
            "fulltext" or "fts" => IndexKind.FullText,
            _ => IndexKind.Composite, // forward-compat: unknown kinds load as composite + leave the sidecar alone
        };
    }

    private static string FormatIndexKind(IndexKind kind) => kind switch
    {
        IndexKind.Composite => "composite",
        IndexKind.FullText => "fulltext",
        _ => "composite",
    };

    /// <summary>
    /// Applies a <c>DROP INDEX</c> statement: locates the owning table,
    /// asks the provider to dispose the tree and delete the
    /// <c>.datum-cindex-{name}</c> sidecar, removes the catalog entry, and
    /// persists.
    /// </summary>
    private void ApplyDropIndex(DropIndexStatement drop)
    {
        if (!_indexNameToTable.TryGetValue(drop.IndexName, out string? tableName))
        {
            if (drop.IfExists) return;
            throw new InvalidOperationException(
                $"Index '{drop.IndexName}' is not registered in the catalog.");
        }

        if (!Tables.TryGetValue(tableName, out ITableProvider? provider))
        {
            // Catalog state is inconsistent — the table got dropped without
            // cleaning up the index map. Defensively remove the stale entry
            // and persist.
            _indexNameToTable.Remove(drop.IndexName);
            _catalogStore?.Save(_udfs, _procedures);
            if (drop.IfExists) return;
            throw new InvalidOperationException(
                $"Index '{drop.IndexName}' references missing table '{tableName}'.");
        }

        // Find the descriptor so we can dispatch the per-kind drop.
        IndexDescriptor? descriptor = null;
        if (_persistentTableIndexes.TryGetValue(tableName, out List<IndexDescriptor>? indexList))
        {
            descriptor = indexList.FirstOrDefault(idx =>
                string.Equals(idx.Name, drop.IndexName, StringComparison.OrdinalIgnoreCase));
        }

        if (provider is Providers.DatumFileTableProviderV2 datumProvider)
        {
            IndexKind kind = descriptor?.Kind ?? IndexKind.Composite;
            switch (kind)
            {
                case IndexKind.Composite:
                    datumProvider.DropCompositeIndex(drop.IndexName);
                    break;
                case IndexKind.FullText:
                    datumProvider.DropFtsIndex(drop.IndexName);
                    break;
            }
        }

        _indexNameToTable.Remove(drop.IndexName);
        if (_persistentTableIndexes.TryGetValue(tableName, out List<IndexDescriptor>? list))
        {
            list.RemoveAll(idx => string.Equals(idx.Name, drop.IndexName, StringComparison.OrdinalIgnoreCase));
            if (list.Count == 0)
            {
                _persistentTableIndexes.Remove(tableName);
            }
        }
        _catalogStore?.Save(_udfs, _procedures);
    }

    /// <summary>
    /// Applies a <c>REINDEX</c> statement: rebuilds the table's
    /// <c>.datum-index</c> sidecar from current data. Indexed queries
    /// run after this see acceleration restored. In-memory tables have
    /// no acceleration sidecar, so REINDEX rejects them.
    /// </summary>
    private async Task ApplyReindexTableAsync(ReindexTableStatement reindex)
    {
        if (!Tables.TryGetValue(Key(reindex.TableName), out ITableProvider? provider))
        {
            throw new InvalidOperationException(
                $"Table '{reindex.TableName}' is not registered in the catalog.");
        }

        if (!provider.CanRebuildIndex)
        {
            throw new InvalidOperationException(
                $"Table '{reindex.TableName}' does not support REINDEX " +
                $"(provider type '{provider.GetType().Name}' has no .datum-index sidecar).");
        }

        await provider.RebuildIndexAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Applies an <c>ANALYZE</c> statement: refreshes the cached half of the
    /// <c>.datum-manifest</c> sidecar (top-K, quantiles, histogram, entropy,
    /// kind-specific summaries) by scanning the current data, and rebuilds
    /// the <c>.datum-index</c> acceleration sidecar so the planner's
    /// chunk-pruning decisions reflect current data. Both passes are
    /// best-effort — providers that don't support either skip that pass.
    /// At least one of the two must be supported, otherwise the table can't
    /// meaningfully be analysed.
    /// </summary>
    private async Task ApplyAnalyzeTableAsync(AnalyzeTableStatement analyze)
    {
        if (!Tables.TryGetValue(Key(analyze.TableName), out ITableProvider? provider))
        {
            throw new InvalidOperationException(
                $"Table '{analyze.TableName}' is not registered in the catalog.");
        }

        if (!provider.CanRebuildIndex && !provider.CanRebuildManifest)
        {
            throw new InvalidOperationException(
                $"Table '{analyze.TableName}' does not support ANALYZE " +
                $"(provider type '{provider.GetType().Name}' has no acceleration sidecar or " +
                "manifest to refresh).");
        }

        if (provider.CanRebuildManifest)
        {
            await provider.RebuildManifestAsync().ConfigureAwait(false);
        }
        if (provider.CanRebuildIndex)
        {
            await provider.RebuildIndexAsync().ConfigureAwait(false);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { /* best-effort cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
    }

    private async Task<Schema> BuildSchemaFromColumnDefinitionsAsync(
        IReadOnlyList<ColumnDefinition> definitions,
        IReadOnlyList<string>? primaryKeyColumnNames)
    {
        ColumnInfo[] columns = new ColumnInfo[definitions.Count];
        int identityColumnIndex = -1;

        // Resolve the PK column-name list (already deduplicated /
        // ordered by the parser — see CreateTableParser) into schema
        // indices, validating along the way.
        int[]? pkSchemaIndices = null;
        if (primaryKeyColumnNames is { Count: > 0 })
        {
            pkSchemaIndices = ResolvePrimaryKeyColumnIndices(definitions, primaryKeyColumnNames);
            ValidatePrimaryKeySize(definitions, pkSchemaIndices);
        }
        HashSet<int>? pkIndexSet = pkSchemaIndices is null ? null : new HashSet<int>(pkSchemaIndices);

        for (int i = 0; i < definitions.Count; i++)
        {
            ColumnDefinition d = definitions[i];
            if (!Model.TypeAnnotationResolver.TryParse(d.TypeName, out DataKind kind, out bool isArray))
            {
                throw new InvalidOperationException(
                    $"Unknown column type '{d.TypeName}' on column '{d.Name}'. " +
                    "Use a DataKind name (Int32, String, Float64, Uuid, ...) optionally " +
                    "suffixed with [] for typed-array columns.");
            }

            Expression? defaultExpression = null;
            if (d.DefaultValue is not null)
            {
                ValidateDefaultExpression(d.DefaultValue, d.Name);
                await ValidateDefaultExpressionFitsColumnAsync(d.DefaultValue, d.Name, kind, isArray)
                    .ConfigureAwait(false);
                defaultExpression = d.DefaultValue;
            }

            IdentitySpec? identity = null;
            if (d.Identity is not null)
            {
                if (identityColumnIndex >= 0)
                {
                    throw new InvalidOperationException(
                        $"Table may have at most one IDENTITY column; both " +
                        $"'{definitions[identityColumnIndex].Name}' and '{d.Name}' carry IDENTITY.");
                }
                ValidateIdentitySpecForColumn(d.Identity, d.Name, kind, isArray);
                identity = d.Identity;
                identityColumnIndex = i;
            }

            // PK columns are implicitly NOT NULL — auto-promote so the
            // Nullable flag is consistent with the runtime check the
            // INSERT layer performs.
            bool isPrimaryKey = pkIndexSet is not null && pkIndexSet.Contains(i);
            bool effectiveNullable = isPrimaryKey ? false : d.Nullable;

            // Computed columns: `GENERATED ALWAYS AS (expr)`. Mutually
            // exclusive with DEFAULT and IDENTITY — the value is derived,
            // not supplied. PRIMARY KEY on a computed column is rejected
            // in v1 because the value depends on other columns and the PK
            // index would need re-keying on every UPDATE of a referenced
            // column; not worth the complexity until a real use case lands.
            if (d.ComputedExpression is not null)
            {
                if (defaultExpression is not null)
                {
                    throw new InvalidOperationException(
                        $"Column '{d.Name}': cannot combine DEFAULT and GENERATED ALWAYS AS — " +
                        "computed columns derive their value from other columns and never accept " +
                        "an explicit fallback.");
                }
                if (identity is not null)
                {
                    throw new InvalidOperationException(
                        $"Column '{d.Name}': cannot combine IDENTITY and GENERATED ALWAYS AS.");
                }
                if (isPrimaryKey)
                {
                    throw new InvalidOperationException(
                        $"Column '{d.Name}': GENERATED ALWAYS AS columns cannot be part of the " +
                        "PRIMARY KEY in v1.");
                }
            }

            columns[i] = new ColumnInfo(d.Name, kind, effectiveNullable)
            {
                IsArray = isArray,
                DefaultExpression = defaultExpression,
                Identity = identity,
                IsPrimaryKey = isPrimaryKey,
                ComputedExpression = d.ComputedExpression,
            };
        }

        // GENERATED expressions cannot reference other GENERATED columns —
        // the single-pass evaluator in InsertExecutor / UpdateExecutor
        // would see the referenced computed column still NULL and silently
        // produce a NULL result. Lift to topological-sort eval if a real
        // workload needs it.
        ValidateNoComputedToComputedReferences(columns);

        return new Schema(columns, pkSchemaIndices);
    }

    /// <summary>
    /// Rejects schemas where a <c>GENERATED ALWAYS AS</c> expression
    /// references another <c>GENERATED</c> column. Without this gate the
    /// single-pass evaluator silently fills the dependent column with
    /// NULL (the referenced column hasn't been computed yet when the
    /// dependent's expression runs). Users get a clear error at
    /// <c>CREATE TABLE</c> / <c>ALTER TABLE ADD COLUMN</c> time and can
    /// inline the inner expression.
    /// </summary>
    private static void ValidateNoComputedToComputedReferences(IReadOnlyList<ColumnInfo> columns)
    {
        Dictionary<string, ColumnInfo> byName = new(StringComparer.OrdinalIgnoreCase);
        foreach (ColumnInfo c in columns)
        {
            byName[c.Name] = c;
        }

        foreach (ColumnInfo c in columns)
        {
            if (c.ComputedExpression is null) continue;
            HashSet<(string? TableName, string ColumnName)> refs =
                ColumnReferenceCollector.Collect(c.ComputedExpression);
            foreach ((string? _, string refName) in refs)
            {
                if (byName.TryGetValue(refName, out ColumnInfo? referenced) &&
                    referenced.ComputedExpression is not null &&
                    !ReferenceEquals(referenced, c))
                {
                    throw new InvalidOperationException(
                        $"Column '{c.Name}': GENERATED expressions cannot reference other " +
                        $"GENERATED columns (references '{referenced.Name}'). Inline the inner " +
                        "expression instead.");
                }
            }
        }
    }

    /// <summary>
    /// Resolves PK column names (in user-declared order) to schema
    /// indices. Rejects unknown column names and duplicates that
    /// somehow slipped past the parser's PK-list validation.
    /// </summary>
    private static int[] ResolvePrimaryKeyColumnIndices(
        IReadOnlyList<ColumnDefinition> definitions,
        IReadOnlyList<string> primaryKeyColumnNames)
    {
        int[] indices = new int[primaryKeyColumnNames.Count];
        HashSet<int> seen = new();
        for (int p = 0; p < primaryKeyColumnNames.Count; p++)
        {
            string name = primaryKeyColumnNames[p];
            int found = -1;
            for (int i = 0; i < definitions.Count; i++)
            {
                if (string.Equals(definitions[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    found = i;
                    break;
                }
            }
            if (found < 0)
            {
                throw new InvalidOperationException(
                    $"PRIMARY KEY references column '{name}' which is not declared in the table.");
            }
            if (!seen.Add(found))
            {
                throw new InvalidOperationException(
                    $"PRIMARY KEY column '{name}' appears more than once.");
            }
            indices[p] = found;
        }
        return indices;
    }

    /// <summary>
    /// Validates that every PRIMARY KEY column is of a kind the
    /// <c>CompositeKeyEncoder</c> can encode. Rejects array / struct /
    /// blob / decimal / geometric kinds — those are either deferred
    /// (Decimal, Point2D/Point3D) or fundamentally unsuitable for B+Tree
    /// indexing (arrays, structs, large blobs).
    /// </summary>
    /// <remarks>
    /// Single-column PKs use the typed B+Tree (which natively handles
    /// the kind), composite PKs use the bytes-keyed B+Tree fed by
    /// <c>CompositeKeyEncoder</c>. Both paths reject the same unsupported
    /// kinds — this validator catches them at <c>CREATE TABLE</c> time
    /// instead of letting the user discover the gap at the first <c>INSERT</c>.
    /// </remarks>
    private static void ValidatePrimaryKeySize(
        IReadOnlyList<ColumnDefinition> definitions,
        IReadOnlyList<int> pkSchemaIndices)
    {
        foreach (int idx in pkSchemaIndices)
        {
            ColumnDefinition d = definitions[idx];
            if (!Model.TypeAnnotationResolver.TryParse(d.TypeName, out DataKind kind, out bool isArray))
            {
                // Type-parse error will be surfaced by the main loop;
                // skip the size check here so the user sees the more
                // specific error.
                continue;
            }
            if (isArray)
            {
                throw new InvalidOperationException(
                    $"PRIMARY KEY column '{d.Name}' is an array (kind {kind}[]); array kinds " +
                    "are not supported in PRIMARY KEY columns. Consider an inverted index for " +
                    "array contents or a hash projection for unique constraints.");
            }
            if (!IsAcceptedPrimaryKeyKind(kind))
            {
                throw new InvalidOperationException(
                    $"PRIMARY KEY column '{d.Name}' has unsupported kind {kind}. Supported PK " +
                    "kinds: Boolean, Int8–Int128, UInt8–UInt128, Float16/32/64, Date, Time, " +
                    "DateTime, Duration, Uuid, String. Decimal and geometric kinds are deferred.");
            }
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when the kind is supported as a
    /// PRIMARY KEY component. Matches the kinds <c>CompositeKeyEncoder</c>
    /// can encode (used by composite PKs) and that the typed B+Tree
    /// can store inline (used by single-column PKs).
    /// </summary>
    private static bool IsAcceptedPrimaryKeyKind(DataKind kind) =>
        kind is DataKind.Boolean
            or DataKind.Int8 or DataKind.Int16 or DataKind.Int32 or DataKind.Int64 or DataKind.Int128
            or DataKind.UInt8 or DataKind.UInt16 or DataKind.UInt32 or DataKind.UInt64 or DataKind.UInt128
            or DataKind.Float16 or DataKind.Float32 or DataKind.Float64
            or DataKind.Date or DataKind.Time or DataKind.DateTime or DataKind.Duration
            or DataKind.Uuid
            or DataKind.String;


    /// <summary>
    /// Returns the byte size for a fixed-size scalar <paramref name="kind"/>.
    /// Used by the PRIMARY KEY size check; <see langword="false"/> for
    /// variable-size kinds (String, byte arrays, struct, image / audio).
    /// </summary>
    private static bool TryGetFixedKindSizeBytes(DataKind kind, out int size)
    {
        size = kind switch
        {
            DataKind.Boolean or DataKind.Int8 or DataKind.UInt8 => 1,
            DataKind.Int16 or DataKind.UInt16 or DataKind.Float16 => 2,
            DataKind.Int32 or DataKind.UInt32 or DataKind.Float32 or DataKind.Date => 4,
            DataKind.Int64 or DataKind.UInt64 or DataKind.Float64 or
                DataKind.Time or DataKind.DateTime or DataKind.Duration => 8,
            DataKind.Int128 or DataKind.UInt128 or DataKind.Decimal or DataKind.Uuid => 16,
            _ => 0,
        };
        return size > 0;
    }

    /// <summary>
    /// Shared validation for an <see cref="IdentitySpec"/> attached to a
    /// column at <c>CREATE TABLE</c> or <c>ALTER TABLE ADD COLUMN</c>
    /// time. Enforces: integer column kind in the 8/16/32/64-bit range
    /// (Int8…Int64, UInt8…UInt64), non-array, non-zero step, and
    /// seed/step that fit the kind's range. The single-IDENTITY-per-table
    /// check lives at the caller because the "existing IDENTITY" set is
    /// caller-specific (definitions vs. live schema).
    /// </summary>
    private static void ValidateIdentitySpecForColumn(
        IdentitySpec identity, string columnName, DataKind kind, bool isArray)
    {
        if (isArray)
        {
            throw new InvalidOperationException(
                $"Column '{columnName}': IDENTITY is not supported on typed-array columns.");
        }
        if (!DataValueComparer.IsIntegerKind(kind) ||
            kind is DataKind.Int128 or DataKind.UInt128)
        {
            // Int128 / UInt128 are integer kinds per the comparer but
            // don't fit in the prologue's int64 seed/step storage;
            // reject them explicitly so the error names the actual
            // constraint.
            throw new InvalidOperationException(
                $"Column '{columnName}': IDENTITY requires a 8/16/32/64-bit integer column kind " +
                "(Int8/Int16/Int32/Int64 or UInt8/UInt16/UInt32/UInt64); got " + kind + ".");
        }
        if (identity.Step == 0)
        {
            throw new InvalidOperationException(
                $"Column '{columnName}': IDENTITY step must be non-zero.");
        }
        ValidateIdentityValueFitsInKind(kind, identity.Seed, columnName, "seed");
        ValidateIdentityValueFitsInKind(kind, identity.Step, columnName, "step");
    }

    private static void ValidateIdentityValueFitsInKind(DataKind kind, long value, string columnName, string label)
    {
        bool fits = kind switch
        {
            DataKind.Int8 => value is >= sbyte.MinValue and <= sbyte.MaxValue,
            DataKind.Int16 => value is >= short.MinValue and <= short.MaxValue,
            DataKind.Int32 => value is >= int.MinValue and <= int.MaxValue,
            DataKind.Int64 => true,
            DataKind.UInt8 => value is >= 0 and <= byte.MaxValue,
            DataKind.UInt16 => value is >= 0 and <= ushort.MaxValue,
            DataKind.UInt32 => value is >= 0 and <= uint.MaxValue,
            DataKind.UInt64 => value >= 0,
            _ => false,
        };
        if (!fits)
        {
            throw new InvalidOperationException(
                $"Column '{columnName}': IDENTITY {label} {value} does not fit in {kind}.");
        }
    }

    /// <summary>
    /// Validates that a column's <c>DEFAULT</c> expression is a literal
    /// the catalog can persist as a SQL fragment and the INSERT layer
    /// can evaluate without a per-row pipeline. Accepts any
    /// <see cref="LiteralExpression"/>, plus <c>-&lt;numeric literal&gt;</c>
    /// (an arithmetic <see cref="UnaryExpression"/> over a numeric
    /// literal) since the parser models negative number literals that
    /// way.
    /// </summary>
    /// <summary>
    /// Validates a column's <c>DEFAULT</c> expression. Any tableless
    /// expression is accepted — literal, function call (<c>now()</c>,
    /// <c>gen_random_uuid()</c>), arithmetic, CASE, array literal, etc.
    /// The walker rejects shapes that need a source row or a query plan
    /// at evaluation time: <see cref="ColumnReference"/>,
    /// <see cref="SubqueryExpression"/> / <see cref="InSubqueryExpression"/> /
    /// <see cref="ExistsExpression"/>, and
    /// <see cref="WindowFunctionCallExpression"/>. INSERT-time evaluation
    /// uses an empty <see cref="EvaluationFrame"/> so those shapes would
    /// have nothing to resolve against.
    /// </summary>
    private static void ValidateDefaultExpression(Expression expression, string columnName)
    {
        Expression? offending = FindDisallowedDefaultNode(expression);
        if (offending is null) return;

        string offendingKind = offending switch
        {
            ColumnReference col => $"column reference '{(col.TableName is null ? col.ColumnName : col.TableName + "." + col.ColumnName)}'",
            SubqueryExpression => "scalar subquery",
            InSubqueryExpression => "IN-subquery",
            ExistsExpression => "EXISTS subquery",
            WindowFunctionCallExpression => "window function",
            _ => offending.GetType().Name,
        };

        throw new InvalidOperationException(
            $"DEFAULT for column '{columnName}': {offendingKind} is not allowed. " +
            "DEFAULT expressions evaluate with no source row in scope; use a literal, " +
            "a function call (e.g. now(), gen_random_uuid()), or any other tableless " +
            "expression instead.");
    }

    /// <summary>
    /// Eagerly evaluates the <c>DEFAULT</c> expression against an empty
    /// frame at <c>CREATE TABLE</c> / <c>ALTER TABLE ADD COLUMN</c> time
    /// and coerces the result to the column's <see cref="DataKind"/>. If
    /// the coercion fails — type mismatch, out-of-range literal, etc. —
    /// the error surfaces here instead of at the first <c>INSERT</c>'s
    /// per-row evaluation. Side effects from the probe are discarded
    /// (functions like <c>now()</c> / <c>uuidv4()</c> are pure scalars,
    /// so the throwaway value never escapes).
    /// </summary>
    private async Task ValidateDefaultExpressionFitsColumnAsync(
        Expression expression, string columnName, DataKind kind, bool isArray)
    {
        using Arena probeArena = new();
        ExpressionEvaluator evaluator = new(_functions, store: probeArena);
        ColumnLookup emptyLookup = new(Array.Empty<string>());
        Row emptyRow = new(emptyLookup, Array.Empty<DataValue>());
        EvaluationFrame frame = new(emptyRow, probeArena, probeArena);

        // Build a column-info shim with the target kind/nullable/array
        // shape so ConvertValueRefToTarget validates against the real
        // surface. Nullable=true keeps null-allowed; runtime per-row
        // evaluation handles NOT-NULL rejection separately.
        ColumnInfo probeTarget = new(columnName, kind, nullable: true) { IsArray = isArray };

        try
        {
            ValueRef result = await evaluator.EvaluateAsValueRefAsync(
                expression, frame, CancellationToken.None).ConfigureAwait(false);
            _ = ComputedColumnEvaluator.ConvertValueRefToTarget(
                result, probeTarget, probeArena, columnName);
        }
        catch (Exception inner)
            when (inner is InvalidOperationException
                  or NotSupportedException
                  or OverflowException
                  or FormatException
                  or ArgumentException)
        {
            throw new InvalidOperationException(
                $"DEFAULT for column '{columnName}' ({kind}{(isArray ? "[]" : "")}) is not " +
                $"compatible with the column type: {inner.Message}",
                inner);
        }
    }

    private static Expression? FindDisallowedDefaultNode(Expression expression)
    {
        switch (expression)
        {
            case ColumnReference:
            case SubqueryExpression:
            case InSubqueryExpression:
            case ExistsExpression:
            case WindowFunctionCallExpression:
                return expression;

            case BinaryExpression binary:
                return FindDisallowedDefaultNode(binary.Left) ?? FindDisallowedDefaultNode(binary.Right);

            case UnaryExpression unary:
                return FindDisallowedDefaultNode(unary.Operand);

            case LikeExpression like:
                return FindDisallowedDefaultNode(like.Expression)
                    ?? FindDisallowedDefaultNode(like.Pattern)
                    ?? FindDisallowedDefaultNode(like.EscapeCharacter);

            case FunctionCallExpression function:
                foreach (Expression arg in function.Arguments)
                {
                    Expression? offending = FindDisallowedDefaultNode(arg);
                    if (offending is not null) return offending;
                }
                return null;

            case InExpression inExpr:
            {
                Expression? offending = FindDisallowedDefaultNode(inExpr.Expression);
                if (offending is not null) return offending;
                foreach (Expression v in inExpr.Values)
                {
                    offending = FindDisallowedDefaultNode(v);
                    if (offending is not null) return offending;
                }
                return null;
            }

            case BetweenExpression between:
                return FindDisallowedDefaultNode(between.Expression)
                    ?? FindDisallowedDefaultNode(between.Low)
                    ?? FindDisallowedDefaultNode(between.High);

            case IsNullExpression isNull:
                return FindDisallowedDefaultNode(isNull.Expression);

            case CastExpression cast:
                return FindDisallowedDefaultNode(cast.Expression);

            case CaseExpression caseExpr:
            {
                if (caseExpr.Operand is not null)
                {
                    Expression? offending = FindDisallowedDefaultNode(caseExpr.Operand);
                    if (offending is not null) return offending;
                }
                foreach (WhenClause when in caseExpr.WhenClauses)
                {
                    Expression? offending = FindDisallowedDefaultNode(when.Condition)
                        ?? FindDisallowedDefaultNode(when.Result);
                    if (offending is not null) return offending;
                }
                if (caseExpr.ElseResult is not null)
                {
                    return FindDisallowedDefaultNode(caseExpr.ElseResult);
                }
                return null;
            }

            case StructLiteralExpression structLit:
                foreach (StructField field in structLit.Fields)
                {
                    Expression? offending = FindDisallowedDefaultNode(field.Value);
                    if (offending is not null) return offending;
                }
                return null;

            case IndexAccessExpression indexAccess:
                return FindDisallowedDefaultNode(indexAccess.Source)
                    ?? FindDisallowedDefaultNode(indexAccess.Index);

            // Leaf shapes that need no source row: literals, type literals,
            // parameter binders (resolved at INSERT time via the parameter
            // dictionary), current-timestamp, error markers, lambdas
            // (closures over no outer row). All accepted.
            default:
                return null;
        }
    }

    /// <summary>
    /// Applies an <c>ALTER TABLE ADD COLUMN</c> statement. PR10b ships
    /// the additive shape only — the new column must be nullable, the
    /// <c>DEFAULT</c> clause is rejected (existing-row backfill is a
    /// later-PR concern), and computed columns (<c>AS expr</c>) are
    /// reserved for a future PR.
    /// </summary>
    private async Task ApplyAlterTableAddColumnAsync(AlterTableAddColumnStatement alter)
    {
        // PRIMARY KEY columns implicitly take !Nullable from the parser
        // (matches CREATE TABLE). The general NOT-NULL-on-ALTER restriction
        // doesn't apply here because the PK path requires a guaranteed
        // non-null backfill (IDENTITY on a populated table, or an empty
        // table). Other !Nullable callers still hit the restriction below.
        if (!alter.Nullable && !alter.PrimaryKey)
        {
            throw new InvalidOperationException(
                $"ALTER TABLE '{alter.TableName}' ADD COLUMN '{alter.ColumnName}' NOT NULL " +
                "is not yet supported. Existing rows would need a non-null backfill value, " +
                "and the format does not yet persist a missing-value sentinel for that path. " +
                "Add the column nullable (with or without a DEFAULT) for now.");
        }

        if (!Model.TypeAnnotationResolver.TryParse(alter.TypeName, out DataKind kind, out bool isArray))
        {
            throw new InvalidOperationException(
                $"Unknown column type '{alter.TypeName}' on column '{alter.ColumnName}'.");
        }

        // DEFAULT validation: same literal-only rules as CREATE TABLE. The
        // catalog persists the SQL fragment; existing rows continue to read
        // NULL (the column wasn't present), and new INSERTs that omit this
        // column auto-fill via the existing CREATE-TABLE default path.
        Expression? defaultExpr = alter.DefaultValue;
        if (defaultExpr is not null)
        {
            ValidateDefaultExpression(defaultExpr, alter.ColumnName);
            await ValidateDefaultExpressionFitsColumnAsync(defaultExpr, alter.ColumnName, kind, isArray)
                .ConfigureAwait(false);
        }

        // Computed columns: mutually exclusive with DEFAULT — both supply a
        // value, just from different sides. Pre-existing rows in the table
        // read NULL for the new computed column (no recompute pass against
        // historical rows in v1); only INSERTs after the ALTER fire the
        // expression.
        Expression? computedExpr = alter.ComputedExpression;
        if (computedExpr is not null && defaultExpr is not null)
        {
            throw new InvalidOperationException(
                $"ALTER TABLE '{alter.TableName}' ADD COLUMN '{alter.ColumnName}': cannot combine " +
                "DEFAULT and GENERATED ALWAYS AS — pick one.");
        }

        // IDENTITY validation. Mutually exclusive with DEFAULT and
        // computed expression; rejected when the table already carries
        // an IDENTITY column.
        IdentitySpec? identity = alter.Identity;
        if (identity is not null)
        {
            if (defaultExpr is not null || computedExpr is not null)
            {
                throw new InvalidOperationException(
                    $"ALTER TABLE '{alter.TableName}' ADD COLUMN '{alter.ColumnName}': IDENTITY " +
                    "cannot combine with DEFAULT or GENERATED ALWAYS AS — pick one.");
            }
            ValidateIdentitySpecForColumn(identity, alter.ColumnName, kind, isArray);
            if (TryGetTable(alter.TableName, out ITableProvider? existingForIdentity))
            {
                Schema existingSchema = existingForIdentity.GetSchema();
                foreach (ColumnInfo existing in existingSchema.Columns)
                {
                    if (existing.Identity is not null)
                    {
                        throw new InvalidOperationException(
                            $"ALTER TABLE '{alter.TableName}' ADD COLUMN '{alter.ColumnName}': " +
                            $"table already has an IDENTITY column '{existing.Name}'. Only one " +
                            "IDENTITY column is allowed per table.");
                    }
                }
            }
        }

        // Reject computed-to-computed dependencies against the table's
        // existing schema. Same rationale as the CREATE TABLE gate: the
        // single-pass row-fill evaluator can't see another computed
        // column's value during evaluation.
        if (computedExpr is not null && TryGetTable(alter.TableName, out ITableProvider? existingProvider))
        {
            Schema existingSchema = existingProvider.GetSchema();
            HashSet<(string? TableName, string ColumnName)> refs =
                ColumnReferenceCollector.Collect(computedExpr);
            foreach ((string? _, string refName) in refs)
            {
                foreach (ColumnInfo existing in existingSchema.Columns)
                {
                    if (string.Equals(existing.Name, refName, StringComparison.OrdinalIgnoreCase) &&
                        existing.ComputedExpression is not null)
                    {
                        throw new ExecutionException(
                            $"ALTER TABLE '{alter.TableName}' ADD COLUMN '{alter.ColumnName}': " +
                            $"GENERATED expressions cannot reference other GENERATED columns " +
                            $"(references '{existing.Name}'). Inline the inner expression instead.");
                    }
                }
            }
        }

        // PRIMARY KEY validation — only one PK per table, and on a
        // non-empty table we need IDENTITY to supply unique non-null
        // values for existing rows (DEFAULT doesn't backfill historical
        // rows in the current writer, and a plain PK column would leave
        // every existing row NULL).
        if (alter.PrimaryKey)
        {
            if (computedExpr is not null)
            {
                throw new InvalidOperationException(
                    $"ALTER TABLE '{alter.TableName}' ADD COLUMN '{alter.ColumnName}': PRIMARY KEY " +
                    "cannot combine with GENERATED ALWAYS AS (computed column).");
            }
            if (TryGetTable(alter.TableName, out ITableProvider? existingForPk))
            {
                Schema existingSchema = existingForPk.GetSchema();
                if (existingSchema.PrimaryKeyColumnIndices.Count > 0)
                {
                    string existingPkName = existingSchema.Columns[existingSchema.PrimaryKeyColumnIndices[0]].Name;
                    throw new InvalidOperationException(
                        $"ALTER TABLE '{alter.TableName}' ADD COLUMN '{alter.ColumnName}': table already " +
                        $"has a PRIMARY KEY (column '{existingPkName}'). Only one PRIMARY KEY per table " +
                        "is supported.");
                }
                if (existingForPk.GetRowCount() > 0 && identity is null)
                {
                    throw new InvalidOperationException(
                        $"ALTER TABLE '{alter.TableName}' ADD COLUMN '{alter.ColumnName}' PRIMARY KEY " +
                        "on a non-empty table requires GENERATED IDENTITY so existing rows can be " +
                        "backfilled with unique non-null values. Either truncate the table first or " +
                        "declare the column GENERATED ALWAYS AS IDENTITY PRIMARY KEY.");
                }
            }
        }

        ColumnInfo column = new(alter.ColumnName, kind, nullable: true)
        {
            IsArray = isArray,
            DefaultExpression = defaultExpr,
            ComputedExpression = computedExpr,
            Identity = identity,
        };
        this[alter.TableName].AddColumn(column);

        // V2-F2: backfill the just-added computed column against the
        // table's historical rows. provider.AddColumn() pumped NULLs into
        // every existing row's slot for the new column; now we scan the
        // post-mutation snapshot, evaluate the expression per row, and
        // dispatch one UpdateRows call with the computed values.
        //
        // Caveat: non-deterministic calls (now(), uuidv4(), random()) get
        // captured at ALTER time, so every historical row sees the value
        // computed during this scan — not the original INSERT time. New
        // INSERTs after the ALTER continue to evaluate per row, matching
        // the v1 INSERT-time behaviour.
        if (computedExpr is not null)
        {
            await BackfillComputedColumnAsync(alter.TableName, column).ConfigureAwait(false);
        }

        // Promote the new column to PRIMARY KEY. AddColumn has already
        // committed the column (with IDENTITY backfill if specified), so
        // the column is populated. EnablePrimaryKeyAsync scans, builds the
        // PK index, and flips the footer's PrimaryKeyColumnIndices. On any
        // failure (NULL in column, duplicate value) the partial sidecar is
        // cleaned up — we additionally drop the just-added column so the
        // table returns to its pre-ALTER state.
        if (alter.PrimaryKey)
        {
            if (!TryGetTable(alter.TableName, out ITableProvider? provider))
            {
                throw new InvalidOperationException(
                    $"ALTER TABLE '{alter.TableName}' ADD COLUMN '{alter.ColumnName}' PRIMARY KEY: " +
                    "provider for table disappeared between AddColumn and EnablePrimaryKey.");
            }

            int newColumnIndex = -1;
            Schema postAddSchema = provider.GetSchema();
            for (int i = 0; i < postAddSchema.Columns.Count; i++)
            {
                if (string.Equals(postAddSchema.Columns[i].Name, alter.ColumnName, StringComparison.OrdinalIgnoreCase))
                {
                    newColumnIndex = i;
                    break;
                }
            }
            if (newColumnIndex < 0)
            {
                throw new InvalidOperationException(
                    $"ALTER TABLE '{alter.TableName}' ADD COLUMN '{alter.ColumnName}' PRIMARY KEY: " +
                    "could not locate freshly-added column in the post-AddColumn schema.");
            }

            try
            {
                await provider.EnablePrimaryKeyAsync(newColumnIndex).ConfigureAwait(false);
            }
            catch
            {
                // Roll back the just-added column. Use best-effort because
                // the rollback path itself shouldn't fail on a freshly-
                // added column (it isn't part of any PK at this point, so
                // DropColumn's PK rejection doesn't apply).
                try { provider.DropColumn(alter.ColumnName); } catch { /* swallow */ }
                throw;
            }
        }
    }

    /// <summary>
    /// Streams the table's historical rows through the new column's
    /// <see cref="ColumnInfo.ComputedExpression"/>, then dispatches a
    /// single page-COW <c>UpdateRows</c> call that installs the computed
    /// values in place of the NULL pump that <c>provider.AddColumn</c>
    /// just emitted.
    /// </summary>
    private async Task BackfillComputedColumnAsync(string tableName, ColumnInfo column)
    {
        if (!TryGetTable(tableName, out ITableProvider? provider)) return;
        if (!provider.CanUpdateRows)
        {
            // Without an UpdateRows path we can't install values into
            // historical rows. Surface the gap explicitly so a user
            // doesn't silently get all-NULL historical values. Roll
            // back the just-added column too — leaving it half-added
            // would force the user to manually DROP before retrying.
            try { provider.DropColumn(column.Name); } catch { /* best-effort rollback */ }
            throw new InvalidOperationException(
                $"ALTER TABLE '{tableName}' ADD COLUMN '{column.Name}' AS (...): " +
                $"provider type '{provider.GetType().Name}' does not support UpdateRows, " +
                "so historical rows cannot be backfilled with the computed expression.");
        }

        // Wrap the backfill in a try/catch — if any per-row evaluation
        // or coercion throws, the column is already committed (the
        // writer's AddColumn finalised its tail flip before we got
        // here). Drop the half-added column so the table returns to
        // its pre-ALTER state; the user-facing error then mirrors what
        // the same failure would look like at INSERT time.
        try
        {
            await BackfillComputedColumnAsync(provider, column).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort rollback. DropColumn shouldn't fail on a
            // freshly-added column (PK rejection doesn't apply; the
            // column was just nulled), but if it does we still want to
            // surface the original backfill exception, not the
            // rollback failure.
            try { provider.DropColumn(column.Name); } catch { /* swallow */ }
            throw;
        }
    }

    private async Task BackfillComputedColumnAsync(ITableProvider provider, ColumnInfo column)
    {
        Schema schema = provider.GetSchema();
        int newColIdx = -1;
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            if (string.Equals(schema.Columns[i].Name, column.Name, StringComparison.OrdinalIgnoreCase))
            {
                newColIdx = i;
                break;
            }
        }
        if (newColIdx < 0) return;

        using Arena workArena = new();
        ExpressionEvaluator evaluator = new(Functions, sidecarRegistry: SidecarRegistry);
        List<RowUpdateRequest> requests = new();
        long liveRowIndex = 0;

        await foreach (RowBatch batch in provider.ScanAsync(
            requiredColumns: null,
            filterHint: null,
            targetArena: null,
            cancellationToken: CancellationToken.None).ConfigureAwait(false))
        {
            try
            {
                Arena scanArena = batch.Arena;
                for (int r = 0; r < batch.Count; r++, liveRowIndex++)
                {
                    Row row = batch[r];
                    EvaluationFrame frame = new(
                        row,
                        scanArena,
                        workArena,
                        outerRow: null,
                        sidecarRegistry: SidecarRegistry,
                        types: null);
                    ValueRef result = await evaluator.EvaluateAsValueRefAsync(
                        column.ComputedExpression!, frame, CancellationToken.None).ConfigureAwait(false);
                    DataValue computed = ComputedColumnEvaluator.ConvertValueRefToTarget(
                        result, column, workArena, column.Name);

                    // Skip rows whose computed value is NULL — the column's
                    // pages already hold NULL after AddColumn's pump, so an
                    // UpdateRows request would be a no-op. Keeps the batch
                    // tight when an expression like `nullable_col + 1`
                    // produces NULL for many rows.
                    if (computed.IsNull) continue;

                    requests.Add(new RowUpdateRequest(
                        liveRowIndex,
                        new Dictionary<int, DataValue> { [newColIdx] = computed }));
                }
            }
            finally
            {
                batch.Dispose();
            }
        }

        if (requests.Count > 0)
        {
            await provider.UpdateRowsAsync(requests, workArena).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Applies an <c>ALTER TABLE DROP COLUMN</c> statement. The column
    /// is soft-dropped (tombstoned) on the underlying provider; the
    /// data block stays on disk for compaction-time reclamation.
    /// </summary>
    private void ApplyAlterTableDropColumn(AlterTableDropColumnStatement alter)
    {
        if (!TryGetTable(alter.TableName, out ITableProvider? provider))
        {
            throw new InvalidOperationException(
                $"Table '{alter.TableName}' is not registered in the catalog.");
        }

        // Honor IF EXISTS — schema lookup is the cheapest way to ask
        // "does this column exist?" without poking at provider internals.
        bool columnPresent = false;
        bool columnIsPrimaryKey = false;
        Schema schema = provider.GetSchema();
        foreach (ColumnInfo c in schema.Columns)
        {
            if (string.Equals(c.Name, alter.ColumnName, StringComparison.OrdinalIgnoreCase))
            {
                columnPresent = true;
                columnIsPrimaryKey = c.IsPrimaryKey;
                break;
            }
        }
        if (!columnPresent)
        {
            if (alter.IfExists) return;
            throw new InvalidOperationException(
                $"Column '{alter.ColumnName}' does not exist on table '{alter.TableName}'.");
        }
        if (columnIsPrimaryKey)
        {
            // PK columns are load-bearing on the prologue's PK index
            // list and on the runtime uniqueness check. Dropping one
            // would leave the table referencing a non-existent column;
            // require the user to drop the constraint first.
            throw new InvalidOperationException(
                $"Column '{alter.ColumnName}' is part of the table's PRIMARY KEY and cannot be " +
                "dropped. Drop the PRIMARY KEY constraint first (e.g., " +
                $"`ALTER TABLE {alter.TableName} DROP CONSTRAINT {alter.TableName}_pkey`).");
        }

        // PG-style dependent-column check: a column referenced by any
        // computed (`GENERATED ALWAYS AS (...)`) column can't be
        // dropped without first dropping the dependents. Silently
        // allowing the drop would leave the computed expression with a
        // dangling name reference that breaks the next INSERT or
        // UPDATE.
        List<string>? dependentComputedColumns = null;
        foreach (ColumnInfo c in schema.Columns)
        {
            if (c.ComputedExpression is null) continue;
            HashSet<(string? TableName, string ColumnName)> refs =
                ColumnReferenceCollector.Collect(c.ComputedExpression);
            foreach ((string? _, string refName) in refs)
            {
                if (string.Equals(refName, alter.ColumnName, StringComparison.OrdinalIgnoreCase))
                {
                    dependentComputedColumns ??= new List<string>();
                    dependentComputedColumns.Add(c.Name);
                    break;
                }
            }
        }
        if (dependentComputedColumns is not null)
        {
            throw new InvalidOperationException(
                $"Cannot drop column '{alter.ColumnName}' from table '{alter.TableName}' because " +
                $"the following GENERATED column(s) depend on it: {string.Join(", ", dependentComputedColumns)}. " +
                "Drop the dependent column(s) first, or alter their expression to remove the reference.");
        }

        // PG-style index cascade: any composite index that covers the
        // dropped column is silently dropped along with it (Postgres
        // behavior — indexes aren't user-visible "dependent objects" the
        // way views and triggers are). Reads-only access to the index map
        // here; mutation is gated on provider being the persistent
        // .datum variant.
        string alterKey = Key(alter.TableName);
        if (_persistentTableIndexes.TryGetValue(alterKey, out List<IndexDescriptor>? indexList) &&
            indexList.Count > 0 &&
            provider is Providers.DatumFileTableProviderV2 datumProvider)
        {
            List<IndexDescriptor>? indexesToDrop = null;
            foreach (IndexDescriptor index in indexList)
            {
                foreach (string col in index.Columns)
                {
                    if (string.Equals(col, alter.ColumnName, StringComparison.OrdinalIgnoreCase))
                    {
                        indexesToDrop ??= new List<IndexDescriptor>();
                        indexesToDrop.Add(index);
                        break;
                    }
                }
            }
            if (indexesToDrop is not null)
            {
                foreach (IndexDescriptor index in indexesToDrop)
                {
                    switch (index.Kind)
                    {
                        case IndexKind.FullText:
                            datumProvider.DropFtsIndex(index.Name);
                            break;
                        case IndexKind.Composite:
                        default:
                            datumProvider.DropCompositeIndex(index.Name);
                            break;
                    }
                    indexList.Remove(index);
                    _indexNameToTable.Remove(index.Name);
                }
                if (indexList.Count == 0)
                {
                    _persistentTableIndexes.Remove(alterKey);
                }
                // Persist the index removals before DropColumn runs — if
                // DropColumn fails, we've already lost the index files,
                // and the catalog json should reflect that.
                _catalogStore?.Save(_udfs, _procedures);
            }
        }

        this[alter.TableName].DropColumn(alter.ColumnName);
    }

    /// <summary>
    /// Applies <c>ALTER TABLE name DROP CONSTRAINT constraint_name [IF EXISTS]</c>.
    /// In v1 the only constraint kind that can be dropped is PRIMARY KEY,
    /// whose auto-derived name is <c>&lt;table&gt;_pkey</c>. Other constraint
    /// names produce a PG-flavored "does not exist" error (suppressed by
    /// <c>IF EXISTS</c>).
    /// </summary>
    private async Task ApplyAlterTableDropConstraintAsync(AlterTableDropConstraintStatement alter)
    {
        if (!TryGetTable(alter.TableName, out ITableProvider? provider))
        {
            throw new InvalidOperationException(
                $"Table '{alter.TableName}' is not registered in the catalog.");
        }

        // The PK constraint name might be user-supplied (stored in
        // _persistentTablePkNames) or derived from the table name
        // (<table>_pkey). GetPrimaryKeyConstraintName returns whichever
        // applies. v1 is PK-only; future PRs extend this to UNIQUE /
        // FK / CHECK names.
        string expectedPkName = GetPrimaryKeyConstraintName(alter.TableName);

        if (string.Equals(alter.ConstraintName, expectedPkName, StringComparison.OrdinalIgnoreCase))
        {
            Schema schema = provider.GetSchema();
            if (schema.PrimaryKeyColumnIndices.Count == 0)
            {
                if (alter.IfExists) return;
                throw new InvalidOperationException(
                    $"constraint \"{alter.ConstraintName}\" of relation \"{alter.TableName}\" does not exist");
            }

            await provider.DisablePrimaryKeyAsync().ConfigureAwait(false);
            // Constraint no longer exists — clear the custom-name binding
            // so a subsequent ADD CONSTRAINT (when we ship it) starts from
            // a clean slate, and so the catalog file doesn't carry a stale
            // name for a non-existent constraint.
            if (_persistentTablePkNames.Remove(Key(alter.TableName)))
            {
                _catalogStore?.Save(_udfs, _procedures);
            }
            return;
        }

        // Name didn't match the PK convention. In v1 there are no other
        // droppable constraint names, so this is always "does not exist".
        if (alter.IfExists) return;
        throw new InvalidOperationException(
            $"constraint \"{alter.ConstraintName}\" of relation \"{alter.TableName}\" does not exist");
    }

    /// <summary>
    /// Applies <c>ALTER TABLE name ALTER COLUMN col DROP { IDENTITY | DEFAULT } [IF EXISTS]</c>.
    /// Validates that the column exists and (for DROP IDENTITY without
    /// IF EXISTS) that the attribute being dropped is actually present.
    /// </summary>
    private async Task ApplyAlterTableAlterColumnDropAsync(AlterTableAlterColumnDropStatement alter)
    {
        if (!TryGetTable(alter.TableName, out ITableProvider? provider))
        {
            throw new InvalidOperationException(
                $"Table '{alter.TableName}' is not registered in the catalog.");
        }

        Schema schema = provider.GetSchema();
        int columnIndex = -1;
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            if (string.Equals(schema.Columns[i].Name, alter.ColumnName, StringComparison.OrdinalIgnoreCase))
            {
                columnIndex = i;
                break;
            }
        }
        if (columnIndex < 0)
        {
            throw new InvalidOperationException(
                $"column \"{alter.ColumnName}\" of relation \"{alter.TableName}\" does not exist");
        }

        ColumnInfo column = schema.Columns[columnIndex];

        switch (alter.Target)
        {
            case AlterColumnDropTarget.Identity:
                if (column.Identity is null)
                {
                    if (alter.IfExists) return;
                    throw new InvalidOperationException(
                        $"column \"{alter.ColumnName}\" of relation \"{alter.TableName}\" is not an IDENTITY column");
                }
                await provider.DropColumnIdentityAsync(columnIndex).ConfigureAwait(false);
                return;

            case AlterColumnDropTarget.Default:
                // PG treats DROP DEFAULT as idempotent — no error when the
                // column has no default. Match that behavior whether or
                // not IF EXISTS is supplied.
                await provider.DropColumnDefaultAsync(columnIndex).ConfigureAwait(false);
                return;

            default:
                throw new InvalidOperationException(
                    $"ALTER COLUMN DROP target {alter.Target} is not implemented.");
        }
    }

    /// <summary>
    /// Resolves the storage path for a new persistent table. Returns
    /// the absolute path the <c>.datum</c> file should land at.
    /// </summary>
    private string ResolveCreateTablePath(string tableName, string? explicitPath)
    {
        if (explicitPath is not null)
        {
            // Explicit AT clause — use as-is, but if relative, anchor
            // to the catalog directory so it ends up reachable.
            return System.IO.Path.IsPathRooted(explicitPath)
                ? explicitPath
                : ResolveTablePath(explicitPath);
        }

        // Default: {catalog_dir}/{tablename}.datum. _catalogStore is
        // non-null here because the persistent path requires a
        // catalog file (validated upstream).
        string catalogDir = System.IO.Path.GetDirectoryName(_catalogStore!.Path) ?? Environment.CurrentDirectory;
        return System.IO.Path.Combine(catalogDir, tableName + ".datum");
    }

    /// <summary>
    /// Resolves a stored path to an absolute path. Relative paths are
    /// anchored to the catalog directory; absolute paths are returned
    /// unchanged.
    /// </summary>
    private string ResolveTablePath(string storedPath)
    {
        if (System.IO.Path.IsPathRooted(storedPath)) return storedPath;
        string catalogDir = _catalogStore is null
            ? Environment.CurrentDirectory
            : System.IO.Path.GetDirectoryName(_catalogStore.Path) ?? Environment.CurrentDirectory;
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(catalogDir, storedPath));
    }

    /// <summary>
    /// Converts an absolute path to a form suitable for the catalog
    /// json: relative to the catalog directory when the path lives
    /// underneath it, absolute otherwise. Keeps catalog-internal
    /// tables portable while letting <c>AT '/abs/path'</c> tables
    /// stay reachable.
    /// </summary>
    private string ToPersistedPath(string absolutePath)
    {
        if (_catalogStore is null) return absolutePath;
        string catalogDir = System.IO.Path.GetDirectoryName(_catalogStore.Path) ?? Environment.CurrentDirectory;
        string relative = System.IO.Path.GetRelativePath(catalogDir, absolutePath);
        // GetRelativePath returns the absolute path back when there's
        // no shared root (e.g. different drive on Windows). Use the
        // absolute form in that case.
        if (System.IO.Path.IsPathRooted(relative)) return absolutePath;
        // Keep "../"-prefixed paths as absolute too — they're valid
        // but more fragile across catalog moves.
        if (relative.StartsWith("..")) return absolutePath;
        return relative;
    }

    /// <summary>
    /// Snapshots the current persistent-table set into the wire format
    /// for the catalog json. Called by <see cref="CatalogStore.Save"/>
    /// at every save.
    /// </summary>
    private IReadOnlyList<CatalogFileTableEntry> SnapshotPersistentTablesForSave()
    {
        List<CatalogFileTableEntry> result = new(_persistentTableEntries.Count);
        foreach ((string name, string path) in _persistentTableEntries)
        {
            CatalogFileTableEntry entry = new() { Name = name, FilePath = path };
            if (_persistentTableIndexes.TryGetValue(name, out List<IndexDescriptor>? indexes) && indexes.Count > 0)
            {
                List<CatalogFileIndexEntry> indexEntries = new(indexes.Count);
                foreach (IndexDescriptor index in indexes)
                {
                    indexEntries.Add(new CatalogFileIndexEntry
                    {
                        Name = index.Name,
                        Columns = new List<string>(index.Columns),
                        IsUnique = index.IsUnique,
                        Kind = FormatIndexKind(index.Kind),
                        AnalyzerName = index.AnalyzerName,
                    });
                }
                entry.Indexes = indexEntries;
            }
            if (_persistentTablePkNames.TryGetValue(name, out string? pkName))
            {
                entry.PrimaryKeyConstraintName = pkName;
            }
            result.Add(entry);
        }
        return result;
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
        if (Tables.ContainsKey(Key(name)))
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
        if (Tables.TryGetValue(Key(name), out provider))
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
            if (Tables.TryGetValue(Key(name), out ITableProvider? provider))
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
        string key = Key(tableDescriptor.Name);
        if (Parent is TableCatalog parent && parent.Tables.ContainsKey(key))
        {
            throw new ArgumentException($"A table with the name '{tableDescriptor.Name}' is already registered in the parent catalog.");
        }
        // v2 reader validates magic + version inside its constructor via
        // DatumFileReaderV2.Open. Files written by older format versions
        // throw InvalidDataException at open time.
        DatumFileTableProviderV2 provider = new(tableDescriptor, Pool);
        if (Tables.TryAdd(key, provider))
        {
            RegisterProviderSidecar(provider);
            return Tables[key];
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
        string key = tableProvider.Name.ToString();

        if (Parent is TableCatalog parent && parent.Tables.ContainsKey(key))
        {
            throw new ArgumentException($"A table with the name '{tableProvider.Name}' is already registered in the parent catalog.");
        }
        else if (Tables.TryAdd(key, tableProvider))
        {
            RegisterProviderSidecar(tableProvider);
            return Tables[key];
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
        // Wire the registry first so mutations can swap sidecar sources
        // even on tabular-only providers (no current sidecar) that may
        // produce one later via AppendRows.
        datumProvider.SidecarRegistry = SidecarRegistry;
        if (datumProvider.Sidecar is not { } source) return;

        datumProvider.SidecarStoreId = SidecarRegistry.Register(source);
    }

    /// <summary>
    /// Removes a previously registered table from the catalog, cleaning up any
    /// </summary>
    public void Remove(string tableName)
    {
        string key = Key(tableName);
        if (Tables.TryGetValue(key, out ITableProvider? provider))
        {
            // TODO: when we properly support information_schema then we'll need
            // to take steps to prevent dropping it. Leaving this here until that time
            // since this is where we'd want to check.
            // NOTE: you can't drop from the parent catalog
            Tables.TryRemove(key, out _);
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
                if (!Tables.ContainsKey(provider.Name.ToString()))
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
        // Dispose locally-registered providers; parent-catalog providers
        // remain owned by the parent. Best-effort: a misbehaving provider's
        // Dispose shouldn't leak handles for its siblings.
        foreach (ITableProvider provider in Tables.Values)
        {
            try { provider.Dispose(); }
            catch { /* best-effort cleanup */ }
        }
        Tables.Clear();
    }


}
