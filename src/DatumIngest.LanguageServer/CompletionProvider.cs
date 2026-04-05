namespace DatumIngest.LanguageServer;

using DatumIngest.Manifest;
using DatumIngest.Parsing.Tokens;

/// <summary>
/// Generates context-aware completion items from a <see cref="LanguageServerManifest"/>
/// based on the cursor position within a SQL fragment.
/// </summary>
public sealed class CompletionProvider
{
    private readonly LanguageServerManifest _manifest;

    /// <summary>
    /// Creates a completion provider backed by the given manifest.
    /// </summary>
    public CompletionProvider(LanguageServerManifest manifest)
    {
        _manifest = manifest;
    }

    /// <summary>
    /// Returns completion items for the given SQL text and cursor offset.
    /// </summary>
    /// <param name="sql">The full SQL text in the editor.</param>
    /// <param name="cursorOffset">The 0-based character offset of the cursor.</param>
    /// <returns>An array of completion items, filtered by any typed prefix.</returns>
    public CompletionItem[] GetCompletions(string sql, int cursorOffset)
    {
        CompletionZone zone = CompletionContext.Classify(sql, cursorOffset);
        // CTE projection schemas are resolved by parsing the SQL — the
        // manifest-free classifier above doesn't know about them. Result
        // is empty when there are no CTEs or when parsing failed, so the
        // existing zones keep working unchanged for CTE-less queries.
        CteSchemaResult cteSchemas = CteSchemaResolver.Resolve(sql, _manifest);
        List<CompletionItem> items = new();

        // Variables declared earlier in the fragment (DECLARE @x, FOR @i,
        // CATCH @err, ...) — surfaced in any expression-like zone where
        // they could legally be referenced. Done up front so each zone
        // case below stays focused on its non-variable contributions.
        if (ZoneAcceptsVariableReferences(zone.Kind))
        {
            AddVariablesInScope(items, zone.VariablesInScope);
        }

        switch (zone.Kind)
        {
            case CompletionZoneKind.StatementStart:
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterSelect:
                AddColumns(items, zone.TablesInScope, zone.TvfAliasesInScope, cteSchemas);
                AddScalarFunctions(items);
                AddAggregateFunctions(items);
                AddWindowFunctions(items);
                AddSchemaNames(items, SchemaSurfaces.Expression);
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterFrom:
            case CompletionZoneKind.AfterJoin:
                AddTables(items);
                AddTableValuedFunctions(items);
                AddSchemaNames(items, SchemaSurfaces.FromClause);
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterFromSource:
            case CompletionZoneKind.AfterJoinSource:
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterWhere:
                AddColumns(items, zone.TablesInScope, zone.TvfAliasesInScope, cteSchemas);
                AddScalarFunctions(items);
                AddSchemaNames(items, SchemaSurfaces.Expression);
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterOn:
                AddColumns(items, zone.TablesInScope, zone.TvfAliasesInScope, cteSchemas);
                AddScalarFunctions(items);
                AddSchemaNames(items, SchemaSurfaces.Expression);
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.Expression:
                AddColumns(items, zone.TablesInScope, zone.TvfAliasesInScope, cteSchemas);
                AddScalarFunctions(items);
                AddSchemaNames(items, SchemaSurfaces.Expression);
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.ProceduralExpression:
                // Procedural top level has no row context, so columns aren't
                // in scope — only @vars (not yet in the manifest), scalar
                // functions, and literals/keywords. Without this branch the
                // user typing `IF b…` would see column names like `backend`
                // from `system_models` even though there's no FROM in scope.
                AddScalarFunctions(items);
                AddSchemaNames(items, SchemaSurfaces.Expression);
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterOrderBy:
                AddColumns(items, zone.TablesInScope, zone.TvfAliasesInScope, cteSchemas);
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterGroupBy:
                AddColumns(items, zone.TablesInScope, zone.TvfAliasesInScope, cteSchemas);
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterHaving:
                AddColumns(items, zone.TablesInScope, zone.TvfAliasesInScope, cteSchemas);
                AddAggregateFunctions(items);
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterQualify:
                AddColumns(items, zone.TablesInScope, zone.TvfAliasesInScope, cteSchemas);
                AddAggregateFunctions(items);
                AddWindowFunctions(items);
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterAssert:
                AddColumns(items, zone.TablesInScope, zone.TvfAliasesInScope, cteSchemas);
                AddScalarFunctions(items);
                AddAggregateFunctions(items);
                AddWindowFunctions(items);
                AddSchemaNames(items, SchemaSurfaces.Expression);
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.InsideDefineBlock:
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.InFunctionArguments:
                AddColumns(items, zone.TablesInScope, zone.TvfAliasesInScope, cteSchemas);
                AddScalarFunctions(items);
                AddAggregateFunctions(items);
                AddSchemaNames(items, SchemaSurfaces.Expression);
                break;

            case CompletionZoneKind.InsideOver:
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.InsideExtract:
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterDot:
                if (zone.TableQualifier is not null)
                {
                    // The <c>models.</c> call namespace is still a special
                    // bucket pre-S9 (model registry doesn't surface in the
                    // schema router yet). Everything else treats the
                    // qualifier as a schema and offers what's registered
                    // there: columns on a table alias, UDFs / procedures
                    // in that schema, and built-in functions when
                    // qualifier == "system".
                    if (string.Equals(zone.TableQualifier, "models", StringComparison.OrdinalIgnoreCase))
                    {
                        AddModels(items);
                        break;
                    }
                    AddQualifiedColumns(items, zone.TableQualifier, zone.TvfAliasesInScope, cteSchemas);
                    AddSchemaRoutines(items, zone.TableQualifier);
                }
                break;

            case CompletionZoneKind.AfterSetOperation:
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterCteAs:
                // `WITH foo AS |` — keyword-only zone offering the
                // materialization hint. The opening paren of the CTE body
                // is typed directly; we don't synthesize a "(" item because
                // bare punctuation isn't a useful completion offer.
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterCreate:
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterDrop:
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            // Model-runtime DDL: AfterEvict / AfterReset narrow to the
            // single follow-up keyword; the model-name zones surface
            // the IF EXISTS modifier plus every registered model's bare
            // name so the user can complete the target without typing
            // it from memory.
            case CompletionZoneKind.AfterEvict:
            case CompletionZoneKind.AfterReset:
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterDropModel:
            case CompletionZoneKind.AfterEvictModel:
            case CompletionZoneKind.AfterResetCalibration:
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                AddModelNames(items);
                break;

            case CompletionZoneKind.AfterCreateTableColumns:
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterDeclareType:
                // Type names only — the binding's variable is named, the
                // type is what the user is typing now. Constraints / default
                // values come later.
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterInsertInto:
                AddTables(items);
                break;

            case CompletionZoneKind.AfterInsertTable:
                // INSERT INTO t (...) — `t` is in scope but the FROM/JOIN
                // extractor doesn't see it. Pass null so columns from every
                // table are still surfaced; tightening this is a separate
                // task once INSERT/UPDATE scope extraction lands.
                AddColumns(items, tablesInScope: null);
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterUpdate:
                AddTables(items);
                break;

            case CompletionZoneKind.AfterUpdateSet:
                // Same caveat as AfterInsertTable: target table comes from
                // UPDATE rather than FROM, so leave columns un-scoped here.
                AddColumns(items, tablesInScope: null);
                AddScalarFunctions(items);
                AddSchemaNames(items, SchemaSurfaces.Expression);
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterDeleteFrom:
                AddTables(items);
                break;

            case CompletionZoneKind.AfterReturning:
                // Projection list of an INSERT / UPDATE / DELETE …
                // RETURNING. Target table is in scope via the augmented
                // tablesInScope walk (picks up UPDATE x / INSERT INTO x /
                // DELETE FROM x).
                AddColumns(items, zone.TablesInScope, zone.TvfAliasesInScope, cteSchemas);
                AddScalarFunctions(items);
                AddSchemaNames(items, SchemaSurfaces.Expression);
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterAlterTable:
                AddTables(items);
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterAlterTableAdd:
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterInto:
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterAs:
                // No schema-based completions — user is typing an alias name.
                break;

            // ───────────────────── Contextual identifier zones ─────────────────────

            case CompletionZoneKind.AfterTablesample:
                AddTablesampleMethods(items);
                break;

            case CompletionZoneKind.AfterTablesampleMethodArg:
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.InsideTablesampleArg:
                // No schema completions — the argument is a numeric literal (percentage or count).
                break;

            case CompletionZoneKind.InsideStringOrComment:
                // Cursor is inside a string or comment — suppress all completions
                // so e.g. ALTER doesn't get inserted while the user types a literal.
                break;

            case CompletionZoneKind.AfterCall:
                // CALL is permissive in DatumIngest: PlanCall lowers
                // `CALL fn(args)` to `SELECT fn(args)`, so procedures,
                // UDFs, and built-in scalar functions are all callable
                // through it. Surface every callable kind from the
                // search path as a bare name + drillable schemas for
                // the rest. Aggregates / window functions / TVFs are
                // excluded — they don't work standalone (need
                // FROM / GROUP BY / OVER context). Table-only schemas
                // (datum_catalog, information_schema) are also filtered
                // out — nothing scalar-CALLable lives there.
                AddProcedures(items);
                AddUdfs(items);
                AddScalarFunctions(items);
                AddSchemaNames(items, SchemaSurfaces.Call);
                break;
        }

        // Filter by prefix if the user has partially typed something.
        if (zone.Prefix is not null)
        {
            items.RemoveAll(item =>
                !item.Label.StartsWith(zone.Prefix, StringComparison.OrdinalIgnoreCase));
        }

        items.Sort((left, right) =>
        {
            int orderComparison = left.SortOrder.CompareTo(right.SortOrder);
            return orderComparison != 0
                ? orderComparison
                : string.Compare(left.Label, right.Label, StringComparison.OrdinalIgnoreCase);
        });

        return items.ToArray();
    }

