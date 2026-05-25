using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Catalog;

/// <summary>
/// Resolves a parsed list of <see cref="ColumnDefinition"/>s into a
/// validated <see cref="Schema"/>. Owns the validator cluster shared by
/// <c>CREATE TABLE</c> and <c>ALTER TABLE ADD COLUMN</c>: DEFAULT
/// expression shape + fit, IDENTITY shape, PRIMARY KEY column kinds, and
/// the no-computed-to-computed-reference rule.
/// </summary>
/// <remarks>
/// Lives alongside <see cref="SchemaResolver"/>, <see cref="ComputedColumnEvaluator"/>,
/// and <see cref="LiteralCoercion"/> — the validator/resolver/coercer
/// cluster that catalog DDL leans on. Executors compose against this so
/// the per-statement files stay focused on orchestration; TableCatalog
/// keeps the storage- and routing-shaped state.
/// </remarks>
internal static class ColumnDefinitionResolver
{
    /// <summary>
    /// Builds a validated <see cref="Schema"/> from the parsed column
    /// definitions. Used by <c>CREATE TABLE</c>. The
    /// <paramref name="catalog"/> supplies the <see cref="FunctionRegistry"/>
    /// for the DEFAULT-expression probe; no other instance state is read.
    /// </summary>
    public static async Task<Schema> BuildSchemaAsync(
        TableCatalog catalog,
        IReadOnlyList<ColumnDefinition> definitions,
        IReadOnlyList<string>? primaryKeyColumnNames,
        Heliosoph.DatumV.Execution.ExecutionContext context)
    {
        ColumnInfo[] columns = new ColumnInfo[definitions.Count];
        int identityColumnIndex = -1;

        // Resolve the PK column-name list (already deduplicated /
        // ordered by the parser — see CreateTableParser) into schema
        // indices, validating along the way.
        int[]? pkSchemaIndices = null;
        if (primaryKeyColumnNames is { Count: > 0 })
        {
            pkSchemaIndices = ResolvePrimaryKeyColumnIndices(definitions, primaryKeyColumnNames);
            ValidatePrimaryKeySize(definitions, pkSchemaIndices);
        }
        HashSet<int>? pkIndexSet = pkSchemaIndices is null ? null : new HashSet<int>(pkSchemaIndices);

        for (int i = 0; i < definitions.Count; i++)
        {
            ColumnDefinition d = definitions[i];
            if (!TypeAnnotationResolver.TryParse(
                    d.TypeName, types: null,
                    out DataKind kind, out bool isArray, out _,
                    out int? maxLength, out int[]? fixedShape, out bool isBlankPadded))
            {
                throw new InvalidOperationException(
                    $"Unknown column type '{d.TypeName}' on column '{d.Name}'. " +
                    "Use a DataKind name (Int32, String, Float64, Uuid, ...) optionally " +
                    "suffixed with [] for typed-array columns.");
            }

            // Multi-dim (ndim ≥ 2) arrays support fixed-width primitive element
            // kinds, byte arrays (UInt8), String, and Image. The remaining
            // reference / blob kinds (Struct, Audio, Video, Json, PointCloud,
            // Mesh) don't have multi-dim factories yet; reject at DDL time so
            // users see the error on CREATE TABLE rather than on the first INSERT.
            if (isArray && fixedShape is { Length: >= 2 } && IsMultiDimIncompatibleElementKind(kind))
            {
                throw new InvalidOperationException(
                    $"Column '{d.Name}': multi-dimensional shape (ndim={fixedShape.Length}) is " +
                    $"not supported for element kind {kind} in this version. Supported element " +
                    "kinds: Int*, UInt*, Float*, Decimal, Date, Time, Duration, Uuid, Point*, " +
                    $"Boolean, String, Image. Use a 1-D array (Array<{kind}>) or denormalize the row.");
            }

            Expression? defaultExpression = null;
            if (d.DefaultValue is not null)
            {
                ValidateDefaultExpression(d.DefaultValue, d.Name);
                await ValidateDefaultExpressionFitsColumnAsync(context, d.DefaultValue, d.Name, kind, isArray)
                    .ConfigureAwait(false);
                defaultExpression = d.DefaultValue;
            }

            IdentitySpec? identity = null;
            if (d.Identity is not null)
            {
                if (identityColumnIndex >= 0)
                {
                    throw new InvalidOperationException(
                        $"Table may have at most one IDENTITY column; both " +
                        $"'{definitions[identityColumnIndex].Name}' and '{d.Name}' carry IDENTITY.");
                }
                ValidateIdentitySpecForColumn(d.Identity, d.Name, kind, isArray);
                identity = d.Identity;
                identityColumnIndex = i;
            }

            // PK columns are implicitly NOT NULL — auto-promote so the
            // Nullable flag is consistent with the runtime check the
            // INSERT layer performs.
            bool isPrimaryKey = pkIndexSet is not null && pkIndexSet.Contains(i);
            bool effectiveNullable = isPrimaryKey ? false : d.Nullable;

            // Computed columns: `GENERATED ALWAYS AS (expr)`. Mutually
            // exclusive with DEFAULT and IDENTITY — the value is derived,
            // not supplied. PRIMARY KEY on a computed column is rejected
            // in v1 because the value depends on other columns and the PK
            // index would need re-keying on every UPDATE of a referenced
            // column; not worth the complexity until a real use case lands.
            if (d.ComputedExpression is not null)
            {
                if (defaultExpression is not null)
                {
                    throw new InvalidOperationException(
                        $"Column '{d.Name}': cannot combine DEFAULT and GENERATED ALWAYS AS — " +
                        "computed columns derive their value from other columns and never accept " +
                        "an explicit fallback.");
                }
                if (identity is not null)
                {
                    throw new InvalidOperationException(
                        $"Column '{d.Name}': cannot combine IDENTITY and GENERATED ALWAYS AS.");
                }
                if (isPrimaryKey)
                {
                    throw new InvalidOperationException(
                        $"Column '{d.Name}': GENERATED ALWAYS AS columns cannot be part of the " +
                        "PRIMARY KEY in v1.");
                }
            }

            columns[i] = new ColumnInfo(d.Name, kind, effectiveNullable)
            {
                IsArray = isArray,
                DefaultExpression = defaultExpression,
                Identity = identity,
                IsPrimaryKey = isPrimaryKey,
                ComputedExpression = d.ComputedExpression,
                MaxLength = maxLength,
                FixedShape = fixedShape,
                IsBlankPadded = isBlankPadded,
            };
        }

        // GENERATED expressions cannot reference other GENERATED columns —
        // the single-pass evaluator in InsertExecutor / UpdateExecutor
        // would see the referenced computed column still NULL and silently
        // produce a NULL result. Lift to topological-sort eval if a real
        // workload needs it.
        ValidateNoComputedToComputedReferences(columns);

        Schema schema = new(columns, pkSchemaIndices);

        // Drive ExpressionTypeResolver over every GENERATED ALWAYS AS body
        // so that arity / kind errors inside the expression (e.g. a typo'd
        // `brighten(image)` missing the `intensity` argument) surface here
        // instead of at the first INSERT/UPDATE — which could be far away
        // in time from the DDL that introduced the bug.
        foreach (ColumnInfo column in columns)
        {
            if (column.ComputedExpression is null) continue;
            ValidateComputedExpression(
                catalog.Functions, column.ComputedExpression, schema, column.Name);
        }

        return schema;
    }

