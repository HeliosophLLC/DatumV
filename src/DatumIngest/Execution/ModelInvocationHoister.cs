using DatumIngest.Execution.Operators;
using DatumIngest.Models;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

/// <summary>
/// Plan-time pass that hoists every <c>models.&lt;name&gt;(...)</c> call out of
/// expressions into dedicated <see cref="ModelInvocationOperator"/> nodes,
/// leaving only column references in their place. This avoids async-ifying the
/// scalar function infrastructure and enables proper batched GPU dispatch.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Phase A scope</strong> — hoists only out of <see cref="ProjectOperator"/>
/// expressions, which covers Demo 0.5
/// (<c>SELECT models.classify(image) FROM coco LIMIT 10</c>). Filter, Join,
/// OrderBy, GroupBy, and Window expressions still execute model calls inline if
/// any are present — for those operators the planner currently throws at runtime
/// (the model "function" is a marker only; it has no <c>Execute</c> body).
/// Expanding hoisting to those operators is a follow-up — the architecture is
/// ready, the work is mechanical.
/// </para>
/// <para>
/// <strong>CSE invariant</strong> — each <see cref="FunctionCallExpression"/>
/// AST node hoists to <em>one</em> operator. Two clauses referencing the same
/// AST node share the result automatically (e.g. a planner-introduced shared
/// reference). For nondeterministic models, <em>different</em> textual call
/// sites with equal arguments still produce separate evaluations — the rule
/// from <c>project_inference_integration_approach.md</c>.
/// </para>
/// </remarks>
public static class ModelInvocationHoister
{
    /// <summary>The namespace prefix that flags a function call as a model invocation.</summary>
    public const string ModelNamespacePrefix = "models.";

    /// <summary>
    /// Walks the operator tree rooted at <paramref name="op"/> and rewrites it
    /// so every <c>models.*</c> call inside reachable <see cref="ProjectOperator"/>
    /// expressions is hoisted into a <see cref="ModelInvocationOperator"/>.
    /// Returns the root of the rewritten tree (which may differ from
    /// <paramref name="op"/> when hoisting inserted new operators above it).
    /// </summary>
    public static IQueryOperator Hoist(IQueryOperator op, ModelCatalog? catalog)
    {
        // Catalog is required for plan-time validation (input arity, output kind).
        // When no catalog is configured, plans containing models.* calls would
        // already fail at runtime via ExecutionContext.Models == null; here we
        // skip hoisting entirely so non-inference queries still plan cleanly.
        if (catalog is null)
        {
            return op;
        }

        return HoistRecursive(op, catalog);
    }

    private static IQueryOperator HoistRecursive(IQueryOperator op, ModelCatalog catalog)
    {
        // Only ProjectOperator carries expressions where models.* makes sense in
        // Phase A. Other operators just recurse into their children. Specific
        // operators (Filter, Join, OrderBy) get their own hoisting in follow-ups
        // — the rewrite primitives are the same.
        if (op is ProjectOperator project)
        {
            IQueryOperator hoistedSource = HoistRecursive(project.Source, catalog);
            return HoistProject(project, hoistedSource, catalog);
        }

        return RewriteChildren(op, child => HoistRecursive(child, catalog));
    }

