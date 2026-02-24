using DatumIngest.DatumFile.Sidecar;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Catalog.Executors;

/// <summary>
/// Owns the <c>UPDATE</c>-statement pipeline for
/// <see cref="TableCatalog.PlanAsync(Statement)"/>.
/// </summary>
/// <remarks>
/// PR11a shipped parse + plan-time validation. PR11c wired the plain
/// path: a single scan of the target with the WHERE predicate
/// accumulates per-row SET expression results, then dispatches the
/// page-COW rewrite via <see cref="ITableProvider.UpdateRowsAsync"/>.
/// PR11d adds <c>UPDATE … FROM &lt;single-source&gt;</c>: a Cartesian
/// nested-loop join (target × source) drives the same accumulator,
/// with last-match-wins for multiple source rows matching the same
/// target. JOINs inside the <c>FROM</c> clause are rejected pending a
/// follow-up.
/// </remarks>
internal static class UpdateExecutor
{
    public static async Task ExecuteAsync(TableCatalog catalog, UpdateStatement update)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(update);

        ITableProvider provider = Validate(catalog, update);

        if (update.Joins is { Count: > 0 })
        {
            throw new QueryPlanException(
                $"UPDATE '{update.TableName}': UPDATE … FROM with JOIN inside the " +
                "FROM clause is pending a follow-up. The current build accepts a " +
                "single source table; encode multi-table joins by collapsing them " +
                "into the WHERE clause when possible.");
        }

