namespace Heliosoph.DatumV.LanguageServer;

using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;

/// <summary>
/// Resolves <c>WITH</c>-clause CTE projections to a name-and-kind list the
/// language server can offer through hover and completion. Walks the parsed
/// AST, builds each CTE's output schema by looking at its SELECT list, and
/// chains lookups so a later CTE can reference an earlier one's columns.
/// </summary>
/// <remarks>
/// <para>
/// Resolution is best-effort: expressions whose kind can't be derived from a
/// bare column reference surface with <c>Kind = "?"</c>. Catching every
/// arithmetic / function shape would require a full type resolver — the
/// engine's <c>ExpressionTypeResolver</c> isn't reachable from the
/// LanguageServer assembly graph today. The placeholder keeps the user aware
/// of the column's existence without lying about its type.
/// </para>
/// <para>
/// Scope rules mirror what the engine does at planning time:
/// <list type="bullet">
///   <item>CTEs resolve in declaration order; each one sees the schemas of
///   all earlier CTEs, plus persistent tables and any TVF source visible in
///   its own FROM / JOIN clauses.</item>
///   <item>Recursive CTEs use the anchor side's schema only — the recursive
///   member references the CTE itself, which doesn't have a schema yet when
///   we're resolving it.</item>
///   <item>An explicit column list (<c>WITH foo (a, b) AS (...)</c>)
///   overrides the inner SELECT's derived names; kinds still come from the
///   inner SELECT.</item>
/// </list>
/// </para>
/// </remarks>
internal static class DerivedTableSchemaResolver
{
    /// <summary>
    /// Empty result returned when parsing fails or no CTEs are present.
    /// Kept as a singleton to avoid allocating a new empty dictionary on
    /// every classify call.
    /// </summary>
    public static readonly DerivedTableSchemaResult Empty = new(
        new Dictionary<string, IReadOnlyList<TableColumnEntry>>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Parses <paramref name="sql"/> and resolves every CTE's projected
    /// columns. Returns <see cref="Empty"/> on parse failure (the recovering
    /// parser will usually still return a tree, but defensively we treat any
    /// exception as "no CTE info available").
    /// </summary>
    public static DerivedTableSchemaResult Resolve(string sql, LanguageServerManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(sql)) return Empty;

        ParseResult parseResult;
        try
        {
            parseResult = SqlParser.TryParseRecovering(sql);
        }
        catch
        {
            return Empty;
        }

        // EffectiveQuery (vs Query) so a DECLARE preceding the WITH/SELECT
        // doesn't strand the CTE walker — ParseResult.Query is intentionally
        // null for multi-statement batches.
        QueryExpression? query = parseResult.EffectiveQuery;
        if (query is null) return Empty;

        SelectStatement? root = ExtractRootStatement(query);
        if (root is null) return Empty;

        // Collect DECLAREd variables visible across the whole batch so a
        // LET binding's RHS that references one (e.g.
        // `LET sx = width::Float32 / model_in_w`) can resolve `model_in_w`
        // to its declared kind instead of unknown. Without this, the LS
        // would mis-promote the binary expression by treating the unknown
        // operand as null and returning only the known side's kind.
        Dictionary<string, string?> declaredVariables = CollectDeclaredVariables(parseResult);

        Dictionary<string, IReadOnlyList<TableColumnEntry>> schemas =
            new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> fromAliasToSource =
            new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> letKinds =
            new(StringComparer.OrdinalIgnoreCase);
        // Tracks which entries in `schemas` came from FROM/JOIN subquery
        // aliases versus CTE definitions, so the hover label can render
        // "subquery" instead of "CTE" for derived-table aliases.
        HashSet<string> subqueryAliases =
            new(StringComparer.OrdinalIgnoreCase);

        if (root.CommonTableExpressions is not null)
        {
            foreach (CommonTableExpression cte in root.CommonTableExpressions)
            {
                IReadOnlyList<TableColumnEntry>? columns = ResolveCteColumns(cte, manifest, schemas, letKinds, declaredVariables);
                if (columns is not null)
                {
                    schemas[cte.Name] = columns;
                }
            }
        }

        // Capture LET bindings on the outer (top-level) SELECT too —
        // queries without CTEs still use LET, and the hover path needs to
        // resolve them by name.
        CollectLetKinds(root, manifest, schemas, letKinds, declaredVariables);

        // Record `FROM <cte_name> [AS] <alias>` aliases so qualified column
        // lookups like `f1.frame_index` can route to the CTE's schema.
        // Walked from the root and from every CTE body — an inner CTE may
        // alias an earlier CTE source too.
        CollectCteFromAliases(root, schemas, fromAliasToSource);

        // Project every FROM / JOIN subquery alias's columns into `schemas`
        // so the LSP's qualified-column lookups (`t.col`) and dot-completion
        // (`t.`) treat `FROM (SELECT …) t` the same as a CTE reference. The
        // engine resolves `t` against the subquery's projection at runtime;
        // mirroring that here keeps hover / completion in sync with what the
        // engine accepts.
        CollectSubqueryAliases(root, manifest, schemas, subqueryAliases, letKinds, declaredVariables);

        return new DerivedTableSchemaResult(schemas, fromAliasToSource, letKinds, subqueryAliases);
    }

