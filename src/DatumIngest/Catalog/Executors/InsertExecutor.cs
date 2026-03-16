using DatumIngest.Catalog.Plans;
using DatumIngest.DatumFile.Sidecar;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog.Executors;

/// <summary>
/// Owns the <c>INSERT</c>-statement pipeline for <see cref="TableCatalog.PlanAsync(Statement)"/>:
/// resolves the target schema, builds a column mapping (positional or
/// named), pre-evaluates column <c>DEFAULT</c>s for omitted columns,
/// coerces literal values to each target column's kind, and streams the
/// resulting <see cref="RowBatch"/> through
/// <see cref="ITableProvider.AppendRowsAsync"/>.
/// </summary>
/// <remarks>
/// PR10c shipped <c>INSERT … VALUES (…)</c> with literal expressions
/// only (matching <c>DEFAULT</c>'s literal-only restriction from PR10b).
/// PR10c' adds <c>INSERT … SELECT</c>: the source query is planned via
/// <see cref="TableCatalog.PlanQuery"/>, batches stream through the
/// shared column plan + per-value coercion, and rows commit through an
/// <see cref="IAppendSession"/>. PR10c'' lifts the literal-only restriction
/// on VALUES — each VALUES expression is evaluated through
/// <see cref="ExpressionEvaluator"/> against an empty row, so binary
/// expressions, function calls, and array literals (<c>['a','b','c']</c>
/// → <c>array(...)</c>) work uniformly. Column references in VALUES still
/// fail because there's no source row to bind against.
/// </remarks>
internal static class InsertExecutor
{
    public static async Task<IQueryPlan> ExecuteAsync(
        TableCatalog catalog, InsertStatement insert, BatchContext? batchContext = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(insert);

        // Resolve via the session search_path. Explicit schema (post-S8
        // qualified parsing) bypasses the walk; unqualified targets fall
        // through to the first match on search_path. Resolve throws
        // SchemaResolutionException for missing-schema / missing-table —
        // same rich diagnostic SELECT and DDL surface.
        SchemaResolver resolver = new(catalog, catalog.SearchPath);
        QualifiedName qn = resolver.Resolve(insert.SchemaName, insert.TableName);
        if (!catalog.TryGetTable(qn.ToString(), out ITableProvider? provider))
        {
            throw new InvalidOperationException(
                $"INSERT INTO '{insert.TableName}': table is not registered in the catalog.");
        }
        if (!provider.CanAppendRows)
        {
            throw new InvalidOperationException(
                $"INSERT INTO '{insert.TableName}': provider type {provider.GetType().Name} " +
                "is read-only (CanAppendRows = false).");
        }

        Schema targetSchema = provider.GetSchema();
        bool captureRows = insert.Returning is not null;
        IReadOnlyList<RowBatch>? captured;

        switch (insert.Source)
        {
            case InsertValuesSource values:
                RowBatch? singleBatch = await ApplyValuesAsync(
                    catalog, provider, targetSchema, insert.ColumnNames, values, captureRows, batchContext)
                    .ConfigureAwait(false);
                captured = singleBatch is null ? null : [singleBatch];
                break;

            case InsertDefaultValuesSource:
                if (insert.ColumnNames is not null)
                {
                    throw new InvalidOperationException(
                        $"INSERT INTO '{insert.TableName}' DEFAULT VALUES: a column list cannot " +
                        "be combined with DEFAULT VALUES. Either drop the column list, or list " +
                        "each value (or `DEFAULT`) explicitly via VALUES (...).");
                }
                // DEFAULT VALUES = one row, every target column omitted.
                // Route through ApplyValuesAsync with an empty column list
                // and a single empty row — every slot lands on the omitted
                // fill path (IDENTITY → DEFAULT expr → NULL → throw).
                InsertValuesSource synthesisedValues = new(new[] { (IReadOnlyList<Expression>)Array.Empty<Expression>() });
                RowBatch? defaultBatch = await ApplyValuesAsync(
                    catalog, provider, targetSchema,
                    columnList: Array.Empty<string>(),
                    synthesisedValues, captureRows, batchContext).ConfigureAwait(false);
                captured = defaultBatch is null ? null : [defaultBatch];
                break;

            case InsertQuerySource queryRow:
                captured = await ApplySelectAsync(
                    catalog, provider, targetSchema, insert.ColumnNames, queryRow, captureRows, batchContext)
                    .ConfigureAwait(false);
                break;

            default:
                throw new NotSupportedException(
                    $"Unrecognised INSERT source: {insert.Source.GetType().Name}.");
        }

        if (captured is null) return EmptyQueryPlan.Instance;

        return new DmlReturningPlan(
            DmlReturningKind.Insert,
            insert.TableName,
            targetSchema,
            captured,
            insert.Returning!,
            catalog);
    }

