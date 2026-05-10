namespace DatumIngest.Tests.Execution;

using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Execution.Operators;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

/// <summary>
/// Tests the within-projection CSE pass: pure subexpressions appearing at
/// multiple sites in a single <see cref="ProjectOperator"/> are hoisted into
/// a shared <see cref="RowEnricherOperator"/>; LET-named matches reuse the
/// LET name instead of allocating a synthetic column.
/// </summary>
public sealed class CommonSubexpressionEliminationTests : ServiceTestBase
{
    private static QueryOperator PlanQuery(string sql, TableCatalog catalog)
    {
        QueryExpression query = SqlParser.Parse(sql);
        QueryPlanner planner = new(catalog, FunctionRegistry.CreateDefault());
        return planner.Plan(query);
    }

    private TableCatalog Catalog2Cols() =>
        CreateCatalog("t",
            columns: ["a", "b"],
            new object?[] { "alpha", "beta" },
            new object?[] { "gamma", "delta" });

    [Fact]
    public void DuplicatePureCall_HoistsIntoRowEnricher_AndProjectionRefsHidden()
    {
        // concat(a, b) appears twice — should hoist into a single RowEnricher,
        // both projection columns become ColumnReference("__cse_0").
        TableCatalog catalog = Catalog2Cols();
        QueryOperator plan = PlanQuery("SELECT concat(a, b), concat(a, b) FROM t", catalog);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        RowEnricherOperator enricher = Assert.IsType<RowEnricherOperator>(project.Source);

        // Single hoist, named __cse_0.
        Assert.Single(enricher.Enrichments);
        Assert.Equal("__cse_0", enricher.Enrichments[0].ColumnName);

        // The hidden expression is the concat call.
        FunctionCallExpression hoisted = Assert.IsType<FunctionCallExpression>(
            enricher.Enrichments[0].Expression);
        Assert.Equal("concat", hoisted.FunctionName);

        // Both projection columns are ColumnReferences to __cse_0.
        Assert.Equal(2, project.Columns.Count);
        ColumnReference col0 = Assert.IsType<ColumnReference>(project.Columns[0].Expression);
        ColumnReference col1 = Assert.IsType<ColumnReference>(project.Columns[1].Expression);
        Assert.Equal("__cse_0", col0.ColumnName);
        Assert.Equal("__cse_0", col1.ColumnName);
    }

    [Fact]
    public void DuplicateInsideLambdaBody_NotHoisted()
    {
        // Regression: CSE used to descend into lambda bodies, hoisting
        // subexpressions that reference the lambda's parameters out into a
        // RowEnricherOperator running outside the lambda. At execute time
        // the parameter ('x' below) isn't in scope at the enricher's row,
        // throwing "Name 'x' is not a declared variable in scope".
        // After the fix, lambda bodies are opaque to the CSE walker.
        TableCatalog catalog = Catalog2Cols();
        // array_transform takes (Array<T>, Lambda); the lambda body has
        // length(a) twice — a candidate that would historically have been
        // hoisted. With the fix, the lambda body is opaque, no hoist,
        // source isn't a RowEnricher.
        QueryOperator plan = PlanQuery(
            "SELECT array_transform([a, b], x -> length(x) + length(x)) FROM t",
            catalog);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        Assert.IsNotType<RowEnricherOperator>(project.Source);
    }

    [Fact]
    public void SingleOccurrence_NotHoisted()
    {
        // concat(a, b) appears exactly once — no CSE benefit, no rewrite.
        TableCatalog catalog = Catalog2Cols();
        QueryOperator plan = PlanQuery("SELECT concat(a, b) FROM t", catalog);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);

        // Source is NOT a RowEnricherOperator — nothing was hoisted.
        Assert.IsNotType<RowEnricherOperator>(project.Source);

