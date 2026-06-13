using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution;

/// <summary>
/// Plan-time scope-aware validator that walks every <see cref="ColumnReference"/>
/// in a <see cref="QueryExpression"/> and throws
/// <see cref="ExecutionException"/> when a reference cannot resolve at
/// execution time. Catches failure classes that previously surfaced only
/// as runtime <c>InvalidOperationException</c>s from
/// <see cref="ExpressionEvaluator.EvaluateColumn"/>:
/// <list type="bullet">
///   <item>Bare alias used as a value (<c>image_draw_bounding_boxes(file, c)</c>
///   where <c>c</c> is a TVF / table / subquery alias).</item>
///   <item>Qualified <c>x.y</c> where <c>x</c> is not an alias in any
///   reachable scope.</item>
///   <item>Bare unqualified column that no source in scope claims AND
///   no opaque source / procedural variable / lambda parameter /
///   projection alias / LET name can plausibly supply.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Runs in <see cref="TableCatalog.PlanQuery"/> after
/// <see cref="PreFlightWalker"/> and before <see cref="UdfInliner"/>. Pre-
/// inliner so UDF bodies stay opaque — a UDF that references a column
/// the caller's schema doesn't expose only fails when the caller's
/// query directly references the column.
/// </para>
/// <para>
/// <strong>Conservative defaults.</strong> When the validator can't
/// confidently determine that a reference is wrong, it accepts.
/// Suppressors:
/// <list type="bullet">
///   <item>Any scope containing an opaque source (TVF, subquery,
///   CTE without resolved projection) suppresses unqualified-column
///   existence checks — the column might come from the opaque source.</item>
///   <item>Procedural variables known to the script (DECLARE,
///   FOR-counter, FOR-IN, CATCH, procedure / UDF parameters)
///   collected up-front via <see cref="ProceduralVariableCollector"/>
///   are always accepted as bare references.</item>
///   <item>Lambda parameter names are pushed onto a scope stack when
///   the walker enters a <see cref="LambdaExpression"/> body.</item>
///   <item>Projection aliases (<c>SELECT expr AS name</c>) are visible
///   to ORDER BY / HAVING / QUALIFY in the same statement.</item>
/// </list>
/// </para>
/// <para>
/// <strong>Out of scope.</strong> GROUP BY column restriction, struct
/// field correctness (the runtime's <c>Struct '...' has no field</c>
/// path is precise), per-statement procedural scope precision (the
/// collector returns every variable name anywhere in the batch, not
/// just the names actually in scope at the reference site).
/// </para>
/// </remarks>
internal sealed class QueryScopeValidator
{
    private readonly TableCatalog _catalog;
    private readonly FunctionRegistry _functions;
    private readonly HashSet<string> _knownProceduralVariables;
    private readonly Stack<HashSet<string>> _lambdaParameterStack = new();

