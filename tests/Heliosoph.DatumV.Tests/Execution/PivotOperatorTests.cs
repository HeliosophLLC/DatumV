using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;

using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Tests.Execution;

/// <summary>
/// End-to-end tests for PIVOT via SQL parser → planner → executor.
/// Direct-construction operator tests would duplicate planner-side
/// aggregate resolution; the SQL surface is the contract we ship.
/// </summary>
public sealed class PivotOperatorTests : ServiceTestBase
{
    [Fact]
    public async Task ExplicitInList_SingleAggregate_SingleKey()
    {
        TableCatalog catalog = CreateCatalog(
            "sales",
            ["group_id", "region", "revenue"],
            [1f, "north", 100f],
            [1f, "south", 200f],
            [1f, "east",  300f],
            [1f, "west",  400f],
            [2f, "north", 1000f],
            [2f, "south", 2000f],
            [2f, "east",  3000f],
            [2f, "west",  4000f]);

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT * FROM sales PIVOT (SUM(revenue) FOR region IN ('north', 'south', 'east', 'west'))",
            catalog);

        Assert.Equal(2, rows.Count);
        Row group1 = rows.Single(r => r["group_id"].AsFloat32() == 1f);
        Assert.Equal(100f, group1["north"].AsFloat32());
        Assert.Equal(400f, group1["west"].AsFloat32());
        Row group2 = rows.Single(r => r["group_id"].AsFloat32() == 2f);
        Assert.Equal(3000f, group2["east"].AsFloat32());
    }

    [Fact]
    public async Task AutoDiscover_DiscoversDistinctValuesAtRuntime()
    {
        TableCatalog catalog = CreateCatalog(
            "sales",
            ["group_id", "region", "revenue"],
            [1f, "north", 10f],
            [1f, "south", 20f],
            [2f, "north", 100f]);

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT * FROM sales PIVOT (SUM(revenue) FOR region)",
            catalog);

        Assert.Equal(2, rows.Count);
        Row group1 = rows.Single(r => r["group_id"].AsFloat32() == 1f);
        Assert.Equal(10f, group1["north"].AsFloat32());
        Assert.Equal(20f, group1["south"].AsFloat32());
        // Group 2 has no "south" row → cell should evaluate to the aggregate's empty-group result (NULL for SUM).
        Row group2 = rows.Single(r => r["group_id"].AsFloat32() == 2f);
        Assert.Equal(100f, group2["north"].AsFloat32());
        Assert.True(group2["south"].IsNull);
    }

    [Fact]
    public async Task MultipleAggregates_SuffixesOutputColumns()
    {
        TableCatalog catalog = CreateCatalog(
            "sales",
            ["group_id", "region", "revenue"],
            [1f, "north", 10f],
            [1f, "north", 30f],
            [1f, "south", 5f]);

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT * FROM sales PIVOT (SUM(revenue), COUNT(revenue) FOR region IN ('north', 'south'))",
            catalog);

        Assert.Single(rows);
        Row row = rows[0];
        Assert.Equal(40f, row["north_SUM(revenue)"].AsFloat32());
        Assert.Equal(2L, row["north_COUNT(revenue)"].AsInt64());
        Assert.Equal(5f, row["south_SUM(revenue)"].AsFloat32());
        Assert.Equal(1L, row["south_COUNT(revenue)"].AsInt64());
    }

    [Fact]
    public async Task NoKeyColumns_ProducesSingleGlobalRow()
    {
        TableCatalog catalog = CreateCatalog(
            "sales",
            ["region", "revenue"],
            ["north", 10f],
            ["south", 20f],
            ["north", 30f]);

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT * FROM sales PIVOT (SUM(revenue) FOR region IN ('north', 'south'))",
            catalog);

        Assert.Single(rows);
        Assert.Equal(40f, rows[0]["north"].AsFloat32());
        Assert.Equal(20f, rows[0]["south"].AsFloat32());
    }

    [Fact]
    public async Task ExplicitInList_SkipsRowsWithUnlistedPivotValues()
    {
        TableCatalog catalog = CreateCatalog(
            "sales",
            ["group_id", "region", "revenue"],
            [1f, "north",  10f],
            [1f, "central", 999f],
            [1f, "south",  20f]);

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT * FROM sales PIVOT (SUM(revenue) FOR region IN ('north', 'south'))",
            catalog);

        Assert.Single(rows);
        Assert.Equal(10f, rows[0]["north"].AsFloat32());
        Assert.Equal(20f, rows[0]["south"].AsFloat32());
        // 'central' row contributed nothing.
    }

    [Fact]
    public async Task EmptyInput_ProducesEmptyOutput()
    {
        TableCatalog catalog = CreateCatalog(
            "sales",
            ["group_id", "region", "revenue"]);

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT * FROM sales PIVOT (SUM(revenue) FOR region IN ('north', 'south'))",
            catalog);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task Spill_ExplicitInList_ProducesSameResultsAsInMemory()
    {
        // 100 distinct keys × 4 regions × 1 row each = 400 input rows.
        // Tiny budget guarantees spill activates well before all 100 groups fit in memory.
        string[] columns = ["group_id", "region", "revenue"];
        string[] regions = ["north", "south", "east", "west"];
        object?[][] rows = new object?[400][];
        int index = 0;
        for (int g = 0; g < 100; g++)
        {
            for (int r = 0; r < 4; r++)
            {
                rows[index++] = [(float)g, regions[r], (float)(g * 10 + r)];
            }
        }

        TableCatalog catalog = CreateCatalog("sales", columns, rows);
        const string Sql =
            "SELECT * FROM sales PIVOT (SUM(revenue) FOR region IN ('north', 'south', 'east', 'west'))";

        // In-memory reference run (no budget).
        List<Row> reference = await ExecuteQueryAsync(Sql, catalog);

        // Spill run with tiny budget (~256 bytes — forces spill after a handful of groups).
        const long Budget = 256;
        (List<Row> spilled, ExecutionContext context) = await ExecuteWithBudgetAsync(Sql, catalog, Budget);

        Assert.Equal(reference.Count, spilled.Count);
        Assert.Equal(100, spilled.Count);

        // Spill must actually have fired — without it, in-memory residency for 100 groups
        // would dwarf the 256-byte budget. Allow a generous overshoot for the per-group
        // pre-check race documented in GroupBySpillTests.
        const long PerGroupSlack = 256 * 4;
        Assert.True(
            context.Accountant.PeakResidentBytes <= Budget + PerGroupSlack,
            $"Expected spill to bound residency; peak={context.Accountant.PeakResidentBytes}, budget={Budget}");

        // Check a few groups explicitly to confirm aggregation is correct end-to-end.
        Row group0 = spilled.Single(r => r["group_id"].AsFloat32() == 0f);
        Assert.Equal(0f, group0["north"].AsFloat32());
        Assert.Equal(3f, group0["west"].AsFloat32());

        Row group99 = spilled.Single(r => r["group_id"].AsFloat32() == 99f);
        Assert.Equal(990f, group99["north"].AsFloat32());
        Assert.Equal(993f, group99["west"].AsFloat32());

        // Pairwise compare every group across both runs.
        foreach (Row referenceRow in reference)
        {
            float gid = referenceRow["group_id"].AsFloat32();
            Row spilledRow = spilled.Single(r => r["group_id"].AsFloat32() == gid);
            foreach (string region in regions)
            {
                Assert.Equal(referenceRow[region].AsFloat32(), spilledRow[region].AsFloat32());
            }
        }
    }

    [Fact]
    public async Task Spill_AutoDiscover_FallsBackToInMemory()
    {
        // Auto-discover with a tiny budget should NOT spill (v1 limitation).
        // The query should still succeed via the in-memory path.
        TableCatalog catalog = CreateCatalog(
            "sales",
            ["group_id", "region", "revenue"],
            [1f, "north", 10f],
            [1f, "south", 20f],
            [2f, "north", 30f]);

        (List<Row> rows, _) = await ExecuteWithBudgetAsync(
            "SELECT * FROM sales PIVOT (SUM(revenue) FOR region)",
            catalog,
            memoryBudgetBytes: 256);

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task AutoDiscover_CardinalityCap_ThrowsWhenExceeded()
    {
        // 1001 distinct pivot values → exceeds CardinalityCap (1000).
        List<object?[]> rows = new(1001);
        for (int i = 0; i <= 1000; i++)
        {
            rows.Add([1f, $"v{i}", 1f]);
        }
        TableCatalog catalog = CreateCatalog(
            "sales",
            ["group_id", "region", "revenue"],
            rows.ToArray());

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExecuteQueryAsync(
                "SELECT * FROM sales PIVOT (SUM(revenue) FOR region)",
                catalog));

        Assert.Contains("cardinality cap", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(List<Row> Rows, ExecutionContext Context)> ExecuteWithBudgetAsync(
        string sql, TableCatalog catalog, long memoryBudgetBytes)
    {
        QueryExpression query = SqlParser.Parse(sql);
        QueryPlanner planner = new(catalog, FunctionRegistry.CreateDefault());
        ExecutionContext context = CreateExecutionContext(catalog: catalog, memoryBudgetBytes: memoryBudgetBytes);
        QueryOperator plan = await planner.PlanWithSubqueriesAsync(query, context, CancellationToken.None);
        List<Row> rows = await plan.CollectRowsAsync(context);
        return (rows, context);
    }
}
