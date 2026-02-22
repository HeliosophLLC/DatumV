using DatumIngest.Catalog.Plans;
using DatumIngest.Catalog.Providers;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog.Executors;

/// <summary>
/// Owns the table-DDL pipeline for
/// <see cref="TableCatalog.PlanAsync(Statement)"/>: <c>CREATE TABLE</c>
/// and <c>DROP TABLE</c>. ALTER variants live in their own executor.
/// </summary>
internal static class TableExecutor
{
    /// <summary>
    /// Applies a <c>CREATE TABLE</c> statement: validates the
    /// <c>AT 'path'</c> clause against
    /// <see cref="TableCatalog.AllowExplicitTablePaths"/>, resolves the
    /// storage location, materialises the table (in-memory for TEMP, an
    /// empty <c>.datum</c> file for persistent), registers it with the
    /// catalog, and persists the entry in the catalog json (persistent
    /// only).
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
    public static async Task<IQueryPlan> CreateTableAsync(
        TableCatalog catalog, CreateTableStatement create, string? sourceText = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(create);

        // Existence check is against the explicit target location (after
        // ResolveForCreate picks the schema for unqualified names below)
        // — checking via the search-path walker would let a same-named
        // table on a later path entry mask the new-table location.
        // For TEMP, the target is always public.{name}.
        string existenceCheckName = create.SchemaName is not null
            ? new QualifiedName(create.SchemaName, create.TableName).ToString()
            : create.IsTemp
                ? new QualifiedName("public", create.TableName).ToString()
                : new QualifiedName(
                    catalog.FirstWritableSchema() ?? "public",
                    create.TableName).ToString();

        if (catalog.HasTable(existenceCheckName))
        {
            if (create.IfNotExists) return EmptyQueryPlan.Instance;
            throw new InvalidOperationException(
                $"Table '{create.TableName}' already exists.");
        }

        // Build ColumnInfo[] from the AST's ColumnDefinition list.
        Schema schema = await catalog.BuildSchemaFromColumnDefinitionsAsync(create.Columns, create.PrimaryKeyColumns)
            .ConfigureAwait(false);

        if (create.IsTemp)
        {
            // TEMP tables always live in `public`; the parser allows
            // CREATE TEMP TABLE schema.t in principle but the semantics
            // are nonsensical. Reject explicit qualification.
            if (create.SchemaName is not null)
            {
                throw new InvalidOperationException(
                    $"CREATE TEMP TABLE cannot specify a schema (got '{create.SchemaName}'). " +
                    "TEMP tables are always session-scoped in the public schema.");
            }
            catalog.Add(new InMemoryTableProvider(catalog.Pool, create.TableName, schema));
            catalog.Events.Raise(new TableCreatedEvent(
                new QualifiedName("public", create.TableName), schema, sourceText));
            return EmptyQueryPlan.Instance;
        }

        // Persistent: ResolveForCreate picks the first DDL-capable schema
        // on the search_path when the user didn't supply an explicit
        // qualifier; explicit qualifiers are validated DDL-capable
        // (system / information_schema / datum_catalog throw cleanly).
        SchemaResolver resolver = new(catalog, catalog.SearchPath);
        QualifiedName qn = resolver.ResolveForCreate(create.SchemaName, create.TableName);

        // Route to the schema's backend. AT-clause / no-catalog-file /
        // file-already-exists validation lives in the backend so it stays
        // with the storage concerns it depends on.
        if (!catalog.TryResolveBackend(qn.Schema, out ITableCatalog? backend))
        {
            throw new InvalidOperationException(
                $"CREATE TABLE '{create.TableName}': no catalog backend is " +
                $"mounted for schema '{qn.Schema}'.");
        }
        backend.CreatePersistentTable(
            qn,
            schema,
            create.StoragePath,
            create.PrimaryKeyConstraintName);

        catalog.Events.Raise(new TableCreatedEvent(qn, schema, sourceText));
        return EmptyQueryPlan.Instance;
    }

    /// <summary>
    /// Applies a <c>DROP TABLE</c> statement: removes the table from
    /// the catalog, disposes its provider, deletes the underlying
    /// <c>.datum</c> file (and companion sidecars), and updates the
    /// catalog json. <c>IF EXISTS</c> suppresses the not-found error.
    /// </summary>
    public static IQueryPlan DropTable(TableCatalog catalog, DropTableStatement drop, string? sourceText = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(drop);

        QualifiedName qn = catalog.ResolveDdlName(drop.SchemaName, drop.TableName);

        // Capture the column schema before the provider is unregistered so
        // the TableDropped event can carry it for subscribers that diff
        // against a prior snapshot. TryGetTable goes through the backend
        // resolver; if the table isn't there we'll fall through to the
        // existing "not registered" branch and never raise.
        Schema? beforeSchema = null;
        if (catalog.TryResolveBackend(qn.Schema, out ITableCatalog? lookupBackend)
            && lookupBackend.TryGetTable(qn, out ITableProvider? provider))
        {
            beforeSchema = provider.GetSchema();
        }

        if (!catalog.TryResolveBackend(qn.Schema, out ITableCatalog? backend) || !backend.DropTable(qn))
        {
            if (drop.IfExists) return EmptyQueryPlan.Instance;
            throw new InvalidOperationException(
                $"Table '{drop.TableName}' is not registered in the catalog.");
        }

        catalog.Events.Raise(new TableDroppedEvent(qn, beforeSchema, sourceText));
        return EmptyQueryPlan.Instance;
    }
}
