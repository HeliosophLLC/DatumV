using DatumIngest.Execution.Operators;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

/// <summary>
/// Post-pass that runs after <see cref="ModelInvocationHoister"/> and
/// collapses chains of single-invocation
/// <see cref="ModelInvocationOperator"/> nodes (optionally separated by
/// pure-alias <see cref="RowEnricherOperator"/> rungs from the
/// LET-staircase rewrite) into a single multi-invocation
/// <see cref="ModelInvocationOperator"/>. Single-MIO plans with no
/// adjacent or alias-bridged sibling are left unchanged — they already
/// have the desired shape.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a separate pass.</strong> The hoister already knows how to
/// produce MIO nodes from <c>models.*</c> calls; doing the collapse
/// in-line there would tangle two concerns (hoisting vs DAG construction)
/// and make hoister edits riskier. Splitting it out also means a future
/// caller that wants stacked MIOs (e.g. a test, or a per-MIO debug mode)
/// can run the hoister without the collapser.
/// </para>
/// <para>
/// <strong>LET-staircase folding.</strong> When the projection contains
/// <c>LET</c> bindings backed by model calls, the planner's LET-staircase
/// rewrite produces a stack like
/// <c>MIO_outer → RowEnricher[aliases] → MIO_inner</c> rather than two
/// adjacent MIOs. The collapser walks through pure-alias
/// <see cref="RowEnricherOperator"/> rungs (every enrichment is a
/// <see cref="ColumnReference"/>) treating them as transparent, builds
/// the combined chain, and restacks the alias rungs <em>above</em> the
/// new operator so downstream operators still see the LET-aliased
/// column names.
/// </para>
/// <para>
/// <strong>Invocation order.</strong> In a hoister-produced MIO stack the
/// outermost wrap is at the top (closer to the consumer) and the
/// innermost is at the bottom (closer to the source). Invocations run
/// in dependency order — innermost first — so any later invocation that
/// references an earlier invocation's output column resolves naturally
/// against the in-progress output batch.
/// </para>
/// <para>
/// <strong>Alias substitution.</strong> When a LET-aliased name (e.g.
/// <c>model</c>) appears inside an invocation's input expressions, the
/// collapser rewrites it to the canonical hidden column name
/// (e.g. <c>__model_sd_turbo_0</c>) before passing the invocation to
/// the new operator. The new operator's per-invocation evaluation reads
/// from the in-progress output batch, which has the raw hidden columns
/// declared in its schema — not the LET aliases.
/// </para>
/// </remarks>
public static class BatchedModelDagCollapser
{
    /// <summary>
    /// Rewrites <paramref name="op"/> and all of its descendants, collapsing
    /// every chain of two or more <see cref="ModelInvocationOperator"/>
    /// nodes (possibly bridged by pure-alias <see cref="RowEnricherOperator"/>
    /// rungs) into a single multi-invocation operator. Returns
    /// <paramref name="op"/> unchanged when no collapses apply.
    /// </summary>
    public static QueryOperator Collapse(QueryOperator op)
    {
        // Children-first so the chain detection below sees a stable subtree.
        QueryOperator withChildren = ModelInvocationHoister.RewriteChildren(op, Collapse);

        // Direct MIO-over-MIO (no LET bridge) — the original case.
        // Also: MIO-over-(pure-alias-RowEnricher)-over-MIO — the LET case.
        // Also: multi-invocation MIO over an alias-bridged inner MIO,
        // where children-first already merged upper MIOs and we extend
        // that merged operator with another inner invocation.
        if (IsCollapseAnchor(withChildren) && HasFoldableChain(withChildren))
        {
            return CollapseChain(withChildren);
        }

        return withChildren;
    }

    /// <summary>A node that can sit at the top of (or inside) a fold chain.</summary>
    private static bool IsCollapseAnchor(QueryOperator op)
        => op is ModelInvocationOperator;

