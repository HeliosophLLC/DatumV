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

        switch (zone.Kind)
        {
            case CompletionZoneKind.StatementStart:
                AddKeywords(items, ["SELECT", "WITH", "CREATE", "INSERT", "UPDATE", "DELETE", "DROP", "ALTER", "ANALYZE"]);
                break;

            case CompletionZoneKind.AfterSelect:
                AddColumns(items, allTables: true);
                AddScalarFunctions(items);
                AddAggregateFunctions(items);
                AddWindowFunctions(items);
                AddKeywords(items, ["FROM", "AS", "CAST", "CASE", "LET", "SCAN", "ASSERT", "DEFINE", "DISTINCT", "WITHIN GROUP"]);
                break;

            case CompletionZoneKind.AfterFrom:
            case CompletionZoneKind.AfterJoin:
                AddTables(items);
                AddTableValuedFunctions(items);
                AddKeywords(items, ["UNION", "INTERSECT", "EXCEPT"]);
                break;

            case CompletionZoneKind.AfterWhere:
            case CompletionZoneKind.AfterOn:
            case CompletionZoneKind.Expression:
                AddColumns(items, allTables: true);
                AddScalarFunctions(items);
                AddKeywords(items, ExpressionKeywords);
                break;

            case CompletionZoneKind.AfterOrderBy:
                AddColumns(items, allTables: true);
                AddKeywords(items, ["ASC", "DESC", "UNION", "INTERSECT", "EXCEPT"]);
                break;

            case CompletionZoneKind.AfterGroupBy:
                AddColumns(items, allTables: true);
                AddKeywords(items, ["ALL"]);
                break;

            case CompletionZoneKind.AfterHaving:
                AddColumns(items, allTables: true);
                AddAggregateFunctions(items);
                AddKeywords(items, ExpressionKeywords);
                break;

            case CompletionZoneKind.AfterQualify:
                AddColumns(items, allTables: true);
                AddAggregateFunctions(items);
                AddWindowFunctions(items);
                AddKeywords(items, ExpressionKeywords);
                break;

            case CompletionZoneKind.AfterAssert:
                AddColumns(items, allTables: true);
                AddScalarFunctions(items);
                AddAggregateFunctions(items);
                AddWindowFunctions(items);
                AddKeywords(items, [.. ExpressionKeywords, "MESSAGE", "ON FAIL"]);
                break;

            case CompletionZoneKind.InsideDefineBlock:
                AddKeywords(items, ["LET", "ASSERT", "}"]);
                break;

            case CompletionZoneKind.InFunctionArguments:
                AddColumns(items, allTables: true);
                AddScalarFunctions(items);
                AddAggregateFunctions(items);
                break;

            case CompletionZoneKind.InsideOver:
                AddKeywords(items, ["PARTITION BY", "ORDER BY", "ROWS BETWEEN"]);
                break;

            case CompletionZoneKind.AfterDot:
                if (zone.TableQualifier is not null)
                {
                    AddQualifiedColumns(items, zone.TableQualifier);
                }
                break;

            case CompletionZoneKind.AfterSetOperation:
                AddKeywords(items, ["ALL", "SELECT"]);
                break;

            case CompletionZoneKind.AfterCreate:
                AddKeywords(items, ["TEMP", "TEMPORARY", "TABLE", "INDEX"]);
                break;

            case CompletionZoneKind.AfterDrop:
                AddKeywords(items, ["TABLE", "INDEX", "IF EXISTS"]);
                break;

            case CompletionZoneKind.AfterCreateTableColumns:
                AddKeywords(items, ColumnTypeKeywords);
                AddKeywords(items, ["PRIMARY KEY", "NOT NULL", "DEFAULT"]);
                break;

            case CompletionZoneKind.AfterInsertInto:
                AddTables(items);
                break;

            case CompletionZoneKind.AfterInsertTable:
                AddColumns(items, allTables: true);
                AddKeywords(items, ["VALUES", "SELECT"]);
                break;

            case CompletionZoneKind.AfterUpdate:
                AddTables(items);
                break;

            case CompletionZoneKind.AfterUpdateSet:
                AddColumns(items, allTables: true);
                AddScalarFunctions(items);
                AddKeywords(items, ["WHERE", "FROM"]);
                break;

            case CompletionZoneKind.AfterDeleteFrom:
                AddTables(items);
                break;

            case CompletionZoneKind.AfterAlterTable:
                AddTables(items);
                AddKeywords(items, ["ADD"]);
                break;

            case CompletionZoneKind.AfterAlterTableAdd:
                AddKeywords(items, ["COLUMN"]);
                AddKeywords(items, ColumnTypeKeywords);
                AddKeywords(items, ["NOT NULL", "DEFAULT"]);
                break;

            case CompletionZoneKind.AfterInto:
            case CompletionZoneKind.AfterAs:
                // No schema-based completions for file paths or alias names.
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

    private void AddScalarFunctions(List<CompletionItem> items)
    {
        foreach (FunctionSignature function in _manifest.Functions)
        {
            if (function.IsTableValued)
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
                Documentation = function.Description,
                SortOrder = 2,
            });
        }
    }

    private void AddTableValuedFunctions(List<CompletionItem> items)
    {
        foreach (FunctionSignature function in _manifest.Functions)
        {
            if (!function.IsTableValued)
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
                Documentation = function.Description,
                SortOrder = 1,
            });
        }
    }

    private void AddAggregateFunctions(List<CompletionItem> items)
    {
        foreach (FunctionSignature function in _manifest.Functions)
        {
            if (!function.IsAggregate)
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
                Documentation = function.Description,
                SortOrder = 2,
            });
        }
    }

    private void AddWindowFunctions(List<CompletionItem> items)
    {
        foreach (FunctionSignature function in _manifest.Functions)
        {
            if (!function.IsWindowFunction)
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
                Documentation = function.Description,
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
                SortOrder = 3,
            });
        }
    }

    private static string FormatParameter(ParameterSignature parameter)
    {
        string optional = parameter.IsOptional ? "?" : "";
        return $"{parameter.Name}: {parameter.Kind}{optional}";
    }

    private static readonly string[] ExpressionKeywords =
    [
        "AND", "OR", "NOT", "IN", "BETWEEN", "LIKE",
        "IS", "NULL", "TRUE", "FALSE", "CAST", "CASE", "EXISTS", "DISTINCT",
        "AT TIME ZONE",
    ];

    internal static readonly string[] ColumnTypeKeywords =
    [
        "Boolean", "Int8", "Int16", "Int32", "Int64",
        "UInt8", "UInt16", "UInt32", "UInt64",
        "Float32", "Float64",
        "String", "Date", "DateTime", "Time", "Duration",
        "Uuid", "JsonValue", "Vector", "Matrix", "Tensor",
        "Array", "Struct", "Image", "UInt8Array",
        "Type",
    ];
}