    public QueryScopeValidator(
        TableCatalog catalog,
        FunctionRegistry functions,
        HashSet<string>? knownProceduralVariables = null)
    {
        _catalog = catalog;
        _functions = functions;
        _knownProceduralVariables = knownProceduralVariables
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Validates <paramref name="query"/>. Throws
    /// <see cref="ExecutionException"/> on the first detected misuse.
    /// </summary>
    public void Validate(QueryExpression query)
    {
        VisitQuery(query, outer: null);
    }

    private void VisitQuery(QueryExpression query, Scope? outer)
    {
        switch (query)
        {
            case SelectQueryExpression sel:
                VisitSelect(sel.Statement, outer);
                break;
            case CompoundQueryExpression compound:
                VisitQuery(compound.Left, outer);
                VisitQuery(compound.Right, outer);
                break;
            // DML/INSERT/UPDATE/DELETE deliberately not walked here —
            // they have their own validation surfaces.
        }
    }

    private void VisitSelect(SelectStatement statement, Scope? outer)
    {
        Scope localScope = new() { Parent = outer };

        // CTE bodies: walk first so name-resolution sees their projected
        // columns. Each CTE name is registered as an opaque-but-named
        // alias; columns from the body's projection get added as a
        // column set the validator can check `cte_name.col` against.
        if (statement.CommonTableExpressions is not null)
        {
            foreach (CommonTableExpression cte in statement.CommonTableExpressions)
            {
                VisitQuery(cte.Body, outer);
                HashSet<string>? cteColumns = TryCollectCteProjectionColumns(cte);
                if (cteColumns is not null)
                {
                    localScope.RegisterTableAlias(cte.Name, cteColumns);
                }
                else
                {
                    localScope.RegisterCteName(cte.Name);
                }
            }
        }

        // Register LET binding names BEFORE walking FROM/JOIN sources
        // so lateral TVF arguments (`CROSS JOIN unnest(classes) c`
        // where `classes` is declared via `LET classes = …` in the
        // same SELECT) resolve cleanly. The engine's planner lifts
        // LET refs in lateral args into a staircase above the driving
        // source — the validator just needs the names visible. RHS
        // validation is deferred until after FROM/JOIN walks (LET
        // RHS commonly references the driving source's columns like
        // `LET classes = models.X(a.file)`).
        if (statement.LetBindings is { Count: > 0 })
        {
            foreach (LetBinding binding in statement.LetBindings)
            {
                localScope.RegisterLetBinding(binding.Name);
                if (binding.Destructure is not null)
                {
                    foreach (string destructured in binding.Destructure.Names)
                    {
                        localScope.RegisterLetBinding(destructured);
                    }
                }
            }
        }

        if (statement.From is not null)
        {
            AddSourceToScope(statement.From.Source, localScope);
        }
        if (statement.Joins is not null)
        {
            foreach (JoinClause join in statement.Joins)
            {
                AddSourceToScope(join.Source, localScope);
                if (join.OnCondition is not null)
                {
                    ValidateExpression(join.OnCondition, localScope);
                }
            }
        }

        // LET RHS validation runs after FROM/JOIN — LET expressions
        // commonly reference the driving source's columns
        // (`LET classes = models.X(a.file)`). Names are already
        // registered above; this pass just checks the RHS for any
        // bad refs.
        if (statement.LetBindings is { Count: > 0 })
        {
            foreach (LetBinding binding in statement.LetBindings)
            {
                ValidateExpression(binding.Expression, localScope);
            }
        }

        // Projection alias collection — capture every SELECT AS name so
        // ORDER BY / HAVING / QUALIFY refs to those names don't trip
        // the unknown-column check. PG allows projection-alias refs in
        // ORDER BY but not WHERE; we accept them anywhere (the runtime
        // resolves correctly either way — over-acceptance is safer
        // than over-rejection).
        if (statement.Columns is not null)
        {
            foreach (SelectColumn col in statement.Columns)
            {
                if (!string.IsNullOrEmpty(col.Alias))
                {
                    localScope.RegisterProjectionAlias(col.Alias);
                }
                if (!string.IsNullOrEmpty(col.AssignedVariableName))
                {
                    localScope.RegisterProjectionAlias(col.AssignedVariableName);
                }
            }
        }

        // SELECT projection expressions.
        if (statement.Columns is not null)
        {
            foreach (SelectColumn col in statement.Columns)
            {
                if (col.Expression is not null)
                {
                    ValidateExpression(col.Expression, localScope);
                }
            }
        }

        if (statement.Where is not null) ValidateExpression(statement.Where, localScope);
        if (statement.Having is not null) ValidateExpression(statement.Having, localScope);
        if (statement.GroupBy is { Expressions: { } groupByExprs })
        {
            foreach (Expression e in groupByExprs) ValidateExpression(e, localScope);
        }
        if (statement.OrderBy is { Items: { } orderByItems })
        {
            foreach (OrderByItem o in orderByItems) ValidateExpression(o.Expression, localScope);
        }
        if (statement.Qualify is not null) ValidateExpression(statement.Qualify, localScope);
    }

    private void AddSourceToScope(TableSource source, Scope scope)
    {
        switch (source)
        {
            case TableReference tableRef:
                AddTableReferenceToScope(tableRef, scope);
                break;
            case FunctionSource functionSource:
                foreach (Expression arg in functionSource.Arguments)
                {
                    ValidateExpression(arg, scope);
                }
                string functionAlias = functionSource.Alias ?? functionSource.FunctionName;
                // Best-effort: call the TVF's ValidateArguments with
                // placeholder arg kinds to discover its output column
                // names. The names alone are enough for the validator —
                // we don't care about the kinds at this layer. When the
                // TVF accepts the call we register the names as a
                // concrete column set so unknown-column checks fire
                // correctly for refs the TVF doesn't produce (catches
                // typos like `filex` instead of `file` in queries that
                // join a TVF). On any failure (file-peeking TVFs that
                // need real paths, signature rejection, etc.) we fall
                // back to opaque registration — the historical posture.
                HashSet<string>? tvfColumns = TryResolveTvfColumnNames(functionSource);
                if (tvfColumns is not null)
                {
                    scope.RegisterTableAlias(functionAlias, tvfColumns);
                }
                else
                {
                    scope.RegisterOpaqueAlias(functionAlias);
                }
                break;
            case SubquerySource subquery:
                VisitSelect(subquery.Query, scope);
                scope.RegisterOpaqueAlias(subquery.Alias);
                break;
        }
    }

    private void AddTableReferenceToScope(TableReference tableRef, Scope scope)
    {
        string aliasName = tableRef.Alias ?? tableRef.Name;

        if (tableRef.SchemaName is null && scope.IsCteInScope(tableRef.Name))
        {
            // CTE that we couldn't statically project — opaque. The
            // walking-CTE-body branch above would've registered a
            // concrete column set when it could.
            scope.RegisterOpaqueAlias(aliasName);
            return;
        }

        if (!TryResolveTableColumns(tableRef.SchemaName, tableRef.Name, out HashSet<string>? columns)
            || columns is null)
        {
            scope.RegisterOpaqueAlias(aliasName);
            return;
        }
        scope.RegisterTableAlias(aliasName, columns);
    }

    /// <summary>
    /// Best-effort static resolver for a TVF source's output column
    /// names. Looks the TVF up in the function registry and calls
    /// <see cref="Functions.ITableValuedFunction.ValidateArguments"/>
    /// with placeholder argument kinds — for column-name validation
    /// we don't need accurate types, only the output schema's names.
    /// Returns <see langword="null"/> when the registry doesn't know
    /// the function, when the TVF's validate hook throws (file-peek
    /// TVFs that need real paths, signature rejection), or when the
    /// returned schema is empty.
    /// </summary>
    private HashSet<string>? TryResolveTvfColumnNames(FunctionSource source)
    {
        Functions.ITableValuedFunction? tvf = _functions.TryGetTableValued(source.CallName);
        if (tvf is null) return null;

        try
        {
            DataKind[] argumentKinds = new DataKind[source.Arguments.Count];
            DataValue?[] constantArguments = new DataValue?[source.Arguments.Count];
            for (int i = 0; i < source.Arguments.Count; i++)
            {
                // DataKind.Float32 is a defensive placeholder — it matches
                // DataKindMatcher.Any (the matcher used by every TVF whose
                // output names don't depend on input types) and never
                // triggers a kind-mismatch throw. TVFs that DO inspect
                // arg kinds (and reject) fail the call and we fall back
                // to opaque registration.
                argumentKinds[i] = DataKind.Float32;
                constantArguments[i] = null;
            }
            ByteArrayValueStore constantStore = new();
            Model.Schema outputSchema = tvf.ValidateArguments(
                argumentKinds, constantArguments, constantStore, cancellationToken: default);
            if (outputSchema.Columns.Count == 0) return null;

            HashSet<string> columns = new(StringComparer.OrdinalIgnoreCase);
            foreach (Model.ColumnInfo col in outputSchema.Columns)
            {
                columns.Add(col.Name);
            }
            return columns;
        }
        catch
        {
            // Any failure — signature rejection, file-peek failure on a
            // path-typed TVF, internal validation error — falls through
            // to the opaque-alias registration the caller does next.
            return null;
        }
    }

    private bool TryResolveTableColumns(
        string? explicitSchema,
        string tableName,
        out HashSet<string>? columns)
    {
        columns = null;
        try
        {
            string qualified = explicitSchema is null ? tableName : $"{explicitSchema}.{tableName}";
            Model.Schema schema = _catalog[qualified].GetSchema();
            columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Model.ColumnInfo col in schema.Columns)
            {
                columns.Add(col.Name);
            }
            return true;
        }
        catch
        {
            columns = null;
            return false;
        }
    }