    /// <summary>
    /// Validates a column's <c>DEFAULT</c> expression. Any tableless
    /// expression is accepted — literal, function call (<c>now()</c>,
    /// <c>gen_random_uuid()</c>), arithmetic, CASE, array literal, etc.
    /// The walker rejects shapes that need a source row or a query plan
    /// at evaluation time: <see cref="ColumnReference"/>,
    /// <see cref="SubqueryExpression"/> / <see cref="InSubqueryExpression"/> /
    /// <see cref="ExistsExpression"/>, and
    /// <see cref="WindowFunctionCallExpression"/>. INSERT-time evaluation
    /// uses an empty <see cref="EvaluationFrame"/> so those shapes would
    /// have nothing to resolve against.
    /// </summary>
    public static void ValidateDefaultExpression(Expression expression, string columnName)
    {
        Expression? offending = FindDisallowedDefaultNode(expression);
        if (offending is null) return;

        string offendingKind = offending switch
        {
            ColumnReference col => $"column reference '{(col.TableName is null ? col.ColumnName : col.TableName + "." + col.ColumnName)}'",
            SubqueryExpression => "scalar subquery",
            InSubqueryExpression => "IN-subquery",
            ExistsExpression => "EXISTS subquery",
            WindowFunctionCallExpression => "window function",
            _ => offending.GetType().Name,
        };

        throw new InvalidOperationException(
            $"DEFAULT for column '{columnName}': {offendingKind} is not allowed. " +
            "DEFAULT expressions evaluate with no source row in scope; use a literal, " +
            "a function call (e.g. now(), gen_random_uuid()), or any other tableless " +
            "expression instead.");
    }

