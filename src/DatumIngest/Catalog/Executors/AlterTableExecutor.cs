using DatumIngest.Catalog.Plans;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Indexing;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog.Executors;

/// <summary>
/// Owns the <c>ALTER TABLE</c>-statement pipeline for
/// <see cref="TableCatalog.PlanAsync(Statement)"/>: ADD COLUMN, DROP
/// COLUMN, DROP CONSTRAINT, and ALTER COLUMN DROP (DEFAULT/IDENTITY).
/// </summary>
internal static class AlterTableExecutor
{
    /// <summary>
    /// Applies an <c>ALTER TABLE ADD COLUMN</c> statement. PR10b ships
    /// the additive shape only — the new column must be nullable, the
    /// <c>DEFAULT</c> clause is rejected (existing-row backfill is a
    /// later-PR concern), and computed columns (<c>AS expr</c>) are
    /// reserved for a future PR.
    /// </summary>
    public static async Task<IQueryPlan> AddColumnAsync(
        TableCatalog catalog, AlterTableAddColumnStatement alter, string? sourceText = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(alter);

        // Resolve the schema once. Subsequent lookups go through the
        // string indexer with this qualified form so the router picks the
        // right backend regardless of whether the user wrote
        // `ALTER TABLE t` or `ALTER TABLE myapp.t`.
        QualifiedName qn = catalog.ResolveDdlName(alter.SchemaName, alter.TableName);
        string qualifiedTableName = qn.ToString();
        Schema? beforeSchema = catalog.TryGetTable(qualifiedTableName, out ITableProvider? beforeProvider)
            ? beforeProvider.GetSchema()
            : null;

        // PRIMARY KEY columns implicitly take !Nullable from the parser
        // (matches CREATE TABLE). The general NOT-NULL-on-ALTER restriction
        // doesn't apply here because the PK path requires a guaranteed
        // non-null backfill (IDENTITY on a populated table, or an empty
        // table). Other !Nullable callers still hit the restriction below.
        if (!alter.Nullable && !alter.PrimaryKey)
        {
            throw new InvalidOperationException(
                $"ALTER TABLE '{alter.TableName}' ADD COLUMN '{alter.ColumnName}' NOT NULL " +
                "is not yet supported. Existing rows would need a non-null backfill value, " +
                "and the format does not yet persist a missing-value sentinel for that path. " +
                "Add the column nullable (with or without a DEFAULT) for now.");
        }

        if (!TypeAnnotationResolver.TryParse(alter.TypeName, out DataKind kind, out bool isArray))
        {
            throw new InvalidOperationException(
                $"Unknown column type '{alter.TypeName}' on column '{alter.ColumnName}'.");
        }

        // DEFAULT validation: same literal-only rules as CREATE TABLE. The
        // catalog persists the SQL fragment; existing rows continue to read
        // NULL (the column wasn't present), and new INSERTs that omit this
        // column auto-fill via the existing CREATE-TABLE default path.
        Expression? defaultExpr = alter.DefaultValue;
        if (defaultExpr is not null)
        {
            ColumnDefinitionResolver.ValidateDefaultExpression(defaultExpr, alter.ColumnName);
            await ColumnDefinitionResolver.ValidateDefaultExpressionFitsColumnAsync(catalog, defaultExpr, alter.ColumnName, kind, isArray)
                .ConfigureAwait(false);
        }

        // Computed columns: mutually exclusive with DEFAULT — both supply a
        // value, just from different sides. Pre-existing rows in the table
        // read NULL for the new computed column (no recompute pass against
        // historical rows in v1); only INSERTs after the ALTER fire the
        // expression.
        Expression? computedExpr = alter.ComputedExpression;
        if (computedExpr is not null && defaultExpr is not null)
        {
            throw new InvalidOperationException(
                $"ALTER TABLE '{alter.TableName}' ADD COLUMN '{alter.ColumnName}': cannot combine " +
                "DEFAULT and GENERATED ALWAYS AS — pick one.");
        }

        // IDENTITY validation. Mutually exclusive with DEFAULT and
        // computed expression; rejected when the table already carries
        // an IDENTITY column.
        IdentitySpec? identity = alter.Identity;
        if (identity is not null)
        {
            if (defaultExpr is not null || computedExpr is not null)
            {
                throw new InvalidOperationException(
                    $"ALTER TABLE '{alter.TableName}' ADD COLUMN '{alter.ColumnName}': IDENTITY " +
                    "cannot combine with DEFAULT or GENERATED ALWAYS AS — pick one.");
            }
            ColumnDefinitionResolver.ValidateIdentitySpecForColumn(identity, alter.ColumnName, kind, isArray);
            if (catalog.TryGetTable(qualifiedTableName, out ITableProvider? existingForIdentity))
            {
                Schema existingSchema = existingForIdentity.GetSchema();
                foreach (ColumnInfo existing in existingSchema.Columns)
                {
                    if (existing.Identity is not null)
                    {
                        throw new InvalidOperationException(
                            $"ALTER TABLE '{alter.TableName}' ADD COLUMN '{alter.ColumnName}': " +
                            $"table already has an IDENTITY column '{existing.Name}'. Only one " +
                            "IDENTITY column is allowed per table.");
                    }
                }
            }
        }

        // Reject computed-to-computed dependencies against the table's
        // existing schema. Same rationale as the CREATE TABLE gate: the
        // single-pass row-fill evaluator can't see another computed
        // column's value during evaluation.
        if (computedExpr is not null && catalog.TryGetTable(qualifiedTableName, out ITableProvider? existingProvider))
        {
            Schema existingSchema = existingProvider.GetSchema();
            HashSet<(string? TableName, string ColumnName)> refs =
                ColumnReferenceCollector.Collect(computedExpr);
            foreach ((string? _, string refName) in refs)
            {
                foreach (ColumnInfo existing in existingSchema.Columns)
                {
                    if (string.Equals(existing.Name, refName, StringComparison.OrdinalIgnoreCase) &&
                        existing.ComputedExpression is not null)
                    {
                        throw new ExecutionException(
                            $"ALTER TABLE '{alter.TableName}' ADD COLUMN '{alter.ColumnName}': " +
                            $"GENERATED expressions cannot reference other GENERATED columns " +
                            $"(references '{existing.Name}'). Inline the inner expression instead.");
                    }
                }
            }
        }

        // PRIMARY KEY validation — only one PK per table, and on a
        // non-empty table we need IDENTITY to supply unique non-null
        // values for existing rows (DEFAULT doesn't backfill historical
        // rows in the current writer, and a plain PK column would leave
        // every existing row NULL).
        if (alter.PrimaryKey)
        {
            if (computedExpr is not null)
            {
                throw new InvalidOperationException(
                    $"ALTER TABLE '{alter.TableName}' ADD COLUMN '{alter.ColumnName}': PRIMARY KEY " +
                    "cannot combine with GENERATED ALWAYS AS (computed column).");
            }
            if (catalog.TryGetTable(qualifiedTableName, out ITableProvider? existingForPk))
            {
                Schema existingSchema = existingForPk.GetSchema();
                if (existingSchema.PrimaryKeyColumnIndices.Count > 0)
                {
                    string existingPkName = existingSchema.Columns[existingSchema.PrimaryKeyColumnIndices[0]].Name;
                    throw new InvalidOperationException(
                        $"ALTER TABLE '{alter.TableName}' ADD COLUMN '{alter.ColumnName}': table already " +
                        $"has a PRIMARY KEY (column '{existingPkName}'). Only one PRIMARY KEY per table " +
                        "is supported.");
                }
                if (existingForPk.GetRowCount() > 0 && identity is null)
                {
                    throw new InvalidOperationException(
                        $"ALTER TABLE '{alter.TableName}' ADD COLUMN '{alter.ColumnName}' PRIMARY KEY " +
                        "on a non-empty table requires GENERATED IDENTITY so existing rows can be " +
                        "backfilled with unique non-null values. Either truncate the table first or " +
                        "declare the column GENERATED ALWAYS AS IDENTITY PRIMARY KEY.");
                }
            }
        }

        ColumnInfo column = new(alter.ColumnName, kind, nullable: true)
        {
            IsArray = isArray,
            DefaultExpression = defaultExpr,
            ComputedExpression = computedExpr,
            Identity = identity,
        };
        catalog[qualifiedTableName].AddColumn(column);

        // V2-F2: backfill the just-added computed column against the
        // table's historical rows. provider.AddColumn() pumped NULLs into
        // every existing row's slot for the new column; now we scan the
        // post-mutation snapshot, evaluate the expression per row, and
        // dispatch one UpdateRows call with the computed values.
        //
        // Caveat: non-deterministic calls (now(), uuidv4(), random()) get
        // captured at ALTER time, so every historical row sees the value
        // computed during this scan — not the original INSERT time. New
        // INSERTs after the ALTER continue to evaluate per row, matching
        // the v1 INSERT-time behaviour.
        if (computedExpr is not null)
        {
            await BackfillComputedColumnAsync(catalog, qualifiedTableName, column).ConfigureAwait(false);
        }

        // Promote the new column to PRIMARY KEY. AddColumn has already
        // committed the column (with IDENTITY backfill if specified), so
        // the column is populated. EnablePrimaryKeyAsync scans, builds the
        // PK index, and flips the footer's PrimaryKeyColumnIndices. On any
        // failure (NULL in column, duplicate value) the partial sidecar is
        // cleaned up — we additionally drop the just-added column so the
        // table returns to its pre-ALTER state.
        if (alter.PrimaryKey)
        {
            if (!catalog.TryGetTable(qualifiedTableName, out ITableProvider? provider))
            {
                throw new InvalidOperationException(
                    $"ALTER TABLE '{alter.TableName}' ADD COLUMN '{alter.ColumnName}' PRIMARY KEY: " +
                    "provider for table disappeared between AddColumn and EnablePrimaryKey.");
            }

            int newColumnIndex = -1;
            Schema postAddSchema = provider.GetSchema();
            for (int i = 0; i < postAddSchema.Columns.Count; i++)
            {
                if (string.Equals(postAddSchema.Columns[i].Name, alter.ColumnName, StringComparison.OrdinalIgnoreCase))
                {
                    newColumnIndex = i;
                    break;
                }
            }
            if (newColumnIndex < 0)
            {
                throw new InvalidOperationException(
                    $"ALTER TABLE '{alter.TableName}' ADD COLUMN '{alter.ColumnName}' PRIMARY KEY: " +
                    "could not locate freshly-added column in the post-AddColumn schema.");
            }

            try
            {
                await provider.EnablePrimaryKeyAsync(newColumnIndex).ConfigureAwait(false);
            }
            catch
            {
                // Roll back the just-added column. Use best-effort because
                // the rollback path itself shouldn't fail on a freshly-
                // added column (it isn't part of any PK at this point, so
                // DropColumn's PK rejection doesn't apply).
                try { provider.DropColumn(alter.ColumnName); } catch { /* swallow */ }
                throw;
            }
        }

        if (catalog.TryGetTable(qualifiedTableName, out ITableProvider? afterProvider))
        {
            catalog.Events.Raise(new TableAlteredEvent(qn, beforeSchema, afterProvider.GetSchema(), sourceText));
        }
        return EmptyQueryPlan.Instance;
    }

