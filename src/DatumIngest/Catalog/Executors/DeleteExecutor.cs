using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog.Executors;

/// <summary>
/// Owns the <c>DELETE FROM …</c> pipeline for
/// <see cref="TableCatalog.PlanAsync(Statement)"/>: resolves the target
/// provider, walks the live row sequence with a running index, evaluates
/// the optional <c>WHERE</c> predicate per row, and forwards the
/// matching indices to <see cref="ITableProvider.DeleteRows"/>.
/// </summary>
/// <remarks>
/// PR10d ships the simplest correct path: a full scan of the target
/// table feeds a per-row <see cref="ExpressionEvaluator"/>. The scan
/// emits live rows in linear order (post-tombstone from any earlier
/// deletes), which matches the index space
/// <see cref="ITableProvider.DeleteRows"/> expects. A future PR can
/// swap this for a planner-driven row-id projection (predicate
/// pushdown, bitmap-index probes, etc.); the on-disk soft-delete
/// behavior is unchanged.
/// </remarks>
internal static class DeleteExecutor
{
    public static async Task ExecuteAsync(TableCatalog catalog, DeleteStatement delete)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(delete);

        SchemaResolver resolver = new(catalog, catalog.SearchPath);
        QualifiedName qn = resolver.Resolve(delete.SchemaName, delete.TableName);
        if (!catalog.TryGetTable(qn.ToString(), out ITableProvider? provider))
        {
            throw new InvalidOperationException(
                $"DELETE FROM '{delete.TableName}': table is not registered in the catalog.");
        }
        if (!provider.CanDeleteRows)
        {
            throw new InvalidOperationException(
                $"DELETE FROM '{delete.TableName}': provider type {provider.GetType().Name} " +
                "is read-only (CanDeleteRows = false).");
        }

        await ApplyAsync(catalog, provider, delete.Where).ConfigureAwait(false);
    }

    private static async Task ApplyAsync(
        TableCatalog catalog, ITableProvider provider, Expression? predicate)
    {
        // Walk the live row sequence with a running counter. The
        // provider's tombstone index space matches a fresh scan's
        // emission order (per ITableProvider.DeleteRows docs), so the
        // running counter IS the linear row index regardless of any
        // earlier tombstones. We don't shortcut on a null predicate
        // via GetRowCount because some providers (DatumFile in
        // particular) return the gross row count and would over-number
        // the index list when prior deletes are in play; the scan-based
        // walk stays correct without paying for predicate evaluation.
        ExpressionEvaluator? evaluator = predicate is null
            ? null
            : new ExpressionEvaluator(
                functions: catalog.Functions,
                sidecarRegistry: catalog.SidecarRegistry);

        List<long> matched = new();
        long rowIndex = 0;

        await foreach (RowBatch batch in provider.ScanAsync(
            requiredColumns: null,
            filterHint: null,
            targetArena: null,
            cancellationToken: CancellationToken.None).ConfigureAwait(false))
        {
            try
            {
                Arena sourceArena = batch.Arena;

                // Target arena = source arena: predicate eval doesn't
                // produce DataValues that need to outlive this batch
                // (the result is a bool we consume immediately).
                for (int r = 0; r < batch.Count; r++, rowIndex++)
                {
                    if (evaluator is null)
                    {
                        // Unconditional DELETE — every live row matches.
                        matched.Add(rowIndex);
                        continue;
                    }

                    Row row = batch[r];
                    EvaluationFrame frame = new(
                        row,
                        sourceArena,
                        sourceArena,
                        outerRow: null,
                        sidecarRegistry: catalog.SidecarRegistry,
                        types: null);

                    if (await evaluator.EvaluateAsBooleanAsync(
                            predicate!, frame, CancellationToken.None).ConfigureAwait(false))
                    {
                        matched.Add(rowIndex);
                    }
                }
            }
            finally
            {
                batch.Dispose();
            }
        }

        if (matched.Count > 0)
        {
            provider.DeleteRows(matched);
        }
    }
}
