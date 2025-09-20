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
    /// Index of table names → (column name → DataKind string) for O(1) lookup.
    /// Keys are case-insensitive because DatumIngest SQL is case-insensitive.
    /// </summary>
    private readonly Dictionary<string, Dictionary<string, string>> _tableColumnTypes;

    /// <summary>Known function names (case-insensitive).</summary>
    private readonly HashSet<string> _functionNames;

    /// <summary>Function signatures indexed by name for argument type validation.</summary>
    private readonly Dictionary<string, FunctionSignature> _functionSignatures;

    // ── Built-in virtual schema tables (known statically, no manifest needed) ──

    /// <summary>
    /// Maps virtual schema names to their known table names and column schemas.
    /// Column schemas use the same (name → DataKind string) format as
    /// <see cref="_tableColumnTypes"/> for consistent downstream validation.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, Dictionary<string, string>>> VirtualSchemaColumns =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["information_schema"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["tables"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["table_catalog"] = "String",
                    ["table_schema"] = "String",
                    ["table_name"] = "String",
                    ["table_type"] = "String",
                },
                ["columns"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["table_catalog"] = "String",
                    ["table_schema"] = "String",
                    ["table_name"] = "String",
                    ["column_name"] = "String",
                    ["ordinal_position"] = "Int32",
                    ["data_type"] = "String",
                    ["is_nullable"] = "String",
                },
                ["schemata"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["catalog_name"] = "String",
                    ["schema_name"] = "String",
                },
            },
            ["datum_catalog"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["providers"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["provider_name"] = "String",
                },
                ["functions"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["function_name"] = "String",
                    ["function_type"] = "String",
                    ["category"] = "String",
                    ["return_type"] = "String",
                    ["description"] = "String",
                    ["parameter_count"] = "Int32",
                    ["query_unit_cost"] = "Int32",
                },
                ["function_parameters"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["function_name"] = "String",
                    ["ordinal_position"] = "Int32",
                    ["parameter_name"] = "String",
                    ["data_type"] = "String",
                    ["is_optional"] = "String",
                },
                ["statistics"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["table_name"] = "String",
                    ["column_name"] = "String",
                    ["data_type"] = "String",
                    ["row_count"] = "Int64",
                    ["distinct_count"] = "Int64",
                    ["null_ratio"] = "Float64",
                    ["min_value"] = "String",
                    ["max_value"] = "String",
                    ["entropy"] = "Float64",
                    ["dominant_value_ratio"] = "Float64",
                    ["is_constant"] = "String",
                    ["column_role"] = "String",
                    ["top_value"] = "String",
                    ["top_value_frequency"] = "Int64",
                    ["mean"] = "Float64",
                    ["standard_deviation"] = "Float64",
                    ["skewness"] = "Float64",
                    ["kurtosis"] = "Float64",
                    ["p25"] = "Float64",
                    ["p50"] = "Float64",
                    ["p75"] = "Float64",
                    ["zero_ratio"] = "Float64",
                    ["outlier_ratio"] = "Float64",
                    ["integer_valued"] = "String",
                    ["min_length"] = "Int32",
                    ["max_length"] = "Int32",
                    ["true_ratio"] = "Float64",
                },
                ["indexes"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["table_name"] = "String",
                    ["column_name"] = "String",
                    ["index_type"] = "String",
                    ["entry_count"] = "Int64",
                    ["chunk_count"] = "Int32",
                    ["total_row_count"] = "Int64",
                },
                ["interactions"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["table_name"] = "String",
                    ["column_a"] = "String",
                    ["column_b"] = "String",
                    ["pearson"] = "Float64",
                    ["spearman"] = "Float64",
                    ["cramer_v"] = "Float64",
                    ["anova_f"] = "Float64",
                    ["mutual_information"] = "Float64",
                    ["theil_u_ab"] = "Float64",
                    ["theil_u_ba"] = "Float64",
                    ["missingness_correlation"] = "Float64",
                },
            },
        };

    // ── Type category sets for compatibility checks ──

    private static readonly HashSet<string> NumericKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Float32", "Float64", "Int8", "Int16", "Int32", "Int64",
        "UInt8", "UInt16", "UInt32", "UInt64",
    };

    private static readonly HashSet<string> VectorKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Vector", "Matrix", "Tensor",
    };

    private static readonly HashSet<string> ImageKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Image", "UInt8Array",
    };

    private static readonly HashSet<string> TemporalKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Date", "DateTime", "Time", "Duration",
    };

    /// <summary>Creates a new analyzer bound to the given manifest.</summary>
    public SemanticAnalyzer(LanguageServerManifest manifest)
    {
        _manifest = manifest;

        _tableColumnTypes = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (TableSchemaEntry table in _manifest.Tables)
        {
            Dictionary<string, string> columnKinds = new(StringComparer.OrdinalIgnoreCase);
            foreach (TableColumnEntry column in table.Columns)
            {
                columnKinds[column.Name] = column.Kind;
            }

            _tableColumnTypes[table.Name] = columnKinds;
        }

        _functionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _functionSignatures = new Dictionary<string, FunctionSignature>(StringComparer.OrdinalIgnoreCase);
        foreach (FunctionSignature function in _manifest.Functions)
        {
            _functionNames.Add(function.Name);
            _functionSignatures[function.Name] = function;
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
        AnalyzeStatement(statement, diagnostics);
        return diagnostics.ToArray();
    }

    /// <summary>
    /// Analyzes a <see cref="QueryExpression"/> tree, recursively walking compound
    /// set operations and producing semantic warnings for each branch.
    /// </summary>
    public Diagnostic[] Analyze(QueryExpression queryExpression)
    {
        List<Diagnostic> diagnostics = new();

        switch (queryExpression)
        {
            case SelectQueryExpression selectQuery:
                AnalyzeStatement(selectQuery.Statement, diagnostics);
                break;

            case CompoundQueryExpression compound:
                diagnostics.AddRange(Analyze(compound.Left));
                diagnostics.AddRange(Analyze(compound.Right));
                break;
        }

        return diagnostics.ToArray();
    }

    /// <summary>
    /// Core analysis logic for a single SELECT statement.
    /// </summary>
    private void AnalyzeStatement(SelectStatement statement, List<Diagnostic> diagnostics)
    {

        // Collect all table sources to build the scope of available columns.
        // Aliases map to the underlying table's columns; subqueries and
        // function sources are opaque (we skip column validation for them).
        Dictionary<string, string> aliasToTable = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> opaqueAliases = new(StringComparer.OrdinalIgnoreCase);

        // Register CTE names as opaque aliases so references in FROM/JOIN
        // do not produce "Unknown table" warnings.
        if (statement.CommonTableExpressions is not null)
        {
            foreach (CommonTableExpression commonTableExpression in statement.CommonTableExpressions)
            {
                opaqueAliases.Add(commonTableExpression.Name);
            }
        }

        if (statement.From is not null)
        {
            CollectTableSources(statement.From.Source, aliasToTable, opaqueAliases, diagnostics);
        }

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
                ValidateExcludedColumns(tableColumns.ExcludedColumns, tableColumns.TableName, aliasToTable, opaqueAliases, diagnostics);
                ValidateReplacedColumns(tableColumns.ReplacedColumns, tableColumns.TableName, aliasToTable, opaqueAliases, diagnostics);
            }
            else if (column is SelectAllColumns allColumns)
            {
                ValidateExcludedColumns(allColumns.ExcludedColumns, null, aliasToTable, opaqueAliases, diagnostics);
                ValidateReplacedColumns(allColumns.ReplacedColumns, null, aliasToTable, opaqueAliases, diagnostics);
            }
            else
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

        if (statement.GroupBy is not null)
        {
            foreach (Expression groupExpression in statement.GroupBy.Expressions)
            {
                AnalyzeExpression(groupExpression, aliasToTable, opaqueAliases, diagnostics);
            }
        }

        if (statement.Having is not null)
        {
            AnalyzeExpression(statement.Having, aliasToTable, opaqueAliases, diagnostics);
        }

        if (statement.Qualify is not null)
        {
            AnalyzeExpression(statement.Qualify, aliasToTable, opaqueAliases, diagnostics);
        }

        if (statement.Assertions is not null)
        {
            // LET binding names are virtual columns that exist only in the augmented row at
            // runtime — they are not in any underlying table schema, so the regular column
            // resolver would emit false 'Unknown column' squiggles for them. Adding them as
            // opaque pseudo-aliases suppresses unqualified-column warnings inside ASSERT
            // predicates, which is the same conservative approach used for lambda parameters.
            HashSet<string> assertOpaqueAliases = opaqueAliases;
            if (statement.LetBindings is { Count: > 0 })
            {
                assertOpaqueAliases = new(opaqueAliases, StringComparer.OrdinalIgnoreCase);
                foreach (LetBinding letBinding in statement.LetBindings)
                {
                    assertOpaqueAliases.Add(letBinding.Name);
                }
            }

            foreach (AssertClause assertClause in statement.Assertions)
            {
                AnalyzeExpression(assertClause.Predicate, aliasToTable, assertOpaqueAliases, diagnostics);
                if (assertClause.Message is not null)
                {
                    AnalyzeExpression(assertClause.Message, aliasToTable, assertOpaqueAliases, diagnostics);
                }
            }
        }

        if (statement.Pivot is not null)
        {
            foreach (FunctionCallExpression aggregate in statement.Pivot.Aggregates)
            {
                AnalyzeExpression(aggregate, aliasToTable, opaqueAliases, diagnostics);
            }

            AnalyzeExpression(statement.Pivot.PivotColumn, aliasToTable, opaqueAliases, diagnostics);
        }

        if (statement.Unpivot is not null)
        {
            foreach (ColumnReference sourceColumn in statement.Unpivot.SourceColumns)
            {
                AnalyzeExpression(sourceColumn, aliasToTable, opaqueAliases, diagnostics);
            }
        }
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
                if (tableReference.SchemaName is not null)
                {
                    // Schema-qualified reference (e.g. information_schema.tables).
                    // Validate against the built-in virtual schema definitions.
                    if (!TryResolveVirtualTable(
                            tableReference.SchemaName, tableReference.Name,
                            out Dictionary<string, string>? virtualColumns) ||
                        virtualColumns is null)
                    {
                        EmitWarning(diagnostics, tableReference.Span,
                            $"Unknown table '{tableReference.SchemaName}.{tableReference.Name}'.");
                    }
                    else
                    {
                        // Expose the virtual table's columns for downstream
                        // column-reference validation under the effective alias.
                        string virtualKey = $"{tableReference.SchemaName}.{tableReference.Name}";
                        _tableColumnTypes.TryAdd(virtualKey, virtualColumns);
                    }
                }
                else if (!_tableColumnTypes.ContainsKey(tableReference.Name) &&
                    !opaqueAliases.Contains(tableReference.Name))
                {
                    EmitWarning(diagnostics, tableReference.Span,
                        $"Unknown table '{tableReference.Name}'.");
                }

                // Register both alias and raw name so column references
                // qualified by either succeed.
                string effectiveName = tableReference.Alias ?? tableReference.Name;
                if (tableReference.SchemaName is not null)
                {
                    string qualifiedName = $"{tableReference.SchemaName}.{tableReference.Name}";
                    aliasToTable[effectiveName] = qualifiedName;
                    if (tableReference.Alias is not null)
                    {
                        aliasToTable[tableReference.Alias] = qualifiedName;
                    }
                }
                else
                {
                    aliasToTable[effectiveName] = tableReference.Name;
                    if (tableReference.Alias is not null)
                    {
                        aliasToTable[tableReference.Alias] = tableReference.Name;
                    }
                }

                break;

            case FunctionSource functionSource:
                if (!_functionNames.Contains(functionSource.FunctionName))
                {
                    EmitWarning(diagnostics, functionSource.Span,
                        $"Unknown function '{functionSource.FunctionName}'.");
                }

                // Function sources are opaque — we cannot know their output columns
                // statically. Mark the alias (or the function name as a sentinel)
                // so that column validation suppresses unknown-column warnings.
                opaqueAliases.Add(functionSource.Alias ?? functionSource.FunctionName);

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
                else
                {
                    ValidateFunctionArgTypes(functionCall, aliasToTable, opaqueAliases, diagnostics);
                }

                foreach (Expression argument in functionCall.Arguments)
                {
                    AnalyzeExpression(argument, aliasToTable, opaqueAliases, diagnostics);
                }

                // Validate column references in intra-aggregate ORDER BY (STRING_AGG)
                // and WITHIN GROUP ORDER BY (PERCENTILE_DISC/CONT, MODE).
                if (functionCall.OrderBy is not null)
                {
                    foreach (OrderByItem orderByItem in functionCall.OrderBy)
                    {
                        AnalyzeExpression(orderByItem.Expression, aliasToTable, opaqueAliases, diagnostics);
                    }
                }

                break;

            case BinaryExpression binary:
                AnalyzeExpression(binary.Left, aliasToTable, opaqueAliases, diagnostics);
                AnalyzeExpression(binary.Right, aliasToTable, opaqueAliases, diagnostics);
                break;

            case LikeExpression like:
                AnalyzeExpression(like.Expression, aliasToTable, opaqueAliases, diagnostics);
                AnalyzeExpression(like.Pattern, aliasToTable, opaqueAliases, diagnostics);
                AnalyzeExpression(like.EscapeCharacter, aliasToTable, opaqueAliases, diagnostics);
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

            case CaseExpression caseExpr:
                if (caseExpr.Operand is not null)
                {
                    AnalyzeExpression(caseExpr.Operand, aliasToTable, opaqueAliases, diagnostics);
                }

                foreach (WhenClause whenClause in caseExpr.WhenClauses)
                {
                    AnalyzeExpression(whenClause.Condition, aliasToTable, opaqueAliases, diagnostics);
                    AnalyzeExpression(whenClause.Result, aliasToTable, opaqueAliases, diagnostics);
                }

                if (caseExpr.ElseResult is not null)
                {
                    AnalyzeExpression(caseExpr.ElseResult, aliasToTable, opaqueAliases, diagnostics);
                }

                break;

            case SubqueryExpression subquery:
                Diagnostic[] subDiagnostics = Analyze(subquery.Query);
                diagnostics.AddRange(subDiagnostics);
                break;

            case InSubqueryExpression inSubquery:
                AnalyzeExpression(inSubquery.Expression, aliasToTable, opaqueAliases, diagnostics);
                Diagnostic[] inSubDiagnostics = Analyze(inSubquery.Query);
                diagnostics.AddRange(inSubDiagnostics);
                break;

            case ExistsExpression existsExpr:
                Diagnostic[] existsDiagnostics = Analyze(existsExpr.Query);
                diagnostics.AddRange(existsDiagnostics);
                break;

            case WindowFunctionCallExpression windowCall:
                if (!_functionNames.Contains(windowCall.FunctionName))
                {
                    EmitWarning(diagnostics, windowCall.Span,
                        $"Unknown window function '{windowCall.FunctionName}'.");
                }

                foreach (Expression argument in windowCall.Arguments)
                {
                    AnalyzeExpression(argument, aliasToTable, opaqueAliases, diagnostics);
                }

                if (windowCall.Window.PartitionBy is not null)
                {
                    foreach (Expression partition in windowCall.Window.PartitionBy)
                    {
                        AnalyzeExpression(partition, aliasToTable, opaqueAliases, diagnostics);
                    }
                }

                if (windowCall.Window.OrderBy is not null)
                {
                    foreach (OrderByItem orderByItem in windowCall.Window.OrderBy)
                    {
                        AnalyzeExpression(orderByItem.Expression, aliasToTable, opaqueAliases, diagnostics);
                    }
                }

                break;

            // ErrorExpression — inserted by error recovery; skip validation.
            case ErrorExpression:
                break;

            // ParameterExpression — valid placeholder; no validation at parse time.
            case ParameterExpression:
                break;

            // Lambda expressions: validate the body with lambda parameter names
            // added as opaque aliases so they are treated as valid column-like
            // references (same conservative logic used for CTE sources).
            case LambdaExpression lambda:
                HashSet<string> lambdaOpaqueAliases = new(opaqueAliases, StringComparer.OrdinalIgnoreCase);
                foreach (string parameter in lambda.Parameters)
                {
                    lambdaOpaqueAliases.Add(parameter);
                }

                AnalyzeExpression(lambda.Body, aliasToTable, lambdaOpaqueAliases, diagnostics);
                break;

            // StructLiteralExpression — validate each field's value expression.
            case StructLiteralExpression structLiteral:
                foreach (StructField field in structLiteral.Fields)
                {
                    AnalyzeExpression(field.Value, aliasToTable, opaqueAliases, diagnostics);
                }
                break;

            // IndexAccessExpression — validate both source and index sub-expressions.
            case IndexAccessExpression indexAccess:
                AnalyzeExpression(indexAccess.Source, aliasToTable, opaqueAliases, diagnostics);
                AnalyzeExpression(indexAccess.Index, aliasToTable, opaqueAliases, diagnostics);
                break;

            // LiteralExpression — nothing to validate.
        }
    }

    /// <summary>
    /// Validates argument types for a known function call against its registered signature.
    /// Only emits a warning when both the actual type and the expected type can be determined
    /// with high confidence — conservative by design to minimise false positives.
    /// </summary>
    private void ValidateFunctionArgTypes(
        FunctionCallExpression functionCall,
        Dictionary<string, string> aliasToTable,
        HashSet<string> opaqueAliases,
        List<Diagnostic> diagnostics)
    {
        if (!_functionSignatures.TryGetValue(functionCall.FunctionName, out FunctionSignature? signature))
        {
            return;
        }

        for (int argumentIndex = 0; argumentIndex < functionCall.Arguments.Count; argumentIndex++)
        {
            // If the function has fewer parameters than arguments (variadic), apply
            // the last parameter's constraint to all extra arguments.
            int parameterIndex = System.Math.Min(argumentIndex, signature.Parameters.Count - 1);
            if (parameterIndex < 0)
            {
                break;
            }

            ParameterSignature parameter = signature.Parameters[parameterIndex];

            // "Any" means no type restriction — skip.
            if (parameter.Kind.Equals("Any", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? actualKind = TryInferType(functionCall.Arguments[argumentIndex], aliasToTable, opaqueAliases);
            if (actualKind is null)
            {
                // Cannot determine type — skip rather than risk a false positive.
                continue;
            }

            if (!IsTypeCompatible(actualKind, parameter.Kind))
            {
                EmitWarning(diagnostics, functionCall.Span,
                    $"Argument {argumentIndex + 1} of {functionCall.FunctionName}() expects {parameter.Kind}, got {actualKind}.");
            }
        }
    }

    /// <summary>
    /// Attempts to infer the data kind of an expression from manifest information.
    /// Returns null when the type cannot be determined with confidence.
    /// </summary>
    private string? TryInferType(
        Expression expression,
        Dictionary<string, string> aliasToTable,
        HashSet<string> opaqueAliases)
    {
        return expression switch
        {
            ColumnReference column => TryInferColumnType(column, aliasToTable, opaqueAliases),
            LiteralExpression { Value: string } => "String",
            LiteralExpression { Value: double } => "Float32",
            LiteralExpression { Value: bool } => "Boolean",
            CastExpression cast => cast.TargetType,
            FunctionCallExpression functionCall =>
                _functionSignatures.TryGetValue(functionCall.FunctionName, out FunctionSignature? sig)
                    ? sig.ReturnType
                    : null,
            UnaryExpression { Operator: UnaryOperator.Negate } unary =>
                TryInferType(unary.Operand, aliasToTable, opaqueAliases),
            _ => null,
        };
    }

    /// <summary>
    /// Looks up the data kind of a column reference in the manifest.
    /// Returns null when the column cannot be resolved (already warned separately).
    /// </summary>
    private string? TryInferColumnType(
        ColumnReference column,
        Dictionary<string, string> aliasToTable,
        HashSet<string> opaqueAliases)
    {
        if (column.TableName is not null)
        {
            if (opaqueAliases.Contains(column.TableName))
            {
                return null;
            }

            if (aliasToTable.TryGetValue(column.TableName, out string? resolvedTable) &&
                _tableColumnTypes.TryGetValue(resolvedTable, out Dictionary<string, string>? columnKinds) &&
                columnKinds.TryGetValue(column.ColumnName, out string? kind))
            {
                return kind;
            }

            return null;
        }

        // Unqualified — return the first match across all tables in scope.
        foreach (KeyValuePair<string, string> entry in aliasToTable)
        {
            if (_tableColumnTypes.TryGetValue(entry.Value, out Dictionary<string, string>? columnKinds) &&
                columnKinds.TryGetValue(column.ColumnName, out string? kind))
            {
                return kind;
            }
        }

        // If any opaque source is in scope, we cannot rule out the column existing there.
        return null;
    }

    /// <summary>
    /// Returns true when <paramref name="actualKind"/> is compatible with
    /// <paramref name="expectedKind"/>. Uses category-based matching so that,
    /// for example, an Int32 column is accepted where Float32 is expected.
    /// </summary>
    private static bool IsTypeCompatible(string actualKind, string expectedKind)
    {
        if (actualKind.Equals(expectedKind, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Both numeric → compatible (e.g. Int32 column into Float32 parameter).
        if (NumericKinds.Contains(actualKind) && NumericKinds.Contains(expectedKind))
        {
            return true;
        }

        // Both vector-family → compatible (Vector/Matrix/Tensor are interchangeable in most ops).
        if (VectorKinds.Contains(actualKind) && VectorKinds.Contains(expectedKind))
        {
            return true;
        }

        // Both image-family → compatible.
        if (ImageKinds.Contains(actualKind) && ImageKinds.Contains(expectedKind))
        {
            return true;
        }

        // Both temporal → compatible (Date/DateTime/Time/Duration).
        if (TemporalKinds.Contains(actualKind) && TemporalKinds.Contains(expectedKind))
        {
            return true;
        }

        return false;
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
                _tableColumnTypes.TryGetValue(resolvedTable, out Dictionary<string, string>? columnKinds))
            {
                if (!columnKinds.ContainsKey(column.ColumnName))
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
            if (_tableColumnTypes.TryGetValue(entry.Value, out Dictionary<string, string>? columnKinds) &&
                columnKinds.ContainsKey(column.ColumnName))
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
    /// Validates that excluded column names in a <c>SELECT * EXCEPT (...)</c> or
    /// <c>SELECT table.* EXCEPT (...)</c> clause reference columns that exist in the
    /// manifest. When no manifest data is available (opaque sources), validation is skipped.
    /// </summary>
    private void ValidateExcludedColumns(
        IReadOnlyList<string>? excludedColumns,
        string? tableAlias,
        Dictionary<string, string> aliasToTable,
        HashSet<string> opaqueAliases,
        List<Diagnostic> diagnostics)
    {
        if (excludedColumns is null || excludedColumns.Count == 0)
            return;

        if (tableAlias is not null)
        {
            // table.* EXCEPT (...) — validate against the specific table.
            if (opaqueAliases.Contains(tableAlias))
                return;

            if (!aliasToTable.TryGetValue(tableAlias, out string? resolvedTable))
                return;

            if (!_tableColumnTypes.TryGetValue(resolvedTable, out Dictionary<string, string>? columnKinds))
                return;

            foreach (string excluded in excludedColumns)
            {
                if (!columnKinds.ContainsKey(excluded))
                {
                    EmitWarning(diagnostics, null,
                        $"EXCEPT column '{excluded}' not found in table '{resolvedTable}'.");
                }
            }
        }
        else
        {
            // * EXCEPT (...) — validate against all tables in scope.
            if (opaqueAliases.Count > 0)
                return;

            foreach (string excluded in excludedColumns)
            {
                bool found = false;
                foreach (KeyValuePair<string, string> entry in aliasToTable)
                {
                    if (_tableColumnTypes.TryGetValue(entry.Value, out Dictionary<string, string>? columnKinds)
                        && columnKinds.ContainsKey(excluded))
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    EmitWarning(diagnostics, null,
                        $"EXCEPT column '{excluded}' not found in any table in scope.");
                }
            }
        }
    }

    /// <summary>
    /// Validates that replacement column names in a <c>SELECT * REPLACE (...)</c> or
    /// <c>SELECT table.* REPLACE (...)</c> clause reference columns that exist in the
    /// manifest. Also analyzes the replacement expressions for column/function references.
    /// When no manifest data is available (opaque sources), validation is skipped.
    /// </summary>
    private void ValidateReplacedColumns(
        IReadOnlyList<ColumnReplacement>? replacedColumns,
        string? tableAlias,
        Dictionary<string, string> aliasToTable,
        HashSet<string> opaqueAliases,
        List<Diagnostic> diagnostics)
    {
        if (replacedColumns is null || replacedColumns.Count == 0)
            return;

        foreach (ColumnReplacement replacement in replacedColumns)
        {
            AnalyzeExpression(replacement.Expression, aliasToTable, opaqueAliases, diagnostics);

            if (tableAlias is not null)
            {
                if (opaqueAliases.Contains(tableAlias))
                    continue;

                if (!aliasToTable.TryGetValue(tableAlias, out string? resolvedTable))
                    continue;

                if (!_tableColumnTypes.TryGetValue(resolvedTable, out Dictionary<string, string>? columnKinds))
                    continue;

                if (!columnKinds.ContainsKey(replacement.ColumnName))
                {
                    EmitWarning(diagnostics, null,
                        $"REPLACE column '{replacement.ColumnName}' not found in table '{resolvedTable}'.");
                }
            }
            else
            {
                if (opaqueAliases.Count > 0)
                    continue;

                bool found = false;
                foreach (KeyValuePair<string, string> entry in aliasToTable)
                {
                    if (_tableColumnTypes.TryGetValue(entry.Value, out Dictionary<string, string>? columnKinds)
                        && columnKinds.ContainsKey(replacement.ColumnName))
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    EmitWarning(diagnostics, null,
                        $"REPLACE column '{replacement.ColumnName}' not found in any table in scope.");
                }
            }
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

    /// <summary>
    /// Checks whether a schema-qualified table reference points to a known
    /// built-in virtual schema table. When found, returns the column schema
    /// for downstream column validation.
    /// </summary>
    private static bool TryResolveVirtualTable(
        string schemaName,
        string tableName,
        out Dictionary<string, string>? columnTypes)
    {
        columnTypes = null;

        if (VirtualSchemaColumns.TryGetValue(schemaName, out Dictionary<string, Dictionary<string, string>>? tables) &&
            tables.TryGetValue(tableName, out Dictionary<string, string>? columns))
        {
            columnTypes = columns;
            return true;
        }

        return false;
    }
}