    /// <summary>
    /// Applies an <c>ALTER TABLE DROP COLUMN</c> statement. The column
    /// is soft-dropped (tombstoned) on the underlying provider; the
    /// data block stays on disk for compaction-time reclamation.
    /// </summary>
    public static IQueryPlan DropColumn(TableCatalog catalog, AlterTableDropColumnStatement alter, string? sourceText = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(alter);

        QualifiedName qn = catalog.ResolveDdlName(alter.SchemaName, alter.TableName);
        string qualifiedTableName = qn.ToString();
        if (!catalog.TryGetTable(qualifiedTableName, out ITableProvider? provider))
        {
            throw new InvalidOperationException(
                $"Table '{alter.TableName}' is not registered in the catalog.");
        }
        Schema beforeSchema = provider.GetSchema();

        // Honor IF EXISTS — schema lookup is the cheapest way to ask
        // "does this column exist?" without poking at provider internals.
        bool columnPresent = false;
        bool columnIsPrimaryKey = false;
        Schema schema = provider.GetSchema();
        foreach (ColumnInfo c in schema.Columns)
        {
            if (string.Equals(c.Name, alter.ColumnName, StringComparison.OrdinalIgnoreCase))
            {
                columnPresent = true;
                columnIsPrimaryKey = c.IsPrimaryKey;
                break;
            }
        }
        if (!columnPresent)
        {
            if (alter.IfExists) return EmptyQueryPlan.Instance;
            throw new InvalidOperationException(
                $"Column '{alter.ColumnName}' does not exist on table '{alter.TableName}'.");
        }
        if (columnIsPrimaryKey)
        {
            // PK columns are load-bearing on the prologue's PK index
            // list and on the runtime uniqueness check. Dropping one
            // would leave the table referencing a non-existent column;
            // require the user to drop the constraint first.
            throw new InvalidOperationException(
                $"Column '{alter.ColumnName}' is part of the table's PRIMARY KEY and cannot be " +
                "dropped. Drop the PRIMARY KEY constraint first (e.g., " +
                $"`ALTER TABLE {alter.TableName} DROP CONSTRAINT {alter.TableName}_pkey`).");
        }

        // PG-style dependent-column check: a column referenced by any
        // computed (`GENERATED ALWAYS AS (...)`) column can't be
        // dropped without first dropping the dependents. Silently
        // allowing the drop would leave the computed expression with a
        // dangling name reference that breaks the next INSERT or
        // UPDATE.
        List<string>? dependentComputedColumns = null;
        foreach (ColumnInfo c in schema.Columns)
        {
            if (c.ComputedExpression is null) continue;
            HashSet<(string? TableName, string ColumnName)> refs =
                ColumnReferenceCollector.Collect(c.ComputedExpression);
            foreach ((string? _, string refName) in refs)
            {
                if (string.Equals(refName, alter.ColumnName, StringComparison.OrdinalIgnoreCase))
                {
                    dependentComputedColumns ??= new List<string>();
                    dependentComputedColumns.Add(c.Name);
                    break;
                }
            }
        }
        if (dependentComputedColumns is not null)
        {
            throw new InvalidOperationException(
                $"Cannot drop column '{alter.ColumnName}' from table '{alter.TableName}' because " +
                $"the following GENERATED column(s) depend on it: {string.Join(", ", dependentComputedColumns)}. " +
                "Drop the dependent column(s) first, or alter their expression to remove the reference.");
        }

        // PG-style index cascade: any composite index that covers the
        // dropped column is silently dropped along with it (Postgres
        // behavior — indexes aren't user-visible "dependent objects" the
        // way views and triggers are). Reads-only access to the index map
        // here; mutation is gated on provider being the persistent
        // .datum variant.
        QualifiedName alterQn = catalog.ResolveDdlName(alter.SchemaName, alter.TableName);
        IReadOnlyList<IndexDescriptor>? indexList =
            catalog.TryResolveBackend(alterQn.Schema, out ITableCatalog? alterBackend)
                ? alterBackend.GetTableIndexes(alterQn)
                : null;
        if (indexList is { Count: > 0 }
            && alterBackend is not null
            && provider is Providers.DatumFileTableProviderV2 datumProvider)
        {
            List<IndexDescriptor>? indexesToDrop = null;
            foreach (IndexDescriptor index in indexList)
            {
                foreach (string col in index.Columns)
                {
                    if (string.Equals(col, alter.ColumnName, StringComparison.OrdinalIgnoreCase))
                    {
                        indexesToDrop ??= new List<IndexDescriptor>();
                        indexesToDrop.Add(index);
                        break;
                    }
                }
            }
            if (indexesToDrop is not null)
            {
                foreach (IndexDescriptor index in indexesToDrop)
                {
                    switch (index.Kind)
                    {
                        case IndexKind.FullText:
                            datumProvider.DropFtsIndex(index.Name);
                            break;
                        case IndexKind.Composite:
                        default:
                            datumProvider.DropCompositeIndex(index.Name);
                            break;
                    }
                    // Persist the index removals before DropColumn runs — if
                    // DropColumn fails, we've already lost the index files,
                    // and the catalog json should reflect that.
                    alterBackend.UnregisterIndex(index.Name, out _);
                }
            }
        }

        catalog[qualifiedTableName].DropColumn(alter.ColumnName);

        if (catalog.TryGetTable(qualifiedTableName, out ITableProvider? afterProvider))
        {
            catalog.Events.Raise(new TableAlteredEvent(qn, beforeSchema, afterProvider.GetSchema(), sourceText));
        }
        return EmptyQueryPlan.Instance;
    }

