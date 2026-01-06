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
        // (procedural UDF bodies, EXEC, etc.) resolve through this catalog.
        // The closure follows the parent-chain getter so child catalogs
        // inherit the root's models without duplicating registrations.
        this._functions.SetModelCatalogResolver(() => Models);
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
                AddFile(resolved, entry.Name);
                _persistentTableEntries[entry.Name] = entry.FilePath;
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
        return Plan(statement, sql);
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
    public IQueryPlan Plan(Statement statement) => Plan(statement, sourceText: null);

    /// <summary>
    /// Plans an already-parsed <see cref="Statement"/>, threading the original
    /// SQL slice for DDL statements that need to round-trip the source text
    /// through the catalog file. Procedural <c>CREATE FUNCTION</c> and
    /// <c>CREATE PROCEDURE</c> bodies don't have a faithful AST formatter, so
    /// without the slice they fall back to a synthesised header
    /// (<c>CREATE FUNCTION name</c>) that won't reparse on catalog reload.
    /// </summary>
    /// <remarks>
    /// Callers that have parsed via <c>SqlParser.ParseBatchWithText</c> already
    /// have the per-statement slice and should pass it. Callers that built a
    /// statement programmatically (no original SQL) pass <see langword="null"/>
    /// and accept that the body persists as a placeholder.
    /// </remarks>
    public IQueryPlan Plan(Statement statement, string? sourceText)
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

            case ExecStatement exec:
                return PlanExec(exec);

            case CreateTableStatement createTable:
                ApplyCreateTable(createTable);
                return EmptyQueryPlan.Instance;

            case DropTableStatement dropTable:
                ApplyDropTable(dropTable);
                return EmptyQueryPlan.Instance;

            case AlterTableAddColumnStatement alterAdd:
                ApplyAlterTableAddColumn(alterAdd);
                return EmptyQueryPlan.Instance;

            case AlterTableDropColumnStatement alterDrop:
                ApplyAlterTableDropColumn(alterDrop);
                return EmptyQueryPlan.Instance;

            case InsertStatement insert:
                InsertExecutor.Execute(this, insert);
                return EmptyQueryPlan.Instance;

            default:
                throw new NotSupportedException(
                    $"Statement type '{statement.GetType().Name}' is not yet supported by Plan(string). " +
                    $"Use the dedicated APIs (e.g. AddFile for file registration) or extend Plan to dispatch this statement.");
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
    /// <c>IF NOT EXISTS</c>, optional <c>AT 'path'</c>. PRIMARY KEY and
    /// IDENTITY are recorded in the AST but not yet enforced
    /// (PR10e/PR10f). PR10b adds <c>DEFAULT &lt;literal&gt;</c> on each
    /// column — validated literal-only at this layer and persisted in
    /// the footer prologue (<see cref="FooterPrologueV4.ColumnDefaults"/>)
    /// for persistent tables; the INSERT layer (PR10c) materialises the
    /// default when a row omits the column.
    /// </remarks>
    private void ApplyCreateTable(CreateTableStatement create)
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
        Schema schema = BuildSchemaFromColumnDefinitions(create.Columns);

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
        }

        DatumFileWriterV2.CreateEmpty(targetPath, descriptors, columnDefaults);

        Add(new TableDescriptor(create.TableName, targetPath));

        // Record in catalog json. Store the path relative to the
        // catalog directory when possible so the catalog moves with
        // the data.
        string persistedPath = ToPersistedPath(targetPath);
        _persistentTableEntries[create.TableName] = persistedPath;
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
        if (!Tables.TryGetValue(drop.TableName, out ITableProvider? provider))
        {
            if (drop.IfExists) return;
            throw new InvalidOperationException(
                $"Table '{drop.TableName}' is not registered in the catalog.");
        }

        // Capture path BEFORE disposing the provider so we can delete
        // the file. Persistent tables track their path in
        // _persistentTableEntries.
        string? persistedPath = _persistentTableEntries.TryGetValue(drop.TableName, out string? p) ? p : null;

        Tables.TryRemove(drop.TableName, out _);
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

            _persistentTableEntries.Remove(drop.TableName);
            _catalogStore?.Save(_udfs, _procedures);
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

    private Schema BuildSchemaFromColumnDefinitions(IReadOnlyList<ColumnDefinition> definitions)
    {
        ColumnInfo[] columns = new ColumnInfo[definitions.Count];
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
                ValidateDefaultLiteral(d.DefaultValue, d.Name);
                defaultExpression = d.DefaultValue;
            }

            columns[i] = new ColumnInfo(d.Name, kind, d.Nullable)
            {
                IsArray = isArray,
                DefaultExpression = defaultExpression,
            };
        }
        return new Schema(columns);
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
    private static void ValidateDefaultLiteral(Expression expression, string columnName)
    {
        if (IsAcceptedDefaultLiteral(expression)) return;

        throw new InvalidOperationException(
            $"DEFAULT for column '{columnName}' must be a literal expression " +
            "(string, number, boolean, NULL, or a negated numeric literal). " +
            "Function calls and other computed expressions are not yet supported as DEFAULTs.");
    }

    private static bool IsAcceptedDefaultLiteral(Expression expression) =>
        expression switch
        {
            LiteralExpression => true,
            UnaryExpression { Operator: UnaryOperator.Negate, Operand: LiteralExpression literal }
                => literal.Value is sbyte or short or int or long
                    or byte or ushort or uint or ulong
                    or float or double or decimal or Half,
            _ => false,
        };

    /// <summary>
    /// Applies an <c>ALTER TABLE ADD COLUMN</c> statement. PR10b ships
    /// the additive shape only — the new column must be nullable, the
    /// <c>DEFAULT</c> clause is rejected (existing-row backfill is a
    /// later-PR concern), and computed columns (<c>AS expr</c>) are
    /// reserved for a future PR.
    /// </summary>
    private void ApplyAlterTableAddColumn(AlterTableAddColumnStatement alter)
    {
        if (alter.ComputedExpression is not null)
        {
            throw new InvalidOperationException(
                $"ALTER TABLE '{alter.TableName}' ADD COLUMN '{alter.ColumnName}' AS <expr> " +
                "(computed columns) is not yet supported.");
        }
        if (alter.DefaultValue is not null)
        {
            throw new InvalidOperationException(
                $"ALTER TABLE '{alter.TableName}' ADD COLUMN '{alter.ColumnName}' DEFAULT … " +
                "is not yet supported. Existing rows would need backfill, which lands in a " +
                "later PR. Add the column without a DEFAULT for now (existing rows read as NULL).");
        }
        if (!alter.Nullable)
        {
            throw new InvalidOperationException(
                $"ALTER TABLE '{alter.TableName}' ADD COLUMN '{alter.ColumnName}' NOT NULL " +
                "is not yet supported. Existing rows would need a non-null backfill value, " +
                "which requires DEFAULT support that hasn't shipped yet.");
        }

        if (!Model.TypeAnnotationResolver.TryParse(alter.TypeName, out DataKind kind, out bool isArray))
        {
            throw new InvalidOperationException(
                $"Unknown column type '{alter.TypeName}' on column '{alter.ColumnName}'.");
        }

        ColumnInfo column = new(alter.ColumnName, kind, nullable: true) { IsArray = isArray };
        AddColumn(alter.TableName, column);
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
        Schema schema = provider.GetSchema();
        foreach (ColumnInfo c in schema.Columns)
        {
            if (string.Equals(c.Name, alter.ColumnName, StringComparison.OrdinalIgnoreCase))
            {
                columnPresent = true;
                break;
            }
        }
        if (!columnPresent)
        {
            if (alter.IfExists) return;
            throw new InvalidOperationException(
                $"Column '{alter.ColumnName}' does not exist on table '{alter.TableName}'.");
        }

        DropColumn(alter.TableName, alter.ColumnName);
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
            result.Add(new CatalogFileTableEntry { Name = name, FilePath = path });
        }
        return result;
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
        // Wire the registry first so mutations can swap sidecar sources
        // even on tabular-only providers (no current sidecar) that may
        // produce one later via AppendRows.
        datumProvider.SidecarRegistry = SidecarRegistry;
        if (datumProvider.Sidecar is not { } source) return;

        datumProvider.SidecarStoreId = SidecarRegistry.Register(source);
    }

    // ──────────────────── Mutation passthroughs ────────────────────

    /// <summary>
    /// Adds a new (nullable) column to <paramref name="tableName"/>. Throws
    /// <see cref="InvalidOperationException"/> if the resolved provider's
    /// <see cref="ITableProvider.CanAlterColumns"/> is <see langword="false"/>
    /// (e.g. system tables).
    /// </summary>
    public void AddColumn(string tableName, Model.ColumnInfo column)
    {
        ArgumentNullException.ThrowIfNull(column);
        ITableProvider provider = ResolveForMutation(tableName, requireFlag: p => p.CanAlterColumns, op: "AddColumn");
        provider.AddColumn(column);
    }

    /// <summary>
    /// Soft-drops the named column from <paramref name="tableName"/>.
    /// </summary>
    public void DropColumn(string tableName, string columnName)
    {
        ArgumentNullException.ThrowIfNull(columnName);
        ITableProvider provider = ResolveForMutation(tableName, requireFlag: p => p.CanAlterColumns, op: "DropColumn");
        provider.DropColumn(columnName);
    }

    /// <summary>
    /// Opens a streaming append session over <paramref name="tableName"/>.
    /// Caller is responsible for <see cref="IAppendSession.CommitAsync"/>;
    /// disposing without commit aborts.
    /// </summary>
    public IAppendSession BeginAppend(string tableName)
    {
        ITableProvider provider = ResolveForMutation(tableName, requireFlag: p => p.CanAppendRows, op: "BeginAppend");
        return provider.BeginAppend();
    }

    /// <summary>
    /// Appends every batch in <paramref name="batches"/> to <paramref name="tableName"/>
    /// in a single committed unit (open session, drain, commit).
    /// </summary>
    public Task AppendRowsAsync(string tableName, IAsyncEnumerable<RowBatch> batches, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batches);
        ITableProvider provider = ResolveForMutation(tableName, requireFlag: p => p.CanAppendRows, op: "AppendRowsAsync");
        return provider.AppendRowsAsync(batches, cancellationToken);
    }

    /// <summary>
    /// Soft-deletes rows at the given linear indices in <paramref name="tableName"/>.
    /// </summary>
    public void DeleteRows(string tableName, IReadOnlyList<long> rowIndices)
    {
        ArgumentNullException.ThrowIfNull(rowIndices);
        ITableProvider provider = ResolveForMutation(tableName, requireFlag: p => p.CanDeleteRows, op: "DeleteRows");
        provider.DeleteRows(rowIndices);
    }

    private ITableProvider ResolveForMutation(string tableName, Func<ITableProvider, bool> requireFlag, string op)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        if (!TryGetTable(tableName, out ITableProvider? provider))
        {
            throw new KeyNotFoundException($"Table '{tableName}' is not registered in the catalog.");
        }
        if (!requireFlag(provider))
        {
            throw new InvalidOperationException(
                $"Table '{tableName}' is read-only for {op} (provider type {provider.GetType().Name}). " +
                "System tables and read-only providers do not support mutation.");
        }
        return provider;
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
