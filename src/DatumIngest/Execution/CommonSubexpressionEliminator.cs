using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

/// <summary>
/// Plan-time pass that identifies pure subexpressions which appear at
/// multiple call sites within a single <see cref="ProjectOperator"/> and
/// hoists them into a shared <see cref="RowEnricherOperator"/>. The pass
/// is LET-aware: when a duplicate matches the body of an existing LET
/// binding, references rewrite to that LET's name rather than allocating
/// a fresh hidden column.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Scope (first slice).</strong> Within-projection only — duplicates
/// must appear inside the same <see cref="ProjectOperator"/>'s columns or
/// LET bodies. Cross-clause CSE (WHERE / ORDER BY / SELECT sharing the same
/// expression) lands in a follow-up pass that places the
/// <see cref="RowEnricherOperator"/> earlier in the pipeline.
/// </para>
/// <para>
/// <strong>Eligibility.</strong> A subtree is CSE-candidate when:
/// <list type="bullet">
///   <item><description>Its fingerprint (<see cref="QueryExplainer.FormatExpression"/>) appears at <c>≥ 2</c> sites.</description></item>
///   <item><description>Every leaf function is pure — <see cref="IScalarFunction.IsPure"/> = <see langword="true"/>; aggregates, models, and unknown functions disqualify.</description></item>
///   <item><description>It is non-trivial — not just a <see cref="ColumnReference"/> or <see cref="LiteralExpression"/> (those are already cheap).</description></item>
///   <item><description>It does <em>not</em> reference any LET-bound name — keeps the hoist placement (RowEnricher above source, below the projection) sound.</description></item>
///   <item><description>It does <em>not</em> contain a <see cref="LambdaExpression"/> — alpha-equivalence isn't done in this slice.</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Ordering relative to model hoisting.</strong> CSE runs <em>after</em>
/// <see cref="ModelInvocationHoister"/>, so model calls have already been
/// replaced by <see cref="ColumnReference"/>s. CSE therefore can't directly
/// dedup textually-identical model calls; that gap is the model hoister's
/// own concern (see its CSE-invariant note). Once CSE runs, downstream
/// references to the hoisted column carry through naturally.
/// </para>
/// </remarks>
public static class CommonSubexpressionEliminator
{
    /// <summary>The synthesised column-name prefix for hoisted subexpressions.</summary>
    public const string CseColumnPrefix = "__cse_";

    /// <summary>
    /// Walks the operator tree rooted at <paramref name="op"/> and rewrites
    /// it so duplicated pure subexpressions are hoisted into shared
    /// <see cref="RowEnricherOperator"/>s.
    /// </summary>
    /// <remarks>
    /// Two-stage pass:
    /// <list type="number">
    ///   <item><description>
    ///     <strong>Cross-clause</strong> (<see cref="EliminateCrossClauseFirst"/>) —
    ///     walks linear chains of {Project, Filter, OrderBy} and finds pure
    ///     subexpressions appearing at sites in two or more different operators.
    ///     Inserts one <see cref="RowEnricherOperator"/> at the deepest
    ///     referencing operator's source, rewrites every reference site.
    ///   </description></item>
    ///   <item><description>
    ///     <strong>Within-Project</strong> (<see cref="EliminateRecursive"/>) —
    ///     handles residual within-operator duplicates inside any one
    ///     <see cref="ProjectOperator"/>, with LET-name unification when a
    ///     duplicate matches the body of an existing LET binding.
    ///   </description></item>
    /// </list>
    /// </remarks>
    public static IQueryOperator Eliminate(IQueryOperator op, FunctionRegistry functions)
    {
        op = EliminateCrossClauseFirst(op, functions);
        return EliminateRecursive(op, functions);
    }

    private static IQueryOperator EliminateRecursive(IQueryOperator op, FunctionRegistry functions)
    {
        if (op is ProjectOperator project)
        {
            IQueryOperator newSource = EliminateRecursive(project.Source, functions);
            return EliminateInProject(project, newSource, functions);
        }
        return RewriteChildren(op, child => EliminateRecursive(child, functions));
    }