    /// <summary>
    /// Walks <paramref name="statement"/>'s FROM / JOIN sources (and any CTE
    /// body's sources) for <see cref="SubquerySource"/> nodes, projects each
    /// inner SELECT's column list using the same machinery as
    /// <see cref="ResolveCteColumns"/>, and writes the result into
    /// <paramref name="schemas"/> keyed on the subquery's alias.
    /// </summary>
    /// <remarks>
    /// First-write-wins on alias collisions, so a CTE entry already in
    /// <paramref name="schemas"/> shadows a same-named subquery alias —
    /// matches the engine's CTE-shadows-table rule and avoids confusing
    /// the hover popup when the same name is reused.
    /// </remarks>
    private static void CollectSubqueryAliases(
        SelectStatement statement,
        LanguageServerManifest manifest,
        Dictionary<string, IReadOnlyList<TableColumnEntry>> schemas,
        HashSet<string> subqueryAliases,
        Dictionary<string, string> letKinds,
        IReadOnlyDictionary<string, string?> declaredVariables)
    {
        if (statement.CommonTableExpressions is not null)
        {
            foreach (CommonTableExpression cte in statement.CommonTableExpressions)
            {
                SelectStatement? cteBody = ExtractRootStatement(cte.Body);
                if (cteBody is not null)
                {
                    CollectSubqueryAliases(cteBody, manifest, schemas, subqueryAliases, letKinds, declaredVariables);
                }
            }
        }

        if (statement.From is not null)
        {
            BindSubqueryAlias(statement.From.Source, manifest, schemas, subqueryAliases, letKinds, declaredVariables);
        }
        if (statement.Joins is not null)
        {
            foreach (JoinClause join in statement.Joins)
            {
                BindSubqueryAlias(join.Source, manifest, schemas, subqueryAliases, letKinds, declaredVariables);
            }
        }
    }

    private static void BindSubqueryAlias(
        TableSource source,
        LanguageServerManifest manifest,
        Dictionary<string, IReadOnlyList<TableColumnEntry>> schemas,
        HashSet<string> subqueryAliases,
        Dictionary<string, string> letKinds,
        IReadOnlyDictionary<string, string?> declaredVariables)
    {
        if (source is not SubquerySource subquery) return;

        // SubquerySource.Query is already a SelectStatement, so no
        // ExtractRootStatement detour the way a CTE body needs.
        SelectStatement inner = subquery.Query;

        // Recurse so a subquery whose body itself contains another
        // subquery alias surfaces that nested alias too — same scope rule
        // the recursive CTE walk above uses.
        CollectSubqueryAliases(inner, manifest, schemas, subqueryAliases, letKinds, declaredVariables);

        if (schemas.ContainsKey(subquery.Alias)) return;

        IReadOnlyList<TableColumnEntry>? columns = ProjectSelectColumns(
            inner, manifest, schemas, letKinds, declaredVariables, columnRename: null);
        if (columns is not null)
        {
            schemas[subquery.Alias] = columns;
            subqueryAliases.Add(subquery.Alias);
        }
    }

    private static SelectStatement? ExtractRootStatement(QueryExpression query) => query switch
    {
        SelectQueryExpression select => select.Statement,
        // Compound queries (UNION etc.) at the top level — pick the left
        // branch as the schema source. CTE definitions live on the SELECT,
        // not the compound; the WITH clause attaches to whichever
        // SelectStatement carries it.
        CompoundQueryExpression compound => ExtractRootStatement(compound.Left),
        _ => null,
    };

    /// <summary>
    /// Resolves one CTE's output columns. Walks the CTE's inner SELECT list,
    /// building each output column from the named projection (alias when
    /// present, the column reference's name otherwise). Kinds come from the
    /// inner-FROM scope assembled from persistent tables, TVF sources, and
    /// earlier CTEs.
    /// </summary>
    private static IReadOnlyList<TableColumnEntry>? ResolveCteColumns(
        CommonTableExpression cte,
        LanguageServerManifest manifest,
        IReadOnlyDictionary<string, IReadOnlyList<TableColumnEntry>> earlierCtes,
        Dictionary<string, string> letKinds,
        IReadOnlyDictionary<string, string?> declaredVariables)
    {
        SelectStatement? inner = ExtractRootStatement(cte.Body);
        if (inner is null) return null;

        return ProjectSelectColumns(
            inner, manifest, earlierCtes, letKinds, declaredVariables, cte.ColumnNames);
    }

    /// <summary>
    /// Projects an inner <see cref="SelectStatement"/>'s SELECT list into a
    /// flat <see cref="TableColumnEntry"/> list — the schema a CTE body or a
    /// subquery source would expose to the surrounding query. Handles bare
    /// <c>SELECT *</c>, qualified <c>alias.*</c>, named expressions, LET
    /// snapshotting, and optional positional column rename
    /// (<c>WITH foo (a, b) AS …</c>).
    /// </summary>
    private static IReadOnlyList<TableColumnEntry>? ProjectSelectColumns(
        SelectStatement inner,
        LanguageServerManifest manifest,
        IReadOnlyDictionary<string, IReadOnlyList<TableColumnEntry>> earlierCtes,
        Dictionary<string, string> letKinds,
        IReadOnlyDictionary<string, string?> declaredVariables,
        IReadOnlyList<string>? columnRename)
    {
        InnerScope scope = BuildInnerScope(inner, manifest, earlierCtes)
            with { DeclaredVariables = declaredVariables };

        // Snapshot every LET binding's resolved kind into the global
        // `letKinds` map so the hover provider can surface kinds for
        // LET names that are never projected into the CTE's output.
        // First-wins across CTEs — collisions are rare and the user can
        // qualify when they happen.
        if (scope.LetBindings is not null)
        {
            foreach (KeyValuePair<string, Expression> binding in scope.LetBindings)
            {
                if (letKinds.ContainsKey(binding.Key)) continue;
                string? kind = ResolveExpressionKind(binding.Value, scope, manifest);
                if (kind is not null) letKinds[binding.Key] = kind;
            }
        }
        List<TableColumnEntry> projected = new();

        foreach (SelectColumn col in inner.Columns)
        {
            switch (col)
            {
                case SelectAllColumns:
                    // `SELECT *` — expand every source's columns in
                    // declaration order. Same behaviour the runtime
                    // ProjectOperator uses when no REPLACE/EXCLUDE list
                    // applies (we ignore those refinements here for now —
                    // false positives are friendlier than missing columns).
                    foreach (TableColumnEntry srcCol in EnumerateAllScopeColumns(scope))
                    {
                        projected.Add(srcCol);
                    }
                    break;

                case SelectTableColumns tableCols:
                    foreach (TableColumnEntry srcCol in EnumerateScopeColumnsForAlias(scope, tableCols.TableName))
                    {
                        projected.Add(srcCol);
                    }
                    break;

                default:
                    string name = DeriveOutputName(col);
                    string kind = ResolveExpressionKind(col.Expression, scope, manifest) ?? "?";
                    projected.Add(new TableColumnEntry { Name = name, Kind = kind, Nullable = true });
                    break;
            }
        }

        // Explicit column rename: `WITH foo (a, b) AS (...)`. Replaces the
        // derived names but keeps the kinds — same rule the engine applies
        // when projecting through a CTE with an explicit column list.
        if (columnRename is { Count: > 0 } rename)
        {
            List<TableColumnEntry> renamed = new(projected.Count);
            for (int i = 0; i < projected.Count; i++)
            {
                if (i < rename.Count)
                {
                    renamed.Add(new TableColumnEntry
                    {
                        Name = rename[i],
                        Kind = projected[i].Kind,
                        Nullable = projected[i].Nullable,
                    });
                }
                else
                {
                    renamed.Add(projected[i]);
                }
            }
            projected = renamed;
        }

        return projected;
    }