    /// <summary>
    /// Eagerly evaluates the <c>DEFAULT</c> expression against an empty
    /// frame at <c>CREATE TABLE</c> / <c>ALTER TABLE ADD COLUMN</c> time
    /// and coerces the result to the column's <see cref="DataKind"/>. If
    /// the coercion fails — type mismatch, out-of-range literal, etc. —
    /// the error surfaces here instead of at the first <c>INSERT</c>'s
    /// per-row evaluation. Side effects from the probe are discarded
    /// (functions like <c>now()</c> / <c>uuidv4()</c> are pure scalars,
    /// so the throwaway value never escapes).
    /// </summary>
    public static async Task ValidateDefaultExpressionFitsColumnAsync(
        Execution.ExecutionContext context,
        Expression expression,
        string columnName,
        DataKind kind,
        bool isArray)
    {
        using Arena probeArena = new();
        using Execution.ExecutionContext probeContext = context.Derive(store: probeArena);
        ExpressionEvaluator evaluator = probeContext.CreateEvaluator();
        ColumnLookup emptyLookup = new(Array.Empty<string>());
        Row emptyRow = new(emptyLookup, Array.Empty<DataValue>());
        EvaluationFrame frame = probeContext.CreateFrame(emptyRow, probeArena);

        // Build a column-info shim with the target kind/nullable/array
        // shape so ConvertValueRefToTarget validates against the real
        // surface. Nullable=true keeps null-allowed; runtime per-row
        // evaluation handles NOT-NULL rejection separately.
        ColumnInfo probeTarget = new(columnName, kind, nullable: true) { IsArray = isArray };

        try
        {
            ValueRef result = await evaluator.EvaluateAsValueRefAsync(
                expression, frame, CancellationToken.None).ConfigureAwait(false);
            _ = ComputedColumnEvaluator.ConvertValueRefToTarget(
                result, probeTarget, probeArena, columnName);
        }
        catch (Exception inner)
            when (inner is InvalidOperationException
                  or NotSupportedException
                  or OverflowException
                  or FormatException
                  or ArgumentException)
        {
            throw new InvalidOperationException(
                $"DEFAULT for column '{columnName}' ({kind}{(isArray ? "[]" : "")}) is not " +
                $"compatible with the column type: {inner.Message}",
                inner);
        }
    }

    /// <summary>
    /// Walks the GENERATED ALWAYS AS expression with
    /// <see cref="ExpressionTypeResolver.ResolveType"/> against the table's
    /// schema. The resolver eagerly calls
    /// <see cref="IScalarFunction.ValidateArguments"/> at every function
    /// call site, so arity / kind mismatches inside the expression (e.g.
    /// <c>brighten(image)</c> missing the required <c>intensity</c>
    /// argument) throw here at <c>CREATE TABLE</c> / <c>ALTER TABLE ADD
    /// COLUMN</c> time. The result kind is intentionally discarded — the
    /// runtime per-row evaluator's
    /// <see cref="ComputedColumnEvaluator.ConvertValueRefToTarget"/> still
    /// handles coercion against the declared column kind, and DEFAULT
    /// expressions already cover the "result doesn't fit the column" case
    /// for the rowless probe path. This validator's job is the inner-call
    /// arity gate.
    /// </summary>
    public static void ValidateComputedExpression(
        FunctionRegistry functions, Expression expression, Schema sourceSchema, string columnName)
    {
        try
        {
            _ = ExpressionTypeResolver.ResolveType(expression, sourceSchema, functions);
        }
        catch (Exception inner)
            when (inner is InvalidOperationException
                  or NotSupportedException
                  or ArgumentException
                  or FunctionArgumentException)
        {
            throw new InvalidOperationException(
                $"GENERATED ALWAYS AS for column '{columnName}': {inner.Message}",
                inner);
        }
    }

