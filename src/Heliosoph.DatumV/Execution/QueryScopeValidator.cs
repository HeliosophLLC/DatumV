using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Catalog.Registries;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution;

/// <summary>
/// Plan-time scope-aware validator that walks every <see cref="ColumnReference"/>
/// in a <see cref="QueryExpression"/> and throws
/// <see cref="ExecutionException"/> when a reference can't possibly resolve
/// at execution time. Catches three failure classes that previously only
/// surfaced as runtime <c>InvalidOperationException</c>s from
/// <see cref="ExpressionEvaluator.EvaluateColumn"/>:
/// <list type="bullet">
///   <item><description><strong>Alias used as value</strong> — bare
///   <c>c</c> where <c>c</c> is a FROM/JOIN alias (table or TVF
///   output). The runtime would throw "Name 'c' is not a declared
///   variable in scope and is not a column in the current row." after
///   producing the first row, which can take seconds-to-minutes for
///   queries with expensive upstream operators (model invocation,
///   large joins). Surfacing the misuse here makes the error visible
///   the moment the user runs the query.</description></item>
///   <item><description><strong>Unknown alias in 2-part reference</strong>
///   — <c>x.y</c> where <c>x</c> is not a FROM/JOIN alias and not an
///   outer-scope alias.</description></item>
///   <item><description><strong>Unknown unqualified column</strong> —
///   <c>x</c> where no source in scope claims a column named <c>x</c>
///   and no opaque source masks the check (TVFs and subqueries are
///   opaque — their column sets aren't statically knowable from this
///   layer).</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Runs in <see cref="TableCatalog.PlanQuery"/> after
/// <see cref="PreFlightWalker"/> and before <see cref="UdfInliner"/>. Sits
/// pre-inliner so UDF bodies (authored separately from the current
/// caller) stay opaque — a UDF that references a column the caller's
/// schema doesn't expose only fails when the caller's query directly
/// references the column.
/// </para>
/// <para>
/// <strong>Conservative defaults.</strong> When the validator can't
/// confidently determine that a reference is wrong, it accepts. False
/// positives would block valid queries; missed catches just defer the
/// error to runtime (the historical behaviour). Specifically:
/// <list type="bullet">
///   <item>Any scope containing an opaque source (TVF, subquery,
///   CTE without resolved projection) suppresses the "unknown column"
///   check for unqualified references — the column might come from the
///   opaque source.</item>
///   <item>References that fail in the local scope walk parent scopes
///   (correlated subquery support) before failing.</item>
///   <item>Unknown table/alias check skips when the qualifier matches
///   any catalog table name in the search path — the planner may resolve
///   it after UDF inlining.</item>
/// </list>
/// </para>
/// <para>
/// <strong>Out of scope (for this slice).</strong> GROUP BY column
/// restriction, aliased-projection visibility, window function frame
/// scope rules, struct-field correctness (handled by the runtime's
/// existing precise error). INSERT / UPDATE / DELETE column references
/// aren't validated here — those go through their own planner paths.
/// </para>
/// </remarks>
internal sealed class QueryScopeValidator
{
    private readonly TableCatalog _catalog;
    private readonly FunctionRegistry _functions;