        // Projection still holds the original concat call.
        FunctionCallExpression call = Assert.IsType<FunctionCallExpression>(project.Columns[0].Expression);
        Assert.Equal("concat", call.FunctionName);
    }

    [Fact]
    public void TopLevelHoistedColumn_PreservesOriginalNameAsAlias()
    {
        // Regression: when CSE hoisted an expression that ALSO appears at the
        // top level of a SELECT column, the column got rewritten to a bare
        // ColumnReference(__cse_0). The output schema then derived the column
        // name from the rewritten expression — surfacing "__cse_0" to users
        // instead of the original "concat".
        //
        // Fix: preserve the original derived name as an explicit alias when
        // the rewrite collapses the column's top-level expression.
        TableCatalog catalog = Catalog2Cols();
        QueryOperator plan = PlanQuery(
            "SELECT concat(a, b), upper(concat(a, b)) FROM t", catalog);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        Assert.IsType<RowEnricherOperator>(project.Source);

        // First column: expression is now ColumnReference(__cse_0), but the
        // column carries an Alias of "concat" so the output name survives.
        ColumnReference col0 = Assert.IsType<ColumnReference>(project.Columns[0].Expression);
        Assert.Equal("__cse_0", col0.ColumnName);
        Assert.Equal("concat", project.Columns[0].Alias);
    }

    [Fact]
    public void TopLevelHoistedColumn_ExplicitAliasPreserved()
    {
        // When the user already supplied an alias, CSE must not stomp it.
        TableCatalog catalog = Catalog2Cols();
        QueryOperator plan = PlanQuery(
            "SELECT concat(a, b) AS joined, upper(concat(a, b)) FROM t", catalog);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        Assert.Equal("joined", project.Columns[0].Alias);
    }

    [Fact]
    public void DuplicateColumnReferences_NotHoisted()
    {
        // Trivial leaves (column references) don't pay for hoisting.
        // SELECT a, a FROM t — no enricher, two ColRefs preserved.
        TableCatalog catalog = Catalog2Cols();
        QueryOperator plan = PlanQuery("SELECT a, a FROM t", catalog);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        Assert.IsNotType<RowEnricherOperator>(project.Source);

        ColumnReference c0 = Assert.IsType<ColumnReference>(project.Columns[0].Expression);
        ColumnReference c1 = Assert.IsType<ColumnReference>(project.Columns[1].Expression);
        Assert.Equal("a", c0.ColumnName);
        Assert.Equal("a", c1.ColumnName);
    }

    [Fact]
    public void LetBodyMatchesProjection_UsesLetName_NoSyntheticColumn()
    {
        // The user already named the expression with LET. CSE should reuse `v`
        // instead of allocating __cse_0. The LET binding's body is preserved
        // as-is (it IS the canonical site).
        TableCatalog catalog = Catalog2Cols();
        QueryOperator plan = PlanQuery(
            "SELECT LET v = concat(a, b), v, concat(a, b) FROM t",
            catalog);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);

        // No enricher inserted — LET unification doesn't need one.
        Assert.IsNotType<RowEnricherOperator>(project.Source);

        // LET binding body is unchanged.
        Assert.NotNull(project.LetBindings);
        Assert.Single(project.LetBindings);
        FunctionCallExpression letBody = Assert.IsType<FunctionCallExpression>(
            project.LetBindings![0].Expression);
        Assert.Equal("concat", letBody.FunctionName);
        Assert.Equal("v", project.LetBindings[0].Name);

        // Both columns now reference v (the explicit one stays "v"; the formerly-
        // duplicated concat(a,b) rewrites to "v" too).
        Assert.Equal(2, project.Columns.Count);
        ColumnReference c0 = Assert.IsType<ColumnReference>(project.Columns[0].Expression);
        ColumnReference c1 = Assert.IsType<ColumnReference>(project.Columns[1].Expression);
        Assert.Equal("v", c0.ColumnName);
        Assert.Equal("v", c1.ColumnName);
    }

    [Fact]
    public void DuplicateExpressionWithoutLet_AllocatesSyntheticColumn()
    {
        // Same as LET-unification case but no LET — should fall back to __cse_0.
        TableCatalog catalog = Catalog2Cols();
        QueryOperator plan = PlanQuery(
            "SELECT concat(a, b), concat(a, b) AS dup FROM t",
            catalog);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        RowEnricherOperator enricher = Assert.IsType<RowEnricherOperator>(project.Source);

        Assert.Single(enricher.Enrichments);
        Assert.Equal("__cse_0", enricher.Enrichments[0].ColumnName);
    }

    [Fact]
    public async Task EndToEnd_DuplicatePureCall_ProducesCorrectResultsOnce()
    {
        // Sanity: the rewrite preserves query semantics. concat(a,b) duplicated
        // should still produce two identical projection columns per row.
        TableCatalog catalog = Catalog2Cols();
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT concat(a, b) AS x, concat(a, b) AS y FROM t",
            catalog);

        Assert.Equal(2, rows.Count);
        Arena scratch = catalog.Pool.Backing.RentArena();
        try
        {
            Assert.Equal("alphabeta", rows[0]["x"].AsString(scratch));
            Assert.Equal("alphabeta", rows[0]["y"].AsString(scratch));
            Assert.Equal("gammadelta", rows[1]["x"].AsString(scratch));
            Assert.Equal("gammadelta", rows[1]["y"].AsString(scratch));
        }
        finally { catalog.Pool.Backing.TryReturn(scratch); }
    }

    [Fact]
    public async Task EndToEnd_LetUnification_PreservesResults()
    {
        // LET v + duplicate concat: query result unchanged, single evaluation.
        TableCatalog catalog = Catalog2Cols();
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT LET v = concat(a, b), v AS via_let, concat(a, b) AS direct FROM t",
            catalog);

        Assert.Equal(2, rows.Count);
        Arena scratch = catalog.Pool.Backing.RentArena();
        try
        {
            Assert.Equal("alphabeta", rows[0]["via_let"].AsString(scratch));
            Assert.Equal("alphabeta", rows[0]["direct"].AsString(scratch));
            Assert.Equal("gammadelta", rows[1]["via_let"].AsString(scratch));
            Assert.Equal("gammadelta", rows[1]["direct"].AsString(scratch));
        }
        finally { catalog.Pool.Backing.TryReturn(scratch); }
    }

    // ─────────────── Cross-clause CSE ───────────────

    [Fact]
    public void CrossClauseWhereSelect_HoistsUpstreamOfFilter()
    {
        // concat(a, b) appears in WHERE and in SELECT. Cross-clause stage
        // hoists once into a RowEnricher placed upstream of the FilterOperator
        // (the deepest reference), so both WHERE and SELECT see the hidden
        // column.
        TableCatalog catalog = Catalog2Cols();
        QueryOperator plan = PlanQuery(
            "SELECT concat(a, b) FROM t WHERE concat(a, b) = 'alphabeta'",
            catalog);

        // Walk the plan, count RowEnricherOperators and their position.
        int enricherCount = 0;
        bool enricherIsUpstreamOfFilter = false;
        QueryOperator? cursor = plan;
        QueryOperator? prev = null;
        while (cursor is not null)
        {
            if (cursor is RowEnricherOperator)
            {
                enricherCount++;
                if (prev is FilterOperator) enricherIsUpstreamOfFilter = true;
            }
            prev = cursor;
            cursor = cursor switch
            {
                ProjectOperator p => p.Source,
                FilterOperator f => f.Source,
                RowEnricherOperator r => r.Source,
                _ => null,
            };
        }

        Assert.Equal(1, enricherCount);
        Assert.True(enricherIsUpstreamOfFilter,
            "Cross-clause hoist should land between Filter and its source.");
    }

    [Fact]
    public void CrossClauseSelectOrderBy_HoistsOnce()
    {
        // concat(a, b) appears in SELECT and ORDER BY. One RowEnricher,
        // placed upstream of the deepest reference (OrderBy).
        TableCatalog catalog = Catalog2Cols();
        QueryOperator plan = PlanQuery(
            "SELECT concat(a, b) FROM t ORDER BY concat(a, b)",
            catalog);

        int enricherCount = 0;
        QueryOperator? cursor = plan;
        while (cursor is not null)
        {
            if (cursor is RowEnricherOperator) enricherCount++;
            cursor = cursor switch
            {
                ProjectOperator p => p.Source,
                FilterOperator f => f.Source,
                OrderByOperator ob => ob.Source,
                RowEnricherOperator r => r.Source,
                _ => null,
            };
        }

        Assert.Equal(1, enricherCount);
    }

    [Fact]
    public async Task EndToEnd_CrossClauseCSE_PreservesResults()
    {
        // The cross-clause rewrite must not change observable query semantics.
        TableCatalog catalog = Catalog2Cols();
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT concat(a, b) AS x FROM t WHERE concat(a, b) = 'alphabeta'",
            catalog);

        Assert.Single(rows);
        Arena scratch = catalog.Pool.Backing.RentArena();
        try
        {
            Assert.Equal("alphabeta", rows[0]["x"].AsString(scratch));
        }
        finally { catalog.Pool.Backing.TryReturn(scratch); }
    }

    [Fact]
    public void CrossClauseAndWithinProject_BothHoist()
    {
        // concat(a, b) appears in WHERE (1) and SELECT (2 sites). Cross-clause
        // catches all three; one hoist column reused everywhere. (The within-
        // Project pass would normally handle the SELECT-only duplicate, but
        // cross-clause runs first and unifies it with the WHERE site.)
        TableCatalog catalog = Catalog2Cols();
        QueryOperator plan = PlanQuery(
            "SELECT concat(a, b) AS first, concat(a, b) AS second " +
            "FROM t WHERE concat(a, b) = 'alphabeta'",
            catalog);

        // Exactly one RowEnricher in the chain.
        int enricherCount = 0;
        QueryOperator? cursor = plan;
        while (cursor is not null)
        {
            if (cursor is RowEnricherOperator) enricherCount++;
            cursor = cursor switch
            {
                ProjectOperator p => p.Source,
                FilterOperator f => f.Source,
                RowEnricherOperator r => r.Source,
                _ => null,
            };
        }

        Assert.Equal(1, enricherCount);
    }

    [Fact]
    public void NestedDuplicate_HoistsLargestSubtree()
    {
        // upper(concat(a,b)) appears twice. The LARGEST matching subtree wins:
        // upper(concat(a,b)) hoists to __cse_0, both projections rewrite to it.
        // The inner concat(a,b) is gone from the projections — it lives only
        // inside the enrichment expression.
        TableCatalog catalog = Catalog2Cols();
        QueryOperator plan = PlanQuery(
            "SELECT upper(concat(a, b)), upper(concat(a, b)) FROM t",
            catalog);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        RowEnricherOperator enricher = Assert.IsType<RowEnricherOperator>(project.Source);

        // Top-down rewrite picks the largest match — projections collapse to a
        // single ColRef, and only the outer call shows up as an enrichment.
        ColumnReference c0 = Assert.IsType<ColumnReference>(project.Columns[0].Expression);
        ColumnReference c1 = Assert.IsType<ColumnReference>(project.Columns[1].Expression);
        Assert.Equal(c0.ColumnName, c1.ColumnName);

        // The enrichment retains the upper(concat(...)) form — its inner concat
        // appears only here. With no second standalone concat reference there's
        // no subsumption work to do.
        FunctionCallExpression hoisted = Assert.IsType<FunctionCallExpression>(
            enricher.Enrichments.First(e => e.ColumnName == c0.ColumnName).Expression);
        Assert.Equal("upper", hoisted.FunctionName);
    }

    // ─────────────── Subtree subsumption ───────────────

    [Fact]
    public void NestedDuplicate_BothQualify_StackedEnrichersWithSubsumption()
    {
        // upper(concat(a, b)) appears twice; concat(a, b) ALSO appears as a
        // standalone projection. Both hoist. Subsumption: the outer
        // upper(__inner) enrichment references the inner concat enrichment's
        // column, so concat is computed once per row, upper once per row.
        TableCatalog catalog = Catalog2Cols();
        QueryOperator plan = PlanQuery(
            "SELECT upper(concat(a, b)) AS x, upper(concat(a, b)) AS y, concat(a, b) AS z FROM t",
            catalog);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);

        // Walk the chain of RowEnricherOperators. Expect TWO stacked enrichers:
        // one for concat (closer to source), one for upper(concat) (above it).
        // The upper enricher's expression must reference the concat column.
        List<RowEnricherOperator> enrichers = new();
        QueryOperator? cursor = project.Source;
        while (cursor is RowEnricherOperator e)
        {
            enrichers.Add(e);
            cursor = e.Source;
        }

        Assert.Equal(2, enrichers.Count);

        // The OUTERMOST enricher (first hit walking down) is the dependency-
        // last level — i.e., upper(concat). Its enrichment expression should
        // reference a ColumnReference (the inner concat column), not contain
        // a nested concat call.
        RowEnricherOperator outerEnricher = enrichers[0];
        RowEnricherOperator innerEnricher = enrichers[1];

        Assert.Single(outerEnricher.Enrichments);
        Assert.Single(innerEnricher.Enrichments);

        // Outer's enrichment is a function call (upper) whose argument is a
        // ColumnReference — the inner enrichment's column.
        FunctionCallExpression outerCall = Assert.IsType<FunctionCallExpression>(
            outerEnricher.Enrichments[0].Expression);
        Assert.Equal("upper", outerCall.FunctionName);
        ColumnReference outerArgRef = Assert.IsType<ColumnReference>(outerCall.Arguments[0]);
        Assert.Equal(innerEnricher.Enrichments[0].ColumnName, outerArgRef.ColumnName);

        // Inner's enrichment is the concat call itself.
        FunctionCallExpression innerCall = Assert.IsType<FunctionCallExpression>(
            innerEnricher.Enrichments[0].Expression);
        Assert.Equal("concat", innerCall.FunctionName);
    }

    // ─────────────── Within-Filter / Within-OrderBy CSE ───────────────

    [Fact]
    public void WithinFilter_DuplicatePureCallInSinglePredicate_HoistsAboveFilter()
    {
        // The planner splits AND-compound predicates into multiple FilterOperators,
        // so an `AND`-style duplicate is caught by cross-clause (different ops).
        // Within-Filter CSE fires when a SINGLE predicate (e.g. an OR or a
        // boolean comparison) references the same expression twice and the
        // predicate stays as one FilterOperator.
        TableCatalog catalog = Catalog2Cols();
        QueryOperator plan = PlanQuery(
            "SELECT a, b FROM t WHERE concat(a, b) = 'alphabeta' OR concat(a, b) = 'gammadelta'",
            catalog);

        // Walk to the FilterOperator. There may be a Project above it, possibly
        // additional Filters. Find the FIRST Filter encountered top-down.
        FilterOperator? filter = null;
        QueryOperator? cursor = plan;
        while (cursor is not null)
        {
            if (cursor is FilterOperator f) { filter = f; break; }
            cursor = cursor switch
            {
                ProjectOperator p => p.Source,
                _ => null,
            };
        }
        Assert.NotNull(filter);

        // Filter's source must be a RowEnricher (the within-Filter hoist).
        RowEnricherOperator enricher = Assert.IsType<RowEnricherOperator>(filter!.Source);
        Assert.Single(enricher.Enrichments);
        Assert.Equal("__cse_f0", enricher.Enrichments[0].ColumnName);

        FunctionCallExpression hoisted = Assert.IsType<FunctionCallExpression>(
            enricher.Enrichments[0].Expression);
        Assert.Equal("concat", hoisted.FunctionName);

        // Both predicate occurrences became refs.
        string predicateText = QueryExplainer.FormatExpression(filter.Predicate);
        Assert.DoesNotContain("concat", predicateText);
        Assert.Contains("__cse_f0", predicateText);
    }

    [Fact]
    public void WithinFilter_SingleOccurrence_NotHoisted()
    {
        // Single occurrence — no hoist.
        TableCatalog catalog = Catalog2Cols();
        QueryOperator plan = PlanQuery(
            "SELECT a, b FROM t WHERE concat(a, b) = 'alphabeta'",
            catalog);

        // No RowEnricher anywhere.
        bool foundEnricher = false;
        QueryOperator? cursor = plan;
        while (cursor is not null)
        {
            if (cursor is RowEnricherOperator) { foundEnricher = true; break; }
            cursor = cursor switch
            {
                ProjectOperator p => p.Source,
                FilterOperator f => f.Source,
                _ => null,
            };
        }
        Assert.False(foundEnricher);
    }

    [Fact]
    public async Task EndToEnd_WithinFilter_DuplicatePredicate_PreservesResults()
    {
        TableCatalog catalog = Catalog2Cols();
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT a, b FROM t WHERE concat(a, b) = 'alphabeta' OR concat(a, b) = 'gammadelta'",
            catalog);

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void WithinOrderBy_DuplicatePureCall_HoistsAboveSource()
    {
        // concat(a, b) appears twice in ORDER BY: once as a key, once inside
        // a deriving function. Within-OrderBy hoists upstream.
        TableCatalog catalog = Catalog2Cols();
        QueryOperator plan = PlanQuery(
            "SELECT a FROM t ORDER BY concat(a, b) DESC, upper(concat(a, b)) ASC",
            catalog);

        // Walk down to find OrderByOperator and verify a RowEnricher is its
        // direct source.
        OrderByOperator? orderBy = null;
        QueryOperator? cursor = plan;
        while (cursor is not null)
        {
            if (cursor is OrderByOperator ob) { orderBy = ob; break; }
            cursor = cursor switch
            {
                ProjectOperator p => p.Source,
                _ => null,
            };
        }
        Assert.NotNull(orderBy);
        RowEnricherOperator enricher = Assert.IsType<RowEnricherOperator>(orderBy!.Source);

        // One hoist with the __cse_o prefix.
        Assert.Single(enricher.Enrichments);
        Assert.Equal("__cse_o0", enricher.Enrichments[0].ColumnName);

        FunctionCallExpression hoisted = Assert.IsType<FunctionCallExpression>(
            enricher.Enrichments[0].Expression);
        Assert.Equal("concat", hoisted.FunctionName);
    }

    [Fact]
    public async Task EndToEnd_WithinOrderBy_DuplicateKeys_PreservesOrder()
    {
        TableCatalog catalog = Catalog2Cols();
        // SELECT a, b so projection pushdown keeps both columns visible to
        // the upstream RowEnricher inserted by within-OrderBy CSE.
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT a, b FROM t ORDER BY concat(a, b) DESC, upper(concat(a, b)) ASC",
            catalog);

        Assert.Equal(2, rows.Count);
        Arena scratch = catalog.Pool.Backing.RentArena();
        try
        {
            Assert.Equal("gamma", rows[0]["a"].AsString(scratch));
            Assert.Equal("alpha", rows[1]["a"].AsString(scratch));
        }
        finally { catalog.Pool.Backing.TryReturn(scratch); }
    }

    // ─────────────── Within-GroupBy / Within-Window CSE ───────────────

    [Fact]
    public void WithinGroupBy_KeyAndAggregateArgShare_HoistsAboveGroupBy()
    {
        // upper(a) appears in both the GROUP BY key and the aggregate argument.
        // Within-GroupBy CSE hoists it once into a RowEnricher upstream of
        // the GroupByOperator, so partitioning and accumulation share one
        // evaluation per row.
        TableCatalog catalog = Catalog2Cols();
        QueryOperator plan = PlanQuery(
            "SELECT upper(a), COUNT(upper(a)) FROM t GROUP BY upper(a)",
            catalog);

        // Walk the plan to find the GroupByOperator. The chain is typically
        // Project → GroupBy → ... possibly with intermediate operators.
        GroupByOperator? group = null;
        QueryOperator? cursor = plan;
        while (cursor is not null)
        {
            if (cursor is GroupByOperator g) { group = g; break; }
            cursor = cursor switch
            {
                ProjectOperator p => p.Source,
                FilterOperator f => f.Source,
                OrderByOperator ob => ob.Source,
                _ => null,
            };
        }
        Assert.NotNull(group);

        RowEnricherOperator enricher = Assert.IsType<RowEnricherOperator>(group!.Source);

        Assert.Single(enricher.Enrichments);
        Assert.Equal("__cse_g0", enricher.Enrichments[0].ColumnName);

        FunctionCallExpression hoisted = Assert.IsType<FunctionCallExpression>(
            enricher.Enrichments[0].Expression);
        Assert.Equal("upper", hoisted.FunctionName);

        // GROUP BY keys should reference the hidden column, not the original
        // upper(a) call.
        Assert.Single(group.GroupByExpressions);
        ColumnReference keyRef = Assert.IsType<ColumnReference>(group.GroupByExpressions[0]);
        Assert.Equal("__cse_g0", keyRef.ColumnName);

        // The aggregate's argument should likewise reference the hidden column.
        Assert.Single(group.AggregateColumns);
        Assert.Single(group.AggregateColumns[0].ArgumentExpressions);
        ColumnReference argRef = Assert.IsType<ColumnReference>(
            group.AggregateColumns[0].ArgumentExpressions[0]);
        Assert.Equal("__cse_g0", argRef.ColumnName);
    }

    [Fact]
    public void WithinGroupBy_TwoAggregateArgsShare_HoistsOnce()
    {
        // Same expression in two different aggregate args (no group key match).
        // Within-GroupBy CSE collapses to one enrichment.
        TableCatalog catalog = Catalog2Cols();
        QueryOperator plan = PlanQuery(
            "SELECT SUM(upper(a)), MIN(upper(a)) FROM t",
            catalog);

        GroupByOperator? group = null;
        QueryOperator? cursor = plan;
        while (cursor is not null)
        {
            if (cursor is GroupByOperator g) { group = g; break; }
            cursor = cursor switch
            {
                ProjectOperator p => p.Source,
                _ => null,
            };
        }
        Assert.NotNull(group);

        RowEnricherOperator enricher = Assert.IsType<RowEnricherOperator>(group!.Source);
        Assert.Single(enricher.Enrichments);
        Assert.Equal("__cse_g0", enricher.Enrichments[0].ColumnName);

        // Both aggregate args reference the hidden column.
        Assert.Equal(2, group.AggregateColumns.Count);
        ColumnReference sumArg = Assert.IsType<ColumnReference>(
            group.AggregateColumns[0].ArgumentExpressions[0]);
        ColumnReference minArg = Assert.IsType<ColumnReference>(
            group.AggregateColumns[1].ArgumentExpressions[0]);
        Assert.Equal(sumArg.ColumnName, minArg.ColumnName);
        Assert.Equal("__cse_g0", sumArg.ColumnName);
    }

    [Fact]
    public void WithinGroupBy_SingleOccurrence_NotHoisted()
    {
        TableCatalog catalog = Catalog2Cols();
        QueryOperator plan = PlanQuery(
            "SELECT upper(a), COUNT(*) FROM t GROUP BY upper(a)",
            catalog);

        GroupByOperator? group = null;
        QueryOperator? cursor = plan;
        while (cursor is not null)
        {
            if (cursor is GroupByOperator g) { group = g; break; }
            cursor = cursor switch
            {
                ProjectOperator p => p.Source,
                _ => null,
            };
        }
        Assert.NotNull(group);

        // upper(a) used only once (the GROUP BY key; SELECT references the
        // group key column, not re-evaluating). No hoist.
        Assert.IsNotType<RowEnricherOperator>(group!.Source);
    }

    [Fact]
    public async Task EndToEnd_WithinGroupBy_TwoAggsShareArg_PreservesResults()
    {
        TableCatalog catalog = Catalog2Cols();
        // Two aggregates over the same expression — within-GroupBy CSE
        // collapses upper(a) into one __cse_g0 enrichment. End-to-end check
        // verifies the rewritten plan still computes the right aggregates.
        // No GROUP BY → single result row.
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT COUNT(upper(a)) AS cnt, MIN(upper(a)) AS first FROM t",
            catalog);

        Assert.Single(rows);
        Arena scratch = catalog.Pool.Backing.RentArena();
        try
        {
            Assert.Equal(2L, rows[0]["cnt"].AsInt64());
            Assert.Equal("ALPHA", rows[0]["first"].AsString(scratch));
        }
        finally { catalog.Pool.Backing.TryReturn(scratch); }
    }

    [Fact]
    public async Task EndToEnd_SubsumedNestedHoist_PreservesResults()
    {
        // Verify subsumption doesn't change observable semantics.
        TableCatalog catalog = Catalog2Cols();
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT upper(concat(a, b)) AS x, upper(concat(a, b)) AS y, concat(a, b) AS z FROM t",
            catalog);

        Assert.Equal(2, rows.Count);
        Arena scratch = catalog.Pool.Backing.RentArena();
        try
        {
            Assert.Equal("ALPHABETA", rows[0]["x"].AsString(scratch));
            Assert.Equal("ALPHABETA", rows[0]["y"].AsString(scratch));
            Assert.Equal("alphabeta", rows[0]["z"].AsString(scratch));
            Assert.Equal("GAMMADELTA", rows[1]["x"].AsString(scratch));
            Assert.Equal("GAMMADELTA", rows[1]["y"].AsString(scratch));
            Assert.Equal("gammadelta", rows[1]["z"].AsString(scratch));
        }
        finally { catalog.Pool.Backing.TryReturn(scratch); }
    }
}