    /// <summary>
    /// Stage-1 cross-clause CSE: walks linear chains of {Project, Filter,
    /// OrderBy} and finds pure subexpressions appearing at sites in two or
    /// more different operators. Inserts one <see cref="RowEnricherOperator"/>
    /// upstream of the deepest referencing operator and rewrites every
    /// occurrence (across every clause) to a <see cref="ColumnReference"/>
    /// targeting the hidden column.
    /// </summary>
    private static IQueryOperator EliminateCrossClauseFirst(
        IQueryOperator op, FunctionRegistry functions)
    {
        if (IsCrossClauseChainable(op))
        {
            List<IQueryOperator> chain = new();
            IQueryOperator cursor = op;
            while (IsCrossClauseChainable(cursor))
            {
                chain.Add(cursor);
                cursor = GetChainSource(cursor);
            }

            IQueryOperator newSource = EliminateCrossClauseFirst(cursor, functions);
            return ProcessChainCrossClause(chain, newSource, functions);
        }

        return RewriteChildren(op, child => EliminateCrossClauseFirst(child, functions));
    }

    private static bool IsCrossClauseChainable(IQueryOperator op) =>
        op is ProjectOperator or FilterOperator or OrderByOperator;

    private static IQueryOperator GetChainSource(IQueryOperator op) => op switch
    {
        ProjectOperator p => p.Source,
        FilterOperator f => f.Source,
        OrderByOperator ob => ob.Source,
        _ => throw new InvalidOperationException(
            $"GetChainSource called on non-chainable operator {op.GetType().Name}."),
    };

    private static IQueryOperator ProcessChainCrossClause(
        List<IQueryOperator> chain, IQueryOperator source, FunctionRegistry functions)
    {
        if (chain.Count == 0) return source;
        if (chain.Count == 1)
        {
            // Single chain operator — no cross-clause possible.
            return RebuildChainOperator(chain[0], source, EmptyFingerprintMap);
        }

        // LET names from any Project in the chain, so candidates that reference
        // them are excluded (placement upstream would precede the LET evaluation).
        HashSet<string> letNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (IQueryOperator c in chain)
        {
            if (c is ProjectOperator p && p.LetBindings is not null)
            {
                foreach (LetBinding b in p.LetBindings)
                {
                    letNames.Add(b.Name);
                }
            }
        }

        // Per-operator candidate enumeration into a chain-wide entry map.
        Dictionary<string, CrossClauseEntry> entries = new(StringComparer.Ordinal);
        for (int i = 0; i < chain.Count; i++)
        {
            VisitChainOperatorSites(chain[i], i, entries, letNames, functions);
        }

        // Filter to fingerprints that appear in 2+ distinct operators.
        Dictionary<string, string> fpToColumn = new(StringComparer.Ordinal);
        Dictionary<string, int> fpToDeepest = new(StringComparer.Ordinal);
        Dictionary<string, Expression> fpToCanonical = new(StringComparer.Ordinal);
        int counter = 0;
        foreach ((string fp, CrossClauseEntry entry) in entries)
        {
            if (entry.OperatorIndices.Count < 2) continue;
            fpToColumn[fp] = $"__cse_xc{counter++}";
            fpToDeepest[fp] = entry.OperatorIndices.Max();
            fpToCanonical[fp] = entry.Canonical;
        }

        if (fpToColumn.Count == 0)
        {
            // No cross-clause work needed. Rebuild chain unchanged with new source.
            IQueryOperator unchanged = source;
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                unchanged = RebuildChainOperator(chain[i], unchanged, EmptyFingerprintMap);
            }
            return unchanged;
        }

        // Group hoists by their target index (the deepest referencing operator).
        Dictionary<int, List<string>> hoistsByIndex = new();
        foreach ((string fp, int idx) in fpToDeepest)
        {
            if (!hoistsByIndex.TryGetValue(idx, out List<string>? list))
            {
                list = new List<string>();
                hoistsByIndex[idx] = list;
            }
            list.Add(fp);
        }

