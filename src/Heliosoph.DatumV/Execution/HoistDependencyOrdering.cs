using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution;

/// <summary>
/// Shared subtree-subsumption helper for hoisting passes.
/// </summary>
/// <remarks>
/// <para>
/// When a pass decides to hoist multiple subexpressions, some of those
/// subexpressions may contain other hoist targets as nested subtrees —
/// e.g. <c>f(g(x))</c> hoisted alongside <c>g(x)</c>. Without subsumption,
/// the outer's enrichment expression recomputes <c>g(x)</c> internally,
/// duplicating work the inner hoist already does. With subsumption, the
/// outer's enrichment becomes <c>f(__cse_inner)</c>, referencing the inner
/// hoist's hidden column.
/// </para>
/// <para>
/// The implementation requirement: producers (<see cref="Operators.RowEnricherOperator"/>
/// or <see cref="Operators.ModelInvocationOperator"/>) must evaluate inner
/// subtrees <em>first</em> so the column the outer references is on the
/// row by the time the outer producer runs. <see cref="OrderByDependency"/>
/// returns hoists in topologically-grouped levels — level 0 has no
/// dependencies, level <c>n</c> may reference any earlier-level column.
/// Callers stack one producer per level (innermost level closest to the
/// source) so the dependency invariant holds.
/// </para>
/// <para>
/// Cycles are impossible — fingerprints describe finite AST subtrees, and a
/// subtree can't contain itself. The topological pass throws if it
/// encounters a stuck state, defensively.
/// </para>
/// </remarks>
internal static class HoistDependencyOrdering
{
    /// <summary>
    /// Topologically groups <paramref name="hoists"/> by mutual dependency.
    /// The returned list orders levels innermost-first: level 0 has no
    /// dependencies (its enrichments only reference source columns), level 1
    /// may reference level 0, etc. Callers stack one producer per level
    /// with level 0 closest to the source.
    /// </summary>
    /// <param name="hoists">
    /// The fingerprint-keyed map of hoists to order. Each entry's
    /// <c>Canonical</c> is the AST expression that becomes the producer's
    /// computation; its subtrees are inspected for references to other
    /// fingerprints in <paramref name="hoists"/>.
    /// </param>
    public static List<List<string>> OrderByDependency(
        IReadOnlyDictionary<string, Expression> hoists)
    {
        // Build the dependency graph: fp → set of OTHER fps in `hoists` that
        // appear as subtrees of fp's canonical.
        Dictionary<string, HashSet<string>> deps = new(StringComparer.Ordinal);
        foreach ((string fp, Expression canonical) in hoists)
        {
            HashSet<string> myDeps = new(StringComparer.Ordinal);
            CollectInnerFingerprints(canonical, hoists, myDeps, skipSelf: fp);
            deps[fp] = myDeps;
        }

        // Kahn-style level grouping. At each step, pick all fps whose deps are
        // already processed; they form the next level.
        HashSet<string> processed = new(StringComparer.Ordinal);
        List<List<string>> levels = new();
        while (processed.Count < hoists.Count)
        {
            List<string> level = new();
            foreach (string fp in hoists.Keys)
            {
                if (processed.Contains(fp)) continue;
                if (deps[fp].All(d => processed.Contains(d) || !hoists.ContainsKey(d)))
                {
                    level.Add(fp);
                }
            }
            if (level.Count == 0)
            {
                throw new InvalidOperationException(
                    "Cycle detected in hoist dependency graph. AST subtrees can't be cyclic; " +
                    "this indicates a fingerprinting bug.");
            }
            foreach (string fp in level) processed.Add(fp);
            levels.Add(level);
        }
        return levels;
    }

    /// <summary>
    /// Walks <paramref name="expr"/> and registers every fingerprint that is
    /// a key in <paramref name="hoists"/> AND not equal to <paramref name="skipSelf"/>.
    /// Top-down match — when a subtree's fingerprint matches, the search stops
    /// descending below it (the inner deps are the matched fingerprint's
    /// concern, not this caller's).
    /// </summary>
    private static void CollectInnerFingerprints(
        Expression expr,
        IReadOnlyDictionary<string, Expression> hoists,
        HashSet<string> deps,
        string skipSelf)
    {
        string fp = QueryExplainer.FormatExpression(expr);
        if (fp != skipSelf && hoists.ContainsKey(fp))
        {
            deps.Add(fp);
            return;
        }

        VisitChildren(expr, child => CollectInnerFingerprints(child, hoists, deps, skipSelf));
    }

    /// <summary>
    /// Generic AST descent into expression children. Mirrors the visitors in
    /// <see cref="ModelInvocationHoister"/> and <see cref="CommonSubexpressionEliminator"/>;
    /// kept here so dependency analysis doesn't depend on either of those classes.
    /// </summary>
    private static void VisitChildren(Expression expression, Action<Expression> visitor)
    {
        switch (expression)
        {
            case BinaryExpression b: visitor(b.Left); visitor(b.Right); break;
            case UnaryExpression u: visitor(u.Operand); break;
            case CastExpression c: visitor(c.Expression); break;
            case IsNullExpression n: visitor(n.Expression); break;
            case BetweenExpression bt: visitor(bt.Expression); visitor(bt.Low); visitor(bt.High); break;
            case InExpression i:
                visitor(i.Expression);
                foreach (Expression v in i.Values) visitor(v);
                break;
            case LikeExpression like:
                visitor(like.Expression); visitor(like.Pattern); visitor(like.EscapeCharacter);
                break;
            case CaseExpression ce:
                if (ce.Operand is not null) visitor(ce.Operand);
                foreach (WhenClause w in ce.WhenClauses) { visitor(w.Condition); visitor(w.Result); }
                if (ce.ElseResult is not null) visitor(ce.ElseResult);
                break;
            case FunctionCallExpression fn:
                foreach (Expression arg in fn.Arguments) visitor(arg);
                break;
            case StructLiteralExpression sl:
                foreach (StructField f in sl.Fields) visitor(f.Value);
                break;
            case IndexAccessExpression ia:
                visitor(ia.Source);
                foreach (Expression i in ia.Indices) visitor(i);
                break;
            case AtTimeZoneExpression atz:
                visitor(atz.Expression); visitor(atz.TimeZone);
                break;
            case LambdaExpression lam:
                visitor(lam.Body);
                break;
            // Leaves and unhandled kinds: no children.
        }
    }
}
