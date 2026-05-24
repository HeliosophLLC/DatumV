namespace Heliosoph.DatumV.LanguageServer;

using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Parsing.Ast;

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
    /// LET binding names visible in the SELECT statement currently being
    /// analyzed. Save/restore'd around <see cref="AnalyzeStatement"/> so
    /// recursive subquery analysis doesn't clobber the enclosing
    /// statement's set. Consulted by <see cref="ValidateColumnReference"/>
    /// to exclude LET names from the "alias used as value" warning —
    /// LET refs like <c>unnest(classes)</c> are the intended use, not
    /// a misuse like referencing a FROM/JOIN alias bare.
    /// </summary>
    private HashSet<string> _currentStatementLetNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// TVF aliases in the current statement whose output column names
    /// we resolved statically (either from the manifest's
    /// <see cref="FunctionSignature.OutputColumns"/> or via the
    /// per-TVF hardcoded fallback in <see cref="TryGetTvfOutputColumns"/>).
    /// Save/restore'd around <see cref="AnalyzeStatement"/>. Walked by
    /// the unknown-column check so bare references like <c>filex</c>
    /// fail even when an <c>unnest(...) c</c> source is in scope —
    /// previously the TVF's mere presence (opaque) suppressed every
    /// unknown-column warning; resolving its output column names
    /// keeps the suppression scoped to TVFs whose output we really
    /// can't pin down.
    /// </summary>
    private Dictionary<string, HashSet<string>> _currentStatementTvfColumns =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Index of table names → (column name → DataKind string) for O(1) lookup.
    /// Keys are case-insensitive because Heliosoph.DatumV SQL is case-insensitive.
    /// </summary>
    private readonly Dictionary<string, Dictionary<string, string>> _tableColumnTypes;

    /// <summary>Known function names (case-insensitive).</summary>
    private readonly HashSet<string> _functionNames;

    /// <summary>Function signatures indexed by name for argument type validation.</summary>
    private readonly Dictionary<string, FunctionSignature> _functionSignatures;

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
        "Date", "Timestamp", "TimestampTz", "Time", "Duration",
    };

    private static readonly HashSet<string> IntegerKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Int8", "UInt8", "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64",
    };

    private static readonly HashSet<string> FloatKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Float32", "Float64",
    };

    /// <summary>
    /// Maps DataKindFamily labels (the strings produced by
    /// <c>FamilyMatcher.Describe()</c>) to the concrete-kind sets they
    /// accept. Without this, a parameter declared with
    /// <c>DataKindMatcher.Family(DataKindFamily.NumericScalar)</c> surfaces
    /// in the manifest with <c>Kind = "NumericScalar"</c> and the strict
    /// string-equality check in <see cref="IsTypeCompatible"/> rejects
    /// every concrete numeric kind. Keep keys synchronised with
    /// the <c>DataKindFamily</c> enum names.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> KindFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NumericScalar"] = NumericKinds,
        ["IntegerFamily"] = IntegerKinds,
        ["FloatFamily"] = FloatKinds,
        ["Temporal"] = TemporalKinds,
        ["TextLike"] = new(StringComparer.OrdinalIgnoreCase) { "String" },
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
        // Save the enclosing statement's per-statement maps so recursive
        // subquery analysis doesn't clobber them. Restored in the finally
        // at the end of this method.
        HashSet<string> previousLetNames = _currentStatementLetNames;
        Dictionary<string, HashSet<string>> previousTvfColumns = _currentStatementTvfColumns;
        _currentStatementLetNames = new(StringComparer.OrdinalIgnoreCase);
        _currentStatementTvfColumns = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            AnalyzeStatementCore(statement, diagnostics);
        }
        finally
        {
            _currentStatementLetNames = previousLetNames;
            _currentStatementTvfColumns = previousTvfColumns;
        }
    }

    private void AnalyzeStatementCore(SelectStatement statement, List<Diagnostic> diagnostics)
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

        // Register LET binding names (scalar and destructured) in the
        // per-statement LET set so references to them anywhere in the
        // statement — SELECT columns, WHERE, ORDER BY, QUALIFY, ASSERT,
        // and lateral function-source arguments — resolve as values.
        // LET names are virtual row fields computed at runtime and
        // are never present in table schemas. Registered before
        // FROM/JOIN walking so FunctionSource argument analysis
        // (CollectTableSources → AnalyzeExpression) sees them — the
        // planner lifts LET refs in lateral args into a staircase
        // above the driving source.
        //
        // Deliberately NOT added to opaqueAliases — that set is for
        // sources whose column shape we can't introspect, and its
        // mere non-emptiness suppresses every unknown-column warning.
        // Mixing LETs in would let a typo like `filex` slip past
        // whenever any LET is declared in the same SELECT.
        if (statement.LetBindings is { Count: > 0 })
        {
            foreach (LetBinding letBinding in statement.LetBindings)
            {
                if (letBinding.Destructure is not null)
                {
                    foreach (string name in letBinding.Destructure.Names)
                    {
                        _currentStatementLetNames.Add(name);
                    }
                }
                else
                {
                    _currentStatementLetNames.Add(letBinding.Name);
                }
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
            // LET names are already in opaqueAliases (registered above), so ASSERT
            // predicates automatically inherit suppression of unknown-column warnings.
            foreach (AssertClause assertClause in statement.Assertions)
            {
                AnalyzeExpression(assertClause.Predicate, aliasToTable, opaqueAliases, diagnostics);
                if (assertClause.Message is not null)
                {
                    AnalyzeExpression(assertClause.Message, aliasToTable, opaqueAliases, diagnostics);
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
                // Resolve against the manifest's table list. Explicit
                // schema → exact lookup; unqualified → walk search_path
                // (first hit wins, matching the engine's runtime
                // SchemaResolver). The alias map gets the resolved
                // qualified name so downstream column references against
                // either alias or raw name find the right columns.
                if (!TryResolveTableReference(
                        tableReference.SchemaName,
                        tableReference.Name,
                        opaqueAliases,
                        out string resolvedQualifiedName,
                        out string? resolutionError))
                {
                    EmitWarning(diagnostics, tableReference.Span, resolutionError!);
                }

                string effectiveName = tableReference.Alias ?? tableReference.Name;
                aliasToTable[effectiveName] = resolvedQualifiedName;
                if (tableReference.Alias is not null)
                {
                    aliasToTable[tableReference.Alias] = resolvedQualifiedName;
                }
                else
                {
                    // No user alias — also register the fully-qualified key so
                    // a three-part column ref `schema.table.col` finds the
                    // table when written without an alias. (PG hides the
                    // qualified name behind an alias, so we only add this
                    // when one wasn't supplied.)
                    aliasToTable[resolvedQualifiedName] = resolvedQualifiedName;
                }

                break;

            case FunctionSource functionSource:
                // S7b: `FunctionName` is the bare name (post-split); manifest
                // entries are bare today (S7e will add schema awareness).
                if (!_functionNames.Contains(functionSource.FunctionName))
                {
                    EmitWarning(diagnostics, functionSource.Span,
                        $"Unknown function '{functionSource.CallName}'.");
                }

                // Try to resolve the TVF's known output column NAMES.
                // When successful (manifest carries OutputColumns, or
                // the TVF is one of the per-name hardcoded fallbacks
                // like `unnest`), register the column set so unknown-
                // column checks fail on bare refs the TVF doesn't
                // produce (catches typos like `filex` instead of
                // `file` in queries that join a TVF). When the
                // resolver returns null (truly dynamic-shape TVFs we
                // can't introspect), fall back to opaque registration —
                // the historical behaviour.
                string functionAliasKey = functionSource.Alias ?? functionSource.CallName;
                HashSet<string>? tvfColumns = TryGetTvfOutputColumns(functionSource);
                if (tvfColumns is not null)
                {
                    _currentStatementTvfColumns[functionAliasKey] = tvfColumns;
                }
                else
                {
                    opaqueAliases.Add(functionAliasKey);
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
                if (TryResolveProcedureCall(functionCall) is { } procedureInExpr)
                {
                    // S7d locks the rule: procedures REQUIRE CALL. Surface
                    // a specific diagnostic here so the user sees the
                    // CALL nudge instead of falling through to the
                    // "unknown function" branch.
                    EmitWarning(diagnostics, functionCall.Span,
                        $"'{procedureInExpr.SchemaName}.{procedureInExpr.Name}' is a procedure; " +
                        $"invoke it via CALL {procedureInExpr.SchemaName}.{procedureInExpr.Name}(...).");
                }
                else if (ResolvesToFunction(functionCall))
                {
                    ValidateFunctionArgTypes(functionCall, aliasToTable, opaqueAliases, diagnostics);
                }
                else
                {
                    EmitWarning(diagnostics, functionCall.Span,
                        $"Unknown function '{functionCall.CallName}'.");
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

            case AtTimeZoneExpression atz:
                AnalyzeExpression(atz.Expression, aliasToTable, opaqueAliases, diagnostics);
                AnalyzeExpression(atz.TimeZone, aliasToTable, opaqueAliases, diagnostics);
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

            // ScanExpression — validate body, init, and window sub-expressions.
            case ScanExpression scanExpr:
                foreach (Expression body in scanExpr.BodyExpressions)
                {
                    AnalyzeExpression(body, aliasToTable, opaqueAliases, diagnostics);
                }

                foreach (Expression init in scanExpr.InitExpressions)
                {
                    AnalyzeExpression(init, aliasToTable, opaqueAliases, diagnostics);
                }

                if (scanExpr.Window.PartitionBy is not null)
                {
                    foreach (Expression partition in scanExpr.Window.PartitionBy)
                    {
                        AnalyzeExpression(partition, aliasToTable, opaqueAliases, diagnostics);
                    }
                }

                if (scanExpr.Window.OrderBy is not null)
                {
                    foreach (OrderByItem orderByItem in scanExpr.Window.OrderBy)
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

            // IndexAccessExpression — validate source and each index sub-expression.
            case IndexAccessExpression indexAccess:
                AnalyzeExpression(indexAccess.Source, aliasToTable, opaqueAliases, diagnostics);
                foreach (Expression i in indexAccess.Indices)
                    AnalyzeExpression(i, aliasToTable, opaqueAliases, diagnostics);
                break;

            // LiteralExpression — nothing to validate.
        }
    }

    /// <summary>
    /// Checks whether a function call resolves to a registered function:
    /// either a built-in (in <c>system</c>), a UDF on the session
    /// search_path, or a registered model under the <c>models.</c>
    /// namespace. Explicit qualification narrows the lookup to the
    /// named schema. Unqualified names walk the search_path.
    /// </summary>
    private bool ResolvesToFunction(FunctionCallExpression call)
    {
        // Built-in functions live in system. An explicit non-system
        // qualifier should not match a built-in.
        if (call.SchemaName is null
            || string.Equals(call.SchemaName, "system", StringComparison.OrdinalIgnoreCase))
        {
            if (_functionNames.Contains(call.FunctionName)) return true;
        }

        // `models.X(...)` — registered models live in their own catalog,
        // not the UDF or function registry. Without this check, every
        // `models.foo(...)` call would surface as `Unknown function`
        // even though the engine resolves it fine at runtime.
        if (call.SchemaName is not null
            && string.Equals(call.SchemaName, "models", StringComparison.OrdinalIgnoreCase)
            && _manifest.Models is { } models)
        {
            foreach (ModelEntry model in models)
            {
                if (string.Equals(model.Name, call.FunctionName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        if (_manifest.Udfs is null) return false;
        if (call.SchemaName is not null)
        {
            foreach (UdfEntry udf in _manifest.Udfs)
            {
                if (string.Equals(udf.SchemaName, call.SchemaName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(udf.Name, call.FunctionName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
        foreach (string schema in _manifest.SearchPath)
        {
            foreach (UdfEntry udf in _manifest.Udfs)
            {
                if (string.Equals(udf.SchemaName, schema, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(udf.Name, call.FunctionName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Returns the matching procedure entry when <paramref name="call"/>
    /// resolves to a procedure (explicit-schema exact-match, or
    /// search_path walk for unqualified names). Used by the
    /// procedure-in-expression diagnostic.
    /// </summary>
    private ProcedureEntry? TryResolveProcedureCall(FunctionCallExpression call)
    {
        if (_manifest.Procedures is null) return null;
        if (call.SchemaName is not null)
        {
            foreach (ProcedureEntry proc in _manifest.Procedures)
            {
                if (string.Equals(proc.SchemaName, call.SchemaName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(proc.Name, call.FunctionName, StringComparison.OrdinalIgnoreCase))
                {
                    return proc;
                }
            }
            return null;
        }
        foreach (string schema in _manifest.SearchPath)
        {
            foreach (ProcedureEntry proc in _manifest.Procedures)
            {
                if (string.Equals(proc.SchemaName, schema, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(proc.Name, call.FunctionName, StringComparison.OrdinalIgnoreCase))
                {
                    return proc;
                }
            }
        }
        return null;
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

        // Inferred argument types — computed once, reused across every
        // overload shape. Null entries mean "can't infer" and skip the
        // per-shape match without prejudice.
        string?[] actualKinds = new string?[functionCall.Arguments.Count];
        for (int i = 0; i < functionCall.Arguments.Count; i++)
        {
            actualKinds[i] = TryInferType(functionCall.Arguments[i], aliasToTable, opaqueAliases);
        }

        // Try the primary shape first, then each registered overload. The
        // call validates if any shape's per-arg types all match (or are
        // unresolvable, in which case we conservatively pass). Without
        // this fallback the analyzer warned on every legitimate use of an
        // overload past the first variant (e.g. `point_cloud_from_depth_pinhole`
        // taking `Array<Float32>` depth instead of `Image`).
        Diagnostic? primaryFailure = TryMatchShape(functionCall, signature.Parameters, actualKinds);
        if (primaryFailure is null) return;

        if (signature.AdditionalParameterShapes is { } alternatives)
        {
            foreach (IReadOnlyList<ParameterSignature> shape in alternatives)
            {
                if (TryMatchShape(functionCall, shape, actualKinds) is null) return;
            }
        }

        diagnostics.Add(primaryFailure);
    }

    /// <summary>
    /// Validates <paramref name="actualKinds"/> against one parameter shape.
    /// Returns <see langword="null"/> when every resolvable arg is type-compatible
    /// with its slot; returns a populated <see cref="Diagnostic"/> (positioned at
    /// the call site) describing the first mismatch otherwise. The caller emits
    /// the diagnostic only after every overload has been tried.
    /// </summary>
    private static Diagnostic? TryMatchShape(
        FunctionCallExpression functionCall,
        IReadOnlyList<ParameterSignature> shape,
        string?[] actualKinds)
    {
        // Argument-count gate. Required count = non-optional fixed
        // parameters; max count = unbounded when a variadic is present,
        // otherwise the total parameter count. Catch arity mismatches up
        // front so the editor squiggles immediately rather than waiting
        // until runtime's `Validate<T>` throws mid-execution.
        (int requiredCount, int maxCount) = ComputeArityBounds(shape);
        if (actualKinds.Length < requiredCount || actualKinds.Length > maxCount)
        {
            string expectedDesc = maxCount == int.MaxValue
                ? $"at least {requiredCount}"
                : requiredCount == maxCount
                    ? requiredCount.ToString()
                    : $"{requiredCount}–{maxCount}";
            return BuildDiagnostic(
                functionCall,
                $"{functionCall.FunctionName}() expects {expectedDesc} argument(s); got {actualKinds.Length}.");
        }

        for (int argumentIndex = 0; argumentIndex < actualKinds.Length; argumentIndex++)
        {
            // If the function has fewer parameters than arguments (variadic), apply
            // the last parameter's constraint to all extra arguments.
            int parameterIndex = System.Math.Min(argumentIndex, shape.Count - 1);
            if (parameterIndex < 0) break;

            ParameterSignature parameter = shape[parameterIndex];

            // "Any" means no type restriction — skip.
            if (parameter.Kind.Equals("Any", StringComparison.OrdinalIgnoreCase)) continue;

            string? actualKind = actualKinds[argumentIndex];
            if (actualKind is null)
            {
                // Cannot determine type — skip rather than risk a false positive.
                continue;
            }

            if (!IsTypeCompatible(actualKind, parameter.Kind))
            {
                return BuildDiagnostic(
                    functionCall,
                    $"Argument {argumentIndex + 1} of {functionCall.FunctionName}() expects {parameter.Kind}, got {actualKind}.");
            }
        }
        return null;
    }

    /// <summary>
    /// Computes <c>(required, max)</c> argument-count bounds for a
    /// parameter shape. Non-optional fixed parameters set the required
    /// minimum; a trailing variadic slot (identified by the leading
    /// <c>"..."</c> prefix on its name — the convention the manifest
    /// builder renders for variadic specs) makes the max unbounded.
    /// </summary>
    private static (int Required, int Max) ComputeArityBounds(IReadOnlyList<ParameterSignature> shape)
    {
        int required = 0;
        bool hasVariadic = false;
        foreach (ParameterSignature p in shape)
        {
            if (p.Name.StartsWith("...", StringComparison.Ordinal))
            {
                hasVariadic = true;
                continue;
            }
            if (!p.IsOptional) required++;
        }
        int max = hasVariadic
            ? int.MaxValue
            // Total fixed-parameter count: every entry that isn't the
            // variadic placeholder.
            : shape.Count(p => !p.Name.StartsWith("...", StringComparison.Ordinal));
        return (required, max);
    }

    private static Diagnostic BuildDiagnostic(FunctionCallExpression functionCall, string message) =>
        new()
        {
            Message = message,
            Severity = DiagnosticSeverity.Warning,
            StartLine = functionCall.Span?.Line is { } line ? System.Math.Max(0, line - 1) : 0,
            StartColumn = functionCall.Span?.Column is { } col ? System.Math.Max(0, col - 1) : 0,
            EndLine = functionCall.Span?.Line is { } line2 ? System.Math.Max(0, line2 - 1) : 0,
            EndColumn = functionCall.Span?.Column is { } col2
                ? System.Math.Max(0, col2 - 1) + (functionCall.Span?.Length ?? 0)
                : 0,
        };

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
            FunctionCallExpression functionCall => TryInferFunctionCallType(functionCall, aliasToTable, opaqueAliases),
            UnaryExpression { Operator: UnaryOperator.Negate } unary =>
                TryInferType(unary.Operand, aliasToTable, opaqueAliases),
            _ => null,
        };
    }

    /// <summary>
    /// Infers a function call's return type. Most functions return the
    /// manifest's static <see cref="FunctionSignature.ReturnType"/>
    /// directly. <c>array(…)</c> is the one structurally-polymorphic case
    /// we resolve here: the parser desugars every <c>[a, b, c]</c> literal
    /// into <c>array(a, b, c)</c>, so a malformed return type for it
    /// would warn on every legitimate array literal flowing into a typed
    /// slot. Infer the element kind from the first argument and wrap it
    /// in <c>Array&lt;…&gt;</c> — falls back to the manifest string when
    /// the args can't be resolved.
    /// </summary>
    private string? TryInferFunctionCallType(
        FunctionCallExpression call,
        Dictionary<string, string> aliasToTable,
        HashSet<string> opaqueAliases)
    {
        if (string.Equals(call.FunctionName, "array", StringComparison.OrdinalIgnoreCase)
            && call.Arguments.Count > 0)
        {
            string? element = TryInferType(call.Arguments[0], aliasToTable, opaqueAliases);
            if (element is not null) return $"Array<{element}>";
            // Fall through to the manifest string when the first arg
            // can't be resolved — better to skip the check than warn.
        }

        return _functionSignatures.TryGetValue(call.FunctionName, out FunctionSignature? sig)
            ? sig.ReturnType
            : null;
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
            string qualifier = column.SchemaName is not null
                ? $"{column.SchemaName}.{column.TableName}"
                : column.TableName;

            if (opaqueAliases.Contains(qualifier))
            {
                return null;
            }

            if (aliasToTable.TryGetValue(qualifier, out string? resolvedTable) &&
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
    /// Array-aware: <c>Array&lt;X&gt;</c> matches <c>Array&lt;Y&gt;</c> when
    /// <c>X</c> is compatible with <c>Y</c>; scalar-vs-array mismatches are
    /// rejected so a model returning <c>Array&lt;Float32&gt;</c> can flow
    /// into a <c>Float32[]</c> parameter without warning, but a bare
    /// <c>Float32</c> into the same parameter still surfaces a diagnostic.
    /// </summary>
    private static bool IsTypeCompatible(string actualKind, string expectedKind)
    {
        // StringEnumMatcher (and any future descriptive-matcher) renders its
        // Kind as `"<BaseKind> (...)"` — the parenthesised tail is an LS /
        // hover hint, not part of the type. Strip it before comparison so a
        // `'add'` argument (actualKind = "String") matches a parameter slot
        // whose expectedKind is "String (one of 17 values)".
        int parenIndex = expectedKind.IndexOf(" (", StringComparison.Ordinal);
        if (parenIndex > 0)
        {
            expectedKind = expectedKind[..parenIndex];
        }

        if (actualKind.Equals(expectedKind, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Array-wrapped kinds: recurse on the element if both sides are
        // arrays; reject if exactly one side is. Without this branch a
        // legitimate `Array<Int32>` flowing into `Array<Float32>` would
        // miss the numeric-family compatibility check below — the string
        // tests look at the wrapped names, not the elements.
        bool actualIsArray = TryUnwrapArray(actualKind, out string actualElement);
        bool expectedIsArray = TryUnwrapArray(expectedKind, out string expectedElement);
        if (actualIsArray != expectedIsArray) return false;
        if (actualIsArray && expectedIsArray)
        {
            return IsTypeCompatible(actualElement, expectedElement);
        }

        // Family-labelled expected kind (e.g. `NumericScalar`,
        // `IntegerFamily`). The function-signature path uses
        // `DataKindMatcher.Family(...)` and the matcher's Describe()
        // surfaces the family enum name as the parameter's Kind string.
        // Without this branch every concrete numeric / temporal kind
        // gets rejected against a family-typed parameter.
        if (KindFamilies.TryGetValue(expectedKind, out HashSet<string>? expectedFamilyMembers)
            && expectedFamilyMembers.Contains(actualKind))
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

        // Both temporal → compatible (Date/Timestamp/TimestampTz/Time/Duration).
        if (TemporalKinds.Contains(actualKind) && TemporalKinds.Contains(expectedKind))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Unwraps an <c>Array&lt;...&gt;</c> kind string to its element kind.
    /// Returns <see langword="false"/> for non-array kinds. Tolerates the
    /// case-insensitive <c>"array"</c> prefix and a single matching pair of
    /// angle brackets — manifest writers always produce the canonical form,
    /// but compatibility checks pass through whatever the user typed.
    /// </summary>
    private static bool TryUnwrapArray(string kind, out string element)
    {
        const string prefix = "Array<";
        if (kind.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && kind.EndsWith(">", StringComparison.Ordinal))
        {
            element = kind[prefix.Length..^1];
            return true;
        }
        element = kind;
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
            // A 3-part dot chain `a.b.c` is ambiguous: it can mean
            // `schema.table.column` OR `alias.column.structField`. The parser
            // hands us the first reading by default. When the first segment is
            // a known alias in scope, prefer the struct-field reading and skip
            // qualifier/column validation — static struct-field metadata
            // doesn't flow through here today (TVF output schemas don't carry
            // ColumnInfo.Fields), so false-positives on real struct accesses
            // would outweigh the rare miss on a typo'd 3-part qualifier.
            if (column.SchemaName is not null
                && (aliasToTable.ContainsKey(column.SchemaName)
                    || opaqueAliases.Contains(column.SchemaName)
                    || _currentStatementTvfColumns.ContainsKey(column.SchemaName)))
            {
                return;
            }

            // Three-part `schema.table.column` uses the fully-qualified
            // form as its alias-map key (registered by CollectTableSources
            // when the table reference had no alias).
            string qualifier = column.SchemaName is not null
                ? $"{column.SchemaName}.{column.TableName}"
                : column.TableName;

            // Known-TVF qualified reference (`c.value` where `c` is
            // `unnest(...) c` with statically-known output columns):
            // validate the column NAME against the known set.
            if (_currentStatementTvfColumns.TryGetValue(qualifier, out HashSet<string>? tvfCols))
            {
                if (!tvfCols.Contains(column.ColumnName))
                {
                    EmitWarning(diagnostics, column.Span,
                        $"Unknown column '{column.ColumnName}' produced by '{qualifier}'.");
                }
                return;
            }

            ValidateTableQualifier(qualifier, column.Span, aliasToTable, opaqueAliases, diagnostics);

            // If the table is opaque, skip column validation.
            if (opaqueAliases.Contains(qualifier))
            {
                return;
            }

            if (aliasToTable.TryGetValue(qualifier, out string? resolvedTable) &&
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

        // Bare reference to a FROM/JOIN alias used as a value (e.g.
        // `image_draw_bounding_boxes(file, c)` where `c` was meant to be
        // `c.value`). The engine treats an alias as a qualifier only —
        // referencing it as a scalar throws at runtime with "Name 'c' is
        // not a declared variable in scope and is not a column in the
        // current row." Surface the misuse here with a more actionable
        // message so the user fixes it before execution. Runs before the
        // unknown-column scan so a column happening to share a name with
        // an alias still goes through normal column resolution (extremely
        // rare in practice). LET binding names are excluded — they're
        // virtual row values and referencing them bare is the intended
        // use (e.g. `unnest(classes)` where `classes` is a LET).
        if ((aliasToTable.ContainsKey(column.ColumnName)
                || opaqueAliases.Contains(column.ColumnName)
                || _currentStatementTvfColumns.ContainsKey(column.ColumnName))
            && !_currentStatementLetNames.Contains(column.ColumnName))
        {
            EmitWarning(diagnostics, column.Span,
                $"'{column.ColumnName}' is a table or subquery alias, not a column. " +
                $"Use '{column.ColumnName}.<column>' to reference one of its columns.");
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

        // Also walk TVF aliases whose output column NAMES we resolved
        // statically — refs to `value` after `unnest(...) c` succeed
        // even though `value` isn't on the driving table.
        if (!found)
        {
            foreach (KeyValuePair<string, HashSet<string>> entry in _currentStatementTvfColumns)
            {
                if (entry.Value.Contains(column.ColumnName))
                {
                    found = true;
                    break;
                }
            }
        }

        // LET binding names are valid bare references throughout the
        // statement — they're virtual row values, not row columns.
        // Tracked separately from opaqueAliases so their presence
        // doesn't blanket-suppress unknown-column warnings (the old
        // behaviour swallowed typos like `filex` whenever any LET was
        // declared in the same SELECT).
        if (!found && _currentStatementLetNames.Contains(column.ColumnName))
        {
            found = true;
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
    /// Best-effort lookup of a TVF source's static output column names.
    /// Pulls from the manifest's
    /// <see cref="FunctionSignature.OutputColumns"/> when populated;
    /// falls back to per-TVF hardcoded knowledge for dynamic-schema
    /// TVFs whose column names are still constant (today: <c>unnest</c>
    /// always emits a single column named <c>value</c>). Returns
    /// <see langword="null"/> for TVFs whose output we genuinely can't
    /// introspect — the caller treats those as opaque.
    /// </summary>
    private HashSet<string>? TryGetTvfOutputColumns(FunctionSource source)
    {
        if (_functionSignatures.TryGetValue(source.FunctionName, out FunctionSignature? sig)
            && sig.OutputColumns is { Count: > 0 } outputColumns)
        {
            HashSet<string> columns = new(StringComparer.OrdinalIgnoreCase);
            foreach (TableColumnEntry col in outputColumns)
            {
                columns.Add(col.Name);
            }
            return columns;
        }

        // Hardcoded fallback for TVFs whose schema is dynamic in the
        // manifest but whose column NAMES are constant. `unnest(arr)`
        // always produces `{ value }`; the value's kind follows the
        // array element, but the validator only needs the name here.
        if (string.Equals(source.FunctionName, "unnest", StringComparison.OrdinalIgnoreCase))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "value" };
        }

        return null;
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

    // Virtual-schema knowledge moved into the manifest in S5 — every
    // registered provider (system / information_schema / system)
    // surfaces in `_manifest.Tables` with its full qualified name, so
    // the analyzer's lookup against the manifest replaces what was once
    // a hardcoded list here.

    /// <summary>
    /// Mirrors the runtime <c>SchemaResolver</c> against the manifest:
    /// explicit schemas land at <c>schema.table</c>; unqualified names
    /// walk the manifest's <c>search_path</c> and accept the first
    /// schema with a matching table.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the reference resolves (or is treated
    /// as opaque). <see langword="false"/> with <paramref name="error"/>
    /// populated when no schema satisfies the reference.
    /// </returns>
    private bool TryResolveTableReference(
        string? explicitSchema,
        string tableName,
        HashSet<string> opaqueAliases,
        out string resolvedQualifiedName,
        out string? error)
    {
        if (explicitSchema is not null)
        {
            string qualified = $"{explicitSchema}.{tableName}";
            resolvedQualifiedName = qualified;

            if (_tableColumnTypes.ContainsKey(qualified) || opaqueAliases.Contains(qualified))
            {
                error = null;
                return true;
            }

            // Distinguish "schema doesn't exist" from "table not in schema".
            bool schemaExists = false;
            foreach (string existing in _tableColumnTypes.Keys)
            {
                int dot = existing.IndexOf('.');
                if (dot > 0 && string.Equals(existing[..dot], explicitSchema, StringComparison.OrdinalIgnoreCase))
                {
                    schemaExists = true;
                    break;
                }
            }
            error = schemaExists
                ? $"Table '{tableName}' does not exist in schema '{explicitSchema}'."
                : $"Schema '{explicitSchema}' does not exist.";
            return false;
        }

        // Unqualified: walk search_path.
        foreach (string schema in _manifest.SearchPath)
        {
            string candidate = $"{schema}.{tableName}";
            if (_tableColumnTypes.ContainsKey(candidate))
            {
                resolvedQualifiedName = candidate;
                error = null;
                return true;
            }
        }

        // Bare lookups (no schema prefix in the manifest, e.g. from
        // older offline manifests that predate S1a's `system.X` rename
        // or test fixtures that register tables without a schema) also
        // count as a hit.
        if (_tableColumnTypes.ContainsKey(tableName) || opaqueAliases.Contains(tableName))
        {
            resolvedQualifiedName = tableName;
            error = null;
            return true;
        }

        resolvedQualifiedName = tableName;
        string pathDisplay = _manifest.SearchPath.Count == 0
            ? "(empty search_path)"
            : "[" + string.Join(", ", _manifest.SearchPath) + "]";
        error = $"Unknown table '{tableName}' (not found in any schema on search_path {pathDisplay}).";
        return false;
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
