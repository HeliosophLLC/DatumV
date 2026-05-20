namespace Heliosoph.DatumV.Tests.Execution;

using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Execution.Operators;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;

/// <summary>
/// Tests the limit-pushdown rewrite: <c>Limit(Project(x))</c> →
/// <c>Project(Limit(x))</c>, repeated through any adjacent row-preserving
/// wrappers, so expensive per-row work in the projection only evaluates for
/// the rows that survive LIMIT/OFFSET.
/// </summary>
public sealed class LimitPushdownTests : ServiceTestBase
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
    public void LimitWithSimpleProjection_PushesLimitBelowProject()
    {
        // Project's expensive expression should only evaluate on the rows
        // that survive LIMIT/OFFSET, not on every upstream row.
        TableCatalog catalog = Catalog2Cols();
        QueryOperator plan = PlanQuery("SELECT concat(a, b) FROM t LIMIT 1 OFFSET 1", catalog);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        LimitOperator limit = Assert.IsType<LimitOperator>(project.Source);
        Assert.IsType<ScanOperator>(limit.Source);
    }

    [Fact]
    public void LimitWithFilter_PushesLimitBetweenProjectAndFilter()
    {
        // Filter is below Project, so the pushdown moves Limit just below
        // Project and leaves Filter unchanged — the filtered row stream
        // still feeds Limit, then Project evaluates on the limited rows.
        TableCatalog catalog = Catalog2Cols();
        QueryOperator plan = PlanQuery(
            "SELECT concat(a, b) FROM t WHERE a = 'alpha' LIMIT 1 OFFSET 1",
            catalog);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        LimitOperator limit = Assert.IsType<LimitOperator>(project.Source);
        FilterOperator filter = Assert.IsType<FilterOperator>(limit.Source);
        Assert.IsType<ScanOperator>(filter.Source);
    }

    [Fact]
    public void LimitWithCseRowEnricher_PushesLimitBelowBoth()
    {
        // concat(a, b) duplicated triggers CSE, which inserts a
        // RowEnricherOperator between Project and Scan. Limit must end up
        // below BOTH so the enricher only computes the expensive expression
        // for the limited rows.
        TableCatalog catalog = Catalog2Cols();
        QueryOperator plan = PlanQuery(
            "SELECT concat(a, b), concat(a, b) FROM t LIMIT 1",
            catalog);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        RowEnricherOperator enricher = Assert.IsType<RowEnricherOperator>(project.Source);
        LimitOperator limit = Assert.IsType<LimitOperator>(enricher.Source);
        Assert.IsType<ScanOperator>(limit.Source);
    }

    [Fact]
    public void LimitWithOrderBy_LiftsProjectAboveSortAndLimit()
    {
        // ORDER BY references a source column ('a') that the projection does
        // not redefine. SortLimitLift moves the Project above LIMIT/OrderBy so
        // the expensive concat(a, b) only evaluates for the surviving row.
        TableCatalog catalog = Catalog2Cols();
        QueryOperator plan = PlanQuery(
            "SELECT concat(a, b) FROM t ORDER BY a LIMIT 1",
            catalog);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        LimitOperator limit = Assert.IsType<LimitOperator>(project.Source);
        OrderByOperator orderBy = Assert.IsType<OrderByOperator>(limit.Source);
        Assert.IsType<ScanOperator>(orderBy.Source);
    }

    [Fact]
    public void LimitWithOrderBy_OnProjectAlias_DoesNotLift()
    {
        // ORDER BY references the Project's alias 'c' — the column doesn't
        // exist below Project, so the lift would break the sort.
        TableCatalog catalog = Catalog2Cols();
        QueryOperator plan = PlanQuery(
            "SELECT concat(a, b) AS c FROM t ORDER BY c LIMIT 1",
            catalog);

        LimitOperator limit = Assert.IsType<LimitOperator>(plan);
        Assert.IsType<OrderByOperator>(limit.Source);
    }

    [Fact]
    public void LimitWithDistinct_DoesNotPushBelowDistinct()
    {
        // DISTINCT changes cardinality. Pushing LIMIT below DISTINCT would
        // slice rows before deduplication and return the wrong result.
        TableCatalog catalog = Catalog2Cols();
        QueryOperator plan = PlanQuery(
            "SELECT DISTINCT concat(a, b) FROM t LIMIT 1",
            catalog);

        LimitOperator limit = Assert.IsType<LimitOperator>(plan);
        Assert.IsType<DistinctOperator>(limit.Source);
    }

    [Fact]
    public void NoLimit_TreeUnchanged()
    {
        // Sanity: when there's no LIMIT at the top, the pass is a no-op.
        TableCatalog catalog = Catalog2Cols();
        QueryOperator plan = PlanQuery("SELECT concat(a, b) FROM t", catalog);

        ProjectOperator project = Assert.IsType<ProjectOperator>(plan);
        Assert.IsType<ScanOperator>(project.Source);
    }
}