    private static async Task<RowBatch?> ApplyValuesAsync(
        TableCatalog catalog,
        ITableProvider provider,
        Schema targetSchema,
        IReadOnlyList<string>? columnList,
        InsertValuesSource values,
        bool captureRows,
        BatchContext? batchContext)
    {
        // See UpdateExecutor for the non-null fallback rationale.
        MemoryAccountant accountant = batchContext is not null ? batchContext.Accountant : new MemoryAccountant();
        if (values.Rows.Count == 0)
        {
            // Nothing to insert. Don't open a session — keeps the
            // semantics simple ("INSERT … VALUES with zero rows is a
            // no-op") and avoids a noisy empty commit.
            return null;
        }

        // Source-column count is the column list size if provided, else
        // the full schema arity. Per-row arity is validated below — the
        // common case is that every VALUES row matches.
        int sourceColumnCount = columnList?.Count ?? targetSchema.Columns.Count;
        ColumnPlan plan = ResolveColumnPlan(
            targetSchema, columnList, sourceColumnCount,
            defersComputedRejectionToPerRow: true);

        // Build the PK uniqueness checker before opening the session.
        // The pre-scan runs against the same snapshot the writer is
        // about to mutate; concurrent INSERTs serialize via the
        // provider's mutation lock so the snapshot stays valid through
        // commit.
        PrimaryKeyChecker? pkChecker = await PrimaryKeyChecker.CreateAsync(provider, targetSchema)
            .ConfigureAwait(false);

        // Open the session early so IDENTITY-reservation calls have a
        // place to land. Dispose-without-commit aborts on any throw,
        // matching the existing semantics of AppendRowsAsync.
        await using IAppendSession session = provider.BeginAppend();

        // Build a single batch covering every VALUES row. INSERT VALUES
        // is bounded — users don't write 10M rows inline — so a one-shot
        // batch beats per-row session writes.
        Arena arena = new();
        ColumnLookup lookup = BuildTargetLookup(targetSchema);
        RowBatch batch = catalog.Pool.RentRowBatch(lookup, capacity: values.Rows.Count, arena: arena);
        // Account the batch's GC-resident skeleton against the surrounding
        // batch's budget: DataValue cells (~20 bytes each) plus a ~24-byte
        // per-row header. Arena payload bytes are file-backed mmap and don't
        // count. Released after session.WriteAsync flushes the batch.
        long valuesBatchBytes = (long)values.Rows.Count * (targetSchema.Columns.Count * 20L + 24L);
        if (valuesBatchBytes > 0) accountant.NotifyMaterialized(valuesBatchBytes);

        // Tableless evaluator: VALUES expressions can use binary operators,
        // function calls, and array / struct literals; they cannot reference
        // columns (no source row). Same arena for source and target so array
        // payloads materialised by array(...) land directly in the batch's
        // arena and ConvertSourceValue can pass them through without copy.
        ExpressionEvaluator evaluator = new(catalog.Functions, store: arena, accountant: accountant);
        ColumnLookup emptyLookup = new(Array.Empty<string>());
        Row emptyRow = new(emptyLookup, Array.Empty<DataValue>());
        EvaluationFrame frame = new(emptyRow, arena, arena, accountant);

        for (int rowIndex = 0; rowIndex < values.Rows.Count; rowIndex++)
        {
            IReadOnlyList<Expression> sourceRow = values.Rows[rowIndex];
            if (sourceRow.Count != plan.SourceColumnCount)
            {
                throw new InvalidOperationException(
                    $"INSERT INTO '{provider.QualifiedName}': VALUES row {rowIndex + 1} has " +
                    $"{sourceRow.Count} value(s), but the column list expects {plan.SourceColumnCount}.");
            }

            DataValue[] targetRow = catalog.Pool.RentDataValues(targetSchema.Columns.Count);
            for (int targetIndex = 0; targetIndex < targetSchema.Columns.Count; targetIndex++)
            {
                ColumnInfo target = targetSchema.Columns[targetIndex];
                int sourceIndex = plan.SourceIndexForTarget[targetIndex];

                if (sourceIndex >= 0)
                {
                    Expression sourceExpr = sourceRow[sourceIndex];
                    if (sourceExpr is DefaultValueExpression)
                    {
                        // PG `DEFAULT` keyword inside VALUES — route this
                        // slot through the same resolution path an omitted
                        // column would use (IDENTITY → DEFAULT expr →
                        // NULL → throw). Note: for IDENTITY columns this
                        // fires the counter regardless of ALWAYS / BY
                        // DEFAULT — DEFAULT keyword means "let the column
                        // generate the value".
                        targetRow[targetIndex] = await ResolveDefaultKeywordSlotAsync(
                            target, arena, session, evaluator, frame,
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    else
                    {
                        // Explicit (non-DEFAULT) value supplied. Computed
                        // columns reject any explicit value — only the
                        // `DEFAULT` keyword (handled above) is accepted,
                        // matching PostgreSQL's GENERATED-column semantics.
                        // Without this guard the second-pass computed
                        // evaluation would silently overwrite the user's
                        // value.
                        if (target.ComputedExpression is not null)
                        {
                            throw new InvalidOperationException(
                                $"INSERT into target column '{target.Name}': column is GENERATED " +
                                "ALWAYS AS (computed). Supply `DEFAULT` in this VALUES slot, drop " +
                                "the column from the INSERT column list, or omit it from the " +
                                "positional row — the catalog will compute the value from its " +
                                "expression.");
                        }
                        // GENERATED ALWAYS AS IDENTITY (and legacy bare
                        // IDENTITY) rejects explicit values per row — the
                        // only way to fill an ALWAYS IDENTITY slot from a
                        // column-listed VALUES row is the `DEFAULT` keyword
                        // (handled above). GENERATED BY DEFAULT accepts as-is.
                        IdentitySpec? id = target.Identity;
                        if (id is not null && !id.AcceptUserValues)
                        {
                            throw new InvalidOperationException(
                                $"INSERT into '{target.Name}': cannot supply an explicit value for " +
                                "a GENERATED ALWAYS AS IDENTITY column. Use the `DEFAULT` keyword " +
                                "in this VALUES slot, omit the column from the column list, or " +
                                "declare the column as GENERATED BY DEFAULT AS IDENTITY.");
                        }

                        // Pre-fold any scalar subqueries — VALUES expressions can
                        // contain `(SELECT x FROM y LIMIT 1)` and the tableless
                        // evaluator only handles literal/binary/function shapes.
                        // Mirrors BatchExecutor.PrefoldSubqueriesAsync for the
                        // INSERT-VALUES path.
                        sourceExpr = await PrefoldSubqueriesAsync(
                            sourceExpr, catalog, CancellationToken.None).ConfigureAwait(false);

                        ValueRef evaluated = await evaluator.EvaluateAsValueRefAsync(
                            sourceExpr, frame, CancellationToken.None).ConfigureAwait(false);
                        targetRow[targetIndex] = ComputedColumnEvaluator.ConvertValueRefToTarget(
                            evaluated, target, arena, target.Name);
                    }
                }
                else
                {
                    // Omitted column: fill from the per-row evaluator path
                    // (Default expression / IDENTITY reservation), or place
                    // a NULL slot for the second-pass Computed eval.
                    targetRow[targetIndex] = await ResolveOmittedFillAsync(
                        plan, targetIndex, target, arena, session,
                        evaluator, frame, CancellationToken.None).ConfigureAwait(false);
                }
            }

            // Computed columns evaluate after every other slot is filled —
            // they can reference any non-computed column in the row, and
            // catalog validation forbids computed-to-computed dependencies
            // so a single pass is sufficient.
            await EvaluateComputedColumnsAsync(
                catalog, targetSchema, lookup, plan, targetRow, arena).ConfigureAwait(false);

            pkChecker?.EnsureUnique(targetRow, targetSchema.Columns, arena);
            batch.Add(targetRow);
        }

        await session.WriteAsync(batch).ConfigureAwait(false);
        await session.CommitAsync().ConfigureAwait(false);

        // The session has flushed the batch's rows into the table. The
        // in-memory batch is about to be returned to the pool (or captured
        // for RETURNING — but RETURNING capture is already accounted at the
        // SELECT plan layer for SELECT-source INSERTs; for VALUES the capture
        // is the same batch we just notified about, so a single release is
        // correct).
        if (valuesBatchBytes > 0) accountant.NotifyReleased(valuesBatchBytes);

        // Capture the resolved batch for RETURNING. The arena holds the
        // string / blob payloads for non-inline DataValues, so the plan
        // must keep both alive until the caller has iterated. RETURNING
        // is post-commit semantics — yielding only happens after a
        // successful CommitAsync, so an aborted INSERT yields nothing.
        return captureRows ? batch : null;
    }

    /// <summary>
    /// Streams an <c>INSERT … SELECT</c>'s source query through an
    /// <see cref="IAppendSession"/>. Plans the source, sizes the column
    /// plan from the source projection's arity (read off the first
    /// non-empty batch's <see cref="ColumnLookup"/>), then per source
    /// batch builds a target-shaped batch via <see cref="ConvertSourceValue"/>
    /// and writes through the session. Commit fires on completion;
    /// exceptions abort via the session's dispose-without-commit
    /// semantics (PR9 behaviour).
    /// </summary>
    private static async Task<IReadOnlyList<RowBatch>?> ApplySelectAsync(
        TableCatalog catalog,
        ITableProvider provider,
        Schema targetSchema,
        IReadOnlyList<string>? columnList,
        InsertQuerySource source,
        bool captureRows,
        BatchContext? batchContext)
    {
        // INSERT … SELECT streams batch-by-batch through the source plan and
        // flushes each via session.WriteAsync — no held state of consequence to
        // account. The source-side plan still routes through its own
        // ExecutionContext and accounts there. The accountant here is only
        // needed for the per-row DEFAULT-evaluation frames built below.
        MemoryAccountant accountant = batchContext is not null ? batchContext.Accountant : new MemoryAccountant();
        IQueryPlan sourcePlan = catalog.PlanQuery(source.Query);
        ColumnLookup targetLookup = BuildTargetLookup(targetSchema);

        // PK uniqueness check — built once against the pre-INSERT
        // snapshot. Per-row collisions throw, aborting the session
        // (dispose without commit).
        PrimaryKeyChecker? pkChecker = await PrimaryKeyChecker.CreateAsync(provider, targetSchema)
            .ConfigureAwait(false);

        // Plan resolution is deferred to the first batch — that's where
        // we learn the source projection arity. An empty source ends up
        // a no-op (matches PostgreSQL's INSERT … SELECT-with-no-rows
        // semantics).
        ColumnPlan? plan = null;
        IAppendSession? session = null;
        List<RowBatch>? capturedBatches = captureRows ? new() : null;
        bool committed = false;

        try
        {
            await foreach (RowBatch sourceBatch in
                sourcePlan.ExecuteAsync(CancellationToken.None).ConfigureAwait(false))
            {
                if (sourceBatch.Count == 0) continue;

                if (plan is null)
                {
                    int sourceColumnCount = sourceBatch.ColumnLookup.Count;
                    plan = ResolveColumnPlan(targetSchema, columnList, sourceColumnCount);
                    session = provider.BeginAppend();
                }

                IValueStore sourceStore = sourceBatch.Arena;
                Arena targetArena = new();
                RowBatch targetBatch = catalog.Pool.RentRowBatch(
                    targetLookup, capacity: sourceBatch.Count, arena: targetArena);

                // Tableless evaluator + empty frame for the per-row DEFAULT
                // evaluation path. Built per batch because targetArena is
                // batch-scoped; reused across every row in the batch.
                ExpressionEvaluator defaultEvaluator = new(catalog.Functions, store: targetArena, accountant: accountant);
                ColumnLookup emptyLookup = new(Array.Empty<string>());
                Row emptyRow = new(emptyLookup, Array.Empty<DataValue>());
                EvaluationFrame defaultFrame = new(emptyRow, targetArena, targetArena, accountant);

                for (int r = 0; r < sourceBatch.Count; r++)
                {
                    Row sourceRow = sourceBatch[r];
                    DataValue[] targetRow = catalog.Pool.RentDataValues(targetSchema.Columns.Count);

                    for (int targetIndex = 0; targetIndex < targetSchema.Columns.Count; targetIndex++)
                    {
                        ColumnInfo target = targetSchema.Columns[targetIndex];
                        int sourceIndex = plan.SourceIndexForTarget[targetIndex];

                        if (sourceIndex >= 0)
                        {
                            // ALWAYS IDENTITY rejects explicit values from
                            // the SELECT projection — same per-row gate
                            // the VALUES path applies. INSERT … SELECT
                            // has no `DEFAULT` keyword escape, so the
                            // only fix is to omit the column from the
                            // INSERT column list.
                            IdentitySpec? id = target.Identity;
                            if (id is not null && !id.AcceptUserValues)
                            {
                                throw new InvalidOperationException(
                                    $"INSERT … SELECT into '{target.Name}': cannot supply an " +
                                    "explicit value for a GENERATED ALWAYS AS IDENTITY column. " +
                                    "Omit the column from the INSERT column list, or declare it " +
                                    "as GENERATED BY DEFAULT AS IDENTITY.");
                            }
                            targetRow[targetIndex] = ConvertSourceValue(
                                sourceRow[sourceIndex], sourceStore, catalog.SidecarRegistry,
                                target, targetArena, target.Name);
                        }
                        else
                        {
                            targetRow[targetIndex] = await ResolveOmittedFillAsync(
                                plan, targetIndex, target, targetArena, session!,
                                defaultEvaluator, defaultFrame, CancellationToken.None).ConfigureAwait(false);
                        }
                    }

                    // Computed columns evaluate post-fill against the row
                    // (see EvaluateComputedColumnsAsync for the contract).
                    await EvaluateComputedColumnsAsync(
                        catalog, targetSchema, targetLookup, plan, targetRow, targetArena).ConfigureAwait(false);

                    pkChecker?.EnsureUnique(targetRow, targetSchema.Columns, targetArena);
                    targetBatch.Add(targetRow);
                }

                await session!.WriteAsync(targetBatch).ConfigureAwait(false);

                // After WriteAsync, the target batch's arena is no longer
                // needed by the writer — encoded bytes are in the file
                // (or in-memory store). For RETURNING we keep the batch
                // alive so the projection can read from it post-commit;
                // otherwise it goes out of scope and gets GC'd as before.
                if (capturedBatches is not null)
                {
                    capturedBatches.Add(targetBatch);
                }
            }

            if (session is not null)
            {
                await session.CommitAsync().ConfigureAwait(false);
                committed = true;
            }
        }
        finally
        {
            if (session is not null)
            {
                // Dispose closes / aborts whichever the session is in.
                // After a successful CommitAsync this is a no-op; on an
                // unhandled exception it triggers the abort path.
                await session.DisposeAsync().ConfigureAwait(false);
            }

            // If commit didn't reach (exception path), don't surface partial
            // captured rows to the RETURNING plan — return them to the pool
            // so the caller sees null (no rows yielded) per post-commit
            // semantics.
            if (!committed && capturedBatches is not null)
            {
                foreach (RowBatch b in capturedBatches)
                {
                    catalog.Pool.ReturnRowBatch(b);
                }
                capturedBatches = null;
            }
        }

        return capturedBatches;
    }

    /// <summary>
    /// Converts a single value out of an <c>INSERT … SELECT</c> source
    /// batch into the target column's <see cref="DataKind"/>, routing
    /// through <see cref="LiteralCoercion.Coerce"/> so the lossless
    /// numeric / string / boolean / Uuid coercion rules stay shared
    /// with VALUES. Source kinds outside the supported set throw with a
    /// descriptive message — composite kinds (Struct, typed arrays,
    /// Image, Audio, ByteArray, …) are intentionally deferred to a
    /// future PR.
    /// </summary>
    private static DataValue ConvertSourceValue(
        DataValue source,
        IValueStore sourceStore,
        SidecarRegistry sidecarRegistry,
        ColumnInfo target,
        Arena targetArena,
        string columnName)
    {
        if (source.IsNull)
        {
            return LiteralCoercion.Coerce(null, target, targetArena, columnName);
        }

        // Typed-array source for typed-array target: shapes must match.
        // VALUES paths use a single arena (sourceStore == targetArena), so
        // the array DataValue's offsets are valid in the batch and can be
        // passed through directly. INSERT … SELECT cross-arena array copy
        // is a future enhancement — caller's arena equality is the gate.
        if (target.IsArray)
        {
            if (!source.IsArray || source.Kind != target.Kind)
            {
                throw new InvalidOperationException(
                    $"INSERT for column '{columnName}': target is {target.Kind}[] but the " +
                    $"supplied value is {source.Kind}{(source.IsArray ? "[]" : "")}.");
            }
            if (!ReferenceEquals(sourceStore, targetArena))
            {
                throw new InvalidOperationException(
                    $"INSERT for column '{columnName}': cross-arena typed-array copy is not " +
                    "yet supported. INSERT … SELECT of array columns from another table will " +
                    "land in a later PR.");
            }
            LiteralCoercion.EnforceFixedShape(source, target, columnName);
            return source;
        }

        // Reject array-to-scalar shape mismatches.
        if (source.IsArray)
        {
            throw new InvalidOperationException(
                $"INSERT for column '{columnName}': target is scalar {target.Kind} but the " +
                "supplied value is an array.");
        }

        // Blob-kind sources (Image / Audio / Video / Json) carry byte payloads
        // addressed by (p0, p1) into the source store — possibly in a sidecar.
        // Re-emitting them in the target arena is the same byte-copy in either
        // direction; reject only on a kind mismatch (no implicit Image→Audio
        // coercion). Same-arena pass-through skips the copy.
        if (source.IsBlobKind)
        {
            if (target.Kind != source.Kind)
            {
                throw new InvalidOperationException(
                    $"INSERT for column '{columnName}': target is {target.Kind} but the " +
                    $"supplied value is {source.Kind}; blob kinds (Image/Audio/Video/Json) " +
                    "do not coerce across kinds.");
            }
            if (ReferenceEquals(sourceStore, targetArena) && !source.IsInSidecar)
            {
                return source;
            }
            ReadOnlySpan<byte> bytes = source.AsByteSpan(sourceStore, sidecarRegistry);
            return source.Kind switch
            {
                DataKind.Image => DataValue.FromImage(bytes.ToArray(), targetArena),
                DataKind.Audio => DataValue.FromAudio(bytes.ToArray(), targetArena),
                DataKind.Video => DataValue.FromVideo(bytes.ToArray(), targetArena),
                DataKind.Json => DataValue.FromJson(bytes, targetArena),
                _ => throw new InvalidOperationException(
                    $"INSERT for column '{columnName}': unhandled blob kind {source.Kind}."),
            };
        }

        // Struct values surface from struct literals or struct-returning
        // functions. Per-field coercion to a struct target needs the Value
        // Type Registry (see PR16d). Reject explicitly.
        if (source.Kind == DataKind.Struct)
        {
            throw new InvalidOperationException(
                $"INSERT for column '{columnName}': struct values are not yet supported. " +
                "Struct-typed manifest support lands with the Value Type Registry.");
        }

        // Inline / String kinds: route to DataValue.ToObject(store, registry)
        // which boxes the scalar (and resolves String across inline / arena /
        // sidecar tiers). Composite and blob kinds are gated above; anything
        // that survives to here is safe for LiteralCoercion.Coerce — if the
        // target.Kind can't accept the source kind, LiteralCoercion throws
        // with the canonical message.
        object? scalar = source.ToObject(sourceStore, sidecarRegistry);
        return LiteralCoercion.Coerce(scalar, target, targetArena, columnName);
    }

    /// <summary>
    /// Resolves how each target schema column gets its value: either an
    /// index into the source row (VALUES tuple or SELECT projection),
    /// or a default-fill plan (<see cref="OmittedFill.Default"/>) /
    /// null-fill plan (<see cref="OmittedFill.Null"/>). Rejects every
    /// shape that can't produce a value (omitted column with no
    /// <c>DEFAULT</c> on a non-nullable target). Shared between
    /// <see cref="ApplyValuesAsync"/> and <see cref="ApplySelectAsync"/> — the
    /// only difference is whether <paramref name="sourceColumnCount"/>
    /// comes from the column list / VALUES tuple width or from the
    /// source query's projection arity (read off the first batch's
    /// <see cref="ColumnLookup"/>).
    /// </summary>
    private static ColumnPlan ResolveColumnPlan(
        Schema targetSchema,
        IReadOnlyList<string>? columnList,
        int sourceColumnCount,
        bool defersComputedRejectionToPerRow = false)
    {
        // Column-list arity must match source-row arity when both are
        // present; otherwise the per-source-row arity is implied to be
        // the schema's column count (positional default).
        if (columnList is not null && columnList.Count != sourceColumnCount)
        {
            throw new InvalidOperationException(
                $"INSERT column list has {columnList.Count} column(s), but the source produces " +
                $"{sourceColumnCount} value(s) per row. The two must match.");
        }

        // SourceIndexForTarget[i] = index into the source row that
        // supplies target column i, or -1 if column i is omitted.
        int[] sourceIndexForTarget = new int[targetSchema.Columns.Count];
        Array.Fill(sourceIndexForTarget, -1);

        if (columnList is null)
        {
            // Positional match against the full schema, in declaration order.
            if (sourceColumnCount != targetSchema.Columns.Count)
            {
                throw new InvalidOperationException(
                    $"INSERT VALUES has {sourceColumnCount} value(s) per row, but " +
                    $"the table has {targetSchema.Columns.Count} column(s). " +
                    "Either supply a value for every column or use an explicit column list.");
            }
            // Positional INSERT: every column index supplies a source.
            // IDENTITY-ALWAYS rejection of explicit values moved to
            // per-row time so `DEFAULT` keyword in VALUES can still
            // satisfy an IDENTITY-ALWAYS slot (it routes to the counter
            // exactly like an omitted column would).
            for (int i = 0; i < targetSchema.Columns.Count; i++)
            {
                sourceIndexForTarget[i] = i;
            }
        }
        else
        {
            // Named match. Each name must exist in the schema; rejects
            // duplicates so "INSERT INTO t (a, a) VALUES (1, 2)" doesn't
            // silently overwrite.
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            for (int sourceIdx = 0; sourceIdx < columnList.Count; sourceIdx++)
            {
                string name = columnList[sourceIdx];
                if (!seen.Add(name))
                {
                    throw new InvalidOperationException(
                        $"INSERT column list mentions '{name}' more than once.");
                }
                int targetIdx = FindColumnIndex(targetSchema, name);
                if (targetIdx < 0)
                {
                    throw new InvalidOperationException(
                        $"INSERT column '{name}' does not exist on the target table.");
                }
                // IDENTITY-ALWAYS rejection of explicit values is now
                // per-row (a row can supply `DEFAULT` keyword for the
                // slot, which routes to the counter and is allowed even
                // on ALWAYS IDENTITY).
                sourceIndexForTarget[targetIdx] = sourceIdx;
            }
        }

        // For every omitted target column, decide the fill: IDENTITY →
        // session.ReserveNextIdentityValue, DEFAULT (must be a literal —
        // PR10b's validation enforces this at CREATE TABLE time), NULL
        // (column must be Nullable), else throw.
        OmittedFill[] omittedFills = new OmittedFill[targetSchema.Columns.Count];
        for (int i = 0; i < targetSchema.Columns.Count; i++)
        {
            ColumnInfo target = targetSchema.Columns[i];

            // Computed columns: in the SELECT path, any explicit source
            // slot is rejected eagerly here (the source is a column
            // value, never DEFAULT keyword). In the VALUES path, the
            // caller passes defersComputedRejectionToPerRow=true and
            // performs the check per row, because PG allows DEFAULT
            // keyword in a computed-column slot (treated as omitted)
            // and only rejects other explicit values.
            if (target.ComputedExpression is not null)
            {
                if (sourceIndexForTarget[i] >= 0 && !defersComputedRejectionToPerRow)
                {
                    throw new InvalidOperationException(
                        $"INSERT into target column '{target.Name}': column is GENERATED ALWAYS AS " +
                        "(computed). Drop it from the INSERT column list and the catalog will " +
                        "compute the value from its expression.");
                }
                omittedFills[i] = OmittedFill.Computed;
                continue;
            }

            if (sourceIndexForTarget[i] >= 0)
            {
                omittedFills[i] = OmittedFill.None;
                continue;
            }

            if (target.Identity is not null)
            {
                omittedFills[i] = OmittedFill.Identity;
                continue;
            }

            if (target.DefaultExpression is not null)
            {
                // DEFAULT is evaluated per row via ExpressionEvaluator at
                // fill time. The catalog's ValidateDefaultExpression has
                // already rejected anything that needs a source row.
                omittedFills[i] = OmittedFill.Default;
                continue;
            }

            if (target.Nullable)
            {
                omittedFills[i] = OmittedFill.Null;
                continue;
            }

            throw new InvalidOperationException(
                $"INSERT into target column '{target.Name}': column is NOT NULL with no DEFAULT, " +
                "but no value was supplied for it. Add the column to the INSERT column list, " +
                "or add a DEFAULT to the column at CREATE TABLE time.");
        }

        return new ColumnPlan(sourceColumnCount, sourceIndexForTarget, omittedFills);
    }

    /// <summary>
    /// Resolves a <c>DEFAULT</c>-keyword slot in a VALUES row. Mirrors the
    /// omitted-column resolution chain: IDENTITY counter → DEFAULT
    /// expression (evaluated per row) → NULL (if Nullable) → error. Used
    /// when a VALUES row contains <c>DEFAULT</c> at a slot whose column
    /// IS supplied via the column-list (positional or named).
    /// </summary>
    private static async Task<DataValue> ResolveDefaultKeywordSlotAsync(
        ColumnInfo target,
        Arena arena,
        IAppendSession session,
        ExpressionEvaluator evaluator,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        // Computed: placeholder NULL — the second-pass computed-column
        // evaluation overwrites this slot.
        if (target.ComputedExpression is not null)
        {
            return DataValue.Null(target.Kind);
        }
        // IDENTITY: reserve next counter value regardless of ALWAYS / BY
        // DEFAULT — DEFAULT keyword always means "let the column generate".
        if (target.Identity is not null)
        {
            return LiteralCoercion.Coerce(
                session.ReserveNextIdentityValue(), target, arena, target.Name);
        }
        // DEFAULT expression: per-row evaluator (same path as omitted
        // column).
        if (target.DefaultExpression is not null)
        {
            ValueRef evaluated = await evaluator.EvaluateAsValueRefAsync(
                target.DefaultExpression, frame, cancellationToken).ConfigureAwait(false);
            return ComputedColumnEvaluator.ConvertValueRefToTarget(
                evaluated, target, arena, target.Name);
        }
        // Nullable: NULL.
        if (target.Nullable)
        {
            return DataValue.Null(target.Kind);
        }
        // NOT NULL with no DEFAULT/IDENTITY — DEFAULT keyword has no
        // fallback. Same error shape an omitted column would produce.
        throw new InvalidOperationException(
            $"INSERT into target column '{target.Name}': DEFAULT keyword cannot be used on a " +
            "NOT NULL column with no DEFAULT or IDENTITY clause. Supply an explicit value.");
    }

    /// <summary>
    /// Resolves the value for an omitted column. <c>Default</c> evaluates
    /// the column's <see cref="ColumnInfo.DefaultExpression"/> per row via
    /// <paramref name="evaluator"/> against an empty <paramref name="frame"/>
    /// — so <c>DEFAULT now()</c> captures the row's INSERT time,
    /// <c>DEFAULT gen_random_uuid()</c> produces a fresh UUID per row, and
    /// <c>DEFAULT [1,2,3]</c> materialises a typed array into the row's arena.
    /// </summary>
    private static async Task<DataValue> ResolveOmittedFillAsync(
        ColumnPlan plan,
        int targetIndex,
        ColumnInfo target,
        Arena arena,
        IAppendSession session,
        ExpressionEvaluator evaluator,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        OmittedFill fill = plan.OmittedFills[targetIndex];
        switch (fill.Kind)
        {
            case OmittedFill.FillKind.Null:
                return DataValue.Null(target.Kind);

            case OmittedFill.FillKind.Default:
            {
                ValueRef evaluated = await evaluator.EvaluateAsValueRefAsync(
                    target.DefaultExpression!, frame, cancellationToken).ConfigureAwait(false);
                return ComputedColumnEvaluator.ConvertValueRefToTarget(
                    evaluated, target, arena, target.Name);
            }

            case OmittedFill.FillKind.Identity:
                return LiteralCoercion.Coerce(
                    session.ReserveNextIdentityValue(), target, arena, target.Name);

            // Computed columns are filled by a second per-row pass after
            // every other column resolves; placeholder here so the slot
            // exists in the row array (NULL of the target kind).
            case OmittedFill.FillKind.Computed:
                return DataValue.Null(target.Kind);

            default:
                throw new InvalidOperationException(
                    $"Internal error: column '{target.Name}' has no source index and no fill.");
        }
    }

    /// <summary>
    /// Second-pass evaluation for <c>GENERATED ALWAYS AS</c> columns. Runs
    /// after every other slot in <paramref name="targetRow"/> is filled —
    /// each computed expression evaluates against the row built so far,
    /// then overwrites its slot. Computed columns can reference any non-
    /// computed column (DEFAULT-filled, IDENTITY-filled, or VALUES-supplied);
    /// computed-to-computed dependencies are rejected at <c>CREATE TABLE</c>
    /// time so a single pass suffices.
    /// </summary>
    private static async Task EvaluateComputedColumnsAsync(
        TableCatalog catalog,
        Schema targetSchema,
        ColumnLookup targetLookup,
        ColumnPlan plan,
        DataValue[] targetRow,
        Arena arena)
    {
        bool any = false;
        for (int i = 0; i < plan.OmittedFills.Length; i++)
        {
            if (plan.OmittedFills[i].Kind == OmittedFill.FillKind.Computed)
            {
                any = true;
                break;
            }
        }
        if (!any) return;

        // The frame's Row carries the partially-resolved row so the
        // expression can read column references via ColumnReference.
        // Source and Target arenas are both the INSERT batch's arena —
        // computed values land directly in the batch.
        ExpressionEvaluator evaluator = new(catalog.Functions, store: arena);
        Row partialRow = new(targetLookup, targetRow);
        EvaluationFrame frame = new(partialRow, arena, arena, evaluator.Accountant);

        for (int i = 0; i < plan.OmittedFills.Length; i++)
        {
            if (plan.OmittedFills[i].Kind != OmittedFill.FillKind.Computed) continue;

            ColumnInfo target = targetSchema.Columns[i];
            ValueRef evaluated = await evaluator.EvaluateAsValueRefAsync(
                target.ComputedExpression!, frame, CancellationToken.None).ConfigureAwait(false);
            targetRow[i] = ComputedColumnEvaluator.ConvertValueRefToTarget(evaluated, target, arena, target.Name);
        }
    }

    private static int FindColumnIndex(Schema schema, string columnName)
    {
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            if (string.Equals(schema.Columns[i].Name, columnName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    private static ColumnLookup BuildTargetLookup(Schema schema)
    {
        string[] names = new string[schema.Columns.Count];
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            names[i] = schema.Columns[i].Name;
        }
        return new ColumnLookup(names);
    }

    /// <summary>
    /// Resolved column-binding plan for an <c>INSERT</c>: how each
    /// target schema column is filled, plus the cached fill for omitted
    /// columns.
    /// </summary>
    private sealed record ColumnPlan(
        int SourceColumnCount,
        int[] SourceIndexForTarget,
        OmittedFill[] OmittedFills);

    /// <summary>
    /// Rejects per-row INSERT candidates whose PRIMARY KEY collides with an
    /// existing row, collides with another row in the same batch, or
    /// contains a NULL in any PK column.
    /// </summary>
    /// <remarks>
    /// Two backing modes:
    /// <list type="bullet">
    /// <item><b>Lookup-backed (PR10h, single-column PK)</b> — when the
    /// provider exposes a non-null <see cref="IPrimaryKeyLookup"/>, the
    /// checker probes the on-disk B+Tree per row instead of pre-scanning.
    /// O(insert size × log table size) instead of O(table size).</item>
    /// <item><b>Scan-backed (PR10f fallback)</b> — for composite PK or
    /// providers without an on-disk index (TEMP / InMemory), pre-loads
    /// every existing row's PK into a <c>HashSet&lt;string&gt;</c>.</item>
    /// </list>
    /// Both modes share the same within-batch dedup HashSet so a duplicate
    /// inside the same INSERT is caught without consulting the index.
    /// </remarks>
    private sealed class PrimaryKeyChecker
    {
        private readonly IReadOnlyList<int> _pkIndices;
        private readonly HashSet<string> _seenKeys;
        private readonly IPrimaryKeyLookup? _lookup;

        private PrimaryKeyChecker(
            IReadOnlyList<int> pkIndices,
            HashSet<string> seenKeys,
            IPrimaryKeyLookup? lookup)
        {
            _pkIndices = pkIndices;
            _seenKeys = seenKeys;
            _lookup = lookup;
        }

        /// <summary>
        /// Returns <see langword="null"/> when the target schema has no
        /// PRIMARY KEY. For a single-column PK with a provider-supplied
        /// <see cref="IPrimaryKeyLookup"/>, returns a lookup-backed checker
        /// (no pre-scan). Otherwise falls back to PR10f's scan + HashSet.
        /// </summary>
        public static async Task<PrimaryKeyChecker?> CreateAsync(
            ITableProvider provider, Schema targetSchema)
        {
            IReadOnlyList<int> pkIndices = targetSchema.PrimaryKeyColumnIndices;
            if (pkIndices.Count == 0) return null;

            // PK index path: provider-supplied on-disk lookup (single-column
            // typed tree OR composite bytes tree) → lookup-backed checker.
            // No pre-scan; uniqueness check probes the live index per row.
            if (provider.GetPrimaryKeyLookup() is { } lookup)
            {
                return new PrimaryKeyChecker(pkIndices, seenKeys: new HashSet<string>(), lookup: lookup);
            }

            // No on-disk index (TEMP / InMemory providers) → fall back to the
            // pre-scan + HashSet path. O(table size) per INSERT, but the table
            // is already in memory for these provider types.
            HashSet<string> seen = new();
            await foreach (RowBatch batch in provider.ScanAsync(
                requiredColumns: null,
                filterHint: null,
                targetArena: null,
                cancellationToken: CancellationToken.None).ConfigureAwait(false))
            {
                try
                {
                    for (int r = 0; r < batch.Count; r++)
                    {
                        Row row = batch[r];
                        string key = BuildKeyFromRow(row, pkIndices);
                        seen.Add(key); // duplicates in existing data are silently kept (the table is already in this state)
                    }
                }
                finally
                {
                    batch.Dispose();
                }
            }
            return new PrimaryKeyChecker(pkIndices, seen, lookup: null);
        }

        /// <summary>
        /// Validates a candidate target row: rejects NULLs in any PK
        /// column, rejects duplicate keys against existing rows, and
        /// rejects within-batch duplicates. Adds the key to the seen
        /// set on success so the next row in the same INSERT can
        /// detect a duplicate without a re-scan.
        /// </summary>
        public void EnsureUnique(DataValue[] targetRow, IReadOnlyList<ColumnInfo> columns, Arena? arena = null)
        {
            // Lookup-backed path: encode the PK tuple via CompositeKeyEncoder
            // and use the encoded bytes for BOTH within-batch dedup (HashSet
            // keyed on base64-of-encoded-bytes) AND the on-disk probe.
            // Handles arbitrary-length strings / composite tuples without
            // going through ToObject() on non-inline DataValues.
            if (_lookup is not null)
            {
                DataValue[] tuple = new DataValue[_pkIndices.Count];
                for (int p = 0; p < _pkIndices.Count; p++)
                {
                    if (targetRow[_pkIndices[p]].IsNull)
                    {
                        throw new PrimaryKeyViolationException(
                            $"PRIMARY KEY column '{columns[_pkIndices[p]].Name}' is NULL; PK values must be non-null.");
                    }
                    tuple[p] = targetRow[_pkIndices[p]];
                }
                byte[] encoded = Indexing.CompositeKeyEncoder.Encode(tuple, arena);
                string seenKey = Convert.ToBase64String(encoded);
                if (!_seenKeys.Add(seenKey))
                {
                    throw new PrimaryKeyViolationException(
                        BuildDuplicateMessage(_pkIndices, columns, targetRow));
                }
                if (_lookup.TryFind(encoded, out _))
                {
                    throw new PrimaryKeyViolationException(
                        BuildDuplicateMessage(_pkIndices, columns, targetRow));
                }
                return;
            }

            // Scan-based fallback (TEMP / InMemoryProvider, no on-disk index).
            // BuildKeyFromArray includes NULL-in-PK check and uses ToObject()
            // for the seen-set hash — works for inline-only PK values, which
            // is the practical case for in-memory tables.
            string key = BuildKeyFromArray(targetRow, _pkIndices, columns);
            if (!_seenKeys.Add(key))
            {
                throw new PrimaryKeyViolationException(
                    BuildDuplicateMessage(_pkIndices, columns, targetRow));
            }
        }

        private static string BuildKeyFromRow(Row row, IReadOnlyList<int> pkIndices)
        {
            System.Text.StringBuilder sb = new();
            for (int p = 0; p < pkIndices.Count; p++)
            {
                if (p > 0) sb.Append('\x1f');
                AppendKeyPart(sb, row[pkIndices[p]]);
            }
            return sb.ToString();
        }

        private static string BuildKeyFromArray(
            DataValue[] row,
            IReadOnlyList<int> pkIndices,
            IReadOnlyList<ColumnInfo> columns)
        {
            System.Text.StringBuilder sb = new();
            for (int p = 0; p < pkIndices.Count; p++)
            {
                if (p > 0) sb.Append('\x1f');
                int idx = pkIndices[p];
                DataValue v = row[idx];
                if (v.IsNull)
                {
                    throw new PrimaryKeyViolationException(
                        $"PRIMARY KEY column '{columns[idx].Name}' is NULL; PK values must be non-null.");
                }
                AppendKeyPart(sb, v);
            }
            return sb.ToString();
        }

        private static void AppendKeyPart(System.Text.StringBuilder sb, DataValue v)
        {
            // PK columns are restricted to fixed-size scalar kinds at
            // CREATE TABLE time, so ToObject covers everything we'll
            // see here. InvariantCulture so number formatting doesn't
            // shift under different locales.
            object? scalar = v.IsNull ? null : v.ToObject();
            sb.Append(System.FormattableString.Invariant($"{scalar}"));
        }

        private static string BuildDuplicateMessage(
            IReadOnlyList<int> pkIndices,
            IReadOnlyList<ColumnInfo> columns,
            DataValue[] row)
        {
            System.Text.StringBuilder sb = new("PRIMARY KEY violation: row with ");
            for (int p = 0; p < pkIndices.Count; p++)
            {
                if (p > 0) sb.Append(", ");
                int idx = pkIndices[p];
                sb.Append(columns[idx].Name).Append('=');
                DataValue v = row[idx];
                sb.Append(System.FormattableString.Invariant($"{(v.IsNull ? "NULL" : v.ToObject())}"));
            }
            sb.Append(" already exists or is duplicated within the same INSERT.");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Per-target-column fill descriptor for columns omitted from the
    /// <c>INSERT</c>'s column list. <see cref="FillKind.None"/> means
    /// the column has a source index and is filled directly; the other
    /// kinds describe the omission's resolution.
    /// </summary>
    private readonly struct OmittedFill
    {
        public enum FillKind : byte { None, Null, Default, Identity, Computed }

        public FillKind Kind { get; }

        private OmittedFill(FillKind kind)
        {
            Kind = kind;
        }

        public static OmittedFill None => default;
        public static OmittedFill Null { get; } = new(FillKind.Null);

        /// <summary>
        /// Marker for columns with a <c>DEFAULT</c> expression. The fill
        /// happens per row via <see cref="ExpressionEvaluator"/> against
        /// an empty <see cref="EvaluationFrame"/>, so non-deterministic
        /// expressions (<c>now()</c>, <c>gen_random_uuid()</c>) produce a
        /// fresh value per row.
        /// </summary>
        public static OmittedFill Default { get; } = new(FillKind.Default);

        public static OmittedFill Identity { get; } = new(FillKind.Identity);

        /// <summary>
        /// Marker for <c>GENERATED ALWAYS AS</c> columns. The fill happens
        /// in a second pass after the row's non-computed slots are filled;
        /// the executor evaluates <see cref="ColumnInfo.ComputedExpression"/>
        /// against the partially-built row and overwrites this slot.
        /// </summary>
        public static OmittedFill Computed { get; } = new(FillKind.Computed);
    }

    /// <summary>
    /// Walks <paramref name="expression"/> and replaces each scalar
    /// <see cref="SubqueryExpression"/> with a <see cref="LiteralExpression"/>
    /// holding the executed subquery's result. Mirrors
    /// <c>BatchExecutor.PrefoldSubqueriesAsync</c> but standalone (no
    /// procedural batch context required) — INSERT VALUES expressions
    /// can include subqueries but the tableless evaluator only handles
    /// scalar shapes, so subqueries must be folded before evaluation.
    /// </summary>
    private static async Task<Expression> PrefoldSubqueriesAsync(
        Expression expression, TableCatalog catalog, CancellationToken ct)
    {
        switch (expression)
        {
            case SubqueryExpression subquery:
                return await FoldOneSubqueryAsync(subquery, catalog, ct).ConfigureAwait(false);

            case BinaryExpression binary:
            {
                Expression left = await PrefoldSubqueriesAsync(binary.Left, catalog, ct).ConfigureAwait(false);
                Expression right = await PrefoldSubqueriesAsync(binary.Right, catalog, ct).ConfigureAwait(false);
                return ReferenceEquals(left, binary.Left) && ReferenceEquals(right, binary.Right)
                    ? binary
                    : new BinaryExpression(left, binary.Operator, right);
            }

            case UnaryExpression unary:
            {
                Expression operand = await PrefoldSubqueriesAsync(unary.Operand, catalog, ct).ConfigureAwait(false);
                return ReferenceEquals(operand, unary.Operand)
                    ? unary
                    : new UnaryExpression(unary.Operator, operand);
            }

            case CastExpression cast:
            {
                Expression inner = await PrefoldSubqueriesAsync(cast.Expression, catalog, ct).ConfigureAwait(false);
                return ReferenceEquals(inner, cast.Expression)
                    ? cast
                    : new CastExpression(inner, cast.TargetType, cast.Span);
            }

            case FunctionCallExpression fn:
            {
                Expression[]? rewrittenArgs = null;
                for (int i = 0; i < fn.Arguments.Count; i++)
                {
                    Expression rewritten = await PrefoldSubqueriesAsync(fn.Arguments[i], catalog, ct).ConfigureAwait(false);
                    if (!ReferenceEquals(rewritten, fn.Arguments[i]))
                    {
                        rewrittenArgs ??= fn.Arguments.ToArray();
                        rewrittenArgs[i] = rewritten;
                    }
                }
                return rewrittenArgs is null
                    ? fn
                    : new FunctionCallExpression(fn.FunctionName, rewrittenArgs);
            }

            default:
                return expression;
        }
    }

    /// <summary>
    /// Plans + executes one scalar subquery and folds its result into a
    /// <see cref="LiteralExpression"/>. Zero rows → NULL literal; more than
    /// one row → error.
    /// </summary>
    private static async Task<Expression> FoldOneSubqueryAsync(
        SubqueryExpression subquery, TableCatalog catalog, CancellationToken ct)
    {
        IQueryPlan innerPlan = catalog.PlanQuery(new SelectQueryExpression(subquery.Query));

        DataValue captured = default;
        bool haveValue = false;
        bool tooManyRows = false;
        Arena foldArena = new();
        try
        {
            await foreach (RowBatch batch in innerPlan.ExecuteAsync(ct).ConfigureAwait(false))
            {
                for (int i = 0; i < batch.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (haveValue)
                    {
                        tooManyRows = true;
                        break;
                    }
                    Row row = batch[i];
                    if (row.FieldCount != 1)
                    {
                        throw new InvalidOperationException(
                            $"Scalar subquery must return exactly one column, but returned {row.FieldCount}.");
                    }
                    captured = DataValueRetention.Stabilize(row[0], batch.Arena, foldArena);
                    haveValue = true;
                }
                if (tooManyRows) break;
            }

            if (tooManyRows)
            {
                throw new InvalidOperationException(
                    "Scalar subquery returned more than one row.");
            }

            if (!haveValue || captured.IsNull)
            {
                return new LiteralExpression(null);
            }

            // Composite / blob kinds can't become a literal expression
            // (LiteralExpression carries a CLR scalar). ToObject() would
            // fall back to ToString() for those — reject explicitly so the
            // diagnostic names the actual kind.
            if (captured.IsArray || captured.IsBlobKind || captured.Kind == DataKind.Struct)
            {
                throw new InvalidOperationException(
                    $"Subquery in INSERT VALUES produced unsupported kind {captured.Kind}.");
            }
            object literal = captured.ToObject(foldArena)!;
            return new LiteralExpression(literal);
        }
        finally
        {
            foldArena.Dispose();
        }
    }
}