    /// <summary>
    /// Applies <c>ALTER TABLE name DROP CONSTRAINT constraint_name [IF EXISTS]</c>.
    /// In v1 the only constraint kind that can be dropped is PRIMARY KEY,
    /// whose auto-derived name is <c>&lt;table&gt;_pkey</c>. Other constraint
    /// names produce a PG-flavored "does not exist" error (suppressed by
    /// <c>IF EXISTS</c>).
    /// </summary>
    public static async Task<IQueryPlan> DropConstraintAsync(
        TableCatalog catalog, AlterTableDropConstraintStatement alter, string? sourceText = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(alter);

        QualifiedName qn = catalog.ResolveDdlName(alter.SchemaName, alter.TableName);
        string qualifiedTableName = qn.ToString();
        if (!catalog.TryGetTable(qualifiedTableName, out ITableProvider? provider))
        {
            throw new InvalidOperationException(
                $"Table '{alter.TableName}' is not registered in the catalog.");
        }
        Schema beforeSchema = provider.GetSchema();

        // The PK constraint name might be user-supplied (stored in
        // _persistentTablePkNames) or derived from the table name
        // (<table>_pkey). GetPrimaryKeyConstraintName returns whichever
        // applies. v1 is PK-only; future PRs extend this to UNIQUE /
        // FK / CHECK names.
        string expectedPkName = catalog.GetPrimaryKeyConstraintName(qualifiedTableName);

        if (string.Equals(alter.ConstraintName, expectedPkName, StringComparison.OrdinalIgnoreCase))
        {
            Schema schema = provider.GetSchema();
            if (schema.PrimaryKeyColumnIndices.Count == 0)
            {
                if (alter.IfExists) return EmptyQueryPlan.Instance;
                throw new InvalidOperationException(
                    $"constraint \"{alter.ConstraintName}\" of relation \"{alter.TableName}\" does not exist");
            }

            await provider.DisablePrimaryKeyAsync().ConfigureAwait(false);
            // Constraint no longer exists — clear the custom-name binding
            // so a subsequent ADD CONSTRAINT (when we ship it) starts from
            // a clean slate, and so the catalog file doesn't carry a stale
            // name for a non-existent constraint.
            // Only FlatFile tracks custom PK constraint names today.
            catalog.FlatFile.RemoveCustomPrimaryKeyConstraintName(catalog.ResolveDdlName(alter.SchemaName, alter.TableName));

            if (catalog.TryGetTable(qualifiedTableName, out ITableProvider? afterProvider))
            {
                catalog.Events.Raise(new TableAlteredEvent(qn, beforeSchema, afterProvider.GetSchema(), sourceText));
            }
            return EmptyQueryPlan.Instance;
        }

        // Name didn't match the PK convention. In v1 there are no other
        // droppable constraint names, so this is always "does not exist".
        if (alter.IfExists) return EmptyQueryPlan.Instance;
        throw new InvalidOperationException(
            $"constraint \"{alter.ConstraintName}\" of relation \"{alter.TableName}\" does not exist");
    }