    /// <summary>
    /// Best-effort static projection-column extractor for a CTE body.
    /// Walks the leftmost SELECT and pulls AS names + bare column-ref
    /// passthroughs. Returns <see langword="null"/> when the projection
    /// shape isn't statically nameable (SELECT *, table.*, compound
    /// query, missing body) — the caller treats those as opaque.
    /// </summary>
    private static HashSet<string>? TryCollectCteProjectionColumns(CommonTableExpression cte)
    {
        SelectStatement? leftmost = ExtractLeftmostSelect(cte.Body);
        if (leftmost?.Columns is null) return null;

        HashSet<string> columns = new(StringComparer.OrdinalIgnoreCase);
        foreach (SelectColumn col in leftmost.Columns)
        {
            if (col is SelectAllColumns || col is SelectTableColumns)
            {
                // Wildcard expansion — we don't statically know what
                // columns flow through. Mark the CTE as opaque.
                return null;
            }
            if (!string.IsNullOrEmpty(col.Alias))
            {
                columns.Add(col.Alias);
                continue;
            }
            if (col.Expression is ColumnReference colRef && colRef.ColumnName != "*")
            {
                columns.Add(colRef.ColumnName);
                continue;
            }
            // Expression with no alias — the engine names these via
            // ColumnNameResolver but we don't replicate that here.
            // Skip so the CTE doesn't appear to have a column we
            // can't validate against.
        }

        // Explicit `WITH cte(a, b) AS (...)` column-name list wins over
        // the inferred projection: the rename happens at the CTE
        // boundary, so downstream references must use the explicit names.
        if (cte.ColumnNames is { Count: > 0 } explicitNames)
        {
            columns.Clear();
            foreach (string name in explicitNames) columns.Add(name);
        }

        return columns;
    }

