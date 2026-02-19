using DatumIngest.Indexing;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog.Executors;

/// <summary>
/// Owns the index-DDL pipeline for <see cref="TableCatalog.PlanAsync(Statement)"/>.
/// One method per index-related statement (<c>CREATE INDEX</c>,
/// <c>DROP INDEX</c>, <c>REINDEX</c>) — the surface mirrors
/// <see cref="InsertExecutor"/> and the other per-statement executors.
/// </summary>
internal static class IndexExecutor
{
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

        FlatFileCatalog flatFile = catalog.FlatFile;

        // Persistent indexes only live in FlatFile; only it can resolve
        // index-name-to-owning-table.
        if (!flatFile.TryGetIndexOwner(drop.IndexName, out QualifiedName tableName))
        {
            if (drop.IfExists) return;
            throw new InvalidOperationException(
                $"Index '{drop.IndexName}' is not registered in the catalog.");
        }

        if (!flatFile.TryGetTable(tableName, out ITableProvider? provider))
        {
            // Catalog state is inconsistent — the table got dropped without
            // cleaning up the index map. Defensively remove the stale entry
            // and persist.
            flatFile.UnregisterIndex(drop.IndexName, out _);
            if (drop.IfExists) return;
            throw new InvalidOperationException(
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