    /// <summary>
    /// Determines the output column name for a SELECT column. Prefers the
    /// explicit alias; falls back to a bare column reference's name; bottoms
    /// out at a synthesized placeholder so the user still sees the slot in
    /// the popup even when we can't derive a name from the expression.
    /// </summary>
    private static string DeriveOutputName(SelectColumn col)
    {
        if (!string.IsNullOrEmpty(col.Alias)) return col.Alias;
        if (col.Expression is ColumnReference colRef) return colRef.ColumnName;
        if (col.Expression is FunctionCallExpression fn) return fn.FunctionName;
        return "expr";
    }

    /// <summary>
    /// Resolves the kind of a SELECT-list expression to a manifest-friendly
    /// string (<c>"Int32"</c>, <c>"VideoFrame"</c>, etc.). Returns
    /// <see langword="null"/> when the kind can't be determined cheaply —
    /// the caller substitutes <c>"?"</c>. Handles: simple column references
    /// (incl. LET-bound names resolved via <see cref="InnerScope.LetBindings"/>),
    /// casts (return the declared target type), literals (numeric / string /
    /// boolean), and function calls (return type read from the manifest).
    /// Compound expressions (arithmetic, struct field access, index access)
    /// would require the engine's full type resolver and stay as <c>"?"</c>.
    /// </summary>
    private static string? ResolveExpressionKind(Expression expression, InnerScope scope, LanguageServerManifest manifest)
    {
        switch (expression)
        {
            case ColumnReference colRef:
                return ResolveColumnRefKind(colRef, scope, manifest);
            case CastExpression cast:
                return cast.TargetType;
            case LiteralExpression literal:
                return ResolveLiteralKind(literal);
            case FunctionCallExpression functionCall:
                return ResolveFunctionCallKind(functionCall, manifest);
            case StructLiteralExpression structLiteral:
                return ResolveStructLiteralKind(structLiteral, scope, manifest);
            case IndexAccessExpression indexAccess:
                return ResolveIndexAccessKind(indexAccess, scope, manifest);
            case BinaryExpression binary:
                return ResolveBinaryKind(binary, scope, manifest);
            // Unconditionally boolean-result shapes.
            case LikeExpression:
            case BetweenExpression:
            case IsNullExpression:
            case InExpression:
            case InSubqueryExpression:
            case ExistsExpression:
                return "Boolean";
            case UnaryExpression unary:
                return ResolveUnaryKind(unary, scope, manifest);
            case CaseExpression caseExpr:
                return ResolveCaseKind(caseExpr, scope, manifest);
            default:
                return null;
        }
    }

    /// <summary>
    /// Builds a canonical <c>Struct&lt;name: Kind, …&gt;</c> annotation for an
    /// inline struct literal (<c>{ label: 'x', score: 0.9 }</c>). Field names
    /// come straight from the AST; each field's kind is resolved recursively,
    /// falling back to <c>"?"</c> for shapes we can't derive (the name still
    /// surfaces for completion). Returns <see langword="null"/> for the empty
    /// literal so the caller's <c>"?"</c> fallback applies.
    /// </summary>
    private static string? ResolveStructLiteralKind(
        StructLiteralExpression literal, InnerScope scope, LanguageServerManifest manifest)
    {
        if (literal.Fields.Count == 0) return null;
        List<StructFieldShape> shapes = new(literal.Fields.Count);
        foreach (StructField field in literal.Fields)
        {
            string fieldKind = ResolveExpressionKind(field.Value, scope, manifest) ?? "?";
            shapes.Add(new StructFieldShape(field.Name, fieldKind));
        }
        return StructTypeAnnotation.Format(shapes);
    }

    /// <summary>
    /// Resolves a CASE expression's kind by inspecting each branch result
    /// in declaration order (WHEN-THEN bodies plus ELSE). First branch
    /// whose result kind we can derive wins; mismatched branches across a
    /// CASE are a runtime concern, not a hover concern. Returns
    /// <see langword="null"/> if no branch resolves so the caller's
    /// <c>"?"</c> fallback covers ambiguous shapes.
    /// </summary>
    private static string? ResolveCaseKind(
        CaseExpression caseExpr, InnerScope scope, LanguageServerManifest manifest)
    {
        foreach (WhenClause when in caseExpr.WhenClauses)
        {
            string? branchKind = ResolveExpressionKind(when.Result, scope, manifest);
            if (branchKind is not null) return branchKind;
        }
        if (caseExpr.ElseResult is not null)
        {
            return ResolveExpressionKind(caseExpr.ElseResult, scope, manifest);
        }
        return null;
    }