    private static SelectStatement? ExtractLeftmostSelect(QueryExpression query)
    {
        return query switch
        {
            SelectQueryExpression sel => sel.Statement,
            CompoundQueryExpression compound => ExtractLeftmostSelect(compound.Left),
            _ => null,
        };
    }

    private void ValidateExpression(Expression expression, Scope scope)
    {
        switch (expression)
        {
            case ColumnReference colRef:
                ValidateColumnReference(colRef, scope);
                return;

            case FunctionCallExpression fnCall:
                foreach (Expression arg in fnCall.Arguments)
                {
                    ValidateExpression(arg, scope);
                }
                if (fnCall.OrderBy is not null)
                {
                    foreach (OrderByItem o in fnCall.OrderBy)
                        ValidateExpression(o.Expression, scope);
                }
                if (fnCall.WithinGroupOrderBy is not null)
                {
                    foreach (OrderByItem o in fnCall.WithinGroupOrderBy)
                        ValidateExpression(o.Expression, scope);
                }
                return;

            case WindowFunctionCallExpression windowCall:
                foreach (Expression arg in windowCall.Arguments)
                {
                    ValidateExpression(arg, scope);
                }
                if (windowCall.Window.PartitionBy is not null)
                {
                    foreach (Expression p in windowCall.Window.PartitionBy)
                        ValidateExpression(p, scope);
                }
                if (windowCall.Window.OrderBy is not null)
                {
                    foreach (OrderByItem o in windowCall.Window.OrderBy)
                        ValidateExpression(o.Expression, scope);
                }
                return;

            case BinaryExpression bin:
                ValidateExpression(bin.Left, scope);
                ValidateExpression(bin.Right, scope);
                return;
            case UnaryExpression un:
                ValidateExpression(un.Operand, scope);
                return;
            case LikeExpression like:
                ValidateExpression(like.Expression, scope);
                ValidateExpression(like.Pattern, scope);
                if (like.EscapeCharacter is not null) ValidateExpression(like.EscapeCharacter, scope);
                return;
            case InExpression inExpr:
                ValidateExpression(inExpr.Expression, scope);
                foreach (Expression e in inExpr.Values) ValidateExpression(e, scope);
                return;
            case BetweenExpression bet:
                ValidateExpression(bet.Expression, scope);
                ValidateExpression(bet.Low, scope);
                ValidateExpression(bet.High, scope);
                return;
            case IsNullExpression isNull:
                ValidateExpression(isNull.Expression, scope);
                return;
            case CastExpression cast:
                ValidateExpression(cast.Expression, scope);
                return;
            case AtTimeZoneExpression atz:
                ValidateExpression(atz.Expression, scope);
                ValidateExpression(atz.TimeZone, scope);
                return;
            case CaseExpression caseExpr:
                if (caseExpr.Operand is not null) ValidateExpression(caseExpr.Operand, scope);
                foreach (WhenClause when in caseExpr.WhenClauses)
                {
                    ValidateExpression(when.Condition, scope);
                    ValidateExpression(when.Result, scope);
                }
                if (caseExpr.ElseResult is not null) ValidateExpression(caseExpr.ElseResult, scope);
                return;
            case IndexAccessExpression idx:
                ValidateExpression(idx.Source, scope);
                foreach (Expression i in idx.Indices) ValidateExpression(i, scope);
                return;
            case StructLiteralExpression structLit:
                foreach (StructField f in structLit.Fields) ValidateExpression(f.Value, scope);
                return;
            case SubqueryExpression sub:
                VisitSelect(sub.Query, scope);
                return;
            case InSubqueryExpression inSub:
                ValidateExpression(inSub.Expression, scope);
                VisitSelect(inSub.Query, scope);
                return;
            case ExistsExpression exists:
                VisitSelect(exists.Query, scope);
                return;
            case LambdaExpression lambda:
                // Push lambda parameter names onto the stack so
                // references in the body resolve cleanly. Pop on exit
                // — try/finally to keep the stack balanced if a body
                // expression throws (typo inside the body) so the next
                // walker call doesn't see stale parameters.
                HashSet<string> paramSet = new(StringComparer.OrdinalIgnoreCase);
                foreach (string p in lambda.Parameters) paramSet.Add(p);
                _lambdaParameterStack.Push(paramSet);
                try
                {
                    ValidateExpression(lambda.Body, scope);
                }
                finally
                {
                    _lambdaParameterStack.Pop();
                }
                return;
        }
        // LiteralExpression, ParameterExpression, DefaultValueExpression,
        // CurrentTimestampExpression, TypeLiteralExpression, ErrorExpression,
        // and other non-column shapes have nothing to validate.
    }

