using DatumIngest.Catalog;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

/// <summary>
/// Plan-time AST rewrite that replaces every <c>udf.&lt;name&gt;(args...)</c>
/// call with the substituted body of the registered <see cref="UdfDescriptor"/>.
/// UDFs are macros: by the time the planner builds operators, no UDF call
/// sites remain in the tree.
/// </summary>
/// <remarks>
/// <para>
/// Inlining order — children-first, then UDF substitution at this node.
/// Argument expressions may contain nested UDF calls; those are inlined
/// before substitution into the parent UDF's body so the substituted
/// expressions are already fully resolved.
/// </para>
/// <para>
/// Cycle detection — direct self-reference and indirect cycles
/// (<c>A → B → A</c>) are detected via a name stack. The first call site
/// that closes the cycle throws <see cref="InvalidOperationException"/>
/// with the full chain in the message. Cycles cannot be detected at
/// CREATE FUNCTION time alone because B may be created before A; the
/// detection therefore lives at inlining time.
/// </para>
/// <para>
/// Parameter shadowing — substitution targets bare
/// <see cref="ColumnReference"/> nodes whose name matches a UDF parameter.
/// Lambda parameters and SCAN accumulator names are honoured: a
/// <see cref="LambdaExpression"/> or <see cref="ScanExpression"/> inside
/// the body removes its bound names from the substitution map for the
/// duration of the nested walk, so a UDF parameter accidentally named
/// the same as a lambda parameter does not capture the inner reference.
/// </para>
/// </remarks>
public static class UdfInliner
{
    /// <summary>The namespace prefix that flags a function call as a UDF invocation.</summary>
    public const string UdfNamespacePrefix = "udf.";

    /// <summary>
    /// Walks <paramref name="query"/> and returns a new <see cref="QueryExpression"/>
    /// in which every UDF call has been replaced with its substituted body.
    /// </summary>
    /// <remarks>
    /// Walks even when the registry is empty so a query that references a
    /// UDF the catalog doesn't know about surfaces a clear "not registered"
    /// error rather than passing through unchanged and failing later in the
    /// planner with a less obvious message.
    /// </remarks>
    public static QueryExpression Inline(QueryExpression query, UdfRegistry registry)
    {
        Inliner inliner = new(registry);
        return inliner.RewriteQuery(query);
    }

    /// <summary>
    /// Inlines UDF calls in a single <see cref="Expression"/>. Useful for
    /// validating a UDF body at registration time (so cycles and unknown
    /// references surface there rather than at the first call site) and
    /// for tests.
    /// </summary>
    public static Expression Inline(Expression expression, UdfRegistry registry)
    {
        Inliner inliner = new(registry);
        return inliner.Rewrite(expression);
    }

    private sealed class Inliner
    {
        private readonly UdfRegistry _registry;
        private readonly Stack<string> _inliningStack = new();

        public Inliner(UdfRegistry registry)
        {
            _registry = registry;
        }

        public QueryExpression RewriteQuery(QueryExpression query) => query switch
        {
            SelectQueryExpression select => new SelectQueryExpression(RewriteSelect(select.Statement)),
            CompoundQueryExpression compound => new CompoundQueryExpression(
                RewriteQuery(compound.Left),
                compound.OperationType,
                compound.All,
                RewriteQuery(compound.Right),
                compound.OrderBy is { } ob ? RewriteOrderBy(ob) : null,
                compound.Limit,
                compound.Offset,
                compound.Into),
            _ => query,
        };

        private SelectStatement RewriteSelect(SelectStatement stmt)
        {
            return stmt with
            {
                Columns = stmt.Columns.Select(RewriteSelectColumn).ToList(),
                From = stmt.From is { } from ? new FromClause(RewriteTableSource(from.Source)) : null,
                Joins = stmt.Joins?.Select(RewriteJoin).ToList(),
                Where = stmt.Where is { } w ? Rewrite(w) : null,
                GroupBy = stmt.GroupBy is { } gb
                    ? new GroupByClause(gb.Expressions.Select(Rewrite).ToList(), gb.IsAll)
                    : null,
                Having = stmt.Having is { } h ? Rewrite(h) : null,
                Qualify = stmt.Qualify is { } q ? Rewrite(q) : null,
                Assertions = stmt.Assertions?.Select(a => a with
                {
                    Predicate = Rewrite(a.Predicate),
                    Message = a.Message is { } m ? Rewrite(m) : null,
                }).ToList(),
                OrderBy = stmt.OrderBy is { } ob ? RewriteOrderBy(ob) : null,
                LetBindings = stmt.LetBindings?.Select(b => b with { Expression = Rewrite(b.Expression) }).ToList(),
                CommonTableExpressions = stmt.CommonTableExpressions?.Select(cte => cte with
                {
                    Body = RewriteQuery(cte.Body),
                    RecursiveQuery = cte.RecursiveQuery is { } rq ? RewriteSelect(rq) : null,
                }).ToList(),
            };
        }

