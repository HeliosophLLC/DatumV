using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Catalog;

/// <summary>
/// Owns the <c>UPDATE</c>-statement pipeline for
/// <see cref="TableCatalog.Plan(Statement)"/>.
/// </summary>
/// <remarks>
/// PR11a shipped parse + plan-time validation. PR11c wired the plain
/// path: a single scan of the target with the WHERE predicate
/// accumulates per-row SET expression results, then dispatches the
/// page-COW rewrite via <see cref="ITableProvider.UpdateRows"/>.
/// PR11d adds <c>UPDATE … FROM &lt;single-source&gt;</c>: a Cartesian
/// nested-loop join (target × source) drives the same accumulator,
/// with last-match-wins for multiple source rows matching the same
/// target. JOINs inside the <c>FROM</c> clause are rejected pending a
/// follow-up.
/// </remarks>
internal static class UpdateExecutor
{
    public static void Execute(TableCatalog catalog, UpdateStatement update) =>
        ExecuteAsync(catalog, update).GetAwaiter().GetResult();

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
                            raw, sourceArena, workArena, target, update.TableName);

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
            provider.UpdateRows(requests, workArena);
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
                                raw, targetArena, workArena, target, update.TableName);

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

        provider.UpdateRows(requests, workArena);
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
        string tableName)
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

        object scalar = source.Kind switch
        {
            DataKind.String => source.AsString(sourceStore),
            DataKind.Boolean => source.AsBoolean(),
            DataKind.Uuid => source.AsUuid(),
            DataKind.Int8 => source.AsInt8(),
            DataKind.Int16 => source.AsInt16(),
            DataKind.Int32 => source.AsInt32(),
            DataKind.Int64 => source.AsInt64(),
            DataKind.UInt8 => source.AsUInt8(),
            DataKind.UInt16 => source.AsUInt16(),
            DataKind.UInt32 => source.AsUInt32(),
            DataKind.UInt64 => source.AsUInt64(),
            DataKind.Float32 => source.AsFloat32(),
            DataKind.Float64 => source.AsFloat64(),
            DataKind.Date => source.AsDate(),
            DataKind.Time => source.AsTime(),
            DataKind.DateTime => source.AsDateTime(),
            DataKind.Duration => source.AsDuration(),
            _ => throw new QueryPlanException(
                $"UPDATE '{tableName}': SET expression for column '{target.Name}' " +
                $"produced kind {source.Kind} which is not yet supported (composite kinds — " +
                "Struct, typed arrays, Image / Audio / ByteArray — land in a follow-up)."),
        };

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
        if (!catalog.TryGetTable(update.TableName, out ITableProvider? provider))
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
}
