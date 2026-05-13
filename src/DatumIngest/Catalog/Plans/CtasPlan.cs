using System.Runtime.CompilerServices;
using DatumIngest.Catalog.Providers;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog.Plans;

/// <summary>
/// <see cref="StatementPlan"/> for <c>CREATE TABLE … AS SELECT</c>.
/// Composes a child source plan (the SELECT) under the CTAS node so
/// <c>EXPLAIN CREATE TABLE x AS SELECT …</c> walks the full tree without
/// running any side effect. The catalog mutation (materialising the
/// table, raising <see cref="TableCreatedEvent"/>, streaming source rows
/// through an <see cref="IAppendSession"/>, committing or rolling back)
/// happens at execute time inside <see cref="ExecuteImplAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Plan-time work</b> (in <see cref="PlanAsync"/>): pre-flight
/// validation of the statement shape, schema resolution from the source
/// projection, planning the source query into a child <see cref="SelectPlan"/>.
/// No catalog mutation. If <c>IF NOT EXISTS</c> hits an existing table,
/// the factory short-circuits to a <see cref="DdlPlan.NoOp"/> instead of
/// returning a <see cref="CtasPlan"/> — the EXPLAIN tree then reads
/// "no-op, table already exists" which is exactly what executes.
/// </para>
/// <para>
/// <b>Execute-time work</b>: create the table (in-memory for TEMP, an
/// empty <c>.datum</c> file otherwise), open an
/// <see cref="IAppendSession"/>, drain the child source plan into the
/// session, commit. On any mid-stream failure the just-created table is
/// dropped so the catalog never surfaces a half-populated CTAS target.
/// Single-shot: re-iterating throws.
/// </para>
/// </remarks>
internal sealed class CtasPlan : StatementPlan
{
    private readonly CreateTableAsSelectStatement _ctas;
    private readonly string? _sourceText;
    private readonly Schema _targetSchema;
    private readonly QualifiedName _targetName;
    private readonly ITableCatalog _targetBackend;
    private readonly StatementPlan _sourcePlan;
    private int _executed;

    private CtasPlan(
        TableCatalog catalog,
        CreateTableAsSelectStatement ctas,
        string? sourceText,
        Schema targetSchema,
        QualifiedName targetName,
        ITableCatalog targetBackend,
        StatementPlan sourcePlan)
        : base(catalog)
    {
        _ctas = ctas;
        _sourceText = sourceText;
        _targetSchema = targetSchema;
        _targetName = targetName;
        _targetBackend = targetBackend;
        _sourcePlan = sourcePlan;

        ExplainPlanNode tree = new()
        {
            OperatorName = "CreateTableAsSelect",
            Details = $"target={targetName}, columns={targetSchema.Columns.Count}",
            EstimatedRows = 0,
        };
        tree.Children.Add(sourcePlan.ExplainTree);
        ExplainTree = tree;
    }

    /// <summary>
    /// Plan-time factory. Validates the statement shape, resolves the
    /// target schema from the source query's projection, plans the source
    /// query into a child <see cref="SelectPlan"/>, and resolves the
    /// destination backend. Returns either a fresh <see cref="CtasPlan"/>
    /// or — when the target table already exists and <c>IF NOT EXISTS</c>
    /// was supplied — a <see cref="DdlPlan.NoOp"/> short-circuit.
    /// </summary>
    public static async Task<StatementPlan> PlanAsync(
        TableCatalog catalog,
        CreateTableAsSelectStatement ctas,
        string? sourceText)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(ctas);

        // AT 'path' on plain CREATE TABLE is already rejected; reject it
        // here too so any stray script using the old syntax surfaces a
        // clean error rather than a silently-ignored clause.
        if (ctas.StoragePath is not null)
        {
            throw new InvalidOperationException(
                $"CREATE TABLE '{ctas.TableName}' AS SELECT: AT 'path' is no longer supported. " +
                "Table files always land at <catalog>/data/<schema>/<name>.datum.");
        }

        // TEMP tables always live in `public`; reject an explicit
        // schema qualifier the same way TableExecutor does for plain
        // CREATE TEMP TABLE — schema-qualified TEMP has no coherent
        // meaning.
        if (ctas.IsTemp && ctas.SchemaName is not null)
        {
            throw new InvalidOperationException(
                $"CREATE TEMP TABLE cannot specify a schema (got '{ctas.SchemaName}'). " +
                "TEMP tables are always session-scoped in the public schema.");
        }

        // Existence check against the explicit target location.
        string existenceCheckName = ctas.SchemaName is not null
            ? new QualifiedName(ctas.SchemaName, ctas.TableName).ToString()
            : ctas.IsTemp
                ? new QualifiedName("public", ctas.TableName).ToString()
                : new QualifiedName(
                    catalog.FirstWritableSchema() ?? "public",
                    ctas.TableName).ToString();

        if (catalog.HasTable(existenceCheckName))
        {
            if (ctas.IfNotExists) return DdlPlan.NoOp(catalog, "CreateTableAsSelect", "table already exists; IF NOT EXISTS skipped");
            throw new InvalidOperationException(
                $"Table '{ctas.TableName}' already exists.");
        }

        // Resolve the target schema statically from the source projection.
        // Empty result sets still produce a populated schema this way —
        // matches PostgreSQL's "CREATE TABLE … AS SELECT … WHERE false"
        // semantics where the table is created empty rather than the
        // statement failing for lack of an inferable schema. Compound
        // queries (UNION/INTERSECT/EXCEPT) flow through the same path
        // — the resolver unifies branch column types per position.
        QuerySchemaResolver resolver = new(catalog, catalog.Functions);
        ResolvedQuerySchema projection = await resolver
            .ResolveProjectionAsync(ctas.Query, ctas.TableName, CancellationToken.None)
            .ConfigureAwait(false);

