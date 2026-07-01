namespace Heliosoph.DatumV.Tests.Execution;

using System;
using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Execution.Operators;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;

/// <summary>
/// Regression tests ensuring the optimizer's Finalize passes (model hoisting,
/// CSE, LIMIT pushdown, SORT+LIMIT lift) apply to <em>every</em> CTE body —
/// including siblings planned after an earlier CTE is already in scope.
/// <para>
/// <see cref="QueryPlanner"/> planned the first CTE in a WITH list through the
/// Finalize'd <c>Plan(body)</c> path, but every subsequent sibling went through
/// <c>PlanWithSiblingCommonTableExpressions</c>, which called <c>PlanCore</c>
/// directly and skipped Finalize. The visible symptom was an expensive
/// per-row projection (a <c>models.*</c> call, an image draw, a <c>concat</c>)
/// stranded <em>below</em> a <c>LIMIT</c> in the second-or-later CTE, so it ran
/// for every source row instead of only the surviving ones.
/// </para>
/// </summary>
public sealed class CteBodyOptimizationTests : ServiceTestBase
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

    // Two CTEs with identical bodies: an expensive projection over
    // ORDER BY ... LIMIT. SortLimitLift should hoist the Project above the
    // LIMIT+ORDER BY in BOTH bodies (the sort key `a` is a source column the
    // projection doesn't redefine). Both CTEs are referenced so neither is
    // pruned.
    private const string TwoCteQuery = """
        WITH first AS (SELECT concat(a, b) AS c FROM t ORDER BY a LIMIT 1),
             second AS (SELECT concat(a, b) AS c FROM t ORDER BY a LIMIT 1)
        SELECT f.c AS fc, s.c AS sc FROM first f, second s
        """;

    /// <summary>
    /// The lifted shape produced by <c>SortLimitLift</c>: the expensive
    /// projection sits above LIMIT so it only evaluates for surviving rows.
    /// </summary>
    private static void AssertProjectLiftedAboveLimit(QueryOperator cteBody)
    {
        ProjectOperator project = Assert.IsType<ProjectOperator>(cteBody);
        LimitOperator limit = Assert.IsType<LimitOperator>(project.Source);
        OrderByOperator orderBy = Assert.IsType<OrderByOperator>(limit.Source);
        Assert.IsType<ScanOperator>(orderBy.Source);
    }

    private static CommonTableExpressionOperator FindCte(QueryOperator root, string name) =>
        TryFindCte(root, name)
        ?? throw new Xunit.Sdk.XunitException($"CTE '{name}' not found in plan tree.");

    private static CommonTableExpressionOperator? TryFindCte(QueryOperator root, string name)
    {
        if (root is CommonTableExpressionOperator cte
            && string.Equals(cte.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            return cte;
        }

        foreach ((QueryOperator child, string? _) in root.DescribeForExplain().Children)
        {
            if (TryFindCte(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    [Fact]
    public void FirstCteBody_LiftsExpensiveProjectionAboveLimit()
    {
        // Control: the first CTE has always been Finalize'd (planned via the
        // empty-sibling `Plan(body)` path). This documents the expected shape.
        TableCatalog catalog = Catalog2Cols();
        QueryOperator plan = PlanQuery(TwoCteQuery, catalog);

        CommonTableExpressionOperator first = FindCte(plan, "first");
        AssertProjectLiftedAboveLimit(first.InnerOperator);
    }

    [Fact]
    public void SecondCteBody_LiftsExpensiveProjectionAboveLimit()
    {
        // Regression: the second CTE is planned with `first` already in scope,
        // so it went through PlanWithSiblingCommonTableExpressions. Before the
        // fix that path skipped Finalize, leaving the Project stranded below
        // LIMIT — the expensive expression ran for every source row.
        TableCatalog catalog = Catalog2Cols();
        QueryOperator plan = PlanQuery(TwoCteQuery, catalog);

        CommonTableExpressionOperator second = FindCte(plan, "second");
        AssertProjectLiftedAboveLimit(second.InnerOperator);
    }
}
