namespace DatumIngest.LanguageServer;

using DatumIngest.Manifest;
using DatumIngest.Parsing.Ast;

/// <summary>
/// Walks a parsed AST and produces <see cref="DiagnosticSeverity.Warning"/>
/// diagnostics for unknown tables, columns, and functions based on the
/// <see cref="LanguageServerManifest"/>. Operates entirely on the pre-built
/// manifest — no runtime I/O required.
/// </summary>
internal sealed class SemanticAnalyzer
{
    private readonly LanguageServerManifest _manifest;

    /// <summary>
    /// Index of table names → column sets for O(1) lookup.
    /// Keys are case-insensitive because DatumIngest SQL is case-insensitive.
    /// </summary>
    private readonly Dictionary<string, HashSet<string>> _tableColumns;

    /// <summary>Known function names (case-insensitive).</summary>
    private readonly HashSet<string> _functionNames;

    /// <summary>Creates a new analyzer bound to the given manifest.</summary>
    public SemanticAnalyzer(LanguageServerManifest manifest)
    {
        _manifest = manifest;

        _tableColumns = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (TableSchemaEntry table in _manifest.Tables)
        {
            HashSet<string> columns = new(StringComparer.OrdinalIgnoreCase);
            foreach (TableColumnEntry column in table.Columns)
            {
                columns.Add(column.Name);
            }

            _tableColumns[table.Name] = columns;
        }

        _functionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FunctionSignature function in _manifest.Functions)
        {
            _functionNames.Add(function.Name);
        }
    }

    /// <summary>
    /// Analyzes the <paramref name="statement"/> and returns semantic warnings
    /// for any table, column, or function references that cannot be resolved
    /// against the manifest.
    /// </summary>
    public Diagnostic[] Analyze(SelectStatement statement)
    {
        List<Diagnostic> diagnostics = new();

        // Collect all table sources to build the scope of available columns.
        // Aliases map to the underlying table's columns; subqueries and
        // function sources are opaque (we skip column validation for them).
        Dictionary<string, string> aliasToTable = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> opaqueAliases = new(StringComparer.OrdinalIgnoreCase);

        CollectTableSources(statement.From.Source, aliasToTable, opaqueAliases, diagnostics);

        if (statement.Joins is not null)
        {
            foreach (JoinClause join in statement.Joins)
            {
                CollectTableSources(join.Source, aliasToTable, opaqueAliases, diagnostics);
            }
        }

        // Walk expressions for column and function references.
        foreach (SelectColumn column in statement.Columns)
        {
            if (column is SelectTableColumns tableColumns)
            {
                ValidateTableQualifier(tableColumns.TableName, tableColumns.Span, aliasToTable, opaqueAliases, diagnostics);
            }
            else if (column is not SelectAllColumns)
            {
                AnalyzeExpression(column.Expression, aliasToTable, opaqueAliases, diagnostics);
            }
        }

        if (statement.Where is not null)
        {
            AnalyzeExpression(statement.Where, aliasToTable, opaqueAliases, diagnostics);
        }

        if (statement.Joins is not null)
        {
            foreach (JoinClause join in statement.Joins)
            {
                if (join.OnCondition is not null)
                {
                    AnalyzeExpression(join.OnCondition, aliasToTable, opaqueAliases, diagnostics);
                }
            }
        }

        if (statement.OrderBy is not null)
        {
            foreach (OrderByItem item in statement.OrderBy.Items)
            {
                AnalyzeExpression(item.Expression, aliasToTable, opaqueAliases, diagnostics);
            }
        }

        return diagnostics.ToArray();
    }

    /// <summary>
    /// Registers a table source in the scope and emits a diagnostic if the
    /// table is unknown.
    /// </summary>
    private void CollectTableSources(
        TableSource source,
        Dictionary<string, string> aliasToTable,
        HashSet<string> opaqueAliases,
        List<Diagnostic> diagnostics)
    {
        switch (source)
        {
            case TableReference tableReference:
                if (!_tableColumns.ContainsKey(tableReference.Name))
                {
                    EmitWarning(diagnostics, tableReference.Span,
                        $"Unknown table '{tableReference.Name}'.");
                }

                // Register both alias and raw name so column references
                // qualified by either succeed.
                string effectiveName = tableReference.Alias ?? tableReference.Name;
                aliasToTable[effectiveName] = tableReference.Name;
                if (tableReference.Alias is not null)
                {
                    aliasToTable[tableReference.Alias] = tableReference.Name;
                }

                break;

            case FunctionSource functionSource:
                if (!_functionNames.Contains(functionSource.FunctionName))
                {
                    EmitWarning(diagnostics, functionSource.Span,
                        $"Unknown function '{functionSource.FunctionName}'.");
                }

                // Function sources are opaque — we cannot know their output columns.
                if (functionSource.Alias is not null)
                {
                    opaqueAliases.Add(functionSource.Alias);
                }

                // Analyze the function's argument expressions.
                foreach (Expression argument in functionSource.Arguments)
                {
                    AnalyzeExpression(argument, aliasToTable, opaqueAliases, diagnostics);
                }

                break;

            case SubquerySource subquerySource:
                // Subqueries are opaque — the inner columns are not exposed.
                opaqueAliases.Add(subquerySource.Alias);

                // Recurse into the subquery with a fresh scope.
                Diagnostic[] subDiagnostics = Analyze(subquerySource.Query);
                diagnostics.AddRange(subDiagnostics);
                break;
        }
    }

    /// <summary>
    /// Recursively walks an expression tree, validating column references
    /// and function calls.
    /// </summary>
    private void AnalyzeExpression(
        Expression expression,
        Dictionary<string, string> aliasToTable,
        HashSet<string> opaqueAliases,
        List<Diagnostic> diagnostics)
    {
        switch (expression)
        {
            case ColumnReference column:
                ValidateColumnReference(column, aliasToTable, opaqueAliases, diagnostics);
                break;

            case FunctionCallExpression functionCall:
                if (!_functionNames.Contains(functionCall.FunctionName))
                {
                    EmitWarning(diagnostics, functionCall.Span,
                        $"Unknown function '{functionCall.FunctionName}'.");
                }

                foreach (Expression argument in functionCall.Arguments)
                {
                    AnalyzeExpression(argument, aliasToTable, opaqueAliases, diagnostics);
                }

                break;

            case BinaryExpression binary:
                AnalyzeExpression(binary.Left, aliasToTable, opaqueAliases, diagnostics);
                AnalyzeExpression(binary.Right, aliasToTable, opaqueAliases, diagnostics);
                break;

            case UnaryExpression unary:
                AnalyzeExpression(unary.Operand, aliasToTable, opaqueAliases, diagnostics);
                break;

            case InExpression inExpression:
                AnalyzeExpression(inExpression.Expression, aliasToTable, opaqueAliases, diagnostics);
                foreach (Expression value in inExpression.Values)
                {
                    AnalyzeExpression(value, aliasToTable, opaqueAliases, diagnostics);
                }

                break;

            case BetweenExpression between:
                AnalyzeExpression(between.Expression, aliasToTable, opaqueAliases, diagnostics);
                AnalyzeExpression(between.Low, aliasToTable, opaqueAliases, diagnostics);
                AnalyzeExpression(between.High, aliasToTable, opaqueAliases, diagnostics);
                break;

            case IsNullExpression isNull:
                AnalyzeExpression(isNull.Expression, aliasToTable, opaqueAliases, diagnostics);
                break;

            case CastExpression cast:
                AnalyzeExpression(cast.Expression, aliasToTable, opaqueAliases, diagnostics);
                break;

            case SubqueryExpression subquery:
                Diagnostic[] subDiagnostics = Analyze(subquery.Query);
                diagnostics.AddRange(subDiagnostics);
                break;

            // LiteralExpression — nothing to validate.
        }
    }

    /// <summary>
    /// Validates a column reference against the manifest scope.
    /// </summary>
    private void ValidateColumnReference(
        ColumnReference column,
        Dictionary<string, string> aliasToTable,
        HashSet<string> opaqueAliases,
        List<Diagnostic> diagnostics)
    {
        if (column.ColumnName == "*")
        {
            // Wildcard — already handled by SelectAllColumns / SelectTableColumns.
            return;
        }

        if (column.TableName is not null)
        {
            ValidateTableQualifier(column.TableName, column.Span, aliasToTable, opaqueAliases, diagnostics);

            // If the table is opaque, skip column validation.
            if (opaqueAliases.Contains(column.TableName))
            {
                return;
            }

            if (aliasToTable.TryGetValue(column.TableName, out string? resolvedTable) &&
                _tableColumns.TryGetValue(resolvedTable, out HashSet<string>? columns))
            {
                if (!columns.Contains(column.ColumnName))
                {
                    EmitWarning(diagnostics, column.Span,
                        $"Unknown column '{column.ColumnName}' in table '{resolvedTable}'.");
                }
            }

            return;
        }

        // Unqualified column — search all tables in scope.
        bool found = false;
        foreach (KeyValuePair<string, string> entry in aliasToTable)
        {
            if (_tableColumns.TryGetValue(entry.Value, out HashSet<string>? columns) &&
                columns.Contains(column.ColumnName))
            {
                found = true;
                break;
            }
        }

        // If any source is opaque, we cannot be sure the column is missing.
        if (!found && opaqueAliases.Count == 0)
        {
            EmitWarning(diagnostics, column.Span,
                $"Unknown column '{column.ColumnName}'.");
        }
    }

    /// <summary>
    /// Validates that a table qualifier (table name or alias) is known in
    /// the current scope.
    /// </summary>
    private static void ValidateTableQualifier(
        string tableName,
        SourceSpan? span,
        Dictionary<string, string> aliasToTable,
        HashSet<string> opaqueAliases,
        List<Diagnostic> diagnostics)
    {
        if (!aliasToTable.ContainsKey(tableName) && !opaqueAliases.Contains(tableName))
        {
            EmitWarning(diagnostics, span,
                $"Unknown table or alias '{tableName}'.");
        }
    }

    /// <summary>
    /// Creates a <see cref="DiagnosticSeverity.Warning"/> diagnostic from
    /// a <see cref="SourceSpan"/> (which uses 1-based positions from Superpower)
    /// and converts to the 0-based LSP convention.
    /// </summary>
    private static void EmitWarning(List<Diagnostic> diagnostics, SourceSpan? span, string message)
    {
        if (span is null)
        {
            // No position information — emit at the start of the file.
            diagnostics.Add(new Diagnostic
            {
                Message = message,
                Severity = DiagnosticSeverity.Warning,
                StartLine = 0,
                StartColumn = 0,
                EndLine = 0,
                EndColumn = 1,
            });
            return;
        }

        // SourceSpan is 1-based (from Superpower); LSP diagnostics are 0-based.
        int line = span.Line - 1;
        int column = span.Column - 1;

        diagnostics.Add(new Diagnostic
        {
            Message = message,
            Severity = DiagnosticSeverity.Warning,
            StartLine = line,
            StartColumn = column,
            EndLine = line,
            EndColumn = column + span.Length,
        });
    }
}