    /// <summary>
    /// Shared validation for an <see cref="IdentitySpec"/> attached to a
    /// column at <c>CREATE TABLE</c> or <c>ALTER TABLE ADD COLUMN</c>
    /// time. Enforces: integer column kind in the 8/16/32/64-bit range
    /// (Int8…Int64, UInt8…UInt64), non-array, non-zero step, and
    /// seed/step that fit the kind's range. The single-IDENTITY-per-table
    /// check lives at the caller because the "existing IDENTITY" set is
    /// caller-specific (definitions vs. live schema).
    /// </summary>
    public static void ValidateIdentitySpecForColumn(
        IdentitySpec identity, string columnName, DataKind kind, bool isArray)
    {
        if (isArray)
        {
            throw new InvalidOperationException(
                $"Column '{columnName}': IDENTITY is not supported on typed-array columns.");
        }
        if (!DataValueComparer.IsIntegerKind(kind) ||
            kind is DataKind.Int128 or DataKind.UInt128)
        {
            // Int128 / UInt128 are integer kinds per the comparer but
            // don't fit in the prologue's int64 seed/step storage;
            // reject them explicitly so the error names the actual
            // constraint.
            throw new InvalidOperationException(
                $"Column '{columnName}': IDENTITY requires a 8/16/32/64-bit integer column kind " +
                "(Int8/Int16/Int32/Int64 or UInt8/UInt16/UInt32/UInt64); got " + kind + ".");
        }
        if (identity.Step == 0)
        {
            throw new InvalidOperationException(
                $"Column '{columnName}': IDENTITY step must be non-zero.");
        }
        ValidateIdentityValueFitsInKind(kind, identity.Seed, columnName, "seed");
        ValidateIdentityValueFitsInKind(kind, identity.Step, columnName, "step");
    }

    /// <summary>
    /// Rejects schemas where a <c>GENERATED ALWAYS AS</c> expression
    /// references another <c>GENERATED</c> column. Without this gate the
    /// single-pass evaluator silently fills the dependent column with
    /// NULL (the referenced column hasn't been computed yet when the
    /// dependent's expression runs). Users get a clear error at
    /// <c>CREATE TABLE</c> / <c>ALTER TABLE ADD COLUMN</c> time and can
    /// inline the inner expression.
    /// </summary>
    private static void ValidateNoComputedToComputedReferences(IReadOnlyList<ColumnInfo> columns)
    {
        Dictionary<string, ColumnInfo> byName = new(StringComparer.OrdinalIgnoreCase);
        foreach (ColumnInfo c in columns)
        {
            byName[c.Name] = c;
        }

        foreach (ColumnInfo c in columns)
        {
            if (c.ComputedExpression is null) continue;
            HashSet<(string? TableName, string ColumnName)> refs =
                ColumnReferenceCollector.Collect(c.ComputedExpression);
            foreach ((string? _, string refName) in refs)
            {
                if (byName.TryGetValue(refName, out ColumnInfo? referenced) &&
                    referenced.ComputedExpression is not null &&
                    !ReferenceEquals(referenced, c))
                {
                    throw new InvalidOperationException(
                        $"Column '{c.Name}': GENERATED expressions cannot reference other " +
                        $"GENERATED columns (references '{referenced.Name}'). Inline the inner " +
                        "expression instead.");
                }
            }
        }
    }