    /// <summary>
    /// <c>NOT</c> always produces <c>Boolean</c>; negate preserves the
    /// operand's kind. Mirrors the engine's <c>ResolveUnary</c> rules
    /// without reaching into the engine assembly.
    /// </summary>
    private static string? ResolveUnaryKind(
        UnaryExpression unary, InnerScope scope, LanguageServerManifest manifest)
    {
        return unary.Operator switch
        {
            UnaryOperator.Not => "Boolean",
            UnaryOperator.Negate => ResolveExpressionKind(unary.Operand, scope, manifest),
            _ => null,
        };
    }

    /// <summary>
    /// Resolves a binary expression's result kind, mirroring the runtime's
    /// promotion rules well enough to cover the common arithmetic shapes a
    /// user writes in a LET body (<c>width::Float32 / model_in_w</c>,
    /// <c>height / model_in_w</c>, …). Comparisons and logical ops always
    /// produce <c>Boolean</c>; arithmetic delegates to a minimal numeric-
    /// promotion routine in <see cref="PromoteArithmeticKind"/>. Returns
    /// <see langword="null"/> for combinations the LS doesn't model
    /// (temporal, non-numeric strings, unresolved operands), keeping the
    /// caller's existing <c>"?"</c> fallback for the uncertain cases.
    /// </summary>
    private static string? ResolveBinaryKind(
        BinaryExpression binary, InnerScope scope, LanguageServerManifest manifest)
    {
        if (IsBooleanResultOperator(binary.Operator)) return "Boolean";

        string? leftKind = ResolveExpressionKind(binary.Left, scope, manifest);
        string? rightKind = ResolveExpressionKind(binary.Right, scope, manifest);
        if (leftKind is null && rightKind is null) return null;
        // One operand resolved: surface its kind. Matches the engine's
        // "promote the known side" fallback in TryPromoteArithmeticKind.
        if (leftKind is null) return rightKind;
        if (rightKind is null) return leftKind;

        return PromoteArithmeticKind(leftKind, rightKind, binary.Operator);
    }

    private static bool IsBooleanResultOperator(BinaryOperator op) =>
        op is BinaryOperator.Equal
            or BinaryOperator.NotEqual
            or BinaryOperator.LessThan
            or BinaryOperator.GreaterThan
            or BinaryOperator.LessThanOrEqual
            or BinaryOperator.GreaterThanOrEqual
            or BinaryOperator.And
            or BinaryOperator.Or
            or BinaryOperator.Like
            or BinaryOperator.ILike
            or BinaryOperator.Regexp;

    /// <summary>
    /// Minimal numeric promotion mirroring the runtime's
    /// <c>ExpressionEvaluator.PromoteArithmeticKind</c> for the kinds a LET
    /// body realistically touches. Divide follows operand kinds PG-style
    /// (<c>5 / 2 → 2</c>; cast for fractional results); Power always returns
    /// float; Decimal beats float; Float64 beats Float32; small integers
    /// widen to Int32. Returns <see langword="null"/> for shapes outside
    /// this set so the caller surfaces <c>"?"</c> rather than a wrong guess.
    /// </summary>
    private static string? PromoteArithmeticKind(string leftKind, string rightKind, BinaryOperator op)
    {
        bool leftNum = IsNumericKind(leftKind, out bool leftIsDecimal, out bool leftIsFloat64, out bool leftIsFloat32);
        bool rightNum = IsNumericKind(rightKind, out bool rightIsDecimal, out bool rightIsFloat64, out bool rightIsFloat32);
        if (!leftNum || !rightNum) return null;

        if (op == BinaryOperator.Power)
        {
            if (leftIsDecimal || rightIsDecimal || leftIsFloat64 || rightIsFloat64) return "Float64";
            return "Float32";
        }

        if (leftIsDecimal || rightIsDecimal) return "Decimal";
        if (leftIsFloat64 || rightIsFloat64) return "Float64";
        if (leftIsFloat32 || rightIsFloat32) return "Float32";
        // Both small integers: widen to Int32, matching engine behaviour.
        return "Int32";
    }

    private static bool IsNumericKind(
        string kind, out bool isDecimal, out bool isFloat64, out bool isFloat32)
    {
        isDecimal = string.Equals(kind, "Decimal", StringComparison.OrdinalIgnoreCase);
        isFloat64 = string.Equals(kind, "Float64", StringComparison.OrdinalIgnoreCase);
        isFloat32 = string.Equals(kind, "Float32", StringComparison.OrdinalIgnoreCase);
        if (isDecimal || isFloat64 || isFloat32) return true;
        return kind is "Int8" or "Int16" or "Int32" or "Int64" or "Int128"
            or "UInt8" or "UInt16" or "UInt32" or "UInt64" or "UInt128"
            or "Float16";
    }

    /// <summary>
    /// Resolves <c>source[index]</c> when the source's kind is a canonical
    /// <c>Struct&lt;...&gt;</c> annotation — looks up the named field
    /// (string index) or the 1-based positional field (integer index).
    /// Lets LET-of-bracket-access chains like
    /// <c>LET intr = curr_depth['intrinsics']</c> propagate the field's
    /// kind upward so hover and downstream LET kinds resolve correctly.
    /// Array indexing isn't yet handled — that'd need shape-aware element
    /// extraction the LS doesn't track today.
    /// </summary>
    private static string? ResolveIndexAccessKind(
        IndexAccessExpression indexAccess, InnerScope scope, LanguageServerManifest manifest)
    {
        if (indexAccess.Indices.Count != 1) return null;
        string? sourceKind = ResolveExpressionKind(indexAccess.Source, scope, manifest);
        if (!StructTypeAnnotation.TryParse(sourceKind, out IReadOnlyList<StructFieldShape> fields))
        {
            return null;
        }

        Expression index = indexAccess.Indices[0];
        if (index is LiteralExpression { Value: string fieldName })
        {
            foreach (StructFieldShape f in fields)
            {
                if (string.Equals(f.Name, fieldName, StringComparison.OrdinalIgnoreCase))
                {
                    return f.Kind;
                }
            }
            return null;
        }
        // 1-based positional access — `struct[1]` reads the first field.
        // Match the runtime's PG-style ordinal indexing in
        // ExpressionEvaluator.EvaluateIndexAccessAsync's struct branch.
        if (index is LiteralExpression intLit && TryAsInt(intLit.Value, out int ordinal))
        {
            int zeroBased = ordinal - 1;
            if (zeroBased < 0 || zeroBased >= fields.Count) return null;
            return fields[zeroBased].Kind;
        }
        return null;
    }