    private static IQueryOperator HoistProject(
        ProjectOperator project, IQueryOperator hoistedSource, ModelCatalog catalog)
    {
        // Collect every distinct models.* call inside the project's expressions.
        // Distinct = by AST node identity (ReferenceEquals), so a single call
        // node referenced from multiple places hoists once and resolves via
        // the same synthesised column.
        Dictionary<FunctionCallExpression, string> hoistedColumns = new(ReferenceEqualityComparer.Instance);
        List<FunctionCallExpression> hoistedOrder = new();

        void Visit(Expression expr)
        {
            switch (expr)
            {
                case FunctionCallExpression fn when IsModelCall(fn):
                    if (!hoistedColumns.ContainsKey(fn))
                    {
                        string synthName = $"__model_{StripNamespace(fn.FunctionName)}_{hoistedOrder.Count}";
                        hoistedColumns[fn] = synthName;
                        hoistedOrder.Add(fn);
                    }
                    foreach (Expression arg in fn.Arguments) Visit(arg);
                    break;

                case FunctionCallExpression fn:
                    foreach (Expression arg in fn.Arguments) Visit(arg);
                    break;

                case BinaryExpression b: Visit(b.Left); Visit(b.Right); break;
                case UnaryExpression u: Visit(u.Operand); break;
                case CastExpression c: Visit(c.Expression); break;
                case InExpression i: Visit(i.Expression); foreach (Expression v in i.Values) Visit(v); break;
                case BetweenExpression bt: Visit(bt.Expression); Visit(bt.Low); Visit(bt.High); break;
                case IsNullExpression n: Visit(n.Expression); break;
                case CaseExpression ce:
                    if (ce.Operand is not null) Visit(ce.Operand);
                    foreach (WhenClause w in ce.WhenClauses) { Visit(w.Condition); Visit(w.Result); }
                    if (ce.ElseResult is not null) Visit(ce.ElseResult);
                    break;
                case LikeExpression like: Visit(like.Expression); Visit(like.Pattern); Visit(like.EscapeCharacter); break;
                case AtTimeZoneExpression atz: Visit(atz.Expression); Visit(atz.TimeZone); break;
                case StructLiteralExpression sl:
                    foreach (StructField f in sl.Fields) Visit(f.Value);
                    break;
                case IndexAccessExpression ia: Visit(ia.Source); Visit(ia.Index); break;
                case LambdaExpression lam: Visit(lam.Body); break;
                default: break;
            }
        }

        foreach (SelectColumn column in project.Columns) Visit(column.Expression);

        if (hoistedOrder.Count == 0)
        {
            // No model calls in this project — nothing to hoist. Pass the (possibly
            // hoisted) source back, preserving structural sharing when nothing
            // actually changed.
            return ReferenceEquals(hoistedSource, project.Source)
                ? project
                : RewriteChildren(project, _ => hoistedSource);
        }

        // Wrap the source with one ModelInvocationOperator per hoisted call site,
        // in the order they appeared in the expression tree (so dependent calls
        // see their dependency's output column already attached). Each operator
        // adds exactly one column to the row schema.
        IQueryOperator augmented = hoistedSource;
        foreach (FunctionCallExpression fn in hoistedOrder)
        {
            string modelName = StripNamespace(fn.FunctionName);
            ModelCatalogEntry? entry = catalog.TryGetEntry(modelName);
            if (entry is null)
            {
                throw new InvalidOperationException(
                    $"Model '{modelName}' is not registered in the catalog. Reference '{fn.FunctionName}' " +
                    $"requires a matching ModelCatalog entry — register it via ModelCatalog.Register before planning.");
            }

            if (entry.InputKinds.Count != fn.Arguments.Count)
            {
                throw new InvalidOperationException(
                    $"Model '{modelName}' expects {entry.InputKinds.Count} input(s) but the call site '{fn.FunctionName}' supplies {fn.Arguments.Count}.");
            }

            string synthName = hoistedColumns[fn];
            augmented = new ModelInvocationOperator(
                augmented,
                modelName,
                fn.Arguments.ToArray(),
                synthName);
        }

        // Rewrite project expressions: every hoisted FunctionCallExpression node
        // becomes a ColumnReference to its synthesised column.
        SelectColumn[] rewrittenColumns = new SelectColumn[project.Columns.Count];
        for (int i = 0; i < project.Columns.Count; i++)
        {
            rewrittenColumns[i] = project.Columns[i] with
            {
                Expression = RewriteExpression(project.Columns[i].Expression, hoistedColumns),
            };
        }

        return new ProjectOperator(
            augmented,
            rewrittenColumns,
            project.LetBindings,
            project.Assertions);
    }

