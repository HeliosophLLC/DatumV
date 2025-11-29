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
    private static IQueryOperator PlanQuery(string sql, TableCatalog catalog)
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
        IQueryOperator plan = PlanQuery("SELECT concat(a, b), concat(a, b) FROM t", catalog);

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
    public void SingleOccurrence_NotHoisted()
    {
        // concat(a, b) appears exactly once — no CSE benefit, no rewrite.
        TableCatalog catalog = Catalog2Cols();
        IQueryOperator plan = PlanQuery("SELECT concat(a, b) FROM t", catalog);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);

        // Source is NOT a RowEnricherOperator — nothing was hoisted.
        Assert.IsNotType<RowEnricherOperator>(project.Source);

        // Projection still holds the original concat call.
        FunctionCallExpression call = Assert.IsType<FunctionCallExpression>(project.Columns[0].Expression);
        Assert.Equal("concat", call.FunctionName);
    }

    [Fact]
    public void DuplicateColumnReferences_NotHoisted()
    {
        // Trivial leaves (column references) don't pay for hoisting.
        // SELECT a, a FROM t — no enricher, two ColRefs preserved.
        TableCatalog catalog = Catalog2Cols();
        IQueryOperator plan = PlanQuery("SELECT a, a FROM t", catalog);

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
        IQueryOperator plan = PlanQuery(
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
        IQueryOperator plan = PlanQuery(
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
        IQueryOperator plan = PlanQuery(
            "SELECT concat(a, b) FROM t WHERE concat(a, b) = 'alphabeta'",
            catalog);

        // Walk the plan, count RowEnricherOperators and their position.
        int enricherCount = 0;
        bool enricherIsUpstreamOfFilter = false;
        IQueryOperator? cursor = plan;
        IQueryOperator? prev = null;
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
        IQueryOperator plan = PlanQuery(
            "SELECT concat(a, b) FROM t ORDER BY concat(a, b)",
            catalog);

        int enricherCount = 0;
        IQueryOperator? cursor = plan;
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
        IQueryOperator plan = PlanQuery(
            "SELECT concat(a, b) AS first, concat(a, b) AS second " +
            "FROM t WHERE concat(a, b) = 'alphabeta'",
            catalog);

        // Exactly one RowEnricher in the chain.
        int enricherCount = 0;
        IQueryOperator? cursor = plan;
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
        IQueryOperator plan = PlanQuery(
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
        // is computed within this enrichment's evaluation, no separate __cse_1.
        // (Subsumption: hoisting both upper(...) AND concat(...) is suboptimal
        // and not done in this slice — the outer hoist already covers it.)
        FunctionCallExpression hoisted = Assert.IsType<FunctionCallExpression>(
            enricher.Enrichments.First(e => e.ColumnName == c0.ColumnName).Expression);
        Assert.Equal("upper", hoisted.FunctionName);
    }
}