    public QueryScopeValidator(TableCatalog catalog, FunctionRegistry functions)
    {
        _catalog = catalog;
        _functions = functions;
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
        // 1. Build the FROM/JOIN scope first, walking sources left-to-right
        //    so each source's argument expressions can be validated against
        //    earlier-bound aliases (lateral semantics — conservative).
        Scope localScope = new() { Parent = outer };

        // Register CTEs declared in this statement's WITH clause so
        // FROM references can resolve against them.
        if (statement.CommonTableExpressions is not null)
        {
            foreach (CommonTableExpression cte in statement.CommonTableExpressions)
            {
                // CTE projection schema is opaque to the validator (the
                // exact column set requires a recursive resolve). Register
                // the name so FROM references don't false-positive as
                // "unknown alias", and walk the body with this statement's
                // outer scope so the body's own validations run.
                localScope.RegisterCteName(cte.Name);
                VisitQuery(cte.Body, outer);
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

        // 2. LET bindings — register names AND validate their RHS against
        //    the scope built so far. LETs can reference each other in
        //    declaration order; we add each name after validating its
        //    expression.
        if (statement.LetBindings is { Count: > 0 })
        {
            foreach (LetBinding binding in statement.LetBindings)
            {
                ValidateExpression(binding.Expression, localScope);
                localScope.RegisterLetBinding(binding.Name);
            }
        }

        // 3. SELECT projection expressions.
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

        // 4. WHERE / GROUP BY / HAVING / ORDER BY — all share the FROM/JOIN
        //    scope. Aliased-projection visibility (PG-style ORDER BY by alias)
        //    is intentionally not modelled; the runtime accepts it via the
        //    same row-lookup path and we err on the side of acceptance.
        if (statement.Where is not null) ValidateExpression(statement.Where, localScope);
        if (statement.Having is not null) ValidateExpression(statement.Having, localScope);
        if (statement.GroupBy is { Expressions: { } groupByExprs })
        {
            foreach (Expression e in groupByExprs)
            {
                ValidateExpression(e, localScope);
            }
        }
        if (statement.OrderBy is { Items: { } orderByItems })
        {
            foreach (OrderByItem o in orderByItems)
            {
                ValidateExpression(o.Expression, localScope);
            }
        }
    }

    /// <summary>
    /// Resolves a <see cref="TableSource"/> against the catalog and
    /// registers it in <paramref name="scope"/>. Plain table references
    /// contribute their column names (when the table resolves in the
    /// catalog); TVF and subquery sources register as opaque aliases.
    /// Subquery bodies are recursively validated with the current scope
    /// as their outer (correlation support).
    /// </summary>
    private void AddSourceToScope(TableSource source, Scope scope)
    {
        switch (source)
        {
            case TableReference tableRef:
                AddTableReferenceToScope(tableRef, scope);
                break;
            case FunctionSource functionSource:
                // TVF argument expressions are validated against the
                // already-built scope (lateral semantics — they can
                // reference earlier FROM sources).
                foreach (Expression arg in functionSource.Arguments)
                {
                    ValidateExpression(arg, scope);
                }
                // The TVF's output column set is opaque to this validator
                // (static OutputColumns aren't reliably available across
                // every TVF, and call-site synthesis like unnest needs the
                // arg's type which we don't track here). Register the
                // alias name so qualified `c.value` doesn't false-positive
                // as "unknown alias", and mark the scope as containing an
                // opaque source so unqualified-column checks soften.
                string functionAlias = functionSource.Alias ?? functionSource.FunctionName;
                scope.RegisterOpaqueAlias(functionAlias);
                break;
            case SubquerySource subquery:
                // Recurse into the subquery with the current scope as its
                // outer — this is what makes correlated subqueries
                // validate correctly. The subquery's own validation runs
                // against its own FROM/JOIN sources plus the outer scope.
                VisitSelect(subquery.Query, scope);
                scope.RegisterOpaqueAlias(subquery.Alias);
                break;
        }
    }

    private void AddTableReferenceToScope(TableReference tableRef, Scope scope)
    {
        string aliasName = tableRef.Alias ?? tableRef.Name;

        // CTE name lookup first — bare unqualified references against the
        // current WITH-clause shadow catalog tables.
        if (tableRef.SchemaName is null && scope.IsCteInScope(tableRef.Name))
        {
            scope.RegisterOpaqueAlias(aliasName);
            return;
        }

        // Resolve through the catalog. If the table doesn't resolve, the
        // planner will throw a precise error later — we just register the
        // alias as opaque so later expressions don't double-fail.
        if (!TryResolveTableColumns(tableRef.SchemaName, tableRef.Name, out HashSet<string>? columns)
            || columns is null)
        {
            scope.RegisterOpaqueAlias(aliasName);
            return;
        }
        scope.RegisterTableAlias(aliasName, columns);
    }

    /// <summary>
    /// Looks up <paramref name="explicitSchema"/>.<paramref name="tableName"/>
    /// (or a search-path walk when the schema is null) in the catalog and
    /// returns its column names. Returns <see langword="false"/> when the
    /// table doesn't resolve through any reachable schema — the caller
    /// registers an opaque alias instead of failing the whole validation.
    /// </summary>
    private bool TryResolveTableColumns(
        string? explicitSchema,
        string tableName,
        out HashSet<string>? columns)
    {
        columns = null;
        try
        {
            // Views resolve through the catalog like tables; their projected
            // schema flows back through the same indexer surface so the
            // table-by-name path handles both. The indexer throws on miss
            // — wrap in a try so a missing table never blocks validation.
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
            // Catalog lookup failures (KeyNotFoundException for missing
            // table / view, race with concurrent DDL, missing virtual-
            // schema provider, …) all collapse to "unknown" so the
            // validator never blocks plan-time on a transient catalog miss.
            columns = null;
            return false;
        }
    }

    /// <summary>
    /// Walks <paramref name="expression"/> and validates every
    /// <see cref="ColumnReference"/> against <paramref name="scope"/>.
    /// Recurses into every sub-expression shape so nested calls
    /// (<c>image_draw_bounding_boxes(file, c)</c>) and compound
    /// expressions (<c>WHERE c.value.label = 'person'</c>) all get
    /// visited.
    /// </summary>
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
                    {
                        ValidateExpression(o.Expression, scope);
                    }
                }
                if (fnCall.WithinGroupOrderBy is not null)
                {
                    foreach (OrderByItem o in fnCall.WithinGroupOrderBy)
                    {
                        ValidateExpression(o.Expression, scope);
                    }
                }
                return;

            case WindowFunctionCallExpression windowCall:
                foreach (Expression arg in windowCall.Arguments)
                {
                    ValidateExpression(arg, scope);
                }
                // Window partition / order expressions can reference scope
                // columns too; walk them via the WindowSpecification.
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
                // Lambda parameters bind names locally. Skip body
                // validation rather than over-permit or false-positive:
                // lambda bodies use the row scope plus the lambda's own
                // parameters, which we don't currently track precisely.
                _ = lambda;
                return;
        }
        // LiteralExpression, ParameterExpression, DefaultValueExpression,
        // CurrentTimestampExpression, TypeLiteralExpression, ErrorExpression,
        // and other non-column shapes have nothing to validate.
    }

    private static void ValidateColumnReference(ColumnReference colRef, Scope scope)
    {
        // Wildcard `*` survives as a ColumnReference in some shapes; skip
        // — wildcards are validated by SelectAllColumns / SelectTableColumns.
        if (colRef.ColumnName == "*") return;

        // Qualified references (2-part and 3-part) defer to the runtime's
        // existing accessor — the validator doesn't track every name
        // surface that the row-evaluator does (procedural FOR-row
        // variables, CALL bindings, struct-field walks, etc.) so an
        // over-eager "unknown alias" check would false-positive on valid
        // code. The LSP's SemanticAnalyzer covers the edit-time signal.
        if (colRef.TableName is not null) return;

        // Bare unqualified reference. Only one check fires here: a name
        // that matches a FROM/JOIN alias in the *local* scope is the
        // "alias used as value" misuse. The runtime would otherwise
        // emit "Name 'X' is not a declared variable in scope and is
        // not a column in the current row." after the upstream
        // operator produces its first row — which can take seconds-to-
        // minutes for queries with model invocations or large joins.
        // Surfacing the misuse at plan time saves that wait.
        //
        // Skipped fallbacks:
        //   - LET bindings, DECLARE'd variables, lambda parameters,
        //     FOR-loop counters, projection aliases, aggregate results,
        //     CALL block bindings — the runtime row-lookup path resolves
        //     all of these, and they aren't tracked here. The runtime's
        //     existing "Name 'X' is not …" error still fires for genuine
        //     typos. Catching those at plan time would require threading
        //     every variable scope through this walker — out of scope
        //     for this slice.
        string name = colRef.ColumnName;
        if (ScopeAliasUsedAsValue(scope, name))
        {
            throw new ExecutionException(
                $"'{name}' is a table or subquery alias, not a column. "
                + $"Use '{name}.<column>' to reference one of its columns.");
        }
    }

    private static bool ScopeAliasUsedAsValue(Scope? scope, string name)
    {
        // Only check the *local* scope. Outer-scope aliases are visible
        // to correlated subqueries only through qualification — bare
        // unqualified references in a subquery have always meant
        // "row column", never "outer alias". So we don't walk parents.
        return scope is not null && scope.HasAlias(name);
    }

    /// <summary>
    /// Per-statement validation scope. Carries the aliases bound by this
    /// statement's FROM/JOIN, the column sets for non-opaque sources,
    /// LET binding names, and a pointer to the enclosing scope for
    /// outer-correlation walks.
    /// </summary>
    private sealed class Scope
    {
        public Scope? Parent { get; init; }

        private readonly Dictionary<string, HashSet<string>?> _aliasColumns =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _cteNames = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> LetBindings { get; } = new(StringComparer.OrdinalIgnoreCase);

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

        public bool TryGetAliasColumns(string aliasName, out HashSet<string>? cols)
        {
            if (_aliasColumns.TryGetValue(aliasName, out cols)) return true;
            cols = null;
            return false;
        }

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
    }
}