    /// <summary>
    /// True when there's at least one additional MIO reachable from
    /// <paramref name="top"/> by walking through any number of pure-alias
    /// RowEnricher rungs and adjacent MIOs. Just a single-MIO-with-no-
    /// foldable-source returns false so we don't pointlessly rewrap one
    /// MIO into another one-invocation MIO.
    /// </summary>
    private static bool HasFoldableChain(QueryOperator top)
    {
        // Count effective MIO sites in the chain starting at `top`.
        int sites = CountInvocations(top);
        QueryOperator? cursor = ChainNextSource(top);
        while (cursor is not null && IsCollapseAnchor(cursor))
        {
            sites += CountInvocations(cursor);
            if (sites >= 2) return true;
            cursor = ChainNextSource(cursor);
        }
        return false;
    }

    private static int CountInvocations(QueryOperator op) => op switch
    {
        ModelInvocationOperator mio => mio.Invocations.Count,
        _ => 0,
    };

    /// <summary>
    /// Returns the next chain node going inward — the operator's source
    /// directly if it's a MIO, or the source's source after walking
    /// through a pure-alias <see cref="RowEnricherOperator"/>. Returns
    /// <see langword="null"/> when the chain ends here.
    /// </summary>
    private static QueryOperator? ChainNextSource(QueryOperator op)
    {
        QueryOperator? src = op switch
        {
            ModelInvocationOperator m => m.Source,
            _ => null,
        };
        if (src is null) return null;

        // Walk through any number of stacked pure-alias enrichers.
        while (src is RowEnricherOperator enr && IsPassThroughAliasEnricher(enr))
        {
            src = enr.Source;
        }
        return IsCollapseAnchor(src) ? src : null;
    }

    /// <summary>
    /// A "pure-alias" enricher binds each of its columns to a bare
    /// <see cref="ColumnReference"/> — the LET-staircase rewrite's exact
    /// shape. Enrichers with computation in their RHS expressions stay
    /// in place; they may need to evaluate against per-row context that
    /// the collapsed operator's in-progress output batch doesn't
    /// faithfully provide.
    /// </summary>
    private static bool IsPassThroughAliasEnricher(RowEnricherOperator enricher)
    {
        foreach (RowEnrichment e in enricher.Enrichments)
        {
            if (e.Expression is not ColumnReference) return false;
        }
        return true;
    }

