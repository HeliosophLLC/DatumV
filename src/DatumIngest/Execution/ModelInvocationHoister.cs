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
/// (<c>SELECT models.mobilenetv2(image) FROM coco LIMIT 10</c>). Filter, Join,
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
        // Per-operator hoisters share the same collector + MIO-stacker primitives;
        // they differ only in which expression sites they walk and how they
        // reconstruct the operator. Each hoister handles same-operator dedup
        // (textually-identical calls in one operator share an MIO). Cross-operator
        // dedup — same call in WHERE and SELECT, for example — would need a
        // unified pass; not done here.
        if (op is ProjectOperator project)
        {
            IQueryOperator hoistedSource = HoistRecursive(project.Source, catalog);
            return HoistProject(project, hoistedSource, catalog);
        }
        else if (op is FilterOperator filter)
        {
            IQueryOperator hoistedSource = HoistRecursive(filter.Source, catalog);
            return HoistFilter(filter, hoistedSource, catalog);
        }
        else if (op is OrderByOperator orderBy)
        {
            IQueryOperator hoistedSource = HoistRecursive(orderBy.Source, catalog);
            return HoistOrderBy(orderBy, hoistedSource, catalog);
        }

        return RewriteChildren(op, child => HoistRecursive(child, catalog));
    }

    private static IQueryOperator HoistProject(
        ProjectOperator project, IQueryOperator hoistedSource, ModelCatalog catalog)
    {
        ModelHoistCollector collector = new();

        // Walk LET binding bodies first — they evaluate before projection columns,
        // and a model call inside a LET body is just as valid a hoist target as
        // one inside a column expression.
        if (project.LetBindings is not null)
        {
            foreach (LetBinding binding in project.LetBindings)
            {
                collector.Visit(binding.Expression);
            }
        }
        foreach (SelectColumn column in project.Columns)
        {
            collector.Visit(column.Expression);
        }

        if (collector.HoistedOrder.Count == 0)
        {
            // No model calls in this project — nothing to hoist. Pass the (possibly
            // hoisted) source back, preserving structural sharing when nothing
            // actually changed.
            return ReferenceEquals(hoistedSource, project.Source)
                ? project
                : RewriteChildren(project, _ => hoistedSource);
        }

        IQueryOperator augmented = BuildMioStack(hoistedSource, collector, catalog);

        // Rewrite projection columns and LET binding bodies. Hoisted model-call
        // nodes become ColumnReferences to their synthesised columns; the
        // identity-keyed map ensures both canonical and duplicate occurrences
        // resolve to the same column.
        SelectColumn[] rewrittenColumns = new SelectColumn[project.Columns.Count];
        for (int i = 0; i < project.Columns.Count; i++)
        {
            rewrittenColumns[i] = project.Columns[i] with
            {
                Expression = RewriteExpression(project.Columns[i].Expression, collector.HoistedColumns),
            };
        }

        IReadOnlyList<LetBinding>? rewrittenLet = project.LetBindings;
        if (project.LetBindings is not null)
        {
            LetBinding[] rebuilt = new LetBinding[project.LetBindings.Count];
            for (int i = 0; i < project.LetBindings.Count; i++)
            {
                rebuilt[i] = project.LetBindings[i] with
                {
                    Expression = RewriteExpression(project.LetBindings[i].Expression, collector.HoistedColumns),
                };
            }
            rewrittenLet = rebuilt;
        }

        return new ProjectOperator(
            augmented,
            rewrittenColumns,
            rewrittenLet,
            project.Assertions);
    }

    /// <summary>
    /// Hoists model calls out of a <see cref="FilterOperator"/>'s predicate.
    /// The MIO stack lands between the filter's source and the filter itself;
    /// the predicate evaluates against rows that already carry the hoisted
    /// column. Same-operator structural dedup applies — a predicate like
    /// <c>LENGTH(models.x(name)) &gt; 5 AND models.x(name) IS NOT NULL</c>
    /// hoists once, not twice.
    /// </summary>
    private static IQueryOperator HoistFilter(
        FilterOperator filter, IQueryOperator hoistedSource, ModelCatalog catalog)
    {
        ModelHoistCollector collector = new();
        collector.Visit(filter.Predicate);

        if (collector.HoistedOrder.Count == 0)
        {
            return ReferenceEquals(hoistedSource, filter.Source)
                ? filter
                : new FilterOperator(hoistedSource, filter.Predicate);
        }

        IQueryOperator augmented = BuildMioStack(hoistedSource, collector, catalog);
        Expression rewrittenPredicate = RewriteExpression(filter.Predicate, collector.HoistedColumns);
        return new FilterOperator(augmented, rewrittenPredicate);
    }

    /// <summary>
    /// Hoists model calls out of <see cref="OrderByOperator.OrderByItems"/>.
    /// The MIO stack lands between the sort's source and the sort itself, so
    /// the comparator evaluates against rows that already carry the hoisted
    /// column. Note: the model dispatches for every input row, including ones
    /// that a downstream LIMIT will discard — sort fundamentally needs to
    /// inspect every candidate to find the top-N.
    /// </summary>
    private static IQueryOperator HoistOrderBy(
        OrderByOperator orderBy, IQueryOperator hoistedSource, ModelCatalog catalog)
    {
        ModelHoistCollector collector = new();
        foreach (OrderByItem item in orderBy.OrderByItems)
        {
            collector.Visit(item.Expression);
        }

        if (collector.HoistedOrder.Count == 0)
        {
            return ReferenceEquals(hoistedSource, orderBy.Source)
                ? orderBy
                : new OrderByOperator(hoistedSource, orderBy.OrderByItems, orderBy.TopNRows);
        }

        IQueryOperator augmented = BuildMioStack(hoistedSource, collector, catalog);
        OrderByItem[] rewrittenItems = new OrderByItem[orderBy.OrderByItems.Count];
        for (int i = 0; i < orderBy.OrderByItems.Count; i++)
        {
            rewrittenItems[i] = orderBy.OrderByItems[i] with
            {
                Expression = RewriteExpression(orderBy.OrderByItems[i].Expression, collector.HoistedColumns),
            };
        }
        return new OrderByOperator(augmented, rewrittenItems, orderBy.TopNRows);
    }

    /// <summary>
    /// Walks expressions and collects every distinct <c>models.*</c> call by
    /// structural fingerprint. Two textually-identical call sites (different
    /// AST node instances produced by the parser, same canonical text) share
    /// one operator and one GPU dispatch per batch — per the inference-
    /// integration convention "same call site → one eval", regardless of
    /// model determinism. <see cref="HoistedColumns"/> is identity-keyed
    /// because the downstream rewrite step walks the AST and looks up each
    /// <see cref="FunctionCallExpression"/> node by reference; populating
    /// it for both canonical and duplicate occurrences lets that lookup
    /// produce the right shared column name.
    /// </summary>
    private sealed class ModelHoistCollector
    {
        private readonly Dictionary<string, string> _fingerprintToColumn =
            new(StringComparer.Ordinal);

        public Dictionary<FunctionCallExpression, string> HoistedColumns { get; } =
            new(ReferenceEqualityComparer.Instance);

        public List<FunctionCallExpression> HoistedOrder { get; } = new();

        public void Visit(Expression expr)
        {
            switch (expr)
            {
                case FunctionCallExpression fn when IsModelCall(fn):
                    // Post-order: visit children FIRST so any nested model calls
                    // get appended to HoistedOrder before this one. The order in
                    // HoistedOrder dictates operator stacking — the first entry
                    // becomes the innermost MIO (closest to the scan), so nested
                    // models.mobilenetv2(file) inside models.llama31_8b(...) ends
                    // up below models.llama31_8b in the plan and its output
                    // column is available by the time llama31_8b runs.
                    foreach (Expression arg in fn.Arguments) Visit(arg);
                    if (!HoistedColumns.ContainsKey(fn))
                    {
                        string fingerprint = QueryExplainer.FormatExpression(fn);
                        if (_fingerprintToColumn.TryGetValue(fingerprint, out string? existingColumn))
                        {
                            // Textual duplicate of an earlier canonical call —
                            // share its column. No new operator allocated.
                            HoistedColumns[fn] = existingColumn;
                        }
                        else
                        {
                            string synthName = $"__model_{StripNamespace(fn.FunctionName)}_{HoistedOrder.Count}";
                            _fingerprintToColumn[fingerprint] = synthName;
                            HoistedColumns[fn] = synthName;
                            HoistedOrder.Add(fn);
                        }
                    }
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
    }

    /// <summary>
    /// Builds the stack of <see cref="ModelInvocationOperator"/>s above
    /// <paramref name="source"/>. One operator per canonical hoisted call,
    /// in <see cref="ModelHoistCollector.HoistedOrder"/> order so nested
    /// inner calls land closer to the scan than their parents — the parent's
    /// arguments rewrite to <see cref="ColumnReference"/>s pointing at the
    /// inner's output column, available on the row by the time the parent
    /// operator runs.
    /// </summary>
    private static IQueryOperator BuildMioStack(
        IQueryOperator source, ModelHoistCollector collector, ModelCatalog catalog)
    {
        IQueryOperator augmented = source;
        foreach (FunctionCallExpression fn in collector.HoistedOrder)
        {
            string modelName = StripNamespace(fn.FunctionName);
            ModelCatalogEntry? entry = catalog.TryGetEntry(modelName);
            if (entry is null)
            {
                throw new InvalidOperationException(
                    $"Model '{modelName}' is not registered in the catalog. Reference '{fn.FunctionName}' " +
                    $"requires a matching ModelCatalog entry — register it via ModelCatalog.Register before planning.");
            }

            int requiredCount = entry.InputKinds.Count;
            int maxOptional = entry.OptionalArgKinds?.Count ?? 0;
            int suppliedCount = fn.Arguments.Count;

            if (suppliedCount < requiredCount)
            {
                throw new InvalidOperationException(
                    $"Model '{modelName}' expects at least {requiredCount} required input(s) " +
                    $"but the call site '{fn.FunctionName}' supplies only {suppliedCount}.");
            }
            if (suppliedCount > requiredCount + maxOptional)
            {
                throw new InvalidOperationException(
                    $"Model '{modelName}' accepts at most {requiredCount + maxOptional} arguments " +
                    $"({requiredCount} required + {maxOptional} optional) but the call site " +
                    $"'{fn.FunctionName}' supplies {suppliedCount}.");
            }

            Expression[] requiredArgs = new Expression[requiredCount];
            for (int i = 0; i < requiredCount; i++)
            {
                requiredArgs[i] = RewriteExpression(fn.Arguments[i], collector.HoistedColumns);
            }

            Expression[] optionalArgs = new Expression[suppliedCount - requiredCount];
            for (int i = 0; i < optionalArgs.Length; i++)
            {
                optionalArgs[i] = RewriteExpression(fn.Arguments[requiredCount + i], collector.HoistedColumns);
            }

            string synthName = collector.HoistedColumns[fn];
            augmented = new ModelInvocationOperator(
                augmented, modelName, requiredArgs, optionalArgs, synthName);
        }
        return augmented;
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
            OrderByOperator ob => RewriteOrderBy(ob, childRewriter),
            LimitOperator l => RewriteLimit(l, childRewriter),
            // Default: leave the operator alone. Hoisting model calls inside
            // operators we don't recognise here would need their own rewrite
            // — extend the switch as new operators come into the hoisting picture.
            _ => op,
        };
    }

    private static IQueryOperator RewriteOrderBy(OrderByOperator orderBy, Func<IQueryOperator, IQueryOperator> childRewriter)
    {
        IQueryOperator newSource = childRewriter(orderBy.Source);
        return ReferenceEquals(newSource, orderBy.Source)
            ? orderBy
            : new OrderByOperator(newSource, orderBy.OrderByItems, orderBy.TopNRows);
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