    /// <summary>
    /// Applies <c>ALTER TABLE name ALTER COLUMN col DROP { IDENTITY | DEFAULT } [IF EXISTS]</c>.
    /// Validates that the column exists and (for DROP IDENTITY without
    /// IF EXISTS) that the attribute being dropped is actually present.
    /// </summary>
    public static async Task<IQueryPlan> AlterColumnDropAsync(
        TableCatalog catalog, AlterTableAlterColumnDropStatement alter, string? sourceText = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(alter);

        QualifiedName qn = catalog.ResolveDdlName(alter.SchemaName, alter.TableName);
        string qualifiedTableName = qn.ToString();
        if (!catalog.TryGetTable(qualifiedTableName, out ITableProvider? provider))
        {
            throw new InvalidOperationException(
                $"Table '{alter.TableName}' is not registered in the catalog.");
        }

        Schema schema = provider.GetSchema();
        int columnIndex = -1;
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            if (string.Equals(schema.Columns[i].Name, alter.ColumnName, StringComparison.OrdinalIgnoreCase))
            {
                columnIndex = i;
                break;
            }
        }
        if (columnIndex < 0)
        {
            throw new InvalidOperationException(
                $"column \"{alter.ColumnName}\" of relation \"{alter.TableName}\" does not exist");
        }