        if (update.From is not null)
        {
            await ExecuteWithFromAsync(catalog, provider, update).ConfigureAwait(false);
        }
        else
        {
            await ExecuteSimpleAsync(catalog, provider, update).ConfigureAwait(false);
        }
    }

    private static async Task ExecuteSimpleAsync(
        TableCatalog catalog,
        ITableProvider provider,
        UpdateStatement update)
    {
        Schema schema = provider.GetSchema();

        // Resolve SET column names → schema column indices once. The
        // validator already verified that every name resolves and no
        // PK column is touched.
        (int columnIndex, Expression valueExpression)[] setBindings =
            new (int, Expression)[update.Assignments.Count];
        for (int i = 0; i < update.Assignments.Count; i++)
        {
            ColumnAssignment a = update.Assignments[i];
            int idx = FindColumnIndex(schema, a.ColumnName);
            setBindings[i] = (idx, a.Value);
        }

        // Computed-column dependents: for each source-column index, the list
        // of computed columns whose expression references it. When a SET
        // touches a referenced column, we recompute every dependent. The
        // catalog's CREATE-TABLE validation forbids computed-to-computed
        // dependencies, so a single (non-topological) pass suffices.
        Dictionary<int, int[]>? dependentsByColumn = BuildDependentsByColumn(schema);
        ColumnLookup? schemaLookup = dependentsByColumn is null ? null : BuildSchemaLookup(schema);

        // workArena outlives every per-batch arena because UpdateRows
        // resolves non-inline SET results against it after the scan
        // loop completes. Disposed at end of method.
        using Arena workArena = new();
        ExpressionEvaluator evaluator = new(
            functions: catalog.Functions,
            sidecarRegistry: catalog.SidecarRegistry);

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
                Arena sourceArena = batch.Arena;
                for (int r = 0; r < batch.Count; r++, liveRowIndex++)
                {
                    Row row = batch[r];

                    // WHERE — predicate-only frame; results are inline
                    // bools so target = source is fine.
                    if (update.Where is not null)
                    {
                        EvaluationFrame predFrame = new(
                            row,
                            sourceArena,
                            sourceArena,
                            outerRow: null,
                            sidecarRegistry: catalog.SidecarRegistry,
                            types: null);
                        if (!await evaluator.EvaluateAsBooleanAsync(
                                update.Where, predFrame, CancellationToken.None).ConfigureAwait(false))
                        {
                            continue;
                        }
                    }

                    // SET — each expression's result lands in workArena
                    // so non-inline payloads survive past this batch.
                    EvaluationFrame setFrame = new(
                        row,
                        sourceArena,
                        workArena,
                        outerRow: null,
                        sidecarRegistry: catalog.SidecarRegistry,
                        types: null);

                    Dictionary<int, DataValue> rowValues = new(setBindings.Length);
                    foreach ((int columnIndex, Expression valueExpression) in setBindings)
                    {
                        DataValue raw = await evaluator.EvaluateAsync(
                            valueExpression, setFrame, CancellationToken.None).ConfigureAwait(false);

                        ColumnInfo target = schema.Columns[columnIndex];
                        DataValue coerced = CoerceForUpdate(
                            raw, sourceArena, workArena, target, update.TableName, catalog.SidecarRegistry);

                        // No-op detection: when the new value matches the
                        // existing cell, drop it from the per-row map.
                        // Catches `SET col = col`, idempotent updates
                        // (`SET status = 'pending' WHERE status = 'pending'`),
                        // and partial-row matches in multi-column SETs.
                        // DataValue.Equals returns false for cross-store
                        // non-inline values, so this is conservative —
                        // sidecar-pass-through above keeps wide-string
                        // value-copies on the same sidecar pointer so
                        // their offset+length fast path matches.
                        if (coerced.Equals(row[columnIndex])) continue;
                        rowValues[columnIndex] = coerced;
                    }

                    // Recompute dependent GENERATED columns. Runs after the
                    // user-supplied SETs land in rowValues so the partial-row
                    // frame sees the new values; no-op detection (Equals
                    // against the existing slot) drops dependents whose
                    // recomputed value didn't actually change.
                    await RecomputeDependentsAsync(
                        evaluator,
                        schema,
                        schemaLookup,
                        row,
                        sourceArena,
                        rowValues,
                        dependentsByColumn,
                        workArena,
                        catalog.SidecarRegistry,
                        CancellationToken.None).ConfigureAwait(false);

                    if (rowValues.Count > 0)
                    {
                        requests.Add(new RowUpdateRequest(liveRowIndex, rowValues));
                    }
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
    /// PR11d's <c>UPDATE … FROM</c> path. Materialises every source row
    /// into a long-lived arena, then nested-loops target × source,
    /// evaluating the WHERE predicate against each pair. Matches feed
    /// the SET evaluator with a synthetic joined row that resolves
    /// <c>target.col</c> / <c>alias.col</c> / bare <c>col</c> against
    /// the target side and <c>source.col</c> / <c>sourcealias.col</c>
    /// against the source side. Multiple source rows matching the same
    /// target are last-wins (PostgreSQL semantics) — the accumulator
    /// dictionary overwrites prior values for the same target row.
    /// </summary>
    private static async Task ExecuteWithFromAsync(
        TableCatalog catalog,
        ITableProvider provider,
        UpdateStatement update)
    {
        // Source table resolution. PR11d MVP only handles a single
        // TableReference in FROM; subqueries / nested joins are rejected.
        if (update.From!.Source is not TableReference sourceRef)
        {
            throw new QueryPlanException(
                $"UPDATE '{update.TableName}': the FROM clause must name a single " +
                $"table; got {update.From.Source.GetType().Name}.");
        }
        if (string.Equals(sourceRef.Name, update.TableName, StringComparison.OrdinalIgnoreCase))
        {
            throw new QueryPlanException(
                $"UPDATE '{update.TableName}': the target table must not appear in the " +
                "FROM clause. PostgreSQL semantics: target is implicitly the leftmost source.");
        }
        if (!catalog.TryGetTable(sourceRef.Name, out ITableProvider? source))
        {
            throw new QueryPlanException(
                $"UPDATE '{update.TableName}' FROM '{sourceRef.Name}': source table " +
                "is not registered in the catalog.");
        }

        Schema targetSchema = provider.GetSchema();
        Schema sourceSchema = source.GetSchema();

        // Same dependents-map pattern as the simple path. The recompute
        // runs after the cross-product scan completes (see below) because
        // last-match-wins means rowValues only stabilises after every
        // matching source row has had its SET evaluated.
        Dictionary<int, int[]>? dependentsByColumn = BuildDependentsByColumn(targetSchema);
        ColumnLookup? targetSchemaLookup = dependentsByColumn is null ? null : BuildSchemaLookup(targetSchema);

        // Resolve SET column names → target-schema column indices.
        (int columnIndex, Expression valueExpression)[] setBindings =
            new (int, Expression)[update.Assignments.Count];
        for (int i = 0; i < update.Assignments.Count; i++)
        {
            ColumnAssignment a = update.Assignments[i];
            int idx = FindColumnIndex(targetSchema, a.ColumnName);
            setBindings[i] = (idx, a.Value);
        }

        // Build the joined ColumnLookup. Target columns get all
        // qualifications (bare + table-name + alias); source columns
        // get qualifications only (bare names resolve to target on
        // collision, matching PostgreSQL's leftmost-wins).
        string targetTableName = update.TableName;
        string? targetAlias = update.Alias;
        string sourceTableName = sourceRef.Name;
        string? sourceAlias = sourceRef.Alias;

        int targetColumnCount = targetSchema.Columns.Count;
        int sourceColumnCount = sourceSchema.Columns.Count;
        int totalColumns = targetColumnCount + sourceColumnCount;
        string[] orderedNames = new string[totalColumns];
        Dictionary<string, int> nameIndex = new(totalColumns * 3, StringComparer.OrdinalIgnoreCase);

        for (int c = 0; c < targetColumnCount; c++)
        {
            string col = targetSchema.Columns[c].Name;
            orderedNames[c] = $"{targetAlias ?? targetTableName}.{col}";
            nameIndex[col] = c;
            nameIndex[$"{targetTableName}.{col}"] = c;
            if (targetAlias is not null && !string.Equals(targetAlias, targetTableName, StringComparison.OrdinalIgnoreCase))
            {
                nameIndex[$"{targetAlias}.{col}"] = c;
            }
        }
        for (int c = 0; c < sourceColumnCount; c++)
        {
            string col = sourceSchema.Columns[c].Name;
            int slot = targetColumnCount + c;
            orderedNames[slot] = $"{sourceAlias ?? sourceTableName}.{col}";
            nameIndex[$"{sourceTableName}.{col}"] = slot;
            if (sourceAlias is not null && !string.Equals(sourceAlias, sourceTableName, StringComparison.OrdinalIgnoreCase))
            {
                nameIndex[$"{sourceAlias}.{col}"] = slot;
            }
        }
        ColumnLookup joinedLookup = new(orderedNames, nameIndex);

        // workArena: long-lived store for stabilised source rows AND
        // SET expression results that need to outlive batch arenas.
        using Arena workArena = new();

        // Materialise source rows into workArena.
        List<DataValue[]> sourceRows = new();
        await foreach (RowBatch batch in source.ScanAsync(
            requiredColumns: null,
            filterHint: null,
            targetArena: null,
            cancellationToken: CancellationToken.None).ConfigureAwait(false))
        {
            try
            {
                Arena srcArena = batch.Arena;
                for (int r = 0; r < batch.Count; r++)
                {
                    Row sourceRow = batch[r];
                    DataValue[] copy = new DataValue[sourceColumnCount];
                    for (int c = 0; c < sourceColumnCount; c++)
                    {
                        copy[c] = DataValueRetention.Stabilize(sourceRow[c], srcArena, workArena);
                    }
                    sourceRows.Add(copy);
                }
            }
            finally
            {
                batch.Dispose();
            }
        }

        if (sourceRows.Count == 0)
        {
            // No source rows → no joined matches → no UPDATE.
            return;
        }

        ExpressionEvaluator evaluator = new(
            functions: catalog.Functions,
            sidecarRegistry: catalog.SidecarRegistry);

        // Last-match-wins accumulator: liveRowIndex → (column → new value).
        Dictionary<long, Dictionary<int, DataValue>> matched = new();
        long liveRowIndex = 0;

        await foreach (RowBatch batch in provider.ScanAsync(
            requiredColumns: null,
            filterHint: null,
            targetArena: null,
            cancellationToken: CancellationToken.None).ConfigureAwait(false))
        {
            try
            {
                Arena targetArena = batch.Arena;
                for (int r = 0; r < batch.Count; r++, liveRowIndex++)
                {
                    Row targetRow = batch[r];

                    foreach (DataValue[] sourceCols in sourceRows)
                    {
                        // Build joined row. Reuse the same array shape
                        // every iteration via a fresh allocation so
                        // ColumnLookup-keyed reads see consistent slot
                        // contents during predicate / SET evaluation.
                        DataValue[] joined = new DataValue[totalColumns];
                        for (int c = 0; c < targetColumnCount; c++) joined[c] = targetRow[c];
                        for (int c = 0; c < sourceColumnCount; c++) joined[targetColumnCount + c] = sourceCols[c];
                        Row joinedRow = new(joinedLookup, joined);

                        // WHERE — resolves columns from both sides.
                        // Both arenas back the joined row: target side
                        // is targetArena; source side is workArena.
                        // Predicate frame target = source = targetArena
                        // (predicates produce inline bools; non-bool
                        // intermediates land here briefly).
                        if (update.Where is not null)
                        {
                            EvaluationFrame predFrame = new(
                                joinedRow,
                                targetArena,
                                targetArena,
                                outerRow: null,
                                sidecarRegistry: catalog.SidecarRegistry,
                                types: null);
                            if (!await evaluator.EvaluateAsBooleanAsync(
                                    update.Where, predFrame, CancellationToken.None).ConfigureAwait(false))
                            {
                                continue;
                            }
                        }

                        // SET — results land in workArena so they
                        // survive past targetArena disposal at end of
                        // this batch.
                        EvaluationFrame setFrame = new(
                            joinedRow,
                            targetArena,
                            workArena,
                            outerRow: null,
                            sidecarRegistry: catalog.SidecarRegistry,
                            types: null);

                        if (!matched.TryGetValue(liveRowIndex, out Dictionary<int, DataValue>? rowValues))
                        {
                            rowValues = new Dictionary<int, DataValue>(setBindings.Length);
                            matched[liveRowIndex] = rowValues;
                        }

                        foreach ((int columnIndex, Expression valueExpression) in setBindings)
                        {
                            DataValue raw = await evaluator.EvaluateAsync(
                                valueExpression, setFrame, CancellationToken.None).ConfigureAwait(false);

                            ColumnInfo target = targetSchema.Columns[columnIndex];
                            DataValue coerced = CoerceForUpdate(
                                raw, targetArena, workArena, target, update.TableName, catalog.SidecarRegistry);

                            // Last-match-wins with no-op detection: if the
                            // final value matches the existing cell, drop
                            // any prior accumulated entry for this column
                            // (an earlier source-row may have set it to a
                            // different value, but a later match toggles
                            // back to identity → still a no-op).
                            if (coerced.Equals(targetRow[columnIndex]))
                            {
                                rowValues.Remove(columnIndex);
                            }
                            else
                            {
                                rowValues[columnIndex] = coerced;
                            }
                        }
                    }

                    // After every source row has been considered for this
                    // target row, the accumulator carries the last-match-
                    // wins SET state. Recompute dependent GENERATED columns
                    // now while targetRow is still in scope (the scan batch
                    // disposes at end of this foreach block).
                    if (matched.TryGetValue(liveRowIndex, out Dictionary<int, DataValue>? finalValues) &&
                        finalValues.Count > 0)
                    {
                        await RecomputeDependentsAsync(
                            evaluator,
                            targetSchema,
                            targetSchemaLookup,
                            targetRow,
                            targetArena,
                            finalValues,
                            dependentsByColumn,
                            workArena,
                            catalog.SidecarRegistry,
                            CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                batch.Dispose();
            }
        }

        if (matched.Count == 0)
        {
            return;
        }

        List<RowUpdateRequest> requests = new(matched.Count);
        foreach ((long liveIdx, Dictionary<int, DataValue> values) in matched)
        {
            // Drop matched rows whose final SET evaluation equalled the
            // existing cell on every column — see no-op detection in the
            // scan loop above.
            if (values.Count > 0)
            {
                requests.Add(new RowUpdateRequest(liveIdx, values));
            }
        }

        if (requests.Count == 0) return;

        await provider.UpdateRowsAsync(requests, workArena).ConfigureAwait(false);
    }

    /// <summary>
    /// Coerces a SET-expression result to the target column's
    /// <see cref="DataKind"/>, mirroring the coercion path INSERT uses
    /// for source values from <c>SELECT</c>: extract a CLR scalar from
    /// the source <see cref="DataValue"/>, then route through
    /// <see cref="LiteralCoercion.Coerce"/> which handles widening,
    /// fit-checked narrowing, and NOT NULL enforcement uniformly.
    /// </summary>
    private static DataValue CoerceForUpdate(
        DataValue source,
        IValueStore sourceStore,
        Arena targetArena,
        ColumnInfo target,
        string tableName,
        SidecarRegistry? sidecarRegistry)
    {
        if (source.IsNull)
        {
            if (!target.Nullable)
            {
                throw new QueryPlanException(
                    $"UPDATE '{tableName}': column '{target.Name}' is NOT NULL; SET expression " +
                    "produced a null value.");
            }
            return DataValue.Null(target.Kind);
        }

        // Fast path: source already has the right kind and is either
        // self-contained (inline) or sidecar-backed. Pass through without
        // a CLR round-trip + LiteralCoercion re-encode. Critical for
        // value-copy SET on wide-string / byte-array columns: the encoder
        // recognises the IsInSidecar pointer and emits a slot referencing
        // the original sidecar bytes — no duplicate is appended to
        // .datum-blob. Arena-backed source values are deliberately
        // excluded because the scan's per-batch arena disposes after the
        // batch loop; passing one through would dangle into UpdateRows.
        if (source.Kind == target.Kind && (source.IsInSidecar || source.IsInline))
        {
            return source;
        }

        // Composite / blob results from SET expressions aren't supported —
        // LiteralCoercion deals in scalars. Reject them with a descriptive
        // message before falling into ToObject's ToString-summary path.
        if (source.IsArray || source.IsBlobKind || source.Kind == DataKind.Struct)
        {
            throw new QueryPlanException(
                $"UPDATE '{tableName}': SET expression for column '{target.Name}' " +
                $"produced kind {source.Kind} which is not yet supported (composite kinds — " +
                "Struct, typed arrays, Image / Audio / ByteArray — land in a follow-up).");
        }

        object scalar = source.ToObject(sourceStore, sidecarRegistry)!;
        return LiteralCoercion.Coerce(scalar, target, targetArena, target.Name);
    }

    /// <summary>
    /// Resolves the target provider, asserts writability, and validates
    /// the SET assignment list against the target schema.
    /// </summary>
    /// <remarks>
    /// Validation rules (PR11a):
    /// <list type="bullet">
    ///   <item>Target table must be registered in the catalog.</item>
    ///   <item>Target provider must opt in to UPDATE
    ///     (<see cref="ITableProvider.CanUpdateRows"/>), which excludes
    ///     read-only sources (CSV, Parquet, JSON, system tables).</item>
    ///   <item>Every SET column must exist on the target schema
    ///     (case-insensitive), and no column may be assigned twice in the
    ///     same statement.</item>
    ///   <item>No SET column may belong to the table's PRIMARY KEY —
    ///     PK column updates are explicitly out of scope; users must
    ///     <c>DELETE</c> and re-<c>INSERT</c> to change a row's PK.</item>
    /// </list>
    /// </remarks>
    private static ITableProvider Validate(TableCatalog catalog, UpdateStatement update)
    {
        SchemaResolver resolver = new(catalog, catalog.SearchPath);
        QualifiedName qn = resolver.Resolve(update.SchemaName, update.TableName);
        if (!catalog.TryGetTable(qn.ToString(), out ITableProvider? provider))
        {
            throw new QueryPlanException(
                $"UPDATE '{update.TableName}': table is not registered in the catalog.");
        }
        if (!provider.CanUpdateRows)
        {
            throw new QueryPlanException(
                $"UPDATE '{update.TableName}': provider type {provider.GetType().Name} " +
                "does not support row updates (CanUpdateRows = false).");
        }

        if (update.Assignments.Count == 0)
        {
            throw new QueryPlanException(
                $"UPDATE '{update.TableName}': SET list is empty.");
        }

        Schema schema = provider.GetSchema();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        HashSet<int> pkColumns = schema.PrimaryKeyColumnIndices.Count == 0
            ? new HashSet<int>()
            : new HashSet<int>(schema.PrimaryKeyColumnIndices);

        for (int i = 0; i < update.Assignments.Count; i++)
        {
            string name = update.Assignments[i].ColumnName;

            int columnIndex = FindColumnIndex(schema, name);
            if (columnIndex < 0)
            {
                throw new QueryPlanException(
                    $"UPDATE '{update.TableName}': column '{name}' does not exist on the target table.");
            }

            if (!seen.Add(name))
            {
                throw new QueryPlanException(
                    $"UPDATE '{update.TableName}': column '{name}' is assigned more than once in the SET list.");
            }

            if (pkColumns.Contains(columnIndex))
            {
                throw new QueryPlanException(
                    $"UPDATE '{update.TableName}': column '{name}' is part of the PRIMARY KEY. " +
                    "PK column values are immutable — DELETE and re-INSERT to change a row's primary key.");
            }

            // GENERATED ALWAYS AS columns: same rule as PG and SQL Server.
            // The value is derived; explicit assignment is rejected. To
            // change the column's value, change one of the referenced
            // columns and the next UPDATE that touches the row will pick
            // up the new value via the computed-column INSERT/UPDATE path.
            if (schema.Columns[columnIndex].ComputedExpression is not null)
            {
                throw new QueryPlanException(
                    $"UPDATE '{update.TableName}': column '{name}' is GENERATED ALWAYS AS " +
                    "(computed). Computed columns derive their value from other columns and " +
                    "cannot be assigned directly.");
            }
        }

        return provider;
    }

    private static int FindColumnIndex(Schema schema, string name)
    {
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            if (string.Equals(schema.Columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Builds the per-source-column map of dependent <c>GENERATED</c>
    /// columns: for each non-computed column index, the list of computed
    /// column indices whose expression references it. Returns
    /// <see langword="null"/> when the schema has no computed columns
    /// (caller skips the recompute path entirely).
    /// </summary>
    private static Dictionary<int, int[]>? BuildDependentsByColumn(Schema schema)
    {
        Dictionary<int, List<int>>? acc = null;
        for (int comp = 0; comp < schema.Columns.Count; comp++)
        {
            Expression? expr = schema.Columns[comp].ComputedExpression;
            if (expr is null) continue;

            HashSet<(string? TableName, string ColumnName)> refs =
                ColumnReferenceCollector.Collect(expr);
            foreach ((string? _, string refName) in refs)
            {
                int srcIdx = FindColumnIndex(schema, refName);
                if (srcIdx < 0) continue;
                acc ??= new Dictionary<int, List<int>>();
                if (!acc.TryGetValue(srcIdx, out List<int>? list))
                {
                    list = new List<int>();
                    acc[srcIdx] = list;
                }
                if (!list.Contains(comp)) list.Add(comp);
            }
        }

        if (acc is null) return null;
        Dictionary<int, int[]> result = new(acc.Count);
        foreach ((int k, List<int> v) in acc) result[k] = v.ToArray();
        return result;
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

    /// <summary>
    /// Recomputes every <c>GENERATED</c> column whose expression references
    /// at least one column touched by the SET assignments in
    /// <paramref name="rowValues"/>. Builds a partial row by overlaying
    /// the SET values on the existing row's slots (stabilised into
    /// <paramref name="workArena"/> so the evaluator can resolve mixed-store
    /// slots through a single arena), evaluates each dependent's
    /// expression, and writes results back into <paramref name="rowValues"/>.
    /// </summary>
    /// <remarks>
    /// No-op detection (Equals against the existing slot) mirrors the
    /// user-supplied SET path: if a recomputed value matches the existing
    /// cell, it isn't added — keeps the rewrite request minimal and
    /// preserves the "UPDATE that touches no actual state is dropped"
    /// behaviour. Single-pass evaluation is correct because the catalog
    /// rejects computed-to-computed dependencies at CREATE TABLE time.
    /// </remarks>
    private static async Task RecomputeDependentsAsync(
        ExpressionEvaluator evaluator,
        Schema schema,
        ColumnLookup? schemaLookup,
        Row existingRow,
        IValueStore existingStore,
        Dictionary<int, DataValue> rowValues,
        Dictionary<int, int[]>? dependentsByColumn,
        Arena workArena,
        DatumFile.Sidecar.SidecarRegistry? sidecarRegistry,
        CancellationToken cancellationToken)
    {
        if (dependentsByColumn is null || schemaLookup is null || rowValues.Count == 0) return;

        HashSet<int>? dependentsToEval = null;
        foreach (int touched in rowValues.Keys)
        {
            if (!dependentsByColumn.TryGetValue(touched, out int[]? deps)) continue;
            foreach (int dep in deps)
            {
                // rowValues should never already contain a dependent index
                // (UPDATE rejects SET on computed columns); guard anyway so
                // a user-set already-overridden value wins.
                if (rowValues.ContainsKey(dep)) continue;
                dependentsToEval ??= new HashSet<int>();
                dependentsToEval.Add(dep);
            }
        }
        if (dependentsToEval is null) return;

        // Overlay SET values on the existing row; stabilise non-inline
        // existing slots into workArena so the frame's single Source
        // store can resolve every slot the evaluator might read.
        DataValue[] partial = new DataValue[schema.Columns.Count];
        for (int c = 0; c < schema.Columns.Count; c++)
        {
            partial[c] = rowValues.TryGetValue(c, out DataValue overlay)
                ? overlay
                : DataValueRetention.Stabilize(existingRow[c], existingStore, workArena);
        }
        Row partialRow = new(schemaLookup, partial);
        EvaluationFrame frame = new(
            partialRow,
            workArena,
            workArena,
            outerRow: null,
            sidecarRegistry: sidecarRegistry,
            types: null);

        foreach (int depIdx in dependentsToEval)
        {
            ColumnInfo target = schema.Columns[depIdx];
            ValueRef result = await evaluator.EvaluateAsValueRefAsync(
                target.ComputedExpression!, frame, cancellationToken).ConfigureAwait(false);
            DataValue converted = ComputedColumnEvaluator.ConvertValueRefToTarget(
                result, target, workArena, target.Name);

            // No-op detection: if the recomputed value equals the existing
            // slot, don't emit a write (cross-store Equals is conservative
            // and may return false even when the bytes match — that's fine,
            // we just emit a redundant write).
            if (converted.Equals(existingRow[depIdx])) continue;
            rowValues[depIdx] = converted;
        }
    }
}
