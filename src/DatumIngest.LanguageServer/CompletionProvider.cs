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
                AddColumns(items, allTables: true);
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
                AddColumns(items, allTables: true);
                AddScalarFunctions(items);
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterOn:
                AddColumns(items, allTables: true);
                AddScalarFunctions(items);
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.Expression:
                AddColumns(items, allTables: true);
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
                AddColumns(items, allTables: true);
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterGroupBy:
                AddColumns(items, allTables: true);
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterHaving:
                AddColumns(items, allTables: true);
                AddAggregateFunctions(items);
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterQualify:
                AddColumns(items, allTables: true);
                AddAggregateFunctions(items);
                AddWindowFunctions(items);
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterAssert:
                AddColumns(items, allTables: true);
                AddScalarFunctions(items);
                AddAggregateFunctions(items);
                AddWindowFunctions(items);
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.InsideDefineBlock:
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.InFunctionArguments:
                AddColumns(items, allTables: true);
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
                    // Reserved namespaces (`models.X`, future `udf.X` /
                    // `proc.X` / `tasks.X`) are dispatched ahead of the
                    // table-column lookup. The qualifier match is
                    // case-insensitive to mirror SQL's keyword conventions.
                    if (string.Equals(zone.TableQualifier, "models", StringComparison.OrdinalIgnoreCase))
                    {
                        AddModels(items);
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
                AddColumns(items, allTables: true);
                AddKeywords(items, KeywordRegistry.GetKeywords(zone.Kind));
                break;

            case CompletionZoneKind.AfterUpdate:
                AddTables(items);
                break;

            case CompletionZoneKind.AfterUpdateSet:
                AddColumns(items, allTables: true);
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
        }

        // Offer built-in virtual schema tables (e.g. information_schema.tables).
        foreach ((string schemaName, string[] tableNames) in VirtualSchemaTables)
        {
            foreach (string tableName in tableNames)
            {
                string qualifiedName = $"{schemaName}.{tableName}";
                items.Add(new CompletionItem
                {
                    Label = qualifiedName,
                    Kind = CompletionItemKind.Table,
                    Detail = $"Virtual table ({schemaName})",
                    InsertText = qualifiedName,
                    SortOrder = 2,
                });
            }
        }
    }

    private static readonly (string SchemaName, string[] TableNames)[] VirtualSchemaTables =
    [
        ("information_schema", ["tables", "columns", "schemata"]),
        ("datum_catalog", ["providers", "functions", "function_parameters", "statistics", "indexes", "interactions"]),
    ];

    private void AddColumns(List<CompletionItem> items, bool allTables)
    {
        foreach (TableSchemaEntry table in _manifest.Tables)
        {
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
