using Heliosoph.DatumV.Catalog.Plans;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Catalog.Executors;

/// <summary>
/// Owns the index-DDL pipeline for <see cref="TableCatalog.PlanAsync(Statement)"/>.
/// One method per index-related statement (<c>CREATE INDEX</c>,
/// <c>DROP INDEX</c>, <c>REINDEX</c>) — the surface mirrors
/// <see cref="InsertExecutor"/> and the other per-statement executors.
/// </summary>
internal static class IndexExecutor
{
    private const string DefaultFtsAnalyzerName = "simple_en";

    /// <summary>
    /// Applies a <c>CREATE INDEX</c> statement: validates the target table /
    /// columns, asks the provider to materialise a new
    /// <c>.datum-cindex-{name}</c> sidecar (and backfill it from existing
    /// rows), records the index in the catalog descriptor, and persists.
    /// </summary>
    public static async Task CreateIndexAsync(
        TableCatalog catalog, CreateIndexStatement create, string? sourceText = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(create);

        // Validate the target table exists and is owned by this catalog.
        // Resolution honours the new SchemaName (S8): explicit schema →
        // exact lookup; unqualified → walks search_path.
        QualifiedName tableQn = catalog.ResolveDdlName(create.SchemaName, create.TableName);

        if (!catalog.TryResolveBackend(tableQn.Schema, out ITableCatalog? backend)
            || !backend.TryGetTable(tableQn, out ITableProvider? provider))
        {
            throw new ExecutionException(
                $"Table '{create.TableName}' is not registered in the catalog.");
        }

        // Index names are catalog-globally unique (Postgres semantics).
        // Persistent indexes live only on FlatFile, so checking there is enough.
        if (catalog.FlatFileCatalog.TryGetIndexOwner(create.IndexName, out QualifiedName existingOwner))
        {
            if (create.IfNotExists && existingOwner.Equals(tableQn))
            {
                return;
            }

            throw new ExecutionException(
                $"Index '{create.IndexName}' already exists" +
                (existingOwner.Equals(tableQn)
                    ? $" on table '{existingOwner}'."
                    : $" on table '{existingOwner}'. Index names must be unique across the catalog."));
        }

        if (provider is not Providers.DatumFileTableProviderV2 datumProvider)
        {
            throw new ExecutionException(
                $"Table '{create.TableName}' does not support CREATE INDEX. " +
                "Only persistent .datum tables maintain composite indexes; " +
                "TEMP / InMemory / external-source tables are excluded.");
        }

        if (create.Columns.Count == 0)
        {
            throw new ExecutionException(
                $"CREATE INDEX '{create.IndexName}' requires at least one column.");
        }

        // Branch on USING method. Pre-FTS DDL has Method=null → composite.
        IndexKind kind = ResolveCreateIndexKind(create);
        IndexDescriptor descriptor = kind switch
        {
            IndexKind.Composite => BuildCompositeIndexDescriptor(create, datumProvider),
            IndexKind.FullText => BuildFullTextIndexDescriptor(create, datumProvider),
            _ => throw new ExecutionException(
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

        // Persist the index in the backend manifest. The sidecar file
        // creation above lives on the provider; this records the descriptor.
        // Routes through the schema's backend; read-only backends would
        // throw, but we already rejected non-.datum providers above.
        backend.RegisterIndex(tableQn, descriptor);

        catalog.Events.Raise(new IndexCreatedEvent(tableQn, descriptor, sourceText));
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
            _ => throw new ExecutionException(
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
            throw new ExecutionException(
                $"CREATE INDEX '{create.IndexName}': WITH options are not supported for composite (B+Tree) indexes. " +
                "Known options apply to USING FTS only.");
        }

        Schema schema = datumProvider.GetSchema();
        foreach (string columnName in create.Columns)
        {
            ColumnInfo? column = schema.FindColumn(columnName);
            if (column is null)
            {
                throw new ExecutionException(
                    $"CREATE INDEX '{create.IndexName}': column '{columnName}' does not exist on table '{create.TableName}'.");
            }
            if (column.IsArray)
            {
                throw new ExecutionException(
                    $"CREATE INDEX '{create.IndexName}': column '{columnName}' is an array column and cannot be indexed.");
            }
            if (column.Kind is DataKind.Decimal or DataKind.Point2D or DataKind.Point3D)
            {
                throw new ExecutionException(
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
            throw new ExecutionException(
                $"CREATE INDEX '{create.IndexName}': USING FTS requires exactly one column (got {create.Columns.Count}). " +
                "For cross-column search, either create one FTS index per column or use a generated " +
                "concatenation column.");
        }

        string columnName = create.Columns[0];
        Schema schema = datumProvider.GetSchema();
        ColumnInfo? column = schema.FindColumn(columnName);
        if (column is null)
        {
            throw new ExecutionException(
                $"CREATE INDEX '{create.IndexName}': column '{columnName}' does not exist on table '{create.TableName}'.");
        }
        if (column.IsArray || column.Kind != DataKind.String)
        {
            throw new ExecutionException(
                $"CREATE INDEX '{create.IndexName}': USING FTS requires a non-array String column. " +
                $"Column '{columnName}' is {column.Kind}{(column.IsArray ? "[]" : string.Empty)}.");
        }

        // Forbid two FTS indexes on the same column for v1. Deferred-decisions
        // #5 calls out the cheap escape hatch (allow multiple FTS indexes per
        // column with different analyzers); when that ships, drop this check.
        if (datumProvider.TryGetTextSearchIndex(columnName, out _))
        {
            throw new ExecutionException(
                $"CREATE INDEX '{create.IndexName}': column '{columnName}' already has a full-text index. " +
                "Multiple FTS indexes per column are not supported.");
        }

        string analyzerName = ResolveFtsAnalyzerOption(create);

        // Validate the analyzer is registered before we commit to creating the sidecar.
        if (!Indexing.Fts.FtsAnalyzerRegistry.Default.TryGet(analyzerName, out _))
        {
            string known = string.Join(", ", Indexing.Fts.FtsAnalyzerRegistry.Default.RegisteredNames);
            throw new ExecutionException(
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
                throw new ExecutionException(
                    $"CREATE INDEX '{create.IndexName}': unknown WITH option '{key}'. " +
                    "USING FTS recognizes only 'analyzer' in v1.");
            }
        }

        return create.Options["analyzer"];
    }

    /// <summary>
    /// Applies a <c>REINDEX</c> statement: rebuilds the table's
    /// <c>.datum-index</c> sidecar from current data. Indexed queries
    /// run after this see acceleration restored. In-memory tables have
    /// no acceleration sidecar, so REINDEX rejects them.
    /// </summary>
    public static async Task ReindexAsync(TableCatalog catalog, ReindexTableStatement reindex)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(reindex);

        QualifiedName reindexQn = catalog.ResolveDdlName(reindex.SchemaName, reindex.TableName);

        if (!catalog.TryResolveBackend(reindexQn.Schema, out ITableCatalog? reindexBackend)
            || !reindexBackend.TryGetTable(reindexQn, out ITableProvider? provider))
        {
            throw new ExecutionException(
                $"Table '{reindex.TableName}' is not registered in the catalog.");
        }

        if (!provider.CanRebuildIndex)
        {
            throw new ExecutionException(
                $"Table '{reindex.TableName}' does not support REINDEX " +
                $"(provider type '{provider.GetType().Name}' has no .datum-index sidecar).");
        }

        await provider.RebuildIndexAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Applies a <c>DROP INDEX</c> statement: locates the owning table,
    /// asks the provider to dispose the tree and delete the
    /// <c>.datum-cindex-{name}</c> sidecar, removes the catalog entry, and
    /// persists.
    /// </summary>
    public static void DropIndex(TableCatalog catalog, DropIndexStatement drop, string? sourceText = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(drop);

        FlatFileCatalog flatFile = catalog.FlatFileCatalog;

        // Persistent indexes only live in FlatFile; only it can resolve
        // index-name-to-owning-table.
        if (!flatFile.TryGetIndexOwner(drop.IndexName, out QualifiedName tableName))
        {
            if (drop.IfExists) return;

            throw new ExecutionException(
                $"Index '{drop.IndexName}' is not registered in the catalog.");
        }

        if (!flatFile.TryGetTable(tableName, out ITableProvider? provider))
        {
            // Catalog state is inconsistent — the table got dropped without
            // cleaning up the index map. Defensively remove the stale entry
            // and persist.
            flatFile.UnregisterIndex(drop.IndexName, out _);
            if (drop.IfExists) return;

            throw new ExecutionException(
                $"Index '{drop.IndexName}' references missing table '{tableName}'.");
        }

        // Find the descriptor so we can dispatch the per-kind drop.
        IndexDescriptor? descriptor = flatFile.GetTableIndexes(tableName)?.FirstOrDefault(idx =>
            string.Equals(idx.Name, drop.IndexName, StringComparison.OrdinalIgnoreCase));

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

        flatFile.UnregisterIndex(drop.IndexName, out _);

        catalog.Events.Raise(new IndexDroppedEvent(tableName, descriptor, sourceText));
    }
}