    private void AddTables(List<CompletionItem> items)
    {
        // The manifest's Tables list now carries every schema-qualified
        // table from the live catalog (S1a renamed system_udfs →
        // system.udfs, the virtual providers always carried dotted
        // names, and CatalogManifestBuilder iterates every backend).
        // For each entry we offer two completions: the fully-qualified
        // form (system.udfs) and — when the table lives on a
        // search_path schema — the unqualified shortcut (udfs).
        HashSet<string> seenUnqualified = new(StringComparer.OrdinalIgnoreCase);
        foreach (TableSchemaEntry table in _manifest.Tables)
        {
            string columnSummary = string.Join(", ", table.Columns.Select(
                column => $"{column.Name}: {column.Kind}"));

            string insertText = SqlIdentifier.QuoteIfNeeded(table.Name);
            items.Add(new CompletionItem
            {
                Label = table.Name,
                Kind = CompletionItemKind.Table,
                Detail = $"Table ({table.Columns.Count} columns)",
                InsertText = insertText != table.Name ? insertText : null,
                Documentation = columnSummary,
                SortOrder = 0,
            });

            // Offer the bare table name as a separate completion when
            // the table's schema is on search_path. First-on-path wins
            // — collisions on later schemas don't add a duplicate
            // shortcut, mirroring the engine's resolution.
            int dot = table.Name.IndexOf('.');
            if (dot <= 0) continue;
            string schema = table.Name[..dot];
            string unqualified = table.Name[(dot + 1)..];
            if (!ContainsIgnoreCase(_manifest.SearchPath, schema)) continue;
            if (!seenUnqualified.Add(unqualified)) continue;

            items.Add(new CompletionItem
            {
                Label = unqualified,
                Kind = CompletionItemKind.Table,
                Detail = $"Table — resolves to {table.Name} via search_path",
                InsertText = SqlIdentifier.QuoteIfNeeded(unqualified) is { } quoted && quoted != unqualified
                    ? quoted
                    : null,
                Documentation = columnSummary,
                SortOrder = 1,
            });
        }
    }