    /// <summary>
    /// Walks <paramref name="expression"/> and replaces every hoisted
    /// <see cref="FunctionCallExpression"/> reference with a
    /// <see cref="ColumnReference"/> pointing at its synthesised output column.
    /// </summary>
    private static Expression RewriteExpression(
        Expression expression, IReadOnlyDictionary<FunctionCallExpression, string> hoisted)
    {
        return expression switch
        {
            FunctionCallExpression fn when hoisted.TryGetValue(fn, out string? synthName)
                => new ColumnReference(TableName: null, ColumnName: synthName),

            FunctionCallExpression fn => fn with
            {
                Arguments = RewriteList(fn.Arguments, hoisted),
            },

            BinaryExpression b => b with
            {
                Left = RewriteExpression(b.Left, hoisted),
                Right = RewriteExpression(b.Right, hoisted),
            },

            UnaryExpression u => u with { Operand = RewriteExpression(u.Operand, hoisted) },
            CastExpression c => c with { Expression = RewriteExpression(c.Expression, hoisted) },
            InExpression i => i with
            {
                Expression = RewriteExpression(i.Expression, hoisted),
                Values = RewriteList(i.Values, hoisted),
            },
            BetweenExpression bt => bt with
            {
                Expression = RewriteExpression(bt.Expression, hoisted),
                Low = RewriteExpression(bt.Low, hoisted),
                High = RewriteExpression(bt.High, hoisted),
            },
            IsNullExpression n => n with { Expression = RewriteExpression(n.Expression, hoisted) },
            CaseExpression ce => ce with
            {
                Operand = ce.Operand is null ? null : RewriteExpression(ce.Operand, hoisted),
                WhenClauses = ce.WhenClauses
                    .Select(w => new WhenClause(
                        RewriteExpression(w.Condition, hoisted),
                        RewriteExpression(w.Result, hoisted)))
                    .ToList(),
                ElseResult = ce.ElseResult is null ? null : RewriteExpression(ce.ElseResult, hoisted),
            },
            LikeExpression like => like with
            {
                Expression = RewriteExpression(like.Expression, hoisted),
                Pattern = RewriteExpression(like.Pattern, hoisted),
                EscapeCharacter = RewriteExpression(like.EscapeCharacter, hoisted),
            },
            AtTimeZoneExpression atz => atz with
            {
                Expression = RewriteExpression(atz.Expression, hoisted),
                TimeZone = RewriteExpression(atz.TimeZone, hoisted),
            },
            StructLiteralExpression sl => sl with
            {
                Fields = sl.Fields
                    .Select(f => new StructField(f.Name, RewriteExpression(f.Value, hoisted)))
                    .ToList(),
            },
            IndexAccessExpression ia => ia with
            {
                Source = RewriteExpression(ia.Source, hoisted),
                Index = RewriteExpression(ia.Index, hoisted),
            },
            LambdaExpression lam => lam with { Body = RewriteExpression(lam.Body, hoisted) },

            _ => expression,
        };
    }

    private static IReadOnlyList<Expression> RewriteList(
        IReadOnlyList<Expression> list, IReadOnlyDictionary<FunctionCallExpression, string> hoisted)
    {
        if (list.Count == 0) return list;

        Expression[] rewritten = new Expression[list.Count];
        bool changed = false;
        for (int i = 0; i < list.Count; i++)
        {
            Expression original = list[i];
            Expression updated = RewriteExpression(original, hoisted);
            rewritten[i] = updated;
            if (!ReferenceEquals(original, updated)) changed = true;
        }
        return changed ? rewritten : list;
    }

    /// <summary>
    /// Generic operator rewrite: replace each child operator via
    /// <paramref name="childRewriter"/> and reconstruct the parent. Falls back
    /// to <see cref="IQueryOperator.RewriteExpressions"/> when the operator
    /// doesn't expose a child set we recognise — that path is identity-on-
    /// expressions and just hands the rewriter a no-op.
    /// </summary>
    private static IQueryOperator RewriteChildren(
        IQueryOperator op, Func<IQueryOperator, IQueryOperator> childRewriter)
    {
        // For Phase A we recognise the small set of operators that wrap a single
        // source. The full set lives in different operator subclasses; we extend
        // this switch as new operators come into the hoisting picture.
        return op switch
        {
            ProjectOperator p => new ProjectOperator(
                childRewriter(p.Source), p.Columns, p.LetBindings, p.Assertions),
            FilterOperator f => new FilterOperator(childRewriter(f.Source), f.Predicate),
            LimitOperator l => RewriteLimit(l, childRewriter),
            // Default: leave the operator alone. Hoisting model calls inside
            // operators we don't recognise here would need their own rewrite
            // — Phase A doesn't reach them.
            _ => op,
        };
    }

    private static IQueryOperator RewriteLimit(LimitOperator limit, Func<IQueryOperator, IQueryOperator> childRewriter)
    {
        IQueryOperator newSource = childRewriter(limit.Source);
        return ReferenceEquals(newSource, limit.Source) ? limit : new LimitOperator(newSource, limit.Limit, limit.Offset);
    }

    private static bool IsModelCall(FunctionCallExpression fn)
        => fn.FunctionName.StartsWith(ModelNamespacePrefix, StringComparison.OrdinalIgnoreCase);

    private static string StripNamespace(string qualifiedName)
        => qualifiedName.StartsWith(ModelNamespacePrefix, StringComparison.OrdinalIgnoreCase)
            ? qualifiedName[ModelNamespacePrefix.Length..]
            : qualifiedName;
}
