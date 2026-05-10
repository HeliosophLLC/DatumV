using DatumIngest.Catalog.Plans;
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
    public static Task<StatementPlan> ExecuteAsync(
        TableCatalog catalog, DeleteStatement delete, BatchContext? batchContext = null)
        => ExecuteAsync(catalog, delete, captureSink: null, batchContext);

    /// <summary>
    /// Primary overload. When <paramref name="captureSink"/> is non-null,
    /// pre-delete captured rows are routed into it (for a wrapping
    /// <see cref="Plans.DmlReturningPlan"/> composer to project over) and
    /// the executor returns <see cref="DdlPlan.NoOp"/>. When the sink is
    /// null, behavior matches the legacy contract: a
    /// <see cref="Plans.DmlReturningPlan"/> is constructed post-hoc from
    /// the internal captured list (when RETURNING is set) or
    /// <see cref="DdlPlan.NoOp"/> is returned otherwise.
    /// </summary>
    public static async Task<StatementPlan> ExecuteAsync(
        TableCatalog catalog,
        DeleteStatement delete,
        Plans.CapturedRowsSource? captureSink,
        BatchContext? batchContext = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(delete);

        // Block DELETE against a view before the table resolver fires its
        // misleading "table not found" diagnostic.
        if (catalog.Views.TryResolve(delete.SchemaName, delete.TableName, catalog.SearchPath, out Registries.ViewDescriptor? targetView))
        {
            throw new InvalidOperationException(
                $"DELETE FROM '{targetView.QualifiedName}': '{targetView.QualifiedName}' is a view; " +
                "DELETE through a view is not supported. DELETE from the underlying table directly.");
        }

        SchemaResolver resolver = new(catalog, catalog.SearchPath);
        QualifiedName qn = resolver.Resolve(delete.SchemaName, delete.TableName);
        if (!catalog.TryGetTable(qn.ToString(), out ITableProvider? provider))
        {
            throw new InvalidOperationException(
                $"DELETE FROM '{delete.TableName}': table is not registered in the catalog.");
        }
        else if (!provider.CanDeleteRows)
        {
            throw new InvalidOperationException(
                $"DELETE FROM '{delete.TableName}': provider type {provider.GetType().Name} " +
                "is read-only (CanDeleteRows = false).");
        }

        IReadOnlyList<RowBatch>? captured = await ApplyAsync(catalog, provider, delete, batchContext).ConfigureAwait(false);

        if (captured is null || delete.Returning is null)
        {
            return DdlPlan.NoOp(catalog, "Delete", "no matching rows");
        }

        // Composer path: route captured rows into the sink for a wrapping
        // DmlReturningPlan to project.
        if (captureSink is not null)
        {
            foreach (RowBatch batch in captured) captureSink.Capture(batch);
            return DdlPlan.NoOp(catalog, "Delete", "captured rows routed to composer sink");
        }

        return new DmlReturningPlan(
            DmlReturningKind.Delete,
            delete.TableName,
            provider.GetSchema(),
            captured,
            delete.Returning,
            catalog);
    }

    private static async Task<IReadOnlyList<RowBatch>?> ApplyAsync(
        TableCatalog catalog, ITableProvider provider, DeleteStatement delete, BatchContext? batchContext)
    {
        // See UpdateExecutor for the non-null fallback rationale.
        MemoryAccountant accountant = batchContext is not null ? batchContext.Accountant : new MemoryAccountant();
        Expression? predicate = delete.Where;
        bool captureRows = delete.Returning is not null;
        Schema schema = provider.GetSchema();
        ColumnLookup? schemaLookup = captureRows ? BuildSchemaLookup(schema) : null;

        // Transient ExecutionContext for the DML's frame ambient state.
        // Borrows the (BatchContext-supplied or fresh) accountant so the
        // dispose at end of this method only releases the unused Store
        // baseline + VideoRegistry it allocated.
        using DatumIngest.Execution.ExecutionContext context = catalog.CreateExecutionContext(accountant: accountant,
            types: batchContext?.Types);

        // Walk the live row sequence with a running counter. The
        // provider's tombstone index space matches a fresh scan's
        // emission order (per ITableProvider.DeleteRows docs), so the
        // running counter IS the linear row index regardless of any
        // earlier tombstones. We don't shortcut on a null predicate
        // via GetRowCount because some providers (DatumFile in
        // particular) return the gross row count and would over-number
        // the index list when prior deletes are in play; the scan-based
        // walk stays correct without paying for predicate evaluation.
        ExpressionEvaluator? evaluator = predicate is null ? null : context.CreateEvaluator();

        List<long> matched = new();
        long rowIndex = 0;

        // RETURNING captures the pre-delete image of every tombstoned row.
        // One captured RowBatch per scan batch — each owns its own arena.
        List<RowBatch>? capturedBatches = captureRows ? new() : null;

        try
        {
            await foreach (RowBatch batch in provider.ScanAsync(
                requiredColumns: null,
                filterHint: null,
                targetArena: null,
                cancellationToken: CancellationToken.None).ConfigureAwait(false))
            {
                Arena? captureArena = null;
                RowBatch? capturedBatch = null;
                try
                {
                    Arena sourceArena = batch.Arena;
                    if (captureRows)
                    {
                        captureArena = new Arena();
                        capturedBatch = catalog.Pool.RentRowBatch(
                            schemaLookup!, capacity: batch.Count, arena: captureArena);
                    }

                    // Target arena = source arena: predicate eval doesn't
                    // produce DataValues that need to outlive this batch
                    // (the result is a bool we consume immediately).
                    for (int r = 0; r < batch.Count; r++, rowIndex++)
                    {
                        Row row = batch[r];
                        bool matches;
                        if (evaluator is null)
                        {
                            matches = true;
                        }
                        else
                        {
                            EvaluationFrame frame = new(row, sourceArena, context);
                            matches = await evaluator.EvaluateAsBooleanAsync(
                                predicate!, frame, CancellationToken.None).ConfigureAwait(false);
                        }

                        if (!matches) continue;
                        matched.Add(rowIndex);
                        // 8 bytes per index in the matched list. Accountant
                        // notification keeps DELETE residency visible alongside
                        // UPDATE/INSERT for batch-wide budgeting and profiling.
                        accountant?.NotifyMaterialized(sizeof(long));

                        // RETURNING capture: pre-delete image. Stabilize each
                        // cell from the scan batch's arena into the capture
                        // arena so the captured row survives batch disposal.
                        if (capturedBatch is not null)
                        {
                            DataValue[] preImage = catalog.Pool.RentDataValues(schema.Columns.Count);
                            for (int c = 0; c < schema.Columns.Count; c++)
                            {
                                preImage[c] = DataValueRetention.Stabilize(
                                    row[c], sourceArena, captureArena!);
                            }
                            capturedBatch.Add(preImage);
                        }
                    }

                    if (capturedBatch is not null && capturedBatch.Count > 0)
                    {
                        capturedBatches!.Add(capturedBatch);
                        capturedBatch = null; // ownership transferred
                    }
                }
                finally
                {
                    if (capturedBatch is not null)
                    {
                        catalog.Pool.ReturnRowBatch(capturedBatch);
                    }
                    batch.Dispose();
                }
            }
        }
        catch
        {
            if (capturedBatches is not null)
            {
                foreach (RowBatch b in capturedBatches) catalog.Pool.ReturnRowBatch(b);
            }
            throw;
        }

        if (matched.Count > 0)
        {
            provider.DeleteRows(matched);
        }

        // Release the matched-index buffer's bytes from the accountant; the
        // list goes out of scope when this method returns and is GC-eligible.
        accountant?.NotifyReleased(matched.Count * sizeof(long));

        return capturedBatches;
    }

    private static ColumnLookup BuildSchemaLookup(Schema schema)
    {
        string[] names = new string[schema.Columns.Count];
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            names[i] = schema.Columns[i].Name;
        }
        return new ColumnLookup(names);
    }
}
