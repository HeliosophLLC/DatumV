namespace DatumIngest.LanguageServer;

using DatumIngest.Manifest;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

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
internal static class CteSchemaResolver
{
    /// <summary>
    /// Empty result returned when parsing fails or no CTEs are present.
    /// Kept as a singleton to avoid allocating a new empty dictionary on
    /// every classify call.
    /// </summary>
    public static readonly CteSchemaResult Empty = new(
        new Dictionary<string, IReadOnlyList<TableColumnEntry>>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Parses <paramref name="sql"/> and resolves every CTE's projected
    /// columns. Returns <see cref="Empty"/> on parse failure (the recovering
    /// parser will usually still return a tree, but defensively we treat any
    /// exception as "no CTE info available").
    /// </summary>
    public static CteSchemaResult Resolve(string sql, LanguageServerManifest manifest)
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

        if (parseResult.Query is null) return Empty;

        SelectStatement? root = ExtractRootStatement(parseResult.Query);
        if (root is null) return Empty;

        Dictionary<string, IReadOnlyList<TableColumnEntry>> schemas =
            new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> fromAliasToCte =
            new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> letKinds =
            new(StringComparer.OrdinalIgnoreCase);

        if (root.CommonTableExpressions is not null)
        {
            foreach (CommonTableExpression cte in root.CommonTableExpressions)
            {
                IReadOnlyList<TableColumnEntry>? columns = ResolveCteColumns(cte, manifest, schemas, letKinds);
                if (columns is not null)
                {
                    schemas[cte.Name] = columns;
                }
            }
        }

        // Capture LET bindings on the outer (top-level) SELECT too —
        // queries without CTEs still use LET, and the hover path needs to
        // resolve them by name.
        CollectLetKinds(root, manifest, schemas, letKinds);

        // Record `FROM <cte_name> [AS] <alias>` aliases so qualified column
        // lookups like `f1.frame_index` can route to the CTE's schema.
        // Walked from the root and from every CTE body — an inner CTE may
        // alias an earlier CTE source too.
        CollectCteFromAliases(root, schemas, fromAliasToCte);

        return new CteSchemaResult(schemas, fromAliasToCte, letKinds);
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
        Dictionary<string, string> letKinds)
    {
        SelectStatement? inner = ExtractRootStatement(cte.Body);
        if (inner is null) return null;

        InnerScope scope = BuildInnerScope(inner, manifest, earlierCtes);

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
        if (cte.ColumnNames is { Count: > 0 } rename)
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
            case IndexAccessExpression indexAccess:
                return ResolveIndexAccessKind(indexAccess, scope, manifest);
            default:
                return null;
        }
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
            foreach (FunctionSignature f in manifest.Functions)
            {
                if (string.Equals(f.SchemaName, call.SchemaName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(f.Name, call.FunctionName, StringComparison.OrdinalIgnoreCase))
                {
                    return f.ReturnType;
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
                    return f.ReturnType;
                }
            }
        }
        foreach (FunctionSignature f in manifest.Functions)
        {
            if (string.Equals(f.Name, call.FunctionName, StringComparison.OrdinalIgnoreCase))
            {
                return f.ReturnType;
            }
        }
        return null;
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
                    // Subqueries in FROM project a derived row shape. We
                    // could resolve their projection the same way we do
                    // for CTEs; deferred for now — derived-table support
                    // is an extension on top of this slice.
                    _ = subquery;
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
        IReadOnlyDictionary<string, IReadOnlyList<TableColumnEntry>> cteSchemas,
        Dictionary<string, string> fromAliasToCte)
    {
        if (statement.CommonTableExpressions is not null)
        {
            foreach (CommonTableExpression cte in statement.CommonTableExpressions)
            {
                SelectStatement? inner = ExtractRootStatement(cte.Body);
                if (inner is not null) CollectCteFromAliases(inner, cteSchemas, fromAliasToCte);
            }
        }

        if (statement.From is not null) CollectFromSource(statement.From.Source);
        if (statement.Joins is not null)
        {
            foreach (JoinClause join in statement.Joins) CollectFromSource(join.Source);
        }

        void CollectFromSource(TableSource source)
        {
            if (source is TableReference tableRef && cteSchemas.ContainsKey(tableRef.Name))
            {
                if (tableRef.Alias is not null)
                {
                    fromAliasToCte.TryAdd(tableRef.Alias, tableRef.Name);
                }
                // Bare CTE name (no alias) is also a valid qualifier.
                fromAliasToCte.TryAdd(tableRef.Name, tableRef.Name);
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
        Dictionary<string, string> letKinds)
    {
        if (statement.LetBindings is not { Count: > 0 }) return;
        InnerScope scope = BuildInnerScope(statement, manifest, earlierCtes);
        if (scope.LetBindings is null) return;
        foreach (KeyValuePair<string, Expression> binding in scope.LetBindings)
        {
            if (letKinds.ContainsKey(binding.Key)) continue;
            string? kind = ResolveExpressionKind(binding.Value, scope, manifest);
            if (kind is not null) letKinds[binding.Key] = kind;
        }
    }
}

/// <summary>
/// Output of <see cref="CteSchemaResolver.Resolve"/>. <see cref="Schemas"/>
/// maps each CTE's name to its projected column list; <see cref="FromAliasToCteName"/>
/// maps every <c>FROM &lt;cte&gt; [AS] alias</c> alias (including the bare
/// CTE name when no alias was given) to the CTE name for qualified lookups.
/// <see cref="LetBindingKinds"/> maps every LET binding's name (across the
/// outermost SELECT and every CTE body) to its resolved kind label so the
/// hover path can surface a type for LET names that aren't projected.
/// </summary>
internal sealed record CteSchemaResult(
    IReadOnlyDictionary<string, IReadOnlyList<TableColumnEntry>> Schemas,
    IReadOnlyDictionary<string, string> FromAliasToCteName,
    IReadOnlyDictionary<string, string> LetBindingKinds);

/// <summary>
/// Per-CTE inner-FROM scope used by <see cref="CteSchemaResolver"/>: maps
/// each source alias (or unaliased name) to the column list that alias
/// resolves to, plus the CTE's LET bindings (name → expression) so the
/// SELECT-list resolver can chase a reference like <c>prev_image</c>
/// through to its <c>LET prev_image = video_frame_to_image(...)</c>
/// definition. Built fresh for every CTE because the FROM sources and
/// LET set differ CTE-to-CTE.
/// </summary>
internal sealed record InnerScope(
    IReadOnlyDictionary<string, IReadOnlyList<TableColumnEntry>> AliasToColumns,
    IReadOnlyDictionary<string, Expression>? LetBindings = null);