    private static bool TryAsInt(object? value, out int result)
    {
        switch (value)
        {
            case int i: result = i; return true;
            case long l when l >= int.MinValue && l <= int.MaxValue: result = (int)l; return true;
            case short s: result = s; return true;
            case sbyte sb: result = sb; return true;
            case byte b: result = b; return true;
            case ushort us: result = us; return true;
            case uint u when u <= int.MaxValue: result = (int)u; return true;
            default: result = 0; return false;
        }
    }

    /// <summary>
    /// Maps a literal's runtime <see cref="System.Type"/> to the
    /// corresponding DataKind name. Null literals come back as
    /// <see langword="null"/> — the kind depends on context the language
    /// server doesn't track.
    /// </summary>
    private static string? ResolveLiteralKind(LiteralExpression literal)
    {
        return literal.Value switch
        {
            null => null,
            string => "String",
            bool => "Boolean",
            sbyte => "Int8",
            short => "Int16",
            int => "Int32",
            long => "Int64",
            float => "Float32",
            double => "Float64",
            _ => null,
        };
    }

    /// <summary>
    /// Resolves a function call's return type by looking up the call name in
    /// the manifest's function and model lists. Schema-qualified calls match
    /// by both name and schema; unqualified calls walk the manifest's
    /// search_path. Returns <see langword="null"/> when no match is found —
    /// the LET / SELECT-column path that called us substitutes <c>"?"</c>.
    /// </summary>
    private static string? ResolveFunctionCallKind(FunctionCallExpression call, LanguageServerManifest manifest)
    {
        // Schema-qualified call (e.g. `models.X(...)`, `inference.devices(...)`).
        if (call.SchemaName is not null)
        {
            // `models.X(...)` — registered models live in their own manifest
            // list, not the function list. A struct-returning model surfaces
            // its field shape so `models.X(...) AS p` then `p.` completes the
            // struct's fields; otherwise fall back to the scalar output kind.
            if (string.Equals(call.SchemaName, "models", StringComparison.OrdinalIgnoreCase)
                && manifest.Models is { } models)
            {
                foreach (ModelEntry model in models)
                {
                    if (string.Equals(model.Name, call.FunctionName, StringComparison.OrdinalIgnoreCase))
                    {
                        return StructKindFromSignatures(model.OutputStructFields, model.OutputIsArray)
                            ?? model.OutputKind;
                    }
                }
            }

            foreach (FunctionSignature f in manifest.Functions)
            {
                if (string.Equals(f.SchemaName, call.SchemaName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(f.Name, call.FunctionName, StringComparison.OrdinalIgnoreCase))
                {
                    return StructKindFromSignatures(f.OutputStructFields, f.OutputIsArray) ?? f.ReturnType;
                }
            }
            return null;
        }

        // Unqualified — walk search_path, then any registered function.
        foreach (string schema in manifest.SearchPath)
        {
            foreach (FunctionSignature f in manifest.Functions)
            {
                if (string.Equals(f.SchemaName, schema, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(f.Name, call.FunctionName, StringComparison.OrdinalIgnoreCase))
                {
                    return StructKindFromSignatures(f.OutputStructFields, f.OutputIsArray) ?? f.ReturnType;
                }
            }
        }
        foreach (FunctionSignature f in manifest.Functions)
        {
            if (string.Equals(f.Name, call.FunctionName, StringComparison.OrdinalIgnoreCase))
            {
                return StructKindFromSignatures(f.OutputStructFields, f.OutputIsArray) ?? f.ReturnType;
            }
        }
        return null;
    }

    /// <summary>
    /// Builds a canonical <c>Struct&lt;name: Kind, …&gt;</c> annotation from a
    /// model/function's declared <c>OutputStructFields</c>, or
    /// <see langword="null"/> when there are none. The annotation round-trips
    /// through <see cref="StructTypeAnnotation.TryParse"/>, so a column carrying
    /// it expands to its fields in hover and dot completion.
    /// </summary>
    private static string? StructKindFromSignatures(
        IReadOnlyList<StructFieldSignature>? fields, bool isArray)
    {
        if (fields is not { Count: > 0 }) return null;
        List<StructFieldShape> shapes = new(fields.Count);
        foreach (StructFieldSignature field in fields)
        {
            shapes.Add(new StructFieldShape(field.Name, field.Kind));
        }
        string structLabel = StructTypeAnnotation.Format(shapes);
        // Array-of-struct returns (detectors like `models.yolox_s`) wrap the
        // element shape so hover shows `Array<Struct<…>>` and `unnest(...)`
        // can strip one array level back to the element struct. Without the
        // wrapper the array-ness is lost and `unnest`'s element synthesis
        // (which strips an `Array<…>` prefix) can't resolve the `value` column.
        return isArray ? $"Array<{structLabel}>" : structLabel;
    }

    /// <summary>
    /// Walks <paramref name="scope"/>'s sources looking for a column that
    /// matches <paramref name="colRef"/>. Qualified references narrow by
    /// alias first; unqualified references check LET bindings (so a SELECT
    /// list that refers to a sibling <c>LET name = expr</c> resolves through
    /// the binding's expression) before walking the FROM scope.
    /// </summary>
    private static string? ResolveColumnRefKind(ColumnReference colRef, InnerScope scope, LanguageServerManifest manifest)
    {
        if (colRef.TableName is not null)
        {
            if (scope.AliasToColumns.TryGetValue(colRef.TableName, out IReadOnlyList<TableColumnEntry>? aliasCols))
            {
                foreach (TableColumnEntry c in aliasCols)
                {
                    if (string.Equals(c.Name, colRef.ColumnName, StringComparison.OrdinalIgnoreCase))
                    {
                        return c.Kind;
                    }
                }
            }
            // Qualifier didn't match a FROM-source alias — try resolving it
            // as a LET-bound struct: `LET curr_depth = models.X(...)` then
            // `curr_depth.depth`. Pull the LET's expression kind; if it's
            // a `Struct<...>` annotation, look up the field by name.
            if (scope.LetBindings is { } letBindingsForStruct
                && letBindingsForStruct.TryGetValue(colRef.TableName, out Expression? letExprForStruct))
            {
                string? structKind = ResolveExpressionKind(letExprForStruct, scope, manifest);
                return ResolveStructFieldKind(structKind, colRef.ColumnName);
            }
            return null;
        }

        // Unqualified — LET bindings shadow FROM-source columns, matching
        // the engine's row-augmentation order (LET names land on the
        // augmented row before projection sees the source columns).
        if (scope.LetBindings is { } letBindings
            && letBindings.TryGetValue(colRef.ColumnName, out Expression? letExpr))
        {
            return ResolveExpressionKind(letExpr, scope, manifest);
        }

        foreach (KeyValuePair<string, IReadOnlyList<TableColumnEntry>> entry in scope.AliasToColumns)
        {
            foreach (TableColumnEntry c in entry.Value)
            {
                if (string.Equals(c.Name, colRef.ColumnName, StringComparison.OrdinalIgnoreCase))
                {
                    return c.Kind;
                }
            }
        }

        // Final fallback: DECLAREd variable visible across the whole batch.
        // The engine evaluator checks variable scope before the row schema
        // when unqualified, so a name not matching any column here that does
        // match a DECLARE resolves to the declared type — letting binary
        // expressions like `width::Float32 / model_in_w` promote correctly.
        if (scope.DeclaredVariables is { } declared
            && declared.TryGetValue(colRef.ColumnName, out string? declaredKind))
        {
            return declaredKind;
        }
        return null;
    }

    /// <summary>
    /// Resolves a field-access kind through a struct annotation. When
    /// <paramref name="structKindLabel"/> is a canonical
    /// <c>"Struct&lt;name: Kind, …&gt;"</c> string and a field matching
    /// <paramref name="fieldName"/> exists, returns that field's kind
    /// label. Returns <see langword="null"/> for opaque <c>Struct</c>, for
    /// non-struct kinds, and for missing field names — every fallback
    /// keeps the SELECT-list path emitting <c>"?"</c> rather than a wrong
    /// concrete kind.
    /// </summary>
    internal static string? ResolveStructFieldKind(string? structKindLabel, string fieldName)
    {
        if (!StructTypeAnnotation.TryParse(structKindLabel, out IReadOnlyList<StructFieldShape> fields))
        {
            return null;
        }
        foreach (StructFieldShape f in fields)
        {
            if (string.Equals(f.Name, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                return f.Kind;
            }
        }
        return null;
    }

    private static IEnumerable<TableColumnEntry> EnumerateAllScopeColumns(InnerScope scope)
    {
        foreach (KeyValuePair<string, IReadOnlyList<TableColumnEntry>> entry in scope.AliasToColumns)
        {
            foreach (TableColumnEntry c in entry.Value) yield return c;
        }
    }

    private static IEnumerable<TableColumnEntry> EnumerateScopeColumnsForAlias(InnerScope scope, string alias)
    {
        if (scope.AliasToColumns.TryGetValue(alias, out IReadOnlyList<TableColumnEntry>? cols))
        {
            foreach (TableColumnEntry c in cols) yield return c;
        }
    }

    /// <summary>
    /// Assembles the per-CTE inner-FROM scope: alias → column list for every
    /// table / TVF / CTE-reference in the CTE's own FROM / JOIN clauses.
    /// CTE references resolve through <paramref name="earlierCtes"/>; TVFs
    /// resolve through the manifest's <see cref="FunctionSignature.OutputColumns"/>;
    /// persistent tables resolve through the manifest's <see cref="LanguageServerManifest.Tables"/>.
    /// </summary>
    private static InnerScope BuildInnerScope(
        SelectStatement statement,
        LanguageServerManifest manifest,
        IReadOnlyDictionary<string, IReadOnlyList<TableColumnEntry>> earlierCtes)
    {
        Dictionary<string, IReadOnlyList<TableColumnEntry>> aliasToColumns =
            new(StringComparer.OrdinalIgnoreCase);

        if (statement.From is not null)
        {
            BindSource(statement.From.Source, manifest, earlierCtes, aliasToColumns);
        }
        if (statement.Joins is not null)
        {
            foreach (JoinClause join in statement.Joins)
            {
                BindSource(join.Source, manifest, earlierCtes, aliasToColumns);
            }
        }

        // LET bindings — capture name → expression so SELECT-list refs to
        // LET-bound names resolve through the binding's RHS. Skip
        // destructured bindings (their per-name kinds depend on shape
        // inference we don't do here) and any binding with a duplicate
        // name (defensive — the engine rejects duplicates at planning).
        Dictionary<string, Expression>? letBindings = null;
        if (statement.LetBindings is { Count: > 0 } lets)
        {
            letBindings = new Dictionary<string, Expression>(lets.Count, StringComparer.OrdinalIgnoreCase);
            foreach (LetBinding b in lets)
            {
                if (b.Destructure is not null) continue;
                letBindings.TryAdd(b.Name, b.Expression);
            }
        }

        return new InnerScope(aliasToColumns, letBindings);
    }

    private static void BindSource(
        TableSource source,
        LanguageServerManifest manifest,
        IReadOnlyDictionary<string, IReadOnlyList<TableColumnEntry>> earlierCtes,
        Dictionary<string, IReadOnlyList<TableColumnEntry>> aliasToColumns)
    {
        switch (source)
        {
            case TableReference tableRef:
                {
                    // CTE references match by name first — engine semantics:
                    // a CTE name shadows a real table when both exist.
                    if (earlierCtes.TryGetValue(tableRef.Name, out IReadOnlyList<TableColumnEntry>? cteCols))
                    {
                        string aliasKey = tableRef.Alias ?? tableRef.Name;
                        aliasToColumns.TryAdd(aliasKey, cteCols);
                        return;
                    }
                    // Persistent table lookup. Walk the manifest with an
                    // exact match first, then fall back to the unqualified
                    // suffix (mirrors `HoverProvider.GetTableHover`).
                    TableSchemaEntry? table = FindTable(manifest, tableRef.SchemaName, tableRef.Name);
                    if (table is null) return;
                    IReadOnlyList<TableColumnEntry> cols = table.Columns;
                    string tableAliasKey = tableRef.Alias ?? tableRef.Name;
                    aliasToColumns.TryAdd(tableAliasKey, cols);
                    break;
                }
            case FunctionSource functionSource:
                {
                    FunctionSignature? signature = FindTvf(manifest, functionSource.FunctionName, functionSource.SchemaName);
                    if (signature?.OutputColumns is null) return;
                    string fnAliasKey = functionSource.Alias ?? functionSource.FunctionName;
                    aliasToColumns.TryAdd(fnAliasKey, signature.OutputColumns);
                    break;
                }
            case SubquerySource subquery:
                {
                    // Derived-table projection: walk the inner SELECT and
                    // project its columns the same way we do for a CTE
                    // body, then expose the result under the subquery's
                    // alias so qualified references (`sub.col`) in the
                    // surrounding SELECT list resolve correctly.
                    SelectStatement innerBody = subquery.Query;
                    // LET kinds and DECLARE variables collected at the
                    // outer scope aren't threaded down here — names still
                    // resolve correctly; any opaque expression kinds fall
                    // back to "?" the same way an unresolvable CTE column
                    // would.
                    IReadOnlyList<TableColumnEntry>? subColumns = ProjectSelectColumns(
                        innerBody,
                        manifest,
                        earlierCtes,
                        letKinds: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                        declaredVariables: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
                        columnRename: null);
                    if (subColumns is not null)
                    {
                        aliasToColumns.TryAdd(subquery.Alias, subColumns);
                    }
                    break;
                }
        }
    }

    private static TableSchemaEntry? FindTable(LanguageServerManifest manifest, string? schemaName, string tableName)
    {
        if (schemaName is not null)
        {
            string qualified = $"{schemaName}.{tableName}";
            foreach (TableSchemaEntry t in manifest.Tables)
            {
                if (string.Equals(t.Name, qualified, StringComparison.OrdinalIgnoreCase)) return t;
            }
            return null;
        }

        foreach (TableSchemaEntry t in manifest.Tables)
        {
            if (string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase)) return t;
        }
        foreach (string schema in manifest.SearchPath)
        {
            string qualified = $"{schema}.{tableName}";
            foreach (TableSchemaEntry t in manifest.Tables)
            {
                if (string.Equals(t.Name, qualified, StringComparison.OrdinalIgnoreCase)) return t;
            }
        }
        return null;
    }

    private static FunctionSignature? FindTvf(LanguageServerManifest manifest, string functionName, string? schemaName)
    {
        foreach (FunctionSignature f in manifest.Functions)
        {
            if (!f.IsTableValued) continue;
            if (!string.Equals(f.Name, functionName, StringComparison.OrdinalIgnoreCase)) continue;
            if (schemaName is not null && !string.Equals(f.SchemaName, schemaName, StringComparison.OrdinalIgnoreCase)) continue;
            return f;
        }
        return null;
    }

    /// <summary>
    /// Walks every FROM / JOIN in the root statement and any CTE body,
    /// recording each <c>FROM &lt;cte_name&gt; [AS] &lt;alias&gt;</c> pair so
    /// downstream qualified-column lookups (<c>f1.frame_index</c>) can route
    /// to the CTE's schema. Bare CTE-name references (no alias) are
    /// recorded too — the engine treats the name itself as a valid
    /// qualifier in that case.
    /// </summary>
    private static void CollectCteFromAliases(
        SelectStatement statement,
        IReadOnlyDictionary<string, IReadOnlyList<TableColumnEntry>> derivedSchemas,
        Dictionary<string, string> fromAliasToSource)
    {
        if (statement.CommonTableExpressions is not null)
        {
            foreach (CommonTableExpression cte in statement.CommonTableExpressions)
            {
                SelectStatement? inner = ExtractRootStatement(cte.Body);
                if (inner is not null) CollectCteFromAliases(inner, derivedSchemas, fromAliasToSource);
            }
        }

        if (statement.From is not null) CollectFromSource(statement.From.Source);
        if (statement.Joins is not null)
        {
            foreach (JoinClause join in statement.Joins) CollectFromSource(join.Source);
        }

        void CollectFromSource(TableSource source)
        {
            if (source is TableReference tableRef && derivedSchemas.ContainsKey(tableRef.Name))
            {
                if (tableRef.Alias is not null)
                {
                    fromAliasToSource.TryAdd(tableRef.Alias, tableRef.Name);
                }
                // Bare CTE name (no alias) is also a valid qualifier.
                fromAliasToSource.TryAdd(tableRef.Name, tableRef.Name);
            }
        }
    }

    /// <summary>
    /// Captures LET bindings on the outer (top-level) <paramref name="statement"/>
    /// — every CTE's LETs have already been recorded by
    /// <see cref="ResolveCteColumns"/>. Without this top-level pass, a
    /// SELECT-with-LET that doesn't use CTEs would surface no LET kinds
    /// for hover.
    /// </summary>
    private static void CollectLetKinds(
        SelectStatement statement,
        LanguageServerManifest manifest,
        IReadOnlyDictionary<string, IReadOnlyList<TableColumnEntry>> earlierCtes,
        Dictionary<string, string> letKinds,
        IReadOnlyDictionary<string, string?> declaredVariables)
    {
        if (statement.LetBindings is not { Count: > 0 }) return;
        InnerScope scope = BuildInnerScope(statement, manifest, earlierCtes)
            with { DeclaredVariables = declaredVariables };
        if (scope.LetBindings is null) return;
        foreach (KeyValuePair<string, Expression> binding in scope.LetBindings)
        {
            if (letKinds.ContainsKey(binding.Key)) continue;
            string? kind = ResolveExpressionKind(binding.Value, scope, manifest);
            if (kind is not null) letKinds[binding.Key] = kind;
        }
    }

    /// <summary>
    /// Walks <see cref="ParseResult.Statements"/> and collects every
    /// <see cref="DeclareStatement"/>'s variable name → declared type so
    /// LET-RHS expressions can resolve references to DECLAREd variables.
    /// Recurses into block / if / loop / try bodies; first declaration
    /// wins on collision (matches the outer-scope-shadowing rule).
    /// </summary>
    private static Dictionary<string, string?> CollectDeclaredVariables(ParseResult parseResult)
    {
        Dictionary<string, string?> result = new(StringComparer.OrdinalIgnoreCase);
        if (parseResult.Statements is null) return result;
        foreach (Statement statement in parseResult.Statements)
        {
            CollectDeclares(statement, result);
        }
        return result;

        static void CollectDeclares(Statement statement, Dictionary<string, string?> sink)
        {
            switch (statement)
            {
                case DeclareStatement decl:
                    AddIfNew(sink, decl.VariableName, decl.TypeName);
                    break;
                case BlockStatement block:
                    foreach (Statement child in block.Statements) CollectDeclares(child, sink);
                    break;
                case IfStatement ifStmt:
                    CollectDeclares(ifStmt.Then, sink);
                    if (ifStmt.Else is not null) CollectDeclares(ifStmt.Else, sink);
                    break;
                case WhileStatement whileStmt:
                    CollectDeclares(whileStmt.Body, sink);
                    break;
                case ForCounterStatement forCtr:
                    // `FOR i = start TO end` introduces i in the loop scope.
                    AddIfNew(sink, forCtr.VariableName, "Int32");
                    CollectDeclares(forCtr.Body, sink);
                    break;
                case ForInStatement forIn:
                    // Cursor-FOR variables are struct-shaped — out of scope
                    // for now; recurse so nested DECLAREs aren't lost.
                    CollectDeclares(forIn.Body, sink);
                    break;
                case TryStatement tryStmt:
                    CollectDeclares(tryStmt.TryBody, sink);
                    AddIfNew(sink, tryStmt.ErrorVariableName, "String");
                    CollectDeclares(tryStmt.CatchBody, sink);
                    if (tryStmt.FinallyBody is not null) CollectDeclares(tryStmt.FinallyBody, sink);
                    break;
                case CreateProcedureStatement createProc:
                    foreach (UdfParameter p in createProc.Parameters)
                        AddIfNew(sink, p.Name, p.TypeName);
                    CollectDeclares(createProc.Body, sink);
                    break;
                case CreateFunctionStatement createFn:
                    foreach (UdfParameter p in createFn.Parameters)
                        AddIfNew(sink, p.Name, p.TypeName);
                    if (createFn.StatementBody is { } stmtBody)
                    {
                        foreach (Statement child in stmtBody) CollectDeclares(child, sink);
                    }
                    break;
            }
        }

        static void AddIfNew(Dictionary<string, string?> sink, string name, string? kind)
        {
            if (!sink.ContainsKey(name)) sink[name] = kind;
        }
    }
}

/// <summary>
/// Output of <see cref="DerivedTableSchemaResolver.Resolve"/>. <see cref="Schemas"/>
/// maps each derived-table source's name (CTE name or FROM/JOIN subquery alias)
/// to its projected column list; <see cref="FromAliasToSourceName"/>
/// maps every <c>FROM &lt;cte&gt; [AS] alias</c> alias (including the bare
/// CTE name when no alias was given) to the CTE name for qualified lookups.
/// <see cref="LetBindingKinds"/> maps every LET binding's name (across the
/// outermost SELECT and every CTE body) to its resolved kind label so the
/// hover path can surface a type for LET names that aren't projected.
/// <see cref="SubqueryAliases"/> lists the subset of <see cref="Schemas"/>
/// keys that came from inline FROM/JOIN subqueries (the rest being CTE
/// definitions), so consumers can pick the right hover label.
/// </summary>
internal sealed record DerivedTableSchemaResult(
    IReadOnlyDictionary<string, IReadOnlyList<TableColumnEntry>> Schemas,
    IReadOnlyDictionary<string, string> FromAliasToSourceName,
    IReadOnlyDictionary<string, string> LetBindingKinds,
    IReadOnlySet<string> SubqueryAliases);

/// <summary>
/// Per-CTE inner-FROM scope used by <see cref="DerivedTableSchemaResolver"/>: maps
/// each source alias (or unaliased name) to the column list that alias
/// resolves to, plus the CTE's LET bindings (name → expression) so the
/// SELECT-list resolver can chase a reference like <c>prev_image</c>
/// through to its <c>LET prev_image = video_frame_to_image(...)</c>
/// definition. Built fresh for every CTE because the FROM sources and
/// LET set differ CTE-to-CTE.
/// </summary>
internal sealed record InnerScope(
    IReadOnlyDictionary<string, IReadOnlyList<TableColumnEntry>> AliasToColumns,
    IReadOnlyDictionary<string, Expression>? LetBindings = null,
    IReadOnlyDictionary<string, string?>? DeclaredVariables = null);
