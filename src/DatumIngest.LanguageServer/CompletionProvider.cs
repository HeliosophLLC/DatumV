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
                AddColumns(items, zone.TablesInScope);
                AddScalarFunctions(items);
                AddAggregateFunctions(items);
                AddWindowFunctions(items);
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterFrom:
            case CompletionZoneKind.AfterJoin:
                AddTables(items);
                AddTableValuedFunctions(items);
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterFromSource:
            case CompletionZoneKind.AfterJoinSource:
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterWhere:
                AddColumns(items, zone.TablesInScope);
                AddScalarFunctions(items);
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterOn:
                AddColumns(items, zone.TablesInScope);
                AddScalarFunctions(items);
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.Expression:
                AddColumns(items, zone.TablesInScope);
                AddScalarFunctions(items);
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.ProceduralExpression:
                // Procedural top level has no row context, so columns aren't
                // in scope — only @vars (not yet in the manifest), scalar
                // functions, and literals/keywords. Without this branch the
                // user typing `IF b…` would see column names like `backend`
                // from `system_models` even though there's no FROM in scope.
                AddScalarFunctions(items);
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterOrderBy:
                AddColumns(items, zone.TablesInScope);
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterGroupBy:
                AddColumns(items, zone.TablesInScope);
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterHaving:
                AddColumns(items, zone.TablesInScope);
                AddAggregateFunctions(items);
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterQualify:
                AddColumns(items, zone.TablesInScope);
                AddAggregateFunctions(items);
                AddWindowFunctions(items);
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterAssert:
                AddColumns(items, zone.TablesInScope);
                AddScalarFunctions(items);
                AddAggregateFunctions(items);
                AddWindowFunctions(items);
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.InsideDefineBlock:
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.InFunctionArguments:
                AddColumns(items, zone.TablesInScope);
                AddScalarFunctions(items);
                AddAggregateFunctions(items);
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
                    // Reserved namespaces (`models.X`, `udf.X`, future
                    // `proc.X` / `tasks.X`) are dispatched ahead of the
                    // table-column lookup. The qualifier match is
                    // case-insensitive to mirror SQL's keyword conventions.
                    if (string.Equals(zone.TableQualifier, "models", StringComparison.OrdinalIgnoreCase))
                    {
                        AddModels(items);
                        break;
                    }
                    if (string.Equals(zone.TableQualifier, "udf", StringComparison.OrdinalIgnoreCase))
                    {
                        AddUdfs(items);
                        break;
                    }
                    AddQualifiedColumns(items, zone.TableQualifier);
                }
                break;

            case CompletionZoneKind.AfterSetOperation:
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterCreate:
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterDrop:
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
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
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterDeleteFrom:
                AddTables(items);
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
        // Empty list = "FROM scope was extracted and there's nothing in it" —
        // surfacing every catalog column would be the bug we're fixing.
        if (tablesInScope is { Count: 0 }) return;

        foreach (TableSchemaEntry table in _manifest.Tables)
        {
            if (tablesInScope is not null && !ContainsIgnoreCase(tablesInScope, table.Name))
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

    private void AddQualifiedColumns(List<CompletionItem> items, string tableQualifier)
    {
        TableSchemaEntry? table = _manifest.Tables.FirstOrDefault(
            entry => string.Equals(entry.Name, tableQualifier, StringComparison.OrdinalIgnoreCase));

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
    /// generated by the planner (e.g. <c>__assert_not_null</c> wrapping
    /// <c>IS NOT NULL</c> parameter checks). They're real entries in the
    /// function registry — and surface to introspection like
    /// <c>system_functions</c> — but should never appear in interactive
    /// completion because users can't usefully invoke them by name.
    /// </summary>
    private static bool IsInternalFunction(FunctionSignature function) =>
        function.Name.StartsWith("__", StringComparison.Ordinal);

    private void AddScalarFunctions(List<CompletionItem> items)
    {
        foreach (FunctionSignature function in _manifest.Functions)
        {
            if (function.IsTableValued || IsInternalFunction(function))
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
    /// Surfaces every catalog UDF after the user types <c>udf.</c> Mirrors
    /// <see cref="AddModels"/> in shape; <see cref="UdfEntry.BodyKind"/> and
    /// <see cref="UdfEntry.IsPure"/> become hint text in the popup detail
    /// line so users can see at a glance whether they're invoking a macro
    /// (inlined) or a procedural body (per-row).
    /// </summary>
    private void AddUdfs(List<CompletionItem> items)
    {
        if (_manifest.Udfs is null) return;

        foreach (UdfEntry udf in _manifest.Udfs)
        {
            string parameters = udf.Parameters is null
                ? ""
                : string.Join(", ", udf.Parameters.Select(FormatParameter));
            string signature = $"udf.{udf.Name}({parameters})";
            string returnInfo = udf.ReturnType is not null ? $" → {udf.ReturnType}" : "";

            // Detail line: "[procedural pure] udf.foo(@x INT32) → STRING"
            // Body kind comes first so the eye lands on the most important
            // operational hint (per-row vs inlined). Empty when no kind.
            List<string> tags = new(2);
            if (udf.BodyKind is not null) tags.Add(udf.BodyKind);
            if (udf.IsPure) tags.Add("pure");
            string tagPrefix = tags.Count > 0 ? $"[{string.Join(' ', tags)}] " : "";

            items.Add(new CompletionItem
            {
                Label = udf.Name,
                Kind = CompletionItemKind.Function,
                Detail = $"{tagPrefix}{signature}{returnInfo}",
                InsertText = $"{udf.Name}(",
                SortOrder = 1,
            });
        }
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