    /// <summary>
    /// Materialises the chain rooted at <paramref name="top"/> into one
    /// multi-invocation <see cref="ModelInvocationOperator"/> stacked
    /// beneath whatever pure-alias <see cref="RowEnricherOperator"/>
    /// rungs were found in the chain.
    /// </summary>
    private static QueryOperator CollapseChain(QueryOperator top)
    {
        // Walk the chain top-down, collecting MIO nodes and the alias
        // enrichers that bridged between them. Stop at the first
        // non-anchor (either a non-foldable enricher's source, or
        // anything else entirely).
        List<QueryOperator> anchors = [];
        List<RowEnricherOperator> bridgeEnrichers = [];

        QueryOperator cursor = top;
        QueryOperator? bottomSource = null;
        while (true)
        {
            anchors.Add(cursor);
            QueryOperator? src = cursor switch
            {
                ModelInvocationOperator m => m.Source,
                _ => null,
            };
            // Walk through pure-alias enrichers, recording them for later
            // restacking.
            while (src is RowEnricherOperator enr && IsPassThroughAliasEnricher(enr))
            {
                bridgeEnrichers.Add(enr);
                src = enr.Source;
            }
            if (src is not null && IsCollapseAnchor(src))
            {
                cursor = src;
                continue;
            }
            bottomSource = src;
            break;
        }

        // Anchors are top-down (outermost first); invocations need to run
        // innermost-first because outer expressions may reference inner
        // output columns.
        anchors.Reverse();
        // Bridge enrichers were collected in walk order (top-down). When
        // restacking we want the same top-down order — the outermost
        // enricher ends up on top of the stack again.
        bridgeEnrichers.Reverse();

        // Build the alias map by walking innermost-to-outermost. Inner
        // enrichers contribute aliases first, so an outer enricher's
        // RHS referencing an inner alias resolves transitively to the
        // canonical hidden column name.
        Dictionary<string, string> aliasMap = new(StringComparer.OrdinalIgnoreCase);

        // Flatten anchors into an invocation list, alias-rewriting input
        // expressions as the alias map grows.
        List<ModelInvocationOperator.Invocation> invocations = [];
        int enricherIdx = 0;
        for (int anchorIdx = 0; anchorIdx < anchors.Count; anchorIdx++)
        {
            QueryOperator anchor = anchors[anchorIdx];
            AppendInvocations(anchor, aliasMap, invocations);

            // The enricher (if any) that sits ABOVE this anchor binds
            // aliases that become visible to anchors above it. Note the
            // ordering: enrichers were reversed to outer-first, but here
            // we're walking anchors inner-to-outer, so the enricher
            // between anchor[i] and anchor[i+1] is bridgeEnrichers[N-1-i]
            // — i.e., we consume bridge enrichers in REVERSE-of-reversed
            // = original walk order... but reversed against anchor order.
            // Easier: enrichers between anchor[i] and anchor[i+1] (inner
            // to outer) live at index `bridgeEnrichers.Count - 1 - i`.
            if (anchorIdx < anchors.Count - 1)
            {
                int bridgeIdx = bridgeEnrichers.Count - 1 - enricherIdx;
                if (bridgeIdx >= 0 && bridgeIdx < bridgeEnrichers.Count)
                {
                    AddEnricherAliases(bridgeEnrichers[bridgeIdx], aliasMap);
                }
                enricherIdx++;
            }
        }

        // Stack: collapsed MIO at the bottom, then bridge enrichers in
        // their original outer-to-inner order with the outermost on top.
        QueryOperator result = new ModelInvocationOperator(bottomSource!, invocations);
        // bridgeEnrichers is currently outer-first. To put the OUTERMOST
        // enricher on TOP of the stack, we apply innermost-first.
        for (int i = bridgeEnrichers.Count - 1; i >= 0; i--)
        {
            result = new RowEnricherOperator(result, bridgeEnrichers[i].Enrichments);
        }
        return result;
    }

    /// <summary>
    /// Appends <paramref name="anchor"/>'s invocations to
    /// <paramref name="invocations"/>, applying <paramref name="aliasMap"/>
    /// to each invocation's input + override expressions so column
    /// references that used a LET-aliased name resolve to the canonical
    /// hidden column name the new operator will emit.
    /// </summary>
    private static void AppendInvocations(
        QueryOperator anchor,
        IReadOnlyDictionary<string, string> aliasMap,
        List<ModelInvocationOperator.Invocation> invocations)
    {
        if (anchor is ModelInvocationOperator mio)
        {
            // Single-invocation MIO: alias-rewrite this anchor's
            // expressions. Multi-invocation MIO (already-collapsed inner
            // operator from children-first walk): its invocations were
            // aliased at their own collapse pass and we don't re-alias
            // them here. The column names they reference are either
            // source columns (which flow through the new operator's
            // source-stabilize stage) or other invocation outputs
            // already in the invocation list above this one.
            if (mio.Invocations.Count == 1)
            {
                ModelInvocationOperator.Invocation inv = mio.Invocations[0];
                invocations.Add(new ModelInvocationOperator.Invocation(
                    inv.ModelName,
                    RewriteAll(inv.InputExpressions, aliasMap),
                    RewriteAll(inv.OptionalExpressions, aliasMap),
                    inv.OutputColumnName));
            }
            else
            {
                foreach (ModelInvocationOperator.Invocation inv in mio.Invocations)
                {
                    invocations.Add(inv);
                }
            }
        }
    }

    private static IReadOnlyList<Expression> RewriteAll(
        IReadOnlyList<Expression> expressions, IReadOnlyDictionary<string, string> aliasMap)
    {
        if (aliasMap.Count == 0) return expressions;
        Expression[] rewritten = new Expression[expressions.Count];
        for (int i = 0; i < expressions.Count; i++)
        {
            rewritten[i] = RewriteExpression(expressions[i], aliasMap);
        }
        return rewritten;
    }

