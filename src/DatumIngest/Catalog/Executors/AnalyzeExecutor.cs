using DatumIngest.Catalog.Plans;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog.Executors;

/// <summary>
/// Owns the <c>ANALYZE</c>-statement pipeline for
/// <see cref="TableCatalog.PlanAsync(Statement)"/>.
/// </summary>
internal static class AnalyzeExecutor
{
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
    public static async Task<StatementPlan> ExecuteAsync(TableCatalog catalog, AnalyzeTableStatement analyze)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(analyze);

        QualifiedName analyzeQn = catalog.ResolveDdlName(analyze.SchemaName, analyze.TableName);
        if (!catalog.TryResolveBackend(analyzeQn.Schema, out ITableCatalog? analyzeBackend)
            || !analyzeBackend.TryGetTable(analyzeQn, out ITableProvider? provider))
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
        return DdlPlan.NoOp(catalog, "Analyze");
    }
}