    private void ValidateColumnReference(ColumnReference colRef, Scope scope)
    {
        // Wildcard `*` survives as a ColumnReference in some shapes; skip
        // — wildcards are validated by SelectAllColumns / SelectTableColumns.
        if (colRef.ColumnName == "*") return;

        // 3-part `schema.table.column` — accept. Either a real schema-
        // qualified table reference (the runtime accessor handles via
        // row lookup) or alias.column.structField (the SemanticAnalyzer
        // disambiguates at edit time). Don't false-positive.
        if (colRef.SchemaName is not null) return;

        if (colRef.TableName is not null)
        {
            // 2-part `alias.column`. Walk local + parent scope for
            // correlation. Only fail when the qualifier is truly
            // unknown across every name-binding surface that could
            // legitimately supply a value (struct, row, scalar) for
            // the runtime to project a field from: FROM/JOIN aliases,
            // LET bindings (commonly struct-valued — e.g.
            // `LET d = models.depth(img)` then `d.depth`), projection
            // aliases (visible to ORDER BY / HAVING / QUALIFY),
            // procedural variables, and lambda parameters.
            if (ScopeKnowsAlias(scope, colRef.TableName)) return;
            if (ScopeKnowsValueBinding(scope, colRef.TableName)) return;
            if (ScopeKnowsProjectionAlias(scope, colRef.TableName)) return;
            if (_knownProceduralVariables.Contains(colRef.TableName)) return;
            if (LambdaParameterInScope(colRef.TableName)) return;
            throw new ExecutionException(
                $"Unknown table or alias '{colRef.TableName}' in reference "
                + $"'{colRef.TableName}.{colRef.ColumnName}'.");
        }

        string name = colRef.ColumnName;

        // Alias-as-value (the headline check) — bare name matches a
        // local FROM/JOIN alias. LET names live in scope.LetBindings,
        // not in the alias map, so they don't trip this.
        if (scope.HasAlias(name))
        {
            throw new ExecutionException(
                $"'{name}' is a table or subquery alias, not a column. "
                + $"Use '{name}.<column>' to reference one of its columns.");
        }

        // Accept paths — in any of these the runtime row-evaluator
        // will resolve the name and the validator should not block.
        if (ScopeKnowsValueBinding(scope, name)) return;
        if (ScopeKnowsProjectionAlias(scope, name)) return;
        if (_knownProceduralVariables.Contains(name)) return;
        if (LambdaParameterInScope(name)) return;
        if (ScopeKnowsColumn(scope, name)) return;
        if (ScopeChainContainsOpaqueSource(scope)) return;

        throw new ExecutionException($"Unknown column '{name}'.");
    }

