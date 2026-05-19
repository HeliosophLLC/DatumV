using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Catalog.Providers;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Execution.Operators;
using Heliosoph.DatumV.Model;

using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Tests.Execution;

/// <summary>
/// Tests for <see cref="UnpivotOperator"/>.
/// </summary>
public sealed class UnpivotOperatorTests : ServiceTestBase
{
    private static readonly string[] WideColumns = ["id", "north", "south", "east", "west"];
    private static readonly string[] RegionSources = ["north", "south", "east", "west"];

    [Fact]
    public async Task UnpivotsFourColumns_EmitsFourRowsPerInputRow()
    {
        MockOperator source = CreateMockOperator(WideColumns,
            [1f, 10f, 20f, 30f, 40f],
            [2f, 100f, 200f, 300f, 400f]);

        UnpivotOperator unpivot = new(
            source,
            valueColumnName: "revenue",
            nameColumnName: "region",
            sourceColumnNames: RegionSources);

        List<Row> rows = await CollectAsync(unpivot);

        Assert.Equal(8, rows.Count);

        // Row 0: id=1, north → revenue=10, region=north
        Assert.Equal(1f, rows[0]["id"].AsFloat32());
        Assert.Equal(10f, rows[0]["revenue"].AsFloat32());
        Assert.Equal("north", rows[0]["region"].AsString());

        // Row 3: id=1, west → revenue=40, region=west
        Assert.Equal(1f, rows[3]["id"].AsFloat32());
        Assert.Equal(40f, rows[3]["revenue"].AsFloat32());
        Assert.Equal("west", rows[3]["region"].AsString());

        // Row 7: id=2, west → revenue=400, region=west
        Assert.Equal(2f, rows[7]["id"].AsFloat32());
        Assert.Equal(400f, rows[7]["revenue"].AsFloat32());
        Assert.Equal("west", rows[7]["region"].AsString());
    }

    [Fact]
    public async Task NullValues_SkippedByDefault()
    {
        MockOperator source = CreateMockOperator(WideColumns,
            [1f, 10f, null, 30f, null]);

        UnpivotOperator unpivot = new(
            source,
            valueColumnName: "revenue",
            nameColumnName: "region",
            sourceColumnNames: RegionSources);

        List<Row> rows = await CollectAsync(unpivot);

        Assert.Equal(2, rows.Count);
        Assert.Equal("north", rows[0]["region"].AsString());
        Assert.Equal("east", rows[1]["region"].AsString());
    }

    [Fact]
    public async Task NullValues_EmittedWhenIncludeNullsTrue()
    {
        MockOperator source = CreateMockOperator(WideColumns,
            [1f, 10f, null, 30f, null]);

        UnpivotOperator unpivot = new(
            source,
            valueColumnName: "revenue",
            nameColumnName: "region",
            sourceColumnNames: RegionSources,
            includeNulls: true);

        List<Row> rows = await CollectAsync(unpivot);

        Assert.Equal(4, rows.Count);
        Assert.False(rows[0]["revenue"].IsNull);
        Assert.True(rows[1]["revenue"].IsNull);
        Assert.False(rows[2]["revenue"].IsNull);
        Assert.True(rows[3]["revenue"].IsNull);
    }

    [Fact]
    public async Task PreservesKeyColumns()
    {
        // Two key columns (id, name) + three source columns
        string[] columns = ["id", "name", "q1", "q2", "q3"];
        MockOperator source = CreateMockOperator(columns,
            [42f, "alpha", 1f, 2f, 3f]);

        UnpivotOperator unpivot = new(
            source,
            valueColumnName: "value",
            nameColumnName: "quarter",
            sourceColumnNames: ["q1", "q2", "q3"]);

        List<Row> rows = await CollectAsync(unpivot);

        Assert.Equal(3, rows.Count);
        foreach (Row row in rows)
        {
            Assert.Equal(42f, row["id"].AsFloat32());
            Assert.Equal("alpha", row["name"].AsString());
        }
    }

    [Fact]
    public async Task EmptyInput_ProducesEmptyOutput()
    {
        MockOperator source = CreateMockOperator(WideColumns);

        UnpivotOperator unpivot = new(
            source,
            valueColumnName: "revenue",
            nameColumnName: "region",
            sourceColumnNames: RegionSources);

        List<Row> rows = await CollectAsync(unpivot);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task EndToEnd_SqlUnpivotClause()
    {
        // Full parser → planner → executor wire-up smoke test.
        TableCatalog catalog = CreateCatalog(
            "wide_sales",
            ["id", "q1", "q2", "q3", "q4"],
            [1f, 100f, 200f, 300f, 400f]);

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT * FROM wide_sales UNPIVOT (revenue FOR quarter IN (q1, q2, q3, q4))",
            catalog);

        Assert.Equal(4, rows.Count);
        Assert.Equal(100f, rows[0]["revenue"].AsFloat32());
        Assert.Equal("q1", rows[0]["quarter"].AsString());
        Assert.Equal(400f, rows[3]["revenue"].AsFloat32());
        Assert.Equal("q4", rows[3]["quarter"].AsString());
    }

    [Fact]
    public async Task EndToEnd_IncludeNullsRetainsNullRows()
    {
        TableCatalog catalog = CreateCatalog(
            "sparse",
            ["id", "a", "b", "c"],
            [1f, 10f, null, 30f]);

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT * FROM sparse UNPIVOT INCLUDE NULLS (m FOR sensor IN (a, b, c))",
            catalog);

        Assert.Equal(3, rows.Count);
        Assert.True(rows[1]["m"].IsNull);
        Assert.Equal("b", rows[1]["sensor"].AsString());
    }

    private async Task<List<Row>> CollectAsync(QueryOperator op)
    {
        ExecutionContext context = CreateExecutionContext();
        return await op.CollectRowsAsync(context);
    }
}