    /// <summary>
    /// Inserts <paramref name="enricher"/>'s aliases into
    /// <paramref name="aliasMap"/>, resolving each RHS column name
    /// transitively through any already-mapped aliases so that
    /// <c>LET b = a; LET a = ...</c> produces a map where <c>b</c> maps
    /// directly to <c>a</c>'s canonical name.
    /// </summary>
    private static void AddEnricherAliases(RowEnricherOperator enricher, Dictionary<string, string> aliasMap)
    {
        foreach (RowEnrichment e in enricher.Enrichments)
        {
            if (e.Expression is not ColumnReference cref) continue;
            string canonical = aliasMap.TryGetValue(cref.ColumnName, out string? resolved)
                ? resolved
                : cref.ColumnName;
            aliasMap[e.ColumnName] = canonical;
        }
    }

    // ─── Expression rewriter: substitutes ColumnReference names per the alias map ───

    private static Expression RewriteExpression(Expression expression, IReadOnlyDictionary<string, string> aliasMap)
    {
        Expression rewritten = RewriteChildren(expression, aliasMap);

        if (rewritten is ColumnReference cref &&
            aliasMap.TryGetValue(cref.ColumnName, out string? canonical))
        {
            return cref with { ColumnName = canonical };
        }
        return rewritten;
    }

    /// <summary>
    /// Children-first recursive walk. Mirrors
    /// <see cref="InlineAccessorElider"/>'s <c>RewriteChildren</c> so the
    /// two passes cover the same expression-node surface.
    /// </summary>
    private static Expression RewriteChildren(Expression expression, IReadOnlyDictionary<string, string> aliasMap)
    {
        Expression Rec(Expression e) => RewriteExpression(e, aliasMap);

        return expression switch
        {
            FunctionCallExpression f => f with
            {
                Arguments = f.Arguments.Select(Rec).ToList(),
                OrderBy = f.OrderBy?.Select(i => new OrderByItem(Rec(i.Expression), i.Direction)).ToList(),
                WithinGroupOrderBy = f.WithinGroupOrderBy?.Select(i => new OrderByItem(Rec(i.Expression), i.Direction)).ToList(),
            },
            BinaryExpression b => new BinaryExpression(Rec(b.Left), b.Operator, Rec(b.Right)),
            UnaryExpression u => new UnaryExpression(u.Operator, Rec(u.Operand)),
            CastExpression c => c with { Expression = Rec(c.Expression) },
            CaseExpression ce => new CaseExpression(
                ce.Operand is { } o ? Rec(o) : null,
                ce.WhenClauses.Select(w => new WhenClause(Rec(w.Condition), Rec(w.Result))).ToList(),
                ce.ElseResult is { } er ? Rec(er) : null,
                ce.Span),
            InExpression ie => new InExpression(Rec(ie.Expression), ie.Values.Select(Rec).ToList(), ie.Negated),
            BetweenExpression be => new BetweenExpression(Rec(be.Expression), Rec(be.Low), Rec(be.High), be.Negated),
            IsNullExpression isn => new IsNullExpression(Rec(isn.Expression), isn.Negated),
            LikeExpression lk => new LikeExpression(Rec(lk.Expression), Rec(lk.Pattern), Rec(lk.EscapeCharacter), lk.CaseInsensitive),
            AtTimeZoneExpression atz => atz with { Expression = Rec(atz.Expression), TimeZone = Rec(atz.TimeZone) },
            LambdaExpression lam => lam with { Body = Rec(lam.Body) },
            IndexAccessExpression ix => ix with { Source = Rec(ix.Source), Indices = ix.Indices.Select(Rec).ToArray() },
            StructLiteralExpression sl => sl with
            {
                Fields = sl.Fields.Select(f => new StructField(f.Name, Rec(f.Value))).ToList(),
            },
            InlineAccessorExpression iax => iax with { Argument = Rec(iax.Argument) },
            _ => expression,
        };
    }
}