    private bool LambdaParameterInScope(string name)
    {
        foreach (HashSet<string> frame in _lambdaParameterStack)
        {
            if (frame.Contains(name)) return true;
        }
        return false;
    }

    private static bool ScopeKnowsAlias(Scope? scope, string aliasName)
    {
        while (scope is not null)
        {
            if (scope.HasAlias(aliasName)) return true;
            scope = scope.Parent;
        }
        return false;
    }

    private static bool ScopeKnowsValueBinding(Scope? scope, string name)
    {
        while (scope is not null)
        {
            if (scope.LetBindings.Contains(name)) return true;
            scope = scope.Parent;
        }
        return false;
    }

    private static bool ScopeKnowsProjectionAlias(Scope? scope, string name)
    {
        while (scope is not null)
        {
            if (scope.ProjectionAliases.Contains(name)) return true;
            scope = scope.Parent;
        }
        return false;
    }

    private static bool ScopeKnowsColumn(Scope? scope, string name)
    {
        while (scope is not null)
        {
            foreach (HashSet<string> cols in scope.AllNonOpaqueColumnSets)
            {
                if (cols.Contains(name)) return true;
            }
            scope = scope.Parent;
        }
        return false;
    }

    private static bool ScopeChainContainsOpaqueSource(Scope? scope)
    {
        while (scope is not null)
        {
            if (scope.HasOpaqueSource) return true;
            scope = scope.Parent;
        }
        return false;
    }

    /// <summary>
    /// Per-statement validation scope: aliases, column sets for non-
    /// opaque sources, LET bindings, projection aliases (visible to
    /// ORDER BY / HAVING / QUALIFY), CTE names, parent-scope pointer
    /// for outer-correlation walks.
    /// </summary>
    private sealed class Scope
    {
        public Scope? Parent { get; init; }

        private readonly Dictionary<string, HashSet<string>?> _aliasColumns =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _cteNames = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> LetBindings { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ProjectionAliases { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool HasOpaqueSource { get; private set; }

        public IEnumerable<HashSet<string>> AllNonOpaqueColumnSets
        {
            get
            {
                foreach (HashSet<string>? cols in _aliasColumns.Values)
                {
                    if (cols is not null) yield return cols;
                }
            }
        }

        public bool HasAlias(string aliasName) => _aliasColumns.ContainsKey(aliasName);

        public bool IsCteInScope(string cteName)
        {
            if (_cteNames.Contains(cteName)) return true;
            return Parent?.IsCteInScope(cteName) ?? false;
        }

        public void RegisterTableAlias(string aliasName, HashSet<string> columns)
        {
            _aliasColumns[aliasName] = columns;
        }

        public void RegisterOpaqueAlias(string aliasName)
        {
            _aliasColumns[aliasName] = null;
            HasOpaqueSource = true;
        }

        public void RegisterCteName(string cteName)
        {
            _cteNames.Add(cteName);
        }

        public void RegisterLetBinding(string name)
        {
            LetBindings.Add(name);
        }

        public void RegisterProjectionAlias(string name)
        {
            ProjectionAliases.Add(name);
        }
    }
}