        // Rebuild bottom-up: at each chain position, insert the RowEnricher for
        // hoists targeting that position before rebuilding the operator.
        IQueryOperator aug = source;
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            if (hoistsByIndex.TryGetValue(i, out List<string>? fps))
            {
                List<RowEnrichment> enrichments = new(fps.Count);
                foreach (string fp in fps)
                {
                    // No subtree subsumption in this pass — each enrichment
                    // expression stays as-is. If two hoists nest (f(g(x)) and
                    // g(x) both cross-clause), the outer's enrichment will
                    // recompute g(x) internally. Subsumption is a separate
                    // optimisation that requires dependency-ordered enricher
                    // stacking.
                    enrichments.Add(new RowEnrichment(fpToColumn[fp], fpToCanonical[fp]));
                }
                aug = new RowEnricherOperator(aug, enrichments);
            }
            aug = RebuildChainOperator(chain[i], aug, fpToColumn);
        }
        return aug;
    }

    private static readonly Dictionary<string, string> EmptyFingerprintMap =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Per-operator visit that delegates expression collection to
    /// <see cref="CollectCandidatesXC"/>, tagging each candidate with its
    /// chain-operator index so cross-clause analysis knows which operators
    /// reference which fingerprint.
    /// </summary>
    private static void VisitChainOperatorSites(
        IQueryOperator op,
        int operatorIndex,
        Dictionary<string, CrossClauseEntry> entries,
        HashSet<string> letNames,
        FunctionRegistry functions)
    {
        switch (op)
        {
            case ProjectOperator p:
                if (p.LetBindings is not null)
                {
                    foreach (LetBinding b in p.LetBindings)
                    {
                        CollectCandidatesXC(b.Expression, operatorIndex, entries, letNames, functions);
                    }
                }
                foreach (SelectColumn c in p.Columns)
                {
                    CollectCandidatesXC(c.Expression, operatorIndex, entries, letNames, functions);
                }
                break;
            case FilterOperator f:
                CollectCandidatesXC(f.Predicate, operatorIndex, entries, letNames, functions);
                break;
            case OrderByOperator ob:
                foreach (OrderByItem i in ob.OrderByItems)
                {
                    CollectCandidatesXC(i.Expression, operatorIndex, entries, letNames, functions);
                }
                break;
        }
    }

    /// <summary>
    /// Like <see cref="CollectCandidates"/> but tags each registered subtree
    /// with the chain-operator index it appeared in. Same eligibility rules:
    /// pure, non-trivial, no LET-name reference, no lambda.
    /// </summary>
    private static void CollectCandidatesXC(
        Expression expression,
        int operatorIndex,
        Dictionary<string, CrossClauseEntry> entries,
        HashSet<string> letNames,
        FunctionRegistry functions)
    {
        VisitChildren(
            expression,
            child => CollectCandidatesXC(child, operatorIndex, entries, letNames, functions));

        if (expression is ColumnReference or LiteralExpression or ParameterExpression
            or TypeLiteralExpression or CurrentTimestampExpression)
        {
            return;
        }

        if (!IsCseEligible(expression, letNames, functions))
        {
            return;
        }

        string fp = QueryExplainer.FormatExpression(expression);
        if (!entries.TryGetValue(fp, out CrossClauseEntry? entry))
        {
            entry = new CrossClauseEntry(expression);
            entries[fp] = entry;
        }
        entry.OperatorIndices.Add(operatorIndex);
    }

    /// <summary>
    /// Rebuilds a chainable operator with a new source and every CSE-hoisted
    /// subtree replaced by a <see cref="ColumnReference"/>. Mirror of
    /// <see cref="RewriteWithHoists"/> applied per expression site.
    /// </summary>
    private static IQueryOperator RebuildChainOperator(
        IQueryOperator op,
        IQueryOperator newSource,
        IReadOnlyDictionary<string, string> hoists)
    {
        return op switch
        {
            ProjectOperator p => new ProjectOperator(
                newSource,
                p.Columns
                    .Select(c => c with { Expression = RewriteWithHoists(c.Expression, hoists) })
                    .ToArray(),
                p.LetBindings?
                    .Select(b => b with { Expression = RewriteWithHoists(b.Expression, hoists) })
                    .ToArray(),
                p.Assertions),
            FilterOperator f => new FilterOperator(
                newSource,
                RewriteWithHoists(f.Predicate, hoists)),
            OrderByOperator ob => new OrderByOperator(
                newSource,
                ob.OrderByItems
                    .Select(i => i with { Expression = RewriteWithHoists(i.Expression, hoists) })
                    .ToArray(),
                ob.TopNRows),
            _ => throw new InvalidOperationException(
                $"RebuildChainOperator called on non-chainable {op.GetType().Name}."),
        };
    }

    /// <summary>
    /// One cross-clause fingerprint's tally: the canonical occurrence (the
    /// first one seen during traversal) and the set of chain-operator indices
    /// where it appeared. A fingerprint with two or more distinct indices is
    /// a cross-clause candidate.
    /// </summary>
    private sealed class CrossClauseEntry
    {
        public Expression Canonical { get; }
        public HashSet<int> OperatorIndices { get; } = new();
        public CrossClauseEntry(Expression canonical) { Canonical = canonical; }
    }

    private static IQueryOperator EliminateInProject(
        ProjectOperator project, IQueryOperator source, FunctionRegistry functions)
    {
        HashSet<string> letNames = project.LetBindings is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(
                project.LetBindings.Select(b => b.Name),
                StringComparer.OrdinalIgnoreCase);

        // First pass: collect occurrence counts of every non-trivial pure subtree
        // appearing in projection columns and LET binding bodies.
        Dictionary<string, FingerprintEntry> entries = new(StringComparer.Ordinal);
        if (project.LetBindings is not null)
        {
            foreach (LetBinding binding in project.LetBindings)
            {
                CollectCandidates(binding.Expression, entries, letNames, functions);
            }
        }
        foreach (SelectColumn column in project.Columns)
        {
            CollectCandidates(column.Expression, entries, letNames, functions);
        }

        if (entries.Count == 0)
        {
            // No pure non-trivial subexpressions appeared — nothing to consider.
            return ReferenceEquals(source, project.Source)
                ? project
                : RewriteChildren(project, _ => source);
        }

        // Map LET binding bodies to their LET name. When the CSE-eligible fingerprint
        // already names a LET, prefer that name (cleaner EXPLAIN, no __cse_N).
        Dictionary<string, string> letBodyToName = new(StringComparer.Ordinal);
        if (project.LetBindings is not null)
        {
            foreach (LetBinding binding in project.LetBindings)
            {
                string fp = QueryExplainer.FormatExpression(binding.Expression);
                letBodyToName.TryAdd(fp, binding.Name);
            }
        }

        // Second pass: decide which fingerprints to hoist.
        // Eligibility: appears ≥ 2 times AND (already names a LET OR is non-trivial CSE candidate).
        // The "appears ≥ 2 times" bar applies even when a LET names it — a LET
        // referenced exactly once needs no rewrite.
        Dictionary<string, string> fingerprintToColumn = new(StringComparer.Ordinal);
        List<RowEnrichment> enrichments = new();
        int cseCounter = 0;

        foreach ((string fingerprint, FingerprintEntry entry) in entries)
        {
            if (entry.Count < 2) continue;

            if (letBodyToName.TryGetValue(fingerprint, out string? letName))
            {
                // LET unification — references rewrite to the LET name. The LET
                // binding stays put; no enrichment needed.
                fingerprintToColumn[fingerprint] = letName;
            }
            else
            {
                string column = $"{CseColumnPrefix}{cseCounter++}";
                fingerprintToColumn[fingerprint] = column;
                enrichments.Add(new RowEnrichment(column, entry.Canonical));
            }
        }

        if (fingerprintToColumn.Count == 0)
        {
            return ReferenceEquals(source, project.Source)
                ? project
                : RewriteChildren(project, _ => source);
        }

        // Third pass: rewrite expressions. For each occurrence whose fingerprint
        // is in the hoist map, replace with ColumnReference. For LET unification,
        // the binding's body itself is NOT rewritten (the LET still names the
        // expression). For __cse_N hoists, the binding's body IS rewritten so
        // it tail-references the hidden column instead of recomputing.
        LetBinding[]? rewrittenLet = null;
        if (project.LetBindings is not null)
        {
            rewrittenLet = new LetBinding[project.LetBindings.Count];
            for (int i = 0; i < project.LetBindings.Count; i++)
            {
                LetBinding binding = project.LetBindings[i];
                Expression bindingBody = binding.Expression;
                string bindingFingerprint = QueryExplainer.FormatExpression(bindingBody);

                Expression rewrittenBody;
                if (letBodyToName.TryGetValue(bindingFingerprint, out string? boundName)
                    && string.Equals(boundName, binding.Name, StringComparison.OrdinalIgnoreCase))
                {
                    // This is the LET that names the fingerprint — leave its body
                    // intact (the body IS the canonical expression). Other LETs
                    // bound to the same fingerprint, and projection columns
                    // matching it, will rewrite to this name in the loop below.
                    rewrittenBody = bindingBody;
                }
                else
                {
                    rewrittenBody = RewriteWithHoists(bindingBody, fingerprintToColumn);
                }

                rewrittenLet[i] = binding with { Expression = rewrittenBody };
            }
        }

        SelectColumn[] rewrittenColumns = new SelectColumn[project.Columns.Count];
        for (int i = 0; i < project.Columns.Count; i++)
        {
            rewrittenColumns[i] = project.Columns[i] with
            {
                Expression = RewriteWithHoists(project.Columns[i].Expression, fingerprintToColumn),
            };
        }

        IQueryOperator augmented = enrichments.Count == 0
            ? source
            : new RowEnricherOperator(source, enrichments);

        return new ProjectOperator(
            augmented,
            rewrittenColumns,
            rewrittenLet,
            project.Assertions);
    }

    /// <summary>
    /// Walks <paramref name="expression"/>, computes a fingerprint for every
    /// non-trivial subtree, and records its first occurrence + occurrence
    /// count. Subtrees containing LET-bound name references, lambdas, or
    /// impure leaves are skipped (they're invisible to the fingerprint map
    /// even if they appear). The traversal still descends into them — child
    /// subtrees may be CSE-eligible even when their parent isn't.
    /// </summary>
    private static void CollectCandidates(
        Expression expression,
        Dictionary<string, FingerprintEntry> entries,
        HashSet<string> letNames,
        FunctionRegistry functions)
    {
        // Always recurse into children so deeper candidates are still tracked.
        VisitChildren(expression, child => CollectCandidates(child, entries, letNames, functions));

        // Skip the trivial leaf cases — column references and literals are
        // already cheap; hoisting them adds operator overhead without benefit.
        if (expression is ColumnReference or LiteralExpression or ParameterExpression
            or TypeLiteralExpression or CurrentTimestampExpression)
        {
            return;
        }

        // Only register if the whole subtree is CSE-eligible.
        if (!IsCseEligible(expression, letNames, functions))
        {
            return;
        }

        string fingerprint = QueryExplainer.FormatExpression(expression);
        if (entries.TryGetValue(fingerprint, out FingerprintEntry existing))
        {
            entries[fingerprint] = existing with { Count = existing.Count + 1 };
        }
        else
        {
            entries[fingerprint] = new FingerprintEntry(expression, 1);
        }
    }

    /// <summary>
    /// Returns whether <paramref name="expression"/> is wholly CSE-eligible:
    /// purity check passes, no lambda, no LET-name reference, no aggregate /
    /// model / window / subquery leaf.
    /// </summary>
    private static bool IsCseEligible(
        Expression expression, HashSet<string> letNames, FunctionRegistry functions)
    {
        switch (expression)
        {
            case ColumnReference col:
                return !letNames.Contains(col.ColumnName);
            case LiteralExpression:
            case ParameterExpression:
            case TypeLiteralExpression:
                return true;
            case CurrentTimestampExpression:
                return false;
            case BinaryExpression bin:
                return IsCseEligible(bin.Left, letNames, functions)
                    && IsCseEligible(bin.Right, letNames, functions);
            case UnaryExpression un:
                return IsCseEligible(un.Operand, letNames, functions);
            case CastExpression cast:
                return IsCseEligible(cast.Expression, letNames, functions);
            case IsNullExpression isn:
                return IsCseEligible(isn.Expression, letNames, functions);
            case BetweenExpression bw:
                return IsCseEligible(bw.Expression, letNames, functions)
                    && IsCseEligible(bw.Low, letNames, functions)
                    && IsCseEligible(bw.High, letNames, functions);
            case InExpression inExpr:
                if (!IsCseEligible(inExpr.Expression, letNames, functions)) return false;
                foreach (Expression v in inExpr.Values)
                    if (!IsCseEligible(v, letNames, functions)) return false;
                return true;
            case LikeExpression like:
                return IsCseEligible(like.Expression, letNames, functions)
                    && IsCseEligible(like.Pattern, letNames, functions)
                    && IsCseEligible(like.EscapeCharacter, letNames, functions);
            case CaseExpression case_:
                if (case_.Operand is not null && !IsCseEligible(case_.Operand, letNames, functions))
                    return false;
                foreach (WhenClause w in case_.WhenClauses)
                {
                    if (!IsCseEligible(w.Condition, letNames, functions)) return false;
                    if (!IsCseEligible(w.Result, letNames, functions)) return false;
                }
                if (case_.ElseResult is not null && !IsCseEligible(case_.ElseResult, letNames, functions))
                    return false;
                return true;
            case StructLiteralExpression sl:
                foreach (StructField f in sl.Fields)
                    if (!IsCseEligible(f.Value, letNames, functions)) return false;
                return true;
            case IndexAccessExpression ia:
                return IsCseEligible(ia.Source, letNames, functions)
                    && IsCseEligible(ia.Index, letNames, functions);
            case AtTimeZoneExpression atz:
                return IsCseEligible(atz.Expression, letNames, functions)
                    && IsCseEligible(atz.TimeZone, letNames, functions);
            case FunctionCallExpression fn:
                return IsFunctionCallCseEligible(fn, letNames, functions);

            // Conservative skips:
            case LambdaExpression:           // alpha-equivalence not done in this slice
            case WindowFunctionCallExpression: // crosses rows
            case SubqueryExpression:         // expensive + correlated semantics
            case InSubqueryExpression:
            case ExistsExpression:
                return false;

            default:
                return false;
        }
    }

    private static bool IsFunctionCallCseEligible(
        FunctionCallExpression fn, HashSet<string> letNames, FunctionRegistry functions)
    {
        // Aggregates aggregate across rows; not CSE-eligible at projection scope.
        if (functions.TryGetAggregate(fn.FunctionName) is not null) return false;

        // Models are handled by ModelInvocationHoister; by the time CSE runs,
        // remaining function calls named "models.*" indicate hoisting was
        // skipped (e.g. catalog absent) — leave them alone.
        if (fn.FunctionName.StartsWith(ModelInvocationHoister.ModelNamespacePrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        IScalarFunction? scalar = functions.TryGetScalar(fn.FunctionName);
        if (scalar is null || !scalar.IsPure) return false;

        foreach (Expression arg in fn.Arguments)
            if (!IsCseEligible(arg, letNames, functions)) return false;

        return true;
    }

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
                visitor(ia.Source); visitor(ia.Index);
                break;
            case AtTimeZoneExpression atz:
                visitor(atz.Expression); visitor(atz.TimeZone);
                break;
            case LambdaExpression lam:
                visitor(lam.Body);
                break;
            // Leaves and unhandled kinds: no children to visit.
        }
    }

    /// <summary>
    /// Walks <paramref name="expression"/> top-down, replacing the largest
    /// subtree whose fingerprint matches a hoisted column. The traversal
    /// stops descending past a replacement — once a subtree is collapsed
    /// into a <see cref="ColumnReference"/>, smaller fingerprints inside
    /// it no longer apply.
    /// </summary>
    private static Expression RewriteWithHoists(
        Expression expression, IReadOnlyDictionary<string, string> hoists)
    {
        string fingerprint = QueryExplainer.FormatExpression(expression);
        if (hoists.TryGetValue(fingerprint, out string? hoistedColumn))
        {
            return new ColumnReference(TableName: null, ColumnName: hoistedColumn);
        }

        return expression switch
        {
            BinaryExpression b => b with
            {
                Left = RewriteWithHoists(b.Left, hoists),
                Right = RewriteWithHoists(b.Right, hoists),
            },
            UnaryExpression u => u with { Operand = RewriteWithHoists(u.Operand, hoists) },
            CastExpression c => c with { Expression = RewriteWithHoists(c.Expression, hoists) },
            IsNullExpression n => n with { Expression = RewriteWithHoists(n.Expression, hoists) },
            BetweenExpression bt => bt with
            {
                Expression = RewriteWithHoists(bt.Expression, hoists),
                Low = RewriteWithHoists(bt.Low, hoists),
                High = RewriteWithHoists(bt.High, hoists),
            },
            InExpression i => i with
            {
                Expression = RewriteWithHoists(i.Expression, hoists),
                Values = i.Values.Select(v => RewriteWithHoists(v, hoists)).ToList(),
            },
            LikeExpression like => like with
            {
                Expression = RewriteWithHoists(like.Expression, hoists),
                Pattern = RewriteWithHoists(like.Pattern, hoists),
                EscapeCharacter = RewriteWithHoists(like.EscapeCharacter, hoists),
            },
            CaseExpression ce => ce with
            {
                Operand = ce.Operand is null ? null : RewriteWithHoists(ce.Operand, hoists),
                WhenClauses = ce.WhenClauses
                    .Select(w => new WhenClause(
                        RewriteWithHoists(w.Condition, hoists),
                        RewriteWithHoists(w.Result, hoists)))
                    .ToList(),
                ElseResult = ce.ElseResult is null ? null : RewriteWithHoists(ce.ElseResult, hoists),
            },
            FunctionCallExpression fn => fn with
            {
                Arguments = fn.Arguments.Select(a => RewriteWithHoists(a, hoists)).ToList(),
            },
            StructLiteralExpression sl => sl with
            {
                Fields = sl.Fields
                    .Select(f => new StructField(f.Name, RewriteWithHoists(f.Value, hoists)))
                    .ToList(),
            },
            IndexAccessExpression ia => ia with
            {
                Source = RewriteWithHoists(ia.Source, hoists),
                Index = RewriteWithHoists(ia.Index, hoists),
            },
            AtTimeZoneExpression atz => atz with
            {
                Expression = RewriteWithHoists(atz.Expression, hoists),
                TimeZone = RewriteWithHoists(atz.TimeZone, hoists),
            },
            LambdaExpression lam => lam with
            {
                Body = RewriteWithHoists(lam.Body, hoists),
            },
            _ => expression,
        };
    }

    private static IQueryOperator RewriteChildren(
        IQueryOperator op, Func<IQueryOperator, IQueryOperator> childRewriter)
    {
        return op switch
        {
            ProjectOperator p => new ProjectOperator(
                childRewriter(p.Source), p.Columns, p.LetBindings, p.Assertions),
            FilterOperator f => new FilterOperator(childRewriter(f.Source), f.Predicate),
            OrderByOperator ob => new OrderByOperator(
                childRewriter(ob.Source), ob.OrderByItems, ob.TopNRows),
            LimitOperator l => RewriteLimit(l, childRewriter),
            ModelInvocationOperator m => new ModelInvocationOperator(
                childRewriter(m.Source), m.ModelName, m.InputExpressions, m.OptionalExpressions, m.OutputColumnName),
            RowEnricherOperator r => new RowEnricherOperator(childRewriter(r.Source), r.Enrichments),
            _ => op,
        };
    }

    private static IQueryOperator RewriteLimit(
        LimitOperator limit, Func<IQueryOperator, IQueryOperator> childRewriter)
    {
        IQueryOperator newSource = childRewriter(limit.Source);
        return ReferenceEquals(newSource, limit.Source)
            ? limit
            : new LimitOperator(newSource, limit.Limit, limit.Offset);
    }

    /// <summary>
    /// One distinct fingerprint's tally: the canonical AST occurrence (the
    /// first one seen) plus the count of times it has been observed.
    /// </summary>
    private readonly record struct FingerprintEntry(Expression Canonical, int Count);
}