        ColumnInfo column = schema.Columns[columnIndex];

        switch (alter.Target)
        {
            case AlterColumnDropTarget.Identity:
                if (column.Identity is null)
                {
                    if (alter.IfExists) return EmptyQueryPlan.Instance;
                    throw new InvalidOperationException(
                        $"column \"{alter.ColumnName}\" of relation \"{alter.TableName}\" is not an IDENTITY column");
                }
                await provider.DropColumnIdentityAsync(columnIndex).ConfigureAwait(false);
                break;

            case AlterColumnDropTarget.Default:
                // PG treats DROP DEFAULT as idempotent — no error when the
                // column has no default. Match that behavior whether or
                // not IF EXISTS is supplied.
                await provider.DropColumnDefaultAsync(columnIndex).ConfigureAwait(false);
                break;

            default:
                throw new InvalidOperationException(
                    $"ALTER COLUMN DROP target {alter.Target} is not implemented.");
        }

        if (catalog.TryGetTable(qualifiedTableName, out ITableProvider? afterProvider))
        {
            catalog.Events.Raise(new TableAlteredEvent(qn, schema, afterProvider.GetSchema(), sourceText));
        }
        return EmptyQueryPlan.Instance;
    }

    /// <summary>
    /// Streams the table's historical rows through the new column's
    /// <see cref="ColumnInfo.ComputedExpression"/>, then dispatches a
    /// single page-COW <c>UpdateRows</c> call that installs the computed
    /// values in place of the NULL pump that <c>provider.AddColumn</c>
    /// just emitted.
    /// </summary>
    private static async Task BackfillComputedColumnAsync(TableCatalog catalog, string tableName, ColumnInfo column)
    {
        if (!catalog.TryGetTable(tableName, out ITableProvider? provider)) return;
        if (!provider.CanUpdateRows)
        {
            // Without an UpdateRows path we can't install values into
            // historical rows. Surface the gap explicitly so a user
            // doesn't silently get all-NULL historical values. Roll
            // back the just-added column too — leaving it half-added
            // would force the user to manually DROP before retrying.
            try { provider.DropColumn(column.Name); } catch { /* best-effort rollback */ }
            throw new InvalidOperationException(
                $"ALTER TABLE '{tableName}' ADD COLUMN '{column.Name}' AS (...): " +
                $"provider type '{provider.GetType().Name}' does not support UpdateRows, " +
                "so historical rows cannot be backfilled with the computed expression.");
        }

        // Wrap the backfill in a try/catch — if any per-row evaluation
        // or coercion throws, the column is already committed (the
        // writer's AddColumn finalised its tail flip before we got
        // here). Drop the half-added column so the table returns to
        // its pre-ALTER state; the user-facing error then mirrors what
        // the same failure would look like at INSERT time.
        try
        {
            await BackfillComputedColumnAsync(catalog, provider, column).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort rollback. DropColumn shouldn't fail on a
            // freshly-added column (PK rejection doesn't apply; the
            // column was just nulled), but if it does we still want to
            // surface the original backfill exception, not the
            // rollback failure.
            try { provider.DropColumn(column.Name); } catch { /* swallow */ }
            throw;
        }
    }

    private static async Task BackfillComputedColumnAsync(TableCatalog catalog, ITableProvider provider, ColumnInfo column)
    {
        Schema schema = provider.GetSchema();
        int newColIdx = -1;
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            if (string.Equals(schema.Columns[i].Name, column.Name, StringComparison.OrdinalIgnoreCase))
            {
                newColIdx = i;
                break;
            }
        }
        if (newColIdx < 0) return;

        using Arena workArena = new();
        ExpressionEvaluator evaluator = new(catalog.Functions, sidecarRegistry: catalog.SidecarRegistry);
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
                Arena scanArena = batch.Arena;
                for (int r = 0; r < batch.Count; r++, liveRowIndex++)
                {
                    Row row = batch[r];
                    EvaluationFrame frame = new(
                        row,
                        scanArena,
                        workArena,
                        outerRow: null,
                        sidecarRegistry: catalog.SidecarRegistry,
                        types: null);
                    ValueRef result = await evaluator.EvaluateAsValueRefAsync(
                        column.ComputedExpression!, frame, CancellationToken.None).ConfigureAwait(false);
                    DataValue computed = ComputedColumnEvaluator.ConvertValueRefToTarget(
                        result, column, workArena, column.Name);

                    // Skip rows whose computed value is NULL — the column's
                    // pages already hold NULL after AddColumn's pump, so an
                    // UpdateRows request would be a no-op. Keeps the batch
                    // tight when an expression like `nullable_col + 1`
                    // produces NULL for many rows.
                    if (computed.IsNull) continue;

                    requests.Add(new RowUpdateRequest(
                        liveRowIndex,
                        new Dictionary<int, DataValue> { [newColIdx] = computed }));
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
}