    /// <summary>
    /// Adds column completions, filtered to <paramref name="tablesInScope"/>
    /// when the classifier extracted FROM/JOIN bindings. Three states:
    /// <list type="bullet">
    ///   <item><c>null</c> — caller didn't provide scope info; show every catalog table's columns (legacy behaviour, kept for callers we haven't migrated).</item>
    ///   <item>empty list — classifier saw no FROM/JOIN; suppress columns entirely so e.g. <c>SELECT abs(|)</c> doesn't dump the whole catalog into the popup.</item>
    ///   <item>non-empty list — only emit columns from the named tables/aliases (case-insensitive match against the manifest's table names).</item>
    /// </list>
    /// </summary>
    private void AddColumns(List<CompletionItem> items, IReadOnlyList<string>? tablesInScope)
    {
        AddColumns(items, tablesInScope, tvfAliases: null, cteSchemas: CteSchemaResolver.Empty);
    }

    private void AddColumns(
        List<CompletionItem> items,
        IReadOnlyList<string>? tablesInScope,
        IReadOnlyDictionary<string, string>? tvfAliases,
        CteSchemaResult cteSchemas)
    {
        // Empty list = "FROM scope was extracted and there's nothing in it" —
        // surfacing every catalog column would be the bug we're fixing.
        // Exception: an empty table scope with TVF aliases or CTE schemas
        // present means the only sources are TVFs / CTEs; we still want
        // those columns.
        bool tablesEmpty = tablesInScope is { Count: 0 };
        bool tvfEmpty = tvfAliases is null or { Count: 0 };
        bool cteEmpty = cteSchemas.Schemas.Count == 0;
        if (tablesEmpty && tvfEmpty && cteEmpty) return;

        foreach (TableSchemaEntry table in _manifest.Tables)
        {
            // The manifest stores fully-qualified names (`public.users`)
            // but the parser's scoped list usually carries the typed
            // form, which may be unqualified (`users`). Match either way.
            if (tablesInScope is not null && !MatchesAnyScopedName(tablesInScope, table.Name))
            {
                continue;
            }
            foreach (TableColumnEntry column in table.Columns)
            {
                string nullable = column.Nullable ? " (nullable)" : "";
                items.Add(new CompletionItem
                {
                    Label = column.Name,
                    Kind = CompletionItemKind.Column,
                    Detail = $"{column.Kind}{nullable} — {table.Name}",
                    SortOrder = 1,
                });
            }
        }

        AddTvfColumns(items, tvfAliases);
        AddCteColumns(items, tablesInScope, cteSchemas);
    }

    /// <summary>
    /// Appends columns from every CTE source in <paramref name="tablesInScope"/>.
    /// A CTE source surfaces when its name (or an alias bound to it) appears
    /// in the FROM/JOIN scope. Without this branch, references like
    /// <c>SELECT frame_index FROM frames</c> wouldn't get column completions
    /// because <c>frames</c> isn't a persistent table.
    /// </summary>
    private static void AddCteColumns(
        List<CompletionItem> items,
        IReadOnlyList<string>? tablesInScope,
        CteSchemaResult cteSchemas)
    {
        if (cteSchemas.Schemas.Count == 0) return;

        foreach (KeyValuePair<string, IReadOnlyList<TableColumnEntry>> cte in cteSchemas.Schemas)
        {
            // Only surface a CTE's columns when it (or an alias bound to
            // it) is actually in the FROM/JOIN scope at the cursor.
            // Otherwise typing inside one CTE's body would dump every
            // other CTE's projection into the popup.
            if (tablesInScope is not null && !CteVisibleInScope(cte.Key, tablesInScope, cteSchemas.FromAliasToCteName))
            {
                continue;
            }
            foreach (TableColumnEntry column in cte.Value)
            {
                string nullable = column.Nullable ? " (nullable)" : "";
                items.Add(new CompletionItem
                {
                    Label = column.Name,
                    Kind = CompletionItemKind.Column,
                    Detail = $"{column.Kind}{nullable} — {cte.Key}",
                    SortOrder = 1,
                });
            }
        }
    }

