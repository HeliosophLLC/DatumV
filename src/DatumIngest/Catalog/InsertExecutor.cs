using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Catalog;

/// <summary>
/// Owns the <c>INSERT</c>-statement pipeline for <see cref="TableCatalog.Plan(Statement)"/>:
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
/// <see cref="IAppendSession"/>. VALUES still rejects non-literal
/// expressions; users who need expressions can write
/// <c>INSERT INTO t SELECT 1 + 2, 'foo'</c>.
/// </remarks>
internal static class InsertExecutor
{
    /// <summary>
    /// Entry point used by <see cref="TableCatalog.Plan(Statement)"/>.
    /// Runs synchronously: VALUES batches are small enough to materialise
    /// in one shot, and the underlying <see cref="ITableProvider.AppendRowsAsync"/>
    /// is awaited via <c>GetAwaiter().GetResult()</c> so the dispatch
    /// stays consistent with the rest of <c>Plan()</c>'s sync DDL flow.
    /// </summary>
    public static void Execute(TableCatalog catalog, InsertStatement insert)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(insert);

        if (!catalog.TryGetTable(insert.TableName, out ITableProvider? provider))
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

        switch (insert.Source)
        {
            case InsertValuesSource values:
                ApplyValues(catalog.Pool, provider, targetSchema, insert.ColumnNames, values);
                break;

            case InsertQuerySource queryRow:
                ApplySelect(catalog, provider, targetSchema, insert.ColumnNames, queryRow);
                break;

            default:
                throw new NotSupportedException(
                    $"Unrecognised INSERT source: {insert.Source.GetType().Name}.");
        }
    }

    private static void ApplyValues(
        Pool pool,
        ITableProvider provider,
        Schema targetSchema,
        IReadOnlyList<string>? columnList,
        InsertValuesSource values) =>
        ApplyValuesAsync(pool, provider, targetSchema, columnList, values).GetAwaiter().GetResult();

    private static async Task ApplyValuesAsync(
        Pool pool,
        ITableProvider provider,
        Schema targetSchema,
        IReadOnlyList<string>? columnList,
        InsertValuesSource values)
    {
        if (values.Rows.Count == 0)
        {
            // Nothing to insert. Don't open a session — keeps the
            // semantics simple ("INSERT … VALUES with zero rows is a
            // no-op") and avoids a noisy empty commit.
            return;
        }

        // Source-column count is the column list size if provided, else
        // the full schema arity. Per-row arity is validated below — the
        // common case is that every VALUES row matches.
        int sourceColumnCount = columnList?.Count ?? targetSchema.Columns.Count;
        ColumnPlan plan = ResolveColumnPlan(targetSchema, columnList, sourceColumnCount);

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
        RowBatch batch = pool.RentRowBatch(lookup, capacity: values.Rows.Count, arena: arena);

        for (int rowIndex = 0; rowIndex < values.Rows.Count; rowIndex++)
        {
            IReadOnlyList<Expression> sourceRow = values.Rows[rowIndex];
            if (sourceRow.Count != plan.SourceColumnCount)
            {
                throw new InvalidOperationException(
                    $"INSERT INTO '{provider.Name}': VALUES row {rowIndex + 1} has " +
                    $"{sourceRow.Count} value(s), but the column list expects {plan.SourceColumnCount}.");
            }

            DataValue[] targetRow = pool.RentDataValues(targetSchema.Columns.Count);
            for (int targetIndex = 0; targetIndex < targetSchema.Columns.Count; targetIndex++)
            {
                ColumnInfo target = targetSchema.Columns[targetIndex];
                int sourceIndex = plan.SourceIndexForTarget[targetIndex];

                if (sourceIndex >= 0)
                {
                    object? literal = ExtractLiteral(sourceRow[sourceIndex], target.Name);
                    targetRow[targetIndex] = LiteralCoercion.Coerce(literal, target, arena, target.Name);
                }
                else
                {
                    // Omitted column: pre-resolved fill (Default / Null /
                    // Identity). Rebuild the DataValue per row so each
                    // row owns its own arena-backed copy of strings /
                    // byte arrays.
                    targetRow[targetIndex] = ResolveOmittedFill(plan, targetIndex, target, arena, session);
                }
            }

            pkChecker?.EnsureUnique(targetRow, targetSchema.Columns);
            batch.Add(targetRow);
        }

        await session.WriteAsync(batch).ConfigureAwait(false);
        await session.CommitAsync().ConfigureAwait(false);
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
    private static void ApplySelect(
        TableCatalog catalog,
        ITableProvider provider,
        Schema targetSchema,
        IReadOnlyList<string>? columnList,
        InsertQuerySource source)
    {
        ApplySelectAsync(catalog, provider, targetSchema, columnList, source)
            .GetAwaiter().GetResult();
    }

    private static async Task ApplySelectAsync(
        TableCatalog catalog,
        ITableProvider provider,
        Schema targetSchema,
        IReadOnlyList<string>? columnList,
        InsertQuerySource source)
    {
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

                for (int r = 0; r < sourceBatch.Count; r++)
                {
                    Row sourceRow = sourceBatch[r];
                    DataValue[] targetRow = catalog.Pool.RentDataValues(targetSchema.Columns.Count);

                    for (int targetIndex = 0; targetIndex < targetSchema.Columns.Count; targetIndex++)
                    {
                        ColumnInfo target = targetSchema.Columns[targetIndex];
                        int sourceIndex = plan.SourceIndexForTarget[targetIndex];

                        targetRow[targetIndex] = sourceIndex >= 0
                            ? ConvertSourceValue(sourceRow[sourceIndex], sourceStore, target, targetArena, target.Name)
                            : ResolveOmittedFill(plan, targetIndex, target, targetArena, session!);
                    }

                    pkChecker?.EnsureUnique(targetRow, targetSchema.Columns);
                    targetBatch.Add(targetRow);
                }

                await session!.WriteAsync(targetBatch).ConfigureAwait(false);
            }

            if (session is not null)
            {
                await session.CommitAsync().ConfigureAwait(false);
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
        }
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
        ColumnInfo target,
        Arena targetArena,
        string columnName)
    {
        if (source.IsNull)
        {
            return LiteralCoercion.Coerce(null, target, targetArena, columnName);
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
            _ => throw new InvalidOperationException(
                $"INSERT … SELECT for target column '{columnName}': source kind " +
                $"{source.Kind} is not yet supported. Composite kinds (Struct, " +
                "typed arrays, Image / Audio / ByteArray) will land in a later PR."),
        };

        return LiteralCoercion.Coerce(scalar, target, targetArena, columnName);
    }

    /// <summary>
    /// Resolves how each target schema column gets its value: either an
    /// index into the source row (VALUES tuple or SELECT projection),
    /// or a default-fill plan (<see cref="OmittedFill.Default"/>) /
    /// null-fill plan (<see cref="OmittedFill.Null"/>). Rejects every
    /// shape that can't produce a value (omitted column with no
    /// <c>DEFAULT</c> on a non-nullable target). Shared between
    /// <see cref="ApplyValues"/> and <see cref="ApplySelect"/> — the
    /// only difference is whether <paramref name="sourceColumnCount"/>
    /// comes from the column list / VALUES tuple width or from the
    /// source query's projection arity (read off the first batch's
    /// <see cref="ColumnLookup"/>).
    /// </summary>
    private static ColumnPlan ResolveColumnPlan(
        Schema targetSchema,
        IReadOnlyList<string>? columnList,
        int sourceColumnCount)
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
            for (int i = 0; i < targetSchema.Columns.Count; i++)
            {
                if (targetSchema.Columns[i].Identity is not null)
                {
                    throw new InvalidOperationException(
                        $"INSERT into '{targetSchema.Columns[i].Name}': cannot supply an explicit " +
                        "value for an IDENTITY column. Use an explicit column list that excludes " +
                        $"'{targetSchema.Columns[i].Name}', or omit the IDENTITY column declaration.");
                }
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
                if (targetSchema.Columns[targetIdx].Identity is not null)
                {
                    throw new InvalidOperationException(
                        $"INSERT into '{name}': cannot supply an explicit value for an IDENTITY " +
                        "column. Drop it from the INSERT column list and the catalog will fill it " +
                        "automatically.");
                }
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
            if (sourceIndexForTarget[i] >= 0)
            {
                omittedFills[i] = OmittedFill.None;
                continue;
            }

            ColumnInfo target = targetSchema.Columns[i];

            if (target.Identity is not null)
            {
                omittedFills[i] = OmittedFill.Identity;
                continue;
            }

            if (target.DefaultExpression is not null)
            {
                // PR10b validates DEFAULT is a literal at CREATE TABLE
                // time, so the cast here is structural — surface a
                // descriptive error if a future code path slipped a
                // non-literal through.
                object? value = ExtractLiteral(target.DefaultExpression, target.Name);
                omittedFills[i] = OmittedFill.Default(value);
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

    private static DataValue ResolveOmittedFill(
        ColumnPlan plan, int targetIndex, ColumnInfo target, Arena arena, IAppendSession session)
    {
        OmittedFill fill = plan.OmittedFills[targetIndex];
        return fill.Kind switch
        {
            OmittedFill.FillKind.Null => DataValue.Null(target.Kind),
            OmittedFill.FillKind.Default
                => LiteralCoercion.Coerce(fill.LiteralValue, target, arena, target.Name),
            OmittedFill.FillKind.Identity
                => LiteralCoercion.Coerce(session.ReserveNextIdentityValue(), target, arena, target.Name),
            _ => throw new InvalidOperationException(
                $"Internal error: column '{target.Name}' has no source index and no fill."),
        };
    }

    /// <summary>
    /// Extracts the CLR value carried by a <see cref="LiteralExpression"/>,
    /// flattening <c>UnaryExpression(Negate, numeric literal)</c> into a
    /// negative literal. Mirrors <see cref="TableCatalog.IsAcceptedDefaultLiteral"/>
    /// so VALUES accepts the same shapes as <c>DEFAULT</c>.
    /// </summary>
    private static object? ExtractLiteral(Expression expression, string columnName)
    {
        switch (expression)
        {
            case LiteralExpression literal:
                return literal.Value;

            case UnaryExpression { Operator: UnaryOperator.Negate, Operand: LiteralExpression numeric }:
                return Negate(numeric.Value, columnName);

            default:
                throw new InvalidOperationException(
                    $"INSERT VALUES for column '{columnName}': only literal expressions are " +
                    "supported in PR10c. Use INSERT … SELECT for computed values (PR10c').");
        }
    }

    private static object? Negate(object? value, string columnName) =>
        value switch
        {
            sbyte s => (object)checked((sbyte)-s),
            short s => (object)checked((short)-s),
            int i => (object)checked(-i),
            long l => (object)checked(-l),
            float f => -f,
            double d => -d,
            decimal m => -m,
            Half h => (Half)(-(double)h),
            null => throw new InvalidOperationException(
                $"INSERT VALUES for column '{columnName}': cannot negate NULL."),
            _ => throw new InvalidOperationException(
                $"INSERT VALUES for column '{columnName}': cannot negate {value.GetType().Name}."),
        };

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
    /// Walks the target table's existing rows once, captures the live
    /// PRIMARY KEY values, and rejects per-row INSERT candidates whose
    /// PK either collides with an existing row, collides with another
    /// row in the same batch, or contains a NULL in any PK column.
    /// Lazily constructed only when the target schema declares a PK;
    /// no overhead on PK-less tables.
    /// </summary>
    private sealed class PrimaryKeyChecker
    {
        private readonly IReadOnlyList<int> _pkIndices;
        private readonly HashSet<string> _seenKeys;

        private PrimaryKeyChecker(IReadOnlyList<int> pkIndices, HashSet<string> seenKeys)
        {
            _pkIndices = pkIndices;
            _seenKeys = seenKeys;
        }

        /// <summary>
        /// Returns <see langword="null"/> when the target schema has no
        /// PRIMARY KEY; otherwise builds a checker pre-loaded with the
        /// existing row keys via a full scan.
        /// </summary>
        public static async Task<PrimaryKeyChecker?> CreateAsync(
            ITableProvider provider, Schema targetSchema)
        {
            IReadOnlyList<int> pkIndices = targetSchema.PrimaryKeyColumnIndices;
            if (pkIndices.Count == 0) return null;

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
            return new PrimaryKeyChecker(pkIndices, seen);
        }

        /// <summary>
        /// Validates a candidate target row: rejects NULLs in any PK
        /// column, rejects duplicate keys against existing rows, and
        /// rejects within-batch duplicates. Adds the key to the seen
        /// set on success so the next row in the same INSERT can
        /// detect a duplicate without a re-scan.
        /// </summary>
        public void EnsureUnique(DataValue[] targetRow, IReadOnlyList<ColumnInfo> columns)
        {
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
        public enum FillKind : byte { None, Null, Default, Identity }

        public FillKind Kind { get; }
        public object? LiteralValue { get; }

        private OmittedFill(FillKind kind, object? value)
        {
            Kind = kind;
            LiteralValue = value;
        }

        public static OmittedFill None => default;
        public static OmittedFill Null { get; } = new(FillKind.Null, null);
        public static OmittedFill Default(object? literalValue) => new(FillKind.Default, literalValue);
        public static OmittedFill Identity { get; } = new(FillKind.Identity, null);
    }
}

/// <summary>
/// Coerces a CLR literal value (as produced by
/// <c>SqlParser.NarrowNumericLiteral</c>: <see cref="sbyte"/> / <see cref="short"/> /
/// <see cref="int"/> / <see cref="long"/> for integers, <see cref="float"/> /
/// <see cref="double"/> for fractionals) into a <see cref="DataValue"/>
/// of a target <see cref="DataKind"/>. Lossless coercions are accepted;
/// lossy or cross-family coercions throw a descriptive
/// <see cref="InvalidOperationException"/>.
/// </summary>
internal static class LiteralCoercion
{
    public static DataValue Coerce(object? literal, ColumnInfo target, Arena arena, string columnName)
    {
        if (literal is null)
        {
            if (!target.Nullable)
            {
                throw new InvalidOperationException(
                    $"Column '{columnName}' is NOT NULL but the supplied value is NULL.");
            }
            return DataValue.Null(target.Kind);
        }

        // Typed-array columns aren't yet writable from a literal in
        // PR10c — there's no inline-array literal syntax wired to the
        // INSERT path. INSERT … SELECT (PR10c') is how array columns
        // get populated.
        if (target.IsArray)
        {
            throw new InvalidOperationException(
                $"INSERT VALUES for column '{columnName}': typed-array columns are not yet " +
                "writable from a literal. Use INSERT … SELECT (PR10c').");
        }

        return target.Kind switch
        {
            DataKind.Boolean => CoerceBoolean(literal, columnName),
            DataKind.Int8 => DataValue.FromInt8(ToSignedInRange<sbyte>(literal, sbyte.MinValue, sbyte.MaxValue, columnName, "Int8")),
            DataKind.Int16 => DataValue.FromInt16(ToSignedInRange<short>(literal, short.MinValue, short.MaxValue, columnName, "Int16")),
            DataKind.Int32 => DataValue.FromInt32(ToSignedInRange<int>(literal, int.MinValue, int.MaxValue, columnName, "Int32")),
            DataKind.Int64 => DataValue.FromInt64(ToInt64(literal, columnName)),
            DataKind.UInt8 => DataValue.FromUInt8(ToUnsignedInRange<byte>(literal, byte.MaxValue, columnName, "UInt8")),
            DataKind.UInt16 => DataValue.FromUInt16(ToUnsignedInRange<ushort>(literal, ushort.MaxValue, columnName, "UInt16")),
            DataKind.UInt32 => DataValue.FromUInt32(ToUnsignedInRange<uint>(literal, uint.MaxValue, columnName, "UInt32")),
            DataKind.UInt64 => DataValue.FromUInt64(ToUInt64(literal, columnName)),
            DataKind.Float32 => DataValue.FromFloat32(ToFloat32Lossless(literal, columnName)),
            DataKind.Float64 => DataValue.FromFloat64(ToFloat64(literal, columnName)),
            DataKind.String => CoerceString(literal, arena, columnName),
            DataKind.Uuid => CoerceUuid(literal, columnName),
            _ => throw new InvalidOperationException(
                $"INSERT VALUES for column '{columnName}': literal coercion to " +
                $"{target.Kind} is not yet supported."),
        };
    }

    private static DataValue CoerceBoolean(object literal, string columnName) =>
        literal switch
        {
            bool b => DataValue.FromBoolean(b),
            _ => throw IncompatibleLiteral(literal, "Boolean", columnName),
        };

    private static DataValue CoerceString(object literal, Arena arena, string columnName) =>
        literal switch
        {
            string s => DataValue.FromString(s, arena),
            _ => throw IncompatibleLiteral(literal, "String", columnName),
        };

    private static DataValue CoerceUuid(object literal, string columnName)
    {
        if (literal is Guid g) return DataValue.FromUuid(g);
        if (literal is string s && Guid.TryParse(s, out Guid parsed)) return DataValue.FromUuid(parsed);
        throw IncompatibleLiteral(literal, "Uuid", columnName);
    }

    private static long ToInt64(object literal, string columnName) =>
        literal switch
        {
            sbyte s => s,
            short s => s,
            int i => i,
            long l => l,
            byte b => b,
            ushort u => u,
            uint u => u,
            ulong u when u <= long.MaxValue => (long)u,
            _ => throw IncompatibleLiteral(literal, "Int64", columnName),
        };

    private static ulong ToUInt64(object literal, string columnName)
    {
        return literal switch
        {
            sbyte s when s >= 0 => (ulong)s,
            short s when s >= 0 => (ulong)s,
            int i when i >= 0 => (ulong)i,
            long l when l >= 0 => (ulong)l,
            byte b => b,
            ushort u => u,
            uint u => u,
            ulong u => u,
            _ when literal is sbyte or short or int or long
                => throw new InvalidOperationException(
                    $"Column '{columnName}': cannot store negative literal in UInt64."),
            _ => throw IncompatibleLiteral(literal, "UInt64", columnName),
        };
    }

    private static T ToSignedInRange<T>(object literal, long min, long max, string columnName, string targetName)
        where T : struct
    {
        long widened = literal switch
        {
            sbyte s => s,
            short s => s,
            int i => i,
            long l => l,
            byte b => b,
            ushort u => u,
            uint u => u,
            ulong u when u <= long.MaxValue => (long)u,
            _ => throw IncompatibleLiteral(literal, targetName, columnName),
        };

        if (widened < min || widened > max)
        {
            throw new InvalidOperationException(
                $"Column '{columnName}': literal {widened} does not fit in {targetName} " +
                $"(range [{min}, {max}]).");
        }
        return (T)Convert.ChangeType(widened, typeof(T));
    }

    private static T ToUnsignedInRange<T>(object literal, ulong max, string columnName, string targetName)
        where T : struct
    {
        ulong widened = literal switch
        {
            sbyte s when s >= 0 => (ulong)s,
            short s when s >= 0 => (ulong)s,
            int i when i >= 0 => (ulong)i,
            long l when l >= 0 => (ulong)l,
            byte b => b,
            ushort u => u,
            uint u => u,
            ulong u => u,
            _ when literal is sbyte or short or int or long
                => throw new InvalidOperationException(
                    $"Column '{columnName}': cannot store negative literal in {targetName}."),
            _ => throw IncompatibleLiteral(literal, targetName, columnName),
        };

        if (widened > max)
        {
            throw new InvalidOperationException(
                $"Column '{columnName}': literal {widened} does not fit in {targetName} " +
                $"(max {max}).");
        }
        return (T)Convert.ChangeType(widened, typeof(T));
    }

    private static float ToFloat32Lossless(object literal, string columnName)
    {
        switch (literal)
        {
            case float f: return f;
            case double d:
            {
                float candidate = (float)d;
                // Round-trip check rejects coercions that lose precision
                // (e.g. 0.1 → Float32 isn't exact). NaN compares unequal,
                // so handle it explicitly to keep the lossless path open
                // for NaN literals (they round-trip bit-for-bit).
                if (double.IsNaN(d)) return float.NaN;
                if ((double)candidate != d)
                {
                    throw new InvalidOperationException(
                        $"Column '{columnName}': Float64 literal {d} cannot be represented exactly in Float32.");
                }
                return candidate;
            }
            case decimal m: return (float)m;
            case sbyte s: return s;
            case short s: return s;
            case int i: return i;
            case long l: return l;
            case byte b: return b;
            case ushort u: return u;
            case uint u: return u;
            case ulong u: return u;
            default: throw IncompatibleLiteral(literal, "Float32", columnName);
        }
    }

    private static double ToFloat64(object literal, string columnName) =>
        literal switch
        {
            float f => f,
            double d => d,
            decimal m => (double)m,
            sbyte s => s,
            short s => s,
            int i => i,
            long l => l,
            byte b => b,
            ushort u => u,
            uint u => u,
            ulong u => u,
            _ => throw IncompatibleLiteral(literal, "Float64", columnName),
        };

    private static InvalidOperationException IncompatibleLiteral(object literal, string targetKind, string columnName) =>
        new($"Column '{columnName}': cannot coerce {literal.GetType().Name} literal to {targetKind}.");
}