        Schema targetSchema = BuildSchemaFromProjection(projection, ctas.TableName);

        // Resolve the destination backend at plan time so the EXPLAIN tree
        // is informative ("target=public.colors") even before any
        // catalog mutation.
        QualifiedName targetName;
        ITableCatalog targetBackend;
        if (ctas.IsTemp)
        {
            targetName = new QualifiedName("public", ctas.TableName);
            if (!catalog.TryResolveBackend("public", out ITableCatalog? publicBackend))
            {
                throw new InvalidOperationException(
                    "CREATE TEMP TABLE: no catalog backend is mounted for schema 'public'.");
            }
            targetBackend = publicBackend;
        }
        else
        {
            SchemaResolver schemaResolver = new(catalog, catalog.SearchPath);
            targetName = schemaResolver.ResolveForCreate(ctas.SchemaName, ctas.TableName);
            if (!catalog.TryResolveBackend(targetName.Schema, out ITableCatalog? backend))
            {
                throw new InvalidOperationException(
                    $"CREATE TABLE '{ctas.TableName}' AS SELECT: no catalog backend is " +
                    $"mounted for schema '{targetName.Schema}'.");
            }
            targetBackend = backend;
        }

        // Plan the source query at plan time. The resulting SelectPlan
        // becomes a real child of this CtasPlan so the EXPLAIN tree shows
        // the full SELECT subtree under the CTAS node.
        StatementPlan sourcePlan = catalog.PlanQuery(ctas.Query);

        return new CtasPlan(catalog, ctas, sourceText, targetSchema, targetName, targetBackend, sourcePlan);
    }

    /// <inheritdoc />
    public override ExplainPlanNode ExplainTree { get; }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<RowBatch> ExecuteImplAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        BatchContext batchContext)
    {
        if (Interlocked.Exchange(ref _executed, 1) != 0)
        {
            throw new InvalidOperationException(
                $"CtasPlan for '{_targetName}' has already been executed. " +
                "Statement plans represent a single pending execution; re-plan the statement to run it again.");
        }
        cancellationToken.ThrowIfCancellationRequested();

        // Materialise the table. CreatePersistentTable returns the
        // provider; Add on the in-memory side does the same. Both paths
        // hand back the provider we'll open the append session against.
        ITableProvider provider;
        if (_ctas.IsTemp)
        {
            InMemoryTableProvider inMemory = new(Catalog.Pool, _ctas.TableName, _targetSchema);
            provider = Catalog.Add(inMemory);
        }
        else
        {
            provider = _targetBackend.CreatePersistentTable(_targetName, _targetSchema, primaryKeyConstraintName: null);
        }

        Catalog.Events.Raise(new TableCreatedEvent(_targetName, _targetSchema, _sourceText));

        // Stream the child source plan into the new table. Source projection
        // names line up with target column names by construction (target
        // schema was built from the same ResolvedQuerySchema the runtime
        // ColumnLookup is derived from), so source batches go straight
        // through WriteAsync — no per-row coercion or rebuild needed.
        IAppendSession? session = null;
        bool committed = false;
        try
        {
            session = provider.BeginAppend();
            await foreach (RowBatch batch in _sourcePlan
                .ExecuteAsync(cancellationToken, batchContext)
                .ConfigureAwait(false))
            {
                if (batch.Count == 0) continue;
                await session.WriteAsync(batch, cancellationToken).ConfigureAwait(false);
            }
            await session.CommitAsync(cancellationToken).ConfigureAwait(false);
            committed = true;
        }
        finally
        {
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            // Drop the table on any failure path — keeps the catalog
            // free of empty placeholders when the SELECT throws partway
            // through. The drop itself swallows exceptions so we never
            // mask the original failure with a cleanup error.
            if (!committed)
            {
                try { _targetBackend.DropTable(_targetName); }
                catch { /* original exception wins */ }
            }
        }

        yield break;
    }

    /// <summary>
    /// Builds a <see cref="Schema"/> from the resolved projection. Column
    /// names, kinds, nullability, and array flags carry across directly.
    /// Duplicate column names are rejected with a PG-style diagnostic
    /// pointing at the offending name — the user has to disambiguate with
    /// an alias.
    /// </summary>
    private static Schema BuildSchemaFromProjection(ResolvedQuerySchema projection, string tableName)
    {
        if (projection.Columns.Count == 0)
        {
            throw new InvalidOperationException(
                $"CREATE TABLE '{tableName}' AS SELECT: the source query produces no columns.");
        }

        HashSet<string> seenNames = new(StringComparer.OrdinalIgnoreCase);
        ColumnInfo[] columns = new ColumnInfo[projection.Columns.Count];
        for (int i = 0; i < projection.Columns.Count; i++)
        {
            ResolvedColumn resolved = projection.Columns[i];
            if (!seenNames.Add(resolved.ColumnName))
            {
                throw new InvalidOperationException(
                    $"CREATE TABLE '{tableName}' AS SELECT: column \"{resolved.ColumnName}\" " +
                    "specified more than once. Add an alias (e.g. SELECT expr AS name) to " +
                    "disambiguate.");
            }
            columns[i] = new ColumnInfo(resolved.ColumnName, resolved.Kind, resolved.Nullable)
            {
                IsArray = resolved.IsArray,
                IsMultiDim = resolved.IsMultiDim,
            };
        }
        return new Schema(columns);
    }
}