        private SelectColumn RewriteSelectColumn(SelectColumn column) => column switch
        {
            SelectAllColumns or SelectTableColumns => column,
            _ => column with { Expression = Rewrite(column.Expression) },
        };

        private TableSource RewriteTableSource(TableSource source) => source switch
        {
            SubquerySource sub => new SubquerySource(RewriteSelect(sub.Query), sub.Alias),
            FunctionSource fn => new FunctionSource(
                fn.FunctionName, fn.Arguments.Select(Rewrite).ToList(), fn.Alias, fn.Span),
            _ => source,
        };

        private JoinClause RewriteJoin(JoinClause join) => new(
            join.Type,
            RewriteTableSource(join.Source),
            join.OnCondition is { } cond ? Rewrite(cond) : null,
            join.IsLateral);

        private OrderByClause RewriteOrderBy(OrderByClause orderBy) => new(
            orderBy.Items.Select(i => new OrderByItem(Rewrite(i.Expression), i.Direction)).ToList());

        /// <summary>
        /// Recursively rewrites <paramref name="expression"/>: children first,
        /// then UDF inlining at this node.
        /// </summary>
        public Expression Rewrite(Expression expression)
        {
            Expression rewritten = RewriteChildren(expression);

            if (rewritten is FunctionCallExpression call &&
                call.FunctionName.StartsWith(UdfNamespacePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return InlineUdfCall(call);
            }

            return rewritten;
        }

        /// <summary>
        /// Returns a copy of <paramref name="expression"/> with children
        /// recursively rewritten. Leaf expressions return unchanged.
        /// </summary>
        private Expression RewriteChildren(Expression expression) => expression switch
        {
            FunctionCallExpression f => f with
            {
                Arguments = f.Arguments.Select(Rewrite).ToList(),
                OrderBy = f.OrderBy?.Select(i => new OrderByItem(Rewrite(i.Expression), i.Direction)).ToList(),
            },
            WindowFunctionCallExpression w => w with
            {
                Arguments = w.Arguments.Select(Rewrite).ToList(),
                Window = RewriteWindow(w.Window),
            },
            BinaryExpression b => new BinaryExpression(Rewrite(b.Left), b.Operator, Rewrite(b.Right)),
            UnaryExpression u => new UnaryExpression(u.Operator, Rewrite(u.Operand)),
            CastExpression c => c with { Expression = Rewrite(c.Expression) },
            CaseExpression ce => new CaseExpression(
                ce.Operand is { } o ? Rewrite(o) : null,
                ce.WhenClauses.Select(w => new WhenClause(Rewrite(w.Condition), Rewrite(w.Result))).ToList(),
                ce.ElseResult is { } er ? Rewrite(er) : null,
                ce.Span),
            InExpression ie => new InExpression(Rewrite(ie.Expression), ie.Values.Select(Rewrite).ToList(), ie.Negated),
            BetweenExpression be => new BetweenExpression(Rewrite(be.Expression), Rewrite(be.Low), Rewrite(be.High), be.Negated),
            IsNullExpression isn => new IsNullExpression(Rewrite(isn.Expression), isn.Negated),
            LikeExpression lk => new LikeExpression(
                Rewrite(lk.Expression), Rewrite(lk.Pattern), Rewrite(lk.EscapeCharacter), lk.CaseInsensitive),
            AtTimeZoneExpression atz => atz with
            {
                Expression = Rewrite(atz.Expression),
                TimeZone = Rewrite(atz.TimeZone),
            },
            SubqueryExpression sq => new SubqueryExpression(RewriteSelect(sq.Query)),
            InSubqueryExpression isq => new InSubqueryExpression(Rewrite(isq.Expression), RewriteSelect(isq.Query), isq.Negated),
            ExistsExpression ex => new ExistsExpression(RewriteSelect(ex.Query), ex.Negated),
            LambdaExpression lam => lam with { Body = Rewrite(lam.Body) },
            ScanExpression sc => sc with
            {
                BodyExpressions = sc.BodyExpressions.Select(Rewrite).ToList(),
                InitExpressions = sc.InitExpressions.Select(Rewrite).ToList(),
                Window = RewriteWindow(sc.Window),
            },
            StructLiteralExpression sl => sl with
            {
                Fields = sl.Fields.Select(f => new StructField(f.Name, Rewrite(f.Value))).ToList(),
            },
            IndexAccessExpression ix => ix with { Source = Rewrite(ix.Source), Index = Rewrite(ix.Index) },
            // Leaves
            _ => expression,
        };

        private WindowSpecification RewriteWindow(WindowSpecification window) => new(
            window.PartitionBy?.Select(Rewrite).ToList(),
            window.OrderBy?.Select(i => new OrderByItem(Rewrite(i.Expression), i.Direction)).ToList(),
            window.Frame);

        /// <summary>
        /// Replaces a <c>udf.X(args)</c> call with the substituted body of UDF
        /// <c>X</c>. Validates arity, detects cycles, and recursively inlines
        /// any nested UDF calls in the substituted body.
        /// </summary>
        private Expression InlineUdfCall(FunctionCallExpression call)
        {
            string name = call.FunctionName[UdfNamespacePrefix.Length..];

            if (!_registry.TryGet(name, out UdfDescriptor? udf))
            {
                throw new InvalidOperationException(
                    $"UDF '{call.FunctionName}' is not registered. " +
                    $"Register it via CREATE FUNCTION {name}(...) AS ... before referencing it.");
            }

            if (call.Arguments.Count != udf.Parameters.Count)
            {
                throw new InvalidOperationException(
                    $"UDF '{call.FunctionName}' expects {udf.Parameters.Count} argument(s), " +
                    $"got {call.Arguments.Count}.");
            }

            // Cycle detection — case-insensitive on UDF names.
            foreach (string active in _inliningStack)
            {
                if (string.Equals(active, name, StringComparison.OrdinalIgnoreCase))
                {
                    string chain = string.Join(" → ", _inliningStack.Reverse().Append(name));
                    throw new InvalidOperationException(
                        $"Cyclic UDF reference detected: {chain}. " +
                        "UDFs cannot reference themselves directly or transitively.");
                }
            }

            // Build the parameter → call-site-arg substitution map.
            Dictionary<string, Expression> paramToArg = new(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < udf.Parameters.Count; i++)
            {
                paramToArg[udf.Parameters[i].Name] = call.Arguments[i];
            }

            // Substitute parameters in the body. The body's AST may itself
            // contain udf.* calls that need recursive inlining; that happens
            // after substitution so the result is fully resolved.
            Expression substituted = SubstituteParameters(udf.Body, paramToArg);

            _inliningStack.Push(name);
            try
            {
                return Rewrite(substituted);
            }
            finally
            {
                _inliningStack.Pop();
            }
        }

        /// <summary>
        /// Walks <paramref name="body"/> and replaces each bare
        /// <see cref="ColumnReference"/> whose name matches a key in
        /// <paramref name="paramToArg"/> with the corresponding argument
        /// expression. Honours lambda and SCAN-accumulator scopes by
        /// removing shadowed names for the duration of the inner walk.
        /// </summary>
        private static Expression SubstituteParameters(
            Expression body,
            Dictionary<string, Expression> paramToArg)
        {
            return Walk(body, paramToArg);

            static Expression Walk(Expression expr, Dictionary<string, Expression> activeParams)
            {
                switch (expr)
                {
                    case ColumnReference col when col.TableName is null
                        && activeParams.TryGetValue(col.ColumnName, out Expression? arg):
                        return arg;

                    case LambdaExpression lam:
                    {
                        Dictionary<string, Expression>? shadowed = ShadowParams(activeParams, lam.Parameters);
                        Expression body2 = Walk(lam.Body, shadowed ?? activeParams);
                        return lam with { Body = body2 };
                    }

                    case ScanExpression sc:
                    {
                        // SCAN body sees accumulator names but not the outer column-named params.
                        Dictionary<string, Expression>? shadowed = ShadowParams(activeParams, sc.AccumulatorNames);
                        Dictionary<string, Expression> innerParams = shadowed ?? activeParams;
                        return sc with
                        {
                            BodyExpressions = sc.BodyExpressions.Select(e => Walk(e, innerParams)).ToList(),
                            InitExpressions = sc.InitExpressions.Select(e => Walk(e, activeParams)).ToList(),
                            Window = WalkWindow(sc.Window, activeParams),
                        };
                    }

                    case FunctionCallExpression f:
                        return f with
                        {
                            Arguments = f.Arguments.Select(a => Walk(a, activeParams)).ToList(),
                            OrderBy = f.OrderBy?.Select(i => new OrderByItem(Walk(i.Expression, activeParams), i.Direction)).ToList(),
                        };

                    case WindowFunctionCallExpression w:
                        return w with
                        {
                            Arguments = w.Arguments.Select(a => Walk(a, activeParams)).ToList(),
                            Window = WalkWindow(w.Window, activeParams),
                        };

                    case BinaryExpression b:
                        return new BinaryExpression(Walk(b.Left, activeParams), b.Operator, Walk(b.Right, activeParams));

                    case UnaryExpression u:
                        return new UnaryExpression(u.Operator, Walk(u.Operand, activeParams));

                    case CastExpression c:
                        return c with { Expression = Walk(c.Expression, activeParams) };

                    case CaseExpression ce:
                        return new CaseExpression(
                            ce.Operand is { } o ? Walk(o, activeParams) : null,
                            ce.WhenClauses.Select(w => new WhenClause(Walk(w.Condition, activeParams), Walk(w.Result, activeParams))).ToList(),
                            ce.ElseResult is { } er ? Walk(er, activeParams) : null,
                            ce.Span);

                    case InExpression ie:
                        return new InExpression(Walk(ie.Expression, activeParams),
                            ie.Values.Select(v => Walk(v, activeParams)).ToList(), ie.Negated);

                    case BetweenExpression be:
                        return new BetweenExpression(
                            Walk(be.Expression, activeParams),
                            Walk(be.Low, activeParams),
                            Walk(be.High, activeParams),
                            be.Negated);

                    case IsNullExpression isn:
                        return new IsNullExpression(Walk(isn.Expression, activeParams), isn.Negated);

                    case LikeExpression lk:
                        return new LikeExpression(
                            Walk(lk.Expression, activeParams),
                            Walk(lk.Pattern, activeParams),
                            Walk(lk.EscapeCharacter, activeParams),
                            lk.CaseInsensitive);

                    case AtTimeZoneExpression atz:
                        return atz with
                        {
                            Expression = Walk(atz.Expression, activeParams),
                            TimeZone = Walk(atz.TimeZone, activeParams),
                        };

                    case StructLiteralExpression sl:
                        return sl with
                        {
                            Fields = sl.Fields.Select(f => new StructField(f.Name, Walk(f.Value, activeParams))).ToList(),
                        };

                    case IndexAccessExpression ix:
                        return ix with { Source = Walk(ix.Source, activeParams), Index = Walk(ix.Index, activeParams) };

                    // Subqueries are intentionally not walked here. A UDF body
                    // that contains a subquery does not have its outer
                    // parameter names visible inside the subquery's select
                    // list — the subquery introduces its own column scope.
                    // Refusing to substitute keeps behaviour predictable.
                    case SubqueryExpression:
                    case InSubqueryExpression:
                    case ExistsExpression:
                        return expr;

                    // Leaves: literals, parameters, type literals, current-timestamp,
                    // error placeholders, qualified column references that don't match
                    // an unscoped param.
                    default:
                        return expr;
                }
            }

            static Dictionary<string, Expression>? ShadowParams(
                Dictionary<string, Expression> active, IReadOnlyList<string> shadowedNames)
            {
                if (shadowedNames.Count == 0) return null;
                Dictionary<string, Expression>? copy = null;
                foreach (string n in shadowedNames)
                {
                    if (active.ContainsKey(n))
                    {
                        copy ??= new Dictionary<string, Expression>(active, StringComparer.OrdinalIgnoreCase);
                        copy.Remove(n);
                    }
                }
                return copy;
            }

            static WindowSpecification WalkWindow(WindowSpecification window, Dictionary<string, Expression> active) => new(
                window.PartitionBy?.Select(p => Walk(p, active)).ToList(),
                window.OrderBy?.Select(i => new OrderByItem(Walk(i.Expression, active), i.Direction)).ToList(),
                window.Frame);
        }
    }
}