    /// <summary>
    /// Resolves PK column names (in user-declared order) to schema
    /// indices. Rejects unknown column names and duplicates that
    /// somehow slipped past the parser's PK-list validation.
    /// </summary>
    private static int[] ResolvePrimaryKeyColumnIndices(
        IReadOnlyList<ColumnDefinition> definitions,
        IReadOnlyList<string> primaryKeyColumnNames)
    {
        int[] indices = new int[primaryKeyColumnNames.Count];
        HashSet<int> seen = new();
        for (int p = 0; p < primaryKeyColumnNames.Count; p++)
        {
            string name = primaryKeyColumnNames[p];
            int found = -1;
            for (int i = 0; i < definitions.Count; i++)
            {
                if (string.Equals(definitions[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    found = i;
                    break;
                }
            }
            if (found < 0)
            {
                throw new InvalidOperationException(
                    $"PRIMARY KEY references column '{name}' which is not declared in the table.");
            }
            if (!seen.Add(found))
            {
                throw new InvalidOperationException(
                    $"PRIMARY KEY column '{name}' appears more than once.");
            }
            indices[p] = found;
        }
        return indices;
    }

    /// <summary>
    /// Validates that every PRIMARY KEY column is of a kind the
    /// <c>CompositeKeyEncoder</c> can encode. Rejects array / struct /
    /// blob / decimal / geometric kinds — those are either deferred
    /// (Decimal, Point2D/Point3D) or fundamentally unsuitable for B+Tree
    /// indexing (arrays, structs, large blobs).
    /// </summary>
    /// <remarks>
    /// Single-column PKs use the typed B+Tree (which natively handles
    /// the kind), composite PKs use the bytes-keyed B+Tree fed by
    /// <c>CompositeKeyEncoder</c>. Both paths reject the same unsupported
    /// kinds — this validator catches them at <c>CREATE TABLE</c> time
    /// instead of letting the user discover the gap at the first <c>INSERT</c>.
    /// </remarks>
    private static void ValidatePrimaryKeySize(
        IReadOnlyList<ColumnDefinition> definitions,
        IReadOnlyList<int> pkSchemaIndices)
    {
        foreach (int idx in pkSchemaIndices)
        {
            ColumnDefinition d = definitions[idx];
            if (!TypeAnnotationResolver.TryParse(d.TypeName, out DataKind kind, out bool isArray))
            {
                // Type-parse error will be surfaced by the main loop;
                // skip the size check here so the user sees the more
                // specific error.
                continue;
            }
            else if (isArray)
            {
                throw new InvalidOperationException(
                    $"PRIMARY KEY column '{d.Name}' is an array (kind {kind}[]); array kinds " +
                    "are not supported in PRIMARY KEY columns. Consider an inverted index for " +
                    "array contents or a hash projection for unique constraints.");
            }
            else if (!IsAcceptedPrimaryKeyKind(kind))
            {
                throw new InvalidOperationException(
                    $"PRIMARY KEY column '{d.Name}' has unsupported kind {kind}. Supported PK " +
                    "kinds: Boolean, Int8–Int128, UInt8–UInt128, Float16/32/64, Date, Time, " +
                    "DateTime, Duration, Uuid, String. Decimal and geometric kinds are deferred.");
            }
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when the kind is supported as a
    /// PRIMARY KEY component. Matches the kinds <c>CompositeKeyEncoder</c>
    /// can encode (used by composite PKs) and that the typed B+Tree
    /// can store inline (used by single-column PKs).
    /// </summary>
    private static bool IsAcceptedPrimaryKeyKind(DataKind kind) =>
        kind is DataKind.Boolean
            or DataKind.Int8 or DataKind.Int16 or DataKind.Int32 or DataKind.Int64 or DataKind.Int128
            or DataKind.UInt8 or DataKind.UInt16 or DataKind.UInt32 or DataKind.UInt64 or DataKind.UInt128
            or DataKind.Float16 or DataKind.Float32 or DataKind.Float64
            or DataKind.Date or DataKind.Time or DataKind.Timestamp or DataKind.TimestampTz or DataKind.Duration
            or DataKind.Uuid
            or DataKind.String;

    private static void ValidateIdentityValueFitsInKind(DataKind kind, long value, string columnName, string label)
    {
        bool fits = kind switch
        {
            DataKind.Int8 => value is >= sbyte.MinValue and <= sbyte.MaxValue,
            DataKind.Int16 => value is >= short.MinValue and <= short.MaxValue,
            DataKind.Int32 => value is >= int.MinValue and <= int.MaxValue,
            DataKind.Int64 => true,
            DataKind.UInt8 => value is >= 0 and <= byte.MaxValue,
            DataKind.UInt16 => value is >= 0 and <= ushort.MaxValue,
            DataKind.UInt32 => value is >= 0 and <= uint.MaxValue,
            DataKind.UInt64 => value >= 0,
            _ => false,
        };
        if (!fits)
        {
            throw new InvalidOperationException(
                $"Column '{columnName}': IDENTITY {label} {value} does not fit in {kind}.");
        }
    }

    private static Expression? FindDisallowedDefaultNode(Expression expression)
    {
        switch (expression)
        {
            case ColumnReference:
            case SubqueryExpression:
            case InSubqueryExpression:
            case ExistsExpression:
            case WindowFunctionCallExpression:
                return expression;

            case BinaryExpression binary:
                return FindDisallowedDefaultNode(binary.Left) ?? FindDisallowedDefaultNode(binary.Right);

            case UnaryExpression unary:
                return FindDisallowedDefaultNode(unary.Operand);

            case LikeExpression like:
                return FindDisallowedDefaultNode(like.Expression)
                    ?? FindDisallowedDefaultNode(like.Pattern)
                    ?? FindDisallowedDefaultNode(like.EscapeCharacter);

            case FunctionCallExpression function:
                foreach (Expression arg in function.Arguments)
                {
                    Expression? offending = FindDisallowedDefaultNode(arg);
                    if (offending is not null) return offending;
                }
                return null;

            case InExpression inExpr:
            {
                Expression? offending = FindDisallowedDefaultNode(inExpr.Expression);
                if (offending is not null) return offending;
                foreach (Expression v in inExpr.Values)
                {
                    offending = FindDisallowedDefaultNode(v);
                    if (offending is not null) return offending;
                }
                return null;
            }

            case BetweenExpression between:
                return FindDisallowedDefaultNode(between.Expression)
                    ?? FindDisallowedDefaultNode(between.Low)
                    ?? FindDisallowedDefaultNode(between.High);

            case IsNullExpression isNull:
                return FindDisallowedDefaultNode(isNull.Expression);

            case CastExpression cast:
                return FindDisallowedDefaultNode(cast.Expression);

            case CaseExpression caseExpr:
            {
                if (caseExpr.Operand is not null)
                {
                    Expression? offending = FindDisallowedDefaultNode(caseExpr.Operand);
                    if (offending is not null) return offending;
                }
                foreach (WhenClause when in caseExpr.WhenClauses)
                {
                    Expression? offending = FindDisallowedDefaultNode(when.Condition)
                        ?? FindDisallowedDefaultNode(when.Result);
                    if (offending is not null) return offending;
                }
                if (caseExpr.ElseResult is not null)
                {
                    return FindDisallowedDefaultNode(caseExpr.ElseResult);
                }
                return null;
            }

            case StructLiteralExpression structLit:
                foreach (StructField field in structLit.Fields)
                {
                    Expression? offending = FindDisallowedDefaultNode(field.Value);
                    if (offending is not null) return offending;
                }
                return null;

            case IndexAccessExpression indexAccess:
            {
                Expression? bad = FindDisallowedDefaultNode(indexAccess.Source);
                if (bad is not null) return bad;
                foreach (Expression i in indexAccess.Indices)
                {
                    bad = FindDisallowedDefaultNode(i);
                    if (bad is not null) return bad;
                }
                return null;
            }

            // Leaf shapes that need no source row: literals, type literals,
            // parameter binders (resolved at INSERT time via the parameter
            // dictionary), current-timestamp, error markers, lambdas
            // (closures over no outer row). All accepted.
            default:
                return null;
        }
    }

    /// <summary>
    /// True when <paramref name="kind"/> has no multi-dim factory yet — the
    /// reference / blob kinds without a per-kind multi-dim factory. Mirrors
    /// <c>DataValue.RejectReferenceElementKind</c>; we duplicate the list here
    /// to surface the error at DDL time rather than first INSERT.
    /// <see cref="DataKind.String"/> and <see cref="DataKind.UInt8"/> are
    /// supported (String via <see cref="DataValue.FromArenaMultiDimStringArray"/>;
    /// UInt8 via the fixed-width factory with shape-prefix-aware accessors) and
    /// are not on this list.
    /// </summary>
    private static bool IsMultiDimIncompatibleElementKind(DataKind kind) =>
        kind is DataKind.Struct
              or DataKind.Audio
              or DataKind.Video
              or DataKind.Json
              or DataKind.PointCloud
              or DataKind.Mesh;
}