    /// <summary>
    /// True when <paramref name="cteName"/> is in scope — either referenced
    /// directly by name or via an alias that the CTE-collection pass bound
    /// to that name.
    /// </summary>
    private static bool CteVisibleInScope(
        string cteName,
        IReadOnlyList<string> tablesInScope,
        IReadOnlyDictionary<string, string> fromAliasToCte)
    {
        for (int i = 0; i < tablesInScope.Count; i++)
        {
            string scoped = tablesInScope[i];
            if (string.Equals(scoped, cteName, StringComparison.OrdinalIgnoreCase)) return true;
            if (fromAliasToCte.TryGetValue(scoped, out string? alias)
                && string.Equals(alias, cteName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Appends columns from every TVF source in <paramref name="tvfAliases"/>
    /// to the completion list. Mirrors the persistent-table column path —
    /// same shape, same SortOrder bucket so the two kinds interleave
    /// alphabetically in the popup. Skips TVFs whose manifest entry has
    /// no fixed output schema (the entry's column list is null) so
    /// completions don't promise columns the engine can't statically
    /// validate.
    /// </summary>
    private void AddTvfColumns(List<CompletionItem> items, IReadOnlyDictionary<string, string>? tvfAliases)
    {
        if (tvfAliases is null || tvfAliases.Count == 0) return;
        foreach (KeyValuePair<string, string> entry in tvfAliases)
        {
            FunctionSignature? signature = LookupTvfSignature(entry.Value);
            if (signature?.OutputColumns is null) continue;
            string sourceLabel = $"{entry.Key} ({signature.Name})";
            foreach (TableColumnEntry column in signature.OutputColumns)
            {
                string nullable = column.Nullable ? " (nullable)" : "";
                items.Add(new CompletionItem
                {
                    Label = column.Name,
                    Kind = CompletionItemKind.Column,
                    Detail = $"{column.Kind}{nullable} — {sourceLabel}",
                    SortOrder = 1,
                });
            }
        }
    }

    /// <summary>
    /// Resolves a dot qualifier (the <c>X</c> in <c>X.col</c>) against the
    /// CTE schema map. Matches a direct CTE name first, then a FROM alias
    /// bound to a CTE. <paramref name="cteName"/> receives the resolved CTE
    /// name so the completion popup can show it as the column source.
    /// </summary>
    private static bool TryGetCteColumns(
        string qualifier,
        CteSchemaResult cteSchemas,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? cteName,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IReadOnlyList<TableColumnEntry>? columns)
    {
        if (cteSchemas.Schemas.TryGetValue(qualifier, out IReadOnlyList<TableColumnEntry>? direct))
        {
            cteName = qualifier;
            columns = direct;
            return true;
        }
        if (cteSchemas.FromAliasToCteName.TryGetValue(qualifier, out string? mapped)
            && cteSchemas.Schemas.TryGetValue(mapped, out IReadOnlyList<TableColumnEntry>? aliasCols))
        {
            cteName = mapped;
            columns = aliasCols;
            return true;
        }
        cteName = null;
        columns = null;
        return false;
    }

    private FunctionSignature? LookupTvfSignature(string functionName)
    {
        foreach (FunctionSignature signature in _manifest.Functions)
        {
            if (!signature.IsTableValued) continue;
            if (string.Equals(signature.Name, functionName, StringComparison.OrdinalIgnoreCase))
            {
                return signature;
            }
        }
        return null;
    }

    private static bool ContainsIgnoreCase(IReadOnlyList<string> names, string target)
    {
        for (int i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], target, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    // Matches a manifest table's fully-qualified name (e.g. "public.users")
    // against any name in a parser-extracted scoped list. A scoped entry
    // matches exactly OR — when unqualified (no dot) — when it equals the
    // suffix after the schema separator. Returns true on the first match.
    private static bool MatchesAnyScopedName(IReadOnlyList<string> scopedNames, string qualifiedTableName)
    {
        int dotIndex = qualifiedTableName.IndexOf('.');
        string unqualified = dotIndex >= 0
            ? qualifiedTableName[(dotIndex + 1)..]
            : qualifiedTableName;

        for (int i = 0; i < scopedNames.Count; i++)
        {
            string scoped = scopedNames[i];
            if (string.Equals(scoped, qualifiedTableName, StringComparison.OrdinalIgnoreCase))
                return true;
            // Treat a scoped name with no dot as matching the manifest
            // entry's unqualified portion. The user typing `FROM users`
            // matches every schema's `users` until FROM-scope extraction
            // gains its own search-path awareness.
            if (!scoped.Contains('.') &&
                string.Equals(scoped, unqualified, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void AddQualifiedColumns(List<CompletionItem> items, string tableQualifier)
    {
        AddQualifiedColumns(items, tableQualifier, tvfAliases: null, cteSchemas: CteSchemaResolver.Empty);
    }

    private void AddQualifiedColumns(
        List<CompletionItem> items,
        string tableQualifier,
        IReadOnlyDictionary<string, string>? tvfAliases,
        CteSchemaResult cteSchemas)
    {
        // TVF aliases first — when the user typed `vid.`, we want the
        // popup to show the TVF's output columns rather than fall through
        // to a persistent-table lookup that wouldn't match anyway.
        if (tvfAliases is not null
            && tvfAliases.TryGetValue(tableQualifier, out string? functionName))
        {
            FunctionSignature? signature = LookupTvfSignature(functionName);
            if (signature?.OutputColumns is not null)
            {
                foreach (TableColumnEntry column in signature.OutputColumns)
                {
                    string nullable = column.Nullable ? " (nullable)" : "";
                    items.Add(new CompletionItem
                    {
                        Label = column.Name,
                        Kind = CompletionItemKind.Column,
                        Detail = $"{column.Kind}{nullable} — {signature.Name}",
                        SortOrder = 0,
                    });
                }
                return;
            }
        }

        // CTE aliases (and bare CTE names) next: `f1.frame_index` →
        // f1 ↦ frames ↦ frames' projected columns. Same precedence as TVFs
        // because a SELECT-list reference qualifies a row source, not a
        // schema. Schema-qualified table lookup is the natural fallback.
        if (TryGetCteColumns(tableQualifier, cteSchemas, out string? cteName, out IReadOnlyList<TableColumnEntry>? cteCols))
        {
            foreach (TableColumnEntry column in cteCols)
            {
                string nullable = column.Nullable ? " (nullable)" : "";
                items.Add(new CompletionItem
                {
                    Label = column.Name,
                    Kind = CompletionItemKind.Column,
                    Detail = $"{column.Kind}{nullable} — {cteName}",
                    SortOrder = 0,
                });
            }
            return;
        }

        // tableQualifier is what the user typed before the dot — could be
        // a schema-qualified name or just an unqualified table name.
        // Manifest stores tables fully-qualified, so accept either.
        TableSchemaEntry? table = _manifest.Tables.FirstOrDefault(
            entry => string.Equals(entry.Name, tableQualifier, StringComparison.OrdinalIgnoreCase));

        if (table is null && !tableQualifier.Contains('.'))
        {
            foreach (string schema in _manifest.SearchPath)
            {
                string qualified = $"{schema}.{tableQualifier}";
                table = _manifest.Tables.FirstOrDefault(
                    entry => string.Equals(entry.Name, qualified, StringComparison.OrdinalIgnoreCase));
                if (table is not null) break;
            }
        }

        if (table is null)
        {
            return;
        }

        foreach (TableColumnEntry column in table.Columns)
        {
            string nullable = column.Nullable ? " (nullable)" : "";
            items.Add(new CompletionItem
            {
                Label = column.Name,
                Kind = CompletionItemKind.Column,
                Detail = $"{column.Kind}{nullable}",
                SortOrder = 0,
            });
        }
    }

    /// <summary>
    /// Functions whose name starts with <c>__</c> are internal helpers
    /// generated by the planner. They're real entries in the function
    /// registry — and surface to introspection like
    /// <c>system_functions</c> — but should never appear in interactive
    /// completion because users can't usefully invoke them by name.
    /// </summary>
    private static bool IsInternalFunction(FunctionSignature function) =>
        function.Name.StartsWith("__", StringComparison.Ordinal);

    /// <summary>
    /// Surfaces procedures whose schema is on the search path as bare,
    /// callable names after the user types <c>CALL</c>. Procedures in
    /// non-search-path schemas need qualification — those show up via
    /// <see cref="AddSchemaNames"/> + the <see cref="CompletionZoneKind.AfterDot"/>
    /// drill-in path.
    /// </summary>
    private void AddProcedures(List<CompletionItem> items)
    {
        if (_manifest.Procedures is null) return;
        foreach (ProcedureEntry proc in _manifest.Procedures)
        {
            if (!ContainsIgnoreCase(_manifest.SearchPath, proc.SchemaName)) continue;
            items.Add(BuildProcedureCompletion(proc));
        }
    }

    /// <summary>
    /// Surfaces user-defined functions whose schema is on the search
    /// path as bare callable names. Used by the <c>CALL</c> popup:
    /// DatumIngest lowers <c>CALL udf.fn(args)</c> to
    /// <c>SELECT udf.fn(args)</c> (see <c>TableCatalog.PlanCall</c>), so
    /// UDFs are CALLable just like procedures. Without this, a user who
    /// runs <c>CREATE FUNCTION Test()</c> and then types <c>CALL </c>
    /// sees no completion for their own function.
    /// </summary>
    private void AddUdfs(List<CompletionItem> items)
    {
        if (_manifest.Udfs is null) return;
        foreach (UdfEntry udf in _manifest.Udfs)
        {
            if (!ContainsIgnoreCase(_manifest.SearchPath, udf.SchemaName)) continue;
            items.Add(BuildUdfCompletion(udf));
        }
    }

    /// <summary>
    /// Sources <see cref="AddSchemaNames"/> should consult when collecting
    /// schema names. Lets each completion zone ask for the schemas that
    /// actually host the kind of thing the zone surfaces — e.g.
    /// <see cref="CompletionZoneKind.AfterCall"/> wants <see cref="Procedures"/>
    /// only, not table or function schemas that have nothing to do with
    /// <c>CALL</c>.
    /// </summary>
    [Flags]
    private enum SchemaSurfaces
    {
        Functions  = 1 << 0,
        Procedures = 1 << 1,
        Tables     = 1 << 2,
        Models     = 1 << 3,

        /// <summary>Everything reachable from an expression position.</summary>
        Expression = Functions | Models,

        /// <summary>Everything reachable from a FROM / JOIN position.</summary>
        FromClause = Functions | Tables,

        /// <summary>
        /// Everything CALLable: procedures (CALL-canonical) plus
        /// functions and UDFs (DatumIngest lowers <c>CALL fn(args)</c>
        /// to <c>SELECT fn(args)</c>, so all scalar callables apply).
        /// Excludes tables — nothing is CALLable from a table-only
        /// schema like datum_catalog or information_schema.
        /// </summary>
        Call       = Procedures | Functions,
    }

    /// <summary>
    /// Surfaces distinct schema names from the manifest sources selected
    /// by <paramref name="include"/> as <see cref="CompletionItemKind.Schema"/>
    /// completions. Lets the user type <c>tok</c> and pick <c>tokenizer</c>,
    /// or <c>mod</c> and pick <c>models</c>, without knowing the full
    /// qualified call up front.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Search-path schemas (<c>system</c>) are <em>included</em> — the
    /// user can drill <c>system.</c> to discover the catalog-introspection
    /// tables and the explicitly-qualified function forms. Only
    /// <c>public</c> is filtered out, because it's the default-default
    /// and surfacing it would be noise (same identifiers are reachable
    /// unqualified).
    /// </para>
    /// <para>
    /// Inserts just the schema name (no trailing dot); the editor's own
    /// trigger-on-dot completion fires the qualified-completion zone
    /// once the user types <c>.</c> after acceptance. Sort order 3 puts
    /// these below scalar functions (2) and TVFs (1).
    /// </para>
    /// </remarks>
    private void AddSchemaNames(List<CompletionItem> items, SchemaSurfaces include)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        if (include.HasFlag(SchemaSurfaces.Functions))
        {
            // Built-in functions / TVFs / aggregates / window functions.
            foreach (FunctionSignature function in _manifest.Functions)
            {
                if (IsInternalFunction(function)) continue;
                string schema = function.SchemaName;
                if (!string.IsNullOrEmpty(schema)) seen.Add(schema);
            }

            // User-defined functions — same call shape as built-ins.
            if (_manifest.Udfs is not null)
            {
                foreach (UdfEntry udf in _manifest.Udfs)
                {
                    if (!string.IsNullOrEmpty(udf.SchemaName)) seen.Add(udf.SchemaName);
                }
            }
        }

        if (include.HasFlag(SchemaSurfaces.Procedures))
        {
            if (_manifest.Procedures is not null)
            {
                foreach (ProcedureEntry proc in _manifest.Procedures)
                {
                    if (!string.IsNullOrEmpty(proc.SchemaName)) seen.Add(proc.SchemaName);
                }
            }
        }

        if (include.HasFlag(SchemaSurfaces.Tables))
        {
            // Tables (schema-qualified entries surface their schema —
            // <c>system.tables</c>, <c>datum_catalog.functions</c>, etc.).
            foreach (TableSchemaEntry table in _manifest.Tables)
            {
                int dot = table.Name.IndexOf('.');
                if (dot > 0) seen.Add(table.Name[..dot]);
            }
        }

        if (include.HasFlag(SchemaSurfaces.Models))
        {
            // Models always live in the `models` namespace — surface it
            // whenever there's at least one registered so the user can drill
            // `models.<name>` even without typing the full model name first.
            if (_manifest.Models is not null && _manifest.Models.Count > 0)
            {
                seen.Add("models");
            }
        }

        // `public` is the default-default schema. Some zones surface its
        // contents unqualified (where it adds nothing) but in zones with
        // restrictive filters — CALL surfacing only procedures /
        // callables — the user's own `CREATE FUNCTION` lands in public
        // and they expect public to be drillable too. List it.

        foreach (string schema in seen)
        {
            items.Add(new CompletionItem
            {
                Label = schema,
                Kind = CompletionItemKind.Schema,
                Detail = $"[schema] {schema}.*",
                InsertText = schema,
                SortOrder = 3,
            });
        }
    }

    private void AddScalarFunctions(List<CompletionItem> items)
    {
        foreach (FunctionSignature function in _manifest.Functions)
        {
            // Exclude every non-scalar kind. Aggregates, window functions,
            // and TVFs each have their own emitter (AddAggregateFunctions
            // / AddWindowFunctions / AddTableValuedFunctions) — zones
            // that want them call those explicitly. Without this filter
            // SUM, ARRAY_AGG, ROW_NUMBER, RANGE etc. leak into every
            // expression / CALL / WHERE / ON popup as duplicates that
            // surface in contexts where they aren't legal standalone.
            if (function.IsTableValued
                || function.IsAggregate
                || function.IsWindowFunction
                || IsInternalFunction(function))
            {
                continue;
            }
            // Only surface unqualified names for functions whose schema is
            // on the search path. Functions in inference / tokenizer /
            // templates require the user to qualify; surfacing them bare
            // would suggest a name that doesn't actually resolve.
            if (!ContainsIgnoreCase(_manifest.SearchPath, function.SchemaName))
            {
                continue;
            }

            string parameters = string.Join(", ", function.Parameters.Select(FormatParameter));
            string signature = $"{function.Name}({parameters})";
            string returnInfo = function.ReturnType is not null ? $" → {function.ReturnType}" : "";

            items.Add(new CompletionItem
            {
                Label = function.Name,
                Kind = CompletionItemKind.Function,
                Detail = $"[{function.Category}] {signature}{returnInfo}",
                InsertText = $"{function.Name}(",
                Documentation = EnrichFunctionDoc(function.Name),
                SortOrder = 2,
            });
        }
    }

    private void AddTableValuedFunctions(List<CompletionItem> items)
    {
        foreach (FunctionSignature function in _manifest.Functions)
        {
            if (!function.IsTableValued || IsInternalFunction(function))
            {
                continue;
            }
            // Same search-path filter as scalar functions — qualified-only
            // TVFs (inference.onnx_inspect, inference.devices, ...) should
            // not surface as bare names.
            if (!ContainsIgnoreCase(_manifest.SearchPath, function.SchemaName))
            {
                continue;
            }

            string parameters = string.Join(", ", function.Parameters.Select(FormatParameter));
            string signature = $"{function.Name}({parameters})";

            items.Add(new CompletionItem
            {
                Label = function.Name,
                Kind = CompletionItemKind.Function,
                Detail = $"[{function.Category}] Table function: {signature}",
                InsertText = $"{function.Name}(",
                Documentation = EnrichFunctionDoc(function.Name),
                SortOrder = 1,
            });
        }
    }

    /// <summary>
    /// Surfaces every model registered in the catalog's <c>ModelCatalog</c> as
    /// a <c>models.&lt;name&gt;(...)</c> call-site suggestion. Items insert
    /// just the bare name (the leading <c>models.</c> is already in the
    /// buffer); the editor follows up with an open-paren via the user's
    /// next keystroke.
    /// </summary>
    private void AddModels(List<CompletionItem> items)
    {
        if (_manifest.Models is null) return;

        foreach (ModelEntry model in _manifest.Models)
        {
            string parameters = model.Parameters is null
                ? ""
                : string.Join(", ", model.Parameters.Select(FormatParameter));
            string signature = $"models.{model.Name}({parameters})";
            string returnInfo = $" → {model.OutputKind ?? "?"}";
            string detail = model.Category is not null
                ? $"[{model.Category}] {signature}{returnInfo}"
                : $"{signature}{returnInfo}";

            string? doc = model.DisplayName is not null && model.Backend is not null
                ? $"{model.DisplayName} ({model.Backend})"
                : model.DisplayName ?? (model.Backend is not null ? $"backend: {model.Backend}" : null);

            items.Add(new CompletionItem
            {
                Label = model.Name,
                Kind = CompletionItemKind.Function,
                Detail = detail,
                InsertText = $"{model.Name}(",
                Documentation = doc,
                SortOrder = 1,
            });
        }
    }

    /// <summary>
    /// Surfaces every registered model as a bare-identifier suggestion
    /// (no <c>models.</c> prefix, no trailing paren). Used by the
    /// model-runtime DDL zones (<c>DROP MODEL</c> / <c>EVICT MODEL</c> /
    /// <c>RESET CALIBRATION</c>) where the model name is the literal
    /// argument to the statement, not a call site. Distinct from
    /// <see cref="AddModels"/> which generates <c>models.&lt;name&gt;(</c>
    /// call-site insertions for SELECT/projection contexts.
    /// </summary>
    private void AddModelNames(List<CompletionItem> items)
    {
        if (_manifest.Models is null) return;

        foreach (ModelEntry model in _manifest.Models)
        {
            string detail = model.Category is not null
                ? $"[{model.Category}] models.{model.Name}"
                : $"models.{model.Name}";

            string? doc = model.DisplayName is not null && model.Backend is not null
                ? $"{model.DisplayName} ({model.Backend})"
                : model.DisplayName ?? (model.Backend is not null ? $"backend: {model.Backend}" : null);

            items.Add(new CompletionItem
            {
                Label = model.Name,
                Kind = CompletionItemKind.Variable,
                Detail = detail,
                InsertText = model.Name,
                Documentation = doc,
                SortOrder = 1,
            });
        }
    }

    /// <summary>
    /// Surfaces UDFs, procedures, and (when <paramref name="schema"/> is
    /// <c>system</c>) built-in functions registered in
    /// <paramref name="schema"/> after the user types
    /// <c>{schema}.</c>. Procedures get a <c>[procedure]</c> tag so users
    /// see them as CALL-only targets distinct from regular functions.
    /// </summary>
    private void AddSchemaRoutines(List<CompletionItem> items, string schema)
    {
        if (_manifest.Udfs is not null)
        {
            foreach (UdfEntry udf in _manifest.Udfs)
            {
                if (!string.Equals(udf.SchemaName, schema, StringComparison.OrdinalIgnoreCase)) continue;
                items.Add(BuildUdfCompletion(udf));
            }
        }

        if (_manifest.Procedures is not null)
        {
            foreach (ProcedureEntry proc in _manifest.Procedures)
            {
                if (!string.Equals(proc.SchemaName, schema, StringComparison.OrdinalIgnoreCase)) continue;
                items.Add(BuildProcedureCompletion(proc));
            }
        }

        // Built-in scalar / aggregate / window / table-valued functions
        // live in whichever schema they were registered under — system,
        // inference, tokenizer, templates, … — so filter by SchemaName
        // rather than hardcoding "system". Without this, qualified
        // completions like `inference.` / `tokenizer.` would surface
        // nothing for built-in functions.
        foreach (FunctionSignature function in _manifest.Functions)
        {
            if (IsInternalFunction(function)) continue;
            if (!string.Equals(function.SchemaName, schema, StringComparison.OrdinalIgnoreCase)) continue;
            items.Add(BuildBuiltinCompletion(function));
        }
    }

    private static CompletionItem BuildUdfCompletion(UdfEntry udf)
    {
        string parameters = udf.Parameters is null
            ? ""
            : string.Join(", ", udf.Parameters.Select(FormatParameter));
        string returnInfo = udf.ReturnType is not null ? $" → {udf.ReturnType}" : "";

        // Detail line: "[procedural pure] schema.foo(@x INT32) → STRING"
        // Body kind comes first so the eye lands on the operational hint.
        List<string> tags = new(2);
        if (udf.BodyKind is not null) tags.Add(udf.BodyKind);
        if (udf.IsPure) tags.Add("pure");
        string tagPrefix = tags.Count > 0 ? $"[{string.Join(' ', tags)}] " : "";

        return new CompletionItem
        {
            Label = udf.Name,
            Kind = CompletionItemKind.Function,
            Detail = $"{tagPrefix}{udf.SchemaName}.{udf.Name}({parameters}){returnInfo}",
            InsertText = $"{udf.Name}(",
            SortOrder = 1,
        };
    }

    private static CompletionItem BuildProcedureCompletion(ProcedureEntry proc)
    {
        string parameters = proc.Parameters is null
            ? ""
            : string.Join(", ", proc.Parameters.Select(FormatParameter));

        // Procedures require CALL, surfaced via the [procedure] tag so
        // users don't mistake them for SELECT-callable functions.
        return new CompletionItem
        {
            Label = proc.Name,
            Kind = CompletionItemKind.Function,
            Detail = $"[procedure] {proc.SchemaName}.{proc.Name}({parameters})",
            InsertText = $"{proc.Name}(",
            SortOrder = 1,
        };
    }

    private static CompletionItem BuildBuiltinCompletion(FunctionSignature function)
    {
        string parameters = string.Join(", ", function.Parameters.Select(FormatParameter));
        string returnInfo = function.ReturnType is not null ? $" → {function.ReturnType}" : "";
        return new CompletionItem
        {
            Label = function.Name,
            Kind = CompletionItemKind.Function,
            Detail = $"{function.SchemaName}.{function.Name}({parameters}){returnInfo}",
            InsertText = $"{function.Name}(",
            SortOrder = 1,
        };
    }

    private void AddAggregateFunctions(List<CompletionItem> items)
    {
        foreach (FunctionSignature function in _manifest.Functions)
        {
            if (!function.IsAggregate || IsInternalFunction(function))
            {
                continue;
            }

            string parameters = string.Join(", ", function.Parameters.Select(FormatParameter));
            string signature = $"{function.Name}({parameters})";
            string returnInfo = function.ReturnType is not null ? $" → {function.ReturnType}" : "";

            items.Add(new CompletionItem
            {
                Label = function.Name,
                Kind = CompletionItemKind.Function,
                Detail = $"[{function.Category}] {signature}{returnInfo}",
                InsertText = $"{function.Name}(",
                Documentation = EnrichFunctionDoc(function.Name),
                SortOrder = 2,
            });
        }
    }

    private void AddWindowFunctions(List<CompletionItem> items)
    {
        foreach (FunctionSignature function in _manifest.Functions)
        {
            if (!function.IsWindowFunction || IsInternalFunction(function))
            {
                continue;
            }

            string parameters = string.Join(", ", function.Parameters.Select(FormatParameter));
            string signature = $"{function.Name}({parameters})";
            string returnInfo = function.ReturnType is not null ? $" → {function.ReturnType}" : "";

            items.Add(new CompletionItem
            {
                Label = function.Name,
                Kind = CompletionItemKind.Function,
                Detail = $"[{function.Category}] {signature}{returnInfo}",
                InsertText = $"{function.Name}(",
                Documentation = EnrichFunctionDoc(function.Name),
                SortOrder = 2,
            });
        }
    }

    private static void AddKeywords(List<CompletionItem> items, IReadOnlyList<string> keywords)
    {
        foreach (string keyword in keywords)
        {
            items.Add(new CompletionItem
            {
                Label = keyword,
                Kind = CompletionItemKind.Keyword,
                Documentation = EnrichKeywordDoc(keyword),
                SortOrder = 3,
            });
        }
    }

    /// <summary>
    /// Whether <paramref name="kind"/> is a zone where references to
    /// procedural variables (<c>@var</c>) are legal. Predicates / scalar
    /// expression contexts qualify; pure structural zones (FROM, INTO,
    /// AS, AfterDot, etc.) do not. The list is intentionally permissive:
    /// we'd rather surface a variable that won't help than hide one the
    /// user wanted.
    /// </summary>
    private static bool ZoneAcceptsVariableReferences(CompletionZoneKind kind) => kind switch
    {
        CompletionZoneKind.Expression => true,
        CompletionZoneKind.ProceduralExpression => true,
        CompletionZoneKind.AfterSelect => true,
        CompletionZoneKind.AfterWhere => true,
        CompletionZoneKind.AfterOn => true,
        CompletionZoneKind.AfterHaving => true,
        CompletionZoneKind.AfterQualify => true,
        CompletionZoneKind.AfterAssert => true,
        CompletionZoneKind.AfterOrderBy => true,
        CompletionZoneKind.AfterGroupBy => true,
        CompletionZoneKind.InFunctionArguments => true,
        CompletionZoneKind.AfterUpdateSet => true,
        CompletionZoneKind.AfterInsertTable => true,
        CompletionZoneKind.AfterReturning => true,
        _ => false,
    };

    /// <summary>
    /// Surfaces procedural <c>@var</c> bindings declared earlier in the
    /// fragment as completion items. Sort order 0 keeps them at the top
    /// of expression-context popups — when the user types `@x`, they
    /// almost certainly want a binding match before any column or
    /// function alphabetically nearby.
    /// </summary>
    private static void AddVariablesInScope(
        List<CompletionItem> items, IReadOnlyList<string>? variables)
    {
        if (variables is null) return;
        foreach (string name in variables)
        {
            items.Add(new CompletionItem
            {
                Label = name,
                Kind = CompletionItemKind.Variable,
                InsertText = name,
                Detail = "procedural variable",
                SortOrder = 0,
            });
        }
    }

    /// <summary>
    /// Returns a documentation excerpt and "See more" link from the embedded docs
    /// for the given function name, or null if no matching section exists.
    /// </summary>
    private static string? EnrichFunctionDoc(string functionName)
    {
        string? sectionKey = DocumentationIndex.Instance.FindFunctionSection(functionName);
        if (sectionKey is null)
        {
            return null;
        }

        DocumentationSection? section = DocumentationIndex.Instance.TryGetSection(sectionKey);
        if (section is null)
        {
            return null;
        }

        string encodedKey = Uri.EscapeDataString($"\"{sectionKey}\"");
        return string.IsNullOrEmpty(section.Excerpt)
            ? $"[See more](command:datumingest.openDoc?{encodedKey})"
            : $"{section.Excerpt}\n\n[See more](command:datumingest.openDoc?{encodedKey})";
    }

    /// <summary>
    /// Returns a documentation excerpt and "See more" link from the embedded docs
    /// for the given SQL keyword, or null if no matching section exists.
    /// For compound keywords like "GROUP BY", tries the full phrase first, then
    /// the first word (e.g. "GROUP").
    /// </summary>
    private static string? EnrichKeywordDoc(string keyword)
    {
        string? sectionKey = DocumentationIndex.Instance.FindKeywordSection(keyword);

        // For compound keywords like "CROSS VALIDATE", "LEFT JOIN", etc.,
        // fall back to the first word if the full phrase has no match.
        if (sectionKey is null && keyword.Contains(' '))
        {
            string firstWord = keyword[..keyword.IndexOf(' ')];
            sectionKey = DocumentationIndex.Instance.FindKeywordSection(firstWord);
        }

        if (sectionKey is null)
        {
            return null;
        }

        DocumentationSection? section = DocumentationIndex.Instance.TryGetSection(sectionKey);
        if (section is null)
        {
            return null;
        }

        string encodedKey = Uri.EscapeDataString($"\"{sectionKey}\"");
        return string.IsNullOrEmpty(section.Excerpt)
            ? $"[See more](command:datumingest.openDoc?{encodedKey})"
            : $"{section.Excerpt}\n\n[See more](command:datumingest.openDoc?{encodedKey})";
    }

    private static string FormatParameter(ParameterSignature parameter)
    {
        string optional = parameter.IsOptional ? "?" : "";
        return $"{parameter.Name}: {parameter.Kind}{optional}";
    }


    /// <summary>
    /// Adds TABLESAMPLE method names as contextual keyword completions with
    /// documentation explaining each method's semantics and syntax.
    /// </summary>
    private static void AddTablesampleMethods(List<CompletionItem> items)
    {
        items.Add(new CompletionItem
        {
            Label = "BERNOULLI",
            Kind = CompletionItemKind.Keyword,
            Detail = "Row-level probabilistic sampling",
            InsertText = "BERNOULLI(",
            Documentation = "Each row is independently included with the given probability.\n" +
                "Syntax: `TABLESAMPLE BERNOULLI(percentage) [REPEATABLE(seed)]`",
            SortOrder = 0,
        });
        items.Add(new CompletionItem
        {
            Label = "SYSTEM",
            Kind = CompletionItemKind.Keyword,
            Detail = "Chunk-level sampling",
            InsertText = "SYSTEM(",
            Documentation = "Entire chunks/pages are included or excluded.\n" +
                "Syntax: `TABLESAMPLE SYSTEM(percentage) [REPEATABLE(seed)]`",
            SortOrder = 0,
        });
        items.Add(new CompletionItem
        {
            Label = "STRATIFIED",
            Kind = CompletionItemKind.Keyword,
            Detail = "Per-class proportional sampling (preserves distribution)",
            InsertText = "STRATIFIED(",
            Documentation = "Samples each class at the same rate, preserving class proportions.\n" +
                "Syntax: `TABLESAMPLE STRATIFIED(percentage) ON column [REPEATABLE(seed)]`",
            SortOrder = 0,
        });
        items.Add(new CompletionItem
        {
            Label = "BALANCED",
            Kind = CompletionItemKind.Keyword,
            Detail = "Per-class fixed-count sampling (equalizes distribution)",
            InsertText = "BALANCED(",
            Documentation = "Returns exactly N rows per class via reservoir sampling.\n" +
                "Syntax: `TABLESAMPLE BALANCED(count) ON column [REPEATABLE(seed)]`",
            SortOrder = 0,
        });
    }

}
