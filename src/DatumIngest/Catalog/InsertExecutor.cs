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
/// PR10c covers <c>INSERT … VALUES (…)</c> with literal expressions only
/// (matching <c>DEFAULT</c>'s literal-only restriction from PR10b).
/// <c>INSERT … SELECT</c> is rejected with a <see cref="NotSupportedException"/>
/// pending PR10c'; that PR will reuse <see cref="ResolveColumnPlan"/>
/// and <see cref="LiteralCoercion.Coerce"/> on each source batch.
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

            case InsertQuerySource:
                throw new NotSupportedException(
                    $"INSERT INTO '{insert.TableName}' SELECT … is not yet supported. " +
                    "PR10c ships INSERT … VALUES; INSERT … SELECT lands in the next pass.");

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
        InsertValuesSource values)
    {
        if (values.Rows.Count == 0)
        {
            // Nothing to insert. Don't open a session — keeps the
            // semantics simple ("INSERT … VALUES with zero rows is a
            // no-op") and avoids a noisy empty commit.
            return;
        }

        ColumnPlan plan = ResolveColumnPlan(targetSchema, columnList, values);

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
                    // Omitted column: pre-resolved fill from default
                    // (rebuild the DataValue per row so each row owns its
                    // own arena-backed copy of strings/byte arrays — the
                    // underlying expression is still literal so this is
                    // cheap and avoids share-arena hazards).
                    targetRow[targetIndex] = ResolveOmittedFill(plan, targetIndex, target, arena);
                }
            }

            batch.Add(targetRow);
        }

        // Single-batch commit. AppendRowsAsync wraps BeginAppend and
        // commits on completion; aborts on exception via the session's
        // dispose-without-commit semantics.
        provider.AppendRowsAsync(YieldOnce(batch), CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Resolves how each target schema column gets its value: either an
    /// index into the source <c>VALUES</c> row, or a default-fill plan
    /// (<see cref="OmittedFill.Default"/>) / null-fill plan
    /// (<see cref="OmittedFill.Null"/>). Rejects every shape that can't
    /// produce a value (omitted column with no <c>DEFAULT</c> on a
    /// non-nullable target).
    /// </summary>
    private static ColumnPlan ResolveColumnPlan(
        Schema targetSchema,
        IReadOnlyList<string>? columnList,
        InsertValuesSource values)
    {
        int sourceColumnCount = columnList?.Count ?? targetSchema.Columns.Count;

        // Validate every VALUES row has the same arity. Done here so
        // the per-row loop can assume consistent column counts.
        for (int i = 0; i < values.Rows.Count; i++)
        {
            if (values.Rows[i].Count != sourceColumnCount)
            {
                throw new InvalidOperationException(
                    $"INSERT VALUES row {i + 1} has {values.Rows[i].Count} value(s), " +
                    $"but the {(columnList is null ? "table schema" : "column list")} expects {sourceColumnCount}.");
            }
        }

        // SourceIndexForTarget[i] = index into the source VALUES row
        // that supplies column i, or -1 if column i is omitted.
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
                sourceIndexForTarget[targetIdx] = sourceIdx;
            }
        }

        // For every omitted target column, decide the fill: DEFAULT
        // expression (must be a LiteralExpression — PR10b's validation
        // already enforced this at CREATE TABLE time) → cached object,
        // else NULL (column must be Nullable), else throw.
        OmittedFill[] omittedFills = new OmittedFill[targetSchema.Columns.Count];
        for (int i = 0; i < targetSchema.Columns.Count; i++)
        {
            if (sourceIndexForTarget[i] >= 0)
            {
                omittedFills[i] = OmittedFill.None;
                continue;
            }

            ColumnInfo target = targetSchema.Columns[i];
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
        ColumnPlan plan, int targetIndex, ColumnInfo target, Arena arena)
    {
        OmittedFill fill = plan.OmittedFills[targetIndex];
        return fill.Kind switch
        {
            OmittedFill.FillKind.Null => DataValue.Null(target.Kind),
            OmittedFill.FillKind.Default
                => LiteralCoercion.Coerce(fill.LiteralValue, target, arena, target.Name),
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

    private static async IAsyncEnumerable<RowBatch> YieldOnce(RowBatch batch)
    {
        yield return batch;
        await Task.CompletedTask;
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
    /// Per-target-column fill descriptor for columns omitted from the
    /// <c>INSERT</c>'s column list. <see cref="FillKind.None"/> means
    /// the column has a source index and is filled directly; the other
    /// kinds describe the omission's resolution.
    /// </summary>
    private readonly struct OmittedFill
    {
        public enum FillKind : byte { None, Null, Default }

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
