using DatumIngest.Catalog.Plans;
using DatumIngest.Catalog.Providers;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog.Executors;

/// <summary>
/// Owns the <c>CREATE TABLE … AS SELECT</c> pipeline for
/// <see cref="TableCatalog.PlanAsync(Statement)"/>: derives the target
/// schema statically from the source query's projection via
/// <see cref="QuerySchemaResolver.ResolveProjectionAsync(QueryExpression, string, CancellationToken)"/>, materialises
/// the table (in-memory for TEMP, an empty <c>.datum</c> file for
/// persistent), then streams the source query's batches through an
/// <see cref="IAppendSession"/> without buffering. On any mid-stream
/// failure the just-created table is dropped so the catalog never
/// surfaces a half-populated CTAS target.
/// </summary>
internal static class CtasExecutor
{
    public static async Task<IQueryPlan> ExecuteAsync(
        TableCatalog catalog, CreateTableAsSelectStatement ctas, string? sourceText = null)
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

        // Existence check against the explicit target location — same
        // logic TableExecutor uses for plain CREATE TABLE.
        string existenceCheckName = ctas.SchemaName is not null
            ? new QualifiedName(ctas.SchemaName, ctas.TableName).ToString()
            : ctas.IsTemp
                ? new QualifiedName("public", ctas.TableName).ToString()
                : new QualifiedName(
                    catalog.FirstWritableSchema() ?? "public",
                    ctas.TableName).ToString();

        if (catalog.HasTable(existenceCheckName))
        {
            if (ctas.IfNotExists) return EmptyQueryPlan.Instance;
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

        Schema schema = BuildSchemaFromProjection(projection, ctas.TableName);

        // Materialise the table. CreatePersistentTable returns the
        // provider; Add on the in-memory side does the same. Both paths
        // hand back the provider we'll open the append session against.
        ITableProvider provider;
        QualifiedName createdName;
        ITableCatalog createdBackend;
        if (ctas.IsTemp)
        {
            InMemoryTableProvider inMemory = new(catalog.Pool, ctas.TableName, schema);
            provider = catalog.Add(inMemory);
            createdName = new QualifiedName("public", ctas.TableName);
            if (!catalog.TryResolveBackend("public", out ITableCatalog? publicBackend))
            {
                throw new InvalidOperationException(
                    "CREATE TEMP TABLE: no catalog backend is mounted for schema 'public'.");
            }
            createdBackend = publicBackend;
        }
        else
        {
            SchemaResolver schemaResolver = new(catalog, catalog.SearchPath);
            createdName = schemaResolver.ResolveForCreate(ctas.SchemaName, ctas.TableName);
            if (!catalog.TryResolveBackend(createdName.Schema, out ITableCatalog? backend))
            {
                throw new InvalidOperationException(
                    $"CREATE TABLE '{ctas.TableName}' AS SELECT: no catalog backend is " +
                    $"mounted for schema '{createdName.Schema}'.");
            }
            createdBackend = backend;
            provider = backend.CreatePersistentTable(createdName, schema, primaryKeyConstraintName: null);
        }

        catalog.Events.Raise(new TableCreatedEvent(createdName, schema, sourceText));

        // Stream the source query into the new table. Source projection
        // names line up with target column names by construction (target
        // schema was built from the same ResolvedQuerySchema the runtime
        // ColumnLookup is derived from), so source batches go straight
        // through WriteAsync — no per-row coercion or rebuild needed.
        IQueryPlan sourcePlan = catalog.PlanQuery(ctas.Query);
        IAppendSession? session = null;
        bool committed = false;
        try
        {
            session = provider.BeginAppend();
            await foreach (RowBatch batch in
                sourcePlan.ExecuteAsync(CancellationToken.None).ConfigureAwait(false))
            {
                if (batch.Count == 0) continue;
                await session.WriteAsync(batch).ConfigureAwait(false);
            }
            await session.CommitAsync().ConfigureAwait(false);
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
                try { createdBackend.DropTable(createdName); }
                catch { /* original exception wins */ }
            }
        }

        return EmptyQueryPlan.Instance;
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
